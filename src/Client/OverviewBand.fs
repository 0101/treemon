module OverviewBand

// The chrome-less Overview band (spec: docs/spec/beads-overview-band.md, Corrections v1.1).
//
// A native Feliz view rendered inside the dashboard, above the repo list, gated by the caller on
// Model.OverviewPanelOpen. It consumes the pure cross-worktree roll-up from OverviewData.aggregate
// and paints the prototype's `.band` block (.agents/canvas/beads-panel-prototypes.html) exactly:
// two STACKED sections split by a 1px dashed rule, each opening with an uppercase muted header, each
// category a column whose count+label meta line sits ABOVE its visual (count FIRST in the accent
// colour, label neutral, same size/weight):
//   - Agents        -> a row of ~15px CIRCLES, one per agent, grouped by activity (red-dot working
//                      agents), plus the distinct Waiting group (yellow) and Idle group (blue-dot
//                      agents with an open-but-idle session).
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
open OverviewPresentation
open AppTypes

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
/// group's accent (circle fill = currentColor, driven by the accent class). Each agent with a known
/// context-window occupancy renders as a donut whose arc = fraction of context *remaining* (inline
/// `--ctx-remaining`), so a healthy low-usage agent reads as a nearly full ring and one near its limit
/// thins to a sliver; an agent that hasn't reported usage falls back to the plain solid circle.
/// Clicking the column raises onSelectGroup (App toggles the drill-down selection); when this group is
/// the selected one it renders as the black "tab" (overview-item-selected) sitting flush above its
/// breakdown panel.
let private agentColumn (selection: OverviewSelection option) (onSelectGroup: OverviewSelection -> unit) (group: AgentGroup) =
    let accent = agentClass group.Kind
    let target = OverviewSelection.Agents group.Kind
    let isSelected = selection = Some target

    // One circle per SESSION, tinted to the group accent and — when the session has reported context
    // usage — rendered as a donut filled to its remaining context. Grouping is per session, so each
    // group holds only the sessions actually in that state; every agent member carries at least one
    // matching session. All circles share one uniform gap regardless of which worktree they belong to.
    let sessionCircle (key: string) (s: SessionDot) =
        match s.ContextUsage with
        | Some usage ->
            Html.span
                [ prop.key key
                  prop.className [ "overview-circle"; "overview-donut"; accent ]
                  prop.style [ style.custom ("--ctx-remaining", string (ContextUsage.remainingFraction usage)) ] ]
        | None -> Html.span [ prop.key key; prop.className ("overview-circle " + accent) ]

    let circles =
        group.Members
        |> List.collect (fun m -> m.Sessions |> List.mapi (fun j s -> sessionCircle $"{m.ScopedKey}-{j}" s))

    Html.div
        [ prop.className [ "overview-item"; accent; if isSelected then "overview-item-selected" ]
          prop.key accent
          prop.onClick (fun _ -> onSelectGroup target)
          prop.children
              [ metaLine accent (agentLabel group.Kind) group.Count
                Html.div [ prop.className "overview-circles"; prop.children circles ] ] ]

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

/// The shared black breakdown-panel shell: just the ✕ close button (absolutely positioned so it adds
/// no vertical space), above the repo-grouped member blocks. The title/summary the header used to show
/// only repeated the selected column tab sitting flush above the panel, so they're dropped. `accent`
/// tints the members via currentColor (chip dots / task bars). The ✕ raises onClose (App re-selects
/// the group, toggling the panel shut); Esc and re-clicking the tab close it too.
let private breakdownPanel
    (accent: string)
    (onClose: unit -> unit)
    (repoBlocks: ReactElement list)
    =
    let closeButton =
        Html.button
            [ prop.className "overview-bd-close"
              prop.title "Close (Esc)"
              prop.onClick (fun _ -> onClose ())
              prop.text "\u2715" ]

    Html.div
        [ prop.className [ "overview-breakdown"; accent ]
          prop.children (closeButton :: repoBlocks) ]

/// The small uppercase muted repo name introducing each repo's members (band-header style).
let private repoNameLabel (name: string) =
    Html.div [ prop.className "overview-bd-repo-name"; prop.text name ]

/// The fraction of context used by the most-loaded of a member's sessions, for the drill-down chip's
/// progress-bar background fill (0 when no session has reported usage). Uses the max so the chip
/// reflects the session closest to its limit — the one worth noticing.
let private chipUsedFraction (sessions: SessionDot list) =
    sessions
    |> List.choose _.ContextUsage
    |> List.map ContextUsage.fraction
    |> function
        | [] -> 0.0
        | fractions -> List.max fractions

/// Agent breakdown for one selected agent group: per repo, borderless [branch] chips whose background
/// fills as a subtle progress bar to the member's most-loaded session's context usage. Clicking a chip
/// focuses that worktree with arrow-nav parity (onSelectWorktree). The ✕ re-selects the group to close.
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
                                            prop.style [ style.custom ("--ctx-used", string (chipUsedFraction m.Sessions)) ]
                                            prop.onClick (fun _ -> onSelectWorktree m.ScopedKey)
                                            prop.children
                                                [ Html.span [ prop.className "overview-chip-name"; prop.text m.Branch ]
                                                  match m.Since with
                                                  | Some since ->
                                                      Html.span
                                                          [ prop.className "overview-chip-since"
                                                            prop.text (Components.relativeTimeCompact System.DateTimeOffset.Now since) ]
                                                  | None -> Html.none ] ])) ] ] ])

    breakdownPanel accent (fun () -> onSelectGroup (OverviewSelection.Agents group.Kind)) repoBlocks

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

    breakdownPanel accent (fun () -> onSelectGroup (OverviewSelection.Tasks bucket.Kind)) repoBlocks

/// A section shell: an uppercase header over the (wrapping) row of category columns, plus the
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

/// The band's single history-window cycle button. None is the client-only Hidden state.
let private cycleButton (historyWindow: HistoryWindow option) (onCycleChart: unit -> unit) =
    let label =
        historyWindow
        |> Option.map (fun window -> $"\u25F7 {historyWindowLabel window}")
        |> Option.defaultValue "\u25F7 History"

    Html.div
        [ prop.className "overview-toolbar"
          prop.children
              [ Html.button
                    [ prop.className "history-toggle"
                      prop.ariaPressed (Option.isSome historyWindow)
                      prop.title "Cycle history window (hidden \u2192 12h \u2192 24h \u2192 72h)"
                      prop.onClick (fun _ -> onCycleChart ())
                      prop.text label ] ] ]

/// Render the Overview band for the current repos. Returns Html.none when the whole roll-up is empty
/// so the band adds no chrome (not even margin) when there is nothing to show. `selection` is the
/// currently drilled-down group (if any); `onSelectGroup` toggles a group's selection when its column
/// is clicked, and `onSelectWorktree` (used by the breakdown panel) focuses a member card with
/// arrow-nav parity. `historyWindow`/`onCycleChart` drive the ephemeral in-band history chart's cycle,
/// mutually exclusive with the drill-down. The anchored server response fixes both charts' right edge.
let view
    (selection: OverviewSelection option)
    (onSelectGroup: OverviewSelection -> unit)
    (onSelectWorktree: string -> unit)
    (historyWindow: HistoryWindow option)
    (onCycleChart: unit -> unit)
    (history: InstalledOverviewHistory option)
    (repos: RepoModel list)
    : ReactElement =
    let overview = repos |> List.map toRepoWorktrees |> OverviewData.aggregate
    let chartData =
        match historyWindow, history with
        | Some _, Some installed -> Some(installed.Window, installed.Response)
        | _ -> None

    match overview.Agents, overview.Tasks with
    | [], [] -> Html.none
    | agents, tasks ->
        Html.div
            [ prop.className "overview-band"
              prop.children
                  [ cycleButton historyWindow onCycleChart
                    match agents with
                    | [] -> Html.none
                    | groups ->
                        // Bare uppercase muted section header (CSS upper-cases it): the per-group
                        // counts live in the columns right below, so the header stays just "AGENTS".
                        section "Agents" (groups |> List.map (agentColumn selection onSelectGroup)) (
                            match selection with
                            | Some (OverviewSelection.Agents kind) ->
                                groups
                                |> List.tryFind (fun g -> g.Kind = kind)
                                |> Option.map (agentBreakdown onSelectGroup onSelectWorktree)
                                |> Option.defaultValue Html.none
                            | _ -> Html.none)
                    // Agents history chart, directly under the agents live section (order: agents live ->
                    // agents history -> tasks live -> tasks history). Only when a window is open and the
                    // agents section is present; mutually exclusive with the drill-down (enforced in state).
                    match agents, chartData with
                    | [], _ -> Html.none
                    | _, None -> Html.none
                    | _, Some (window, response) ->
                        OverviewChart.agentsChart window response.Anchor response.Snapshots
                    match tasks with
                    | [] -> Html.none
                    | buckets ->
                        section
                            "Tasks"
                            (buckets |> List.map (taskColumn selection onSelectGroup overview.Scale))
                            (match selection with
                             | Some (OverviewSelection.Tasks kind) ->
                                 buckets
                                 |> List.tryFind (fun b -> b.Kind = kind)
                                 |> Option.map (taskBreakdown onSelectGroup onSelectWorktree overview.Scale)
                                 |> Option.defaultValue Html.none
                             | _ -> Html.none)
                    // Tasks history chart, directly under the tasks live section.
                    match tasks, chartData with
                    | [], _ -> Html.none
                    | _, None -> Html.none
                    | _, Some (window, response) ->
                        OverviewChart.tasksChart window response.Anchor response.Snapshots ] ]
