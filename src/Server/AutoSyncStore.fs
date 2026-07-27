module Server.AutoSyncStore

open System
open System.Globalization
open System.IO
open System.Text.Json

/// Names one acceptance: one prompt accepted for one revision at one moment. Both suppression
/// layers carry it so that a clear can name the acceptance it observed instead of "whatever is
/// stored for this path". The path is not enough and neither is the revision: the same revision is
/// legitimately accepted again after a catch-up, and a clear that matched on either alone could
/// erase a newer acceptance's record while leaving that acceptance's claim in place — a claim
/// suppressing its revision with no record left to age out.
type AcceptanceGeneration = AcceptanceGeneration of string

/// A fresh token per acceptance. Uniqueness cannot be derived from the acceptance time either: the
/// system clock is coarser than the gap between two acceptances of one worktree, so two distinct
/// acceptances can share an instant.
let nextAcceptance () = AcceptanceGeneration(Guid.NewGuid().ToString("N"))

/// One accepted auto-sync prompt, keyed by canonical worktree path. Only the base revision the
/// prompt was accepted for, the acceptance time, and the opaque generation that pairs the record
/// with its in-process claim are durable — never prompt or session content — so the record can only
/// answer "was this exact revision already prompted, how long ago, and under which acceptance".
type AcceptedSyncRecord =
    { BaseRevision: string
      AcceptedAt: DateTimeOffset
      Generation: AcceptanceGeneration }

let filePathForPort port = Path.Combine("data", $"auto-sync-{port}.json")

let private writeState (writer: Utf8JsonWriter) (state: Map<string, AcceptedSyncRecord>) =
    writer.WriteStartObject()

    state
    |> Map.iter (fun path record ->
        let (AcceptanceGeneration acceptance) = record.Generation
        writer.WritePropertyName(path)
        writer.WriteStartObject()
        writer.WriteString("base_revision", record.BaseRevision)
        writer.WriteString("accepted_at", record.AcceptedAt.ToUniversalTime().ToString("O", CultureInfo.InvariantCulture))
        writer.WriteString("acceptance", acceptance)
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

    match revision, acceptedAt with
    | Some revision, Some acceptedAt ->
        // A missing or blank generation is not a partial record: the token only pairs a record with
        // the in-process claim published under the same acceptance, and a just-loaded record has no
        // claim to pair with, so a fresh token serves the same purpose as the one on disk. This is
        // also how a file written before generations existed keeps suppressing its revision.
        let generation =
            JsonHelpers.tryStringValue "acceptance" el
            |> Option.filter (String.IsNullOrWhiteSpace >> not)
            |> Option.map AcceptanceGeneration
            |> Option.defaultWith nextAcceptance

        Some
            { BaseRevision = revision
              AcceptedAt = acceptedAt
              Generation = generation }
    | _ -> None

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
/// a caller that must not publish anything derived from the acceptance ahead of the record itself
/// needs this read-back to know the store applied it.
let publishAccepted (store: Store) path record =
    async {
        setAccepted store path record
        do! store.Get path |> Async.Ignore
    }

let clear (store: Store) path = store.Update path (fun _ -> None)

/// Retires exactly one acceptance, leaving any other stored record alone. A record published after
/// the read that asked for this clear belongs to a newer acceptance whose claim is live, and
/// erasing it would leave that claim suppressing its revision with no record left to age out. Use
/// `clear` only where auto-sync for the worktree ends outright — disable, merged cleanup, worktree
/// removal — because there the claim is dropped unconditionally too and no acceptance survives.
let clearAccepted (store: Store) path generation =
    store.Update path (function
        | Some record when record.Generation = generation -> None
        | existing -> existing)
