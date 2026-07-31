module Tests.ServerLifecycleTests

open System
open System.Collections.Concurrent
open System.IO
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open Program
open Shared
open Server
open Server.SessionActivity
open Server.SessionActivityStore
open Tests.SqliteTestDatabase

let private withDbPath =
    SqliteTestDatabase.withDbPath "treemon-server-lifecycle"

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type ServerLifecycleTests() =

    [<Test>]
    member _.``runtime shares one store and ingestion drains before releasing its borrow``() =
        withDbPath (fun path ->
            let agent = SchedulerState.createAgent ()
            let components = SessionActivityRuntime.createComponents path agent
            let occurredAt = DateTimeOffset.UtcNow

            let report =
                { SessionId = SessionId "lifecycle-session"
                  WorktreePath =
                    WorktreePath(Path.Combine(Path.GetTempPath(), "lifecycle-worktree"))
                  Provider = CopilotCli
                  EventId = EventId "lifecycle-event"
                  OccurredAt = occurredAt
                  Event = TurnStarted }

            try
                try
                    Assert.Multiple(fun () ->
                        Assert.That(Object.ReferenceEquals(components.Store, components.Service.Store), Is.True))
                    components.Service.Submit report
                finally
                    (components.Service :> IDisposable).Dispose()

                Assert.That(
                    scalarInt
                        path
                        "SELECT count(*) FROM activity_events WHERE event_id = 'lifecycle-event';",
                    Is.EqualTo 1
                )
            finally
                (components.Store :> IDisposable).Dispose())

    [<Test>]
    member _.``demo and fixture modes do not create the durable activity runtime``() =
        let real = parseArgs [| "--no-canvas" |]
        let demo = parseArgs [| "--demo" |]
        let fixture = parseArgs [| "--test-fixtures"; "worktrees.json"; "--no-canvas" |]

        Assert.Multiple(fun () ->
            Assert.That(usesSessionActivity real, Is.True)
            Assert.That(usesSessionActivity demo, Is.False)
            Assert.That(usesSessionActivity fixture, Is.False))

    [<Test>]
    member _.``an empty new snapshot store starts without publication preparation``() =
        withDbPath (fun path ->
            let agent = SchedulerState.createAgent ()
            let runtime =
                SessionActivityRuntime.create
                    path
                    agent
                    Map.empty

            try
                runtime.Components.Service.Start()
                let state =
                    agent.PostAndAsyncReply SchedulerState.GetState
                    |> Async.RunSynchronously

                Assert.That(state.SessionStatusesHydrated, Is.True)
                Assert.That(runtime.SnapshotStore.LatestAnchor(), Is.EqualTo None)
            finally
                SessionActivityRuntime.shutdown runtime None)

    [<Test>]
    member _.``shutdown stops every store user before disposing the store``() =
        let order = ConcurrentQueue<string>()

        SessionActivityRuntime.shutdownStoreUsers
            (fun () -> order.Enqueue "ingestion")
            (fun () -> order.Enqueue "scheduler")
            (fun () -> order.Enqueue "store")

        Assert.That(
            order.ToArray(),
            Is.EqualTo([| "ingestion"; "scheduler"; "store" |])
        )

    [<Test>]
    member _.``background loop shares cancellation between workflow and task``() =
        let started =
            TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
        let stopped =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        let loop =
            BackgroundLoop.start (fun cancellationToken -> async {
                let! ambientCancellation = Async.CancellationToken
                let usesSameCancellation = ambientCancellation = cancellationToken
                started.SetResult usesSameCancellation

                try
                    do!
                        Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                        |> Async.AwaitTask
                finally
                    stopped.SetResult()
            })

        Assert.That(
            started.Task.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult(),
            Is.True
        )

        BackgroundLoop.stop "Test background loop" loop
        Assert.That(stopped.Task.IsCompletedSuccessfully, Is.True)

    [<Test>]
    member _.``capture failure does not stop host startup or scheduler work``() =
        let order = ConcurrentQueue<string>()
        let failed =
            TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously)

        let capture _ =
            async {
                order.Enqueue "capture-started"

                try
                    return failwith "forced capture failure"
                with ex ->
                    order.Enqueue "capture-failed"
                    failed.SetResult ex.Message
                    return raise ex
            }

        runHostWithCapture
            (fun () ->
                failed.Task.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()
                |> ignore
                order.Enqueue "http-started")
            (fun () ->
                order.Enqueue "scheduler-work")
            CancellationToken.None
            (Some capture)

        Assert.That(
            order.ToArray(),
            Is.EqualTo(
                [| "capture-started"
                   "capture-failed"
                   "http-started"
                   "scheduler-work" |]
            )
        )

    [<Test>]
    member _.``capture starts while slow HTTP startup is still in progress``() =
        let order = ConcurrentQueue<string>()
        let httpStarting =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let captureStarted =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        let capture (cancellationToken: CancellationToken) =
            async {
                do! httpStarting.Task |> Async.AwaitTask
                order.Enqueue "capture-started"
                captureStarted.SetResult()

                try
                    do!
                        Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                        |> Async.AwaitTask
                with :? OperationCanceledException when cancellationToken.IsCancellationRequested ->
                    ()
            }

        runHostWithCapture
            (fun () ->
                order.Enqueue "http-starting"
                httpStarting.SetResult()
                captureStarted.Task.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()
                order.Enqueue "http-started")
            (fun () -> order.Enqueue "shutdown")
            CancellationToken.None
            (Some capture)

        Assert.That(
            order.ToArray(),
            Is.EqualTo(
                [| "http-starting"
                   "capture-started"
                   "http-started"
                   "shutdown" |]
            )
        )

    [<Test>]
    member _.``host startup failure cancels and awaits capture``() =
        let order = ConcurrentQueue<string>()
        let captureStarted =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let captureStopped =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

        let capture (cancellationToken: CancellationToken) =
            async {
                order.Enqueue "capture-started"
                captureStarted.SetResult()

                try
                    try
                        do!
                            Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                            |> Async.AwaitTask
                    with :? OperationCanceledException when cancellationToken.IsCancellationRequested ->
                        ()
                finally
                    order.Enqueue "capture-stopped"
                    captureStopped.SetResult()
            }

        let failure =
            Assert.Throws<InvalidOperationException>(fun () ->
                runHostWithCapture
                    (fun () ->
                        captureStarted.Task.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()
                        order.Enqueue "http-failed"
                        raise (InvalidOperationException "forced host startup failure"))
                    (fun () -> order.Enqueue "unexpected-wait")
                    CancellationToken.None
                    (Some capture))

        Assert.Multiple(fun () ->
            Assert.That(failure.Message, Is.EqualTo "forced host startup failure")
            Assert.That(captureStopped.Task.IsCompletedSuccessfully, Is.True)
            Assert.That(
                order.ToArray(),
                Is.EqualTo(
                    [| "capture-started"
                       "http-failed"
                       "capture-stopped" |]
                )
            ))

    [<Test>]
    member _.``shutdown cancels and awaits the background capture``() =
        let order = ConcurrentQueue<string>()
        let started =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        let stopped =
            TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
        use hostStopping = new CancellationTokenSource()

        let capture (cancellationToken: CancellationToken) =
            async {
                order.Enqueue "capture-started"
                started.SetResult()

                try
                    try
                        do!
                            Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                            |> Async.AwaitTask
                    with :? OperationCanceledException when cancellationToken.IsCancellationRequested ->
                        ()
                finally
                    order.Enqueue "capture-stopped"
                    stopped.SetResult()
            }

        runHostWithCapture
            (fun () ->
                started.Task.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()
                order.Enqueue "http-started")
            (fun () ->
                order.Enqueue "shutdown"
                hostStopping.Cancel())
            hostStopping.Token
            (Some capture)

        Assert.Multiple(fun () ->
            Assert.That(stopped.Task.IsCompletedSuccessfully, Is.True)
            Assert.That(
                order.ToArray(),
                Is.EqualTo(
                    [| "capture-started"
                       "http-started"
                       "shutdown"
                       "capture-stopped" |]
                )
            ))
