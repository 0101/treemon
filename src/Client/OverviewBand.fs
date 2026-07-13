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
//   - Tasks         -> ONE proportional BAR per status on one true shared linear scale. Each bar
//                      carries an inline `--bar-fill` custom property = count / Overview.Scale (its
//                      share of the largest bucket); CSS multiplies that by a RESPONSIVE shared max
//                      (`min(300px, 80cqi)`) so every bar shrinks together on a narrow dashboard pane
//                      while the shared scale and the count-1 min-width floor hold. The inline custom
//                      property is the accepted, documented exception to the CSS-classes-only rule (a
//                      proportional width is inherently dynamic; spec decision (g)). The old N-unit-cell
//                      workaround is gone.
//
// Empty categories never reach the view (aggregate omits them), so nothing ever renders a 0, and a
// fully-empty roll-up collapses to Html.none. v1 is static — no hover/click/greenlight.

open Shared
open Navigation
open Feliz
open OverviewData

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
/// group's accent (circle fill = currentColor, driven by the accent class). Clicking the column
/// raises onSelectGroup (App toggles the drill-down selection); when this group is the selected one
/// it renders as the black "tab" (overview-item-selected) sitting flush above its breakdown panel.
let private agentColumn (selection: OverviewSelection option) (onSelectGroup: OverviewSelection -> unit) (group: AgentGroup) =
    let accent = agentClass group.Kind
    let target = OverviewSelection.Agents group.Kind
    let isSelected = selection = Some target

    Html.div
        [ prop.className [ "overview-item"; accent; if isSelected then "overview-item-selected" ]
          prop.key accent
          prop.onClick (fun _ -> onSelectGroup target)
          prop.children
              [ metaLine accent (agentLabel group.Kind) group.Count
                Html.div
                    [ prop.className "overview-circles"
                      prop.children (
                          List.init group.Count (fun i ->
                              Html.span [ prop.key i; prop.className ("overview-circle " + accent) ]) ) ] ] ]

/// One task bucket column: the meta line above ONE proportional bar. The bar's share of the shared
/// scale — count / Scale — is emitted as the inline `--bar-fill` custom property; CSS multiplies it by
/// the responsive shared max (`min(300px, 80cqi)`) and floors it at a visible `min-width`, so every
/// bar scales together and a narrow dashboard pane can never make one overflow (the accepted,
/// documented CSS-classes-only exception; spec decision (g)). `scale` is the largest bucket count, so
/// the widest bar's fill is 1. Fill = currentColor via the accent class. Clicking the column raises
/// onSelectGroup; the selected bucket renders as the black "tab" (overview-item-selected).
let private taskColumn (selection: OverviewSelection option) (onSelectGroup: OverviewSelection -> unit) (scale: int) (bucket: TaskBucket) =
    let accent = taskClass bucket.Kind
    let target = OverviewSelection.Tasks bucket.Kind
    let isSelected = selection = Some target
    // scale > 0 whenever any bucket is shown; Fable stringifies the float with a '.' separator.
    let fill = float bucket.Count / float scale

    Html.div
        [ prop.className [ "overview-item"; accent; if isSelected then "overview-item-selected" ]
          prop.key accent
          prop.onClick (fun _ -> onSelectGroup target)
          prop.children
              [ metaLine accent (taskLabel bucket.Kind) bucket.Count
                Html.div
                    [ prop.className ("overview-bar " + accent)
                      prop.style [ style.custom ("--bar-fill", string fill) ] ] ] ]

/// "1 agent" / "3 agents": count + word, pluralized, for the muted breakdown summary line.
let private plural (n: int) (word: string) = $"""{n} {word}{if n = 1 then "" else "s"}"""

/// Group a group's members by owning repo IDENTITY (RepoId), PRESERVING the aggregate's repo/worktree
/// order (members from one repo arrive contiguous, so folding keeps first-appearance repo order).
/// Grouping on RepoId — not the display name — keeps two distinct repos that happen to share a folder
/// name in separate blocks (matches how the rest of the client keys repo identity). Each entry is the
/// repo's stable id (for React keys), its display name, and its members in order.
let private membersByRepo (members: GroupMember list) : (RepoId * string * GroupMember list) list =
    members
    |> List.fold (fun acc m ->
        match acc with
        | (repoId, name, ms) :: rest when repoId = m.RepoId -> (repoId, name, m :: ms) :: rest
        | _ -> (m.RepoId, m.RepoName, [ m ]) :: acc) []
    |> List.map (fun (repoId, name, ms) -> repoId, name, List.rev ms)
    |> List.rev

/// The shared black breakdown-panel shell: an accent-tinted title, a muted summary, and the ✕ close
/// button, above the repo-grouped member blocks. `accent` tints the title and, via currentColor, the
/// chip dots / task bars. The ✕ raises onClose (App re-selects the group, toggling the panel shut).
let private breakdownPanel
    (accent: string)
    (title: string)
    (sub: string)
    (onClose: unit -> unit)
    (repoBlocks: ReactElement list)
    =
    let head =
        Html.div
            [ prop.className "overview-bd-head"
              prop.children
                  [ Html.span [ prop.className "overview-bd-title"; prop.text title ]
                    Html.span [ prop.className "overview-bd-sub"; prop.text sub ]
                    Html.button
                        [ prop.className "overview-bd-close"
                          prop.title "Close (Esc)"
                          prop.onClick (fun _ -> onClose ())
                          prop.text "\u2715" ] ] ]

    Html.div
        [ prop.className [ "overview-breakdown"; accent ]
          prop.children (head :: repoBlocks) ]

/// The small uppercase muted repo name introducing each repo's members (band-header style).
let private repoNameLabel (name: string) =
    Html.div [ prop.className "overview-bd-repo-name"; prop.text name ]

/// Agent breakdown for one selected agent group: per repo, borderless [● branch] chips (one per
/// member worktree, dot in the group's activity colour). Clicking a chip focuses that worktree with
/// arrow-nav parity (onSelectWorktree). The ✕ re-selects the group to close the panel.
let private agentBreakdown
    (onSelectGroup: OverviewSelection -> unit)
    (onSelectWorktree: string -> unit)
    (group: AgentGroup)
    =
    let accent = agentClass group.Kind
    let repoBlocks =
        membersByRepo group.Members
        |> List.map (fun (repoId, repoName, members) ->
            Html.div
                [ prop.className "overview-bd-repo"
                  prop.key (RepoId.value repoId)
                  prop.children
                      [ repoNameLabel repoName
                        Html.div
                            [ prop.className "overview-chips"
                              prop.children (
                                  members
                                  |> List.map (fun m ->
                                      Html.div
                                          [ prop.className [ "overview-chip"; accent ]
                                            prop.key m.ScopedKey
                                            prop.onClick (fun _ -> onSelectWorktree m.ScopedKey)
                                            prop.children
                                                [ Html.span [ prop.className "overview-chip-dot" ]
                                                  Html.span [ prop.className "overview-chip-name"; prop.text m.Branch ] ] ])) ] ] ])

    let repoCount = group.Members |> List.map _.RepoId |> List.distinct |> List.length
    let agentsPart = plural group.Count "agent"
    let reposPart = plural repoCount "repo"
    let sub = $"{agentsPart} · {reposPart}"
    breakdownPanel accent (agentLabel group.Kind) sub (fun () -> onSelectGroup (OverviewSelection.Agents group.Kind)) repoBlocks

/// Task breakdown for one selected task bucket: per repo, one `branch + bar` row per member worktree.
/// The bar is the bucket colour and sized on the SAME shared scale as the band bars above
/// (`--bar-fill = Contribution / Scale`, floored by CSS min-width), so one task is the same pixel
/// width here as in the band. Clicking a row focuses that worktree; the ✕ closes the panel.
let private taskBreakdown
    (onSelectGroup: OverviewSelection -> unit)
    (onSelectWorktree: string -> unit)
    (scale: int)
    (bucket: TaskBucket)
    =
    let accent = taskClass bucket.Kind
    let repoBlocks =
        membersByRepo bucket.Members
        |> List.map (fun (repoId, repoName, members) ->
            Html.div
                [ prop.className "overview-bd-repo"
                  prop.key (RepoId.value repoId)
                  prop.children (
                      repoNameLabel repoName
                      :: (members
                          |> List.map (fun m ->
                              let fill = float m.Contribution / float scale

                              Html.div
                                  [ prop.className "overview-task-row"
                                    prop.key m.ScopedKey
                                    prop.onClick (fun _ -> onSelectWorktree m.ScopedKey)
                                    prop.children
                                        [ Html.span [ prop.className "overview-task-name"; prop.text m.Branch ]
                                          Html.span
                                              [ prop.className "overview-task-track"
                                                prop.children
                                                    [ Html.span
                                                          [ prop.className [ "overview-task-bar"; accent ]
                                                            prop.style [ style.custom ("--bar-fill", string fill) ] ] ] ] ] ]))) ])

    let tasksPart = plural bucket.Count "task"
    let worktreesPart = plural bucket.Members.Length "worktree"
    let sub = $"{tasksPart} · {worktreesPart}"
    breakdownPanel accent (taskLabel bucket.Kind) sub (fun () -> onSelectGroup (OverviewSelection.Tasks bucket.Kind)) repoBlocks

/// A section shell: an uppercase header over the single-line row of category columns, plus the
/// (optional) drill-down breakdown panel rendered INSIDE the section, flush beneath its row — so the
/// agent breakdown sits between the agents row and the Tasks section, and the task breakdown sits
/// directly below the Tasks row (Html.none when nothing in this section is selected). The stacked
/// layout + dashed separator live in CSS.
let private section (header: string) (columns: ReactElement list) (breakdown: ReactElement) =
    Html.div
        [ prop.className "overview-section"
          prop.children
              [ Html.div [ prop.className "overview-header"; prop.text header ]
                Html.div [ prop.className "overview-items"; prop.children columns ]
                breakdown ] ]

/// Whether an Overview drill-down selection still maps to a present (non-empty) group in the given
/// repos' fresh roll-up. Empty groups are dropped by aggregate, so a selection is stale once its
/// group's count hits 0 — App's DataLoaded reducer uses this to clear the selection and close the
/// panel. Lives here because it runs the exact same `repos |> List.map toRepoWorktrees |>
/// OverviewData.aggregate` pipeline the view does — a pure Overview data query, not App/Elmish state.
let overviewSelectionPresent (selection: OverviewSelection) (repos: RepoModel list) =
    let overview = repos |> List.map toRepoWorktrees |> OverviewData.aggregate
    match selection with
    | OverviewSelection.Agents kind -> overview.Agents |> List.exists (fun g -> g.Kind = kind)
    | OverviewSelection.Tasks kind -> overview.Tasks |> List.exists (fun b -> b.Kind = kind)

/// Render the Overview band for the current repos. Returns Html.none when the whole roll-up is empty
/// so the band adds no chrome (not even margin) when there is nothing to show. `selection` is the
/// currently drilled-down group (if any); `onSelectGroup` toggles a group's selection when its column
/// is clicked, and `onSelectWorktree` (used by the breakdown panel) focuses a member card with
/// arrow-nav parity.
let view
    (selection: OverviewSelection option)
    (onSelectGroup: OverviewSelection -> unit)
    (onSelectWorktree: string -> unit)
    (repos: RepoModel list)
    : ReactElement =
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

                        section header (groups |> List.map (agentColumn selection onSelectGroup)) (
                            match selection with
                            | Some (OverviewSelection.Agents kind) ->
                                groups
                                |> List.tryFind (fun g -> g.Kind = kind)
                                |> Option.map (agentBreakdown onSelectGroup onSelectWorktree)
                                |> Option.defaultValue Html.none
                            | _ -> Html.none)
                    match tasks with
                    | [] -> Html.none
                    | buckets ->
                        section
                            "Tasks · across all worktrees"
                            (buckets |> List.map (taskColumn selection onSelectGroup overview.Scale))
                            (match selection with
                             | Some (OverviewSelection.Tasks kind) ->
                                 buckets
                                 |> List.tryFind (fun b -> b.Kind = kind)
                                 |> Option.map (taskBreakdown onSelectGroup onSelectWorktree overview.Scale)
                                 |> Option.defaultValue Html.none
                             | _ -> Html.none) ] ]
