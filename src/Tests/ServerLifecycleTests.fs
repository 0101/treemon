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
    member _.``Program shares one store and ingestion drains before releasing its borrow``() =
        withDbPath (fun path ->
            let agent = RefreshScheduler.createAgent ()
            let components = createSessionActivityComponents path agent
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
            let agent = RefreshScheduler.createAgent ()
            let runtime =
                createSessionActivityRuntime
                    path
                    agent
                    Map.empty

            try
                runtime.Components.Service.Start()
                Assert.That(runtime.SnapshotStore.LatestAnchor(), Is.EqualTo None)
            finally
                shutdownSessionActivityRuntime runtime None)

    [<Test>]
    member _.``shutdown stops every store user before disposing the store``() =
        let order = ConcurrentQueue<string>()

        shutdownStoreUsers
            (fun () -> order.Enqueue "ingestion")
            (fun () -> order.Enqueue "scheduler")
            (fun () -> order.Enqueue "store")

        Assert.That(
            order.ToArray(),
            Is.EqualTo([| "ingestion"; "scheduler"; "store" |])
        )

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
            (fun () -> order.Enqueue "http-started")
            (fun () ->
                failed.Task.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()
                |> ignore
                order.Enqueue "scheduler-work")
            CancellationToken.None
            (Some capture)

        Assert.That(
            order.ToArray(),
            Is.EqualTo(
                [| "http-started"
                   "capture-started"
                   "capture-failed"
                   "scheduler-work" |]
            )
        )

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
            (fun () -> order.Enqueue "http-started")
            (fun () ->
                started.Task.WaitAsync(TimeSpan.FromSeconds 5.0).GetAwaiter().GetResult()
                order.Enqueue "shutdown"
                hostStopping.Cancel())
            hostStopping.Token
            (Some capture)

        Assert.Multiple(fun () ->
            Assert.That(stopped.Task.IsCompletedSuccessfully, Is.True)
            Assert.That(
                order.ToArray(),
                Is.EqualTo(
                    [| "http-started"
                       "capture-started"
                       "shutdown"
                       "capture-stopped" |]
                )
            ))
