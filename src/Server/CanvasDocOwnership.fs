module Server.CanvasDocOwnership

open System.IO

let private normalizePath = Server.PathUtils.normalizePath

let private filePath = Path.Combine("data", "canvas-owners.json")

let private persistImpl (state: Map<string, Map<string, string>>) =
    JsonStore.tryPersist "CanvasDocOwnership" filePath (fun writer ->
        writer.WriteStartObject()

        state
        |> Map.iter (fun worktreeKey docs ->
            writer.WritePropertyName(worktreeKey)
            writer.WriteStartObject()
            docs |> Map.iter (fun filename sessionId ->
                writer.WriteString(filename, sessionId))
            writer.WriteEndObject())

        writer.WriteEndObject())

let private loadFromDisk () : Map<string, Map<string, string>> =
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
    |> Option.defaultValue Map.empty

let private store =
    PersistentStore.create "CanvasDocOwnership" persistImpl loadFromDisk

let attribute (worktreePath: string) (filename: string) (sessionId: string) =
    store.Update (normalizePath worktreePath) (fun docs ->
        docs
        |> Option.defaultValue Map.empty
        |> Map.add filename sessionId
        |> Some)

let getOwner (worktreePath: string) (filename: string) =
    async {
        let! docs = store.Get(normalizePath worktreePath)
        return docs |> Option.bind (Map.tryFind filename)
    }

let getAll (worktreePath: string) =
    async {
        let! docs = store.Get(normalizePath worktreePath)
        return docs |> Option.defaultValue Map.empty
    }

let load () = store.Load()
