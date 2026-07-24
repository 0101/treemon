module Tests.CanvasSessionRecoveryTests

open System
open System.Collections.Concurrent
open System.IO
open System.Threading.Tasks
open NUnit.Framework
open Shared
open Server
open Tests.TestUtils

let private uniquePath prefix =
    Path.Combine(Path.GetTempPath(), $"treemon-{prefix}-{Guid.NewGuid():N}")

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
[<NonParallelizable>]
type CanvasSessionRecoveryTests() =

    [<Test>]
    member _.``comment with no target queues and starts a fresh session``() =
        let freshStarts = ConcurrentQueue<unit>()
        let resumed = ConcurrentQueue<string>()

        let result =
            WorktreeApi.recoverQueuedCanvasMessageWith
                (fun () -> async { return CanvasBridge.NoTarget })
                (fun sessionId ->
                    async {
                        resumed.Enqueue(sessionId)
                        return CanvasMessageResult.Queued
                    })
                (fun () ->
                    async {
                        freshStarts.Enqueue(())
                        return Ok ()
                    })
                "beads.html"
            |> runAsync

        Assert.That(result, Is.EqualTo(CanvasMessageResult.Queued))
        Assert.That(freshStarts.Count, Is.EqualTo(1))
        Assert.That(resumed, Is.Empty)

    [<Test>]
    member _.``offline target resumes exactly and surfaces spawn or registration failure``() =
        let resumed = ConcurrentQueue<string>()
        let freshStarts = ConcurrentQueue<unit>()

        let run spawn waitForRegistration =
            WorktreeApi.recoverQueuedCanvasMessageWith
                (fun () -> async { return CanvasBridge.OfflineTarget "session-a" })
                (fun sessionId ->
                    async {
                        resumed.Enqueue(sessionId)
                        return!
                            WorktreeApi.resumeCanvasTargetWith
                                spawn
                                waitForRegistration
                                "diff.html"
                    })
                (fun () ->
                    async {
                        freshStarts.Enqueue(())
                        return Ok ()
                    })
                "diff.html"
            |> runAsync

        Assert.That(
            run
                (fun () -> async { return Ok () })
                (fun () -> async { return true }),
            Is.EqualTo(CanvasMessageResult.Queued))

        match
            run
                (fun () -> async { return Error "resume rejected" })
                (fun () -> async { return true })
        with
        | CanvasMessageResult.OwnerUnavailable message ->
            Assert.That(message, Does.Contain("resume rejected"))
        | other -> Assert.Fail($"Expected spawn failure recovery, got {other}")

        match
            run
                (fun () -> async { return Ok () })
                (fun () -> async { return false })
        with
        | CanvasMessageResult.OwnerUnavailable message ->
            Assert.That(message, Does.Contain("did not register"))
        | other -> Assert.Fail($"Expected registration-timeout recovery, got {other}")

        Assert.That(resumed, Is.EqualTo([ "session-a"; "session-a"; "session-a" ]))
        Assert.That(freshStarts, Is.Empty, "Known targets must never silently fall back to a fresh session")

    [<Test>]
    member _.``concurrent filenames share one worktree launch``() =
        withTempCwd (fun () ->
            let path = uniquePath "shared-canvas-launch"
            let sessionId = $"session-{Guid.NewGuid():N}"
            let launchCalls = ConcurrentQueue<unit>()
            let launchEntered =
                TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let releaseLaunch =
                TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

            let launch () =
                async {
                    launchCalls.Enqueue(())
                    launchEntered.TrySetResult(()) |> ignore
                    do! releaseLaunch.Task |> Async.AwaitTask
                    return Ok ()
                }

            let run filename =
                WorktreeApi.launchFreshCanvasSessionWith
                    (fun () -> CanvasBridge.beginPendingLaunch path filename)
                    (fun () -> CanvasBridge.cancelPendingLaunch path)
                    launch
                    (fun () ->
                        CanvasBridge.waitForPendingLaunchCompletion
                            (TimeSpan.FromSeconds 2.0)
                            path)

            let first = run "diff.html" |> Async.StartAsTask
            Assert.That(
                launchEntered.Task.Wait(TimeSpan.FromSeconds 2.0),
                Is.True,
                "The first request must own and enter the launch")

            Assert.That(runAsync (run "beads.html") |> Result.isOk, Is.True)
            Assert.That(launchCalls.Count, Is.EqualTo(1), "The joining filename must not launch again")

            releaseLaunch.TrySetResult(()) |> ignore
            CanvasBridge.registerSession path "http://127.0.0.1:1/inject" (Some sessionId)

            Assert.That(first.Wait(TimeSpan.FromSeconds 3.0), Is.True)
            Assert.That(first.Result |> Result.isOk, Is.True)
            Assert.That(
                runAsync (CanvasDocOwnership.getOwner path "diff.html"),
                Is.EqualTo(Some sessionId))
            Assert.That(
                runAsync (CanvasDocOwnership.getOwner path "beads.html"),
                Is.EqualTo(Some sessionId)))

    [<Test>]
    member _.``spawn failure and timeout clear pending launch without replacing target``() =
        withTempCwd (fun () ->
            let path = uniquePath "canvas-launch-rollback"
            runAsync (CanvasDocOwnership.assign path "diff.html" "session-a")

            let run launch waitForRegistration =
                WorktreeApi.launchFreshCanvasSessionWith
                    (fun () -> CanvasBridge.beginPendingLaunch path "diff.html")
                    (fun () -> CanvasBridge.cancelPendingLaunch path)
                    launch
                    waitForRegistration
                |> runAsync

            match
                run
                    (fun () -> async { return Error "terminal failed" })
                    (fun () -> async { return true })
            with
            | Error "terminal failed" -> ()
            | other -> Assert.Fail($"Expected terminal failure, got {other}")
            Assert.That(
                runAsync (CanvasBridge.beginPendingLaunch path "diff.html"),
                Is.EqualTo(CanvasBridge.PendingLaunchStarted),
                "Spawn failure must release the worktree launch slot")
            runAsync (CanvasBridge.cancelPendingLaunch path)

            match
                run
                    (fun () -> async { return Ok () })
                    (fun () -> async { return false })
            with
            | Error "the session did not register with Treemon before the timeout" -> ()
            | other -> Assert.Fail($"Expected registration timeout, got {other}")
            Assert.That(
                runAsync (CanvasBridge.beginPendingLaunch path "diff.html"),
                Is.EqualTo(CanvasBridge.PendingLaunchStarted),
                "Timeout must release the worktree launch slot")
            runAsync (CanvasBridge.cancelPendingLaunch path)

            Assert.That(
                runAsync (CanvasDocOwnership.getOwner path "diff.html"),
                Is.EqualTo(Some "session-a"),
                "Failed fresh launches must preserve the durable target"))
