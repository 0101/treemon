module Server.CanvasDocOwnership

open System
open System.IO
open System.Text.Json
open Shared
open Server.SessionActivity

let private normalizePath = Server.PathUtils.normalizePath

let private defaultFilePath = Path.Combine("data", "canvas-owners.json")

type internal Targets = Map<string, Map<string, SessionId>>

[<RequireQualifiedAccess>]
type PersistenceFailure =
    | SaveFailed

type private Assignment =
    | Claim
    | FillUnowned

type private Msg =
    | Assign of Assignment * worktreeKey: string * filename: string * sessionId: SessionId * AsyncReplyChannel<Result<unit, PersistenceFailure>>
    | GetOwner of worktreeKey: string * filename: string * AsyncReplyChannel<SessionId option>
    | GetAll of worktreeKey: string * AsyncReplyChannel<Map<string, SessionId>>
    | RemoveView of worktreeKey: string * filename: string * AsyncReplyChannel<Result<unit, PersistenceFailure>>
    | RemoveWorktree of worktreeKey: string * AsyncReplyChannel<Result<unit, PersistenceFailure>>
    | Prune of knownWorktrees: Set<string> * AsyncReplyChannel<Result<unit, PersistenceFailure>>
    | Replace of targets: Targets * AsyncReplyChannel<unit>

let private ownerFor worktreeKey filename targets =
    targets
    |> Map.tryFind worktreeKey
    |> Option.bind (Map.tryFind filename)

let private addTarget worktreeKey filename sessionId targets =
    let views =
        targets
        |> Map.tryFind worktreeKey
        |> Option.defaultValue Map.empty
        |> Map.add filename sessionId

    targets |> Map.add worktreeKey views

let private removeTarget worktreeKey filename targets =
    match targets |> Map.tryFind worktreeKey with
    | None -> targets
    | Some views ->
        let remaining = views |> Map.remove filename
        if Map.isEmpty remaining then targets |> Map.remove worktreeKey
        else targets |> Map.add worktreeKey remaining

let private persist (filePath: string) (targets: Targets) =
    JsonStore.tryPersist "CanvasDocOwnership" filePath (fun writer ->
        writer.WriteStartObject()
        targets
        |> Map.iter (fun worktreeKey views ->
            writer.WritePropertyName(worktreeKey)
            writer.WriteStartObject()
            views |> Map.iter (fun filename sessionId -> writer.WriteString(filename, SessionId.value sessionId))
            writer.WriteEndObject())
        writer.WriteEndObject())

let private readTargets filePath =
    try
        if not (File.Exists filePath) then
            Ok Map.empty
        else
            use doc = JsonDocument.Parse(File.ReadAllText filePath)

            let targets, invalidOwnerCount =
                doc.RootElement.EnumerateObject()
                |> Seq.fold (fun (targets, invalidOwnerCount) worktreeProp ->
                    let views, invalidInWorktree =
                        worktreeProp.Value.EnumerateObject()
                        |> Seq.fold (fun (views, invalidCount) viewProp ->
                            let parsed =
                                if viewProp.Value.ValueKind = JsonValueKind.String then
                                    viewProp.Value.GetString()
                                    |> Option.ofObj
                                    |> Option.bind (fun value ->
                                        match SessionId.create value with
                                        | Ok sessionId -> Some sessionId
                                        | Error _ -> None)
                                else
                                    None

                            match parsed with
                            | Some sessionId -> views |> Map.add viewProp.Name sessionId, invalidCount
                            | None -> views, invalidCount + 1
                        ) (Map.empty, 0)

                    targets |> Map.add (normalizePath worktreeProp.Name) views,
                    invalidOwnerCount + invalidInWorktree
                ) (Map.empty, 0)

            if invalidOwnerCount > 0 then
                Log.log "CanvasDocOwnership" $"Ignored {invalidOwnerCount} invalid persisted canvas owner(s)"

            targets
            |> Ok
    with error ->
        Log.logException "CanvasDocOwnership" $"Failed to load {filePath}" error
        Error error.Message

type internal OwnershipStore internal (filePath: string, initialTargets: Targets) =
    let commit targets proposed (reply: AsyncReplyChannel<Result<unit, PersistenceFailure>>) =
        async {
            let! saved =
                if proposed = targets then async.Return(Ok())
                else persist filePath proposed

            match saved with
            | Ok() ->
                reply.Reply(Ok())
                return proposed
            | Error _ ->
                reply.Reply(Error PersistenceFailure.SaveFailed)
                return targets
        }

    let agent =
        MailboxProcessor.Start(fun inbox ->
            let rec loop targets =
                async {
                    let! msg = inbox.Receive()

                    match msg with
                    | Assign(assignment, worktreeKey, filename, sessionId, reply) ->
                        let proposed =
                            match assignment, ownerFor worktreeKey filename targets with
                            | FillUnowned, Some _ -> targets
                            | Claim, _
                            | FillUnowned, None -> targets |> addTarget worktreeKey filename sessionId
                        let! committed = commit targets proposed reply
                        return! loop committed

                    | GetOwner(worktreeKey, filename, reply) ->
                        targets
                        |> ownerFor worktreeKey filename
                        |> reply.Reply

                        return! loop targets

                    | GetAll(worktreeKey, reply) ->
                        targets
                        |> Map.tryFind worktreeKey
                        |> Option.defaultValue Map.empty
                        |> reply.Reply

                        return! loop targets

                    | RemoveView(worktreeKey, filename, reply) ->
                        let! committed = commit targets (removeTarget worktreeKey filename targets) reply
                        return! loop committed

                    | RemoveWorktree(worktreeKey, reply) ->
                        let! committed = commit targets (Map.remove worktreeKey targets) reply
                        return! loop committed

                    | Prune(knownWorktrees, reply) ->
                        // Worktree removal is handled by the known-worktree filter; the file check
                        // reclaims entries for individual documents that were deleted, which is the
                        // only path that releases a per-document entry.
                        let! survivingKeys =
                            targets
                            |> Map.toList
                            |> List.filter (fun (worktreeKey, _) ->
                                knownWorktrees |> Set.contains worktreeKey)
                            |> List.collect (fun (worktreeKey, views) ->
                                views |> Map.keys |> List.ofSeq |> List.map (fun f -> worktreeKey, f))
                            |> List.map (fun ((worktreeKey, filename) as key) ->
                                async {
                                    match Server.PathUtils.validateCanvasPath worktreeKey filename with
                                    | Ok path when File.Exists path -> return Some key
                                    | _ -> return None
                                })
                            |> Async.Parallel

                        let surviving = survivingKeys |> Array.choose id |> Set.ofArray

                        let targets' =
                            targets
                            |> Map.toList
                            |> List.choose (fun (worktreeKey, views) ->
                                let kept =
                                    views
                                    |> Map.filter (fun filename _ ->
                                        surviving |> Set.contains (worktreeKey, filename))

                                if Map.isEmpty kept then None else Some(worktreeKey, kept))
                            |> Map.ofList

                        let! committed = commit targets targets' reply
                        return! loop committed

                    | Replace(targets', reply) ->
                        reply.Reply()
                        return! loop targets'
                }

            loop initialTargets)

    member _.Assign(worktreePath: string, filename: string, sessionId: SessionId) =
        agent.PostAndAsyncReply(fun reply ->
            Assign(Claim, normalizePath worktreePath, filename, sessionId, reply))

    member _.Attribute(worktreePath: string, filename: string, sessionId: SessionId) =
        agent.PostAndAsyncReply(fun reply ->
            Assign(FillUnowned, normalizePath worktreePath, filename, sessionId, reply))

    member _.GetOwner(worktreePath: string, filename: string) =
        agent.PostAndAsyncReply(fun reply ->
            GetOwner(normalizePath worktreePath, filename, reply))

    member _.GetAll(worktreePath: string) =
        agent.PostAndAsyncReply(fun reply -> GetAll(normalizePath worktreePath, reply))

    member _.RemoveView(worktreePath: string, filename: string) =
        agent.PostAndAsyncReply(fun reply ->
            RemoveView(normalizePath worktreePath, filename, reply))

    member _.RemoveWorktree(worktreePath: string) =
        agent.PostAndAsyncReply(fun reply -> RemoveWorktree(normalizePath worktreePath, reply))

    member _.Prune(knownWorktrees: Set<string>) =
        let normalized = knownWorktrees |> Set.map normalizePath
        agent.PostAndAsyncReply(fun reply -> Prune(normalized, reply))

    member _.Load() =
        async {
            match readTargets filePath with
            | Error _ -> ()
            | Ok loaded ->
                do! agent.PostAndAsyncReply(fun reply -> Replace(loaded, reply))
                Log.log "CanvasDocOwnership" $"Loaded targets for {Map.count loaded} worktree(s)"
        }

let internal createStore filePath =
    let initialTargets =
        readTargets filePath
        |> Result.defaultValue Map.empty

    OwnershipStore(filePath, initialTargets)

let private defaultStore = OwnershipStore(defaultFilePath, Map.empty)

let load () =
    defaultStore.Load()
    |> Async.RunSynchronously

let assign worktreePath filename sessionId =
    defaultStore.Assign(worktreePath, filename, sessionId)

let attribute worktreePath filename sessionId =
    defaultStore.Attribute(worktreePath, filename, sessionId)

let getOwner worktreePath filename =
    defaultStore.GetOwner(worktreePath, filename)

let getAll worktreePath =
    defaultStore.GetAll(worktreePath)

let removeView worktreePath filename =
    defaultStore.RemoveView(worktreePath, filename)

let removeWorktree worktreePath =
    defaultStore.RemoveWorktree(worktreePath)

let prune knownWorktrees =
    defaultStore.Prune(knownWorktrees)
