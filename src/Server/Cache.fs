module Server.Cache

open System
open System.Collections.Concurrent

type CacheEntry<'T> =
    { Value: 'T
      CachedAt: DateTimeOffset }

type TtlCache<'T>(ttl: TimeSpan) =
    let store = ConcurrentDictionary<string, CacheEntry<'T>>()

    member _.GetOrRefreshAsync key (compute: string -> Async<'T>) =
        async {
            let now = DateTimeOffset.UtcNow

            match store.TryGetValue(key) with
            | true, entry when now - entry.CachedAt < ttl -> return entry.Value
            | _ ->
                let! value = compute key
                store.[key] <- { Value = value; CachedAt = now }
                return value
        }

    member _.GetOrRefresh key (compute: string -> 'T) =
        let now = DateTimeOffset.UtcNow

        match store.TryGetValue(key) with
        | true, entry when now - entry.CachedAt < ttl -> entry.Value
        | _ ->
            let value = compute key
            store.[key] <- { Value = value; CachedAt = now }
            value

    member _.GetCachedAt key =
        match store.TryGetValue(key) with
        | true, entry -> Some entry.CachedAt
        | _ -> None

    member _.GetOldestCachedAt() =
        store.Values
        |> Seq.map (fun entry -> entry.CachedAt)
        |> Seq.sortBy id
        |> Seq.tryHead

    member _.Invalidate (key: string) =
        store.TryRemove(key) |> ignore
