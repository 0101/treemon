module Tests.IdleDetectionTests

open System
open NUnit.Framework
open Server.RefreshScheduler
open Server.CodingToolStatus
open Shared

let private testRepoId = RepoId "TestRepo"
let private now = DateTimeOffset(2026, 3, 27, 12, 0, 0, TimeSpan.Zero)

// --- helpers ---

let private emptyDashboard =
    { DashboardState.empty with ClientActivity = ActivityLevel.Idle; ClientActivityAt = now }

let private dashboardWithCodingTool (lastMessageAge: TimeSpan) =
    let ct: CodingToolResult =
        { Status = CodingToolStatus.Idle
          Provider = None
          LastUserMessage = Some("hello", now - lastMessageAge)
          LastAssistantMessage = None }

    let repo =
        { PerRepoState.empty with
            CodingToolData = Map.ofList [ "/repo/a", ct ] }

    { DashboardState.empty with
        Repos = Map.ofList [ testRepoId, repo ]
        ClientActivity = ActivityLevel.Idle
        ClientActivityAt = now }

let private dashboardWithClientActivity (level: ActivityLevel) (activityAge: TimeSpan) =
    { DashboardState.empty with
        ClientActivity = level
        ClientActivityAt = now - activityAge }

// ==================== intervalOf tests ====================

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
[<Category("IdleDetection")>]
type IntervalOfTests() =

    // --- WorktreeList ---
    [<Test>]
    member _.``Active WorktreeList returns 10s``() =
        Assert.That(intervalOf ActivityLevel.Active (RefreshWorktreeList testRepoId), Is.EqualTo(TimeSpan.FromSeconds(10.0)))

    [<Test>]
    member _.``Idle WorktreeList returns 15s``() =
        Assert.That(intervalOf ActivityLevel.Idle (RefreshWorktreeList testRepoId), Is.EqualTo(TimeSpan.FromSeconds(15.0)))

    [<Test>]
    member _.``DeepIdle WorktreeList returns 60s``() =
        Assert.That(intervalOf ActivityLevel.DeepIdle (RefreshWorktreeList testRepoId), Is.EqualTo(TimeSpan.FromSeconds(60.0)))

    // --- Git ---
    [<Test>]
    member _.``Active Git returns 5s``() =
        Assert.That(intervalOf ActivityLevel.Active (RefreshGit(testRepoId, "/repo/a")), Is.EqualTo(TimeSpan.FromSeconds(5.0)))

    [<Test>]
    member _.``Idle Git returns 15s``() =
        Assert.That(intervalOf ActivityLevel.Idle (RefreshGit(testRepoId, "/repo/a")), Is.EqualTo(TimeSpan.FromSeconds(15.0)))

    [<Test>]
    member _.``DeepIdle Git returns 60s``() =
        Assert.That(intervalOf ActivityLevel.DeepIdle (RefreshGit(testRepoId, "/repo/a")), Is.EqualTo(TimeSpan.FromSeconds(60.0)))

    // --- Beads ---
    [<Test>]
    member _.``Active Beads returns 30s``() =
        Assert.That(intervalOf ActivityLevel.Active (RefreshBeads(testRepoId, "/repo/a")), Is.EqualTo(TimeSpan.FromSeconds(30.0)))

    [<Test>]
    member _.``Idle Beads returns 60s``() =
        Assert.That(intervalOf ActivityLevel.Idle (RefreshBeads(testRepoId, "/repo/a")), Is.EqualTo(TimeSpan.FromSeconds(60.0)))

    [<Test>]
    member _.``DeepIdle Beads returns 240s``() =
        Assert.That(intervalOf ActivityLevel.DeepIdle (RefreshBeads(testRepoId, "/repo/a")), Is.EqualTo(TimeSpan.FromSeconds(240.0)))

    // --- CodingTool ---
    [<Test>]
    member _.``Active CodingTool returns 5s``() =
        Assert.That(intervalOf ActivityLevel.Active (RefreshCodingTool(testRepoId, "/repo/a")), Is.EqualTo(TimeSpan.FromSeconds(5.0)))

    [<Test>]
    member _.``Idle CodingTool returns 15s``() =
        Assert.That(intervalOf ActivityLevel.Idle (RefreshCodingTool(testRepoId, "/repo/a")), Is.EqualTo(TimeSpan.FromSeconds(15.0)))

    [<Test>]
    member _.``DeepIdle CodingTool returns 60s``() =
        Assert.That(intervalOf ActivityLevel.DeepIdle (RefreshCodingTool(testRepoId, "/repo/a")), Is.EqualTo(TimeSpan.FromSeconds(60.0)))

    // --- PR ---
    [<Test>]
    member _.``Active PR returns 10s``() =
        Assert.That(intervalOf ActivityLevel.Active (RefreshPr testRepoId), Is.EqualTo(TimeSpan.FromSeconds(10.0)))

    [<Test>]
    member _.``Idle PR returns 120s``() =
        Assert.That(intervalOf ActivityLevel.Idle (RefreshPr testRepoId), Is.EqualTo(TimeSpan.FromSeconds(120.0)))

    [<Test>]
    member _.``DeepIdle PR returns 600s``() =
        Assert.That(intervalOf ActivityLevel.DeepIdle (RefreshPr testRepoId), Is.EqualTo(TimeSpan.FromSeconds(600.0)))

    // --- Fetch ---
    [<Test>]
    member _.``Active Fetch returns 10s``() =
        Assert.That(intervalOf ActivityLevel.Active (RefreshFetch testRepoId), Is.EqualTo(TimeSpan.FromSeconds(10.0)))

    [<Test>]
    member _.``Idle Fetch returns 120s``() =
        Assert.That(intervalOf ActivityLevel.Idle (RefreshFetch testRepoId), Is.EqualTo(TimeSpan.FromSeconds(120.0)))

    [<Test>]
    member _.``DeepIdle Fetch returns 600s``() =
        Assert.That(intervalOf ActivityLevel.DeepIdle (RefreshFetch testRepoId), Is.EqualTo(TimeSpan.FromSeconds(600.0)))

// ==================== effectiveActivity tests ====================

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
[<Category("IdleDetection")>]
type EffectiveActivityTests() =

    // --- Coding tool override ---
    [<Test>]
    member _.``Coding tool message within 5 min overrides to Active``() =
        let state = dashboardWithCodingTool (TimeSpan.FromMinutes(3.0))
        Assert.That(effectiveActivity now state, Is.EqualTo(ActivityLevel.Active))

    [<Test>]
    member _.``Coding tool message at exactly 5 min does not override``() =
        let state = dashboardWithCodingTool (TimeSpan.FromMinutes(5.0))
        Assert.That(effectiveActivity now state, Is.Not.EqualTo(ActivityLevel.Active))

    [<Test>]
    member _.``Coding tool message older than 5 min does not override``() =
        let state = dashboardWithCodingTool (TimeSpan.FromMinutes(10.0))
        Assert.That(effectiveActivity now state, Is.Not.EqualTo(ActivityLevel.Active))

    [<Test>]
    member _.``Coding tool message 1 second ago overrides Idle client to Active``() =
        let ct: CodingToolResult =
            { Status = CodingToolStatus.Idle
              Provider = None
              LastUserMessage = Some("test", now - TimeSpan.FromSeconds(1.0))
              LastAssistantMessage = None }

        let repo =
            { PerRepoState.empty with
                CodingToolData = Map.ofList [ "/repo/a", ct ] }

        let state =
            { DashboardState.empty with
                Repos = Map.ofList [ testRepoId, repo ]
                ClientActivity = ActivityLevel.DeepIdle
                ClientActivityAt = now - TimeSpan.FromHours(1.0) }

        Assert.That(effectiveActivity now state, Is.EqualTo(ActivityLevel.Active))

    // --- Empty data ---
    [<Test>]
    member _.``No repos returns client activity as-is when fresh``() =
        let state = dashboardWithClientActivity ActivityLevel.Idle (TimeSpan.FromSeconds(30.0))
        Assert.That(effectiveActivity now state, Is.EqualTo(ActivityLevel.Idle))

    [<Test>]
    member _.``No coding tool data with Active client returns Active when fresh``() =
        let state = dashboardWithClientActivity ActivityLevel.Active (TimeSpan.FromSeconds(30.0))
        Assert.That(effectiveActivity now state, Is.EqualTo(ActivityLevel.Active))

    [<Test>]
    member _.``No coding tool data with DeepIdle client returns DeepIdle when fresh``() =
        let state = dashboardWithClientActivity ActivityLevel.DeepIdle (TimeSpan.FromSeconds(30.0))
        Assert.That(effectiveActivity now state, Is.EqualTo(ActivityLevel.DeepIdle))

    // --- Client activity decay ---
    [<Test>]
    member _.``Active client stale 5 min decays to Idle``() =
        let state = dashboardWithClientActivity ActivityLevel.Active (TimeSpan.FromMinutes(5.0))
        Assert.That(effectiveActivity now state, Is.EqualTo(ActivityLevel.Idle))

    [<Test>]
    member _.``Active client stale 4m59s does not decay``() =
        let state = dashboardWithClientActivity ActivityLevel.Active (TimeSpan.FromMinutes(5.0) - TimeSpan.FromSeconds(1.0))
        Assert.That(effectiveActivity now state, Is.EqualTo(ActivityLevel.Active))

    [<Test>]
    member _.``Active client stale 20 min decays to DeepIdle``() =
        let state = dashboardWithClientActivity ActivityLevel.Active (TimeSpan.FromMinutes(20.0))
        Assert.That(effectiveActivity now state, Is.EqualTo(ActivityLevel.DeepIdle))

    [<Test>]
    member _.``Idle client stale 20 min decays to DeepIdle``() =
        let state = dashboardWithClientActivity ActivityLevel.Idle (TimeSpan.FromMinutes(20.0))
        Assert.That(effectiveActivity now state, Is.EqualTo(ActivityLevel.DeepIdle))

    [<Test>]
    member _.``Idle client stale 10 min stays Idle (no mid-decay to Active)``() =
        // Idle client at 10 min: not >= 20 min so no DeepIdle;
        // the Active->Idle decay only triggers when ClientActivity = Active, so Idle stays Idle
        let state = dashboardWithClientActivity ActivityLevel.Idle (TimeSpan.FromMinutes(10.0))
        Assert.That(effectiveActivity now state, Is.EqualTo(ActivityLevel.Idle))

    [<Test>]
    member _.``DeepIdle client stale 20 min stays DeepIdle``() =
        let state = dashboardWithClientActivity ActivityLevel.DeepIdle (TimeSpan.FromMinutes(20.0))
        Assert.That(effectiveActivity now state, Is.EqualTo(ActivityLevel.DeepIdle))

    [<Test>]
    member _.``Coding tool with no LastUserMessage does not override``() =
        let ct: CodingToolResult =
            { Status = CodingToolStatus.Idle
              Provider = None
              LastUserMessage = None
              LastAssistantMessage = None }

        let repo =
            { PerRepoState.empty with
                CodingToolData = Map.ofList [ "/repo/a", ct ] }

        let state =
            { DashboardState.empty with
                Repos = Map.ofList [ testRepoId, repo ]
                ClientActivity = ActivityLevel.Idle
                ClientActivityAt = now }

        Assert.That(effectiveActivity now state, Is.EqualTo(ActivityLevel.Idle))

    [<Test>]
    member _.``Multiple repos - one has recent coding tool activity overrides to Active``() =
        let recentCt: CodingToolResult =
            { Status = CodingToolStatus.Idle
              Provider = None
              LastUserMessage = Some("recent", now - TimeSpan.FromMinutes(1.0))
              LastAssistantMessage = None }

        let staleCt: CodingToolResult =
            { Status = CodingToolStatus.Idle
              Provider = None
              LastUserMessage = Some("stale", now - TimeSpan.FromMinutes(10.0))
              LastAssistantMessage = None }

        let repo1 =
            { PerRepoState.empty with
                CodingToolData = Map.ofList [ "/repo/a", staleCt ] }

        let repo2 =
            { PerRepoState.empty with
                CodingToolData = Map.ofList [ "/repo/b", recentCt ] }

        let state =
            { DashboardState.empty with
                Repos = Map.ofList [ RepoId "Repo1", repo1; RepoId "Repo2", repo2 ]
                ClientActivity = ActivityLevel.DeepIdle
                ClientActivityAt = now - TimeSpan.FromHours(1.0) }

        Assert.That(effectiveActivity now state, Is.EqualTo(ActivityLevel.Active))

// ==================== computeActivityLevel (client) tests ====================

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
[<Category("IdleDetection")>]
type ComputeActivityLevelTests() =

    let nowMs = 1000000.0 // arbitrary "now" in milliseconds

    [<Test>]
    member _.``Recent activity (0 elapsed) returns Active``() =
        Assert.That(App.computeActivityLevel nowMs nowMs, Is.EqualTo(ActivityLevel.Active))

    [<Test>]
    member _.``Activity 30s ago returns Active``() =
        Assert.That(App.computeActivityLevel (nowMs - 30_000.0) nowMs, Is.EqualTo(ActivityLevel.Active))

    [<Test>]
    member _.``Activity 2m59.999s ago returns Active``() =
        Assert.That(App.computeActivityLevel (nowMs - 179_999.0) nowMs, Is.EqualTo(ActivityLevel.Active))

    [<Test>]
    member _.``Activity exactly 3 min ago returns Idle``() =
        Assert.That(App.computeActivityLevel (nowMs - 180_000.0) nowMs, Is.EqualTo(ActivityLevel.Idle))

    [<Test>]
    member _.``Activity 5 min ago returns Idle``() =
        Assert.That(App.computeActivityLevel (nowMs - 300_000.0) nowMs, Is.EqualTo(ActivityLevel.Idle))

    [<Test>]
    member _.``Activity 14m59s ago returns Idle``() =
        Assert.That(App.computeActivityLevel (nowMs - 899_999.0) nowMs, Is.EqualTo(ActivityLevel.Idle))

    [<Test>]
    member _.``Activity exactly 15 min ago returns DeepIdle``() =
        Assert.That(App.computeActivityLevel (nowMs - 900_000.0) nowMs, Is.EqualTo(ActivityLevel.DeepIdle))

    [<Test>]
    member _.``Activity 1 hour ago returns DeepIdle``() =
        Assert.That(App.computeActivityLevel (nowMs - 3_600_000.0) nowMs, Is.EqualTo(ActivityLevel.DeepIdle))
