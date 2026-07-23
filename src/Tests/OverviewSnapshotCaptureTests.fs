module Tests.OverviewSnapshotCaptureTests

open System
open System.Collections.Concurrent
open System.IO
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open NUnit.Framework
open OverviewData
open Server
open Server.OverviewSnapshotCapture
open Server.OverviewSnapshotBoundary
open Shared

let private timeout = TimeSpan.FromSeconds 5.0
let private initial = DateTimeOffset(2026, 7, 23, 12, 0, 1, TimeSpan.Zero)

let private assemblyInputs now : WorktreeApi.OverviewAssemblyInputs =
    { Now = now
      IgnorePredicate = fun _ -> false
      ArchivedBranches = Map.empty }

let private nonEmptyState =
    { RefreshScheduler.DashboardState.empty with
        Repos =
            Map.ofList
                [ RepoId "capture-test", RefreshScheduler.PerRepoState.empty ] }

let private worktree path branch : GitWorktree.WorktreeInfo =
    { Path = path
      Head = "abc123"
      Branch = Some branch }

let private storedStatus sessionId path status skill lastUser seen : SessionActivityStore.StoredStatus =
    { SessionId = SessionActivity.SessionId sessionId
      WorktreePath = WorktreePath path
      Provider = CopilotCli
      Status =
        { SessionActivity.emptyStatus with
            Status = status
            Skill = skill
            LastUserMessage =
                lastUser
                |> Option.map (fun text ->
                    { SessionActivity.Message.Text = text
                      At = seen }) }
      UpdatedAt = seen
      LastSeen = seen
      ContextUsageAt = None }

let private overview =
    { Tasks =
        [ { Kind = TaskBucketKind.Planned
            Count = 3
            Members = [] } ]
      Agents =
        [ { Kind = AgentGroupKind.Activity CurrentActivity.Executing
            Count = 2
            Members = [] } ]
      Scale = 3 }

let private defaultDependencies insert : CaptureDependencies =
    { GetState = fun () -> async.Return RefreshScheduler.DashboardState.empty
      LoadAssemblyInputs = assemblyInputs
      AssembleRepos = fun _ _ -> []
      Aggregate = fun _ -> overview
      Insert = insert }

type private CaptureClockWait =
    { Target: DateTimeOffset
      Release: TaskCompletionSource<unit> }

type private ControllableClock(initialNow: DateTimeOffset) =
    let gate = obj ()
    let requests = Channel.CreateUnbounded<DateTimeOffset>()
    // Test time and pending delays are an intentionally mutable clock boundary controlled by AdvanceTo.
    let mutable now = initialNow
    let mutable waits: CaptureClockWait list = []

    member _.Clock: CaptureClock =
        { UtcNow = fun () -> lock gate (fun () -> now)
          WaitUntil =
            fun target cancellationToken -> async {
                let release =
                    TaskCompletionSource<unit>(
                        TaskCreationOptions.RunContinuationsAsynchronously
                    )

                let isReady =
                    lock gate (fun () ->
                        if now >= target then
                            true
                        else
                            waits <- { Target = target; Release = release } :: waits
                            false)

                do!
                    requests.Writer.WriteAsync(target, cancellationToken).AsTask()
                    |> Async.AwaitTask

                if isReady then
                    release.SetResult()

                do! release.Task.WaitAsync(cancellationToken) |> Async.AwaitTask
            } }

    member _.AdvanceTo(timestamp: DateTimeOffset) =
        let ready =
            lock gate (fun () ->
                now <- timestamp
                let ready, pending = waits |> List.partition (fun wait -> wait.Target <= now)
                waits <- pending
                ready)

        ready
        |> List.iter (fun wait -> wait.Release.TrySetResult() |> ignore)

    member _.NextWait() =
        requests.Reader.ReadAsync().AsTask().WaitAsync(timeout)

let private runCapture clock dependencies onError =
    let capture = SnapshotCapture(dependencies, clock, onError)
    let cancellation = new CancellationTokenSource()
    let running = capture.Run cancellation.Token |> Async.StartAsTask
    cancellation, running

let private stopCapture (cancellation: CancellationTokenSource) (running: Task) =
    cancellation.Cancel()
    running.WaitAsync(timeout).GetAwaiter().GetResult()
    cancellation.Dispose()

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type OverviewSnapshotCaptureTests() =

    [<Test>]
    member _.``canonical boundary helper floors the current bucket and returns the next future bucket``() =
        let at second millisecond =
            DateTimeOffset(2026, 7, 23, 12, 0, second, millisecond, TimeSpan.Zero)

        [ at 0 0, at 0 0, at 30 0
          at 0 1, at 0 0, at 30 0
          at 29 999, at 0 0, at 30 0
          at 30 0, at 30 0, DateTimeOffset(2026, 7, 23, 12, 1, 0, TimeSpan.Zero) ]
        |> List.iter (fun (now, expectedFloor, expectedNext) ->
            Assert.Multiple(fun () ->
                Assert.That(floor now, Is.EqualTo expectedFloor)
                Assert.That(next now, Is.EqualTo expectedNext)))

    [<Test>]
    member _.``one boundary uses one pinned projection and reduces tasks and agents from the same aggregate``() =
        let boundary = next initial
        let calls = ConcurrentQueue<string>()
        let inserted = ConcurrentQueue<OverviewSnapshot>()

        let dependencies =
            { GetState =
                fun () -> async {
                    calls.Enqueue "state"
                    return nonEmptyState
                }
              LoadAssemblyInputs =
                fun now ->
                    calls.Enqueue "inputs"
                    assemblyInputs now
              AssembleRepos =
                fun inputs _ ->
                    calls.Enqueue "assemble"
                    Assert.That(inputs.Now, Is.EqualTo boundary)
                    []
              Aggregate =
                fun _ ->
                    calls.Enqueue "aggregate"
                    overview
              Insert =
                fun snapshot ->
                    calls.Enqueue "insert"
                    inserted.Enqueue snapshot
                    true }

        captureBoundary dependencies boundary CancellationToken.None
        |> Async.RunSynchronously

        let expected =
            { Timestamp = boundary
              Tasks = [ { Kind = TaskBucketKind.Planned; Count = 3 } ]
              Agents =
                [ { Kind = AgentGroupKind.Activity CurrentActivity.Executing
                    Count = 2 } ] }

        Assert.Multiple(fun () ->
            Assert.That(
                calls.ToArray(),
                Is.EqualTo [| "state"; "inputs"; "assemble"; "aggregate"; "insert" |]
            )
            Assert.That(inserted.ToArray(), Is.EqualTo [| expected |]))

    [<Test>]
    member _.``empty monitored state stores the canonical zero snapshot without loading assembly inputs``() =
        let boundary = next initial
        let calls = ConcurrentQueue<string>()
        let inserted = ConcurrentQueue<OverviewSnapshot>()

        let dependencies =
            { GetState =
                fun () -> async {
                    calls.Enqueue "state"
                    return RefreshScheduler.DashboardState.empty
                }
              LoadAssemblyInputs =
                fun _ ->
                    calls.Enqueue "inputs"
                    failwith "empty state must not load capture inputs"
              AssembleRepos =
                fun _ _ ->
                    calls.Enqueue "assemble"
                    failwith "empty state must not assemble repos"
              Aggregate =
                fun repos ->
                    calls.Enqueue "aggregate"
                    Assert.That(repos, Is.Empty)
                    OverviewData.aggregate repos
              Insert =
                fun snapshot ->
                    calls.Enqueue "insert"
                    inserted.Enqueue snapshot
                    true }

        captureBoundary dependencies boundary CancellationToken.None
        |> Async.RunSynchronously

        Assert.Multiple(fun () ->
            Assert.That(calls.ToArray(), Is.EqualTo [| "state"; "aggregate"; "insert" |])
            Assert.That(
                inserted.ToArray(),
                Is.EqualTo
                    [| { Timestamp = boundary
                         Tasks = []
                         Agents = [] } |]
            ))

    [<Test>]
    member _.``lean assembly matches the complete canonical Overview while omitting card-only decoration``() =
        let boundary = next initial
        let repoId = RepoId "equivalence"
        let root = Path.Combine(Path.GetTempPath(), "treemon-overview-capture-equivalence")
        let activePath = Path.Combine(root, "active")
        let retainedPath = Path.Combine(root, "retained")
        let archivedPath = Path.Combine(root, "archived")
        let ignoredPath = Path.Combine(root, "ignored")

        let activeSession =
            storedStatus
                "active"
                activePath
                SessionActivity.SessionLevelStatus.Working
                (Some "bd-execute")
                None
                (boundary.AddSeconds(-2.0))

        let waitingSession =
            storedStatus
                "waiting"
                activePath
                SessionActivity.SessionLevelStatus.WaitingForUser
                None
                None
                (boundary.AddSeconds(-1.0))

        let retainedFooter =
            storedStatus
                "retained"
                retainedPath
                SessionActivity.SessionLevelStatus.Idle
                None
                (Some "retained footer")
                (boundary.AddDays(-1.0))

        let repo =
            { RefreshScheduler.PerRepoState.empty with
                WorktreeList =
                    [ worktree activePath "active"
                      worktree retainedPath "retained"
                      worktree archivedPath "archived"
                      worktree ignoredPath "ignored" ]
                BeadsData =
                    Map.ofList
                        [ activePath,
                          { Open = 0
                            InProgress = 2
                            Blocked = 1
                            Closed = 1 }
                          retainedPath,
                          { Open = 0
                            InProgress = 4
                            Blocked = 2
                            Closed = 1 }
                          archivedPath,
                          { Open = 0
                            InProgress = 100
                            Blocked = 100
                            Closed = 100 }
                          ignoredPath,
                          { Open = 0
                            InProgress = 100
                            Blocked = 100
                            Closed = 100 } ]
                PlanningData =
                    Map.ofList
                        [ activePath,
                          { Planned = 1
                            Queued = 3
                            Loose = 1 }
                          retainedPath,
                          { Planned = 2
                            Queued = 5
                            Loose = 0 }
                          archivedPath,
                          { Planned = 100
                            Queued = 100
                            Loose = 100 }
                          ignoredPath,
                          { Planned = 100
                            Queued = 100
                            Loose = 100 } ]
                IsReady = true }

        let state =
            { RefreshScheduler.DashboardState.empty with
                Repos = Map.ofList [ repoId, repo ]
                SessionStatuses =
                    Map.ofList
                        [ activeSession.SessionId, activeSession
                          waitingSession.SessionId, waitingSession ] }

        let rootPaths = Map.ofList [ repoId, root ]
        let archivedBranches = Map.ofList [ repoId, Set.singleton "archived" ]
        let ignorePredicate value = value = "ignored"

        let completeRepos =
            WorktreeApi.assembleRepos
                { Now = boundary
                  IgnorePredicate = ignorePredicate
                  RetainedByWorktree = Map.ofList [ retainedPath, retainedFooter ]
                  ArchivedBranches = archivedBranches
                  TestFailureLogPaths = Set.singleton activePath }
                rootPaths
                (Set.singleton activePath)
                state

        let leanRepos =
            WorktreeApi.assembleOverviewRepos
                { Now = boundary
                  IgnorePredicate = ignorePredicate
                  ArchivedBranches = archivedBranches }
                rootPaths
                state

        let worktrees (repos: RepoWorktrees list) =
            repos
            |> List.collect _.Worktrees

        let status branch repos =
            worktrees repos
            |> List.find (fun wt -> wt.Branch = branch)

        let completeActive = status "active" completeRepos
        let leanActive = status "active" leanRepos
        let completeRetained = status "retained" completeRepos
        let leanRetained = status "retained" leanRepos

        Assert.Multiple(fun () ->
            Assert.That(
                OverviewData.aggregate leanRepos,
                Is.EqualTo(OverviewData.aggregate completeRepos)
            )
            Assert.That(worktrees leanRepos |> List.map _.Branch, Does.Not.Contain("ignored"))
            Assert.That((status "archived" leanRepos).IsArchived, Is.True)
            Assert.That(completeActive.HasActiveSession, Is.True)
            Assert.That(completeActive.HasTestFailureLog, Is.True)
            Assert.That(leanActive.HasActiveSession, Is.False)
            Assert.That(leanActive.HasTestFailureLog, Is.False)
            Assert.That(completeRetained.LastUserMessage.IsSome, Is.True)
            Assert.That(leanRetained.LastUserMessage, Is.EqualTo None))

    [<Test>]
    member _.``an overtaken unstarted boundary is skipped without catch-up``() =
        let clock = ControllableClock(initial)
        let inserted = Channel.CreateUnbounded<OverviewSnapshot>()
        let dependencies =
            defaultDependencies (fun snapshot ->
                inserted.Writer.TryWrite snapshot |> ignore
                true)
        let cancellation, running =
            runCapture clock.Clock dependencies (fun _ _ -> ())

        let firstBoundary = next initial
        Assert.That(clock.NextWait().GetAwaiter().GetResult(), Is.EqualTo firstBoundary)
        clock.AdvanceTo(firstBoundary.AddSeconds 30.0)

        let nextFuture = firstBoundary.AddSeconds 60.0
        Assert.That(clock.NextWait().GetAwaiter().GetResult(), Is.EqualTo nextFuture)
        Assert.That(inserted.Reader.TryRead() |> fst, Is.False)

        clock.AdvanceTo nextFuture
        let snapshot =
            inserted.Reader.ReadAsync().AsTask().WaitAsync(timeout).GetAwaiter().GetResult()

        stopCapture cancellation running
        Assert.That(snapshot.Timestamp, Is.EqualTo nextFuture)

    [<Test>]
    member _.``capture failure is isolated and the next future boundary still runs``() =
        let clock = ControllableClock(initial)
        let attempts = ConcurrentQueue<DateTimeOffset>()
        let errors = ConcurrentQueue<DateTimeOffset * string>()
        let completed = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        let dependencies =
            defaultDependencies (fun snapshot ->
                attempts.Enqueue snapshot.Timestamp

                if attempts.Count = 1 then
                    raise (InvalidOperationException "forced capture failure")

                completed.SetResult()
                true)

        let cancellation, running =
            runCapture
                clock.Clock
                dependencies
                (fun boundary ex -> errors.Enqueue(boundary, ex.Message))

        let firstBoundary = next initial
        clock.NextWait().GetAwaiter().GetResult() |> ignore
        clock.AdvanceTo firstBoundary

        let secondBoundary = firstBoundary.AddSeconds 30.0
        Assert.That(clock.NextWait().GetAwaiter().GetResult(), Is.EqualTo secondBoundary)
        clock.AdvanceTo secondBoundary
        completed.Task.WaitAsync(timeout).GetAwaiter().GetResult()

        stopCapture cancellation running
        Assert.Multiple(fun () ->
            Assert.That(attempts.ToArray(), Is.EqualTo [| firstBoundary; secondBoundary |])
            Assert.That(
                errors.ToArray(),
                Is.EqualTo [| firstBoundary, "forced capture failure" |]
            ))

    [<Test>]
    member _.``a long attempt never overlaps or catches up missed boundaries``() =
        let clock = ControllableClock(initial)
        let started = Channel.CreateUnbounded<int>()
        let releaseFirst = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let observedActive = ConcurrentQueue<int>()
        // Concurrency counters are mutable only at this test synchronization boundary.
        let mutable active = 0
        let mutable attempt = 0

        let dependencies =
            { defaultDependencies (fun _ -> true) with
                GetState =
                    fun () -> async {
                        let currentAttempt = Interlocked.Increment(&attempt)
                        let currentActive = Interlocked.Increment(&active)
                        observedActive.Enqueue currentActive
                        started.Writer.TryWrite currentAttempt |> ignore

                        if currentAttempt = 1 then
                            do! releaseFirst.Task |> Async.AwaitTask

                        Interlocked.Decrement(&active) |> ignore
                        return RefreshScheduler.DashboardState.empty
                    } }

        let cancellation, running =
            runCapture clock.Clock dependencies (fun _ _ -> ())
        let firstBoundary = next initial
        Assert.That(clock.NextWait().GetAwaiter().GetResult(), Is.EqualTo firstBoundary)
        clock.AdvanceTo firstBoundary
        Assert.That(
            started.Reader.ReadAsync().AsTask().WaitAsync(timeout).GetAwaiter().GetResult(),
            Is.EqualTo 1
        )

        let overtakenAt = firstBoundary.AddSeconds 90.0
        clock.AdvanceTo overtakenAt
        releaseFirst.SetResult()

        let nextFuture = overtakenAt.AddSeconds 30.0
        Assert.That(clock.NextWait().GetAwaiter().GetResult(), Is.EqualTo nextFuture)
        clock.AdvanceTo nextFuture
        Assert.That(
            started.Reader.ReadAsync().AsTask().WaitAsync(timeout).GetAwaiter().GetResult(),
            Is.EqualTo 2
        )

        stopCapture cancellation running
        Assert.That(observedActive |> Seq.max, Is.EqualTo 1)

    [<Test>]
    member _.``cancellation during projection prevents the store write and stops the loop``() =
        let clock = ControllableClock(initial)
        let projectionStarted =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let releaseProjection =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let inserts = ConcurrentQueue<OverviewSnapshot>()

        let dependencies =
            { defaultDependencies (fun snapshot ->
                  inserts.Enqueue snapshot
                  true) with
                GetState =
                    fun () -> async {
                        projectionStarted.SetResult()
                        do! releaseProjection.Task |> Async.AwaitTask
                        return RefreshScheduler.DashboardState.empty
                    } }

        let cancellation, running =
            runCapture clock.Clock dependencies (fun _ _ -> ())
        let boundary = next initial
        clock.NextWait().GetAwaiter().GetResult() |> ignore
        clock.AdvanceTo boundary
        projectionStarted.Task.WaitAsync(timeout).GetAwaiter().GetResult()

        cancellation.Cancel()
        releaseProjection.SetResult()
        running.WaitAsync(timeout).GetAwaiter().GetResult()
        cancellation.Dispose()

        Assert.That(inserts, Is.Empty)
