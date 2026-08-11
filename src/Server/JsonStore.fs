module Server.JsonStore

open System
open System.IO
open System.Text.Json

/// Atomically persists a JSON document and reports failures after logging them.
let tryPersist (logTag: string) (path: string) (writeBody: Utf8JsonWriter -> unit) : Async<Result<unit, string>> =
    async {
        try
            let dir = Path.GetDirectoryName(path)
            if not (String.IsNullOrEmpty dir) && not (Directory.Exists dir) then
                Directory.CreateDirectory dir |> ignore

            use stream = new MemoryStream()
            use writer = new Utf8JsonWriter(stream, JsonWriterOptions(Indented = true))
            writeBody writer
            writer.Flush()

            let json = System.Text.Encoding.UTF8.GetString(stream.ToArray())
            let tempPath = path + ".tmp"
            do! File.WriteAllTextAsync(tempPath, json) |> Async.AwaitTask
            File.Move(tempPath, path, overwrite = true)
            return Ok()
        with ex ->
            let error = $"Failed to persist {path}: {ex.Message}"
            Log.log logTag error
            return Error error
    }

/// Loads a JSON document from `path` and projects its root element with `parse`, returning `None`
/// for an absent OR corrupt file and `Some (parse root)` otherwise. NEVER throws — this runs at
/// server startup, where a throw would crash boot.
///
/// `parse` receives the root `JsonElement` and MUST fully materialize its result before returning:
/// the backing `JsonDocument` is disposed the instant `parse` completes, so returning a lazy `Seq`
/// that re-reads the element would read a disposed document.
let load (logTag: string) (path: string) (parse: JsonElement -> 'T) : 'T option =
    try
        if File.Exists path then
            let json = File.ReadAllText path
            use doc = JsonDocument.Parse json
            Some(parse doc.RootElement)
        else
            None
    with ex ->
        Log.log logTag $"Failed to load {path}: {ex.Message}"
        None
