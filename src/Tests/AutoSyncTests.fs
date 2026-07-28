module Tests.AutoSyncTests

open System
open System.IO
open System.Net
open System.Threading.Tasks
open NUnit.Framework
open Shared
open Server
open Server.AutoSync
open Server.RefreshScheduler
open Server.SchedulerState
open Server.SessionActivity
open Server.SessionActivityStore

let private tempDirectory () =
    let path = Path.Combine(Path.GetTempPath(), $"treemon-auto-sync-{Guid.NewGuid():N}")
    Directory.CreateDirectory(path) |> ignore
    path

let private storedSession sessionId worktreePath status updatedAt lastSeen =
    { SessionId = SessionId sessionId
      WorktreePath = WorktreePath worktreePath
      Provider = CopilotCli
      Status = { emptyStatus with Status = status }
      UpdatedAt = updatedAt
      LastSeen = lastSeen
      ContextUsageAt = None }

let private gitData path branch behind revision dirty : GitWorktree.GitData =
    { Path = path
      Branch = branch
      HeadCommit = ""
      LastCommitMessage = ""
      LastCommitTime = DateTimeOffset.MinValue
      Upstream = GitWorktree.NoUpstream
      MainBehindCount = behind
      BaseRevision = revision
      IsDirty = dirty
      Comparison = GitWorktree.Clean
      WorkMetrics = None }

/// The same observation with a provider branch, so a PR lookup — which is keyed by the upstream
/// name — resolves instead of falling back to `NoPr`.
let private trackedGitData path branch upstream behind revision dirty =
    { gitData path branch behind revision dirty with
        Upstream = GitWorktree.Upstream upstream }

/// Trigger dependencies with no durable layer: nothing is ever recorded, so every observation is a
/// first one. A session is open, so a test that does not say otherwise exercises the agent path.
/// Tests override the fields whose behavior they assert on.
let private withoutAcceptedRecords: TriggerDependencies =
    { ReadAcceptedRevision = fun _ -> async { return None }
      RecordAcceptedRevision = fun _ _ -> async { return () }
      ClearAcceptedRevision = ignore
      ReadPrStatus = fun _ -> async { return NoPr }
      SelectTarget = fun _ -> async { return OpenSession "session-a" }
      TryBeginOperation = fun _ -> async { return true }
      CompleteOperation = ignore
      MechanicalSync = fun _ -> async { return Ok() }
      Deliver = fun _ -> async { return true } }

let private prInfo isMerged : PrStatus =
    HasPr
        { Id = 42
          Title = "Sync worktree"
          Url = "https://example.test/pr/42"
          IsDraft = false
          Comments = CommentSummary.WithResolution(0, 0)
          Builds = []
          IsOpen = not isMerged
          IsMerged = isMerged
          AutoMergeEnabled = false
          HasConflicts = false }

let private mergedPr = prInfo true
let private openPr = prInfo false

/// The production wiring against a real durable store, with only the delivery outcome faked, so the
/// read/record/clear functions the scheduler injects are the ones under test. The per-worktree
/// operation guard is neutralized on purpose: these fixtures model two observations overlapping
/// inside the durable-record layer, which the guard serializes in production, and
/// `AutoSyncMechanicalTests` covers the guard itself.
let private withAcceptedRecords agent store deliver =
    { autoSyncDependencies agent (SessionManager.createAgent ()) None (Some store) with
        SelectTarget = fun _ -> async { return OpenSession "session-a" }
        TryBeginOperation = fun _ -> async { return true }
        CompleteOperation = ignore
        Deliver = deliver }

let private acceptedRecord revision acceptedAt : AutoSyncStore.AcceptedSyncRecord =
    { BaseRevision = revision
      AcceptedAt = acceptedAt }

/// A durable store rooted in `dir`, loaded from disk exactly as server startup loads it.
let private loadedStore dir =
    let store = AutoSyncStore.create (Path.Combine(dir, "auto-sync.json"))
    store.Load()
    store

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type AutoSyncPersistenceTests() =

    // NUnit lifecycle field: setup creates the per-test directory and teardown consumes it, so it must span fixture members.
    let mutable root = ""

    [<SetUp>]
    member _.SetUp() =
        root <- tempDirectory ()

    [<TearDown>]
    member _.TearDown() =
        if Directory.Exists root then Directory.Delete(root, true)

    [<Test>]
    member _.``Missing auto-sync config reads as an empty set``() =
        Assert.That(TreemonConfig.readAutoSyncBranchSet (Some root), Is.Empty)

    [<Test>]
    member _.``Writing auto-sync branches preserves unrelated repo config``() =
        let path = Path.Combine(root, ".treemon.json")
        File.WriteAllText(path, """{ "archivedBranches": ["old"], "baseBranch": "develop" }""")

        TreemonConfig.setAutoSyncBranches root [ "feature-a"; "feature-b" ]

        Assert.Multiple(fun () ->
            Assert.That(
                TreemonConfig.readAutoSyncBranches root,
                Is.EqualTo([ "feature-a"; "feature-b" ]))
            Assert.That(TreemonConfig.readArchivedBranches root, Is.EqualTo([ "old" ]))
            Assert.That(TreemonConfig.readBaseBranch root, Is.EqualTo("develop")))

    [<Test>]
    member _.``Adding auto-sync preference does not prune stale branch names``() =
        TreemonConfig.setAutoSyncBranches root [ "deleted-branch" ]

        TreemonConfig.modifyAutoSyncBranches root (Set.ofList >> Set.add "feature-a" >> Set.toList)

        Assert.That(
            TreemonConfig.readAutoSyncBranchSet (Some root),
            Is.EqualTo(Set.ofList [ "deleted-branch"; "feature-a" ]))

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type AutoSyncSelectionTests() =

    let now = DateTimeOffset(2026, 7, 23, 12, 0, 0, TimeSpan.Zero)

    [<Test>]
    member _.``Active open winner supplies the auto-sync session id``() =
        let active =
            storedSession
                "active"
                "/repo/wt"
                SessionLevelStatus.Working
                (now.AddMinutes(-2.0))
                (now.AddMinutes(-2.0))

        let newerIdle =
            storedSession
                "idle"
                "/repo/wt"
                SessionLevelStatus.Idle
                (now.AddMinutes(-1.0))
                (now.AddMinutes(-1.0))

        Assert.That(
            selectTargetFromSessions now [ newerIdle; active ],
            Is.EqualTo(OpenSession "active"))

    [<Test>]
    member _.``Greatest activity UpdatedAt supplies the open idle auto-sync session id``() =
        let older =
            storedSession
                "older"
                "/repo/wt"
                SessionLevelStatus.Idle
                (now.AddMinutes(-2.0))
                (now.AddSeconds(-10.0))

        let newer =
            storedSession
                "newer"
                "/repo/wt"
                SessionLevelStatus.Idle
                (now.AddMinutes(-1.0))
                (now.AddMinutes(-2.0))

        Assert.That(
            selectTargetFromSessions now [ older; newer ],
            Is.EqualTo(OpenSession "newer"))

    [<Test>]
    member _.``Greatest activity UpdatedAt supplies a retained id only when no session is open``() =
        let older =
            storedSession
                "older"
                "/repo/wt"
                SessionLevelStatus.Idle
                (now.AddMinutes(-2.0))
                (now.AddMinutes(-20.0))

        let newer =
            storedSession
                "newer"
                "/repo/wt"
                SessionLevelStatus.Idle
                (now.AddMinutes(-1.0))
                (now.AddMinutes(-10.0))

        Assert.That(
            selectTargetFromSessions now [ older; newer ],
            Is.EqualTo(NoOpenSession(Some "newer")))

    [<Test>]
    [<Category("AutoSyncVerification")>]
    member _.``An open idle session is a different target from a retained-only session with the same id``() =
        let session lastSeen =
            storedSession "shared-id" "/repo/wt" SessionLevelStatus.Idle (now.AddMinutes(-1.0)) lastSeen

        let openIdle = selectTargetFromSessions now [ session (now.AddSeconds(-30.0)) ]
        let retainedOnly = selectTargetFromSessions now [ session (now.AddMinutes(-10.0)) ]

        Assert.Multiple(fun () ->
            Assert.That(
                openIdle,
                Is.EqualTo(OpenSession "shared-id"),
                "an idle CLI inside the openness window is still attached")
            Assert.That(
                retainedOnly,
                Is.EqualTo(NoOpenSession(Some "shared-id")),
                "the same id from a closed CLI is retained identity, not an open session")
            Assert.That(openIdle, Is.Not.EqualTo retainedOnly)
            Assert.That(
                SyncTarget.sessionId openIdle,
                Is.EqualTo(SyncTarget.sessionId retainedOnly),
                "the id alone cannot tell the two apart, which is why openness is its own case"))

    [<Test>]
    member _.``A worktree with no sessions has no open session and no retained identity``() =
        Assert.That(selectTargetFromSessions now [], Is.EqualTo(NoOpenSession None))

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type AutoSyncTriggerTests() =

    [<Test>]
    member _.``Fallback launch guard allows only one in-flight launch per worktree``() =
        async {
            let agent = createAgent ()
            let path = "/repo/wt"

            let! first =
                agent.PostAndAsyncReply(fun reply -> TryBeginAutoSyncLaunch(path, reply))

            let! duplicate =
                agent.PostAndAsyncReply(fun reply -> TryBeginAutoSyncLaunch(path, reply))

            agent.Post(CompleteAutoSyncLaunch path)

            let! afterCompletion =
                agent.PostAndAsyncReply(fun reply -> TryBeginAutoSyncLaunch(path, reply))

            Assert.Multiple(fun () ->
                Assert.That(first, Is.True)
                Assert.That(duplicate, Is.False)
                Assert.That(afterCompletion, Is.True))
        }
        |> Async.RunSynchronously

    [<Test>]
    [<Category("AutoSyncVerification")>]
    member _.``Dirty behind worktrees remain eligible and up-to-date worktrees do not``() =
        let dirtyBehind = gitData "/repo/wt" "feature" 2 (Some "base-a") true
        let upToDate = gitData "/repo/wt" "feature" 0 (Some "base-a") true
        let dirtyBehindRevision = revision true dirtyBehind
        let upToDateRevision = revision true upToDate
        let disabledRevision = revision false dirtyBehind

        Assert.Multiple(fun () ->
            Assert.That(dirtyBehindRevision, Is.EqualTo(Some "base-a"))
            Assert.That(upToDateRevision, Is.EqualTo None)
            Assert.That(disabledRevision, Is.EqualTo None))

    [<Test>]
    [<Category("AutoSyncVerification")>]
    member _.``A known merged PR makes a behind worktree ineligible``() =
        TestUtils.withTempDir "treemon-auto-sync-merged" (fun root ->
            let worktree = Path.Combine(root, "feature-a")
            TreemonConfig.setAutoSyncBranches root [ "feature-a" ]
            // Mutable because delivery is the impure boundary whose invocation count is under test.
            let mutable deliveries = 0

            let dependencies =
                { withoutAcceptedRecords with
                    Deliver =
                        fun _ ->
                            async {
                                deliveries <- deliveries + 1
                                return true
                            } }

            let observation = gitData worktree "feature-a" 2 (Some "base-a") false

            trigger dependencies root "origin" "main" mergedPr observation |> TestUtils.runAsync
            let afterMerged = deliveries
            trigger dependencies root "origin" "main" openPr observation |> TestUtils.runAsync

            Assert.Multiple(fun () ->
                Assert.That(revision (isEligible true mergedPr) observation, Is.EqualTo None)
                Assert.That(revision (isEligible true openPr) observation, Is.EqualTo(Some "base-a"))
                Assert.That(
                    afterMerged,
                    Is.Zero,
                    "a merged PR leaves nothing to sync, even while the branch is still listed")
                Assert.That(
                    deliveries,
                    Is.EqualTo(1),
                    "an unmerged PR on the same enabled branch still syncs")))

    [<Test>]
    [<Category("AutoSyncVerification")>]
    member _.``Disabling during target selection delivers nothing and records nothing``() =
        TestUtils.withTempDir "treemon-auto-sync-disable-race" (fun root ->
            let worktree = Path.Combine(root, "feature-a")
            TreemonConfig.setAutoSyncBranches root [ "feature-a" ]
            // Mutable because delivery and recording are the impure boundaries under test.
            let mutable deliveries = 0
            let mutable recorded = []

            let dependencies =
                { withoutAcceptedRecords with
                    RecordAcceptedRevision =
                        fun path baseRevision ->
                            async { recorded <- (path, baseRevision) :: recorded }
                    SelectTarget =
                        fun _ ->
                            async {
                                // Stands in for the RefreshPr cleanup landing mid-operation.
                                TreemonConfig.modifyAutoSyncBranches
                                    root
                                    (Set.ofList >> Set.remove "feature-a" >> Set.toList)

                                return OpenSession "session-a"
                            }
                    Deliver =
                        fun _ ->
                            async {
                                deliveries <- deliveries + 1
                                return true
                            } }

            gitData worktree "feature-a" 2 (Some "base-a") false
            |> trigger dependencies root "origin" "main" NoPr
            |> TestUtils.runAsync

            Assert.Multiple(fun () ->
                Assert.That(deliveries, Is.Zero, "a branch disabled mid-operation must not be prompted")
                Assert.That(
                    recorded,
                    Is.Empty,
                    "nothing was delivered, so nothing may suppress the revision after a re-enable")))

    [<Test>]
    [<Category("AutoSyncVerification")>]
    member _.``A PR reconciled merged during target selection delivers nothing``() =
        TestUtils.withTempDir "treemon-auto-sync-merged-race" (fun root ->
            let worktree = Path.Combine(root, "feature-a")
            TreemonConfig.setAutoSyncBranches root [ "feature-a" ]
            // Mutable because both the observed PR state and delivery are impure boundaries here.
            let mutable observedPr = NoPr
            let mutable deliveries = 0

            let dependencies =
                { withoutAcceptedRecords with
                    ReadPrStatus = fun _ -> async { return observedPr }
                    SelectTarget =
                        fun _ ->
                            async {
                                // The PR refresh reconciles the merge while the target is chosen.
                                observedPr <- mergedPr
                                return OpenSession "session-a"
                            }
                    Deliver =
                        fun _ ->
                            async {
                                deliveries <- deliveries + 1
                                return true
                            } }

            gitData worktree "feature-a" 2 (Some "base-a") false
            |> trigger dependencies root "origin" "main" NoPr
            |> TestUtils.runAsync

            Assert.That(
                deliveries,
                Is.Zero,
                "the pre-delivery gate must re-read the merged state, not trust the starting observation"))

    [<Test>]
    member _.``Only the same revision inside the retry age counts as already accepted``() =
        let now = DateTimeOffset(2026, 7, 27, 12, 0, 0, TimeSpan.Zero)
        let inWindow = acceptedRecord "base-a" (now - acceptedRetryAge + TimeSpan.FromMinutes 1.0)
        let expired = acceptedRecord "base-a" (now - acceptedRetryAge - TimeSpan.FromMinutes 1.0)

        Assert.Multiple(fun () ->
            Assert.That(isAlreadyAccepted now "base-a" (Some inWindow), Is.True)
            Assert.That(
                isAlreadyAccepted now "base-a" (Some expired),
                Is.False,
                "an expired record must allow one more attempt rather than suppress forever")
            Assert.That(
                isAlreadyAccepted now "base-b" (Some inWindow),
                Is.False,
                "a different base revision is new work and must trigger immediately")
            Assert.That(isAlreadyAccepted now "base-a" None, Is.False))

    [<Test>]
    [<Category("AutoSyncVerification")>]
    member _.``An accepted revision stays suppressed when the store is reloaded after a restart``() =
        TestUtils.withTempDir "treemon-auto-sync-restart" (fun root ->
            let worktree = Path.Combine(root, "feature-a")
            TreemonConfig.setAutoSyncBranches root [ "feature-a" ]
            // Mutable because delivery is the impure boundary whose invocation count is under test.
            let mutable deliveries = 0

            let deliver _ =
                async {
                    deliveries <- deliveries + 1
                    return true
                }

            let observation = gitData worktree "feature-a" 2 (Some "base-a") false

            let observeWith store =
                trigger (withAcceptedRecords (createAgent ()) store deliver) root "origin" "main" NoPr observation
                |> TestUtils.runAsync

            let store = loadedStore root
            observeWith store
            TestUtils.assertOk (TestUtils.runAsync (store.Flush())) "persisting the accepted record"

            // A restart recreates every in-process claim empty; only the durable record survives it.
            observeWith (loadedStore root)

            Assert.That(
                deliveries,
                Is.EqualTo(1),
                "a revision accepted before the restart must not be prompted again after it"))

    [<Test>]
    member _.``A rejected delivery leaves no durable record so the revision can be retried``() =
        TestUtils.withTempDir "treemon-auto-sync-rejected" (fun root ->
            let worktree = Path.Combine(root, "feature-a")
            TreemonConfig.setAutoSyncBranches root [ "feature-a" ]
            // Mutable because delivery is the impure boundary whose invocation count is under test.
            let mutable deliveries = 0

            let deliver _ =
                async {
                    deliveries <- deliveries + 1
                    return false
                }

            let store = loadedStore root
            let dependencies = withAcceptedRecords (createAgent ()) store deliver
            let observation = gitData worktree "feature-a" 2 (Some "base-a") false

            trigger dependencies root "origin" "main" NoPr observation |> TestUtils.runAsync
            let rejectedRecord = TestUtils.runAsync (store.Get worktree)
            trigger dependencies root "origin" "main" NoPr observation |> TestUtils.runAsync

            Assert.Multiple(fun () ->
                Assert.That(
                    rejectedRecord,
                    Is.EqualTo(None),
                    "a prompt that was never accepted must leave nothing to suppress the retry")
                Assert.That(deliveries, Is.EqualTo(2), "the same revision must be retried")))

    [<Test>]
    member _.``A newer base revision is delivered once and replaces the record``() =
        TestUtils.withTempDir "treemon-auto-sync-advance" (fun root ->
            let worktree = Path.Combine(root, "feature-a")
            TreemonConfig.setAutoSyncBranches root [ "feature-a" ]
            // Mutable because delivery is the impure boundary whose invocation count is under test.
            let mutable deliveries = 0

            let deliver _ =
                async {
                    deliveries <- deliveries + 1
                    return true
                }

            let store = loadedStore root
            let dependencies = withAcceptedRecords (createAgent ()) store deliver

            let observe baseRevision =
                gitData worktree "feature-a" 2 (Some baseRevision) false
                |> trigger dependencies root "origin" "main" NoPr
                |> TestUtils.runAsync

            observe "base-a"
            observe "base-b"
            observe "base-b"

            Assert.Multiple(fun () ->
                Assert.That(deliveries, Is.EqualTo(2), "each new base revision prompts exactly once")
                Assert.That(
                    TestUtils.runAsync (store.Get worktree) |> Option.map _.BaseRevision,
                    Is.EqualTo(Some "base-b"))))

    [<Test>]
    [<Category("AutoSyncVerification")>]
    member _.``Catching up clears the record so the same revision prompts again``() =
        TestUtils.withTempDir "treemon-auto-sync-catchup" (fun root ->
            let worktree = Path.Combine(root, "feature-a")
            TreemonConfig.setAutoSyncBranches root [ "feature-a" ]
            // Mutable because delivery is the impure boundary whose invocation count is under test.
            let mutable deliveries = 0

            let deliver _ =
                async {
                    deliveries <- deliveries + 1
                    return true
                }

            let store = loadedStore root
            let agent = createAgent ()
            let dependencies = withAcceptedRecords agent store deliver

            let observe behind =
                gitData worktree "feature-a" behind (Some "base-a") false
                |> trigger dependencies root "origin" "main" NoPr
                |> TestUtils.runAsync

            observe 2
            let recordWhileBehind = TestUtils.runAsync (store.Get worktree)
            observe 0
            let recordAfterCatchUp = TestUtils.runAsync (store.Get worktree)
            observe 2

            Assert.Multiple(fun () ->
                Assert.That(recordWhileBehind |> Option.map _.BaseRevision, Is.EqualTo(Some "base-a"))
                Assert.That(recordAfterCatchUp, Is.EqualTo(None))
                Assert.That(
                    deliveries,
                    Is.EqualTo(2),
                    "falling behind the same revision again is new work and must prompt again")))

    [<Test>]
    member _.``A record older than the retry age allows one more prompt``() =
        TestUtils.withTempDir "treemon-auto-sync-expiry" (fun root ->
            let worktree = Path.Combine(root, "feature-a")
            TreemonConfig.setAutoSyncBranches root [ "feature-a" ]
            // Mutable because delivery is the impure boundary whose invocation count is under test.
            let mutable deliveries = 0

            let deliver _ =
                async {
                    deliveries <- deliveries + 1
                    return true
                }

            let store = loadedStore root
            let expiredAt = DateTimeOffset.UtcNow - acceptedRetryAge - TimeSpan.FromMinutes 1.0
            AutoSyncStore.setAccepted store worktree (acceptedRecord "base-a" expiredAt)

            gitData worktree "feature-a" 2 (Some "base-a") false
            |> trigger (withAcceptedRecords (createAgent ()) store deliver) root "origin" "main" NoPr
            |> TestUtils.runAsync

            let refreshedAt =
                TestUtils.runAsync (store.Get worktree)
                |> Option.map _.AcceptedAt
                |> Option.defaultValue DateTimeOffset.MinValue

            Assert.Multiple(fun () ->
                Assert.That(deliveries, Is.EqualTo(1), "an accepted prompt that was never acted on is retried")
                Assert.That(
                    refreshedAt,
                    Is.GreaterThan(expiredAt),
                    "the retry restarts the suppression window")))

    [<Test>]
    [<Category("AutoSyncVerification")>]
    member _.``An expired record retries once without restarting the server``() =
        TestUtils.withTempDir "treemon-auto-sync-live-expiry" (fun root ->
            let worktree = Path.Combine(root, "feature-a")
            TreemonConfig.setAutoSyncBranches root [ "feature-a" ]
            // Mutable because delivery is the impure boundary whose invocation count is under test.
            let mutable deliveries = 0

            let deliver _ =
                async {
                    deliveries <- deliveries + 1
                    return true
                }

            let store = loadedStore root
            // One long-running server: the same durable store spans every observation below.
            let dependencies = withAcceptedRecords (createAgent ()) store deliver
            let observation = gitData worktree "feature-a" 2 (Some "base-a") false

            let observe () =
                trigger dependencies root "origin" "main" NoPr observation |> TestUtils.runAsync

            observe ()
            let afterAccept = deliveries
            observe ()
            let insideWindow = deliveries

            let expiredAt = DateTimeOffset.UtcNow - acceptedRetryAge - TimeSpan.FromMinutes 1.0
            AutoSyncStore.setAccepted store worktree (acceptedRecord "base-a" expiredAt)

            observe ()
            let afterExpiry = deliveries
            observe ()

            Assert.Multiple(fun () ->
                Assert.That(afterAccept, Is.EqualTo(1))
                Assert.That(insideWindow, Is.EqualTo(1), "the same revision stays suppressed inside the retry window")
                Assert.That(
                    afterExpiry,
                    Is.EqualTo(2),
                    "an accepted revision whose record expired must be retried in the same process")
                Assert.That(
                    deliveries,
                    Is.EqualTo(2),
                    "the retry restarts the window instead of prompting on every later refresh")))

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
[<Category("AutoSyncVerification")>]
type AutoSyncMechanicalTests() =

    let now = DateTimeOffset.UtcNow

    /// A behind, clean, auto-sync-enabled worktree: the observation every mechanical test starts
    /// from, with the preference living in the repo root rather than in the worktree itself.
    let enabledObservation root =
        TreemonConfig.setAutoSyncBranches root [ "feature-a" ]
        gitData (Path.Combine(root, "feature-a")) "feature-a" 2 (Some "base-a") false

    [<Test>]
    member _.``An open idle session keeps the sync on the agent path``() =
        TestUtils.withTempDir "treemon-auto-sync-idle-open" (fun root ->
            let observation = enabledObservation root
            // Mutable because delivery and the mechanical sync are the impure boundaries under test.
            let mutable deliveries = []
            let mutable mechanicalRuns = 0

            let idleButOpen =
                storedSession "idle-session" observation.Path SessionLevelStatus.Idle now now

            let dependencies =
                { withoutAcceptedRecords with
                    SelectTarget = fun _ -> async { return selectTargetFromSessions now [ idleButOpen ] }
                    MechanicalSync =
                        fun _ ->
                            async {
                                mechanicalRuns <- mechanicalRuns + 1
                                return Ok()
                            }
                    Deliver =
                        fun request ->
                            async {
                                deliveries <- request :: deliveries
                                return true
                            } }

            trigger dependencies root "origin" "main" NoPr observation |> TestUtils.runAsync

            Assert.Multiple(fun () ->
                Assert.That(
                    mechanicalRuns,
                    Is.Zero,
                    "an idle CLI is still attached to its worktree, so Treemon must not mutate it")
                Assert.That(deliveries |> List.map _.Target, Is.EqualTo([ OpenSession "idle-session" ]))
                Assert.That(deliveries |> List.map _.Prompt, Is.EqualTo([ prompt "origin" "main" ]))))

    [<Test>]
    member _.``No open session completes the sync mechanically without prompting anyone``() =
        TestUtils.withTempDir "treemon-auto-sync-mechanical" (fun root ->
            let observation = enabledObservation root
            let agent = createAgent ()
            // Mutable because the mechanical sync, the bridge, and the launch are the impure
            // boundaries whose requests and invocation counts are under test.
            let mutable requests = []
            let mutable bridgeSends = 0
            let mutable launches = 0
            let mutable recorded = []

            let dependencies =
                { withoutAcceptedRecords with
                    SelectTarget = fun _ -> async { return NoOpenSession None }
                    RecordAcceptedRevision =
                        fun path baseRevision -> async { recorded <- (path, baseRevision) :: recorded }
                    MechanicalSync =
                        fun request ->
                            async {
                                requests <- request :: requests
                                return Ok()
                            }
                    // The real delivery, so "no agent" means neither a bridge send nor a launch.
                    Deliver =
                        deliver
                            (fun _ ->
                                async {
                                    bridgeSends <- bridgeSends + 1
                                    return SessionBridge.DeliveryResult.NoLiveSession
                                })
                            (fun () -> async { return () })
                            (fun path -> agent.PostAndAsyncReply(fun reply -> TryBeginAutoSyncLaunch(path, reply)))
                            (CompleteAutoSyncLaunch >> agent.Post)
                            (fun _ _ ->
                                async {
                                    launches <- launches + 1
                                    return Ok()
                                }) }

            trigger dependencies root "origin" "main" NoPr observation |> TestUtils.runAsync

            let expectedRequest =
                { Sync =
                    { WorktreePath = observation.Path
                      UpstreamRemote = "origin"
                      BaseBranch = "main"
                      Branch = "feature-a" }
                  PrStatus = NoPr }

            Assert.Multiple(fun () ->
                Assert.That(requests, Is.EqualTo([ expectedRequest ]))
                Assert.That(bridgeSends, Is.Zero, "a completed mechanical sync costs no agent delivery")
                Assert.That(launches, Is.Zero, "a completed mechanical sync launches no session")
                Assert.That(
                    recorded,
                    Is.EqualTo([ observation.Path, "base-a" ]),
                    "a finished mechanical sync is an acceptance, so the same revision is not re-synced")))

    [<Test>]
    member _.``Each stopping point hands the agent its own reason and no repository text``() =
        TestUtils.withTempDir "treemon-auto-sync-fallback" (fun root ->
            let observation = enabledObservation root

            // The real composition, so each prompt is reached the way production reaches it: a Git
            // outcome, then — for a branch with an open pull request — the push.
            let promptWith syncOutcome prStatus pushOutcome =
                // Mutable because delivery is the impure boundary the prompt is read from.
                let mutable delivered = []

                let dependencies =
                    { withoutAcceptedRecords with
                        SelectTarget = fun _ -> async { return NoOpenSession None }
                        MechanicalSync =
                            fun request ->
                                mechanicalSync
                                    (fun _ -> async { return syncOutcome })
                                    (fun _ _ -> async { return pushOutcome })
                                    { request with PrStatus = prStatus }
                        Deliver =
                            fun request ->
                                async {
                                    delivered <- request.Prompt :: delivered
                                    return true
                                } }

                trigger dependencies root "origin" "main" NoPr observation |> TestUtils.runAsync
                delivered |> List.exactlyOne

            let dirty =
                promptWith
                    GitBranchSync.BranchSyncOutcome.RefusedDirty
                    NoPr
                    GitBranchSync.BranchPushOutcome.Pushed

            let conflicted =
                promptWith
                    GitBranchSync.BranchSyncOutcome.Conflicted
                    NoPr
                    GitBranchSync.BranchPushOutcome.Pushed

            let pushFailed =
                promptWith
                    GitBranchSync.BranchSyncOutcome.Merged
                    openPr
                    GitBranchSync.BranchPushOutcome.PushFailed

            let branchChanged =
                promptWith
                    GitBranchSync.BranchSyncOutcome.BranchChanged
                    NoPr
                    GitBranchSync.BranchPushOutcome.Pushed

            let prompts = [ dirty; conflicted; pushFailed; branchChanged ]

            let leakedRepositoryText =
                prompts
                |> List.filter (fun text ->
                    text.Contains(observation.Path, StringComparison.Ordinal)
                    || text.Contains(observation.Branch, StringComparison.Ordinal))

            Assert.Multiple(fun () ->
                Assert.That(dirty, Is.EqualTo(fallbackPrompt "origin" "main" DirtyWorktree))
                Assert.That(conflicted, Is.EqualTo(fallbackPrompt "origin" "main" MergeConflict))
                Assert.That(pushFailed, Is.EqualTo(fallbackPrompt "origin" "main" PushFailed))
                Assert.That(branchChanged, Is.EqualTo(fallbackPrompt "origin" "main" BranchChanged))
                Assert.That(
                    prompts |> List.distinct,
                    Has.Exactly(4).Items,
                    "an agent must be able to tell the stopping points apart")
                Assert.That(
                    leakedRepositoryText,
                    Is.Empty,
                    "a prompt carries the structured reason only — never a path, branch, or Git output")))

    [<Test>]
    member _.``Both prompts state both sides of the push policy``() =
        let openPrHalf = "If this branch has an open pull request, push the synced branch after the checks pass"
        let noPrHalf = "otherwise, do not push"

        Assert.Multiple(fun () ->
            Assert.That(prompt "origin" "main", Does.Contain(openPrHalf).And.Contains(noPrHalf))
            Assert.That(
                fallbackPrompt "origin" "main" MergeConflict,
                Does.Contain(openPrHalf).And.Contains(noPrHalf)))

    [<Test>]
    member _.``A merge reconciled before the mutation stops the sync and records nothing``() =
        TestUtils.withTempDir "treemon-auto-sync-merged-mechanical" (fun root ->
            let observation = enabledObservation root
            // Mutable because the observed PR state, the mechanical sync, and delivery are the
            // impure boundaries this race is expressed through.
            let mutable observedPr = NoPr
            let mutable mechanicalRuns = 0
            let mutable deliveries = 0
            let mutable recorded = []

            let dependencies =
                { withoutAcceptedRecords with
                    ReadPrStatus = fun _ -> async { return observedPr }
                    RecordAcceptedRevision =
                        fun path baseRevision -> async { recorded <- (path, baseRevision) :: recorded }
                    SelectTarget =
                        fun _ ->
                            async {
                                // The PR refresh reconciles the merge while the target is chosen.
                                observedPr <- mergedPr
                                return NoOpenSession None
                            }
                    MechanicalSync =
                        fun _ ->
                            async {
                                mechanicalRuns <- mechanicalRuns + 1
                                return Ok()
                            }
                    Deliver =
                        fun _ ->
                            async {
                                deliveries <- deliveries + 1
                                return true
                            } }

            trigger dependencies root "origin" "main" NoPr observation |> TestUtils.runAsync

            Assert.Multiple(fun () ->
                Assert.That(mechanicalRuns, Is.Zero, "nothing may be merged into a branch already merged")
                Assert.That(deliveries, Is.Zero)
                Assert.That(recorded, Is.Empty, "nothing happened, so nothing may suppress the revision")))

    [<Test>]
    member _.``A branch disabled during the sync is never handed to an agent``() =
        TestUtils.withTempDir "treemon-auto-sync-disabled-fallback" (fun root ->
            let observation = enabledObservation root
            // Mutable because delivery is the impure boundary whose invocation count is under test.
            let mutable deliveries = 0

            let dependencies =
                { withoutAcceptedRecords with
                    SelectTarget = fun _ -> async { return NoOpenSession None }
                    MechanicalSync =
                        fun _ ->
                            async {
                                // Stands in for a disable landing while Treemon's own sync ran.
                                TreemonConfig.modifyAutoSyncBranches
                                    root
                                    (Set.ofList >> Set.remove "feature-a" >> Set.toList)

                                return Error GitCommandFailed
                            }
                    Deliver =
                        fun _ ->
                            async {
                                deliveries <- deliveries + 1
                                return true
                            } }

            trigger dependencies root "origin" "main" NoPr observation |> TestUtils.runAsync

            Assert.That(
                deliveries,
                Is.Zero,
                "the gate must be re-passed after the mechanical attempt, not only before it"))

    [<Test>]
    member _.``The operation guard keeps a later observation out of a running sync``() =
        TestUtils.withTempDir "treemon-auto-sync-operation-guard" (fun root ->
            let observation = enabledObservation root
            let agent = createAgent ()
            let syncStarted = TaskCompletionSource()
            let releaseSync = TaskCompletionSource()
            // Mutable because the mechanical sync is the impure boundary whose overlap is under test.
            let mutable mechanicalRuns = 0

            let dependencies =
                { withoutAcceptedRecords with
                    TryBeginOperation =
                        fun path -> agent.PostAndAsyncReply(fun reply -> TryBeginAutoSyncOperation(path, reply))
                    CompleteOperation = CompleteAutoSyncOperation >> agent.Post
                    SelectTarget = fun _ -> async { return NoOpenSession None }
                    MechanicalSync =
                        fun _ ->
                            async {
                                mechanicalRuns <- mechanicalRuns + 1
                                syncStarted.TrySetResult() |> ignore
                                do! Async.AwaitTask releaseSync.Task
                                return Ok()
                            } }

            let running =
                trigger dependencies root "origin" "main" NoPr observation |> Async.StartAsTask

            TestUtils.runAsync (Async.AwaitTask syncStarted.Task)

            // A later fetch that saw the base advance again: the durable record cannot stop it,
            // because a newer revision is genuinely new work, so only the guard can.
            let laterObservation = { observation with BaseRevision = Some "base-b" }

            trigger dependencies root "origin" "main" NoPr laterObservation |> TestUtils.runAsync
            let runsDuringSync = mechanicalRuns

            releaseSync.SetResult()
            TestUtils.runAsync (Async.AwaitTask running)
            trigger dependencies root "origin" "main" NoPr laterObservation |> TestUtils.runAsync

            Assert.Multiple(fun () ->
                Assert.That(runsDuringSync, Is.EqualTo(1), "a second observation must not start a second merge")
                Assert.That(
                    mechanicalRuns,
                    Is.EqualTo(2),
                    "the guard is released with the operation, so the next observation runs")))

    [<Test>]
    member _.``A worktree that disappears mid-operation keeps its guard until the operation completes``() =
        TestUtils.withTempDir "treemon-auto-sync-operation-guard-removal" (fun root ->
            let observation = enabledObservation root
            let agent = createAgent ()
            let repoId = PathUtils.toRepoId root

            let tryBegin path =
                agent.PostAndAsyncReply(fun reply -> TryBeginAutoSyncOperation(path, reply))

            agent.Post(
                UpdateWorktreeList(
                    repoId,
                    [ { GitWorktree.WorktreeInfo.Path = observation.Path
                        Head = "head"
                        Branch = Some "feature-a" } ]))

            let held = tryBegin observation.Path |> TestUtils.runAsync

            // A discovery that no longer lists the path, and an explicit removal: a worktree can vanish
            // from either while its operation is still merging, and neither may hand the guard on.
            agent.Post(UpdateWorktreeList(repoId, []))
            agent.Post(RemoveWorktree(repoId, observation.Path))

            let afterRemoval = tryBegin observation.Path |> TestUtils.runAsync

            agent.Post(CompleteAutoSyncOperation observation.Path)
            let afterCompletion = tryBegin observation.Path |> TestUtils.runAsync

            Assert.Multiple(fun () ->
                Assert.That(held, Is.True, "the first operation takes the guard")
                Assert.That(
                    afterRemoval,
                    Is.False,
                    "removal must not release a guard the running operation still holds")
                Assert.That(
                    afterCompletion,
                    Is.True,
                    "only the operation's own completion releases the guard")))

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type AutoSyncSchedulerDispatchTests() =

    [<Test>]
    [<Category("AutoSyncVerification")>]
    member _.``Slow auto-sync delivery does not delay the next scheduled task``() =
        let root = tempDirectory ()
        let releaseDelivery =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let deliveryStarted =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let deliveryCompleted =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let nextTaskRan =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        try
            TreemonConfig.setAutoSyncBranches root [ "feature" ]

            let dependencies =
                { withoutAcceptedRecords with
                    SelectTarget = fun _ -> async { return OpenSession "session-a" }
                    Deliver =
                        fun _ ->
                            async {
                                deliveryStarted.TrySetResult(()) |> ignore
                                do! releaseDelivery.Task |> Async.AwaitTask
                                deliveryCompleted.TrySetResult(()) |> ignore
                                return true
                            } }

            let schedulerStep =
                async {
                    triggerInBackground
                        dependencies
                        root
                        "origin"
                        "main"
                        NoPr
                        (gitData (Path.Combine(root, "feature")) "feature" 1 (Some "base-a") false)

                    nextTaskRan.TrySetResult(()) |> ignore
                }
                |> Async.StartAsTask

            nextTaskRan.Task.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()
            schedulerStep.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()
            deliveryStarted.Task.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()

            let schedulerCompleted = schedulerStep.IsCompletedSuccessfully
            let deliveryCompletedBeforeRelease = deliveryCompleted.Task.IsCompleted

            Assert.Multiple(fun () ->
                Assert.That(schedulerCompleted, Is.True)
                Assert.That(deliveryCompletedBeforeRelease, Is.False))

            releaseDelivery.TrySetResult(()) |> ignore
            deliveryCompleted.Task.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()
        finally
            releaseDelivery.TrySetResult(()) |> ignore
            if Directory.Exists root then Directory.Delete(root, true)

    [<Test>]
    member _.``Guarded background execution catches workflow failures``() =
        let observed =
            TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously)

        startGuarded
            (fun ex -> observed.TrySetResult(ex.Message) |> ignore)
            (async { return raise (InvalidOperationException "configuration read failed") })

        Assert.That(
            observed.Task.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult(),
            Is.EqualTo("configuration read failed"))

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type AutoSyncDeliveryTests() =

    let request =
        { WorktreePath = WorktreePath "/repo/wt"
          Target = OpenSession "session-a"
          Prompt = "Sync with upstream/main." }

    [<Test>]
    member _.``Live selected session receives the agent prompt without fallback launch``() =
        let expected: SessionBridge.SendRequest =
            { WorktreePath = "/repo/wt"
              SessionId = Some "session-a"
              Prompt = SessionBridge.Prompt.agentPrompt "Sync with upstream/main." }

        let tryDeliver (value: SessionBridge.SendRequest) =
            async {
                Assert.That(value, Is.EqualTo expected)
                return SessionBridge.DeliveryResult.Delivered
            }

        let launch _ _ = failwith "fallback launch must not run"

        let accepted =
            deliver
                tryDeliver
                (fun () -> failwith "registration grace must not run")
                (fun _ -> async { return true })
                ignore
                launch
                request
            |> Async.RunSynchronously

        Assert.That(accepted, Is.True)

    [<Test>]
    [<Category("AutoSyncVerification")>]
    member _.``Open idle session beats newer retained session without fallback launch``() =
        let root = tempDirectory ()

        try
            let now = DateTimeOffset.UtcNow
            let path = "/repo/wt"
            let openIdle =
                storedSession
                    "open-idle"
                    path
                    SessionLevelStatus.Idle
                    (now.AddMinutes(-2.0))
                    now

            let newerClosed =
                storedSession
                    "newer-closed"
                    path
                    SessionLevelStatus.Idle
                    (now.AddMinutes(-1.0))
                    (now.AddMinutes(-10.0))

            use store = new SessionActivityStore(Path.Combine(root, "session-activity.db"))
            store.UpsertStatus newerClosed

            let target = selectTarget (Some store) [ openIdle ] path

            let tryDeliver (value: SessionBridge.SendRequest) =
                async {
                    Assert.That(value.SessionId, Is.EqualTo(Some "open-idle"))
                    return SessionBridge.DeliveryResult.Delivered
                }

            let tryBeginLaunch _ = failwith "fallback launch guard must not run"
            let launch _ _ = failwith "fallback launch must not run"

            let accepted =
                deliver
                    tryDeliver
                    (fun () -> failwith "registration grace must not run")
                    tryBeginLaunch
                    ignore
                    launch
                    { request with Target = target }
                |> Async.RunSynchronously

            Assert.Multiple(fun () ->
                Assert.That(
                    target,
                    Is.EqualTo(OpenSession "open-idle"),
                    "an open idle session wins over newer retained identity as an OPEN target")
                Assert.That(accepted, Is.True))
        finally
            if Directory.Exists root then Directory.Delete(root, true)

    [<Test>]
    [<Category("AutoSyncVerification")>]
    member _.``Selected session registering during grace receives prompt without fallback launch``() =
        // Callback probes cross async boundaries, so immutable values cannot capture their ordering.
        let mutable attempts = 0
        let mutable registrationCompleted = false
        let mutable graceCalls = 0
        let mutable launchAttempts = 0

        let tryDeliver _ =
            async {
                attempts <- attempts + 1

                return
                    if registrationCompleted then
                        SessionBridge.DeliveryResult.Delivered
                    else
                        SessionBridge.DeliveryResult.NoLiveSession
            }

        let accepted =
            deliver
                tryDeliver
                (fun () ->
                    async {
                        graceCalls <- graceCalls + 1
                        registrationCompleted <- true
                    })
                (fun _ -> failwith "fallback launch guard must not run")
                ignore
                (fun _ _ ->
                    async {
                        launchAttempts <- launchAttempts + 1
                        return Ok ()
                    })
                request
            |> Async.RunSynchronously

        Assert.Multiple(fun () ->
            Assert.That(accepted, Is.True)
            Assert.That(attempts, Is.EqualTo(2))
            Assert.That(graceCalls, Is.EqualTo(1))
            Assert.That(registrationGraceMilliseconds, Is.InRange(1, 5000))
            Assert.That(launchAttempts, Is.Zero))

    [<Test>]
    member _.``Successful fallback holds the launch guard through registration grace``() =
        // Callback probes cross async boundaries, so immutable values cannot capture invocation counts.
        let mutable guardHeld = false
        let mutable completions = 0
        let graceStarted =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let releaseGrace =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        let tryBeginLaunch _ =
            async {
                if guardHeld then
                    return false
                else
                    guardHeld <- true
                    return true
            }

        let delivery =
            deliver
                (fun value ->
                    async {
                        Assert.That(value.SessionId, Is.EqualTo None)
                        return SessionBridge.DeliveryResult.NoLiveSession
                    })
                (fun () ->
                    async {
                        graceStarted.TrySetResult(()) |> ignore
                        do! releaseGrace.Task |> Async.AwaitTask
                    })
                tryBeginLaunch
                (fun _ ->
                    completions <- completions + 1
                    guardHeld <- false)
                (fun path prompt ->
                    async {
                        Assert.That(path, Is.EqualTo(WorktreePath "/repo/wt"))
                        Assert.That(prompt, Is.EqualTo("Sync with upstream/main."))
                        return Ok ()
                    })
                { request with Target = NoOpenSession None }
            |> Async.StartAsTask

        graceStarted.Task.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()
        let duplicateAccepted = tryBeginLaunch "/repo/wt" |> Async.RunSynchronously

        Assert.Multiple(fun () ->
            Assert.That(duplicateAccepted, Is.False)
            Assert.That(guardHeld, Is.True)
            Assert.That(completions, Is.Zero)
            Assert.That(delivery.IsCompleted, Is.False))

        releaseGrace.TrySetResult(()) |> ignore
        let accepted =
            delivery.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()
        let acceptedAfterGrace = tryBeginLaunch "/repo/wt" |> Async.RunSynchronously

        Assert.Multiple(fun () ->
            Assert.That(accepted, Is.True)
            Assert.That(completions, Is.EqualTo(1))
            Assert.That(acceptedAfterGrace, Is.True))

    [<Test>]
    member _.``No selected session attempts bridge before fallback launch``() =
        // The async launch callback is the impure boundary under test.
        let mutable deliveryAttempts = 0
        let mutable graceCalls = 0
        let mutable launchAttempts = 0

        let accepted =
            deliver
                (fun value ->
                    async {
                        deliveryAttempts <- deliveryAttempts + 1
                        Assert.That(value.SessionId, Is.EqualTo None)
                        return SessionBridge.DeliveryResult.NoLiveSession
                    })
                (fun () ->
                    async {
                        graceCalls <- graceCalls + 1
                    })
                (fun _ -> async { return true })
                ignore
                (fun _ _ ->
                    async {
                        launchAttempts <- launchAttempts + 1
                        return Ok ()
                    })
                { request with Target = NoOpenSession None }
            |> Async.RunSynchronously

        Assert.Multiple(fun () ->
            Assert.That(accepted, Is.True)
            Assert.That(deliveryAttempts, Is.EqualTo(1))
            Assert.That(graceCalls, Is.EqualTo(1))
            Assert.That(launchAttempts, Is.EqualTo(1)))

    [<TestCase("error")>]
    [<TestCase("exception")>]
    member _.``Failed fallback releases the launch guard immediately``(failureKind: string) =
        // Callback probes cross async boundaries, so immutable values cannot capture guard state.
        let mutable guardHeld = false
        let mutable completions = 0

        let accepted =
            deliver
                (fun _ -> async { return SessionBridge.DeliveryResult.NoLiveSession })
                (fun () -> failwith "registration grace must not run after failed launch")
                (fun _ ->
                    async {
                        guardHeld <- true
                        return true
                    })
                (fun _ ->
                    completions <- completions + 1
                    guardHeld <- false)
                (fun _ _ ->
                    async {
                        if failureKind = "exception" then
                            return raise (InvalidOperationException "launch failed")
                        else
                            return Error "launch failed"
                    })
                { request with Target = NoOpenSession None }
            |> Async.RunSynchronously

        Assert.Multiple(fun () ->
            Assert.That(accepted, Is.False)
            Assert.That(guardHeld, Is.False)
            Assert.That(completions, Is.EqualTo(1)))

    [<Test>]
    member _.``Delivery failure is accepted for queued retry without fallback launch``() =
        let accepted =
            deliver
                (fun _ -> async { return SessionBridge.DeliveryResult.DeliveryFailed })
                (fun () -> failwith "registration grace must not run")
                (fun _ -> failwith "fallback launch guard must not run")
                ignore
                (fun _ _ -> failwith "fallback launch must not run")
                request
            |> Async.RunSynchronously

        Assert.That(accepted, Is.True)

    [<Test>]
    member _.``In-flight fallback guard prevents a duplicate launch after grace``() =
        let launch _ _ = failwith "duplicate launch must not run"

        let accepted =
            deliver
                (fun _ -> async { return SessionBridge.DeliveryResult.NoLiveSession })
                (fun () -> async { return () })
                (fun _ -> async { return false })
                ignore
                launch
                request
            |> Async.RunSynchronously

        Assert.That(accepted, Is.False)

    [<Test>]
    [<Category("AutoSyncVerification")>]
    member _.``Transient bridge POST failure queues the prompt for retry on registration``() =
        let path = $"/test/auto-sync-retry/{Guid.NewGuid():N}"
        let sessionId = $"session-{Guid.NewGuid():N}"
        let port = TestUtils.getFreeTcpPort ()
        // Delivery callbacks cross async HTTP boundaries, so counters must be captured mutably.
        let mutable fallbackGuardAttempts = 0
        let mutable fallbackLaunchAttempts = 0

        use listener = new HttpListener()
        listener.Prefixes.Add($"http://127.0.0.1:{port}/")
        listener.Start()

        SessionBridge.registerSession path $"http://127.0.0.1:{port}/" (Some sessionId)

        let firstRequest = listener.GetContextAsync()
        let delivery =
            deliver
                SessionBridge.tryDeliver
                (fun () -> failwith "registration grace must not run")
                (fun _ ->
                    async {
                        fallbackGuardAttempts <- fallbackGuardAttempts + 1
                        return true
                    })
                ignore
                (fun _ _ ->
                    async {
                        fallbackLaunchAttempts <- fallbackLaunchAttempts + 1
                        return Ok ()
                    })
                { request with
                    WorktreePath = WorktreePath path
                    Target = OpenSession sessionId }
            |> Async.StartAsTask

        let firstContext =
            firstRequest.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()
        firstContext.Response.StatusCode <- 503
        firstContext.Response.Close()

        let accepted = delivery.GetAwaiter().GetResult()

        let retryRequest = listener.GetContextAsync()
        SessionBridge.registerSession path $"http://127.0.0.1:{port}/" (Some sessionId)

        let retryContext =
            retryRequest.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()
        use reader = new StreamReader(retryContext.Request.InputStream)
        let body = reader.ReadToEnd()
        retryContext.Response.StatusCode <- 200
        retryContext.Response.Close()

        let expectedBody =
            SessionBridge.serializePrompt(
                SessionBridge.Prompt.agentPrompt request.Prompt)

        Assert.Multiple(fun () ->
            Assert.That(accepted, Is.True)
            Assert.That(body, Is.EqualTo expectedBody)
            Assert.That(fallbackGuardAttempts, Is.Zero)
            Assert.That(fallbackLaunchAttempts, Is.Zero))

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type AutoSyncEndpointTests() =

    // NUnit lifecycle fields: setup initializes per-test paths consumed by tests and teardown, so immutable locals cannot span those members.
    let mutable root = ""
    let mutable worktree = ""

    [<SetUp>]
    member _.SetUp() =
        root <- tempDirectory ()
        worktree <- Path.Combine(root, "feature-a")
        Directory.CreateDirectory(worktree) |> ignore

    [<TearDown>]
    member _.TearDown() =
        if Directory.Exists root then Directory.Delete(root, true)

    [<Test>]
    [<Category("AutoSyncVerification")>]
    member _.``Differently-cased disable lets the same base revision trigger again``() =
        let normalizedPath = PathUtils.normalizePath worktree
        let differentlyCasedPath = normalizedPath.ToUpperInvariant()
        let repoId = PathUtils.toRepoId root
        let now = DateTimeOffset.UtcNow
        let agent = createAgent ()
        let sessionAgent = SessionManager.createAgent ()
        let port = TestUtils.getFreeTcpPort ()

        use listener = new HttpListener()
        listener.Prefixes.Add($"http://127.0.0.1:{port}/")
        listener.Start()

        agent.Post(
            UpdateWorktreeList(
                repoId,
                [ { GitWorktree.WorktreeInfo.Path = normalizedPath
                    Head = "head"
                    Branch = Some "feature-a" } ]))

        agent.Post(
            UpdateGit(
                repoId,
                normalizedPath,
                gitData normalizedPath "feature-a" 2 (Some "base-a") true))

        agent.Post(
            UpdateSessionStatus(
                storedSession
                    "session-a"
                    normalizedPath
                    SessionLevelStatus.Working
                    now
                    now))

        SessionBridge.registerSession
            normalizedPath
            $"http://127.0.0.1:{port}/"
            (Some "session-a")

        let store = AutoSyncStore.create (Path.Combine(root, "auto-sync.json"))
        store.Load()

        let api =
            WorktreeApi.worktreeApi
                agent
                (CardEventLog.createAgent ())
                sessionAgent
                None
                None
                (Some store)
                [ root ]
                None
                "1.0"
                None

        let enableAndReceive apiWorktreePath =
            let receive = listener.GetContextAsync()
            let toggleTask =
                api.toggleAutoSync apiWorktreePath true
                |> Async.StartAsTask

            let context = receive.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()
            use reader = new StreamReader(context.Request.InputStream)
            let body = reader.ReadToEnd()
            context.Response.StatusCode <- 200
            context.Response.Close()
            body, toggleTask.GetAwaiter().GetResult()

        let body, result = enableAndReceive (WorktreePath differentlyCasedPath)
        let dashboard = api.getWorktrees () |> Async.RunSynchronously
        let status = dashboard.Repos |> List.collect _.Worktrees |> List.exactlyOne
        let enabledRecord = TestUtils.runAsync (store.Get normalizedPath)

        Assert.Multiple(fun () ->
            Assert.That(differentlyCasedPath, Is.Not.EqualTo(normalizedPath))
            Assert.That(Result.isOk result, Is.True)
            Assert.That(
                TreemonConfig.readAutoSyncBranchSet (Some root),
                Is.EqualTo(Set.singleton "feature-a"))
            Assert.That(
                body,
                Is.EqualTo(
                    SessionBridge.serializePrompt(
                    SessionBridge.Prompt.agentPrompt(prompt "origin" "main"))))
            Assert.That(status.AutoSyncEnabled, Is.True)
            Assert.That(
                enabledRecord |> Option.map _.BaseRevision,
                Is.EqualTo(Some "base-a"),
                "the durable record must be keyed by the resolved canonical path"))

        let disableResult =
            api.toggleAutoSync (WorktreePath differentlyCasedPath) false
            |> Async.RunSynchronously
        let disabledBranches = TreemonConfig.readAutoSyncBranchSet (Some root)
        let disabledRecord = TestUtils.runAsync (store.Get normalizedPath)
        let secondBody, reenableResult = enableAndReceive (WorktreePath normalizedPath)
        let finalRecord = TestUtils.runAsync (store.Get normalizedPath)

        Assert.Multiple(fun () ->
            Assert.That(Result.isOk disableResult, Is.True)
            Assert.That(disabledBranches, Is.Empty)
            Assert.That(
                disabledRecord,
                Is.EqualTo(None),
                "disabling must clear the durable record so re-enabling can prompt again")
            Assert.That(Result.isOk reenableResult, Is.True)
            Assert.That(secondBody, Is.EqualTo(body))
            Assert.That(finalRecord |> Option.map _.BaseRevision, Is.EqualTo(Some "base-a")))

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
[<Category("AutoSyncVerification")>]
type AutoSyncVerificationTests() =

    [<Test>]
    member _.``Verification selected working session receives one configured generic prompt``() =
        let root = tempDirectory ()
        let worktree = Path.Combine(root, "feature-a")
        Directory.CreateDirectory(worktree) |> ignore
        let normalizedPath = PathUtils.normalizePath worktree
        let repoId = PathUtils.toRepoId root
        let agent = createAgent ()
        let sessionAgent = SessionManager.createAgent ()
        let selectedPort, otherPort =
            match TestUtils.getFreeTcpPorts 2 with
            | [ selected; other ] -> selected, other
            | ports -> failwith $"expected two free ports, got {ports.Length}"

        use selectedListener = new HttpListener()
        use otherListener = new HttpListener()
        selectedListener.Prefixes.Add($"http://127.0.0.1:{selectedPort}/")
        otherListener.Prefixes.Add($"http://127.0.0.1:{otherPort}/")
        selectedListener.Start()
        otherListener.Start()

        try
            let now = DateTimeOffset.UtcNow

            agent.Post(
                UpdateWorktreeList(
                    repoId,
                    [ { GitWorktree.WorktreeInfo.Path = normalizedPath
                        Head = "head"
                        Branch = Some "feature-a" } ]))
            agent.Post(UpdateUpstreamRemote(repoId, "upstream"))
            agent.Post(UpdateBaseBranch(repoId, "develop"))
            agent.Post(
                UpdateGit(
                    repoId,
                    normalizedPath,
                    gitData normalizedPath "feature-a" 2 (Some "base-a") true))
            agent.Post(
                UpdateSessionStatus(
                    storedSession
                        "selected-working"
                        normalizedPath
                        SessionLevelStatus.Working
                        (now.AddMinutes(-2.0))
                        now))
            agent.Post(
                UpdateSessionStatus(
                    storedSession
                        "other-idle"
                        normalizedPath
                        SessionLevelStatus.Idle
                        (now.AddMinutes(-1.0))
                        now))

            SessionBridge.registerSession
                normalizedPath
                $"http://127.0.0.1:{selectedPort}/"
                (Some "selected-working")
            SessionBridge.registerSession
                normalizedPath
                $"http://127.0.0.1:{otherPort}/"
                (Some "other-idle")

            let api =
                WorktreeApi.worktreeApi
                    agent
                    (CardEventLog.createAgent ())
                    sessionAgent
                    None
                    None
                    None
                    [ root ]
                    None
                    "1.0"
                    None

            let selectedRequest = selectedListener.GetContextAsync()
            let otherRequest = otherListener.GetContextAsync()
            let toggleTask =
                api.toggleAutoSync (WorktreePath normalizedPath) true
                |> Async.StartAsTask

            let selectedContext =
                selectedRequest.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()
            use reader = new StreamReader(selectedContext.Request.InputStream)
            let body = reader.ReadToEnd()
            selectedContext.Response.StatusCode <- 200
            selectedContext.Response.Close()

            let result = toggleTask.GetAwaiter().GetResult()
            let duplicateSelectedRequest = selectedListener.GetContextAsync()
            let expectedPrompt = prompt "upstream" "develop"
            let expectedBody =
                SessionBridge.serializePrompt(
                    SessionBridge.Prompt.agentPrompt expectedPrompt)
            let otherReceived = otherRequest.IsCompleted
            let duplicateDelivery = duplicateSelectedRequest.IsCompleted

            Assert.Multiple(fun () ->
                Assert.That(Result.isOk result, Is.True)
                Assert.That(body, Is.EqualTo expectedBody)
                Assert.That(body.StartsWith("{\"kind\":\"agent-prompt\"", StringComparison.Ordinal), Is.True)
                Assert.That(body.Contains("[canvas]", StringComparison.Ordinal), Is.False)
                Assert.That(otherReceived, Is.False, "The other session must not receive the prompt")
                Assert.That(duplicateDelivery, Is.False, "Delivery must occur exactly once"))
        finally
            if Directory.Exists root then Directory.Delete(root, true)

    [<Test>]
    member _.``Verification repeated no-live observations launch one new session with the same prompt``() =
        let root = tempDirectory ()
        let path = Path.Combine(root, "feature-a")
        Directory.CreateDirectory(path) |> ignore
        let agent = createAgent ()
        let expectedPrompt = fallbackPrompt "upstream" "develop" DirtyWorktree
        // Mutable because the launch callback is the impure boundary whose invocation count is under
        // test, and because the accepted records stand in for the durable store's own mutation.
        let mutable launches = []
        let mutable acceptedRecords = Map.empty

        try
            TreemonConfig.setAutoSyncBranches root [ "feature-a" ]

            let dependencies =
                { withoutAcceptedRecords with
                    ReadAcceptedRevision =
                        fun worktreePath -> async { return Map.tryFind worktreePath acceptedRecords }
                    RecordAcceptedRevision =
                        fun worktreePath baseRevision ->
                            async {
                                acceptedRecords <-
                                    acceptedRecords
                                    |> Map.add
                                        worktreePath
                                        (acceptedRecord baseRevision DateTimeOffset.UtcNow)
                            }
                    SelectTarget = fun _ -> async { return NoOpenSession(Some "retained-session") }
                    // The observation is dirty, so Treemon's own sync refuses it and the worktree
                    // reaches the agent path the way production would send it there.
                    MechanicalSync = fun _ -> async { return Error DirtyWorktree }
                    Deliver =
                        deliver
                            (fun _ -> async { return SessionBridge.DeliveryResult.NoLiveSession })
                            (fun () -> async { return () })
                            (fun worktreePath ->
                                agent.PostAndAsyncReply(fun reply ->
                                    TryBeginAutoSyncLaunch(worktreePath, reply)))
                            (CompleteAutoSyncLaunch >> agent.Post)
                            (fun worktreePath promptText ->
                                async {
                                    launches <- (worktreePath, promptText) :: launches
                                    return Ok ()
                                }) }

            let observation = gitData path "feature-a" 2 (Some "base-a") true
            trigger dependencies root "upstream" "develop" NoPr observation
            |> Async.RunSynchronously
            trigger dependencies root "upstream" "develop" NoPr observation
            |> Async.RunSynchronously

            let expectedLaunches = [ WorktreePath path, expectedPrompt ]

            Assert.That(
                launches,
                Is.EqualTo expectedLaunches,
                "Repeated observations of one base revision must start exactly one new prompted session")
        finally
            if Directory.Exists root then Directory.Delete(root, true)

    [<Test>]
    member _.``Verification revision observations disabling re-enabling and config reload``() =
        let root = tempDirectory ()
        let path = Path.Combine(root, "feature-a")
        Directory.CreateDirectory(path) |> ignore
        // Mutable because delivery is the impure boundary whose exact invocation sequence is under
        // test, and because the accepted records stand in for the durable store's own mutation.
        let mutable deliveries = []
        let mutable acceptedRecords = Map.empty

        try
            File.WriteAllText(
                Path.Combine(root, ".treemon.json"),
                """{ "archivedBranches": ["old"], "baseBranch": "develop", "custom": {"keep": true} }""")
            TreemonConfig.modifyAutoSyncBranches root (Set.ofList >> Set.add "feature-a" >> Set.toList)

            let dependencies =
                { withoutAcceptedRecords with
                    ReadAcceptedRevision =
                        fun worktreePath -> async { return Map.tryFind worktreePath acceptedRecords }
                    RecordAcceptedRevision =
                        fun worktreePath baseRevision ->
                            async {
                                acceptedRecords <-
                                    acceptedRecords
                                    |> Map.add
                                        worktreePath
                                        (acceptedRecord baseRevision DateTimeOffset.UtcNow)
                            }
                    ClearAcceptedRevision =
                        fun worktreePath -> acceptedRecords <- Map.remove worktreePath acceptedRecords
                    SelectTarget = fun _ -> async { return OpenSession "selected-working" }
                    Deliver =
                        fun request ->
                            async {
                                deliveries <- request :: deliveries
                                return true
                            } }

            let observe revision =
                gitData path "feature-a" 2 (Some revision) true
                |> trigger dependencies root "upstream" "develop" NoPr
                |> Async.RunSynchronously

            observe "base-a"
            observe "base-a"
            let afterRepeated = deliveries.Length

            observe "base-b"
            let afterAdvance = deliveries.Length

            TreemonConfig.modifyAutoSyncBranches root (Set.ofList >> Set.remove "feature-a" >> Set.toList)
            dependencies.ClearAcceptedRevision path
            observe "base-b"
            let afterDisable = deliveries.Length

            TreemonConfig.modifyAutoSyncBranches root (Set.ofList >> Set.add "feature-a" >> Set.toList)
            observe "base-b"
            let afterReenable = deliveries.Length

            use config =
                System.Text.Json.JsonDocument.Parse(
                    File.ReadAllText(Path.Combine(root, ".treemon.json")))
            let custom = config.RootElement.GetProperty("custom").GetProperty("keep").GetBoolean()
            let expectedPrompt = prompt "upstream" "develop"

            Assert.Multiple(fun () ->
                Assert.That(afterRepeated, Is.EqualTo(1))
                Assert.That(afterAdvance, Is.EqualTo(2))
                Assert.That(afterDisable, Is.EqualTo(2))
                Assert.That(afterReenable, Is.EqualTo(3))
                Assert.That(
                    deliveries |> List.map _.Prompt,
                    Is.EqualTo(List.replicate 3 expectedPrompt))
                Assert.That(
                    TreemonConfig.readAutoSyncBranchSet (Some root),
                    Is.EqualTo(Set.singleton "feature-a"))
                Assert.That(TreemonConfig.readArchivedBranches root, Is.EqualTo([ "old" ]))
                Assert.That(TreemonConfig.readBaseBranch root, Is.EqualTo("develop"))
                Assert.That(custom, Is.True)
                Assert.That(
                    acceptedRecords |> Map.tryFind path |> Option.map _.BaseRevision,
                    Is.EqualTo(Some "base-b")))
        finally
            if Directory.Exists root then Directory.Delete(root, true)
