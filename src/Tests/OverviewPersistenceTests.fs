module Tests.OverviewPersistenceTests

open NUnit.Framework
open Server

// The Overview band's open/closed state is meant to persist across a reload, exactly like the Canvas
// pane. The OverviewBand E2E suite boots the --test-fixtures server, whose IWorktreeApi is
// WorktreeApi.readOnlyApi — there saveOverviewPanelOpen is a no-op, so no browser reload against that
// server can prove persistence. This integration test drives the REAL persistence path the writable
// API and the dashboard delegate to: GlobalConfig.writeOverviewPanelOpen is the body of the writable
// api's saveOverviewPanelOpen, and GlobalConfig.readOverviewPanelOpen is the source of
// DashboardResponse.OverviewPanelOpen (read on first load). Each case runs against a real config.json
// under an isolated TREEMON_CONFIG_DIR, so a fresh read genuinely re-parses from disk = a reload.
[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
[<NonParallelizable>]
type OverviewPersistenceTests() =

    [<Test>]
    member _.``Defaults to closed when the config has never been written``() =
        TestUtils.withTempConfigDir "overview-persist" (fun _ ->
            Assert.That(GlobalConfig.readOverviewPanelOpen (), Is.False))

    [<Test>]
    member _.``Open state survives a reload (fresh read of a real config.json)``() =
        TestUtils.withTempConfigDir "overview-persist" (fun _ ->
            GlobalConfig.writeOverviewPanelOpen true
            Assert.That(GlobalConfig.readOverviewPanelOpen (), Is.True))

    [<Test>]
    member _.``Closing after opening persists the closed state``() =
        TestUtils.withTempConfigDir "overview-persist" (fun _ ->
            GlobalConfig.writeOverviewPanelOpen true
            GlobalConfig.writeOverviewPanelOpen false
            Assert.That(GlobalConfig.readOverviewPanelOpen (), Is.False))

    [<Test>]
    member _.``Overview state is independent of the Canvas pane state``() =
        // Both are booleans in the same config.json; a shared/confused key would couple them.
        TestUtils.withTempConfigDir "overview-persist" (fun _ ->
            GlobalConfig.writeCanvasPaneOpen true
            GlobalConfig.writeOverviewPanelOpen false
            Assert.That(GlobalConfig.readOverviewPanelOpen (), Is.False, "overview stays closed")
            Assert.That(GlobalConfig.readCanvasPaneOpen (), Is.True, "canvas stays open"))
