module Server.CanvasDocOwnership

open System
open System.IO
open System.Text
open System.Text.Json
open Shared

let private normalizePath = Server.PathUtils.normalizePath

let private defaultFilePath = Path.Combine("data", "canvas-owners.json")
let private legacyFilePath = Path.Combine("data", "canvas-interaction-owners.json")

type internal Targets = Map<string, Map<string, string>>

type private Msg =
    | Assign of worktreeKey: string * filename: string * sessionId: string * AsyncReplyChannel<unit> option
    | AssignIfCurrentOwner of
        worktreeKey: string *
        filename: string *
        expectedOwner: string option *
        sessionId: string *
        AsyncReplyChannel<bool>
    | GetOwner of worktreeKey: string * filename: string * AsyncReplyChannel<string option>
    | GetAll of worktreeKey: string * AsyncReplyChannel<Map<string, string>>
    | RemoveView of worktreeKey: string * filename: string * AsyncReplyChannel<unit>
    | RemoveWorktree of worktreeKey: string * AsyncReplyChannel<unit>
    | Prune of knownWorktrees: Set<string> * AsyncReplyChannel<unit>
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

let private trackedTargetKeys knownWorktrees targets =
    targets
    |> Map.toSeq
    |> Seq.filter (fun (worktreeKey, _) -> knownWorktrees |> Set.contains worktreeKey)
    |> Seq.collect (fun (worktreeKey, views) ->
        views
        |> Map.keys
        |> Seq.map (fun filename -> worktreeKey, filename))
    |> Set.ofSeq

let private keepTargets existingTargetKeys targets =
    targets
    |> Map.toSeq
    |> Seq.choose (fun (worktreeKey, views) ->
        let existing =
            views
            |> Map.filter (fun filename _ ->
                existingTargetKeys |> Set.contains (worktreeKey, filename))

        if Map.isEmpty existing then None else Some(worktreeKey, existing))
    |> Map.ofSeq

let private persist (filePath: string) (targets: Targets) =
    async {
        try
            let dir = Path.GetDirectoryName(filePath)
            if not (String.IsNullOrEmpty dir) then Directory.CreateDirectory(dir) |> ignore

            let options = JsonWriterOptions(Indented = true)
            use stream = new MemoryStream()
            use writer = new Utf8JsonWriter(stream, options)
            writer.WriteStartObject()

            targets
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
            return true
        with ex ->
            Log.log "CanvasDocOwnership" $"Failed to persist: {ex.Message}"
            return false
    }

let private readTargets filePath =
    try
        if not (File.Exists filePath) then
            Ok Map.empty
        else
            use doc = JsonDocument.Parse(File.ReadAllText filePath)

            doc.RootElement.EnumerateObject()
            |> Seq.fold (fun targets worktreeProp ->
                let views =
                    worktreeProp.Value.EnumerateObject()
                    |> Seq.choose (fun viewProp ->
                        viewProp.Value.GetString()
                        |> Option.ofObj
                        |> Option.map (fun sessionId -> viewProp.Name, sessionId))
                    |> Map.ofSeq

                targets |> Map.add (normalizePath worktreeProp.Name) views
            ) Map.empty
            |> Ok
    with ex ->
        Log.log "CanvasDocOwnership" $"Failed to load {filePath}: {ex.Message}"
        Error ex.Message

let private mergeLegacySystemViews unified legacy =
    legacy
    |> Map.fold (fun targets worktreeKey legacyViews ->
        let existing =
            targets
            |> Map.tryFind worktreeKey
            |> Option.defaultValue Map.empty

        let merged =
            legacyViews
            |> Map.fold (fun views filename sessionId ->
                let alreadyOwned =
                    views
                    |> Map.exists (fun existingFilename _ ->
                        String.Equals(existingFilename, filename, StringComparison.OrdinalIgnoreCase))

                match CanvasDocKinds.classify filename, alreadyOwned with
                | SystemView, false -> views |> Map.add filename sessionId
                | _ -> views
            ) existing

        if merged = existing then targets else targets |> Map.add worktreeKey merged
    ) unified

let private migrateLegacy filePath legacyPath unified =
    async {
        if not (File.Exists legacyPath) then
            return unified
        else
            match readTargets legacyPath with
            | Error _ -> return unified
            | Ok legacy ->
                let merged = mergeLegacySystemViews unified legacy
                let! migrationPersisted =
                    if merged = unified then async { return true }
                    else persist filePath merged

                if migrationPersisted then
                    try
                        File.Delete legacyPath
                    with ex ->
                        Log.log "CanvasDocOwnership" $"Failed to remove migrated legacy store: {ex.Message}"

                    return merged
                else
                    return unified
    }

type internal OwnershipStore internal (filePath: string, initialTargets: Targets) =
    let agent =
        MailboxProcessor.Start(fun inbox ->
            let rec loop targets =
                async {
                    let! msg = inbox.Receive()

                    match msg with
                    | Assign(worktreeKey, filename, sessionId, reply) ->
                        let targets' = targets |> addTarget worktreeKey filename sessionId
                        if targets' <> targets then
                            do! persist filePath targets' |> Async.Ignore
                        reply |> Option.iter _.Reply()
                        return! loop targets'

                    | AssignIfCurrentOwner(worktreeKey, filename, expectedOwner, sessionId, reply) ->
                        if ownerFor worktreeKey filename targets = expectedOwner then
                            let targets' = targets |> addTarget worktreeKey filename sessionId
                            if targets' <> targets then
                                do! persist filePath targets' |> Async.Ignore
                            reply.Reply(true)
                            return! loop targets'
                        else
                            reply.Reply(false)
                            return! loop targets

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
                        let targets' = targets |> removeTarget worktreeKey filename
                        if targets' <> targets then
                            do! persist filePath targets' |> Async.Ignore
                        reply.Reply()
                        return! loop targets'

                    | RemoveWorktree(worktreeKey, reply) ->
                        let targets' = targets |> Map.remove worktreeKey
                        if targets' <> targets then
                            do! persist filePath targets' |> Async.Ignore
                        reply.Reply()
                        return! loop targets'

                    | Prune(knownWorktrees, reply) ->
                        let! existingKeys =
                            targets
                            |> trackedTargetKeys knownWorktrees
                            |> Set.toList
                            |> List.map (fun ((worktreeKey, filename) as key) ->
                                async {
                                    match Server.PathUtils.validateCanvasPath worktreeKey filename with
                                    | Ok path when File.Exists path -> return Some key
                                    | _ -> return None
                                })
                            |> Async.Parallel

                        let targets' =
                            targets
                            |> keepTargets (existingKeys |> Array.choose id |> Set.ofArray)

                        if targets' <> targets then
                            do! persist filePath targets' |> Async.Ignore
                        reply.Reply()
                        return! loop targets'

                    | Replace(targets', reply) ->
                        reply.Reply()
                        return! loop targets'
                }

            loop initialTargets)

    member _.Attribute(worktreePath: string, filename: string, sessionId: string) =
        agent.Post(Assign(normalizePath worktreePath, filename, sessionId, None))

    member _.Assign(worktreePath: string, filename: string, sessionId: string) =
        agent.PostAndAsyncReply(fun reply ->
            Assign(normalizePath worktreePath, filename, sessionId, Some reply))

    member _.AssignIfCurrentOwner(
        worktreePath: string,
        filename: string,
        expectedOwner: string option,
        sessionId: string
    ) =
        agent.PostAndAsyncReply(fun reply ->
            AssignIfCurrentOwner(
                normalizePath worktreePath,
                filename,
                expectedOwner,
                sessionId,
                reply))

    member _.GetOwner(worktreePath: string, filename: string) =
        agent.PostAndAsyncReply(fun reply ->
            GetOwner(normalizePath worktreePath, filename, reply))

    member _.GetOwnerSync(worktreePath: string, filename: string) =
        agent.PostAndReply(fun reply ->
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

    member _.Load(legacyPath: string option) =
        async {
            match readTargets filePath with
            | Error _ -> ()
            | Ok unified ->
                let! loaded =
                    match legacyPath with
                    | Some path -> migrateLegacy filePath path unified
                    | None -> async { return unified }

                do! agent.PostAndAsyncReply(fun reply -> Replace(loaded, reply))
                Log.log "CanvasDocOwnership" $"Loaded targets for {Map.count loaded} worktree(s)"
        }

let internal createStore filePath =
    let initialTargets =
        readTargets filePath
        |> Result.defaultValue Map.empty

    OwnershipStore(filePath, initialTargets)

let internal loadStore filePath legacyPath =
    async {
        let store = OwnershipStore(filePath, Map.empty)
        do! store.Load(Some legacyPath)
        return store
    }

let private defaultStore = OwnershipStore(defaultFilePath, Map.empty)

let load () =
    defaultStore.Load(Some legacyFilePath)
    |> Async.RunSynchronously

let attribute worktreePath filename sessionId =
    defaultStore.Attribute(worktreePath, filename, sessionId)

let assign worktreePath filename sessionId =
    defaultStore.Assign(worktreePath, filename, sessionId)

let internal assignIfCurrentOwner worktreePath filename expectedOwner sessionId =
    defaultStore.AssignIfCurrentOwner(
        worktreePath,
        filename,
        expectedOwner,
        sessionId)

let getOwner worktreePath filename =
    defaultStore.GetOwner(worktreePath, filename)

let internal getOwnerSync worktreePath filename =
    defaultStore.GetOwnerSync(worktreePath, filename)

let getAll worktreePath =
    defaultStore.GetAll(worktreePath)

let removeView worktreePath filename =
    defaultStore.RemoveView(worktreePath, filename)

let removeWorktree worktreePath =
    defaultStore.RemoveWorktree(worktreePath)

let prune knownWorktrees =
    defaultStore.Prune(knownWorktrees)
