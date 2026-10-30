module Server.BeadsStatus

open System
open System.IO
open System.Text.Json
open Microsoft.Data.Sqlite
open Shared

/// Lightweight projection of a beads issue — only the fields the planning classifier needs.
/// Produced by the database reader or JSONL parser; IssueType and Status mirror
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

/// Pure classifier: projects non-feature issues for the Overview task buckets. Open issues are
/// partitioned into Planned/Queued/Loose by the status of their DIRECT parent-child parent when
/// that parent is a feature (ONE hop, not transitive); other statuses are counted directly.
/// Features themselves are containers, never task units.
module Planning =

    let [<Literal>] private FeatureType = "feature"
    let [<Literal>] private StatusOpen = "open"
    let [<Literal>] private StatusInProgress = "in_progress"
    let [<Literal>] private StatusBlocked = "blocked"
    let [<Literal>] private StatusClosed = "closed"

    /// Status of an issue's parent feature via one parent-child hop, or None when there is no
    /// parent, the parent is missing from the set, or the parent is not a feature.
    let private parentFeatureStatus (byId: Map<string, PlanningIssue>) (issue: PlanningIssue) =
        issue.ParentId
        |> Option.bind (fun pid -> Map.tryFind pid byId)
        |> Option.filter (fun parent -> eqCI parent.IssueType FeatureType)
        |> Option.map _.Status

    /// Project non-feature issues into feature-free task counts. Open issues are partitioned by
    /// their direct parent-feature status:
    ///   open feature parent        => Planned
    ///   in_progress feature parent => Queued
    ///   no parent / closed|blocked feature / non-feature parent => Loose
    let classify (issues: PlanningIssue list) : BeadsPlanning =
        let byId = issues |> List.map (fun i -> i.Id, i) |> Map.ofList

        issues
        |> List.filter (fun issue -> not (eqCI issue.IssueType FeatureType))
        |> List.fold (fun acc issue ->
            match issue.Status with
            | status when eqCI status StatusOpen ->
                match parentFeatureStatus byId issue with
                | Some parentStatus when eqCI parentStatus StatusOpen -> { acc with Planned = acc.Planned + 1 }
                | Some parentStatus when eqCI parentStatus StatusInProgress -> { acc with Queued = acc.Queued + 1 }
                | _ -> { acc with Loose = acc.Loose + 1 }
            | status when eqCI status StatusInProgress -> { acc with InProgress = acc.InProgress + 1 }
            | status when eqCI status StatusBlocked -> { acc with Blocked = acc.Blocked + 1 }
            | status when eqCI status StatusClosed -> { acc with Closed = acc.Closed + 1 }
            | _ -> acc)
            BeadsPlanning.zero

/// Parse one issues.jsonl line into the lightweight model. The parent-child parent is resolved
/// from an inline dependency edge ONLY: a "parent-child" edge carries issue_id = this record (the
/// child) and depends_on_id = its parent feature, so we take depends_on_id. A "blocks" edge is
/// deliberately ignored and NEVER populates ParentId (the planning classifier relies on this).
/// Returns None for a blank/malformed line (logged, then skipped) so one bad row can't nuke the
/// whole collection. A row with no `id` is a malformed row and is skipped (never counted).
let private parseLine (line: string) : PlanningIssue option =
    try
        use doc = JsonDocument.Parse(line)
        let root = doc.RootElement

        match JsonHelpers.tryStringValue "id" root with
        | Some id when not (String.IsNullOrWhiteSpace id) ->
            let parentId =
                match root.TryGetProperty("dependencies") with
                | true, deps when deps.ValueKind = JsonValueKind.Array ->
                    deps.EnumerateArray()
                    |> Seq.tryPick (fun edge ->
                        // Guard the edge direction: it must belong to THIS record as the child
                        // (issue_id = id); depends_on_id is then the parent we want.
                        let childMatches =
                            match JsonHelpers.tryStringValue "issue_id" edge with
                            | Some iid -> String.Equals(iid, id, StringComparison.Ordinal)
                            | None -> false // Reject malformed edge — a missing issue_id cannot match this record

                        if eqCI (JsonHelpers.tryStringValue "type" edge |> Option.defaultValue "") "parent-child" && childMatches then
                            JsonHelpers.tryStringValue "depends_on_id" edge
                        else
                            None)
                | _ -> None

            Some
                { Id = id
                  IssueType = JsonHelpers.tryStringValue "issue_type" root |> Option.defaultValue ""
                  Status = JsonHelpers.tryStringValue "status" root |> Option.defaultValue ""
                  ParentId = parentId }
        | _ ->
            Log.log "Beads" $"Skipping issues.jsonl row with no id: {line}"
            None
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

/// Card-level BeadsSummary counting ALL issue types by status. The Overview band instead consumes
/// the feature-free Planning projection.
let summarize (issues: PlanningIssue list) : BeadsSummary =
    let count status = issues |> List.filter (fun i -> eqCI i.Status status) |> List.length
    { Open = count "open"
      InProgress = count "in_progress"
      Blocked = count "blocked"
      Closed = count "closed" }

let getBeadsIssueList (dbPath: string) =
    async {
        if File.Exists(dbPath) then
            let! output =
                ProcessRunner.text
                    { ProcessRunner.Spawn.create "bd" with Context = "Beads" }
                    [ "list"; "--json"; "--db"; dbPath ]

            return output |> Option.defaultValue "[]"
        else
            return "[]"
    }

let private readDatabase (dbPath: string) : PlanningIssue list =
    use connection =
        new SqliteConnection(
            SqliteConnectionStringBuilder(
                DataSource = dbPath,
                Mode = SqliteOpenMode.ReadOnly,
                Pooling = false
            ).ConnectionString
        )

    connection.Open()
    use command = connection.CreateCommand()
    // One statement keeps issues and their directed parent edges in the same committed snapshot.
    command.CommandText <-
        """
SELECT i.id, i.issue_type, i.status,
       (SELECT d.depends_on_id
        FROM dependencies d
        WHERE d.issue_id = i.id AND d.type = 'parent-child' COLLATE NOCASE
        ORDER BY d.depends_on_id
        LIMIT 1)
FROM issues i;
"""
    use reader = command.ExecuteReader()

    SqliteStorage.readRows reader (fun row ->
        { Id = row.GetString(0)
          IssueType = row.GetString(1)
          Status = row.GetString(2)
          ParentId = if row.IsDBNull(3) then None else Some(row.GetString(3)) }) []

let private sourceExists (path: string) =
    // File.Exists also hides access errors, which must not select a stale export.
    try
        File.GetAttributes(path) |> ignore
        true
    with
    | :? FileNotFoundException
    | :? DirectoryNotFoundException -> false

/// Read the live database when present, otherwise the JSONL-only store, deriving both projections
/// from one snapshot without spawning bd. Read failures propagate to the scheduler so it retains
/// the previous counts and page rather than publishing empty or stale export data.
let getBeadsData (worktreePath: string) : Async<BeadsSummary * BeadsPlanning> =
    async {
        let dbPath = Path.Combine(worktreePath, ".beads", "beads.db")
        let jsonlPath = Path.Combine(worktreePath, ".beads", "issues.jsonl")

        let! issues =
            async {
                if sourceExists dbPath then
                    return readDatabase dbPath
                elif sourceExists jsonlPath then
                    let! content = File.ReadAllTextAsync(jsonlPath) |> Async.AwaitTask
                    return parseIssues content
                else
                    return []
            }

        return summarize issues, Planning.classify issues
    }
