module Tests.OverviewHistoryIntegrationTests

open System
open System.IO
open Newtonsoft.Json.Linq
open NUnit.Framework
open Shared
open OverviewData
open Server
open Tests.WorktreeFixtures

// Server-side logging INTEGRATION test for the Overview activity-history path (spec:
// docs/spec/overview-activity-history.md; verify task tm-activity-history-83i). Unlike the unit
// coverage in OverviewHistoryTests.fs (which pokes `changed`/`serialize`/`parseWindow` in isolation),
// this fixture drives the FULL server path end-to-end against a real temp logfile:
//
//     seeded RepoWorktrees  ->  OverviewData.aggregate  ->  changed?  ->  snapshot + append
//
// mirroring exactly what RefreshScheduler.loop does (src/Server/RefreshScheduler.fs ~L726-735):
// only append when the count-only projection changed, and only advance the "last logged" accumulator
// when the write actually reached disk. Each test maps 1:1 to a numbered step of the verify task.
[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type OverviewHistoryIntegrationTests() =

    // ---- seed builders (a real RepoWorktrees list, the sanctioned "or the assembled list" input) --

    /// An ACTIVE (red-dot Working) worktree carrying beads/planning counts and a skill, so the
    /// aggregate yields a non-empty Tasks list (Planned/InProgress/Blocked/Done) AND a non-empty
    /// Agents list (a Working activity group). `ip` lets a caller perturb In-progress to force a
    /// changed roll-up.
    let activeWt (ip: int) : WorktreeStatus =
        { baseWt with
            CodingTool = CodingToolStatus.Working
            CurrentSkill = Some "executing"
            Beads = { BeadsSummary.zero with InProgress = ip; Blocked = 1; Closed = 3 }
            Planning = { BeadsPlanning.zero with Planned = 4 } }

    let repo (wts: WorktreeStatus list) : RepoWorktrees =
        { RepoId = RepoId "r"
          RootFolderName = "root"
          Worktrees = wts
          IsReady = true
          Provider = None
          BaseBranch = "main" }

    /// The assembled roll-up for a given In-progress count. `aggregate` here is the REAL shared
    /// aggregation the client band and the server logger both call.
    let overviewWith (ip: int) : Overview = aggregate [ repo [ activeWt ip ] ]

    // ---- scheduler-mirroring iteration: the exact changed -> snapshot -> append logic -------------

    /// One scheduler iteration against the on-disk log, byte-for-byte the RefreshScheduler.loop body:
    /// append (and advance the accumulator) only on a changed projection AND a successful write.
    let runIteration (last: OverviewSnapshot option) (ts: DateTimeOffset) (overview: Overview) : OverviewSnapshot option =
        if OverviewHistory.changed last overview then
            let snap = OverviewHistory.snapshot ts overview
            if OverviewHistory.append snap then Some snap else last
        else
            last

    let historyPath () =
        Path.Combine(Directory.GetCurrentDirectory(), "logs", "overview-history.jsonl")

    /// The non-blank JSONL lines actually on disk (what a skeptical reader would `cat`).
    let rawLines () : string[] =
        let path = historyPath ()
        if File.Exists path then
            File.ReadAllLines path |> Array.filter (String.IsNullOrWhiteSpace >> not)
        else
            [||]

    // === Step 1: an UNCHANGED aggregate appends no second line ==================================
    [<Test>]
    [<NonParallelizable>]
    member _.``step1: running the aggregate->append path twice with an unchanged roll-up appends no second line``() =
        TestUtils.withTempCwd (fun () ->
            let overview = overviewWith 2

            // First iteration: no prior snapshot -> baseline line written.
            let afterFirst = runIteration None DateTimeOffset.UtcNow overview
            Assert.That(rawLines().Length, Is.EqualTo 1, "baseline line should be appended on first change")

            // Second iteration with the SAME aggregate -> change-detection must suppress the append.
            let afterSecond = runIteration afterFirst DateTimeOffset.UtcNow overview

            let lines = rawLines ()
            TestContext.WriteLine(sprintf "logfile after two unchanged iterations (%d line(s)):" lines.Length)
            lines |> Array.iter (fun l -> TestContext.WriteLine("  " + l))
            Assert.That(lines.Length, Is.EqualTo 1, "an unchanged aggregate must NOT append a duplicate line")
            // Accumulator did not advance to a new object on the no-op iteration.
            Assert.That(afterSecond, Is.EqualTo afterFirst))

    // === Step 2: a CHANGED aggregate appends exactly one new line with an ISO-8601 ts + matching
    //             tasks/agents ==============================================================
    [<Test>]
    [<NonParallelizable>]
    member _.``step2: a changed aggregate appends exactly one new line whose timestamp is ISO-8601 and tasks/agents match``() =
        TestUtils.withTempCwd (fun () ->
            let baseline = overviewWith 2
            let changed = overviewWith 9 // In-progress 2 -> 9 : a genuinely different projection
            Assert.That(OverviewHistory.changed (Some(OverviewHistory.snapshot DateTimeOffset.UtcNow baseline)) changed,
                        Is.True, "sanity: the two roll-ups must differ")

            let afterBaseline = runIteration None DateTimeOffset.UtcNow baseline
            Assert.That(rawLines().Length, Is.EqualTo 1)

            let capture = DateTimeOffset.UtcNow
            let afterChange = runIteration afterBaseline capture changed

            let lines = rawLines ()
            TestContext.WriteLine("logfile after the changed iteration:")
            lines |> Array.iter (fun l -> TestContext.WriteLine("  " + l))
            // Exactly one NEW line (2 total).
            Assert.That(lines.Length, Is.EqualTo 2, "a changed aggregate must append exactly one new line")

            let newLine = lines.[1]

            // --- the timestamp parses as ISO-8601 (raw JSON, independent of the F# type) ---
            let obj = JObject.Parse newLine
            let tsToken = obj.["Timestamp"]
            Assert.That(tsToken, Is.Not.Null, "the appended line must carry a timestamp field")
            let tsRaw = string tsToken
            let mutable parsed = DateTimeOffset.MinValue
            let ok =
                DateTimeOffset.TryParse(
                    tsRaw,
                    Globalization.CultureInfo.InvariantCulture,
                    Globalization.DateTimeStyles.RoundtripKind,
                    &parsed)
            Assert.That(ok, Is.True, sprintf "timestamp %A must parse as ISO-8601" tsRaw)
            Assert.That(obj.["Tasks"], Is.Not.Null, "the line must carry tasks")
            Assert.That(obj.["Agents"], Is.Not.Null, "the line must carry agents")

            // --- tasks/agents match the NEW aggregate's count-only projection ---
            let expected = toCounts changed
            let snap = OverviewHistory.tryParse newLine
            Assert.That(snap.IsSome, Is.True, "the appended line must parse back to a snapshot")
            Assert.That(snap.Value.Tasks, Is.EqualTo expected.Tasks, "logged tasks must match the new aggregate")
            Assert.That(snap.Value.Agents, Is.EqualTo expected.Agents, "logged agents must match the new aggregate")
            // The accumulator advanced to the newly-logged snapshot.
            Assert.That(afterChange.Value.Tasks, Is.EqualTo expected.Tasks))

    // === Step 3: readWindow returns in-window records newest-inclusive and excludes older ones =====
    [<Test>]
    [<NonParallelizable>]
    member _.``step3: readWindow returns records within the window newest-inclusive and excludes an older-than-window record``() =
        TestUtils.withTempCwd (fun () ->
            let overview = overviewWith 2
            let now = DateTimeOffset.UtcNow

            // One record OLDER than a 24h window, two within it (written oldest-first, as the log grows).
            let older = OverviewHistory.snapshot (now.AddHours -30.0) overview   // outside 24h
            let recent = OverviewHistory.snapshot (now.AddHours -1.0) overview   // inside 24h
            let newest = OverviewHistory.snapshot (now.AddMinutes -1.0) overview // inside 24h (newest)
            Assert.That(OverviewHistory.append older, Is.True)
            Assert.That(OverviewHistory.append recent, Is.True)
            Assert.That(OverviewHistory.append newest, Is.True)

            let read = OverviewHistory.readWindow (TimeSpan.FromHours 24.0)
            TestContext.WriteLine(sprintf "readWindow(24h) returned %d record(s)" read.Length)
            read |> List.iter (fun s -> TestContext.WriteLine(sprintf "  ts=%O" s.Timestamp))

            Assert.That(read, Is.EqualTo [ recent; newest ], "must keep both in-window records, newest included, in order")
            Assert.That(read |> List.contains older, Is.False, "the 30h-old record must be excluded from a 24h window"))

    // === Step 4: a truncated trailing line does not break parsing of prior complete records ========
    [<Test>]
    [<NonParallelizable>]
    member _.``step4: a truncated partial trailing line is tolerated and prior complete records still parse``() =
        TestUtils.withTempCwd (fun () ->
            // Two good, in-window records via the real append path.
            let a = overviewWith 2
            let b = overviewWith 9
            let last = runIteration None (DateTimeOffset.UtcNow.AddMinutes -2.0) a
            let last2 = runIteration last (DateTimeOffset.UtcNow.AddMinutes -1.0) b
            Assert.That(rawLines().Length, Is.EqualTo 2)

            // Simulate a crash mid-write: append a truncated JSON fragment (no trailing newline) as
            // the new last line.
            let fragmentSource = OverviewHistory.serialize (OverviewHistory.snapshot DateTimeOffset.UtcNow a)
            let partial = fragmentSource.Substring(0, fragmentSource.Length / 2)
            File.AppendAllText(historyPath (), partial)
            TestContext.WriteLine("appended truncated trailing fragment: " + partial)

            // readWindow must NOT throw and must still return the two prior complete records.
            let read = OverviewHistory.readWindow (TimeSpan.FromHours 24.0)
            TestContext.WriteLine(sprintf "readWindow(24h) after truncation returned %d record(s)" read.Length)
            Assert.That(read.Length, Is.EqualTo 2, "the partial line must be skipped, prior records preserved")
            Assert.That(read.[0].Tasks, Is.EqualTo (toCounts a).Tasks)
            Assert.That(read.[1].Tasks, Is.EqualTo (toCounts b).Tasks))
