module Server.PersistentStore

/// A disk-backed `Map<'K,'V>` guarded by a single agent, shared by the runtime-state stores
/// (`MergedPrStore`, `CanvasDocOwnership`). It owns only the concurrency + change-detection shell:
/// `Update` applies a function to one key atomically, `Get` reads a key, `Load` seeds the map at
/// startup, and `Flush` awaits persistence of dirty state. Serialization is injected so the store
/// stays format-agnostic.
type Store<'K, 'V when 'K: comparison and 'V: equality> =
    { Get: 'K -> Async<'V option>
      Update: 'K -> ('V option -> 'V option) -> unit
      Load: unit -> unit
      Flush: unit -> Async<Result<unit, string>> }

type private Msg<'K, 'V when 'K: comparison> =
    | Get of key: 'K * AsyncReplyChannel<'V option>
    | Update of key: 'K * change: ('V option -> 'V option)
    | Load of Map<'K, 'V>
    | Retry of token: int
    | Flush of AsyncReplyChannel<Result<unit, string>>

type private Retry =
    { Token: int }

type private State<'K, 'V when 'K: comparison> =
    { Desired: Map<'K, 'V>
      Durable: Map<'K, 'V>
      Retry: Retry option
      FailureCount: int
      NextRetryToken: int }

let private retryDelay failureCount =
    50 * pown 2 (min 4 (max 0 (failureCount - 1)))

let create<'K, 'V when 'K: comparison and 'V: equality>
    (logTag: string)
    (persist: Map<'K, 'V> -> Async<Result<unit, string>>)
    (loadState: unit -> Map<'K, 'V>)
    : Store<'K, 'V> =
    let agent =
        MailboxProcessor.Start(fun inbox ->
            let clearRetry state =
                { state with
                    Retry = None
                    FailureCount = 0
                    NextRetryToken = state.NextRetryToken + 1 }

            let scheduleRetry state =
                match state.Retry with
                | Some _ -> state
                | None ->
                    let retry = { Token = state.NextRetryToken }

                    async {
                        do! Async.Sleep(retryDelay state.FailureCount)
                        inbox.Post(Retry retry.Token)
                    }
                    |> Async.Start

                    { state with
                        Retry = Some retry
                        NextRetryToken = state.NextRetryToken + 1 }

            let persistDirty state =
                async {
                    let! result = persist state.Desired

                    return
                        match result with
                        | Ok() ->
                            { clearRetry state with Durable = state.Desired }, result
                        | Error _ ->
                            let nextState =
                                { state with FailureCount = state.FailureCount + 1 }
                                |> scheduleRetry

                            nextState, result
                }

            let clean desired state =
                { clearRetry state with
                    Desired = desired
                    Durable = desired }

            let rec loop (state: State<'K, 'V>) =
                async {
                    match! inbox.Receive() with
                    | Get(key, reply) ->
                        reply.Reply(Map.tryFind key state.Desired)
                        return! loop state

                    | Update(key, change) ->
                        let next =
                            match change (Map.tryFind key state.Desired) with
                            | Some value -> Map.add key value state.Desired
                            | None -> Map.remove key state.Desired

                        if next = state.Durable then
                            return! loop (clean next state)
                        elif next = state.Desired then
                            return! loop (scheduleRetry state)
                        else
                            let! nextState, _ = persistDirty { state with Desired = next }
                            return! loop nextState

                    | Load loaded ->
                        Log.log logTag $"Loaded {Map.count loaded} entries"
                        return! loop (clean loaded state)

                    | Retry token ->
                        match state.Retry with
                        | Some retry when retry.Token = token ->
                            let! nextState, _ = persistDirty { state with Retry = None }
                            return! loop nextState
                        | _ ->
                            return! loop state

                    | Flush reply ->
                        if state.Desired = state.Durable then
                            reply.Reply(Ok())
                            return! loop state
                        else
                            let! nextState, result = persistDirty state
                            reply.Reply result
                            return! loop nextState
                }

            loop
                { Desired = Map.empty
                  Durable = Map.empty
                  Retry = None
                  FailureCount = 0
                  NextRetryToken = 0 })

    { Get = fun key -> agent.PostAndAsyncReply(fun reply -> Get(key, reply))
      Update = fun key change -> agent.Post(Update(key, change))
      Load = fun () -> agent.Post(Load(loadState ()))
      Flush = fun () -> agent.PostAndAsyncReply Flush }
