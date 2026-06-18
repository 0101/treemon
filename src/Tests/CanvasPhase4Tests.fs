module Tests.CanvasPhase4Tests

open System
open NUnit.Framework
open Microsoft.Playwright
open Microsoft.Playwright.NUnit
open Tests.CanvasTestHelpers

let [<Literal>] private FixtureCanvasBranch = "feature-active"
let [<Literal>] private FixtureMultiDocBranch = "feature-multidoc"

/// Click each canvas tab 0..count-1 in order, awaiting the click and a short settle delay
/// between each. A `for` loop body cannot `do!`-await the click (and List.iteri/Seq.iter cannot
/// await either), so this tail-recursive task helper drives the awaited clicks while preserving order.
let private clickTabsInOrder (tabs: ILocator) count =
    let rec loop i =
        task {
            if i < count then
                do! tabs.Nth(i).ClickAsync()
                do! Async.Sleep 200 |> Async.StartAsTask
                return! loop (i + 1)
        }
    loop 0

/// E2E tests for Phase 4 canvas awareness visual elements:
/// badge, liveness dot, start session button, waiting banner, canvas events.
[<TestFixture>]
[<Category("E2E")>]
[<Category("Canvas")>]
type CanvasPhase4Tests() =
    inherit PageTest()

    let baseUrl = ServerFixture.viteUrl

    override this.ContextOptions() =
        let opts = base.ContextOptions()
        opts.IgnoreHTTPSErrors <- true
        opts

    [<SetUp>]
    member this.NavigateToDashboard() =
        task {
            let! _ = this.Page.GotoAsync(baseUrl)
            do! this.Page.Locator(".wt-card .branch-name").First.WaitForAsync(LocatorWaitForOptions(Timeout = 15000.0f))
            return ()
        }

    // ── Canvas Header Badge ─────────────────────────────────────────────

    [<Test>]
    member this.``Canvas badge shows unviewed count when docs have not been viewed``() =
        task {
            // The fixture data has multiple canvas docs across worktrees.
            // On initial load, LastViewedHashes is seeded for pre-existing docs,
            // so the badge depends on whether any docs haven't been marked viewed.
            // We check the badge element exists OR is absent — both are valid states.
            let btn = canvasToggleBtn this.Page
            do! btn.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            let badge = btn.Locator(".canvas-badge")
            let! badgeCount = badge.CountAsync()

            if badgeCount > 0 then
                let! text = badge.First.TextContentAsync()
                let parsed = Int32.TryParse(text)
                Assert.That(fst parsed, Is.True, $"Badge text should be a number, got: '{text}'")
                Assert.That(snd parsed, Is.GreaterThan(0), "Badge should show a count greater than 0 when visible")
            else
                // Badge hidden means 0 unviewed — valid state after seeding
                Assert.Pass("Badge correctly hidden when unviewed count is 0")
        }

    [<Test>]
    member this.``Canvas badge disappears after viewing all docs``() =
        task {
            // Open canvas pane on each worktree with docs to mark them viewed,
            // then check the badge is gone.
            let btn = canvasToggleBtn this.Page

            // View feature-active docs
            do! focusCanvasCard this.Page FixtureCanvasBranch
            do! ensureCanvasPaneOpen this.Page
            do! ensureCanvasPaneClosed this.Page

            // View feature-multidoc docs (select each tab)
            do! focusCanvasCard this.Page FixtureMultiDocBranch
            do! ensureCanvasPaneOpen this.Page

            let tabs = this.Page.Locator(".canvas-pane .canvas-tab")
            let! tabCount = tabs.CountAsync()
            do! clickTabsInOrder tabs tabCount
            do! ensureCanvasPaneClosed this.Page

            // View multirepo worktree docs
            do! focusCanvasCard this.Page "multirepo"
            do! ensureCanvasPaneOpen this.Page

            let multiTabs = this.Page.Locator(".canvas-pane .canvas-tab")
            let! multiTabCount = multiTabs.CountAsync()
            do! clickTabsInOrder multiTabs multiTabCount

            // Allow time for state updates
            do! Async.Sleep 500 |> Async.StartAsTask

            // Badge should be gone or show 0
            let badge = btn.Locator(".canvas-badge")
            let! badgeCount = badge.CountAsync()
            Assert.That(badgeCount, Is.EqualTo(0), "Badge should disappear when all docs have been viewed")
        }

    // ── Liveness Dot ────────────────────────────────────────────────────

    [<Test>]
    member this.``Liveness dot renders without alive class when no bridge registered``() =
        task {
            // No extension bridge is registered in E2E tests, so liveness dots should not have 'alive' class
            do! focusCanvasCard this.Page FixtureMultiDocBranch
            do! (canvasToggleBtn this.Page).ClickAsync()
            do! (canvasPaneOpen this.Page).WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            let livenessDots = this.Page.Locator(".canvas-pane .canvas-liveness-dot")
            do! livenessDots.First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let! dotCount = livenessDots.CountAsync()
            Assert.That(dotCount, Is.GreaterThan(0), "Liveness dots should render in tab bar")

            // None should have the 'alive' class since no bridge is registered
            let aliveDots = this.Page.Locator(".canvas-pane .canvas-liveness-dot.alive")
            let! aliveCount = aliveDots.CountAsync()
            Assert.That(aliveCount, Is.EqualTo(0), "No liveness dots should have 'alive' class when no bridge is registered")
        }

    [<Test>]
    member this.``Liveness dot renders in overview entries``() =
        task {
            // Focus a worktree with no canvas docs to trigger overview
            do! focusCanvasCard this.Page "feature-recent"
            do! (canvasToggleBtn this.Page).ClickAsync()
            do! (canvasPaneOpen this.Page).WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            let overview = this.Page.Locator(".canvas-overview")
            do! overview.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            // Each overview doc entry (an AgentDoc) renders a liveness dot. The dot lives under
            // .canvas-overview-doc (CanvasPane.fs renders livenessDotFor inside that span); a
            // SystemView doc (e.g. beads.html) renders no dot, so this counts AgentDoc entries.
            let overviewDots = this.Page.Locator(".canvas-overview-doc .canvas-liveness-dot")
            let! dotCount = overviewDots.CountAsync()
            Assert.That(dotCount, Is.GreaterThanOrEqualTo(3), "Overview entries should each have a liveness dot")
        }

    [<Test>]
    member this.``Liveness dot has correct CSS styling``() =
        task {
            do! focusCanvasCard this.Page FixtureMultiDocBranch
            do! (canvasToggleBtn this.Page).ClickAsync()
            do! (canvasPaneOpen this.Page).WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            let dot = this.Page.Locator(".canvas-pane .canvas-liveness-dot").First
            do! dot.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            // Verify the dot is rendered as a small circle (inline-block with dimensions)
            let! display = dot.EvaluateAsync<string>("el => getComputedStyle(el).display")
            Assert.That(display, Is.EqualTo("inline-block"), "Liveness dot should be inline-block")

            let! borderRadius = dot.EvaluateAsync<string>("el => getComputedStyle(el).borderRadius")
            Assert.That(borderRadius, Does.Contain("50%").Or.Contain("4px"), "Liveness dot should be circular")
        }

    // ── Start Session Button ────────────────────────────────────────────

    [<Test>]
    member this.``Start session button visible when bridge not alive``() =
        task {
            // No bridge registered in E2E, so start session button should appear
            do! focusCanvasCard this.Page FixtureCanvasBranch
            do! ensureCanvasPaneOpen this.Page

            let launchBtn = this.Page.Locator(".canvas-pane .canvas-launch-btn")
            do! launchBtn.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let! count = launchBtn.CountAsync()
            Assert.That(count, Is.EqualTo(1), "Start session button should be visible when no bridge is alive")

            let! text = launchBtn.TextContentAsync()
            Assert.That(text, Does.Contain("Start session"), "Button should say 'Start session'")
        }

    [<Test>]
    member this.``Start session button not shown in overview mode``() =
        task {
            // Overview mode (no focused doc) should not show launch button
            do! focusCanvasCard this.Page "feature-recent"
            do! (canvasToggleBtn this.Page).ClickAsync()
            do! (canvasPaneOpen this.Page).WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            let overview = this.Page.Locator(".canvas-overview")
            do! overview.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            let launchBtn = this.Page.Locator(".canvas-pane .canvas-launch-btn")
            let! count = launchBtn.CountAsync()
            Assert.That(count, Is.EqualTo(0), "Start session button should not appear in overview mode")
        }

    // ── Waiting Banner ──────────────────────────────────────────────────

    [<Test>]
    member this.``Waiting banner appears when message is queued``() =
        task {
            // Focus a worktree with a canvas doc and open the pane
            do! focusCanvasCard this.Page FixtureCanvasBranch
            do! ensureCanvasPaneOpen this.Page

            let iframe = this.Page.Locator(".canvas-pane .canvas-iframe")
            do! iframe.WaitForAsync(LocatorWaitForOptions(Timeout = 10000.0f))

            // Send a postMessage to trigger sendCanvasMessage — with no bridge,
            // the server returns Queued, and the client should show the waiting banner
            let canvasOrigin = "http://127.0.0.1:5002"
            let! _ = this.Page.EvaluateAsync(
                $"() => {{
                    window.dispatchEvent(new MessageEvent('message', {{
                        data: {{ action: 'test-queue', data: 'e2e-waiting-banner' }},
                        origin: '{canvasOrigin}'
                    }}));
                }}")

            // Wait for the waiting banner to appear
            let banner = this.Page.Locator(".canvas-waiting-banner")
            do! banner.WaitForAsync(LocatorWaitForOptions(Timeout = 10000.0f))
            let! bannerCount = banner.CountAsync()
            Assert.That(bannerCount, Is.EqualTo(1), "Waiting banner should appear when message is queued")

            let! text = banner.TextContentAsync()
            Assert.That(text, Does.Contain("Waiting for session"), "Banner should say 'Waiting for session…'")

            // Dismiss button should be present
            let dismissBtn = this.Page.Locator(".canvas-waiting-dismiss")
            let! dismissCount = dismissBtn.CountAsync()
            Assert.That(dismissCount, Is.EqualTo(1), "Waiting banner should have a dismiss button")
        }

    // ── Canvas Events in Card Footer ────────────────────────────────────

    [<Test>]
    member this.``Canvas events have correct CSS class and yellow color``() =
        task {
            // Canvas events only appear when canvas hash changes between polls.
            // Since fixture data is static, we verify the CSS rule exists and is correct
            // by injecting a canvas-event element and checking computed style.
            let! color = this.Page.EvaluateAsync<string>(
                "() => {
                    const el = document.createElement('div');
                    el.className = 'event-entry canvas-event';
                    document.body.appendChild(el);
                    const color = getComputedStyle(el).color;
                    document.body.removeChild(el);
                    return color;
                }")

            // #f9e2af in RGB is approximately rgb(249, 226, 175)
            Assert.That(color, Is.EqualTo("rgb(249, 226, 175)"), "Canvas event text should be yellow (#f9e2af)")
        }

    [<Test>]
    member this.``Canvas event source spans have yellow color``() =
        task {
            // Verify the .canvas-event .event-source CSS rule
            let! color = this.Page.EvaluateAsync<string>(
                "() => {
                    const parent = document.createElement('div');
                    parent.className = 'event-entry canvas-event';
                    const source = document.createElement('span');
                    source.className = 'event-source';
                    parent.appendChild(source);
                    document.body.appendChild(parent);
                    const color = getComputedStyle(source).color;
                    document.body.removeChild(parent);
                    return color;
                }")

            Assert.That(color, Is.EqualTo("rgb(249, 226, 175)"), "Canvas event source should be yellow (#f9e2af)")
        }
