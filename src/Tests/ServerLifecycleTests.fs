module Tests.ServerLifecycleTests

open System
open System.Collections.Concurrent
open System.IO
open System.Threading
open System.Threading.Tasks
open Microsoft.Data.Sqlite
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
                        Assert.That(Object.ReferenceEquals(components.Store, components.Service.Store), Is.True)
                        Assert.That(Object.ReferenceEquals(components.Store, components.Worker.Store), Is.True))
                    components.Service.Submit report
                finally
                    (components.Service :> IDisposable).Dispose()

                let events =
                    components.Store.QueryWindow(
                        occurredAt - TimeSpan.FromSeconds 1.0,
                        occurredAt + TimeSpan.FromSeconds 1.0
                    )

                Assert.That(events |> List.map _.EventId, Is.EqualTo([ EventId "lifecycle-event" ]))
            finally
                (components.Worker :> IDisposable).Dispose()
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
    member _.``failed initial publication prevents a real runtime from becoming available``() =
        withDbPath (fun path ->
            (new SessionActivityStore(path) :> IDisposable).Dispose()

            execute
                path
                """
CREATE TRIGGER fail_initial_publication
BEFORE INSERT ON overview_history_rows
BEGIN
    SELECT RAISE(ABORT, 'forced initial publication failure');
END;
"""

            let agent = RefreshScheduler.createAgent ()

            Assert.Throws<SqliteException>(fun () ->
                startSessionActivityRuntime path agent |> ignore)
            |> ignore

            use reopened = new SessionActivityStore(path)
            let state = reopened.OverviewRollupState()

            Assert.Multiple(fun () ->
                Assert.That(state.CompleteThrough, Is.EqualTo None)
                Assert.That(state.PublishedGeneration, Is.EqualTo 0L)))

    [<Test>]
    member _.``shutdown stops every store user before disposing the store``() =
        let order = ConcurrentQueue<string>()

        shutdownStoreUsers
            (fun () -> order.Enqueue "worker")
            (fun () -> order.Enqueue "ingestion")
            (fun () -> order.Enqueue "scheduler")
            (fun () -> order.Enqueue "store")

        Assert.That(
            order.ToArray(),
            Is.EqualTo([| "worker"; "ingestion"; "scheduler"; "store" |])
        )

    [<Test>]
    member _.``shutdown awaits the rollup worker and scheduler``() =
        withDbPath (fun path ->
            let agent = RefreshScheduler.createAgent ()
            let runtime = startSessionActivityRuntime path agent

            let scheduler =
                startBackgroundLoop (fun cancellationToken -> async {
                    try
                        do!
                            Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken)
                            |> Async.AwaitTask
                    with :? OperationCanceledException when cancellationToken.IsCancellationRequested ->
                        ()
                })

            runtime.Components.Service.Start()

            Assert.Multiple(fun () ->
                Assert.That(runtime.RollupLoop.Completion.IsCompleted, Is.False)
                Assert.That(scheduler.Completion.IsCompleted, Is.False))

            shutdownSessionActivityRuntime runtime (Some scheduler)

            Assert.Multiple(fun () ->
                Assert.That(runtime.RollupLoop.Completion.IsCompletedSuccessfully, Is.True)
                Assert.That(scheduler.Completion.IsCompletedSuccessfully, Is.True)))
