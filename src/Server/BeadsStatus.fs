module Server.BeadsStatus

open System
open System.IO
open System.Text.Json
open Shared

/// Lightweight projection of a beads issue — only the fields the planning classifier needs.
/// Produced by the JSONL parser (see the JSONL data-source task); IssueType and Status mirror
/// beads' raw schema strings (e.g. "task"/"feature", "open"/"in_progress"/"blocked"/"closed").
/// ParentId MUST be resolved from a parent-child dependency edge ONLY (issue_id = child,
/// depends_on_id = parent) — never from a `blocks` edge. Keeping beads-schema knowledge here
/// isolates it to this module.
type PlanningIssue =
    { Id: string
      IssueType: string
      Status: string
      ParentId: string option }

/// Case-insensitive match against a raw beads schema string (status or issue_type). Null-safe: a
/// null left operand never matches. Shared by the planning classifier and the status summary.
let private eqCI (a: string) (b: string) =
    not (isNull a) && String.Equals(a, b, StringComparison.OrdinalIgnoreCase)

/// Pure classifier: partitions OPEN, non-feature issues into Planned/Queued/Loose by the status
/// of their DIRECT parent-child parent when that parent is a feature (ONE hop, not transitive).
/// This is the feature's core signal, so the open-vs-in_progress parent distinction must be exact.
/// Features themselves are containers, never subjects: the spec buckets "open tasks under a
/// feature", and display folds Loose into Planned, so counting an open feature would over-count.
module Planning =

    let [<Literal>] private FeatureType = "feature"
    let [<Literal>] private StatusOpen = "open"
    let [<Literal>] private StatusInProgress = "in_progress"

    /// Status of an issue's parent feature via one parent-child hop, or None when there is no
    /// parent, the parent is missing from the set, or the parent is not a feature.
    let private parentFeatureStatus (byId: Map<string, PlanningIssue>) (issue: PlanningIssue) =
        issue.ParentId
        |> Option.bind (fun pid -> Map.tryFind pid byId)
        |> Option.filter (fun parent -> eqCI parent.IssueType FeatureType)
        |> Option.map (fun parent -> parent.Status)

    /// Partition OPEN, non-feature issues by their direct parent-feature status:
    ///   open feature parent        => Planned
    ///   in_progress feature parent => Queued
    ///   no parent / closed|blocked feature / non-feature parent => Loose
    let classify (issues: PlanningIssue list) : BeadsPlanning =
        let byId = issues |> List.map (fun i -> i.Id, i) |> Map.ofList

        issues
        |> List.filter (fun issue -> eqCI issue.Status StatusOpen && not (eqCI issue.IssueType FeatureType))
        |> List.fold (fun acc issue ->
            match parentFeatureStatus byId issue with
            | Some status when eqCI status StatusOpen -> { acc with Planned = acc.Planned + 1 }
            | Some status when eqCI status StatusInProgress -> { acc with Queued = acc.Queued + 1 }
            | _ -> { acc with Loose = acc.Loose + 1 })
            BeadsPlanning.zero

let private stringProp (el: JsonElement) (name: string) =
    match el.TryGetProperty(name) with
    | true, v when v.ValueKind = JsonValueKind.String -> Some(v.GetString())
    | _ -> None

/// Parse one issues.jsonl line into the lightweight model. The parent-child parent is resolved
/// from an inline dependency edge ONLY: a "parent-child" edge carries issue_id = this record (the
/// child) and depends_on_id = its parent feature, so we take depends_on_id. A "blocks" edge is
/// deliberately ignored and NEVER populates ParentId (the planning classifier relies on this).
/// Returns None for a blank/malformed line (logged, then skipped) so one bad row can't nuke the
/// whole collection.
let private parseLine (line: string) : PlanningIssue option =
    try
        use doc = JsonDocument.Parse(line)
        let root = doc.RootElement
        let id = stringProp root "id" |> Option.defaultValue ""

        let parentId =
            match root.TryGetProperty("dependencies") with
            | true, deps when deps.ValueKind = JsonValueKind.Array ->
                deps.EnumerateArray()
                |> Seq.tryPick (fun edge ->
                    // Guard the edge direction: it must belong to THIS record as the child
                    // (issue_id = id); depends_on_id is then the parent we want.
                    let childMatches =
                        match stringProp edge "issue_id" with
                        | Some iid -> String.Equals(iid, id, StringComparison.Ordinal)
                        | None -> false // Reject malformed edge — a missing issue_id cannot match this record

                    if eqCI (stringProp edge "type" |> Option.defaultValue "") "parent-child" && childMatches then
                        stringProp edge "depends_on_id"
                    else
                        None)
            | _ -> None

        Some
            { Id = id
              IssueType = stringProp root "issue_type" |> Option.defaultValue ""
              Status = stringProp root "status" |> Option.defaultValue ""
              ParentId = parentId }
    with ex ->
        Log.log "Beads" $"Failed to parse issues.jsonl line: {ex.Message}"
        None

/// Parse the full issues.jsonl content (one JSON object per line) into lightweight issues.
let parseIssues (content: string) : PlanningIssue list =
    content.Split('\n')
    |> Array.choose (fun raw ->
        let line = raw.Trim()
        if line.Length = 0 then None else parseLine line)
    |> Array.toList

/// Status BeadsSummary counting ALL issue types by status (features included, unlike the planning
/// split). Closed feeds the band's Done bucket downstream.
let summarize (issues: PlanningIssue list) : BeadsSummary =
    let count status = issues |> List.filter (fun i -> eqCI i.Status status) |> List.length
    { Open = count "open"
      InProgress = count "in_progress"
      Blocked = count "blocked"
      Closed = count "closed" }

let getBeadsIssueList (dbPath: string) =
    async {
        if File.Exists(dbPath) then
            let! output = ProcessRunner.run "Beads" "bd" $"list --json --db \"{dbPath}\""
            return output |> Option.defaultValue "[]"
        else
            return "[]"
    }

/// Reads .beads/issues.jsonl and derives BOTH the status summary (all issue types by status) and
/// the planning split (open non-feature tasks by parent-feature status) from a SINGLE parse — no
/// `bd` spawn, no SQLite dependency. Missing OR empty file => (zero, zero), never an exception.
///
/// FRESHNESS: issues.jsonl is beads' canonical JSONL export, auto-flushed after CRUD, so it can
/// lag the live beads.db by up to the flush interval. We read it as-is and accept that lag
/// deliberately: adding a `bd export` refresh here would reintroduce the per-refresh process spawn
/// this parse exists to remove. If guaranteed freshness is ever required, export before reading.
let getBeadsData (worktreePath: string) : Async<BeadsSummary * BeadsPlanning> =
    async {
        let jsonlPath = Path.Combine(worktreePath, ".beads", "issues.jsonl")

        if File.Exists(jsonlPath) then
            try
                let! content = File.ReadAllTextAsync(jsonlPath) |> Async.AwaitTask
                let issues = parseIssues content
                return summarize issues, Planning.classify issues
            with ex ->
                Log.log "Beads" $"Failed to read {jsonlPath}: {ex.Message}"
                return BeadsSummary.zero, BeadsPlanning.zero
        else
            return BeadsSummary.zero, BeadsPlanning.zero
    }
