module Tests.OverviewChartTests

open System
open NUnit.Framework
open Shared
open OverviewData
open OverviewPresentation

// Unit tests for OverviewChart's pure windowing builders (spec: docs/spec/overview-activity-history.md).
// The SVG rendering itself is inline geometry verified by the E2E band tests; here we pin the pure
// point-projection behaviour the chart depends on:
//   - empty history yields no points (the chart degrades to a bare baseline, never an error),
//   - left-edge CARRY: a window opening after the last change starts from that snapshot's value,
//   - right-edge HOLD: the final point sits at fraction 1.0 holding the last value flat to "now",
//   - windowing drops records older than the window (they only survive as the carried left edge),
//   - counts are aligned to the canonical agent/task series order,
//   - every present series receives at least 2px of chart height.
[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type OverviewChartTests() =

    let now = DateTimeOffset(2026, 7, 14, 12, 0, 0, TimeSpan.Zero)
    let window = TimeSpan.FromHours 24.0

    let hoursAgo (n: float) = now - TimeSpan.FromHours n

    // A task-only snapshot: one bucket count, the rest absent (dropped upstream, so 0 in the chart).
    let taskSnap ts kind count : OverviewSnapshot =
        { Timestamp = ts; Tasks = [ { TaskCount.Kind = kind; Count = count } ]; Agents = [] }

    // Index of a bucket in the canonical task order the chart aligns Counts to.
    // Planned, Queued, In progress, Blocked, Done, Unattended.
    let doneIdx = 4

    [<Test>]
    member _.``empty history yields no points`` () =
        Assert.That(OverviewChart.taskPoints now window [], Is.Empty)
        Assert.That(OverviewChart.agentPoints now window [], Is.Empty)

    [<Test>]
    member _.``a window opening after the last change carries that value across both edges`` () =
        // Only history is a change 30h ago — before the 24h window opens. The chart must still start
        // from it (left-edge carry) and hold it flat to now.
        let pts = OverviewChart.taskPoints now window [ taskSnap (hoursAgo 30.0) TaskBucketKind.Done 5 ]

        Assert.That(pts.Length, Is.EqualTo 2)
        Assert.That(pts.Head.Fraction, Is.EqualTo(0.0).Within 1e-9)
        Assert.That((List.last pts).Fraction, Is.EqualTo(1.0).Within 1e-9)
        // Both edges carry Done = 5 (index 4 in the canonical task order).
        for p in pts do
            Assert.That(List.item doneIdx p.Counts, Is.EqualTo 5)

    [<Test>]
    member _.``inside changes are stepped and the last value holds flat to now`` () =
        let pts =
            OverviewChart.taskPoints
                now
                window
                [ taskSnap (hoursAgo 12.0) TaskBucketKind.Done 2
                  taskSnap (hoursAgo 2.0) TaskBucketKind.Done 7 ]

        // No snapshot precedes the window, so the head carries the first inside value (Done = 2) at
        // fraction 0; the two inside changes follow; a final held point sits at fraction 1.0 (Done = 7).
        Assert.That(pts.Length, Is.EqualTo 4)
        Assert.That(pts.Head.Fraction, Is.EqualTo(0.0).Within 1e-9)
        Assert.That(List.item doneIdx pts.Head.Counts, Is.EqualTo 2)

        let last = List.last pts
        Assert.That(last.Fraction, Is.EqualTo(1.0).Within 1e-9)
        Assert.That(List.item doneIdx last.Counts, Is.EqualTo 7)

        // 12h ago in a 24h window lands at fraction 0.5.
        Assert.That(pts[1].Fraction, Is.EqualTo(0.5).Within 1e-9)
        // Fractions are monotonically non-decreasing across [0, 1].
        let fracs = pts |> List.map _.Fraction
        Assert.That(fracs, Is.Ordered)

    [<Test>]
    member _.``records older than the window survive only as the carried left edge`` () =
        let pts =
            OverviewChart.taskPoints
                now
                window
                [ taskSnap (hoursAgo 40.0) TaskBucketKind.Done 1 // before the window
                  taskSnap (hoursAgo 6.0) TaskBucketKind.Done 9 ] // inside the window

        // The 40h-old record is NOT a plotted point — it is only carried to the left edge; the inside
        // change and the held right edge make three points total.
        Assert.That(pts.Length, Is.EqualTo 3)
        Assert.That(List.item doneIdx pts.Head.Counts, Is.EqualTo 1) // carried left edge
        Assert.That(pts[1].Fraction, Is.EqualTo(0.75).Within 1e-9) // 6h ago in a 24h window
        Assert.That(List.item doneIdx (List.last pts).Counts, Is.EqualTo 9) // held right edge

    [<Test>]
    member _.``counts align to the canonical series order`` () =
        let agentSnap : OverviewSnapshot =
            { Timestamp = hoursAgo 1.0
              Tasks = []
              Agents = [ { AgentCount.Kind = AgentGroupKind.Activity CurrentActivity.Executing; Count = 3 } ] }

        let pts = OverviewChart.agentPoints now window [ agentSnap ]
        // Agent order: Investigating, Planning, Executing, Reviewing, PR, Working, Waiting, Idle.
        Assert.That(pts.Head.Counts.Length, Is.EqualTo 8)
        Assert.That(List.item 2 pts.Head.Counts, Is.EqualTo 3) // Executing is index 2
        Assert.That(pts.Head.Counts |> List.sum, Is.EqualTo 3) // every other series is empty

    [<Test>]
    member _.``small series borrow enough height to remain visible`` () =
        let heights = OverviewChart.seriesPixelHeights 100.0 [ 99; 1; 0 ]

        Assert.That(heights[0], Is.EqualTo(98.0).Within 1e-9)
        Assert.That(heights[1], Is.EqualTo(2.0).Within 1e-9)
        Assert.That(heights[2], Is.EqualTo(0.0).Within 1e-9)
        Assert.That(List.sum heights, Is.EqualTo(100.0).Within 1e-9)

    [<Test>]
    member _.``short stacks expand when every present series needs the minimum height`` () =
        let heights = OverviewChart.seriesPixelHeights 1.0 [ 1; 1; 0 ]

        Assert.That(heights[0], Is.EqualTo(2.0).Within 1e-9)
        Assert.That(heights[1], Is.EqualTo(2.0).Within 1e-9)
        Assert.That(heights[2], Is.EqualTo(0.0).Within 1e-9)

    [<Test>]
    member _.``subpixel intervals get a two pixel marker clamped to the plot edge`` () =
        let markers =
            OverviewChart.minimumIntervalMarkers
                34.0
                752.0
                [| 751.62; 752.0 |]
                [| 0.0; 0.0 |]
                [| 2.0; 2.0 |]

        Assert.That(markers.Length, Is.EqualTo 1)
        Assert.That(markers.Head.X, Is.EqualTo(750.0).Within 1e-9)
        Assert.That(markers.Head.Width, Is.EqualTo(2.0).Within 1e-9)
        Assert.That(markers.Head.Lower, Is.EqualTo(0.0).Within 1e-9)
        Assert.That(markers.Head.Upper, Is.EqualTo(2.0).Within 1e-9)

    [<Test>]
    member _.``visible intervals and absent series do not add markers`` () =
        let visible =
            OverviewChart.minimumIntervalMarkers
                34.0
                752.0
                [| 100.0; 101.0; 102.1 |]
                [| 0.0; 0.0; 0.0 |]
                [| 2.0; 2.0; 2.0 |]

        let absent =
            OverviewChart.minimumIntervalMarkers
                34.0
                752.0
                [| 100.0; 100.5 |]
                [| 2.0; 2.0 |]
                [| 2.0; 2.0 |]

        Assert.That(visible, Is.Empty)
        Assert.That(absent, Is.Empty)

    [<Test>]
    member _.``continuous subpixel intervals coalesce into one marker`` () =
        let markers =
            OverviewChart.minimumIntervalMarkers
                34.0
                752.0
                [| 100.0; 100.4; 100.8 |]
                [| 0.0; 0.0; 0.0 |]
                [| 2.0; 3.0; 3.0 |]

        Assert.That(markers.Length, Is.EqualTo 1)
        Assert.That(markers.Head.X, Is.EqualTo(99.4).Within 1e-9)
        Assert.That(markers.Head.Lower, Is.EqualTo(0.0).Within 1e-9)
        Assert.That(markers.Head.Upper, Is.EqualTo(3.0).Within 1e-9)

    [<Test>]
    member _.``rendering keeps only the latest point in each rounded x pixel`` () =
        let indices =
            OverviewChart.lastPointPerPixel [| 34.0; 34.2; 34.49; 34.51; 35.4; 35.6; 752.0 |]

        Assert.That(indices, Is.EqualTo [| 2; 4; 5; 6 |])

    [<Test>]
    member _.``component geometry memo rebuilds only for its four declared inputs`` () =
        let snapshots = [ taskSnap (hoursAgo 1.0) TaskBucketKind.Done 2 ]

        let input: OverviewChart.GeometryInput =
            { ChartKind = OverviewChart.ChartKind.Tasks
              HistoryWindow = HistoryWindow.Hours24
              Anchor = now
              Snapshots = snapshots }

        let build (geometryInput: OverviewChart.GeometryInput) = geometryInput.Anchor
        let first = OverviewChart.memoizedGeometry build input None

        let afterUnrelatedRenders =
            [ 1 .. 5 ]
            |> List.fold
                (fun memo _ -> OverviewChart.memoizedGeometry build input (Some memo))
                first

        Assert.That(OverviewChart.geometryBuildCount afterUnrelatedRenders, Is.EqualTo 1)
        Assert.That(OverviewChart.geometryValue afterUnrelatedRenders, Is.EqualTo now)

        let equalSnapshotsWithNewIdentity = snapshots |> List.map id
        Assert.That(Object.ReferenceEquals(snapshots, equalSnapshotsWithNewIdentity), Is.False)

        [ { input with ChartKind = OverviewChart.ChartKind.Agents }
          { input with HistoryWindow = HistoryWindow.Hours72 }
          { input with Anchor = now + TimeSpan.FromMinutes 1.0 }
          { input with Snapshots = equalSnapshotsWithNewIdentity } ]
        |> List.iter (fun changedInput ->
            let rebuilt =
                OverviewChart.memoizedGeometry build changedInput (Some afterUnrelatedRenders)

            Assert.That(OverviewChart.geometryBuildCount rebuilt, Is.EqualTo 2))

    [<Test>]
    member _.``hover snapping returns the active stepped point index`` () =
        let points =
            OverviewChart.taskPoints
                now
                window
                [ taskSnap (hoursAgo 12.0) TaskBucketKind.Done 2
                  taskSnap (hoursAgo 2.0) TaskBucketKind.Done 7 ]

        let beforeChange =
            OverviewChart.hoverSampleAt OverviewChart.ChartKind.Tasks window points 0.6
            |> Option.get

        let afterChange =
            OverviewChart.hoverSampleAt OverviewChart.ChartKind.Tasks window points 0.95
            |> Option.get

        Assert.Multiple(fun () ->
            Assert.That(beforeChange.PointIndex, Is.EqualTo 1)
            Assert.That(beforeChange.Fraction, Is.EqualTo(0.5).Within 1e-9)
            Assert.That(beforeChange.Tooltip.Total, Is.EqualTo 2)
            Assert.That(afterChange.PointIndex, Is.EqualTo 2)
            Assert.That(afterChange.Tooltip.Total, Is.EqualTo 7))

    [<Test>]
    member _.``same sampled point suppresses a hover state update`` () =
        Assert.Multiple(fun () ->
            Assert.That(OverviewChart.shouldUpdateHover None 3, Is.True)
            Assert.That(OverviewChart.shouldUpdateHover (Some 3) 3, Is.False)
            Assert.That(OverviewChart.shouldUpdateHover (Some 3) 4, Is.True))

    [<Test>]
    member _.``hover frame queue schedules once and flushes only the latest move`` () =
        let first, scheduleFirst =
            OverviewChart.emptyHoverFrameQueue ()
            |> OverviewChart.queueHoverFrame "first"

        let second, scheduleSecond = first |> OverviewChart.queueHoverFrame "second"
        let third, scheduleThird = second |> OverviewChart.queueHoverFrame "third"
        let candidate, cleared = OverviewChart.takeHoverFrame third
        let _, scheduleAfterFlush = cleared |> OverviewChart.queueHoverFrame "next"

        Assert.Multiple(fun () ->
            Assert.That(scheduleFirst, Is.True)
            Assert.That(scheduleSecond, Is.False)
            Assert.That(scheduleThird, Is.False)
            Assert.That(candidate, Is.EqualTo(Some "third"))
            Assert.That(scheduleAfterFlush, Is.True))

    // ── Crosshair tooltip seam (task tm-activity-history-zx6) ──

    [<Test>]
    member _.``tooltipAt returns None when there is no history`` () =
        let pts = OverviewChart.taskPoints now window []
        Assert.That(OverviewChart.tooltipAt OverviewChart.ChartKind.Tasks window pts 0.5, Is.EqualTo None)

    [<Test>]
    member _.``tooltipAt snaps to the active stepped snapshot and totals its non-empty series`` () =
        let pts =
            OverviewChart.taskPoints
                now
                window
                [ taskSnap (hoursAgo 12.0) TaskBucketKind.Done 2
                  taskSnap (hoursAgo 2.0) TaskBucketKind.Done 7 ]

        // Cursor past the 12h mark but before the 2h change -> the stepped value still held is Done = 2,
        // and only that non-empty series shows as a row.
        let m1 = OverviewChart.tooltipAt OverviewChart.ChartKind.Tasks window pts 0.6 |> Option.get
        Assert.That(m1.Total, Is.EqualTo 2)
        Assert.That(m1.Rows |> List.map (fun r -> r.Label, r.Count), Is.EqualTo [ ("Done", 2) ])
        Assert.That(m1.Rows.Head.Accent, Is.EqualTo "task-done")

        // Cursor past the 2h change -> the held value snaps up to Done = 7.
        let m2 = OverviewChart.tooltipAt OverviewChart.ChartKind.Tasks window pts 0.95 |> Option.get
        Assert.That(m2.Total, Is.EqualTo 7)
        Assert.That(m2.Rows |> List.exists (fun r -> r.Label = "Done" && r.Count = 7))

    [<Test>]
    member _.``tooltipAt header reads the snapped point's relative time`` () =
        let pts = OverviewChart.taskPoints now window [ taskSnap (hoursAgo 12.0) TaskBucketKind.Done 2 ]
        // The head (fraction 0.0) sits a full window back in a 24h window -> "24h 0m ago".
        let head = OverviewChart.tooltipAt OverviewChart.ChartKind.Tasks window pts 0.0 |> Option.get
        Assert.That(head.RelativeLabel, Is.EqualTo "24h 0m ago")
        // The right-edge hold (fraction 1.0) is "now".
        let tail = OverviewChart.tooltipAt OverviewChart.ChartKind.Tasks window pts 1.0 |> Option.get
        Assert.That(tail.RelativeLabel, Is.EqualTo "now")

    [<Test>]
    member _.``tooltipAt rows follow the canonical order and drop empty series`` () =
        let agentSnap : OverviewSnapshot =
            { Timestamp = hoursAgo 1.0
              Tasks = []
              Agents =
                [ { AgentCount.Kind = AgentGroupKind.Waiting; Count = 2 }
                  { AgentCount.Kind = AgentGroupKind.Activity CurrentActivity.Executing; Count = 3 } ] }

        let pts = OverviewChart.agentPoints now window [ agentSnap ]
        let model = OverviewChart.tooltipAt OverviewChart.ChartKind.Agents window pts 1.0 |> Option.get
        // Executing (canonical index 2) precedes Waiting (index 6); both are non-empty, nothing else is.
        Assert.That(model.Rows |> List.map _.Label, Is.EqualTo [ "Executing"; "Waiting" ])
        Assert.That(model.Total, Is.EqualTo 5)

    [<Test>]
    member _.``history refresh is disabled while the Overview panel is closed`` () =
        let lastFetchedAt = now - App.overviewHistoryRefreshInterval
        Assert.That(
            App.shouldRefreshOverviewHistory false (Some HistoryWindow.Hours24) None lastFetchedAt None now,
            Is.False
        )

    [<Test>]
    member _.``visible history keeps the 30 second refresh gate`` () =
        Assert.Multiple(fun () ->
            Assert.That(
                App.shouldRefreshOverviewHistory
                    true
                    (Some HistoryWindow.Hours12)
                    None
                    (now - TimeSpan.FromSeconds 29.0)
                    None
                    now,
                Is.False
            )

            Assert.That(
                App.shouldRefreshOverviewHistory
                    true
                    (Some HistoryWindow.Hours12)
                    None
                    (now - App.overviewHistoryRefreshInterval)
                    None
                    now,
                Is.True
            ))

    [<Test>]
    member _.``an in-flight history request blocks another refresh`` () =
        let request: AppTypes.OverviewHistoryRequest =
            { Window = HistoryWindow.Hours12
              RequestedAt = now - App.overviewHistoryRefreshInterval }

        Assert.That(
            App.shouldRefreshOverviewHistory
                true
                (Some HistoryWindow.Hours12)
                (Some request)
                request.RequestedAt
                None
                now,
            Is.False
        )

    [<Test>]
    member _.``a just-before-expiry cache hit does not postpone the post-expiry refresh`` () =
        let response =
            { Anchor = now
              Snapshots = [] }

        let cacheHitCompletedAt = now + App.overviewHistoryRefreshInterval - TimeSpan.FromSeconds 1.0
        let cacheExpiresAt = now + App.overviewHistoryRefreshInterval

        Assert.That(
            App.shouldRefreshOverviewHistory
                true
                (Some HistoryWindow.Hours24)
                None
                cacheHitCompletedAt
                (Some response)
                cacheExpiresAt,
            Is.True
        )

    [<Test>]
    member _.``history window cycle includes 12h before 24h and 72h`` () =
        let states =
            [ None
              nextHistoryWindow None
              nextHistoryWindow (Some HistoryWindow.Hours12)
              nextHistoryWindow (Some HistoryWindow.Hours24)
              nextHistoryWindow (Some HistoryWindow.Hours72) ]

        Assert.That(
            states,
            Is.EqualTo(
                [ None
                  Some HistoryWindow.Hours12
                  Some HistoryWindow.Hours24
                  Some HistoryWindow.Hours72
                  None ]
            )
        )

    [<Test>]
    member _.``12h chart axis uses three hour quarter points`` () =
        Assert.That(
            OverviewChart.windowAxisLabels HistoryWindow.Hours12,
            Is.EqualTo([ "-12h"; "-9h"; "-6h"; "-3h"; "now" ])
        )

    [<Test>]
    member _.``requested windows map to their explicit durations`` () =
        let hours =
            [ HistoryWindow.Hours12
              HistoryWindow.Hours24
              HistoryWindow.Hours72 ]
            |> List.map (HistoryWindow.duration >> _.TotalHours)

        Assert.That(hours, Is.EqualTo([ 12.0; 24.0; 72.0 ]))

    [<Test>]
    member _.``matching response keeps the server anchor`` () =
        let serverAnchor = now - TimeSpan.FromMinutes 7.0
        let response = { Anchor = serverAnchor; Snapshots = [] }

        let installed =
            App.installOverviewHistory
                (Some HistoryWindow.Hours24)
                HistoryWindow.Hours24
                (Some response)
                None

        Assert.That(installed |> Option.map _.Anchor, Is.EqualTo(Some serverAnchor))

    [<Test>]
    member _.``matching failed refresh clears the selected chart`` () =
        let current =
            { Anchor = now
              Snapshots = [ taskSnap now TaskBucketKind.Done 1 ] }

        let installed =
            App.installOverviewHistory
                (Some HistoryWindow.Hours24)
                HistoryWindow.Hours24
                None
                (Some current)

        Assert.That(installed, Is.EqualTo None)

    [<Test>]
    member _.``failed refresh for a stale or closed window keeps the current chart`` () =
        let current =
            { Anchor = now
              Snapshots = [ taskSnap now TaskBucketKind.Done 1 ] }

        Assert.Multiple(fun () ->
            Assert.That(
                App.installOverviewHistory
                    (Some HistoryWindow.Hours24)
                    HistoryWindow.Hours12
                    None
                    (Some current),
                Is.EqualTo(Some current)
            )

            Assert.That(
                App.installOverviewHistory None HistoryWindow.Hours24 None (Some current),
                Is.EqualTo(Some current)
            ))

    [<Test>]
    member _.``older response for the selected window cannot replace a newer chart`` () =
        let newer =
            { Anchor = now
              Snapshots = [ taskSnap now TaskBucketKind.Done 1 ] }

        let older =
            { Anchor = now - TimeSpan.FromMinutes 1.0
              Snapshots = [ taskSnap now TaskBucketKind.Done 9 ] }

        let installed =
            App.installOverviewHistory
                (Some HistoryWindow.Hours24)
                HistoryWindow.Hours24
                (Some older)
                (Some newer)

        Assert.That(installed, Is.EqualTo(Some newer))
