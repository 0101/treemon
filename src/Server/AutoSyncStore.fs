module Server.AutoSyncStore

open System
open System.Globalization
open System.IO
open System.Text.Json

/// One accepted auto-sync prompt, keyed by canonical worktree path. Only the base revision the
/// prompt was accepted for and the acceptance time are durable — never prompt or session content —
/// so the record can only answer "was this exact revision already prompted, and how long ago".
type AcceptedSyncRecord =
    { BaseRevision: string
      AcceptedAt: DateTimeOffset }

let filePathForPort port = Path.Combine("data", $"auto-sync-{port}.json")

let private writeState (writer: Utf8JsonWriter) (state: Map<string, AcceptedSyncRecord>) =
    writer.WriteStartObject()

    state
    |> Map.iter (fun path record ->
        writer.WritePropertyName(path)
        writer.WriteStartObject()
        writer.WriteString("base_revision", record.BaseRevision)
        writer.WriteString("accepted_at", record.AcceptedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))
        writer.WriteEndObject())

    writer.WriteEndObject()

let internal tryPersistAtPath (path: string) (state: Map<string, AcceptedSyncRecord>) =
    JsonStore.tryPersist "AutoSyncStore" path (fun writer -> writeState writer state)

/// A record survives loading only with a non-blank revision AND a parseable acceptance time: a
/// partial record cannot prove which revision was accepted or whether its retry window expired, and
/// suppressing a sync on a guess is worse than prompting once more.
let private tryParseRecord (el: JsonElement) =
    let revision =
        JsonHelpers.tryStringValue "base_revision" el
        |> Option.filter (String.IsNullOrWhiteSpace >> not)

    let acceptedAt =
        JsonHelpers.tryStringValue "accepted_at" el
        |> Option.bind (fun value ->
            match DateTimeOffset.TryParse(value, CultureInfo.InvariantCulture, DateTimeStyles.RoundtripKind) with
            | true, parsed -> Some parsed
            | _ -> None)

    (revision, acceptedAt)
    ||> Option.map2 (fun revision acceptedAt ->
        { BaseRevision = revision
          AcceptedAt = acceptedAt })

/// Loads the store via the shared safe loader; an absent or corrupt file yields an empty store so
/// startup never throws. The path is explicit so tests can target a temp dir.
let internal loadAtPath (path: string) : Map<string, AcceptedSyncRecord> =
    JsonStore.load "AutoSyncStore" path (fun root ->
        root.EnumerateObject()
        |> Seq.fold
            (fun acc prop ->
                tryParseRecord prop.Value
                |> Option.map (fun record -> acc |> Map.add prop.Name record)
                |> Option.defaultValue acc)
            Map.empty)
    |> Option.defaultValue Map.empty

type Store = PersistentStore.Store<string, AcceptedSyncRecord>

let create path : Store =
    PersistentStore.create "AutoSyncStore" (tryPersistAtPath path) (fun () -> loadAtPath path)

/// Records an accepted prompt for `path`. A blank revision is stored as absence: it can never match
/// a later observation, so persisting it would only keep a permanently dead entry on disk.
let setAccepted (store: Store) path (record: AcceptedSyncRecord) =
    store.Update path (fun _ ->
        if String.IsNullOrWhiteSpace record.BaseRevision then None else Some record)

/// Records an accepted prompt and completes only once the record is readable: `Update` is a post, so
/// the operation guard must not be released before the record the next observation re-reads exists.
let publishAccepted (store: Store) path record =
    async {
        setAccepted store path record
        do! store.Get path |> Async.Ignore
    }

let clear (store: Store) path = store.Update path (fun _ -> None)
