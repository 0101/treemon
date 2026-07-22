module Server.OverviewHistoryCache

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open OverviewData
open Server.OverviewHistoryRollup

type Key =
    { Window: HistoryWindow
      PublishedGeneration: int64
      CompleteThroughBucket: int64 }

type private Entry = Lazy<Task<OverviewHistoryResponse>>

type Cache =
    private
        Cache of ConcurrentDictionary<Key, Entry>

let create () = Cache(ConcurrentDictionary())

let key window publishedGeneration completeThrough =
    { Window = window
      PublishedGeneration = publishedGeneration
      CompleteThroughBucket = toBucket completeThrough }

let private newEntry compute =
    Lazy<Task<OverviewHistoryResponse>>(
        (fun () ->
            async {
                return! compute ()
            }
            |> Async.StartAsTask),
        LazyThreadSafetyMode.ExecutionAndPublication
    )

let private removeIfCurrent
    (Cache entries)
    cacheKey
    entry
    =
    (entries :> ICollection<KeyValuePair<Key, Entry>>)
        .Remove(KeyValuePair(cacheKey, entry))
    |> ignore

let private identity cacheKey =
    cacheKey.PublishedGeneration, cacheKey.CompleteThroughBucket

let private trimWindow
    (Cache entries as cache)
    cacheKey
    entry
    =
    entries
    |> Seq.filter (fun pair -> pair.Key.Window = cacheKey.Window && pair.Key <> cacheKey)
    |> Seq.iter (fun pair ->
        if identity pair.Key < identity cacheKey then
            removeIfCurrent cache pair.Key pair.Value
        else
            removeIfCurrent cache cacheKey entry)

let get
    (Cache entries as cache)
    cacheKey
    (compute: unit -> Async<OverviewHistoryResponse>)
    =
    async {
        let candidate = newEntry compute
        let entry = entries.GetOrAdd(cacheKey, candidate)

        try
            let! response = entry.Value |> Async.AwaitTask
            trimWindow cache cacheKey entry
            return response
        with ex ->
            removeIfCurrent cache cacheKey entry

            let surfaced =
                match ex with
                | :? AggregateException as aggregate when aggregate.InnerExceptions.Count = 1 ->
                    aggregate.InnerException
                | _ -> ex

            return raise surfaced
    }

let internal entryCount (Cache entries) = entries.Count
