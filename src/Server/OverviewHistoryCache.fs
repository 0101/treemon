module Server.OverviewHistoryCache

open System
open System.Collections.Concurrent
open System.Collections.Generic
open System.Threading
open System.Threading.Tasks
open OverviewData

let private lifetime = TimeSpan.FromSeconds 30.0

type private Entry = Lazy<Task<OverviewHistoryResponse>>

type Cache =
    private
        Cache of ConcurrentDictionary<HistoryWindow, Entry>

let create () = Cache(ConcurrentDictionary())

let private reusableAt now (entry: Entry) =
    if not entry.IsValueCreated then
        true
    else
        let computation = entry.Value

        if not computation.IsCompleted then
            true
        elif computation.IsCompletedSuccessfully then
            now < computation.Result.Anchor + lifetime
        else
            false

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
    (entries: ConcurrentDictionary<HistoryWindow, Entry>)
    window
    entry
    =
    (entries :> ICollection<KeyValuePair<HistoryWindow, Entry>>)
        .Remove(KeyValuePair(window, entry))
    |> ignore

let get
    (Cache entries)
    (now: DateTimeOffset)
    window
    (compute: unit -> Async<OverviewHistoryResponse>)
    =
    async {
        let candidate = newEntry compute

        let entry =
            entries.AddOrUpdate(
                window,
                candidate,
                fun _ current ->
                    if reusableAt now current then current
                    else candidate
            )

        try
            return! entry.Value |> Async.AwaitTask
        with ex ->
            removeIfCurrent entries window entry

            let surfaced =
                match ex with
                | :? AggregateException as aggregate when aggregate.InnerExceptions.Count = 1 ->
                    aggregate.InnerException
                | _ -> ex

            return raise surfaced
    }

let internal entryCount (Cache entries) = entries.Count
