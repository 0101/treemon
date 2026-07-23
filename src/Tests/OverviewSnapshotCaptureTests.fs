module Tests.OverviewSnapshotCaptureTests

open System
open System.Collections.Concurrent
open System.Threading
open System.Threading.Channels
open System.Threading.Tasks
open NUnit.Framework
open OverviewData
open Server
open Server.OverviewSnapshotCapture
open Shared

let private timeout = TimeSpan.FromSeconds 5.0
let private initial = DateTimeOffset(2026, 7, 23, 12, 0, 1, TimeSpan.Zero)

let private assemblyInputs now : WorktreeApi.RepoAssemblyInputs =
    { Now = now
      IgnorePredicate = fun _ -> false
      RetainedByWorktree = Map.empty
      ArchivedBranches = Map.empty
      TestFailureLogPaths = Set.empty }

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
      GetActiveSessionPaths = fun () -> async.Return Set.empty
      LoadAssemblyInputs = fun boundary _ -> assemblyInputs boundary
      AssembleRepos = fun _ _ _ -> []
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
    member _.``next boundary is always the next future UTC 30-second boundary``() =
        let at second millisecond =
            DateTimeOffset(2026, 7, 23, 12, 0, second, millisecond, TimeSpan.Zero)

        [ at 0 0, at 30 0
          at 0 1, at 30 0
          at 29 999, at 30 0
          at 30 0, DateTimeOffset(2026, 7, 23, 12, 1, 0, TimeSpan.Zero) ]
        |> List.iter (fun (now, expected) ->
            Assert.That(nextBoundary now, Is.EqualTo expected))

    [<Test>]
    member _.``one boundary uses one pinned projection and reduces tasks and agents from the same aggregate``() =
        let boundary = nextBoundary initial
        let calls = ConcurrentQueue<string>()
        let inserted = ConcurrentQueue<OverviewSnapshot>()

        let dependencies =
            { GetState =
                fun () -> async {
                    calls.Enqueue "state"
                    return RefreshScheduler.DashboardState.empty
                }
              GetActiveSessionPaths =
                fun () -> async {
                    calls.Enqueue "sessions"
                    return Set.empty
                }
              LoadAssemblyInputs =
                fun now _ ->
                    calls.Enqueue "inputs"
                    assemblyInputs now
              AssembleRepos =
                fun inputs _ _ ->
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
                Is.EqualTo [| "state"; "sessions"; "inputs"; "assemble"; "aggregate"; "insert" |]
            )
            Assert.That(inserted.ToArray(), Is.EqualTo [| expected |]))

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

        let firstBoundary = nextBoundary initial
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

        let firstBoundary = nextBoundary initial
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
        let firstBoundary = nextBoundary initial
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
        let boundary = nextBoundary initial
        clock.NextWait().GetAwaiter().GetResult() |> ignore
        clock.AdvanceTo boundary
        projectionStarted.Task.WaitAsync(timeout).GetAwaiter().GetResult()

        cancellation.Cancel()
        releaseProjection.SetResult()
        running.WaitAsync(timeout).GetAwaiter().GetResult()
        cancellation.Dispose()

        Assert.That(inserts, Is.Empty)
