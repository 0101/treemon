module Tests.OverviewHistoryCacheTests

open System
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open OverviewData
open Server

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type OverviewHistoryCacheTests() =

    let anchor = DateTimeOffset(2026, 7, 14, 12, 0, 0, TimeSpan.Zero)
    let timeout = TimeSpan.FromSeconds 5.0

    let response at count =
        { Anchor = at
          Snapshots =
            [ { Timestamp = at
                Tasks = [ { Kind = TaskBucketKind.Planned; Count = count } ]
                Agents = [] } ] }

    let get cache now window compute =
        OverviewHistoryCache.get cache now window compute
        |> Async.StartAsTask

    [<Test>]
    member _.``simultaneous callers share one in-flight computation and response``() =
        task {
            let cache = OverviewHistoryCache.create ()
            let calls = ref 0
            let started = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let release = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

            let compute () =
                async {
                    let count = Interlocked.Increment calls
                    started.TrySetResult(()) |> ignore
                    do! release.Task |> Async.AwaitTask
                    return response anchor count
                }

            let requests =
                [ 1..8 ]
                |> List.map (fun _ -> get cache anchor HistoryWindow.Hours12 compute)

            do! started.Task.WaitAsync timeout
            Assert.That(!calls, Is.EqualTo 1)

            release.SetResult(())
            let! responses = Task.WhenAll requests

            Assert.Multiple(fun () ->
                Assert.That(!calls, Is.EqualTo 1)
                Assert.That(responses |> Array.distinct, Has.Length.EqualTo 1)
                Assert.That(responses[0], Is.EqualTo(response anchor 1)))
        }

    [<Test>]
    member _.``entry expires from its response anchor rather than its last hit``() =
        task {
            let cache = OverviewHistoryCache.create ()
            let calls = ref 0

            let compute () =
                async {
                    let generation = Interlocked.Increment calls
                    let responseAnchor =
                        if generation = 1 then anchor
                        else anchor.AddSeconds 30.0

                    return response responseAnchor generation
                }

            let! first = get cache anchor HistoryWindow.Hours24 compute
            let! hit = get cache (anchor.AddSeconds 29.0) HistoryWindow.Hours24 compute
            let! refreshed = get cache (anchor.AddSeconds 30.0) HistoryWindow.Hours24 compute

            Assert.Multiple(fun () ->
                Assert.That(hit, Is.EqualTo first)
                Assert.That(refreshed.Anchor, Is.EqualTo(anchor.AddSeconds 30.0))
                Assert.That(refreshed, Is.Not.EqualTo first)
                Assert.That(!calls, Is.EqualTo 2))
        }

    [<Test>]
    member _.``window keys cache independently and stay bounded``() =
        task {
            let cache = OverviewHistoryCache.create ()
            let calls = ref 0

            let compute window () =
                async {
                    let count = Interlocked.Increment calls
                    let offset =
                        match window with
                        | HistoryWindow.Hours12 -> 12.0
                        | HistoryWindow.Hours24 -> 24.0
                        | HistoryWindow.Hours72 -> 72.0

                    return response (anchor.AddHours offset) count
                }

            let windows =
                [ HistoryWindow.Hours12
                  HistoryWindow.Hours24
                  HistoryWindow.Hours72 ]

            let! first =
                windows
                |> List.map (fun window -> get cache anchor window (compute window))
                |> Task.WhenAll

            let! hits =
                windows
                |> List.map (fun window ->
                    get cache (anchor.AddSeconds 1.0) window (fun () -> async { return failwith "cache miss" }))
                |> Task.WhenAll

            Assert.Multiple(fun () ->
                Assert.That(hits, Is.EqualTo first)
                Assert.That(first |> Array.map _.Anchor |> Array.distinct, Has.Length.EqualTo 3)
                Assert.That(!calls, Is.EqualTo 3)
                Assert.That(OverviewHistoryCache.entryCount cache, Is.EqualTo 3))
        }

    [<Test>]
    member _.``failed computation is evicted so the next request retries``() =
        task {
            let cache = OverviewHistoryCache.create ()
            let calls = ref 0

            let compute () =
                async {
                    let attempt = Interlocked.Increment calls

                    if attempt = 1 then
                        return raise (InvalidOperationException "first attempt failed")

                    return response anchor attempt
                }

            let! firstError =
                task {
                    try
                        let! _ = get cache anchor HistoryWindow.Hours72 compute
                        return None
                    with ex ->
                        return Some ex.Message
                }

            let! retried = get cache anchor HistoryWindow.Hours72 compute

            Assert.Multiple(fun () ->
                Assert.That(firstError, Is.EqualTo(Some "first attempt failed"))
                Assert.That(retried, Is.EqualTo(response anchor 2))
                Assert.That(!calls, Is.EqualTo 2))
        }
