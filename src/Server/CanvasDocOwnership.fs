module Server.CanvasDocOwnership

open System
open System.Collections.Concurrent
open System.IO
open System.Text.Json

let private normalizePath = Server.PathUtils.normalizePath

// Mutable: ConcurrentDictionary used for thread-safe ownership tracking;
// simple read/write access pattern doesn't warrant MailboxProcessor overhead.
let private ownership = ConcurrentDictionary<string, Map<string, string>>(StringComparer.OrdinalIgnoreCase)

let private filePath = Path.Combine("data", "canvas-owners.json")

let private persistImpl () =
    async {
        try
            let dir = Path.GetDirectoryName(filePath)
            if not (Directory.Exists(dir)) then Directory.CreateDirectory(dir) |> ignore

            let options = JsonWriterOptions(Indented = true)
            use stream = new MemoryStream()
            use writer = new Utf8JsonWriter(stream, options)
            writer.WriteStartObject()

            ownership
            |> Seq.iter (fun kvp ->
                writer.WritePropertyName(kvp.Key)
                writer.WriteStartObject()
                kvp.Value |> Map.iter (fun filename sessionId ->
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

// Serializes persist calls through a MailboxProcessor to avoid
// concurrent writes racing on the shared temp file path.
let private persistAgent =
    MailboxProcessor.Start(fun inbox ->
        let rec loop () =
            async {
                let! _msg = inbox.Receive()
                do! persistImpl ()
                return! loop ()
            }
        loop ())

let private persist () = persistAgent.Post(())

let attribute (worktreePath: string) (filename: string) (sessionId: string) =
    let key = normalizePath worktreePath

    ownership.AddOrUpdate(
        key,
        Map.ofList [ filename, sessionId ],
        fun _ existing -> existing |> Map.add filename sessionId
    )
    |> ignore

    persist ()

let getOwner (worktreePath: string) (filename: string) : string option =
    let key = normalizePath worktreePath

    match ownership.TryGetValue(key) with
    | true, docs -> docs |> Map.tryFind filename
    | false, _ -> None

let getAll (worktreePath: string) : Map<string, string> =
    let key = normalizePath worktreePath

    match ownership.TryGetValue(key) with
    | true, docs -> docs
    | false, _ -> Map.empty

let load () =
    try
        if File.Exists(filePath) then
            let json = File.ReadAllText(filePath)
            use doc = JsonDocument.Parse(json)

            doc.RootElement.EnumerateObject()
            |> Seq.iter (fun worktreeProp ->
                let docs =
                    worktreeProp.Value.EnumerateObject()
                    |> Seq.fold (fun acc prop ->
                        acc |> Map.add (prop.Name) (prop.Value.GetString())
                    ) Map.empty

                ownership[normalizePath worktreeProp.Name] <- docs)

            Log.log "CanvasDocOwnership" $"Loaded ownership for {ownership.Count} worktree(s)"
    with ex ->
        Log.log "CanvasDocOwnership" $"Failed to load: {ex.Message}"
