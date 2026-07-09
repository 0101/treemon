module OverviewBand

// The chrome-less Overview band (spec: docs/spec/beads-overview-band.md, Corrections v1.1).
//
// A native Feliz view rendered inside the dashboard, above the repo list, gated by the caller on
// Model.OverviewPanelOpen. It consumes the pure cross-worktree roll-up from OverviewData.aggregate
// and paints the prototype's `.band` block (.agents/canvas/beads-panel-prototypes.html) exactly:
// two STACKED sections split by a 1px dashed rule, each opening with an uppercase muted header, each
// category a column whose count+label meta line sits ABOVE its visual (count FIRST in the accent
// colour, label neutral, same size/weight):
//   - Active agents -> a row of ~15px CIRCLES, one per red-dot working agent, grouped by activity,
//                      plus the distinct Waiting group.
//   - Tasks         -> ONE proportional BAR per status on one true shared linear scale. The bar
//                      width is COMPUTED from Overview.Scale (count / Scale of a fixed max width,
//                      floored at a visible minimum) and applied as an inline width — the accepted,
//                      documented exception to the CSS-classes-only rule (a proportional width is
//                      inherently dynamic; spec decision (g)). The old N-unit-cell workaround is gone.
//
// Empty categories never reach the view (aggregate omits them), so nothing ever renders a 0, and a
// fully-empty roll-up collapses to Html.none. v1 is static — no hover/click/greenlight.

open Shared
open Navigation
open Feliz
open OverviewData

/// The fixed max bar width, in px: the largest task bucket fills this and every other bar is
/// proportional to it (matches the prototype's `380 / maxN` scale factor).
let private barMaxPx = 380

/// Floor for a bar width, in px: a count-1 bar still reads as a visible sliver (prototype min 5px).
let private barMinPx = 5

/// RepoModel splits archived worktrees into their own field, but OverviewData.aggregate wants the
/// server-shaped RepoWorktrees (every worktree present, archived flagged via IsArchived) so its Done
/// filter can still see them (spec decision (f)). Recombine here — the single aggregate call site —
/// so archived worktrees keep contributing to every non-Done bucket instead of silently vanishing.
let private toRepoWorktrees (repo: RepoModel) : RepoWorktrees =
    { RepoId = repo.RepoId
      RootFolderName = repo.Name
      Worktrees = repo.Worktrees @ repo.ArchivedWorktrees
      IsReady = repo.IsReady
      Provider = repo.Provider
      BaseBranch = repo.BaseBranch }

// Display label per task bucket, in the aggregate's canonical left-to-right order.
let private taskLabel =
    function
    | TaskBucketKind.Planned -> "Planned"
    | TaskBucketKind.Queued -> "Queued"
    | TaskBucketKind.InProgress -> "In progress"
    | TaskBucketKind.Blocked -> "Blocked"
    | TaskBucketKind.Done -> "Done"
    | TaskBucketKind.Unattended -> "Unattended"

// Accent-color modifier class per task bucket. The class sets `color`, which drives BOTH the count
// text and the bar fill (the bar paints `background: currentColor`).
let private taskClass =
    function
    | TaskBucketKind.Planned -> "task-planned"
    | TaskBucketKind.Queued -> "task-queued"
    | TaskBucketKind.InProgress -> "task-inprogress"
    | TaskBucketKind.Blocked -> "task-blocked"
    | TaskBucketKind.Done -> "task-done"
    | TaskBucketKind.Unattended -> "task-unattended"

// Display label per activity bucket, in the aggregate's canonical order.
let private activityLabel =
    function
    | CurrentActivity.Investigating -> "Investigating"
    | CurrentActivity.Planning -> "Planning"
    | CurrentActivity.Executing -> "Executing"
    | CurrentActivity.Reviewing -> "Reviewing"
    | CurrentActivity.Fixing -> "Fixing"
    | CurrentActivity.Working -> "Working"

// Accent-color modifier class per activity bucket (same currentColor scheme as taskClass).
let private activityClass =
    function
    | CurrentActivity.Investigating -> "activity-investigating"
    | CurrentActivity.Planning -> "activity-planning"
    | CurrentActivity.Executing -> "activity-executing"
    | CurrentActivity.Reviewing -> "activity-reviewing"
    | CurrentActivity.Fixing -> "activity-fixing"
    | CurrentActivity.Working -> "activity-working"

// Display label per agent group: the skill-derived activity, or the distinct Waiting group.
let private agentLabel =
    function
    | AgentGroupKind.Activity activity -> activityLabel activity
    | AgentGroupKind.Waiting -> "Waiting"

// Accent-color modifier class per agent group (same currentColor scheme as activityClass).
let private agentClass =
    function
    | AgentGroupKind.Activity activity -> activityClass activity
    | AgentGroupKind.Waiting -> "activity-waiting"

/// The count+label meta line shown ABOVE each visual: count FIRST in the accent colour, label
/// neutral, both the same font size/weight so they differ only by colour (prototype `.ulbl`). The
/// accent class sets `color`, so it tints the count; the label keeps the neutral meta colour.
let private metaLine (accentClass: string) (label: string) (count: int) =
    Html.div
        [ prop.className "overview-meta"
          prop.children
              [ Html.span [ prop.className ("overview-count " + accentClass); prop.text (string count) ]
                Html.span [ prop.className "overview-label"; prop.text label ] ] ]

/// One agent group column: the meta line above a row of ~15px circles, one per agent, tinted to the
/// group's accent (circle fill = currentColor, driven by the accent class).
let private agentColumn (group: AgentGroup) =
    let accent = agentClass group.Kind

    Html.div
        [ prop.className "overview-item"
          prop.key accent
          prop.children
              [ metaLine accent (agentLabel group.Kind) group.Count
                Html.div
                    [ prop.className "overview-circles"
                      prop.children
                          [ for i in 1 .. group.Count ->
                                Html.span [ prop.key i; prop.className ("overview-circle " + accent) ] ] ] ] ]

/// One task bucket column: the meta line above ONE proportional bar. The width is computed on the
/// single shared scale — count / Scale of the fixed max width, floored at barMinPx so a count-1 bar
/// stays visible — and applied inline (the accepted, documented CSS-classes-only exception; spec
/// decision (g)). `scale` is the largest bucket count, so the widest bar is exactly barMaxPx and
/// every other is proportional. Fill = currentColor via the accent class.
let private taskColumn (scale: int) (bucket: TaskBucket) =
    let accent = taskClass bucket.Kind
    // scale > 0 whenever any bucket is shown; +0.5 then truncate mirrors the prototype's Math.round.
    let px = max barMinPx (int (float bucket.Count / float scale * float barMaxPx + 0.5))

    Html.div
        [ prop.className "overview-item"
          prop.key accent
          prop.children
              [ metaLine accent (taskLabel bucket.Kind) bucket.Count
                Html.div [ prop.className ("overview-bar " + accent); prop.style [ style.width (length.px px) ] ] ] ]

/// A section shell: an uppercase header over the wrapping row of category columns. The stacked
/// layout + dashed separator live in CSS.
let private section (header: string) (columns: ReactElement list) =
    Html.div
        [ prop.className "overview-section"
          prop.children
              [ Html.div [ prop.className "overview-header"; prop.text header ]
                Html.div [ prop.className "overview-items"; prop.children columns ] ] ]

/// Render the Overview band for the current repos. Returns Html.none when the whole roll-up is empty
/// so the band adds no chrome (not even margin) when there is nothing to show.
let view (repos: RepoModel list) : ReactElement =
    let overview = repos |> List.map toRepoWorktrees |> OverviewData.aggregate

    match overview.Agents, overview.Tasks with
    | [], [] -> Html.none
    | agents, tasks ->
        // Header counts: N red-dot working agents, and M waiting (only when a Waiting group exists).
        let workingCount =
            agents
            |> List.sumBy (fun g ->
                match g.Kind with
                | AgentGroupKind.Activity _ -> g.Count
                | AgentGroupKind.Waiting -> 0)

        let waitingCount =
            agents
            |> List.tryPick (fun g ->
                match g.Kind with
                | AgentGroupKind.Waiting -> Some g.Count
                | AgentGroupKind.Activity _ -> None)

        Html.div
            [ prop.className "overview-band"
              prop.children
                  [ match agents with
                    | [] -> Html.none
                    | groups ->
                        // Uppercase muted section header (CSS upper-cases it): count of red-dot
                        // working agents, extended with the waiting count only when a Waiting group
                        // is present, e.g. "ACTIVE AGENTS · 9 WORKING · 2 WAITING". Per the "never
                        // render a 0" invariant, the "N working" fragment is dropped when none are
                        // working (a Waiting-only band reads "ACTIVE AGENTS · 2 WAITING").
                        let header =
                            match waitingCount with
                            | Some m when workingCount > 0 -> $"Active agents · {workingCount} working · {m} waiting"
                            | Some m -> $"Active agents · {m} waiting"
                            | None -> $"Active agents · {workingCount} working"

                        section header (groups |> List.map agentColumn)
                    match tasks with
                    | [] -> Html.none
                    | buckets ->
                        section
                            "Tasks · across all worktrees"
                            (buckets |> List.map (taskColumn overview.Scale)) ] ]
