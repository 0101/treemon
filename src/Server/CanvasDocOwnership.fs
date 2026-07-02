module Server.CanvasDocOwnership

open System.IO

let private normalizePath = Server.PathUtils.normalizePath

let private filePath = Path.Combine("data", "canvas-owners.json")

type private Msg =
    | Attribute of key: string * filename: string * sessionId: string
    | GetOwner of key: string * filename: string * AsyncReplyChannel<string option>
    | GetAll of key: string * AsyncReplyChannel<Map<string, string>>
    | Load of state: Map<string, Map<string, string>>

let private persistImpl (state: Map<string, Map<string, string>>) =
    JsonStore.persist "CanvasDocOwnership" filePath (fun writer ->
        writer.WriteStartObject()

        state
        |> Map.iter (fun worktreeKey docs ->
            writer.WritePropertyName(worktreeKey)
            writer.WriteStartObject()
            docs |> Map.iter (fun filename sessionId ->
                writer.WriteString(filename, sessionId))
            writer.WriteEndObject())

        writer.WriteEndObject())

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

let getOwner (worktreePath: string) (filename: string) =
    agent.PostAndAsyncReply(fun reply -> GetOwner(normalizePath worktreePath, filename, reply))

let getAll (worktreePath: string) =
    agent.PostAndAsyncReply(fun reply -> GetAll(normalizePath worktreePath, reply))

let load () =
    JsonStore.load "CanvasDocOwnership" filePath (fun root ->
        root.EnumerateObject()
        |> Seq.fold (fun acc worktreeProp ->
            let docs =
                worktreeProp.Value.EnumerateObject()
                |> Seq.fold (fun acc prop ->
                    acc |> Map.add (prop.Name) (prop.Value.GetString())
                ) Map.empty

            acc |> Map.add (normalizePath worktreeProp.Name) docs
        ) Map.empty)
    |> Option.iter (Load >> agent.Post)
