module Server.JsonStore

open System
open System.IO
open System.Text.Json

/// Atomically persists a JSON document to `path`: serializes to an in-memory buffer, writes it to a
/// temp sibling (`path + ".tmp"`), then `File.Move`s it into place (overwrite) so a concurrent
/// reader never observes a half-written file. Owns the whole effectful shell — directory creation,
/// the indented `MemoryStream`/`Utf8JsonWriter` setup, the temp-file + atomic swap, and
/// swallow-and-log error handling — while the caller supplies only `writeBody`, which writes the
/// document (root object included) to the given writer. NEVER throws: a persist failure is logged
/// under `logTag`, not raised. Mirrors the `FileUtils` `logTag` convention and is shared by
/// `MergedPrStore`, `CanvasDocOwnership`, and `SessionManager` (review F1).
let persist (logTag: string) (path: string) (writeBody: Utf8JsonWriter -> unit) =
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
        with ex ->
            Log.log logTag $"Failed to persist {path}: {ex.Message}"
    }

/// Loads a JSON document from `path` and projects its root element with `parse`, returning `None`
/// for an absent OR corrupt file and `Some (parse root)` otherwise. Owns the safe-load shell —
/// `File.Exists`, `File.ReadAllText`, `JsonDocument.Parse`, and swallow-and-log error handling — so
/// each caller keeps only its own projection. NEVER throws (this runs at server startup, where a
/// throw would crash boot); an unreadable/unparseable file degrades to `None`. Mirrors the
/// `FileUtils` `logTag` convention and is shared by `MergedPrStore`, `CanvasDocOwnership`, and
/// `SessionManager` (review F2/F3).
///
/// `parse` receives the root `JsonElement` and MUST fully materialize its result before returning:
/// the backing `JsonDocument` is disposed the instant `parse` completes, so returning a lazy `Seq`
/// that re-reads the element would read a disposed document. Every current caller folds into a
/// `Map`, which materializes eagerly.
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
