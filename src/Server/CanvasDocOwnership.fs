module Server.CanvasDocOwnership

open System.IO
open System.Text.Json

let private normalizePath = Server.PathUtils.normalizePath

let private filePath = Path.Combine("data", "canvas-owners.json")

type private Msg =
    | Attribute of key: string * filename: string * sessionId: string
    | Remove of key: string * filename: string
    | GetOwner of key: string * filename: string * AsyncReplyChannel<string option>
    | GetAll of key: string * AsyncReplyChannel<Map<string, string>>
    | Load of state: Map<string, Map<string, string>>

let private persistImpl (state: Map<string, Map<string, string>>) =
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
                docs |> Map.iter (fun filename sessionId ->
                    writer.WriteString(filename, sessionId))
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
        let rec loop (state: Map<string, Map<string, string>>) =
            async {
                let! msg = inbox.Receive()

                match msg with
                | Attribute(key, filename, sessionId) ->
                    let docs =
                        state
                        |> Map.tryFind key
                        |> Option.defaultValue Map.empty
                        |> Map.add filename sessionId

                    let state' = state |> Map.add key docs
                    do! persistImpl state'
                    return! loop state'

                | Remove(key, filename) ->
                    let docs =
                        state
                        |> Map.tryFind key
                        |> Option.defaultValue Map.empty
                        |> Map.remove filename

                    let state' =
                        if Map.isEmpty docs then state |> Map.remove key
                        else state |> Map.add key docs

                    do! persistImpl state'
                    return! loop state'

                | GetOwner(key, filename, reply) ->
                    state
                    |> Map.tryFind key
                    |> Option.bind (Map.tryFind filename)
                    |> reply.Reply

                    return! loop state

                | GetAll(key, reply) ->
                    state
                    |> Map.tryFind key
                    |> Option.defaultValue Map.empty
                    |> reply.Reply

                    return! loop state

                | Load loaded ->
                    Log.log "CanvasDocOwnership" $"Loaded ownership for {Map.count loaded} worktree(s)"
                    return! loop loaded
            }

        loop Map.empty)

let attribute (worktreePath: string) (filename: string) (sessionId: string) =
    agent.Post(Attribute(normalizePath worktreePath, filename, sessionId))

let remove (worktreePath: string) (filename: string) =
    agent.Post(Remove(normalizePath worktreePath, filename))

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
                            acc |> Map.add (prop.Name) (prop.Value.GetString())
                        ) Map.empty

                    acc |> Map.add (normalizePath worktreeProp.Name) docs
                ) Map.empty

            agent.Post(Load state)
    with ex ->
        Log.log "CanvasDocOwnership" $"Failed to load: {ex.Message}"
