module Server.MergedPrStore

open System.IO
open System.Text.Json
open Shared

/// One persisted merged-PR fact — the minimal fields the merged badge renders (`Id`/`Title`/`Url`).
/// Volatile PR data (builds, comments, conflicts, draft) is deliberately never stored; only the
/// terminal "merged" fact survives the bounded GitHub fetch window and server restarts.
type MergedPrRecord =
    { Id: int
      Title: string
      Url: string }

/// Default on-disk location: gitignored server runtime state, NOT the user-authored `config.json`.
/// Matches `data/canvas-owners.json` (`CanvasDocOwnership.fs`) and `data/sessions.json`.
let private filePath = Path.Combine("data", "merged-prs.json")

type private Msg =
    | GetForRepo of repoId: RepoId * AsyncReplyChannel<Map<string, MergedPrRecord>>
    | SetForRepo of repoId: RepoId * records: Map<string, MergedPrRecord>
    | Load of state: Map<RepoId, Map<string, MergedPrRecord>>

/// Atomically writes the whole store to `path` (temp file + `File.Move`). The path is explicit so
/// tests can point at a temp dir; the agent always calls it with the module default `filePath`.
/// Mirrors `CanvasDocOwnership.persistImpl`: never throws — a persist failure is logged, not raised.
let internal persistAtPath (path: string) (state: Map<RepoId, Map<string, MergedPrRecord>>) =
    async {
        try
            let dir = Path.GetDirectoryName(path)
            if not (System.String.IsNullOrEmpty dir) && not (Directory.Exists(dir)) then
                Directory.CreateDirectory(dir) |> ignore

            let options = JsonWriterOptions(Indented = true)
            use stream = new MemoryStream()
            use writer = new Utf8JsonWriter(stream, options)
            writer.WriteStartObject()

            state
            |> Map.iter (fun (RepoId repoId) branchMap ->
                writer.WritePropertyName(repoId)
                writer.WriteStartObject()

                branchMap
                |> Map.iter (fun branch record ->
                    writer.WritePropertyName(branch)
                    writer.WriteStartObject()
                    writer.WriteNumber("id", record.Id)
                    writer.WriteString("title", record.Title)
                    writer.WriteString("url", record.Url)
                    writer.WriteEndObject())

                writer.WriteEndObject())

            writer.WriteEndObject()
            writer.Flush()

            let json = System.Text.Encoding.UTF8.GetString(stream.ToArray())
            let tempPath = path + ".tmp"
            do! File.WriteAllTextAsync(tempPath, json) |> Async.AwaitTask
            File.Move(tempPath, path, overwrite = true)
        with ex ->
            Log.log "MergedPrStore" $"Failed to persist: {ex.Message}"
    }

/// Loads the store from `path`. An absent or corrupt file loads as an empty store and NEVER throws
/// (this runs at server startup, mirroring the template's try/with). The path is explicit so tests
/// can point at a temp dir; `load ()` calls it with the module default `filePath`.
let internal loadAtPath (path: string) : Map<RepoId, Map<string, MergedPrRecord>> =
    try
        if File.Exists(path) then
            let json = File.ReadAllText(path)
            use doc = JsonDocument.Parse(json)

            doc.RootElement.EnumerateObject()
            |> Seq.fold
                (fun acc repoProp ->
                    let branchMap =
                        repoProp.Value.EnumerateObject()
                        |> Seq.fold
                            (fun acc branchProp ->
                                let el = branchProp.Value

                                let record =
                                    { Id = el.GetProperty("id").GetInt32()
                                      Title = el.GetProperty("title").GetString()
                                      Url = el.GetProperty("url").GetString() }

                                acc |> Map.add branchProp.Name record)
                            Map.empty

                    acc |> Map.add (RepoId repoProp.Name) branchMap)
                Map.empty
        else
            Map.empty
    with ex ->
        Log.log "MergedPrStore" $"Failed to load: {ex.Message}"
        Map.empty

let private agent =
    MailboxProcessor.Start(fun inbox ->
        let rec loop (state: Map<RepoId, Map<string, MergedPrRecord>>) =
            async {
                let! msg = inbox.Receive()

                match msg with
                | GetForRepo(repoId, reply) ->
                    state
                    |> Map.tryFind repoId
                    |> Option.defaultValue Map.empty
                    |> reply.Reply

                    return! loop state

                | SetForRepo(repoId, records) ->
                    let current =
                        state |> Map.tryFind repoId |> Option.defaultValue Map.empty

                    // Change-persisting: only touch the disk when this repo's records actually
                    // moved (Decision #6). An empty map drops the repo key so the file stays minimal.
                    if records = current then
                        return! loop state
                    else
                        let state' =
                            if Map.isEmpty records then
                                state |> Map.remove repoId
                            else
                                state |> Map.add repoId records

                        do! persistAtPath filePath state'
                        return! loop state'

                | Load loaded ->
                    Log.log "MergedPrStore" $"Loaded merged PRs for {Map.count loaded} repo(s)"
                    return! loop loaded
            }

        loop Map.empty)

/// Async read of a repo's persisted merged-PR records (branch -> record); empty when none stored.
let getForRepo (repoId: RepoId) =
    agent.PostAndAsyncReply(fun reply -> GetForRepo(repoId, reply))

/// Replaces a repo's persisted records, persisting to disk only when they differ from the current
/// store (fire-and-forget; serialized through the agent).
let setForRepo (repoId: RepoId) (records: Map<string, MergedPrRecord>) =
    agent.Post(SetForRepo(repoId, records))

/// Loads the store from the default path at startup. Never throws (absent/corrupt -> empty store).
let load () =
    agent.Post(Load(loadAtPath filePath))
