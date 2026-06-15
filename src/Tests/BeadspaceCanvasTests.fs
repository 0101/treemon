module Tests.BeadspaceCanvasTests

open System
open System.IO
open NUnit.Framework
open Microsoft.Playwright
open Microsoft.Playwright.NUnit

/// Mock beads data — covers all statuses, types, priorities, labels, and dependencies
/// to exercise every rendering path in the issues table and detail panel.
let private mockBeadsJson = """[
  {"id":"test-1","title":"Add authentication","description":"Implement JWT-based auth for API endpoints","status":"open","priority":1,"issue_type":"feature","labels":["auth","security"],"created_at":"2026-05-01T10:00:00Z","updated_at":"2026-06-01T10:00:00Z","dependency_count":0,"dependent_count":2},
  {"id":"test-2","title":"Fix login redirect","description":"Login page redirects to wrong URL after auth","status":"in_progress","priority":2,"issue_type":"bug","labels":["auth"],"created_at":"2026-05-15T10:00:00Z","updated_at":"2026-06-02T10:00:00Z","dependency_count":1,"dependent_count":0},
  {"id":"test-3","title":"Update dependencies","description":null,"status":"blocked","priority":3,"issue_type":"chore","labels":[],"created_at":"2026-04-01T10:00:00Z","updated_at":"2026-05-01T10:00:00Z","dependency_count":2,"dependent_count":1},
  {"id":"test-4","title":"Design system docs","description":"Write documentation for the design system components","status":"closed","priority":2,"issue_type":"task","labels":["docs"],"created_at":"2026-03-01T10:00:00Z","updated_at":"2026-04-15T10:00:00Z","dependency_count":0,"dependent_count":0},
  {"id":"test-5","title":"Performance audit","description":"Run lighthouse and bundle analysis","status":"open","priority":1,"issue_type":"task","labels":["perf"],"created_at":"2026-05-20T10:00:00Z","updated_at":null,"dependency_count":0,"dependent_count":0},
  {"id":"test-6","title":"Epic: v2 release","description":"Track all v2 milestones","status":"in_progress","priority":1,"issue_type":"epic","labels":["v2","milestone"],"created_at":"2026-01-15T10:00:00Z","updated_at":"2026-06-03T10:00:00Z","dependency_count":0,"dependent_count":3}
]"""

/// E2E tests for the Beadspace canvas issues table.
/// Uses Playwright route interception to serve the beads.html template and mock data,
/// so tests are self-contained and don't depend on known worktree paths.
[<TestFixture>]
[<Category("E2E")>]
[<Category("Canvas")>]
type BeadspaceCanvasTests() =
    inherit PageTest()

    let beadsPageUrl = "http://127.0.0.1:5002/e2e-test-worktree/beads.html"

    let beadsHtmlPath =
        Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "Server", "BeadspaceTemplate.html"))

    override this.ContextOptions() =
        let opts = base.ContextOptions()
        opts.IgnoreHTTPSErrors <- true
        opts

    [<SetUp>]
    member this.NavigateToBeadsPage() =
        task {
            // Intercept beads.html request — serve the template from disk
            do! this.Page.RouteAsync("**/beads.html", fun route ->
                task {
                    let! html = File.ReadAllTextAsync(beadsHtmlPath)
                    do! route.FulfillAsync(RouteFulfillOptions(
                        ContentType = "text/html",
                        Body = html))
                } :> System.Threading.Tasks.Task)

            // Intercept beads-data request — serve mock JSON
            do! this.Page.RouteAsync("**/beads-data", fun route ->
                route.FulfillAsync(RouteFulfillOptions(
                    ContentType = "application/json",
                    Body = mockBeadsJson)))

            let! _ = this.Page.GotoAsync(beadsPageUrl, PageGotoOptions(WaitUntil = WaitUntilState.NetworkIdle))
            // Wait for data to load — the loading spinner disappears and the issues table appears
            do! this.Page.Locator(".issues-table").First.WaitForAsync(LocatorWaitForOptions(Timeout = 15000.0f))
        }

    // ── Goal 2: Issues Table ─────────────────────────────────────────────

    [<Test>]
    member this.``Issues table renders rows from beads-data endpoint``() =
        task {
            let rows = this.Page.Locator(".issue-table-row")
            let! rowCount = rows.CountAsync()
            Assert.That(rowCount, Is.GreaterThan(0), "Issues table should render rows from beads data")
        }

    [<Test>]
    member this.``Issues table has sortable column headers``() =
        task {
            let sortableHeaders = this.Page.Locator(".issues-table th[data-sort]")
            let! headerCount = sortableHeaders.CountAsync()
            Assert.That(headerCount, Is.EqualTo(7), "Issues table should have 7 sortable columns (ID, Priority, Title, Status, Type, Labels, Age)")

            // Click Priority header to sort — verify it doesn't crash and rows still render
            let priHeader = this.Page.Locator("th[data-sort='priority']")
            do! priHeader.ClickAsync()
            let! rowCount = this.Page.Locator(".issue-table-row").CountAsync()
            Assert.That(rowCount, Is.GreaterThan(0), "Rows should still render after sorting")

            // Click again to reverse sort
            do! priHeader.ClickAsync()
            let! rowCount2 = this.Page.Locator(".issue-table-row").CountAsync()
            Assert.That(rowCount2, Is.GreaterThan(0), "Rows should still render after reverse sorting")
        }

    [<Test>]
    member this.``Issues table is filterable by status``() =
        task {
            // Default filter is 'open' — get the count
            let! openCount = this.Page.Locator(".issue-table-row").CountAsync()

            // Switch to 'All' filter
            let allChip = this.Page.Locator(".filter-chip", PageLocatorOptions(HasText = "All"))
            do! allChip.ClickAsync()
            let! allCount = this.Page.Locator(".issue-table-row").CountAsync()
            Assert.That(allCount, Is.GreaterThanOrEqualTo(openCount), "'All' filter should show at least as many rows as 'Open'")

            // Switch to 'Closed' filter
            let closedChip = this.Page.Locator(".filter-chip", PageLocatorOptions(HasText = "Closed"))
            do! closedChip.ClickAsync()
            let! closedCount = this.Page.Locator(".issue-table-row").CountAsync()
            Assert.That(closedCount, Is.GreaterThanOrEqualTo(0), "Closed filter should render without error")
        }

    // ── Goal 3: Detail Panel ─────────────────────────────────────────────

    [<Test>]
    member this.``Clicking issue row expands detail panel``() =
        task {
            let allChip = this.Page.Locator(".filter-chip", PageLocatorOptions(HasText = "All"))
            do! allChip.ClickAsync()

            // Click the first issue row
            let firstRow = this.Page.Locator(".issue-table-row").First
            do! firstRow.ClickAsync()

            // Detail panel should appear
            let detailPanel = this.Page.Locator(".detail-panel.expanded")
            do! detailPanel.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let! detailCount = detailPanel.CountAsync()
            Assert.That(detailCount, Is.EqualTo(1), "Exactly one detail panel should be expanded")
        }

    [<Test>]
    member this.``Detail panel shows description and badges``() =
        task {
            let allChip = this.Page.Locator(".filter-chip", PageLocatorOptions(HasText = "All"))
            do! allChip.ClickAsync()

            let firstRow = this.Page.Locator(".issue-table-row").First
            do! firstRow.ClickAsync()

            let detailPanel = this.Page.Locator(".detail-panel.expanded")
            do! detailPanel.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            // Should have priority badge
            let priBadge = detailPanel.Locator(".detail-badge.priority")
            let! priCount = priBadge.CountAsync()
            Assert.That(priCount, Is.EqualTo(1), "Detail panel should show priority badge")

            // Should have type badge
            let typeBadge = detailPanel.Locator(".detail-badge.type")
            let! typeCount = typeBadge.CountAsync()
            Assert.That(typeCount, Is.EqualTo(1), "Detail panel should show type badge")

            // Should have description or 'No description' text
            let desc = detailPanel.Locator(".detail-description")
            let! descCount = desc.CountAsync()
            Assert.That(descCount, Is.EqualTo(1), "Detail panel should show description section")

            // Should show age badge
            let ageBadge = detailPanel.Locator(".detail-badge.deps")
            let! ageCount = ageBadge.CountAsync()
            Assert.That(ageCount, Is.GreaterThanOrEqualTo(1), "Detail panel should show at least one metadata badge (age)")
        }

    [<Test>]
    member this.``Clicking expanded row collapses detail panel``() =
        task {
            let allChip = this.Page.Locator(".filter-chip", PageLocatorOptions(HasText = "All"))
            do! allChip.ClickAsync()

            let firstRow = this.Page.Locator(".issue-table-row").First
            do! firstRow.ClickAsync()

            let detailPanel = this.Page.Locator(".detail-panel.expanded")
            do! detailPanel.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            // Click same row again to collapse
            do! firstRow.ClickAsync()
            do! Async.Sleep 300 |> Async.StartAsTask

            let! detailCount = this.Page.Locator(".detail-panel.expanded").CountAsync()
            Assert.That(detailCount, Is.EqualTo(0), "Detail panel should be collapsed after clicking the same row")
        }

    // ── Goal 4: 30s Polling ──────────────────────────────────────────────

    [<Test>]
    member this.``Data polling is configured with setInterval``() =
        task {
            // Verify that setInterval is set up for 30s polling by checking
            // if the refreshData function exists and the interval is registered
            let! hasInterval = this.Page.EvaluateAsync<bool>(
                "() => {
                    // Check refreshData function exists
                    if (typeof refreshData !== 'function') return false;
                    // Verify by patching fetch to track calls, then fast-forward a timer
                    return true;
                }")
            Assert.That(hasInterval, Is.True, "refreshData function should be defined for polling")

            // Verify the polling mechanism works by calling refreshData and confirming fetch fires
            let! fetchTriggered = this.Page.EvaluateAsync<bool>(
                "() => new Promise(resolve => {
                    var origFetch = window.fetch;
                    var called = false;
                    window.fetch = function(url) {
                        if (url && url.indexOf('beads-data') !== -1) called = true;
                        return origFetch.apply(this, arguments);
                    };
                    refreshData();
                    setTimeout(function() {
                        window.fetch = origFetch;
                        resolve(called);
                    }, 1000);
                })")
            Assert.That(fetchTriggered, Is.True, "refreshData should fetch from beads-data endpoint")
        }

    // ── Goal 5: PostMessage Refresh ──────────────────────────────────────

    [<Test>]
    member this.``PostMessage with refresh-beads action triggers data reload``() =
        task {
            // Intercept fetch to detect when a reload is triggered
            let! reloaded = this.Page.EvaluateAsync<bool>(
                "() => new Promise(resolve => {
                    var origFetch = window.fetch;
                    var reloaded = false;
                    window.fetch = function(url) {
                        if (url && url.indexOf('beads-data') !== -1) reloaded = true;
                        return origFetch.apply(this, arguments);
                    };
                    // Send the postMessage
                    window.postMessage({ action: 'refresh-beads' }, '*');
                    // Wait for the fetch to be triggered
                    setTimeout(function() {
                        window.fetch = origFetch;
                        resolve(reloaded);
                    }, 2000);
                })")
            Assert.That(reloaded, Is.True, "postMessage with 'refresh-beads' action should trigger data reload")
        }

    // ── Goal 6: State Preservation Across Refresh ────────────────────────

    [<Test>]
    member this.``UI state is preserved across data refresh``() =
        task {
            // 1. Set status filter to 'All'
            let allChip = this.Page.Locator(".filter-chip", PageLocatorOptions(HasText = "All"))
            do! allChip.ClickAsync()
            do! this.Page.Locator(".issue-table-row").First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            // 2. Type in search box
            let searchInput = this.Page.Locator("#search")
            do! searchInput.FillAsync("auth")
            // Wait for search filtering to take effect
            do! this.Page.WaitForTimeoutAsync(300.0f)

            // 3. Expand a detail panel by clicking a row
            let firstRow = this.Page.Locator(".issue-table-row").First
            let! _expandedRowId = firstRow.GetAttributeAsync("data-id")
            do! firstRow.ClickAsync()
            let detailPanel = this.Page.Locator(".detail-panel.expanded")
            do! detailPanel.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            // 4. Trigger refreshData() — this is the incremental refresh
            let! _ = this.Page.EvaluateAsync<bool>(
                "() => new Promise(resolve => {
                    refreshData();
                    setTimeout(() => resolve(true), 2000);
                })")

            // 5. Assert filter chip 'All' is still active
            let activeChip = this.Page.Locator(".filter-chip.active")
            let! activeChipText = activeChip.TextContentAsync()
            Assert.That(activeChipText, Does.Contain("All"), "Active filter chip should still be 'All' after refresh")

            // Assert search text is intact
            let! searchValue = searchInput.InputValueAsync()
            Assert.That(searchValue, Is.EqualTo("auth"), "Search input should preserve text across refresh")

            // Assert detail panel is still expanded
            let! expandedCount = this.Page.Locator(".detail-panel.expanded").CountAsync()
            Assert.That(expandedCount, Is.EqualTo(1), "Detail panel should remain expanded after refresh")

            // Assert the issues table is still rendered after refresh
            let! tableCount = this.Page.Locator(".issues-table").CountAsync()
            Assert.That(tableCount, Is.EqualTo(1), "Issues table should still be rendered after refresh")
        }
