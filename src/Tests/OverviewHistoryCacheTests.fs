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

    let cacheKey window generation at =
        OverviewHistoryCache.key window generation at

    let get cache key compute =
        OverviewHistoryCache.get cache key compute
        |> Async.StartAsTask

    [<Test>]
    member _.``simultaneous callers for one publication share one computation``() =
        task {
            let cache = OverviewHistoryCache.create ()
            let key = cacheKey HistoryWindow.Hours12 4L anchor
            // Concurrent callbacks require one shared atomic invocation counter.
            let mutable calls = 0
            let started = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)
            let release = TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

            let compute () =
                async {
                    let count = Interlocked.Increment(&calls)
                    started.TrySetResult(()) |> ignore
                    do! release.Task |> Async.AwaitTask
                    return response anchor count
                }

            let requests =
                [ 1..8 ]
                |> List.map (fun _ -> get cache key compute)

            do! started.Task.WaitAsync timeout
            Assert.That(calls, Is.EqualTo 1)

            release.SetResult(())
            let! responses = Task.WhenAll requests

            Assert.Multiple(fun () ->
                Assert.That(calls, Is.EqualTo 1)
                Assert.That(responses |> Array.distinct, Has.Length.EqualTo 1)
                Assert.That(responses[0], Is.EqualTo(response anchor 1)))
        }

    [<Test>]
    member _.``same publication key is an immediate cache hit``() =
        task {
            let cache = OverviewHistoryCache.create ()
            let key = cacheKey HistoryWindow.Hours24 7L anchor
            // Cache recomputations require a counter retained across async callbacks.
            let mutable calls = 0

            let compute () =
                async {
                    return response anchor (Interlocked.Increment(&calls))
                }

            let! first = get cache key compute

            let! hit =
                get cache key (fun () ->
                    async {
                        return failwith "matching publication should be cached"
                    })

            Assert.Multiple(fun () ->
                Assert.That(hit, Is.EqualTo first)
                Assert.That(calls, Is.EqualTo 1))
        }

    [<Test>]
    member _.``generation and complete-through bucket each invalidate a window entry``() =
        task {
            let cache = OverviewHistoryCache.create ()
            // Publication changes require a counter retained across async callbacks.
            let mutable calls = 0

            let compute at () =
                async {
                    return response at (Interlocked.Increment(&calls))
                }

            let generation1 = cacheKey HistoryWindow.Hours72 1L anchor
            let generation2 = cacheKey HistoryWindow.Hours72 2L anchor
            let nextBucket = cacheKey HistoryWindow.Hours72 2L (anchor.AddSeconds 30.0)

            let! first = get cache generation1 (compute anchor)
            let! repaired = get cache generation2 (compute anchor)
            let! advanced = get cache nextBucket (compute (anchor.AddSeconds 30.0))

            Assert.Multiple(fun () ->
                Assert.That(first.Snapshots.Head.Tasks.Head.Count, Is.EqualTo 1)
                Assert.That(repaired.Snapshots.Head.Tasks.Head.Count, Is.EqualTo 2)
                Assert.That(advanced.Snapshots.Head.Tasks.Head.Count, Is.EqualTo 3)
                Assert.That(calls, Is.EqualTo 3)
                Assert.That(OverviewHistoryCache.entryCount cache, Is.EqualTo 1))
        }

    [<Test>]
    member _.``window keys cache independently and stay bounded``() =
        task {
            let cache = OverviewHistoryCache.create ()
            // Independent async window callbacks require one shared atomic counter.
            let mutable calls = 0

            let windows =
                [ HistoryWindow.Hours12
                  HistoryWindow.Hours24
                  HistoryWindow.Hours72 ]

            let! first =
                windows
                |> List.map (fun window ->
                    get
                        cache
                        (cacheKey window 3L anchor)
                        (fun () ->
                            async {
                                return response anchor (Interlocked.Increment(&calls))
                            }))
                |> Task.WhenAll

            let! hits =
                windows
                |> List.map (fun window ->
                    get
                        cache
                        (cacheKey window 3L anchor)
                        (fun () -> async { return failwith "cache miss" }))
                |> Task.WhenAll

            Assert.Multiple(fun () ->
                Assert.That(hits, Is.EqualTo first)
                Assert.That(calls, Is.EqualTo 3)
                Assert.That(OverviewHistoryCache.entryCount cache, Is.EqualTo 3))
        }

    [<Test>]
    member _.``failed computation is evicted so the next request retries``() =
        task {
            let cache = OverviewHistoryCache.create ()
            let key = cacheKey HistoryWindow.Hours72 9L anchor
            // Retry callbacks require an attempt counter retained after failure.
            let mutable calls = 0

            let compute () =
                async {
                    let attempt = Interlocked.Increment(&calls)

                    if attempt = 1 then
                        return raise (InvalidOperationException "first attempt failed")

                    return response anchor attempt
                }

            let! firstError =
                task {
                    try
                        let! _ = get cache key compute
                        return None
                    with ex ->
                        return Some ex.Message
                }

            let! retried = get cache key compute

            Assert.Multiple(fun () ->
                Assert.That(firstError, Is.EqualTo(Some "first attempt failed"))
                Assert.That(retried, Is.EqualTo(response anchor 2))
                Assert.That(calls, Is.EqualTo 2))
        }
