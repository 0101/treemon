module Server.CanvasDocOwnership

open System.IO
open System.Text.Json

let private normalizePath = Server.PathUtils.normalizePath

let private filePath = Path.Combine("data", "canvas-owners.json")

type private Owner =
    { SessionId: string
      Version: int64 }

type private Msg =
    | Attribute of key: string * filename: string * sessionId: string
    | AttributeIfNewer of key: string * filename: string * sessionId: string * version: int64 * AsyncReplyChannel<bool>
    | RemoveIfNewer of key: string * filename: string * sessionId: string * version: int64 * AsyncReplyChannel<bool>
    | GetOwner of key: string * filename: string * AsyncReplyChannel<string option>
    | GetAll of key: string * AsyncReplyChannel<Map<string, string>>
    | Load of state: Map<string, Map<string, Owner>>

let private currentVersion () =
    (System.DateTime.UtcNow.Ticks - System.DateTime.UnixEpoch.Ticks) / 10L

let private persistImpl (state: Map<string, Map<string, Owner>>) =
    async {
        try
            let dir = Path.GetDirectoryName(filePath)
            if not (Directory.Exists(dir)) then Directory.CreateDirectory(dir) |> ignore

            let options = JsonWriterOptions(Indented = true)
            use stream = new MemoryStream()
            use writer = new Utf8JsonWriter(stream, options)
            writer.WriteStartObject()

            state
            |> Map.iter (fun worktreeKey docs ->
                writer.WritePropertyName(worktreeKey)
                writer.WriteStartObject()
                docs |> Map.iter (fun filename owner ->
                    writer.WritePropertyName(filename)
                    writer.WriteStartObject()
                    writer.WriteString("sessionId", owner.SessionId)
                    writer.WriteNumber("version", owner.Version)
                    writer.WriteEndObject())
                writer.WriteEndObject())

            writer.WriteEndObject()
            writer.Flush()

            let json = System.Text.Encoding.UTF8.GetString(stream.ToArray())
            let tempPath = filePath + ".tmp"
            do! File.WriteAllTextAsync(tempPath, json) |> Async.AwaitTask
            File.Move(tempPath, filePath, overwrite = true)
        with ex ->
            Log.log "CanvasDocOwnership" $"Failed to persist: {ex.Message}"
    }

let private agent =
    MailboxProcessor.Start(fun inbox ->
        let rec loop (state: Map<string, Map<string, Owner>>) =
            async {
                let! msg = inbox.Receive()

                match msg with
                | Attribute(key, filename, sessionId) ->
                    let currentDocs =
                        state
                        |> Map.tryFind key
                        |> Option.defaultValue Map.empty

                    let version =
                        currentDocs
                        |> Map.tryFind filename
                        |> Option.map (fun owner -> owner.Version + 1L)
                        |> Option.defaultValue System.Int64.MinValue
                        |> max (currentVersion ())

                    let docs = currentDocs |> Map.add filename { SessionId = sessionId; Version = version }

                    let state' = state |> Map.add key docs
                    do! persistImpl state'
                    return! loop state'

                | AttributeIfNewer(key, filename, sessionId, version, reply) ->
                    let currentDocs =
                        state
                        |> Map.tryFind key
                        |> Option.defaultValue Map.empty

                    match currentDocs |> Map.tryFind filename with
                    | Some current when
                        version < current.Version
                        || (version = current.Version && sessionId <> current.SessionId) ->
                        reply.Reply false
                        return! loop state
                    | _ ->
                        let docs = currentDocs |> Map.add filename { SessionId = sessionId; Version = version }
                        let state' = state |> Map.add key docs
                        do! persistImpl state'
                        reply.Reply true
                        return! loop state'

                | RemoveIfNewer(key, filename, sessionId, version, reply) ->
                    let currentDocs =
                        state
                        |> Map.tryFind key
                        |> Option.defaultValue Map.empty

                    match currentDocs |> Map.tryFind filename with
                    | Some current when
                        version < current.Version
                        || (version = current.Version && sessionId <> current.SessionId) ->
                        reply.Reply false
                        return! loop state
                    | None ->
                        reply.Reply true
                        return! loop state
                    | Some _ ->
                        let docs = currentDocs |> Map.remove filename
                        let state' =
                            if Map.isEmpty docs then state |> Map.remove key
                            else state |> Map.add key docs

                        do! persistImpl state'
                        reply.Reply true
                        return! loop state'

                | GetOwner(key, filename, reply) ->
                    state
                    |> Map.tryFind key
                    |> Option.bind (Map.tryFind filename)
                    |> Option.map _.SessionId
                    |> reply.Reply

                    return! loop state

                | GetAll(key, reply) ->
                    state
                    |> Map.tryFind key
                    |> Option.defaultValue Map.empty
                    |> Map.map (fun _ owner -> owner.SessionId)
                    |> reply.Reply

                    return! loop state

                | Load loaded ->
                    Log.log "CanvasDocOwnership" $"Loaded ownership for {Map.count loaded} worktree(s)"
                    return! loop loaded
            }

        loop Map.empty)

let attribute (worktreePath: string) (filename: string) (sessionId: string) =
    agent.Post(Attribute(normalizePath worktreePath, filename, sessionId))

let attributeIfNewer (worktreePath: string) (filename: string) (sessionId: string) (version: int64) =
    agent.PostAndAsyncReply(fun reply ->
        AttributeIfNewer(normalizePath worktreePath, filename, sessionId, version, reply))

let removeIfNewer (worktreePath: string) (filename: string) (sessionId: string) (version: int64) =
    agent.PostAndAsyncReply(fun reply ->
        RemoveIfNewer(normalizePath worktreePath, filename, sessionId, version, reply))

let getOwner (worktreePath: string) (filename: string) =
    agent.PostAndAsyncReply(fun reply -> GetOwner(normalizePath worktreePath, filename, reply))

let getAll (worktreePath: string) =
    agent.PostAndAsyncReply(fun reply -> GetAll(normalizePath worktreePath, reply))

let load () =
    try
        if File.Exists(filePath) then
            let json = File.ReadAllText(filePath)
            use doc = JsonDocument.Parse(json)

            let state =
                doc.RootElement.EnumerateObject()
                |> Seq.fold (fun acc worktreeProp ->
                    let docs =
                        worktreeProp.Value.EnumerateObject()
                        |> Seq.fold (fun acc prop ->
                            let owner =
                                match prop.Value.ValueKind with
                                | JsonValueKind.String ->
                                    { SessionId = prop.Value.GetString()
                                      Version = System.Int64.MinValue }
                                | JsonValueKind.Object ->
                                    { SessionId = prop.Value.GetProperty("sessionId").GetString()
                                      Version = prop.Value.GetProperty("version").GetInt64() }
                                | kind ->
                                    failwith $"Invalid ownership value for {prop.Name}: {kind}"

                            acc |> Map.add prop.Name owner
                        ) Map.empty

                    acc |> Map.add (normalizePath worktreeProp.Name) docs
                ) Map.empty

            agent.Post(Load state)
    with ex ->
        Log.log "CanvasDocOwnership" $"Failed to load: {ex.Message}"
