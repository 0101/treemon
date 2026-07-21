module Server.CanvasInteractionOwnership

open System
open System.IO
open System.Text
open System.Text.Json

let private normalizePath = Server.PathUtils.normalizePath
let private normalizeFilename (filename: string) = filename.ToLowerInvariant()

let private defaultFilePath = Path.Combine("data", "canvas-interaction-owners.json")

type private OwnershipState =
    { Owners: Map<string, Map<string, string>>
      Pending: Map<string, Set<string>> }

type private Msg =
    | Assign of worktreeKey: string * filename: string * sessionId: string * AsyncReplyChannel<unit>
    | BeginClaim of worktreeKey: string * filename: string * AsyncReplyChannel<string option>
    | CancelClaim of worktreeKey: string * filename: string * AsyncReplyChannel<unit>
    | ClaimPending of worktreeKey: string * sessionId: string * AsyncReplyChannel<string list>
    | GetOwner of worktreeKey: string * filename: string * AsyncReplyChannel<string option>
    | RemoveView of worktreeKey: string * filename: string * AsyncReplyChannel<unit>
    | RemoveWorktree of worktreeKey: string * AsyncReplyChannel<unit>
    | Prune of knownWorktrees: Set<string> * AsyncReplyChannel<unit>

let private emptyState =
    { Owners = Map.empty
      Pending = Map.empty }

let private ownerFor worktreeKey filename owners =
    owners
    |> Map.tryFind worktreeKey
    |> Option.bind (Map.tryFind filename)

let private addOwner worktreeKey filename sessionId owners =
    let views =
        owners
        |> Map.tryFind worktreeKey
        |> Option.defaultValue Map.empty
        |> Map.add filename sessionId

    owners |> Map.add worktreeKey views

let private removeOwner worktreeKey filename owners =
    match owners |> Map.tryFind worktreeKey with
    | None -> owners
    | Some views ->
        let remaining = views |> Map.remove filename
        if Map.isEmpty remaining then owners |> Map.remove worktreeKey
        else owners |> Map.add worktreeKey remaining

let private removePendingView worktreeKey filename pending =
    match pending |> Map.tryFind worktreeKey with
    | None -> pending
    | Some views ->
        let remaining = views |> Set.remove filename
        if Set.isEmpty remaining then pending |> Map.remove worktreeKey
        else pending |> Map.add worktreeKey remaining

let private viewExists worktreeKey filename =
    match Server.PathUtils.validateCanvasPath worktreeKey filename with
    | Ok path -> File.Exists path
    | Error _ -> false

let private pruneViews knownWorktrees viewsByWorktree =
    viewsByWorktree
    |> Map.toSeq
    |> Seq.choose (fun (worktreeKey, views) ->
        if knownWorktrees |> Set.contains worktreeKey then
            let existing = views |> Map.filter (fun filename _ -> viewExists worktreeKey filename)
            if Map.isEmpty existing then None else Some(worktreeKey, existing)
        else
            None)
    |> Map.ofSeq

let private prunePending knownWorktrees pending =
    pending
    |> Map.toSeq
    |> Seq.choose (fun (worktreeKey, views) ->
        if knownWorktrees |> Set.contains worktreeKey then
            let existing = views |> Set.filter (viewExists worktreeKey)
            if Set.isEmpty existing then None else Some(worktreeKey, existing)
        else
            None)
    |> Map.ofSeq

let private persist (filePath: string) (owners: Map<string, Map<string, string>>) =
    async {
        try
            let dir = Path.GetDirectoryName(filePath)
            if not (String.IsNullOrEmpty dir) then Directory.CreateDirectory(dir) |> ignore

            let options = JsonWriterOptions(Indented = true)
            use stream = new MemoryStream()
            use writer = new Utf8JsonWriter(stream, options)
            writer.WriteStartObject()

            owners
            |> Map.iter (fun worktreeKey views ->
                writer.WritePropertyName(worktreeKey)
                writer.WriteStartObject()
                views |> Map.iter (fun filename sessionId -> writer.WriteString(filename, sessionId))
                writer.WriteEndObject())

            writer.WriteEndObject()
            writer.Flush()

            let tempPath = filePath + ".tmp"
            let json = Encoding.UTF8.GetString(stream.ToArray())
            do! File.WriteAllTextAsync(tempPath, json) |> Async.AwaitTask
            File.Move(tempPath, filePath, overwrite = true)
        with ex ->
            Log.log "CanvasInteractionOwnership" $"Failed to persist: {ex.Message}"
    }

let private readOwners filePath =
    try
        if not (File.Exists filePath) then
            Map.empty
        else
            use doc = JsonDocument.Parse(File.ReadAllText filePath)

            doc.RootElement.EnumerateObject()
            |> Seq.fold (fun owners worktreeProp ->
                let views =
                    worktreeProp.Value.EnumerateObject()
                    |> Seq.choose (fun viewProp ->
                        viewProp.Value.GetString()
                        |> Option.ofObj
                        |> Option.map (fun sessionId -> normalizeFilename viewProp.Name, sessionId))
                    |> Map.ofSeq

                owners |> Map.add (normalizePath worktreeProp.Name) views
            ) Map.empty
    with ex ->
        Log.log "CanvasInteractionOwnership" $"Failed to load: {ex.Message}"
        Map.empty

let private createAgent filePath owners =
    MailboxProcessor.Start(fun inbox ->
        let rec loop state =
            async {
                let! msg = inbox.Receive()

                match msg with
                | Assign(worktreeKey, filename, sessionId, reply) ->
                    let state' =
                        { Owners = state.Owners |> addOwner worktreeKey filename sessionId
                          Pending = state.Pending |> removePendingView worktreeKey filename }

                    do! persist filePath state'.Owners
                    reply.Reply()
                    return! loop state'

                | BeginClaim(worktreeKey, filename, reply) ->
                    match ownerFor worktreeKey filename state.Owners with
                    | Some owner ->
                        reply.Reply(Some owner)
                        return! loop state
                    | None ->
                        let views =
                            state.Pending
                            |> Map.tryFind worktreeKey
                            |> Option.defaultValue Set.empty
                            |> Set.add filename

                        reply.Reply(None)
                        return! loop { state with Pending = state.Pending |> Map.add worktreeKey views }

                | CancelClaim(worktreeKey, filename, reply) ->
                    reply.Reply()
                    return! loop { state with Pending = state.Pending |> removePendingView worktreeKey filename }

                | ClaimPending(worktreeKey, sessionId, reply) ->
                    let pendingViews =
                        state.Pending
                        |> Map.tryFind worktreeKey
                        |> Option.defaultValue Set.empty

                    let claimed =
                        pendingViews
                        |> Set.filter (fun filename -> ownerFor worktreeKey filename state.Owners |> Option.isNone)
                        |> Set.toList

                    let owners =
                        claimed
                        |> List.fold (fun current filename -> current |> addOwner worktreeKey filename sessionId) state.Owners

                    let state' =
                        { Owners = owners
                          Pending = state.Pending |> Map.remove worktreeKey }

                    if not (List.isEmpty claimed) then do! persist filePath owners
                    reply.Reply(claimed)
                    return! loop state'

                | GetOwner(worktreeKey, filename, reply) ->
                    state.Owners
                    |> ownerFor worktreeKey filename
                    |> reply.Reply

                    return! loop state

                | RemoveView(worktreeKey, filename, reply) ->
                    let owners = state.Owners |> removeOwner worktreeKey filename
                    let state' =
                        { Owners = owners
                          Pending = state.Pending |> removePendingView worktreeKey filename }

                    if owners <> state.Owners then do! persist filePath owners
                    reply.Reply()
                    return! loop state'

                | RemoveWorktree(worktreeKey, reply) ->
                    let owners = state.Owners |> Map.remove worktreeKey
                    let state' =
                        { Owners = owners
                          Pending = state.Pending |> Map.remove worktreeKey }

                    if owners <> state.Owners then do! persist filePath owners
                    reply.Reply()
                    return! loop state'

                | Prune(knownWorktrees, reply) ->
                    let owners = state.Owners |> pruneViews knownWorktrees
                    let state' =
                        { Owners = owners
                          Pending = state.Pending |> prunePending knownWorktrees }

                    if owners <> state.Owners then do! persist filePath owners
                    reply.Reply()
                    return! loop state'
            }

        loop
            { emptyState with
                Owners = owners })

type internal OwnershipStore(filePath: string) =
    let agent = createAgent filePath (readOwners filePath)

    member _.Assign(worktreePath: string, filename: string, sessionId: string) =
        agent.PostAndAsyncReply(fun reply ->
            Assign(normalizePath worktreePath, normalizeFilename filename, sessionId, reply))

    member _.BeginClaim(worktreePath: string, filename: string) =
        agent.PostAndAsyncReply(fun reply ->
            BeginClaim(normalizePath worktreePath, normalizeFilename filename, reply))

    member _.ClaimPending(worktreePath: string, sessionId: string) =
        agent.PostAndReply(fun reply ->
            ClaimPending(normalizePath worktreePath, sessionId, reply))

    member _.CancelClaim(worktreePath: string, filename: string) =
        agent.PostAndAsyncReply(fun reply ->
            CancelClaim(normalizePath worktreePath, normalizeFilename filename, reply))

    member _.GetOwner(worktreePath: string, filename: string) =
        agent.PostAndAsyncReply(fun reply ->
            GetOwner(normalizePath worktreePath, normalizeFilename filename, reply))

    member _.GetOwnerSync(worktreePath: string, filename: string) =
        agent.PostAndReply(fun reply ->
            GetOwner(normalizePath worktreePath, normalizeFilename filename, reply))

    member _.RemoveView(worktreePath: string, filename: string) =
        agent.PostAndAsyncReply(fun reply ->
            RemoveView(normalizePath worktreePath, normalizeFilename filename, reply))

    member _.RemoveWorktree(worktreePath: string) =
        agent.PostAndAsyncReply(fun reply ->
            RemoveWorktree(normalizePath worktreePath, reply))

    member _.Prune(knownWorktrees: Set<string>) =
        let normalized = knownWorktrees |> Set.map normalizePath
        agent.PostAndAsyncReply(fun reply -> Prune(normalized, reply))

let internal createStore filePath = OwnershipStore(filePath)

let private defaultStore = lazy (OwnershipStore(defaultFilePath))

let load () =
    defaultStore.Force() |> ignore

let assign worktreePath filename sessionId =
    defaultStore.Value.Assign(worktreePath, filename, sessionId)

let beginClaim worktreePath filename =
    defaultStore.Value.BeginClaim(worktreePath, filename)

let claimPending worktreePath sessionId =
    defaultStore.Value.ClaimPending(worktreePath, sessionId)

let cancelClaim worktreePath filename =
    defaultStore.Value.CancelClaim(worktreePath, filename)

let getOwner worktreePath filename =
    defaultStore.Value.GetOwner(worktreePath, filename)

let internal getOwnerSync worktreePath filename =
    defaultStore.Value.GetOwnerSync(worktreePath, filename)

let removeView worktreePath filename =
    defaultStore.Value.RemoveView(worktreePath, filename)

let removeWorktree worktreePath =
    defaultStore.Value.RemoveWorktree(worktreePath)

let prune knownWorktrees =
    defaultStore.Value.Prune(knownWorktrees)
