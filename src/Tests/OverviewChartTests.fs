module Tests.OverviewChartTests

open System
open NUnit.Framework
open Shared
open OverviewData
open AppTypes

// Unit tests for OverviewChart's pure windowing builders (spec: docs/spec/overview-activity-history.md).
// The SVG rendering itself is inline geometry verified by the E2E band tests; here we pin the pure
// point-projection behaviour the chart depends on:
//   - empty history yields no points (the chart degrades to a bare baseline, never an error),
//   - left-edge CARRY: a window opening after the last change starts from that snapshot's value,
//   - right-edge HOLD: the final point sits at fraction 1.0 holding the last value flat to "now",
//   - windowing drops records older than the window (they only survive as the carried left edge),
//   - counts are aligned to the canonical agent/task series order.
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
            App.shouldRefreshOverviewHistory false OverviewChartWindow.Hours24 lastFetchedAt now,
            Is.False
        )
