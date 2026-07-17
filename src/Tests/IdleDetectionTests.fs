module Tests.IdleDetectionTests

open System
open NUnit.Framework
open Server.RefreshScheduler
open Shared

let private now = DateTimeOffset(2026, 3, 27, 12, 0, 0, TimeSpan.Zero)

// --- helpers ---

let private dashboardWithClientActivity (level: ActivityLevel) (activityAge: TimeSpan) =
    { DashboardState.empty with
        ClientActivity = level
        ClientActivityAt = now - activityAge }

// ==================== effectiveActivity tests ====================
// effectiveActivity is now driven purely by client activity decay.

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
[<Category("IdleDetection")>]
type EffectiveActivityTests() =

    // --- Empty / fresh client activity passes through ---
    [<Test>]
    member _.``No repos returns client activity as-is when fresh``() =
        let state = dashboardWithClientActivity ActivityLevel.Idle (TimeSpan.FromSeconds(30.0))
        Assert.That(effectiveActivity now state, Is.EqualTo(ActivityLevel.Idle))

    [<Test>]
    member _.``Active client returns Active when fresh``() =
        let state = dashboardWithClientActivity ActivityLevel.Active (TimeSpan.FromSeconds(30.0))
        Assert.That(effectiveActivity now state, Is.EqualTo(ActivityLevel.Active))

    [<Test>]
    member _.``DeepIdle client returns DeepIdle when fresh``() =
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

// ==================== computeActivityLevel (client) tests ====================

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
[<Category("IdleDetection")>]
type ComputeActivityLevelTests() =

    let nowMs = 1000000.0 // arbitrary "now" in milliseconds

    [<Test>]
    member _.``Recent activity (0 elapsed) returns Active``() =
        Assert.That(ActivityState.computeActivityLevel nowMs nowMs, Is.EqualTo(ActivityLevel.Active))

    [<Test>]
    member _.``Activity 30s ago returns Active``() =
        Assert.That(ActivityState.computeActivityLevel (nowMs - 30_000.0) nowMs, Is.EqualTo(ActivityLevel.Active))

    [<Test>]
    member _.``Activity 2m59.999s ago returns Active``() =
        Assert.That(ActivityState.computeActivityLevel (nowMs - 179_999.0) nowMs, Is.EqualTo(ActivityLevel.Active))

    [<Test>]
    member _.``Activity exactly 3 min ago returns Idle``() =
        Assert.That(ActivityState.computeActivityLevel (nowMs - 180_000.0) nowMs, Is.EqualTo(ActivityLevel.Idle))

    [<Test>]
    member _.``Activity 5 min ago returns Idle``() =
        Assert.That(ActivityState.computeActivityLevel (nowMs - 300_000.0) nowMs, Is.EqualTo(ActivityLevel.Idle))

    [<Test>]
    member _.``Activity 14m59s ago returns Idle``() =
        Assert.That(ActivityState.computeActivityLevel (nowMs - 899_999.0) nowMs, Is.EqualTo(ActivityLevel.Idle))

    [<Test>]
    member _.``Activity exactly 15 min ago returns DeepIdle``() =
        Assert.That(ActivityState.computeActivityLevel (nowMs - 900_000.0) nowMs, Is.EqualTo(ActivityLevel.DeepIdle))

    [<Test>]
    member _.``Activity 1 hour ago returns DeepIdle``() =
        Assert.That(ActivityState.computeActivityLevel (nowMs - 3_600_000.0) nowMs, Is.EqualTo(ActivityLevel.DeepIdle))
