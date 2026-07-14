module OverviewChart

// Pure builders for the Overview band's in-band history chart (spec: docs/spec/overview-activity-history.md).
// Turn an OverviewSnapshot list + a selected window (24h / 72h) into a STACKED, STEPPED inline SVG area
// chart plus a legend, reusing the band's existing task-*/activity-* accent classes so a bucket's live
// bar/circle and its history area share one colour (area fill = currentColor, tinted by the accent class).
//
// Geometry mirrors .agents/canvas/overview-chart-prototype.html verbatim: a fixed 760x170 viewBox,
// LEFT-EDGE CARRY (a window opening mid-gap starts with the snapshot active at the window start), stepped
// stacked areas (each value holds until the next logged change, so irregular gaps produce uneven step
// widths), a RIGHT-EDGE HOLD flat to "now", and EMPTY SERIES OMITTED from both the chart and the legend.
//
// Pure and Fable-safe: no IO, no Model dependency — just data -> ReactElement. Inline SVG geometry is the
// documented dynamic-value exception (same as the band's proportional bar widths). The crosshair tooltip is
// a separate follow-up (task tm-activity-history-zx6); this module renders the static chart + legend only.

open System
open Shared
open OverviewData
open AppTypes
open Feliz
open Fable.Core.JsInterop

// ── Fixed geometry (verbatim from the prototype) ──
let [<Literal>] private w = 760.0
let [<Literal>] private h = 170.0
let [<Literal>] private padL = 34.0
let [<Literal>] private padR = 8.0
let [<Literal>] private padT = 10.0
let [<Literal>] private padB = 22.0
let private plotW = w - padL - padR
let private plotH = h - padT - padB

/// One chart series: its display label, the band accent class that tints both its legend swatch and its
/// stacked area (via currentColor), and how to read its count out of a snapshot.
type private SeriesDef =
    { Label: string
      Accent: string
      ValueAt: OverviewSnapshot -> int }

// Count of one agent group in a snapshot (0 when the group is absent — empty groups are dropped upstream).
let private agentCount kind (s: OverviewSnapshot) =
    s.Agents
    |> List.tryPick (fun a -> if a.Kind = kind then Some a.Count else None)
    |> Option.defaultValue 0

// Count of one task bucket in a snapshot (0 when the bucket is absent).
let private taskCount kind (s: OverviewSnapshot) =
    s.Tasks
    |> List.tryPick (fun t -> if t.Kind = kind then Some t.Count else None)
    |> Option.defaultValue 0

// Agent series in canonical stacking order (bottom -> top): the activity groups, then Waiting last —
// mirroring OverviewData.agentGroupOrder and the band's palette. Labels/classes mirror OverviewBand.
let private agentDefs : SeriesDef list =
    [ AgentGroupKind.Activity CurrentActivity.Investigating, "Investigating", "activity-investigating"
      AgentGroupKind.Activity CurrentActivity.Planning,      "Planning",      "activity-planning"
      AgentGroupKind.Activity CurrentActivity.Executing,     "Executing",     "activity-executing"
      AgentGroupKind.Activity CurrentActivity.Reviewing,     "Reviewing",     "activity-reviewing"
      AgentGroupKind.Activity CurrentActivity.Fixing,        "Fixing",        "activity-fixing"
      AgentGroupKind.Activity CurrentActivity.Working,       "Working",       "activity-working"
      AgentGroupKind.Waiting,                                "Waiting",       "activity-waiting" ]
    |> List.map (fun (kind, label, accent) -> { Label = label; Accent = accent; ValueAt = agentCount kind })

// Task series in canonical stacking order, mirroring OverviewData.taskOrder and the band's task-* palette.
let private taskDefs : SeriesDef list =
    [ TaskBucketKind.Planned,    "Planned",     "task-planned"
      TaskBucketKind.Queued,     "Queued",      "task-queued"
      TaskBucketKind.InProgress, "In progress", "task-inprogress"
      TaskBucketKind.Blocked,    "Blocked",     "task-blocked"
      TaskBucketKind.Done,       "Done",        "task-done"
      TaskBucketKind.Unattended, "Unattended",  "task-unattended" ]
    |> List.map (fun (kind, label, accent) -> { Label = label; Accent = accent; ValueAt = taskCount kind })

/// The TimeSpan a window scopes to (Hidden collapses to zero — callers gate on non-Hidden before rendering).
let private windowSpan =
    function
    | OverviewChartWindow.Hidden -> TimeSpan.Zero
    | OverviewChartWindow.Hours24 -> TimeSpan.FromHours 24.0
    | OverviewChartWindow.Hours72 -> TimeSpan.FromHours 72.0

/// The window's short label for the chart title (e.g. "24h").
let private windowLabel =
    function
    | OverviewChartWindow.Hidden -> ""
    | OverviewChartWindow.Hours24 -> "24h"
    | OverviewChartWindow.Hours72 -> "72h"

/// One plotted point: its fraction along the window (0 = window start, 1 = now) and its per-series counts
/// aligned to the given `defs`.
type Point = { Fraction: float; Counts: int list }

/// Scope the snapshots to the window [now - window, now], carrying the value active at the LEFT edge
/// (stepped semantics — a window opening mid-gap starts from the last snapshot before it, or the first
/// snapshot if none precedes it) and appending a RIGHT-edge point at `now` that holds the last value flat.
/// Returns [] only when there is no history at all. Pure — see the public `agentPoints`/`taskPoints` seams.
let private buildPoints (defs: SeriesDef list) (now: DateTimeOffset) (window: TimeSpan) (snapshots: OverviewSnapshot list) : Point list =
    let start = now - window
    let sorted = snapshots |> List.sortBy (fun s -> s.Timestamp)
    let before = sorted |> List.filter (fun s -> s.Timestamp <= start) |> List.tryLast
    let inside = sorted |> List.filter (fun s -> s.Timestamp > start)
    let countsOf (s: OverviewSnapshot) = defs |> List.map (fun d -> d.ValueAt s)

    let headCounts =
        match before, inside, sorted with
        | Some b, _, _ -> Some(countsOf b)          // carry the snapshot active at the window start
        | None, first :: _, _ -> Some(countsOf first) // window opens before any history -> carry the first
        | None, [], first :: _ -> Some(countsOf first)
        | None, [], [] -> None

    match headCounts with
    | None -> []
    | Some head ->
        let clamp f = max 0.0 (min 1.0 f)
        let fracOf (t: DateTimeOffset) = clamp ((t - start).TotalMinutes / window.TotalMinutes)
        let insidePts = inside |> List.map (fun s -> { Fraction = fracOf s.Timestamp; Counts = countsOf s })
        let lastCounts =
            match List.tryLast insidePts with
            | Some p -> p.Counts
            | None -> head
        { Fraction = 0.0; Counts = head } :: (insidePts @ [ { Fraction = 1.0; Counts = lastCounts } ])

/// Windowed points for the AGENT series (counts aligned to the canonical agent order
/// Investigating, Planning, Executing, Reviewing, Fixing, Working, Waiting). Pure test/reuse seam.
let agentPoints (now: DateTimeOffset) (window: TimeSpan) (snapshots: OverviewSnapshot list) : Point list =
    buildPoints agentDefs now window snapshots

/// Windowed points for the TASK series (counts aligned to the canonical task order
/// Planned, Queued, In progress, Blocked, Done, Unattended). Pure test/reuse seam.
let taskPoints (now: DateTimeOffset) (window: TimeSpan) (snapshots: OverviewSnapshot list) : Point list =
    buildPoints taskDefs now window snapshots

/// One row of the crosshair tooltip: a non-empty series at the snapped snapshot, carrying its display
/// label, the accent class that tints its swatch, and the count held at that instant.
type TooltipRow = { Label: string; Accent: string; Count: int }

/// The pure model behind the crosshair tooltip (task tm-activity-history-zx6): the series rows visible at
/// the snapped snapshot (each non-empty, in canonical order), the running total, and the relative-time
/// header. Derived purely from the windowed Point list + a cursor fraction so it is unit-testable and
/// Fable-safe — the React component only positions and paints it.
type TooltipModel = { RelativeLabel: string; Rows: TooltipRow list; Total: int }

// The relative-time header for a snapped point, derived from its window-fraction (minutes-ago =
// window * (1 - fraction)); mirrors the prototype's "Hh Mm ago" / "Mm ago" ladder, with 0 -> "now".
let private relativeLabel (window: TimeSpan) (fraction: float) =
    let minutesAgo = int (Math.Round(window.TotalMinutes * (1.0 - fraction)))
    let hh = minutesAgo / 60
    let mm = minutesAgo % 60
    if hh > 0 then $"{hh}h {mm}m ago"
    elif minutesAgo = 0 then "now"
    else $"{mm}m ago"

/// Snap to the ACTIVE stepped snapshot for a cursor fraction and project the crosshair tooltip model: the
/// last point at or before the cursor (the value actually held then — matching the stepped rendering),
/// its non-empty series as rows in canonical order, a total, and a relative-time header. Returns None when
/// there is no history to snap to. `isAgents` selects the canonical series set (matching
/// agentPoints/taskPoints). Pure test/reuse seam — the React component is the only impure caller.
let tooltipAt (isAgents: bool) (window: TimeSpan) (points: Point list) (cursorFraction: float) : TooltipModel option =
    match points with
    | [] -> None
    | head :: _ ->
        let defs = if isAgents then agentDefs else taskDefs
        let snap =
            points
            |> List.filter (fun p -> p.Fraction <= cursorFraction)
            |> List.tryLast
            |> Option.defaultValue head
        let rows =
            List.zip defs snap.Counts
            |> List.choose (fun (d, c) ->
                if c > 0 then Some { Label = d.Label; Accent = d.Accent; Count = c } else None)
        Some
            { RelativeLabel = relativeLabel window snap.Fraction
              Rows = rows
              Total = List.sum snap.Counts }

// Round a coordinate to an integer string (culture-invariant — integers carry no decimal separator), so
// the emitted path/attribute geometry is deterministic under both Fable and the .NET test compile.
let private ix (v: float) = string (int (Math.Round v))

/// Ephemeral hover state for the crosshair tooltip (task tm-activity-history-zx6): the cursor's position
/// in SVG-x (for the dashed guide line) and window-fraction (for snapping to the active snapshot), plus
/// its pixel offset within the figure (for placing the HTML tooltip). Lives only while the pointer is
/// over a chart — reset on mouse-leave, never persisted.
type private HoverState =
    { SvgX: float
      Frac: float
      Px: float
      Py: float
      RectWidth: float }

/// Render one stacked stepped-area chart with its title, legend, and crosshair tooltip for the given
/// series over the window (spec decision #8). `title` is the section name ("Active agents" / "Tasks");
/// `isAgents` selects the canonical series set. When there is no history the chart degrades to a bare
/// baseline (grid + axes, no areas, no hover) rather than erroring.
///
/// Hovering the plot shows a dashed vertical crosshair at the cursor and a tooltip SNAPPED to the active
/// stepped snapshot — the last plotted point at or before the cursor time, matching the stepped rendering
/// — listing each non-empty series as `label: count` with a colour swatch, plus a relative-time header
/// and a total. Rendered as a Feliz React component so the hover state stays local & ephemeral; the pure
/// geometry (buildPoints/agentDefs/taskDefs) is shared with the static path & legend builders above and
/// with the `agentPoints`/`taskPoints` test seams.
[<ReactComponent>]
let HistoryChart (title: string) (winLabel: string) (isAgents: bool) (now: DateTimeOffset) (window: TimeSpan) (snapshots: OverviewSnapshot list) : ReactElement =
    let defs = if isAgents then agentDefs else taskDefs
    let hover, setHover = React.useState (None: HoverState option)
    let pts = buildPoints defs now window snapshots |> List.toArray
    let n = pts.Length

    // Y scale: the tallest stacked total, rounded UP to an even number (so the mid gridline is a whole
    // number), floored at 2 so an all-empty window still draws a sane baseline.
    let maxY = pts |> Array.map (fun p -> List.sum p.Counts) |> Array.fold max 0 |> max 1
    let yTop = float (((maxY + 1) / 2) * 2)

    let xOf (frac: float) = padL + frac * plotW
    let yOf (v: float) = padT + plotH - (v / yTop) * plotH
    let xs = pts |> Array.map (fun p -> xOf p.Fraction)

    // Horizontal gridlines + y labels at 0, mid, top.
    let gridEls =
        [ 0.0; yTop / 2.0; yTop ]
        |> List.collect (fun gy ->
            let yy = yOf gy
            [ Svg.line
                  [ svg.className "grid-line"
                    svg.x1 (int (Math.Round padL))
                    svg.y1 (int (Math.Round yy))
                    svg.x2 (int (Math.Round(w - padR)))
                    svg.y2 (int (Math.Round yy)) ]
              Svg.text
                  [ svg.className "axis-label"
                    svg.x (int (Math.Round(padL - 6.0)))
                    svg.y (int (Math.Round(yy + 3.0)))
                    svg.textAnchor.endOfText
                    svg.text (string (int gy)) ] ])

    // X labels at quarter points, adapting to the window (24h -> -24h..now, 72h -> -72h..now).
    let hoursTotal = window.TotalHours
    let axisEls =
        [ 0.0; 0.25; 0.5; 0.75; 1.0 ]
        |> List.map (fun frac ->
            let hoursAgo = int (Math.Round(hoursTotal * (1.0 - frac)))
            let anchor =
                if frac = 0.0 then svg.textAnchor.startOfText
                elif frac = 1.0 then svg.textAnchor.endOfText
                else svg.textAnchor.middle
            Svg.text
                [ svg.className "axis-label"
                  svg.x (int (Math.Round(xOf frac)))
                  svg.y (int (Math.Round(h - 6.0)))
                  anchor
                  svg.text (if hoursAgo = 0 then "now" else $"-{hoursAgo}h") ])

    // Stacked stepped areas, bottom series first so upper series paint on top. Threads the running lower
    // edge immutably (no let mutable), and OMITS any series that is empty across the whole window.
    let buildPath (accent: string) (lower: float[]) (upper: float[]) =
        let stepSegs =
            [ for i in 1 .. n - 1 ->
                  $"L {ix xs.[i]} {ix (yOf upper.[i - 1])} L {ix xs.[i]} {ix (yOf upper.[i])}" ]
        let downSegs =
            [ for i in n - 1 .. -1 .. 1 ->
                  $"L {ix xs.[i]} {ix (yOf lower.[i])} L {ix xs.[i]} {ix (yOf lower.[i - 1])}" ]
        let d =
            String.concat
                " "
                ([ $"M {ix xs.[0]} {ix (yOf upper.[0])}" ]
                 @ stepSegs
                 @ downSegs
                 @ [ $"L {ix xs.[0]} {ix (yOf lower.[0])} Z" ])
        Svg.path [ svg.className accent; svg.d d; svg.fill "currentColor"; svg.fillOpacity 0.82 ]

    let areaEls =
        if n = 0 then
            []
        else
            let _, revEls =
                defs
                |> List.indexed
                |> List.fold
                    (fun (lower: float[], acc) (k, def) ->
                        let vals = pts |> Array.map (fun p -> float (List.item k p.Counts))
                        let upper = Array.init n (fun i -> lower.[i] + vals.[i])
                        let acc' =
                            if vals |> Array.exists (fun v -> v > 0.0) then buildPath def.Accent lower upper :: acc
                            else acc
                        upper, acc')
                    (Array.zeroCreate n, [])
            List.rev revEls

    // Legend — one entry per series that is non-empty somewhere in the window (matches the omitted areas).
    let legendEls =
        if n = 0 then
            []
        else
            defs
            |> List.indexed
            |> List.choose (fun (k, def) ->
                let has = pts |> Array.exists (fun p -> List.item k p.Counts > 0)
                if has then
                    Some(
                        Html.span
                            [ prop.className def.Accent
                              prop.children
                                  [ Html.span [ prop.className "swatch" ]
                                    Html.span [ prop.className "chart-legend-label"; prop.text def.Label ] ] ]
                    )
                else
                    None)

    // ── Crosshair tooltip (spec decision #8) ──
    // Map a pointer move over the whole SVG into SVG-x + window-fraction + figure-pixel offset (mirrors
    // the prototype: cursor scaled by the viewBox width / rendered width, clamped to the plot). No-op when
    // there is no history to snap to.
    let onMove (ev: Browser.Types.MouseEvent) =
        if n > 0 then
            let rect = ev.currentTarget?getBoundingClientRect ()
            let rectLeft: float = rect?left
            let rectTop: float = rect?top
            let rectWidth: float = rect?width
            let scaleX = if rectWidth > 0.0 then w / rectWidth else 1.0
            let rawX = (ev.clientX - rectLeft) * scaleX
            let svgX = max padL (min (padL + plotW) rawX)
            let frac = (svgX - padL) / plotW

            setHover (
                Some
                    { SvgX = svgX
                      Frac = frac
                      Px = ev.clientX - rectLeft
                      Py = ev.clientY - rectTop
                      RectWidth = rectWidth }
            )

    // Dashed vertical guide line at the cursor (pointer-events:none via CSS so it never steals the move).
    let crosshairEls =
        match hover with
        | Some hv when n > 0 ->
            [ Svg.line
                  [ svg.className "cursor-line"
                    svg.x1 (int (Math.Round hv.SvgX))
                    svg.y1 (int (Math.Round padT))
                    svg.x2 (int (Math.Round hv.SvgX))
                    svg.y2 (int (Math.Round(padT + plotH))) ] ]
        | _ -> []

    let svgEl =
        Svg.svg
            [ svg.viewBox (0, 0, int w, int h)
              svg.width w
              svg.height h
              svg.custom ("role", "img")
              svg.custom ("aria-label", $"{title} over the last {winLabel}")
              svg.onMouseMove onMove
              svg.onMouseLeave (fun _ -> setHover None)
              svg.children (gridEls @ areaEls @ axisEls @ crosshairEls) ]

    // The snapped HTML tooltip, absolutely positioned within the figure. Rows/total/header come from the
    // pure `tooltipAt` seam; the component only positions & paints. Flips left of the cursor near the right
    // edge and sits above it (translateY(-100%)) so the tooltip size never has to be measured.
    let tooltipEl =
        match hover with
        | Some hv ->
            match tooltipAt isAgents window (List.ofArray pts) hv.Frac with
            | Some model ->
                let rows =
                    model.Rows
                    |> List.map (fun r ->
                        Html.div
                            [ prop.className "tip-row"
                              prop.children
                                  [ Html.span [ prop.className (r.Accent + " swatch") ]
                                    Html.span [ prop.className "tip-label"; prop.text (r.Label + ": ") ]
                                    Html.b [ prop.text (string r.Count) ] ] ])

                let flipLeft = hv.Px > hv.RectWidth * 0.62
                let leftPx = if flipLeft then hv.Px - 12.0 else hv.Px + 12.0
                let transformStr = if flipLeft then "translate(-100%, -100%)" else "translate(0, -100%)"

                Html.div
                    [ prop.className "chart-tip"
                      prop.style
                          [ style.left (length.px (int (Math.Round leftPx)))
                            style.top (length.px (int (Math.Round(hv.Py - 8.0))))
                            style.custom ("transform", transformStr) ]
                      prop.children (
                          [ Html.div [ prop.className "tip-time"; prop.text model.RelativeLabel ] ]
                          @ rows
                          @ [ Html.div [ prop.className "tip-total"; prop.text $"Total: {model.Total}" ] ]
                      ) ]
            | None -> Html.none
        | None -> Html.none

    Html.div
        [ prop.className "history-charts"
          prop.children
              [ Html.div
                    [ prop.className "chart-title"
                      prop.children
                          [ Html.text (title + " \u00B7 last ")
                            Html.span [ prop.className "win-label"; prop.text winLabel ] ] ]
                Html.figure [ prop.children [ svgEl; tooltipEl ] ]
                Html.div [ prop.className "chart-legend"; prop.children legendEls ] ] ]

/// The Active-agents history chart for the given window. Returns Html.none when the window is Hidden.
let agentsChart (window: OverviewChartWindow) (now: DateTimeOffset) (snapshots: OverviewSnapshot list) : ReactElement =
    match window with
    | OverviewChartWindow.Hidden -> Html.none
    | _ -> HistoryChart "Active agents" (windowLabel window) true now (windowSpan window) snapshots

/// The Tasks history chart for the given window. Returns Html.none when the window is Hidden.
let tasksChart (window: OverviewChartWindow) (now: DateTimeOffset) (snapshots: OverviewSnapshot list) : ReactElement =
    match window with
    | OverviewChartWindow.Hidden -> Html.none
    | _ -> HistoryChart "Tasks" (windowLabel window) false now (windowSpan window) snapshots
