module Tests.OverviewHistoryTests

open System
open NUnit.Framework
open Shared
open OverviewData
open Server

// Unit tests for the Overview activity-history persistence module (spec:
// docs/spec/overview-activity-history.md). Cover the three behaviours the log must guarantee:
//   - change-detection: an unchanged count roll-up appends nothing, a changed one appends; a
//     membership-only change (same counts, different per-worktree Members) must NOT append.
//   - JSONL round-trip: serialize -> tryParse reproduces the snapshot byte-for-byte.
//   - window filtering: readWindow/parseWindow drops records older than the look-back window.
//   - partial-line tolerance: a torn trailing line (crash mid-write) is skipped, prior lines parse.
[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type OverviewHistoryTests() =

    // A drill-down member — its fields never affect the count-only projection, so they exist purely
    // to prove membership churn is ignored by `changed`.
    let mkMember key contribution : GroupMember =
        { ScopedKey = key
          Branch = "b"
          RepoId = RepoId "r"
          RepoName = "root"
          Since = None
          Contribution = contribution }

    /// Build a full Overview from (kind, count, member-keys) task specs and (kind, count, keys) agent
    /// specs. Members are synthesized from the keys so two Overviews can share counts yet differ in
    /// membership. Scale is the largest task count (matching aggregate), irrelevant to these tests.
    let mkOverview (taskSpecs: (TaskBucketKind * int * string list) list)
                   (agentSpecs: (AgentGroupKind * int * string list) list) : Overview =
        let tasks =
            taskSpecs
            |> List.map (fun (kind, count, keys) ->
                { TaskBucket.Kind = kind
                  Count = count
                  Members = keys |> List.map (fun k -> mkMember k count) })
        let agents =
            agentSpecs
            |> List.map (fun (kind, count, keys) ->
                { AgentGroup.Kind = kind
                  Count = count
                  Members = keys |> List.map (fun k -> mkMember k 1) })
        let scale = taskSpecs |> List.map (fun (_, c, _) -> c) |> (fun cs -> if cs.IsEmpty then 0 else List.max cs)
        { Tasks = tasks; Agents = agents; Scale = scale }

    let sampleSnapshot ts : OverviewSnapshot =
        { Timestamp = ts
          Tasks =
            [ { Kind = TaskBucketKind.Planned; Count = 3 }
              { Kind = TaskBucketKind.InProgress; Count = 2 } ]
          Agents = [ { Kind = AgentGroupKind.Activity CurrentActivity.Executing; Count = 4 } ] }

    // --- change-detection ---------------------------------------------------------------------

    [<Test>]
    member _.``changed is false when the count projection is identical (no line appended)``() =
        let overview = mkOverview [ TaskBucketKind.Planned, 3, [ "wt1" ] ] [ AgentGroupKind.Waiting, 1, [ "wt2" ] ]
        let last = OverviewHistory.snapshot (DateTimeOffset(2026, 7, 14, 9, 0, 0, TimeSpan.Zero)) overview
        Assert.That(OverviewHistory.changed (Some last) overview, Is.False)

    [<Test>]
    member _.``changed is true when a bucket count differs (one line appended)``() =
        let previous = mkOverview [ TaskBucketKind.Planned, 3, [ "wt1" ] ] []
        let last = OverviewHistory.snapshot (DateTimeOffset(2026, 7, 14, 9, 0, 0, TimeSpan.Zero)) previous
        let next = mkOverview [ TaskBucketKind.Planned, 4, [ "wt1" ] ] []
        Assert.That(OverviewHistory.changed (Some last) next, Is.True)

    [<Test>]
    member _.``changed is true when an agent count differs``() =
        let previous = mkOverview [] [ AgentGroupKind.Waiting, 1, [ "wt1" ] ]
        let last = OverviewHistory.snapshot (DateTimeOffset(2026, 7, 14, 9, 0, 0, TimeSpan.Zero)) previous
        let next = mkOverview [] [ AgentGroupKind.Waiting, 2, [ "wt1" ] ]
        Assert.That(OverviewHistory.changed (Some last) next, Is.True)

    [<Test>]
    member _.``changed is false for a membership-only change (same counts, different Members)``() =
        // Same counts, different member worktrees — the projection drops Members, so no append.
        let previous =
            mkOverview
                [ TaskBucketKind.Planned, 1, [ "wtA" ] ]
                [ AgentGroupKind.Activity CurrentActivity.Executing, 1, [ "wtA" ] ]
        let last = OverviewHistory.snapshot (DateTimeOffset(2026, 7, 14, 9, 0, 0, TimeSpan.Zero)) previous
        let next =
            mkOverview
                [ TaskBucketKind.Planned, 1, [ "wtB" ] ]
                [ AgentGroupKind.Activity CurrentActivity.Executing, 1, [ "wtB" ] ]
        Assert.That(OverviewHistory.changed (Some last) next, Is.False)

    [<Test>]
    member _.``changed is true when there is no prior snapshot (first baseline record)``() =
        let overview = mkOverview [ TaskBucketKind.Planned, 1, [ "wt1" ] ] []
        Assert.That(OverviewHistory.changed None overview, Is.True)

    // --- JSONL round-trip ---------------------------------------------------------------------

    [<Test>]
    member _.``serialize then tryParse reproduces the snapshot``() =
        let original = sampleSnapshot (DateTimeOffset(2026, 7, 14, 9, 30, 0, TimeSpan.Zero))
        let parsed = OverviewHistory.serialize original |> OverviewHistory.tryParse
        Assert.That(parsed, Is.EqualTo(Some original))

    [<Test>]
    member _.``serialize emits a single line (no embedded newline)``() =
        let line = OverviewHistory.serialize (sampleSnapshot DateTimeOffset.UtcNow)
        Assert.That(line.Contains("\n"), Is.False)
        Assert.That(line.Contains("\r"), Is.False)

    // --- window filtering ---------------------------------------------------------------------

    [<Test>]
    member _.``parseWindow keeps records within the window and drops older ones``() =
        let now = DateTimeOffset(2026, 7, 14, 12, 0, 0, TimeSpan.Zero)
        let recent = sampleSnapshot (now.AddHours(-1.0))
        let old = sampleSnapshot (now.AddHours(-30.0))
        let lines = [ OverviewHistory.serialize old; OverviewHistory.serialize recent ]
        let kept = OverviewHistory.parseWindow now (TimeSpan.FromHours 24.0) lines
        Assert.That(kept, Is.EqualTo([ recent ]))

    [<Test>]
    member _.``parseWindow keeps a record exactly on the cutoff boundary``() =
        let now = DateTimeOffset(2026, 7, 14, 12, 0, 0, TimeSpan.Zero)
        let boundary = sampleSnapshot (now.AddHours(-24.0))
        let kept = OverviewHistory.parseWindow now (TimeSpan.FromHours 24.0) [ OverviewHistory.serialize boundary ]
        Assert.That(kept, Is.EqualTo([ boundary ]))

    // --- partial-line tolerance ---------------------------------------------------------------

    [<Test>]
    member _.``parseWindow tolerates a partial trailing line and blank lines``() =
        let now = DateTimeOffset(2026, 7, 14, 12, 0, 0, TimeSpan.Zero)
        let good = sampleSnapshot (now.AddHours(-2.0))
        let goodLine = OverviewHistory.serialize good
        // A crash mid-write leaves a truncated JSON fragment as the last line.
        let partial = goodLine.Substring(0, goodLine.Length / 2)
        let lines = [ goodLine; ""; partial ]
        let kept = OverviewHistory.parseWindow now (TimeSpan.FromHours 24.0) lines
        Assert.That(kept, Is.EqualTo([ good ]))

    [<Test>]
    member _.``tryParse returns None for blank and malformed lines``() =
        Assert.That(OverviewHistory.tryParse "", Is.EqualTo(None))
        Assert.That(OverviewHistory.tryParse "   ", Is.EqualTo(None))
        Assert.That(OverviewHistory.tryParse "{not valid json", Is.EqualTo(None))

    // --- end-to-end disk append + read (real FileStream path) ---------------------------------

    [<Test>]
    [<NonParallelizable>]
    member _.``append then readWindow round-trips through logs/overview-history.jsonl``() =
        TestUtils.withTempCwd (fun () ->
            let a = sampleSnapshot (DateTimeOffset.UtcNow.AddHours(-1.0))
            let b = sampleSnapshot (DateTimeOffset.UtcNow.AddMinutes(-5.0))
            // append reports write success so the scheduler only advances its accumulator on a
            // real write (finding F4); a successful append to a writable temp cwd returns true.
            Assert.That(OverviewHistory.append a, Is.True)
            Assert.That(OverviewHistory.append b, Is.True)
            let read = OverviewHistory.readWindow (TimeSpan.FromHours 24.0)
            Assert.That(read, Is.EqualTo([ a; b ])))

    [<Test>]
    [<NonParallelizable>]
    member _.``readWindow returns empty when the history file is absent``() =
        TestUtils.withTempCwd (fun () ->
            Assert.That(OverviewHistory.readWindow (TimeSpan.FromHours 24.0), Is.Empty))
