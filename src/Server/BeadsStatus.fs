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

/// Pure classifier: partitions OPEN, non-feature issues into Planned/Queued/Loose by the status
/// of their DIRECT parent-child parent when that parent is a feature (ONE hop, not transitive).
/// This is the feature's core signal, so the open-vs-in_progress parent distinction must be exact.
/// Features themselves are containers, never subjects: the spec buckets "open tasks under a
/// feature", and display folds Loose into Planned, so counting an open feature would over-count.
module Planning =

    let [<Literal>] private FeatureType = "feature"
    let [<Literal>] private StatusOpen = "open"
    let [<Literal>] private StatusInProgress = "in_progress"

    let private eq (a: string) (b: string) =
        not (isNull a) && String.Equals(a, b, StringComparison.OrdinalIgnoreCase)

    /// Status of an issue's parent feature via one parent-child hop, or None when there is no
    /// parent, the parent is missing from the set, or the parent is not a feature.
    let private parentFeatureStatus (byId: Map<string, PlanningIssue>) (issue: PlanningIssue) =
        issue.ParentId
        |> Option.bind (fun pid -> Map.tryFind pid byId)
        |> Option.filter (fun parent -> eq parent.IssueType FeatureType)
        |> Option.map (fun parent -> parent.Status)

    /// Partition OPEN, non-feature issues by their direct parent-feature status:
    ///   open feature parent        => Planned
    ///   in_progress feature parent => Queued
    ///   no parent / closed|blocked feature / non-feature parent => Loose
    let classify (issues: PlanningIssue list) : BeadsPlanning =
        let byId = issues |> List.map (fun i -> i.Id, i) |> Map.ofList

        issues
        |> List.filter (fun issue -> eq issue.Status StatusOpen && not (eq issue.IssueType FeatureType))
        |> List.fold (fun acc issue ->
            match parentFeatureStatus byId issue with
            | Some status when eq status StatusOpen -> { acc with Planned = acc.Planned + 1 }
            | Some status when eq status StatusInProgress -> { acc with Queued = acc.Queued + 1 }
            | _ -> { acc with Loose = acc.Loose + 1 })
            BeadsPlanning.zero

let private runBd (dbPath: string) =
    ProcessRunner.run "Beads" "bd" $"count --by-status --json --db \"{dbPath}\""

let private parseCountResponse (json: string) =
    try
        use doc = JsonDocument.Parse(json)
        let root = doc.RootElement

        let groups =
            match root.TryGetProperty("groups") with
            | true, arr ->
                arr.EnumerateArray()
                |> Seq.map (fun el ->
                    el.GetProperty("group").GetString(),
                    el.GetProperty("count").GetInt32())
                |> Map.ofSeq
            | _ -> Map.empty

        let findCount name =
            Map.tryFind name groups
            |> Option.defaultValue 0

        { Open = findCount "open"
          InProgress = findCount "in_progress"
          Blocked = findCount "blocked"
          Closed = findCount "closed" }
    with ex ->
        Log.log "Beads" $"Failed to parse bd JSON: {ex.Message}, raw input: {json}"
        BeadsSummary.zero

let getBeadsIssueList (dbPath: string) =
    async {
        if File.Exists(dbPath) then
            let! output = ProcessRunner.run "Beads" "bd" $"list --json --db \"{dbPath}\""
            return output |> Option.defaultValue "[]"
        else
            return "[]"
    }

let getBeadsSummary (worktreePath: string) =
    async {
        let dbPath = Path.Combine(worktreePath, ".beads", "beads.db")

        if File.Exists(dbPath) then
            let! output = runBd dbPath
            return
                output
                |> Option.map parseCountResponse
                |> Option.defaultValue BeadsSummary.zero
        else
            return BeadsSummary.zero
    }
