module OverviewBand

// The chrome-less Overview band (spec: docs/spec/beads-overview-band.md).
//
// A native Feliz view — CSS classes only, no inline styles — rendered inside the dashboard, above
// the repo list, gated by the caller on Model.OverviewPanelOpen. It consumes the pure cross-worktree
// roll-up from OverviewData.aggregate and paints two sibling sections that share one count+label
// rhythm:
//   - Active agents -> one CIRCLE per active agent, grouped by the skill each is running.
//   - Tasks         -> one solid BAR per status, drawn as a run of identical unit CELLS. Because
//                      every cell is the same fixed CSS size, a count-N bar is exactly N cells wide,
//                      so every bar sits on ONE true shared linear scale (no cap, no fade) WITHOUT
//                      any inline width style — honouring the CSS-classes-only constraint. Scale is
//                      therefore implicit in the geometry rather than read from Overview.Scale.
//
// Empty categories never reach the view (aggregate omits them), so nothing ever renders a 0, and a
// fully-empty roll-up collapses to nothing at all. v1 is static — no hover/click/greenlight.

open Shared
open Navigation
open Feliz
open OverviewData

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

// Accent-color modifier class per task bucket. The class sets `color`, which drives BOTH the count
// text and the cell fill (the cell paints `background: currentColor`).
let private taskClass =
    function
    | TaskBucketKind.Planned -> "task-planned"
    | TaskBucketKind.Queued -> "task-queued"
    | TaskBucketKind.InProgress -> "task-inprogress"
    | TaskBucketKind.Blocked -> "task-blocked"
    | TaskBucketKind.Done -> "task-done"

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

/// One category column shared by both sections: a run of `count` identical marks over a count+label
/// meta line. `marksContainerClass` picks the mark spacing (circles keep a gap; the bar closes it so
/// its cells read as one solid bar); `markClass` picks the glyph (circle vs cell). Both the marks and
/// the count carry `accentClass` so they take the category colour; the label stays neutral so count
/// and label share font size/weight and differ only by colour.
let private renderColumn
    (marksContainerClass: string)
    (markClass: string)
    (accentClass: string)
    (label: string)
    (count: int)
    =
    Html.div
        [ prop.className "overview-item"
          prop.key accentClass
          prop.children
              [ Html.div
                    [ prop.className marksContainerClass
                      prop.children
                          [ for i in 1..count ->
                                Html.span [ prop.key i; prop.className (markClass + " " + accentClass) ] ] ]
                Html.div
                    [ prop.className "overview-meta"
                      prop.children
                          [ Html.span [ prop.className "overview-label"; prop.text label ]
                            Html.span [ prop.className ("overview-count " + accentClass); prop.text (string count) ] ] ] ] ]

let private agentColumn (group: ActivityGroup) =
    renderColumn "overview-marks" "overview-circle" (activityClass group.Activity) (activityLabel group.Activity) group.Count

let private taskColumn (bucket: TaskBucket) =
    renderColumn "overview-marks overview-bar" "overview-cell" (taskClass bucket.Kind) (taskLabel bucket.Kind) bucket.Count

// A section renders only when it has at least one non-empty category, so an all-empty lens leaves no
// stray container behind.
let private renderSection (extraClass: string) (columns: ReactElement list) =
    match columns with
    | [] -> Html.none
    | cols -> Html.div [ prop.className ("overview-section " + extraClass); prop.children cols ]

/// Render the Overview band for the current repos. Returns Html.none when the whole roll-up is empty
/// so the band adds no chrome (not even margin) when there is nothing to show.
let view (repos: RepoModel list) : ReactElement =
    let overview = repos |> List.map toRepoWorktrees |> OverviewData.aggregate

    match overview.Activities, overview.Tasks with
    | [], [] -> Html.none
    | activities, tasks ->
        Html.div
            [ prop.className "overview-band"
              prop.children
                  [ renderSection "overview-agents" (activities |> List.map agentColumn)
                    renderSection "overview-tasks" (tasks |> List.map taskColumn) ] ]
