module Server.OverviewHistory

// Append-only history of the Overview band's cross-worktree roll-up (spec:
// docs/spec/overview-activity-history.md). Every change to the `{Tasks; Agents}` aggregate is
// persisted as one JSON-Lines record in logs/overview-history.jsonl so the band can render a
// past-24h/72h timeseries. Three responsibilities:
//   - changed:    decide whether a freshly computed Overview differs from the last logged snapshot,
//                 comparing ONLY the count-only projection (Members and timestamp excluded), so an
//                 unchanged roll-up — even with churning per-worktree membership — appends nothing.
//   - append:     write one snapshot as a single JSONL line, reusing Log.fs's FileStream + lock,
//                 append-mode pattern (crash-safe, concurrent-reader-tolerant).
//   - readWindow: parse the JSONL back into snapshots within a look-back window, tolerating a
//                 partial trailing line left by a crash mid-write.
// The wire/JSON shape is the same Fable.Remoting converter IWorktreeApi.getOverviewHistory uses, so
// what is logged round-trips byte-identically to what the client later receives.

open System
open System.IO
open Newtonsoft.Json
open OverviewData

// Same converter the Fable.Remoting wire uses (see WorktreeApi.loadFixtures + the
// OverviewSnapshot serialization guards in Tests/SerializationTests.fs), so a logged line and the
// value getOverviewHistory returns can never drift in shape.
let private converter = Fable.Remoting.Json.FableJsonConverter()

let private historyPath () =
    Path.Combine(Directory.GetCurrentDirectory(), "logs", "overview-history.jsonl")

// One lock guards both append (write) and readWindow (read); a shared lock plus FileShare.ReadWrite
// keeps a mid-write reader from seeing a torn line (and readWindow tolerates it regardless).
let private lockObj = obj ()

/// Build the count-only snapshot that gets persisted per line: the capture time plus the Overview
/// projected to counts (drill-down `Members` dropped — decision #10). Callers thread the returned
/// snapshot as the scheduler's "last logged" accumulator.
let snapshot (timestamp: DateTimeOffset) (overview: Overview) : OverviewSnapshot =
    let counts = toCounts overview
    { Timestamp = timestamp
      Tasks = counts.Tasks
      Agents = counts.Agents }

/// True when the freshly computed Overview should be appended — i.e. its count-only projection
/// differs from the last logged snapshot's `{Tasks; Agents}` (decision #3). The timestamp is
/// excluded (the count lists are compared directly) and membership is excluded by construction
/// (`toCounts` drops `Members`), so identical counts with churning membership append nothing. With
/// no prior snapshot the first aggregate always counts as changed, so the baseline line is written.
let changed (last: OverviewSnapshot option) (overview: Overview) : bool =
    match last with
    | None -> true
    | Some prev ->
        let counts = toCounts overview
        counts.Tasks <> prev.Tasks || counts.Agents <> prev.Agents

/// Serialize one snapshot to a single JSON line via the Fable.Remoting converter. Formatting.None
/// guarantees no embedded newlines, so exactly one snapshot maps to one JSONL line.
let serialize (snap: OverviewSnapshot) : string =
    JsonConvert.SerializeObject(snap, Formatting.None, converter)

/// Parse one JSONL line back to a snapshot, returning None for a blank, null, or malformed line
/// (e.g. the partial trailing line a crash mid-write leaves behind) so one bad row never aborts the
/// whole read.
let tryParse (line: string) : OverviewSnapshot option =
    if String.IsNullOrWhiteSpace line then
        None
    else
        try
            let value = JsonConvert.DeserializeObject<OverviewSnapshot>(line, converter)
            if obj.ReferenceEquals(value, null) then None else Some value
        with _ ->
            None

/// Given a reference "now", a look-back window, and the raw JSONL lines, parse each line tolerantly
/// and keep only the snapshots at or after `now - window` (older records are ignored, not pruned).
/// Split out from readWindow so window filtering and partial-line tolerance are unit-testable
/// without touching disk.
let parseWindow (now: DateTimeOffset) (window: TimeSpan) (lines: string seq) : OverviewSnapshot list =
    let cutoff = now - window
    lines
    |> Seq.choose tryParse
    |> Seq.filter (fun s -> s.Timestamp >= cutoff)
    |> List.ofSeq

/// Append one snapshot as a JSONL line to logs/overview-history.jsonl, reusing Log.fs's append-mode
/// FileStream + lock pattern (creating logs/ on first write). Append + FileShare.ReadWrite keep the
/// file crash-safe and readable concurrently; write failures are swallowed like Log.log.
let append (snap: OverviewSnapshot) : unit =
    let path = historyPath ()
    path |> Path.GetDirectoryName |> Directory.CreateDirectory |> ignore
    let line = serialize snap + "\n"
    lock lockObj (fun () ->
        try
            use stream = new FileStream(path, FileMode.Append, FileAccess.Write, FileShare.ReadWrite)
            use writer = new StreamWriter(stream)
            writer.Write(line)
        with _ -> ())

/// Read the append-only history file and return the snapshots logged within the last `window`
/// (newest data included), tolerating a partial trailing line. A missing file yields an empty list.
let readWindow (window: TimeSpan) : OverviewSnapshot list =
    let path = historyPath ()
    if not (File.Exists path) then
        []
    else
        let text =
            lock lockObj (fun () ->
                use stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
                use reader = new StreamReader(stream)
                reader.ReadToEnd())

        text.Split('\n') |> parseWindow DateTimeOffset.Now window
