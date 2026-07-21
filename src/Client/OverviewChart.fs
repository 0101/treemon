module OverviewChart

// Pure builders for the Overview band's in-band history chart (spec: docs/spec/overview-activity-history.md).
// Turn an OverviewSnapshot list + a selected window (12h / 24h / 72h) into a STACKED, STEPPED inline SVG area
// chart plus a legend, reusing the band's existing task-*/activity-* accent classes so a bucket's live
// bar/circle and its history area share one colour (area fill = currentColor, tinted by the accent class).
//
// Geometry uses the prototype's fixed 760x170 viewBox, with a 2px minimum for every present series,
// LEFT-EDGE CARRY (a window opening mid-gap starts with the snapshot active at the window start), stepped
// stacked areas (each value holds until the next logged change, so irregular gaps produce uneven step
// widths), a RIGHT-EDGE HOLD flat to "now", and EMPTY SERIES OMITTED from both the chart and the legend.
//
// Pure and Fable-safe: no Model dependency. Inline SVG geometry is the documented dynamic-value exception
// (same as the band's proportional bar widths). Static geometry is cached independently from the local,
// frame-coalesced hover state so dashboard ticks and pointer movement only update the crosshair + tooltip.

open System
open Shared
open OverviewData
open OverviewPresentation
open Feliz
open Fable.Core.JsInterop

let [<Literal>] private w = 760.0
let [<Literal>] private h = 170.0
let [<Literal>] private padL = 34.0
let [<Literal>] private padR = 8.0
let [<Literal>] private padT = 10.0
let [<Literal>] private padB = 22.0
let [<Literal>] private minimumSeriesHeight = 2.0
let [<Literal>] private minimumSeriesWidth = 2.0
let private plotW = w - padL - padR
let private plotH = h - padT - padB

/// One chart series: its display label, the band accent class that tints both its legend swatch and its
/// stacked area (via currentColor), and how to read its count out of a snapshot.
type private SeriesDef =
    { Label: string
      Accent: string
      ValueAt: OverviewSnapshot -> int }

[<RequireQualifiedAccess>]
type ChartKind =
    | Agents
    | Tasks

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
// mirroring OverviewData.agentGroupOrder and the band's palette.
let private agentDefs : SeriesDef list =
    [ AgentGroupKind.Activity CurrentActivity.Investigating
      AgentGroupKind.Activity CurrentActivity.Planning
      AgentGroupKind.Activity CurrentActivity.Executing
      AgentGroupKind.Activity CurrentActivity.Reviewing
      AgentGroupKind.Activity CurrentActivity.PR
      AgentGroupKind.Activity CurrentActivity.Working
      AgentGroupKind.Waiting
      AgentGroupKind.Idle ]
    |> List.map (fun kind -> { Label = agentLabel kind; Accent = agentClass kind; ValueAt = agentCount kind })

// Task series in canonical stacking order, mirroring OverviewData.taskOrder and the band's task-* palette.
let private taskDefs : SeriesDef list =
    [ TaskBucketKind.Planned
      TaskBucketKind.Queued
      TaskBucketKind.InProgress
      TaskBucketKind.Blocked
      TaskBucketKind.Done
      TaskBucketKind.Unattended ]
    |> List.map (fun kind -> { Label = taskLabel kind; Accent = taskClass kind; ValueAt = taskCount kind })

let private definitions =
    function
    | ChartKind.Agents -> agentDefs
    | ChartKind.Tasks -> taskDefs

let windowAxisLabels window =
    let hoursTotal = (HistoryWindow.duration window).TotalHours

    [ 0.0; 0.25; 0.5; 0.75; 1.0 ]
    |> List.map (fun fraction ->
        let hoursAgo = int (Math.Round(hoursTotal * (1.0 - fraction)))
        if hoursAgo = 0 then "now" else $"-{hoursAgo}h")

/// One plotted point: its fraction along the window (0 = window start, 1 = now) and its per-series counts
/// aligned to the given `defs`.
type Point = { Fraction: float; Counts: int list }

/// Scope the snapshots to the window [now - window, now], carrying the value active at the LEFT edge
/// (stepped semantics — a window opening mid-gap starts from the last snapshot before it, or the first
/// snapshot if none precedes it) and appending a RIGHT-edge point at `now` that holds the last value flat.
/// Returns [] only when there is no history at all. Pure — see the public `agentPoints`/`taskPoints` seams.
let private buildPoints (defs: SeriesDef list) (now: DateTimeOffset) (window: TimeSpan) (snapshots: OverviewSnapshot list) : Point list =
    let start = now - window
    let sorted = snapshots |> List.sortBy _.Timestamp
    let before = sorted |> List.filter (fun s -> s.Timestamp <= start) |> List.tryLast
    let inside = sorted |> List.filter (fun s -> s.Timestamp > start)
    let countsOf (s: OverviewSnapshot) = defs |> List.map (fun d -> d.ValueAt s)

    let headCounts =
        match before, inside with
        | Some b, _ -> Some(countsOf b)
        | None, first :: _ -> Some(countsOf first)
        | None, [] -> None

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
/// Investigating, Planning, Executing, Reviewing, PR, Working, Waiting). Pure test/reuse seam.
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

/// The active stepped point selected for a cursor position. PointIndex is stable for the lifetime of one
/// geometry build and is the equality key used to suppress hover updates inside the same sampled interval.
type HoverSample =
    { PointIndex: int
      Fraction: float
      Tooltip: TooltipModel }

// The relative-time header for a snapped point, derived from its window-fraction (minutes-ago =
// window * (1 - fraction)); mirrors the prototype's "Hh Mm ago" / "Mm ago" ladder, with 0 -> "now".
let private relativeLabel (window: TimeSpan) (fraction: float) =
    let minutesAgo = int (Math.Round(window.TotalMinutes * (1.0 - fraction)))
    let hh = minutesAgo / 60
    let mm = minutesAgo % 60
    if hh > 0 then $"{hh}h {mm}m ago"
    elif minutesAgo = 0 then "now"
    else $"{mm}m ago"

let private tooltipForPoint (defs: SeriesDef list) (window: TimeSpan) (point: Point) =
    let rows =
        List.zip defs point.Counts
        |> List.choose (fun (d, c) ->
            if c > 0 then Some { Label = d.Label; Accent = d.Accent; Count = c } else None)

    { RelativeLabel = relativeLabel window point.Fraction
      Rows = rows
      Total = List.sum point.Counts }

let private hoverSampleForPoint defs window index (point: Point) =
    { PointIndex = index
      Fraction = point.Fraction
      Tooltip = tooltipForPoint defs window point }

let private sampleAt (samples: HoverSample array) (cursorFraction: float) =
    let rec search low high best =
        if low > high then
            best
        else
            let middle = low + (high - low) / 2

            if samples[middle].Fraction <= cursorFraction then
                search (middle + 1) high (Some samples[middle])
            else
                search low (middle - 1) best

    match samples with
    | [||] -> None
    | _ -> search 0 (samples.Length - 1) None |> Option.orElse (Some samples[0])

/// Snap to the ACTIVE stepped snapshot for a cursor fraction. The returned point index is the component's
/// same-sample suppression key; the tooltip and crosshair both use the returned fraction.
let hoverSampleAt chartKind (window: TimeSpan) (points: Point list) (cursorFraction: float) : HoverSample option =
    let defs = definitions chartKind

    points
    |> List.mapi (hoverSampleForPoint defs window)
    |> List.toArray
    |> fun samples -> sampleAt samples cursorFraction

/// Tooltip-only compatibility seam over hoverSampleAt.
let tooltipAt chartKind (window: TimeSpan) (points: Point list) (cursorFraction: float) : TooltipModel option =
    hoverSampleAt chartKind window points cursorFraction |> Option.map _.Tooltip

/// The complete key for one static geometry build. Snapshot identity is intentionally significant: an
/// unrelated parent render preserves the same list object, while a newly installed response supplies a new
/// input even when its values happen to compare equal.
type GeometryInput =
    { ChartKind: ChartKind
      HistoryWindow: HistoryWindow
      Anchor: DateTimeOffset
      Snapshots: OverviewSnapshot list }

type GeometryMemo<'geometry> =
    private
        { Input: GeometryInput
          Geometry: 'geometry
          BuildCount: int }

let private sameGeometryInput previous current =
    previous.ChartKind = current.ChartKind
    && previous.HistoryWindow = current.HistoryWindow
    && previous.Anchor = current.Anchor
    && Object.ReferenceEquals(box previous.Snapshots, box current.Snapshots)

/// Pure memoization seam for tests and non-React callers. Production uses the component-local geometry
/// hook below; tests can use a cheap fake builder and assert exactly when the build count changes.
let memoizedGeometry build input current =
    match current with
    | Some memo when sameGeometryInput memo.Input input -> memo
    | previous ->
        { Input = input
          Geometry = build input
          BuildCount = previous |> Option.map (fun memo -> memo.BuildCount + 1) |> Option.defaultValue 1 }

let geometryValue memo = memo.Geometry
let geometryBuildCount memo = memo.BuildCount

/// One pending requestAnimationFrame slot. Repeated pointer moves replace Pending without requesting a
/// second frame; taking the frame returns only the latest candidate and opens the slot for the next frame.
type HoverFrameQueue<'candidate> =
    private
    | Empty
    | Scheduled of 'candidate

let emptyHoverFrameQueue () = Empty

let queueHoverFrame candidate queue =
    match queue with
    | Empty -> Scheduled candidate, true
    | Scheduled _ -> Scheduled candidate, false

let takeHoverFrame queue =
    match queue with
    | Empty -> None, Empty
    | Scheduled candidate -> Some candidate, Empty

let shouldUpdateHover currentPointIndex nextPointIndex =
    currentPointIndex <> Some nextPointIndex

// Round a coordinate to an integer string (culture-invariant — integers carry no decimal separator), so
// the emitted path/attribute geometry is deterministic under both Fable and the .NET test compile.
let private ix (v: float) = string (int (Math.Round v))

/// Project counts into pixel heights while keeping every present series visible. Heights below 2px
/// borrow only from larger series; when the whole stack is shorter than the combined minimum, it expands.
let seriesPixelHeights (totalHeight: float) (counts: int list) : float list =
    let totalCount = List.sum counts

    if totalCount = 0 then
        counts |> List.map (fun _ -> 0.0)
    else
        let rawHeights =
            counts
            |> List.map (fun count ->
                if count > 0 then totalHeight * float count / float totalCount else 0.0)

        let deficit =
            List.zip counts rawHeights
            |> List.sumBy (fun (count, height) ->
                if count > 0 then max 0.0 (minimumSeriesHeight - height) else 0.0)

        let available =
            rawHeights
            |> List.sumBy (fun height -> max 0.0 (height - minimumSeriesHeight))

        if deficit <= 0.0 then
            rawHeights
        elif available >= deficit then
            List.map2
                (fun count height ->
                    if count <= 0 then 0.0
                    elif height < minimumSeriesHeight then minimumSeriesHeight
                    else height - deficit * (height - minimumSeriesHeight) / available)
                counts
                rawHeights
        else
            counts
            |> List.map (fun count -> if count > 0 then minimumSeriesHeight else 0.0)

type IntervalMarker =
    { X: float
      Width: float
      Lower: float
      Upper: float }

/// Add a 2px-wide marker when a present stepped interval is too short to survive x-coordinate rounding.
let minimumIntervalMarkers
    (plotLeft: float)
    (plotRight: float)
    (xs: float[])
    (lower: float[])
    (upper: float[])
    : IntervalMarker list =
    if xs.Length < 2 then
        []
    else
        let intervals =
            [ 0 .. xs.Length - 2 ]
            |> List.map (fun index ->
                let height = upper[index] - lower[index]

                if height > 0.0 then
                    Some(xs[index], xs[index + 1], lower[index], upper[index])
                else
                    None)

        let reversedRuns, currentRun =
            intervals
            |> List.fold
                (fun (runs, current) interval ->
                    match interval, current with
                    | Some present, _ -> runs, present :: current
                    | None, [] -> runs, []
                    | None, present -> List.rev present :: runs, [])
                ([], [])

        let runs =
            (match currentRun with
             | [] -> reversedRuns
             | present -> List.rev present :: reversedRuns)
            |> List.rev

        runs
        |> List.choose (fun run ->
            let startX, _, _, _ = List.head run
            let _, endX, _, _ = List.last run
            let intervalWidth = endX - startX
            let _, _, lower, upper =
                run
                |> List.maxBy (fun (_, _, lower, upper) -> upper - lower)

            if intervalWidth >= minimumSeriesWidth then
                None
            else
                let center = (startX + endX) / 2.0
                let x = max plotLeft (min (plotRight - minimumSeriesWidth) (center - minimumSeriesWidth / 2.0))

                Some
                    { X = x
                      Width = minimumSeriesWidth
                      Lower = lower
                      Upper = upper })

/// Keep the last point that renders into each x pixel. Multiple status changes inside one pixel cannot
/// be drawn as separate areas; retaining all of them makes the closed stacked paths self-intersect.
let lastPointPerPixel (xs: float[]) : int[] =
    xs
    |> Array.indexed
    |> Array.fold
        (fun grouped (index, x) ->
            let pixel = int (Math.Round x)

            match grouped with
            | (previousPixel, _) :: rest when previousPixel = pixel -> (pixel, index) :: rest
            | _ -> (pixel, index) :: grouped)
        []
    |> List.rev
    |> List.map snd
    |> List.toArray

type private ChartGeometry =
    { HoverSamples: HoverSample array
      StaticSvgElements: ReactElement list
      LegendElements: ReactElement list }

let private xOf (fraction: float) =
    padL + fraction * plotW

let private chartTitle =
    function
    | ChartKind.Agents -> "Active agents"
    | ChartKind.Tasks -> "Tasks"

let private buildChartGeometry input =
    let window = HistoryWindow.duration input.HistoryWindow
    let defs = definitions input.ChartKind
    let points = buildPoints defs input.Anchor window input.Snapshots |> List.toArray
    let pointCount = points.Length

    // Y scale: the tallest stacked total, rounded UP to an even number (so the mid gridline is a whole
    // number), floored at 2 so an all-empty window still draws a sane baseline.
    let maxY = points |> Array.map (fun point -> List.sum point.Counts) |> Array.fold max 0 |> max 1
    let yTop = float (((maxY + 1) / 2) * 2)
    let yOf value = padT + plotH - (value / yTop) * plotH
    let yOfHeight height = padT + plotH - height
    let xs = points |> Array.map (fun point -> xOf point.Fraction)

    let heights =
        points
        |> Array.map (fun point ->
            let totalHeight = float (List.sum point.Counts) / yTop * plotH
            seriesPixelHeights totalHeight point.Counts |> List.toArray)

    let gridElements =
        [ 0.0; yTop / 2.0; yTop ]
        |> List.collect (fun gridValue ->
            let y = yOf gridValue

            [ Svg.line
                  [ svg.className "grid-line"
                    svg.x1 (int (Math.Round padL))
                    svg.y1 (int (Math.Round y))
                    svg.x2 (int (Math.Round(w - padR)))
                    svg.y2 (int (Math.Round y)) ]
              Svg.text
                  [ svg.className "axis-label"
                    svg.x (int (Math.Round(padL - 6.0)))
                    svg.y (int (Math.Round(y + 3.0)))
                    svg.textAnchor.endOfText
                    svg.text (string (int gridValue)) ] ])

    let axisElements =
        List.zip [ 0.0; 0.25; 0.5; 0.75; 1.0 ] (windowAxisLabels input.HistoryWindow)
        |> List.map (fun (fraction, label) ->
            let anchor =
                if fraction = 0.0 then svg.textAnchor.startOfText
                elif fraction = 1.0 then svg.textAnchor.endOfText
                else svg.textAnchor.middle

            Svg.text
                [ svg.className "axis-label axis-label-x"
                  svg.x (int (Math.Round(xOf fraction)))
                  svg.y (int (Math.Round(h - 6.0)))
                  anchor
                  svg.text label ])

    let buildPath (pathXs: float[]) (accent: string) (lower: float[]) (upper: float[]) =
        let pathLength = pathXs.Length

        let stepSegments =
            [ for index in 1 .. pathLength - 1 ->
                  $"L {ix pathXs[index]} {ix (yOfHeight upper[index - 1])} L {ix pathXs[index]} {ix (yOfHeight upper[index])}" ]

        let downSegments =
            [ for index in pathLength - 1 .. -1 .. 1 ->
                  $"L {ix pathXs[index]} {ix (yOfHeight lower[index])} L {ix pathXs[index]} {ix (yOfHeight lower[index - 1])}" ]

        let path =
            String.concat
                " "
                ([ $"M {ix pathXs[0]} {ix (yOfHeight upper[0])}" ]
                 @ stepSegments
                 @ downSegments
                 @ [ $"L {ix pathXs[0]} {ix (yOfHeight lower[0])} Z" ])

        Svg.path
            [ svg.className accent
              svg.d path
              svg.fill "currentColor"
              svg.fillOpacity 0.82 ]

    let stackedSeries =
        if pointCount = 0 then
            []
        else
            let _, reversed =
                defs
                |> List.indexed
                |> List.fold
                    (fun (lower: float[], acc) (index, definition) ->
                        let values = heights |> Array.map (fun point -> point[index])
                        let upper = Array.init pointCount (fun pointIndex -> lower[pointIndex] + values[pointIndex])
                        upper, (definition.Accent, values, lower, upper) :: acc)
                    (Array.zeroCreate pointCount, [])

            reversed
            |> List.rev
            |> List.filter (fun (_, values, _, _) -> values |> Array.exists (fun value -> value > 0.0))

    let renderIndices = lastPointPerPixel xs
    let renderXs = renderIndices |> Array.map (fun index -> xs[index])
    let atRenderPoints (values: float[]) = renderIndices |> Array.map (fun index -> values[index])

    let areaElements =
        stackedSeries
        |> List.map (fun (accent, _, lower, upper) ->
            buildPath renderXs accent (atRenderPoints lower) (atRenderPoints upper))

    let markerElements =
        stackedSeries
        |> List.collect (fun (accent, _, lower, upper) ->
            minimumIntervalMarkers padL (padL + plotW) xs lower upper
            |> List.map (fun marker ->
                let x = marker.X + marker.Width / 2.0

                Svg.line
                    [ svg.className accent
                      svg.x1 x
                      svg.y1 (yOfHeight marker.Upper)
                      svg.x2 x
                      svg.y2 (yOfHeight marker.Lower)
                      svg.stroke "currentColor"
                      svg.strokeWidth marker.Width
                      svg.custom ("strokeLinecap", "butt")
                      svg.custom ("vector-effect", "non-scaling-stroke") ]))

    let legendElements =
        if pointCount = 0 then
            []
        else
            defs
            |> List.indexed
            |> List.choose (fun (index, definition) ->
                if points |> Array.exists (fun point -> List.item index point.Counts > 0) then
                    Some(
                        Html.span
                            [ prop.className definition.Accent
                              prop.children
                                  [ Html.span [ prop.className "swatch" ]
                                    Html.span [ prop.className "chart-legend-label"; prop.text definition.Label ] ] ]
                    )
                else
                    None)

    let hoverSamples =
        points
        |> Array.mapi (hoverSampleForPoint defs window)

    { HoverSamples = hoverSamples
      StaticSvgElements = gridElements @ areaElements @ markerElements @ axisElements
      LegendElements = legendElements }

type private HoverState =
    { Sample: HoverSample
      GeometryBuildCount: int }

type private ScheduledFrame =
    { Id: float
      GeometryBuildCount: int }

type private GeometryBuild =
    { Geometry: ChartGeometry
      BuildCount: int }

let private useChartGeometry input =
    let chartKindKey =
        match input.ChartKind with
        | ChartKind.Agents -> 0
        | ChartKind.Tasks -> 1

    let historyWindowKey =
        match input.HistoryWindow with
        | HistoryWindow.Hours12 -> 12
        | HistoryWindow.Hours24 -> 24
        | HistoryWindow.Hours72 -> 72

    let dependencies =
        [| box chartKindKey
           box historyWindowKey
           box (float (input.Anchor.ToUnixTimeMilliseconds()))
           box input.Snapshots |]

    let geometry =
        React.useMemo(
            (fun () -> buildChartGeometry input),
            dependencies
        )

    let trackedBuild, setTrackedBuild =
        React.useState (fun () ->
            { Geometry = geometry
              BuildCount = 1 })

    if Object.ReferenceEquals(box trackedBuild.Geometry, box geometry) then
        trackedBuild
    else
        let nextBuild =
            { Geometry = geometry
              BuildCount = trackedBuild.BuildCount + 1 }

        setTrackedBuild nextBuild
        nextBuild

let private useFrameCoalescedHover geometryBuildCount =
    let hover, updateHover = React.useStateWithUpdater (None: HoverState option)
    let frameQueue = React.useRef<HoverFrameQueue<HoverSample>>(emptyHoverFrameQueue ())
    let scheduledFrame = React.useRef<ScheduledFrame option>(None)

    let cancelFrame frame =
        Browser.Dom.window?cancelAnimationFrame(frame.Id)

    let clearPendingFrame () =
        scheduledFrame.current |> Option.iter cancelFrame
        scheduledFrame.current <- None
        frameQueue.current <- emptyHoverFrameQueue ()

    React.useEffect(
        (fun () -> React.createDisposable clearPendingFrame),
        [| box geometryBuildCount |]
    )

    let commitSample sample =
        updateHover (fun current ->
            let currentForGeometry =
                current
                |> Option.filter (fun state -> state.GeometryBuildCount = geometryBuildCount)

            let currentPointIndex = currentForGeometry |> Option.map _.Sample.PointIndex

            if shouldUpdateHover currentPointIndex sample.PointIndex then
                Some
                    { Sample = sample
                      GeometryBuildCount = geometryBuildCount }
            else
                current)

    let flushHoverFrame (_: float) =
        scheduledFrame.current <- None
        let sample, remaining = takeHoverFrame frameQueue.current
        frameQueue.current <- remaining
        sample |> Option.iter commitSample

    let queueSample sample =
        match scheduledFrame.current with
        | Some frame when frame.GeometryBuildCount <> geometryBuildCount ->
            cancelFrame frame
            scheduledFrame.current <- None
            frameQueue.current <- emptyHoverFrameQueue ()
        | _ -> ()

        let nextQueue, shouldSchedule = queueHoverFrame sample frameQueue.current
        frameQueue.current <- nextQueue

        if shouldSchedule then
            let frameId: float = Browser.Dom.window?requestAnimationFrame(flushHoverFrame)

            scheduledFrame.current <-
                Some
                    { Id = frameId
                      GeometryBuildCount = geometryBuildCount }

    let clearHover () =
        clearPendingFrame ()
        updateHover (fun current -> if current.IsSome then None else current)

    hover |> Option.filter (fun state -> state.GeometryBuildCount = geometryBuildCount), queueSample, clearHover

/// Render one stacked stepped-area chart with its legend and crosshair tooltip. Static paths, markers,
/// axes, and legend elements are built only when the chart kind, selected window, server anchor, or
/// snapshot-list identity changes. Hover commits at most the latest pointer candidate once per animation
/// frame and ignores candidates that resolve to the already-visible sampled point.
[<ReactComponent>]
let HistoryChart
    chartKind
    (anchor: DateTimeOffset)
    (historyWindow: HistoryWindow)
    (snapshots: OverviewSnapshot list)
    : ReactElement =
    let input =
        { ChartKind = chartKind
          HistoryWindow = historyWindow
          Anchor = anchor
          Snapshots = snapshots }

    let geometryBuild = useChartGeometry input
    let geometry = geometryBuild.Geometry
    let activeHover, queueSample, clearHover = useFrameCoalescedHover geometryBuild.BuildCount

    let onMove (event: Browser.Types.MouseEvent) =
        if geometry.HoverSamples.Length > 0 then
            let rect = event.currentTarget?getBoundingClientRect ()
            let rectLeft: float = rect?left
            let rectWidth: float = rect?width
            let scaleX = if rectWidth > 0.0 then w / rectWidth else 1.0
            let rawX = (event.clientX - rectLeft) * scaleX
            let cursorX = max padL (min (padL + plotW) rawX)
            let cursorFraction = (cursorX - padL) / plotW

            match sampleAt geometry.HoverSamples cursorFraction with
            | Some sample -> queueSample sample
            | None -> ()

    let onLeave (_: Browser.Types.MouseEvent) =
        clearHover ()

    let crosshairElements =
        activeHover
        |> Option.map (fun state ->
            let snappedX = xOf state.Sample.Fraction

            [ Svg.line
                  [ svg.className "cursor-line"
                    svg.x1 (int (Math.Round snappedX))
                    svg.y1 (int (Math.Round padT))
                    svg.x2 (int (Math.Round snappedX))
                    svg.y2 (int (Math.Round(padT + plotH))) ] ])
        |> Option.defaultValue []

    let title = chartTitle chartKind

    let svgElement =
        Svg.svg
            [ svg.viewBox (0, 0, int w, int h)
              svg.width w
              svg.height h
              svg.custom ("role", "img")
              svg.custom ("aria-label", $"{title} over the last {historyWindowLabel historyWindow}")
              svg.onMouseMove onMove
              svg.onMouseLeave onLeave
              svg.children (geometry.StaticSvgElements @ crosshairElements) ]

    let tooltipElement =
        match activeHover with
        | Some state ->
            let sample = state.Sample
            let model = sample.Tooltip

            let rows =
                model.Rows
                |> List.map (fun row ->
                    Html.div
                        [ prop.className "tip-row"
                          prop.children
                              [ Html.span [ prop.className (row.Accent + " swatch") ]
                                Html.span [ prop.className "tip-label"; prop.text (row.Label + ": ") ]
                                Html.b [ prop.text (string row.Count) ] ] ])

            let leftPercent = xOf sample.Fraction / w * 100.0
            let flipLeft = leftPercent > 62.0
            let transform = if flipLeft then "translateX(-100%)" else "translateX(0)"

            Html.div
                [ prop.className "chart-tip"
                  prop.style
                      [ style.left (length.percent leftPercent)
                        style.top (length.px 12)
                        style.custom ("transform", transform) ]
                  prop.children (
                      [ Html.div [ prop.className "tip-time"; prop.text model.RelativeLabel ] ]
                      @ rows
                      @ [ Html.div [ prop.className "tip-total"; prop.text $"Total: {model.Total}" ] ]
                  ) ]
        | None -> Html.none

    Html.div
        [ prop.className "history-charts"
          prop.custom ("data-geometry-build-count", string geometryBuild.BuildCount)
          prop.children
              [ Html.figure
                    [ prop.children
                          [ Html.div
                                [ prop.className "chart-stage"
                                  prop.children [ svgElement; tooltipElement ] ] ] ]
                Html.div [ prop.className "chart-legend"; prop.children geometry.LegendElements ] ] ]

let agentsChart (window: HistoryWindow) (anchor: DateTimeOffset) (snapshots: OverviewSnapshot list) : ReactElement =
    HistoryChart ChartKind.Agents anchor window snapshots

let tasksChart (window: HistoryWindow) (anchor: DateTimeOffset) (snapshots: OverviewSnapshot list) : ReactElement =
    HistoryChart ChartKind.Tasks anchor window snapshots
