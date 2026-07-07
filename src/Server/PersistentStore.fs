module Server.PersistentStore

/// A disk-backed `Map<'K,'V>` guarded by a single agent, shared by the runtime-state stores
/// (`MergedPrStore`, `CanvasDocOwnership`). It owns only the concurrency + change-detection shell:
/// `Update` applies a function to one key atomically and persists via the injected `persist` ONLY
/// when the map actually changes (a `None` result removes the key); `Get` reads a key; `Load` seeds
/// the map at startup. Serialization is injected so the store stays format-agnostic.
type Store<'K, 'V when 'K: comparison and 'V: equality> =
    { Get: 'K -> Async<'V option>
      Update: 'K -> ('V option -> 'V option) -> unit
      Load: unit -> unit }

type private Msg<'K, 'V when 'K: comparison> =
    | Get of key: 'K * AsyncReplyChannel<'V option>
    | Update of key: 'K * change: ('V option -> 'V option)
    | Load of Map<'K, 'V>

let create<'K, 'V when 'K: comparison and 'V: equality>
    (logTag: string)
    (persist: Map<'K, 'V> -> Async<unit>)
    (loadState: unit -> Map<'K, 'V>)
    : Store<'K, 'V> =
    let agent =
        MailboxProcessor.Start(fun inbox ->
            let rec loop (state: Map<'K, 'V>) =
                async {
                    match! inbox.Receive() with
                    | Get(key, reply) ->
                        reply.Reply(Map.tryFind key state)
                        return! loop state

                    | Update(key, change) ->
                        let next =
                            match change (Map.tryFind key state) with
                            | Some value -> Map.add key value state
                            | None -> Map.remove key state

                        if next = state then
                            return! loop state
                        else
                            do! persist next
                            return! loop next

                    | Load loaded ->
                        Log.log logTag $"Loaded {Map.count loaded} entries"
                        return! loop loaded
                }

            loop Map.empty)

    { Get = fun key -> agent.PostAndAsyncReply(fun reply -> Get(key, reply))
      Update = fun key change -> agent.Post(Update(key, change))
      Load = fun () -> agent.Post(Load(loadState ())) }
