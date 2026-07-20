module Tests.CodingToolSinceTests

open System
open NUnit.Framework
open Server.GitWorktree
open Server.RefreshScheduler
open Server.SessionActivity
open Server.SessionActivityStore
open Shared

let private testRepoId = RepoId "TestRepo"

let private makeWorktree path branch : WorktreeInfo =
    { Path = path; Head = "abc123"; Branch = Some branch }

// The time-since-idle chip (WorktreeStatus.CodingToolSince) is stamped ONCE when a worktree's
// collapsed coding-tool status enters Idle, FROZEN across the idle heartbeats that keep advancing
// last_seen (so it reads time-in-category, not time-since-last-write), and MOVED (cleared, then
// re-stamped) by a new Working turn. stampIdleSince is the pure core; the mailbox test drives the
// real UpdateSessionStatus path end to end.

let private wtA = "C:/wt/a"

let private storedWt (sid: string) (wt: string) (status: SessionLevelStatus) (seen: DateTimeOffset) : StoredStatus =
    { SessionId = SessionId sid
      WorktreePath = WorktreePath wt
      Provider = CopilotCli
      Status = { emptyStatus with Status = status }
      UpdatedAt = seen
      LastSeen = seen
      ContextUsageAt = None }

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type StampIdleSinceTests() =

    let t0 = DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero)

    [<Test>]
    member _.``Entering Idle stamps the transition time``() =
        let result = stampIdleSince t0 wtA Idle Map.empty
        Assert.That(result |> Map.tryFind wtA, Is.EqualTo(Some t0))

    [<Test>]
    member _.``A second Idle poll freezes the original stamp``() =
        let stamped = stampIdleSince t0 wtA Idle Map.empty
        // A later idle heartbeat (last_seen advanced) must NOT move the stamp.
        let frozen = stampIdleSince (t0 + TimeSpan.FromSeconds 60.0) wtA Idle stamped
        Assert.That(frozen |> Map.tryFind wtA, Is.EqualTo(Some t0))

    [<Test>]
    member _.``Leaving Idle for Working clears the stamp``() =
        let stamped = stampIdleSince t0 wtA Idle Map.empty
        let cleared = stampIdleSince (t0 + TimeSpan.FromSeconds 60.0) wtA Working stamped
        Assert.That(cleared |> Map.containsKey wtA, Is.False)

    [<Test>]
    member _.``WaitingForUser and NoSession both clear the stamp``() =
        let stamped = stampIdleSince t0 wtA Idle Map.empty
        Assert.That(stampIdleSince t0 wtA WaitingForUser stamped |> Map.containsKey wtA, Is.False)
        Assert.That(stampIdleSince t0 wtA NoSession stamped |> Map.containsKey wtA, Is.False)

    [<Test>]
    member _.``Stamps for different worktrees are independent``() =
        let m = stampIdleSince t0 wtA Idle Map.empty
        let m2 = stampIdleSince (t0 + TimeSpan.FromMinutes 1.0) "C:/wt/b" Idle m
        Assert.That(m2 |> Map.tryFind wtA, Is.EqualTo(Some t0))
        Assert.That(m2 |> Map.tryFind "C:/wt/b", Is.EqualTo(Some(t0 + TimeSpan.FromMinutes 1.0)))


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type CodingToolSinceByWorktreeTests() =

    let t0 = DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero)

    let sinceFor (state: DashboardState) = state.CodingToolSinceByWorktree |> Map.tryFind wtA

    [<Test>]
    member _.``CodingToolSince is stamped on entering Idle, frozen across idle heartbeats, and moved by a new Working turn``() =
        async {
            let agent = createAgent ()
            // Working — no idle stamp yet.
            agent.Post(UpdateSessionStatus(storedWt "s1" wtA SessionLevelStatus.Working t0))
            let! working = agent.PostAndAsyncReply(GetState)

            // turn_ended → Idle: stamp the turn-end time (t0 + 30s).
            let idledAt = t0 + TimeSpan.FromSeconds 30.0
            agent.Post(UpdateSessionStatus(storedWt "s1" wtA SessionLevelStatus.Idle idledAt))
            let! entered = agent.PostAndAsyncReply(GetState)

            // Two idle heartbeats 60s/120s later keep advancing last_seen — the stamp must NOT move.
            agent.Post(UpdateSessionStatus(storedWt "s1" wtA SessionLevelStatus.Idle (idledAt + TimeSpan.FromSeconds 60.0)))
            agent.Post(UpdateSessionStatus(storedWt "s1" wtA SessionLevelStatus.Idle (idledAt + TimeSpan.FromSeconds 120.0)))
            let! frozen = agent.PostAndAsyncReply(GetState)

            // A new Working turn moves the chip off the idle stamp.
            agent.Post(UpdateSessionStatus(storedWt "s1" wtA SessionLevelStatus.Working (idledAt + TimeSpan.FromSeconds 180.0)))
            let! resumed = agent.PostAndAsyncReply(GetState)

            // Idle again → a NEW stamp at the new turn-end time.
            let reidledAt = idledAt + TimeSpan.FromSeconds 240.0
            agent.Post(UpdateSessionStatus(storedWt "s1" wtA SessionLevelStatus.Idle reidledAt))
            let! reidled = agent.PostAndAsyncReply(GetState)

            Assert.That(sinceFor working, Is.EqualTo None, "no idle stamp while Working")
            Assert.That(sinceFor entered, Is.EqualTo(Some idledAt), "stamped at the Idle transition")
            Assert.That(sinceFor frozen, Is.EqualTo(Some idledAt), "frozen across idle heartbeats")
            Assert.That(sinceFor resumed, Is.EqualTo None, "cleared by a new Working turn")
            Assert.That(sinceFor reidled, Is.EqualTo(Some reidledAt), "re-stamped at the new Idle transition")
        }
        |> Async.RunSynchronously


// The display debounce (SessionActivity.debounceIdle, applied on the card read path) measures its
// Working→Idle hold from the SAME CodingToolSinceByWorktree stamp the scheduler freezes above. The
// DebounceIdleTests unit tests feed idleSince directly; these drive the real UpdateSessionStatus path
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

            agent.Post(UpdateSessionStatus(storedWt "s1" wtA SessionLevelStatus.Working t0))
            agent.Post(UpdateSessionStatus(storedWt "s1" wtA SessionLevelStatus.Idle idledAt))
            let! entered = agent.PostAndAsyncReply(GetState)

            // An idle heartbeat 60s later advances last_seen, but the stamp stays frozen at idledAt.
            agent.Post(UpdateSessionStatus(storedWt "s1" wtA SessionLevelStatus.Idle (idledAt + TimeSpan.FromSeconds 60.0)))
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

    [<Test>]
    member _.``A new Working turn clears the stamp so debounceIdle stops holding``() =
        async {
            let agent = createAgent ()
            let idledAt = t0 + TimeSpan.FromSeconds 30.0
            agent.Post(UpdateSessionStatus(storedWt "s1" wtA SessionLevelStatus.Idle idledAt))
            agent.Post(UpdateSessionStatus(storedWt "s1" wtA SessionLevelStatus.Working (idledAt + TimeSpan.FromSeconds 5.0)))
            let! resumed = agent.PostAndAsyncReply(GetState)

            Assert.That(sinceFor resumed, Is.EqualTo None, "Working clears the stamp")
            Assert.That(
                displayAt (idledAt + TimeSpan.FromSeconds 6.0) resumed,
                Is.EqualTo Idle,
                "no stamp after Working → a later real Idle falls straight through")
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
    // same way the getWorktrees endpoint does (collapseByWorktree over the live session statuses).
    let assembleAt (displayNow: DateTimeOffset) (state: DashboardState) =
        let pushByWorktree =
            Server.CodingToolStatus.collapseByWorktree displayNow (state.SessionStatuses |> Map.values)

        Server.WorktreeApi.assembleFromState
            displayNow
            Set.empty
            Set.empty
            false
            pushByWorktree
            state.CodingToolSinceByWorktree
            PerRepoState.empty
            (makeWorktree wtA "feat")

    [<Test>]
    member _.``assembleFromState holds CodingTool Working (no chip) inside the window and surfaces Idle (with the frozen chip) after``() =
        async {
            let agent = createAgent ()
            let idledAt = t0 + TimeSpan.FromSeconds 30.0
            agent.Post(UpdateSessionStatus(storedWt "s1" wtA SessionLevelStatus.Working t0))
            agent.Post(UpdateSessionStatus(storedWt "s1" wtA SessionLevelStatus.Idle idledAt))
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


// F10/C-13: CodingToolSinceByWorktree lives on DashboardState (GLOBAL), so — unlike SessionStatuses
// (evicted) or the per-repo data (removeWorktreeData) — it must be pruned when a worktree leaves.
// Otherwise a removed-then-recreated path inherits a stale FROZEN idle stamp (stampIdleSince freezes
// existing keys), overstating the chip on reuse.

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
            agent.Post(UpdateSessionStatus(storedWt "s1" wtA SessionLevelStatus.Idle t0))
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
            agent.Post(UpdateSessionStatus(storedWt "s1" wtA SessionLevelStatus.Idle t0))
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
            // session that also goes idle 10 min later. Without the prune, stampIdleSince would freeze
            // the old t0 stamp and the chip would overstate the reused session's idle time.
            agent.Post(UpdateSessionStatus(storedWt "s1" wtA SessionLevelStatus.Idle t0))
            agent.Post(RemoveWorktree(testRepoId, wtA))

            let reusedAt = t0 + TimeSpan.FromMinutes 10.0
            agent.Post(UpdateSessionStatus(storedWt "s2" wtA SessionLevelStatus.Idle reusedAt))
            let! reused = agent.PostAndAsyncReply(GetState)

            Assert.That(sinceFor reused, Is.EqualTo(Some reusedAt), "fresh stamp after reuse, not the frozen t0")
        }
        |> Async.RunSynchronously


// F11/C-14: on restart the store replays live statuses OLDEST-first (LoadLiveStatuses ORDER BY
// last_seen). Feeding them one-by-one through UpdateSessionStatus lets the oldest idle row stamp and
// FREEZE the chip, locking in a stale timestamp instead of the current open session's — the chip then
// OVERSTATES time-since-idle. SeedSessionStatuses seeds in one batch and stamps each worktree from its
// NEWEST session (the accepted "resets on restart" behaviour), WITHOUT reversing the seed order.

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type SeedSessionStatusesTests() =

    let t0 = DateTimeOffset(2026, 3, 1, 12, 0, 0, TimeSpan.Zero)
    let sinceFor (state: DashboardState) = state.CodingToolSinceByWorktree |> Map.tryFind wtA

    [<Test>]
    member _.``Seeding stamps time-since-idle from the newest per-worktree session, not the oldest replayed``() =
        async {
            let agent = createAgent ()
            // Oldest-first, as LoadLiveStatuses returns: a long-stale idle session, then the current open
            // idle session 90 min later — both within the 2h idle window (so both survive eviction).
            let staleAt = t0
            let currentAt = t0 + TimeSpan.FromMinutes 90.0
            agent.Post(
                SeedSessionStatuses
                    [ storedWt "stale" wtA SessionLevelStatus.Idle staleAt
                      storedWt "current" wtA SessionLevelStatus.Idle currentAt ])
            let! seeded = agent.PostAndAsyncReply(GetState)

            Assert.That(
                sinceFor seeded,
                Is.EqualTo(Some currentAt),
                "chip stamped from the newest (current) session, not the stale oldest-replayed one")
        }
        |> Async.RunSynchronously

    [<Test>]
    member _.``Seeding a Working worktree leaves no idle stamp``() =
        async {
            let agent = createAgent ()
            agent.Post(SeedSessionStatuses [ storedWt "s1" wtA SessionLevelStatus.Working t0 ])
            let! seeded = agent.PostAndAsyncReply(GetState)

            Assert.That(sinceFor seeded, Is.EqualTo None)
        }
        |> Async.RunSynchronously

    [<Test>]
    member _.``Seeding preserves the full live status set (same as replaying each row)``() =
        async {
            let agent = createAgent ()
            let staleAt = t0
            let currentAt = t0 + TimeSpan.FromMinutes 90.0
            agent.Post(
                SeedSessionStatuses
                    [ storedWt "stale" wtA SessionLevelStatus.Idle staleAt
                      storedWt "current" wtA SessionLevelStatus.Idle currentAt ])
            let! seeded = agent.PostAndAsyncReply(GetState)

            let ids = seeded.SessionStatuses |> Map.keys |> Seq.map SessionId.value |> Set.ofSeq
            Assert.That(ids, Is.EqualTo(Set.ofList [ "stale"; "current" ]))
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
            agent.Post(UpdateSessionStatus(storedWt "s1" wtA SessionLevelStatus.Working t0))
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
            agent.Post(UpdateSessionStatus(storedWt "s1" wtA SessionLevelStatus.Idle t0))
            agent.Post(UpdateSessionStatus(storedWt "s2" wtB SessionLevelStatus.Working (t0 + TimeSpan.FromSeconds 30.0)))
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
                SeedSessionStatuses
                    [ storedWt "stale" wtA SessionLevelStatus.Idle t0
                      storedWt "current" wtA SessionLevelStatus.Idle (t0 + TimeSpan.FromMinutes 90.0) ])
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
            agent.Post(SeedSessionStatuses [])
            let! state = agent.PostAndAsyncReply(GetState)

            Assert.That(pushRow state, Is.EqualTo None, "no sessions → no push row, row stays pending")
        }
        |> Async.RunSynchronously
