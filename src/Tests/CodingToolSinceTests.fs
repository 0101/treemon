module Tests.CodingToolSinceTests

open System
open NUnit.Framework
open Server.GitWorktree
open Server.RefreshScheduler
open Server.SchedulerState
open Server.SessionActivity
open Server.SessionActivityStore
open Shared

let private testRepoId = RepoId "TestRepo"

let private makeWorktree path branch : WorktreeInfo =
    { Path = path; Head = "abc123"; Branch = Some branch }

// CodingToolSince is the transition time of the collapsed worktree status. Exact-instance
// heartbeats and sibling updates preserve it while the aggregate status is unchanged.

let private wtA = Server.PathUtils.normalizePath "C:/wt/a"
let private wtB = Server.PathUtils.normalizePath "C:/wt/b"

let private identity sid =
    sid
    |> Seq.fold (fun value character ->
        (value * 31 + int character) % 1_000_000) 10_000
    |> fun processId ->
        ProcessIdentity.create
            processId
            (int64 processId * 1_000L + 1L)
    |> Result.defaultWith invalidOp

let private storedWt (sid: string) (wt: string) (status: SessionLevelStatus) (seen: DateTimeOffset) : StoredInstance =
    { ProcessIdentity = identity sid
      SessionId = SessionId sid
      TerminalSessionId = None
      WorktreePath = WorktreePath wt
      Provider = CopilotCli
      Status = { emptyStatus with Status = status }
      UpdatedAt = seen
      LifecycleAt = Some seen
      LastSeen = seen
      ContextUsageAt = None
      ClosedAt = None }

let private postInstance
    (agent: MailboxProcessor<StateMsg>)
    (instance: StoredInstance)
    =
    agent.Post(
        UpdateSessionInstance(
            instance,
            instance.LastSeen
        )
    )

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type GroupInstancesByWorktreeTests() =

    [<Test>]
    member _.``Grouping visits each exact instance once across a scale fixture``() =
        let observedAt = DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero)
        let worktreeCount = 128
        let instancesPerWorktree = 4

        let instances =
            [ 1 .. worktreeCount ]
            |> List.collect (fun worktreeIndex ->
                let path =
                    System.IO.Path.Combine(
                        System.IO.Path.GetTempPath(),
                        "treemon-scheduler-grouping",
                        $"wt-{worktreeIndex}"
                    )

                [ 1 .. instancesPerWorktree ]
                |> List.map (fun instanceIndex ->
                    storedWt
                        $"session-{worktreeIndex}-{instanceIndex}"
                        path
                        SessionLevelStatus.Idle
                        observedAt))

        let visited =
            System.Collections.Concurrent.ConcurrentQueue<ProcessIdentity>()

        let grouped =
            instances
            |> Seq.map (fun instance ->
                visited.Enqueue(instance.ProcessIdentity)
                instance)
            |> groupInstancesByWorktree

        Assert.Multiple(fun () ->
            Assert.That(
                visited.Count,
                Is.EqualTo(instances.Length),
                "grouping must enumerate the source once")
            Assert.That(grouped.Count, Is.EqualTo(worktreeCount))
            Assert.That(
                grouped
                |> Map.forall (fun _ group ->
                    List.length group = instancesPerWorktree),
                Is.True))

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type CodingToolTransitionTests() =

    let t0 = DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero)

    [<Test>]
    member _.``First observed status stamps the transition time``() =
        let statuses, since =
            updateCodingToolTransition
                t0
                wtA
                Idle
                (Map.empty, Map.empty)

        Assert.Multiple(fun () ->
            Assert.That(statuses |> Map.tryFind wtA, Is.EqualTo(Some Idle))
            Assert.That(since |> Map.tryFind wtA, Is.EqualTo(Some t0)))

    [<Test>]
    member _.``Repeated status preserves the original transition time``() =
        let transition =
            updateCodingToolTransition
                t0
                wtA
                Idle
                (Map.empty, Map.empty)

        let _, since =
            updateCodingToolTransition
                (t0.AddMinutes 1.0)
                wtA
                Idle
                transition

        Assert.That(since |> Map.tryFind wtA, Is.EqualTo(Some t0))

    [<Test>]
    member _.``Status change moves the transition time``() =
        let transition =
            updateCodingToolTransition
                t0
                wtA
                Idle
                (Map.empty, Map.empty)

        let changedAt = t0.AddMinutes 1.0
        let statuses, since =
            updateCodingToolTransition
                changedAt
                wtA
                Working
                transition

        Assert.Multiple(fun () ->
            Assert.That(statuses |> Map.tryFind wtA, Is.EqualTo(Some Working))
            Assert.That(since |> Map.tryFind wtA, Is.EqualTo(Some changedAt)))

    [<Test>]
    member _.``NoSession removes the transition``() =
        let transition =
            updateCodingToolTransition
                t0
                wtA
                Idle
                (Map.empty, Map.empty)

        let statuses, since =
            updateCodingToolTransition
                (t0.AddMinutes 1.0)
                wtA
                NoSession
                transition

        Assert.Multiple(fun () ->
            Assert.That(statuses |> Map.containsKey wtA, Is.False)
            Assert.That(since |> Map.containsKey wtA, Is.False))


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type CodingToolSinceByWorktreeTests() =

    let t0 = DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero)

    let sinceFor (state: DashboardState) = state.CodingToolSinceByWorktree |> Map.tryFind wtA

    [<Test>]
    member _.``CodingToolSince moves only when the collapsed status changes``() =
        async {
            let agent = createAgent ()
            postInstance agent (storedWt "s1" wtA SessionLevelStatus.Working t0)
            let! working = agent.PostAndAsyncReply(GetState)

            let idledAt = t0 + TimeSpan.FromSeconds 30.0
            postInstance agent (storedWt "s1" wtA SessionLevelStatus.Idle idledAt)
            let! entered = agent.PostAndAsyncReply(GetState)

            postInstance agent (storedWt "s1" wtA SessionLevelStatus.Idle (idledAt + TimeSpan.FromSeconds 60.0))
            postInstance agent (storedWt "s1" wtA SessionLevelStatus.Idle (idledAt + TimeSpan.FromSeconds 120.0))
            let! frozen = agent.PostAndAsyncReply(GetState)

            let resumedAt = idledAt + TimeSpan.FromSeconds 180.0
            postInstance agent (storedWt "s1" wtA SessionLevelStatus.Working resumedAt)
            let! resumed = agent.PostAndAsyncReply(GetState)

            let reidledAt = idledAt + TimeSpan.FromSeconds 240.0
            postInstance agent (storedWt "s1" wtA SessionLevelStatus.Idle reidledAt)
            let! reidled = agent.PostAndAsyncReply(GetState)

            Assert.That(sinceFor working, Is.EqualTo(Some t0))
            Assert.That(sinceFor entered, Is.EqualTo(Some idledAt), "stamped at the Idle transition")
            Assert.That(sinceFor frozen, Is.EqualTo(Some idledAt), "frozen across idle heartbeats")
            Assert.That(sinceFor resumed, Is.EqualTo(Some resumedAt))
            Assert.That(sinceFor reidled, Is.EqualTo(Some reidledAt), "re-stamped at the new Idle transition")
        }
        |> Async.RunSynchronously

    [<Test>]
    member _.``An idle heartbeat after the prior openness window starts a new transition``() =
        async {
            let agent = createAgent ()
            postInstance agent (storedWt "s1" wtA SessionLevelStatus.Idle t0)

            let representedAt =
                t0 + openWindow + TimeSpan.FromSeconds 1.0

            postInstance
                agent
                (storedWt
                    "s1"
                    wtA
                    SessionLevelStatus.Idle
                    representedAt)

            let! represented = agent.PostAndAsyncReply(GetState)
            Assert.That(
                sinceFor represented,
                Is.EqualTo(Some representedAt),
                "the previous exact instance was no longer open at this observation"
            )
        }
        |> Async.RunSynchronously

    [<Test>]
    member _.``An unrelated heartbeat expires every worktree at the exact openness boundary``() =
        async {
            let agent = createAgent ()
            postInstance agent (storedWt "a" wtA SessionLevelStatus.Idle t0)
            postInstance agent (storedWt "b" wtB SessionLevelStatus.Idle t0)

            let boundary = t0 + openWindow
            postInstance agent (storedWt "b" wtB SessionLevelStatus.Idle boundary)

            let! state = agent.PostAndAsyncReply(GetState)

            Assert.Multiple(fun () ->
                Assert.That(
                    state.CodingToolStatusByWorktree |> Map.containsKey wtA,
                    Is.False,
                    "the untouched worktree reaches NoSession at the strict open-window boundary")
                Assert.That(
                    state.CodingToolSinceByWorktree |> Map.containsKey wtA,
                    Is.False,
                    "the untouched worktree's transition stamp is removed")
                Assert.That(
                    state.CodingToolStatusByWorktree |> Map.tryFind wtB,
                    Is.EqualTo(Some Idle))
                Assert.That(
                    state.CodingToolSinceByWorktree |> Map.tryFind wtB,
                    Is.EqualTo(Some boundary),
                    "the refreshed worktree starts a new open transition"))
        }
        |> Async.RunSynchronously

    [<Test>]
    member _.``An idle sibling update does not reset a worktree that remains Working``() =
        async {
            let agent = createAgent ()
            postInstance agent (storedWt "working" wtA SessionLevelStatus.Working t0)

            let siblingAt = t0.AddSeconds 10.0
            postInstance agent (storedWt "idle" wtA SessionLevelStatus.Idle siblingAt)

            let! state = agent.PostAndAsyncReply(GetState)
            Assert.Multiple(fun () ->
                Assert.That(
                    state.CodingToolStatusByWorktree |> Map.tryFind wtA,
                    Is.EqualTo(Some Working)
                )
                Assert.That(sinceFor state, Is.EqualTo(Some t0)))
        }
        |> Async.RunSynchronously


// The display debounce (SessionActivity.debounceIdle, applied on the card read path) measures its
// Working→Idle hold from the SAME CodingToolSinceByWorktree stamp the scheduler freezes above. The
// DebounceIdleTests unit tests feed idleSince directly; these drive the real exact-instance path
// so a change to the scheduler's freeze/reset policy surfaces here instead of silently breaking the dot.

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type DebounceIdleSchedulerIntegrationTests() =

    let t0 = DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero)
    let sinceFor (state: DashboardState) = state.CodingToolSinceByWorktree |> Map.tryFind wtA
    let displayAt (now: DateTimeOffset) (state: DashboardState) =
        debounceIdle idleDebounceWindow now (sinceFor state) Idle

    [<Test>]
    member _.``The scheduler's frozen stamp drives debounceIdle: held Working within the window, Idle after, measured from the transition not the heartbeat``() =
        async {
            let agent = createAgent ()
            let idledAt = t0 + TimeSpan.FromSeconds 30.0

            postInstance agent (storedWt "s1" wtA SessionLevelStatus.Working t0)
            postInstance agent (storedWt "s1" wtA SessionLevelStatus.Idle idledAt)
            let! entered = agent.PostAndAsyncReply(GetState)

            // An idle heartbeat 60s later advances last_seen, but the stamp stays frozen at idledAt.
            postInstance agent (storedWt "s1" wtA SessionLevelStatus.Idle (idledAt + TimeSpan.FromSeconds 60.0))
            let! afterHeartbeat = agent.PostAndAsyncReply(GetState)

            Assert.That(
                displayAt (idledAt + TimeSpan.FromSeconds 3.0) entered,
                Is.EqualTo Working,
                "held Working within the grace window")
            Assert.That(
                displayAt (idledAt + idleDebounceWindow + TimeSpan.FromSeconds 1.0) entered,
                Is.EqualTo Idle,
                "real Idle surfaces once the window elapses")
            // Measured from the FROZEN transition, not the advancing heartbeat: a read just after the
            // heartbeat but well past (transition + window) still shows Idle. If the stamp tracked
            // last_seen, this would wrongly re-hold Working.
            Assert.That(
                displayAt (idledAt + TimeSpan.FromSeconds 65.0) afterHeartbeat,
                Is.EqualTo Idle,
                "hold is measured from the transition, not the idle heartbeat")
        }
        |> Async.RunSynchronously


// WorktreeApi.assembleFromState is where the frozen stamp + debounceIdle are actually WIRED onto the
// card (WorktreeStatus.CodingTool / .CodingToolSince). The DebounceIdleSchedulerIntegrationTests above
// call debounceIdle directly with a hard-coded Idle, so they'd still pass if that block were deleted
// or wired to the wrong status/stamp. This seeds the real scheduler state, collapses it exactly as the
// endpoint does, and asserts on the returned WorktreeStatus — held Working (no chip) inside the window,
// real Idle (with the frozen chip) after it — so a mis-wire of the assembly surfaces here.

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type AssembleFromStateDebounceTests() =

    let t0 = DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero)

    // Assemble the card for wtA at `displayNow`, deriving pushByWorktree from the scheduler state the
    // same way the getWorktrees endpoint does (collapseByWorktree over exact instances).
    let assembleAt (displayNow: DateTimeOffset) (state: DashboardState) =
        let pushByWorktree =
            state.SessionInstances
            |> Map.values
            |> Server.CodingToolStatus.collapseByWorktree
                displayNow
                Map.empty

        Server.WorktreeApi.assembleFromState
            displayNow
            Set.empty
            Set.empty
            Set.empty
            pushByWorktree
            state.CodingToolSinceByWorktree
            PerRepoState.empty
            (makeWorktree wtA "feat")

    [<Test>]
    member _.``assembleFromState exposes the transition time for a Working status``() =
        async {
            let agent = createAgent ()
            postInstance agent (storedWt "s1" wtA SessionLevelStatus.Working t0)
            let! state = agent.PostAndAsyncReply(GetState)

            let worktree = assembleAt (t0.AddSeconds 1.0) state
            Assert.Multiple(fun () ->
                Assert.That(worktree.CodingTool, Is.EqualTo Working)
                Assert.That(worktree.CodingToolSince, Is.EqualTo(Some t0)))
        }
        |> Async.RunSynchronously

    [<Test>]
    member _.``assembleFromState holds CodingTool Working (no chip) inside the window and surfaces Idle (with the frozen chip) after``() =
        async {
            let agent = createAgent ()
            let idledAt = t0 + TimeSpan.FromSeconds 30.0
            postInstance agent (storedWt "s1" wtA SessionLevelStatus.Working t0)
            postInstance agent (storedWt "s1" wtA SessionLevelStatus.Idle idledAt)
            let! state = agent.PostAndAsyncReply(GetState)

            let held = assembleAt (idledAt + TimeSpan.FromSeconds 3.0) state
            let settled = assembleAt (idledAt + idleDebounceWindow + TimeSpan.FromSeconds 1.0) state

            Assert.That(held.CodingTool, Is.EqualTo Working, "card holds Working within the debounce window")
            Assert.That(held.CodingToolSince, Is.EqualTo None, "no time-since-idle chip while the dot is held Working")
            Assert.That(settled.CodingTool, Is.EqualTo Idle, "real Idle surfaces on the card once the window elapses")
            Assert.That(
                settled.CodingToolSince,
                Is.EqualTo(Some idledAt),
                "chip surfaces the frozen transition once Idle is displayed")
        }
        |> Async.RunSynchronously


// CodingToolSinceByWorktree lives on DashboardState (GLOBAL), so — unlike SessionInstances
// (evicted) or the per-repo data (removeWorktreeData) — it must be pruned when a worktree leaves.
// Otherwise a removed-then-recreated path inherits a stale transition stamp.

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type CodingToolSincePruningTests() =

    let t0 = DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero)
    let sinceFor (state: DashboardState) = state.CodingToolSinceByWorktree |> Map.tryFind wtA

    [<Test>]
    member _.``RemoveWorktree drops the worktree's time-since-idle stamp``() =
        async {
            let agent = createAgent ()
            postInstance agent (storedWt "s1" wtA SessionLevelStatus.Idle t0)
            let! stamped = agent.PostAndAsyncReply(GetState)

            agent.Post(RemoveWorktree(testRepoId, wtA))
            let! pruned = agent.PostAndAsyncReply(GetState)

            Assert.That(sinceFor stamped, Is.EqualTo(Some t0), "stamped on entering Idle")
            Assert.That(sinceFor pruned, Is.EqualTo None, "stamp pruned on worktree removal")
        }
        |> Async.RunSynchronously

    [<Test>]
    member _.``UpdateWorktreeList prunes the stamp for a worktree dropped from the list``() =
        async {
            let agent = createAgent ()
            agent.Post(UpdateWorktreeList(testRepoId, [ makeWorktree wtA "feat" ]))
            postInstance agent (storedWt "s1" wtA SessionLevelStatus.Idle t0)
            let! stamped = agent.PostAndAsyncReply(GetState)

            // The next discovery no longer lists wtA (removed) → its global stamp must be pruned.
            agent.Post(UpdateWorktreeList(testRepoId, []))
            let! pruned = agent.PostAndAsyncReply(GetState)

            Assert.That(sinceFor stamped, Is.EqualTo(Some t0))
            Assert.That(sinceFor pruned, Is.EqualTo None, "stamp pruned when the worktree leaves the list")
        }
        |> Async.RunSynchronously

    [<Test>]
    member _.``A reused worktree path gets a FRESH idle stamp, not the pre-removal frozen one``() =
        async {
            let agent = createAgent ()
            // Worktree goes idle, is removed (pruning the stamp), then the path is reused by a NEW
            // exact instance that also goes idle 10 min later.
            postInstance agent (storedWt "s1" wtA SessionLevelStatus.Idle t0)
            agent.Post(RemoveWorktree(testRepoId, wtA))

            let reusedAt = t0 + TimeSpan.FromMinutes 10.0
            postInstance agent (storedWt "s2" wtA SessionLevelStatus.Idle reusedAt)
            let! reused = agent.PostAndAsyncReply(GetState)

            Assert.That(sinceFor reused, Is.EqualTo(Some reusedAt), "fresh stamp after reuse, not the frozen t0")
        }
        |> Async.RunSynchronously


// Restart seeds the exact collection in one batch and stamps every currently observed collapsed
// status at the rebuild barrier.

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type SeedSessionInstancesTests() =

    let t0 = DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero)
    let sinceFor (state: DashboardState) = state.CodingToolSinceByWorktree |> Map.tryFind wtA

    [<Test>]
    member _.``Seeding stamps the collapsed status at the rebuild time``() =
        async {
            let agent = createAgent ()
            let staleAt = t0
            let currentAt = t0 + TimeSpan.FromMinutes 90.0
            let rebuiltAt = currentAt + TimeSpan.FromSeconds 5.0
            agent.Post(
                SeedSessionInstances(
                    rebuiltAt,
                    [ storedWt "stale" wtA SessionLevelStatus.Idle staleAt
                      storedWt "current" wtA SessionLevelStatus.Idle currentAt ]
                )
            )
            let! seeded = agent.PostAndAsyncReply(GetState)

            Assert.That(
                sinceFor seeded,
                Is.EqualTo(Some rebuiltAt))
        }
        |> Async.RunSynchronously

    [<Test>]
    member _.``Seeding a Working worktree records its current transition``() =
        async {
            let agent = createAgent ()
            agent.Post(
                SeedSessionInstances(
                    t0,
                    [ storedWt "s1" wtA SessionLevelStatus.Working t0 ]
                )
            )
            let! seeded = agent.PostAndAsyncReply(GetState)

            Assert.That(sinceFor seeded, Is.EqualTo(Some t0))
        }
        |> Async.RunSynchronously

    [<Test>]
    member _.``Seeding preserves every exact instance including duplicate durable session ids``() =
        async {
            let agent = createAgent ()
            let staleAt = t0
            let currentAt = t0 + TimeSpan.FromMinutes 90.0
            let first = storedWt "shared" wtA SessionLevelStatus.Idle staleAt
            let second =
                { storedWt "shared" wtA SessionLevelStatus.Idle currentAt with
                    ProcessIdentity = identity "shared-second" }

            agent.Post(
                SeedSessionInstances(
                    currentAt,
                    [ first; second ]
                )
            )
            let! seeded = agent.PostAndAsyncReply(GetState)

            Assert.That(seeded.SessionInstances.Count, Is.EqualTo 2)
            Assert.That(
                seeded.SessionInstances
                |> Map.values
                |> Seq.map (_.SessionId >> SessionId.value)
                |> Seq.toList,
                Is.EqualTo([ "shared"; "shared" ]))
        }
        |> Async.RunSynchronously


// The status-overview "Agent" row (category CodingToolRefresh) has no poll under the push model, so
// without this it sits permanently `pending`. Every accepted extension push must mark the row with the
// pushing worktree + push instant (green success), and a restart seed must prime it from the newest
// known session so it isn't `pending` until the first live heartbeat.

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type CodingToolPushRowTests() =

    let t0 = DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero)
    let pushRow (state: DashboardState) = state.LatestByCategory |> Map.tryFind "CodingToolRefresh"

    [<Test>]
    member _.``A push stamps the Agent row with the worktree and push time as a success``() =
        async {
            let agent = createAgent ()
            postInstance agent (storedWt "s1" wtA SessionLevelStatus.Working t0)
            let! state = agent.PostAndAsyncReply(GetState)

            match pushRow state with
            | Some evt ->
                Assert.That(evt.Message, Is.EqualTo wtA, "row names the pushing worktree")
                Assert.That(evt.Timestamp, Is.EqualTo t0, "row timestamped at the push instant")
                Assert.That(evt.Status, Is.EqualTo(Some StepStatus.Succeeded))
                Assert.That(evt.Duration, Is.EqualTo None, "a push has no poll duration")
            | None -> Assert.Fail "expected a CodingToolRefresh row after a push"
        }
        |> Async.RunSynchronously

    [<Test>]
    member _.``The Agent row advances to the most recent push (any worktree)``() =
        async {
            let agent = createAgent ()
            let wtB = "C:/wt/b"
            postInstance agent (storedWt "s1" wtA SessionLevelStatus.Idle t0)
            postInstance agent (storedWt "s2" wtB SessionLevelStatus.Working (t0 + TimeSpan.FromSeconds 30.0))
            let! state = agent.PostAndAsyncReply(GetState)

            match pushRow state with
            | Some evt ->
                Assert.That(evt.Message, Is.EqualTo wtB)
                Assert.That(evt.Timestamp, Is.EqualTo(t0 + TimeSpan.FromSeconds 30.0))
            | None -> Assert.Fail "expected a CodingToolRefresh row"
        }
        |> Async.RunSynchronously

    [<Test>]
    member _.``Restart seeding primes the Agent row from the newest known session``() =
        async {
            let agent = createAgent ()
            agent.Post(
                SeedSessionInstances(
                    t0 + TimeSpan.FromMinutes 90.0,
                    [ storedWt "stale" wtA SessionLevelStatus.Idle t0
                      storedWt "current" wtA SessionLevelStatus.Idle (t0 + TimeSpan.FromMinutes 90.0) ]
                )
            )
            let! state = agent.PostAndAsyncReply(GetState)

            match pushRow state with
            | Some evt ->
                Assert.That(evt.Message, Is.EqualTo wtA)
                Assert.That(evt.Timestamp, Is.EqualTo(t0 + TimeSpan.FromMinutes 90.0), "newest session, not oldest replayed")
            | None -> Assert.Fail "expected the Agent row primed on restart seed"
        }
        |> Async.RunSynchronously

    [<Test>]
    member _.``Seeding an empty set leaves the Agent row untouched (still pending)``() =
        async {
            let agent = createAgent ()
            agent.Post(SeedSessionInstances(t0, []))
            let! state = agent.PostAndAsyncReply(GetState)

            Assert.That(pushRow state, Is.EqualTo None, "no sessions → no push row, row stays pending")
        }
        |> Async.RunSynchronously
