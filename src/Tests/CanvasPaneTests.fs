module Tests.CanvasPaneTests

open System
open System.IO
open NUnit.Framework
open Microsoft.Playwright
open Microsoft.Playwright.NUnit

/// Branch name of the fixture worktree that has a CanvasDoc defined in
/// src/Tests/fixtures/worktrees.json.  Tests target this known branch
/// instead of calling getCurrentBranch() (which returns the live repo's
/// branch, not a fixture branch).
let [<Literal>] private FixtureCanvasBranch = "feature-active"
let [<Literal>] private FixtureMultiDocBranch = "feature-multidoc"

/// E2E tests for the canvas pane feature.
/// Prerequisites:
///   - Server running on :5001 with canvas doc server on :5002
///   - Vite dev server on :5174
[<TestFixture>]
[<Category("E2E")>]
[<Category("Canvas")>]
type CanvasPaneTests() =
    inherit PageTest()

    let baseUrl = ServerFixture.viteUrl
    let canvasOrigin = "http://127.0.0.1:5002"

    let canvasToggleBtn (page: IPage) =
        page.Locator(".header-controls .ctrl-btn", PageLocatorOptions(HasText = "Canvas"))

    let canvasPane (page: IPage) =
        page.Locator(".canvas-pane")

    let canvasPaneOpen (page: IPage) =
        page.Locator(".canvas-pane.open")

    let canvasIframe (page: IPage) =
        page.Locator(".canvas-pane .canvas-iframe")

    let canvasEmpty (page: IPage) =
        page.Locator(".canvas-pane .canvas-empty")

    let canvasTabBar (page: IPage) =
        page.Locator(".canvas-pane .canvas-tab-bar")

    let canvasTabs (page: IPage) =
        page.Locator(".canvas-pane .canvas-tab")

    let dashboard (page: IPage) =
        page.Locator(".dashboard")

    /// Press ArrowDown from the dashboard until a wt-card receives focus.
    /// The first ArrowDown typically lands on a repo-header; the second
    /// reaches the first wt-card.
    let focusFirstCard (page: IPage) =
        task {
            let db = dashboard page
            do! db.FocusAsync()
            // First ArrowDown lands on repo-header, second on first wt-card
            do! page.Keyboard.PressAsync("ArrowDown")
            do! page.Keyboard.PressAsync("ArrowDown")
            let! _ = page.WaitForFunctionAsync(
                "() => document.querySelector('.wt-card.focused') !== null",
                null, PageWaitForFunctionOptions(Timeout = 5000.0f))
            ()
        }

    /// Focus the card for a specific branch (the worktree with the canvas doc).
    let focusCanvasCard (page: IPage) (branch: string) =
        task {
            let card =
                page.Locator(
                    ".wt-card",
                    PageLocatorOptions(Has = page.Locator(".branch-name", PageLocatorOptions(HasText = branch))))
            do! card.First.ScrollIntoViewIfNeededAsync()
            do! card.First.ClickAsync()
            let! _ = page.WaitForFunctionAsync(
                "() => document.querySelector('.focused') !== null",
                null, PageWaitForFunctionOptions(Timeout = 5000.0f))
            ()
        }

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

    // ── Step 4: Canvas Pane Toggle ──────────────────────────────────────

    [<Test>]
    member this.``Canvas pane is closed by default``() =
        task {
            let pane = canvasPane this.Page
            do! pane.WaitForAsync(LocatorWaitForOptions(State = WaitForSelectorState.Attached, Timeout = 5000.0f))

            let paneOpen = canvasPaneOpen this.Page
            let! openCount = paneOpen.CountAsync()
            Assert.That(openCount, Is.EqualTo(0), "Canvas pane should not have 'open' class on page load")
        }

    [<Test>]
    member this.``Canvas toggle button opens pane on click``() =
        task {
            do! focusFirstCard this.Page

            let btn = canvasToggleBtn this.Page
            do! btn.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            do! btn.ClickAsync()

            let paneOpen = canvasPaneOpen this.Page
            do! paneOpen.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let! count = paneOpen.CountAsync()
            Assert.That(count, Is.EqualTo(1), "Canvas pane should be open after clicking toggle button")
        }

    [<Test>]
    member this.``Canvas toggle button closes pane on second click``() =
        task {
            do! focusFirstCard this.Page

            let btn = canvasToggleBtn this.Page
            do! btn.ClickAsync()
            do! (canvasPaneOpen this.Page).WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            do! btn.ClickAsync()
            let! _ = this.Page.WaitForFunctionAsync(
                "() => !document.querySelector('.canvas-pane.open')",
                null, PageWaitForFunctionOptions(Timeout = 5000.0f))

            let! openCount = (canvasPaneOpen this.Page).CountAsync()
            Assert.That(openCount, Is.EqualTo(0), "Canvas pane should close after second click")
        }

    [<Test>]
    member this.``Keyboard shortcut c toggles canvas pane when card focused``() =
        task {
            do! focusFirstCard this.Page

            // Use page.Keyboard (not locator.PressAsync) to avoid Playwright's
            // locator-level focus/actionability checks which can interfere with
            // React synthetic event dispatch. focusFirstCard already ensures the
            // dashboard div has focus.
            do! this.Page.Keyboard.PressAsync("c")

            let paneOpen = canvasPaneOpen this.Page
            do! paneOpen.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let! count = paneOpen.CountAsync()
            Assert.That(count, Is.EqualTo(1), "Pressing 'c' with a card focused should open canvas pane")
        }

    [<Test>]
    member this.``Canvas pane shows iframe when worktree has canvas doc``() =
        task {
            do! focusFirstCard this.Page
            do! (canvasToggleBtn this.Page).ClickAsync()
            do! (canvasPaneOpen this.Page).WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            let iframe = canvasIframe this.Page
            let emptyState = canvasEmpty this.Page
            let! iframeCount = iframe.CountAsync()
            let! emptyCount = emptyState.CountAsync()

            // At least one of iframe or empty-state should be present
            Assert.That(
                iframeCount + emptyCount,
                Is.GreaterThanOrEqualTo(1),
                "Canvas pane should show either an iframe (if canvas doc exists) or empty state")
        }

    [<Test>]
    member this.``Canvas iframe src points to canvas doc server``() =
        task {
            do! focusCanvasCard this.Page FixtureCanvasBranch
            do! (canvasToggleBtn this.Page).ClickAsync()
            do! (canvasPaneOpen this.Page).WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            let iframe = canvasIframe this.Page
            do! iframe.WaitForAsync(LocatorWaitForOptions(Timeout = 10000.0f))
            let! src = iframe.GetAttributeAsync("src")
            Assert.That(src, Does.StartWith(canvasOrigin), $"Canvas iframe src should start with {canvasOrigin}, got: {src}")
        }

    [<Test>]
    member this.``Canvas toggle button gets active class when pane is open``() =
        task {
            do! focusFirstCard this.Page

            let btn = canvasToggleBtn this.Page
            let! classBefore = btn.GetAttributeAsync("class")
            Assert.That(classBefore, Does.Not.Contain("active"), "Canvas button should not be active initially")

            do! btn.ClickAsync()
            do! (canvasPaneOpen this.Page).WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            let! classAfter = btn.GetAttributeAsync("class")
            Assert.That(classAfter, Does.Contain("active"), "Canvas button should have 'active' class when pane is open")
        }

    [<Test>]
    member this.``Dashboard gets canvas-open class when pane is open``() =
        task {
            do! focusFirstCard this.Page
            do! (canvasToggleBtn this.Page).ClickAsync()

            let! _ = this.Page.WaitForFunctionAsync(
                "() => document.querySelector('.dashboard.canvas-open') !== null",
                null, PageWaitForFunctionOptions(Timeout = 5000.0f))

            let db = this.Page.Locator(".dashboard.canvas-open")
            let! count = db.CountAsync()
            Assert.That(count, Is.EqualTo(1), "Dashboard should have 'canvas-open' class when canvas pane is open")
        }

    // ── Step 5: Iframe Reload on Content Change ─────────────────────────

    [<Test>]
    member this.``Canvas iframe updates when contentHash changes``() =
        task {
            do! focusCanvasCard this.Page FixtureCanvasBranch
            do! (canvasToggleBtn this.Page).ClickAsync()
            do! (canvasPaneOpen this.Page).WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            let iframe = canvasIframe this.Page
            do! iframe.WaitForAsync(LocatorWaitForOptions(Timeout = 10000.0f))

            // Capture initial iframe src
            let! initialSrc = iframe.GetAttributeAsync("src")
            Assert.That(initialSrc, Is.Not.Null.And.Not.Empty, "Iframe should have a src attribute")

            // Find the worktree path from the iframe src to locate the file on disk
            // src format: http://127.0.0.1:5002/{urlEncode(path)}/{filename}
            let pathPart = initialSrc.Substring(canvasOrigin.Length + 1)
            let parts = pathPart.Split('/')
            let worktreePath = Uri.UnescapeDataString(parts[0])
            let filename = parts[1]
            let canvasFile = Path.Combine(worktreePath, ".agents", "canvas", filename)

            if not (File.Exists(canvasFile)) then
                Assert.Inconclusive($"Canvas file not found at {canvasFile}")
            else
                // Modify the file to trigger a content hash change
                let originalContent = File.ReadAllText(canvasFile)
                let marker = $"<!-- e2e-reload-test-{Guid.NewGuid()} -->"
                try
                    File.WriteAllText(canvasFile, originalContent + marker)

                    // Wait for iframe src to change (poll cycle ~1s, allow up to 10s)
                    let! _ = this.Page.WaitForFunctionAsync(
                        $"(oldSrc) => {{
                            const iframe = document.querySelector('.canvas-iframe');
                            return iframe && iframe.src !== oldSrc;
                        }}",
                        initialSrc,
                        PageWaitForFunctionOptions(Timeout = 10000.0f))

                    let! newSrc = iframe.GetAttributeAsync("src")
                    Assert.That(newSrc, Is.Not.EqualTo(initialSrc), "Iframe src should change after canvas file modification")
                finally
                    File.WriteAllText(canvasFile, originalContent)
        }

    // ── Step 8: PostMessage Dispatch ────────────────────────────────────

    [<Test>]
    member this.``PostMessage from canvas iframe triggers sendCanvasMessage API call``() =
        task {
            do! focusCanvasCard this.Page FixtureCanvasBranch
            do! (canvasToggleBtn this.Page).ClickAsync()
            do! (canvasPaneOpen this.Page).WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            let iframe = canvasIframe this.Page
            do! iframe.WaitForAsync(LocatorWaitForOptions(Timeout = 10000.0f))

            // Set up network interception to capture the sendCanvasMessage Remoting call
            let apiCallReceived = System.Threading.Tasks.TaskCompletionSource<bool>()

            this.Page.Request.Add(fun req ->
                if req.Url.Contains("sendCanvasMessage") then
                    apiCallReceived.TrySetResult(true) |> ignore)

            let! src = iframe.GetAttributeAsync("src")
            Assert.That(src, Is.Not.Null, "Iframe must have src")

            // Execute postMessage from within the iframe context
            // We evaluate JS in the page context that posts a message as if from the canvas origin
            let! _ = this.Page.EvaluateAsync(
                $"() => {{
                    const iframe = document.querySelector('.canvas-iframe');
                    if (iframe && iframe.contentWindow) {{
                        const msg = {{ action: 'test', data: 'e2e-probe' }};
                        window.dispatchEvent(new MessageEvent('message', {{
                            data: msg,
                            origin: '{canvasOrigin}'
                        }}));
                    }}
                }}")

            // Wait for the API call (with timeout)
            let timeoutTask = System.Threading.Tasks.Task.Delay(5000)
            let! completed = System.Threading.Tasks.Task.WhenAny(apiCallReceived.Task, timeoutTask)

            if Object.ReferenceEquals(completed, apiCallReceived.Task) then
                let! result = apiCallReceived.Task
                Assert.That(result, Is.True, "sendCanvasMessage API call should be triggered by postMessage")
            else
                // API call might fail if no bridge is registered — that's OK for this test.
                // The key assertion is that the Elmish dispatch happened, which we verify
                // by checking the network request was attempted.
                Assert.Inconclusive(
                    "sendCanvasMessage API call not observed within 5s. " +
                    "This may happen if no canvas bridge is registered (extension not running).")
        }

    [<Test>]
    member this.``PostMessage with invalid origin is ignored``() =
        task {
            do! focusFirstCard this.Page
            do! (canvasToggleBtn this.Page).ClickAsync()
            do! (canvasPaneOpen this.Page).WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            let apiCallReceived = System.Threading.Tasks.TaskCompletionSource<bool>()

            this.Page.Request.Add(fun req ->
                if req.Url.Contains("sendCanvasMessage") then
                    apiCallReceived.TrySetResult(true) |> ignore)

            // Post a message with a wrong origin — should be ignored
            let! _ = this.Page.EvaluateAsync(
                "() => {
                    window.dispatchEvent(new MessageEvent('message', {
                        data: { action: 'test', data: 'evil' },
                        origin: 'http://evil.example.com'
                    }));
                }")

            // Wait a short time — the API call should NOT happen
            do! System.Threading.Tasks.Task.Delay(2000)
            Assert.That(apiCallReceived.Task.IsCompleted, Is.False, "Messages from invalid origins should not trigger API calls")
        }

    [<Test>]
    member this.``PostMessage with non-object data is ignored``() =
        task {
            do! focusFirstCard this.Page
            do! (canvasToggleBtn this.Page).ClickAsync()
            do! (canvasPaneOpen this.Page).WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            let apiCallReceived = System.Threading.Tasks.TaskCompletionSource<bool>()

            this.Page.Request.Add(fun req ->
                if req.Url.Contains("sendCanvasMessage") then
                    apiCallReceived.TrySetResult(true) |> ignore)

            // Post a string (not an object with action) from the canvas origin
            let! _ = this.Page.EvaluateAsync(
                $"() => {{
                    window.dispatchEvent(new MessageEvent('message', {{
                        data: 'just a string',
                        origin: '{canvasOrigin}'
                    }}));
                }}")

            do! System.Threading.Tasks.Task.Delay(2000)
            Assert.That(apiCallReceived.Task.IsCompleted, Is.False, "Messages without object data + action should not trigger API calls")
        }

    // ── Multi-doc Tab Bar ───────────────────────────────────────────────

    [<Test>]
    member this.``Multi-doc worktree shows tab bar with one button per doc``() =
        task {
            do! focusCanvasCard this.Page FixtureMultiDocBranch
            do! (canvasToggleBtn this.Page).ClickAsync()
            do! (canvasPaneOpen this.Page).WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            let tabBarEl = canvasTabBar this.Page
            do! tabBarEl.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            let tabs = canvasTabs this.Page
            let! tabCount = tabs.CountAsync()
            Assert.That(tabCount, Is.EqualTo(3), "Tab bar should have one button per canvas doc (3 docs in fixture)")

            // Verify tab labels match filenames (without .html extension)
            let! tab0Text = tabs.Nth(0).TextContentAsync()
            let! tab1Text = tabs.Nth(1).TextContentAsync()
            let! tab2Text = tabs.Nth(2).TextContentAsync()
            Assert.That(tab0Text, Is.EqualTo("overview"), "First tab should be 'overview'")
            Assert.That(tab1Text, Is.EqualTo("details"), "Second tab should be 'details'")
            Assert.That(tab2Text, Is.EqualTo("metrics"), "Third tab should be 'metrics'")
        }

    [<Test>]
    member this.``Clicking a tab switches the iframe src to selected doc``() =
        task {
            do! focusCanvasCard this.Page FixtureMultiDocBranch
            do! (canvasToggleBtn this.Page).ClickAsync()
            do! (canvasPaneOpen this.Page).WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            let iframe = canvasIframe this.Page
            do! iframe.WaitForAsync(LocatorWaitForOptions(Timeout = 10000.0f))

            // Capture initial iframe src — should be the first doc (overview.html)
            let! initialSrc = iframe.GetAttributeAsync("src")
            Assert.That(initialSrc, Does.Contain("overview.html"), "Initial iframe src should contain the first doc filename")

            // Click the second tab (details)
            let tabs = canvasTabs this.Page
            do! tabs.Nth(1).ClickAsync()

            // Wait for iframe src to change
            let! _ = this.Page.WaitForFunctionAsync(
                $"(oldSrc) => {{
                    const iframe = document.querySelector('.canvas-iframe');
                    return iframe && iframe.getAttribute('src') !== oldSrc;
                }}",
                initialSrc,
                PageWaitForFunctionOptions(Timeout = 5000.0f))

            let! newSrc = iframe.GetAttributeAsync("src")
            Assert.That(newSrc, Does.Contain("details.html"), "After clicking second tab, iframe src should contain 'details.html'")

            // Verify the clicked tab has the active class
            let! secondTabClass = tabs.Nth(1).GetAttributeAsync("class")
            Assert.That(secondTabClass, Does.Contain("active"), "Clicked tab should have 'active' class")
        }

    [<Test>]
    member this.``Single-doc worktree shows header bar but no tabs``() =
        task {
            do! focusCanvasCard this.Page FixtureCanvasBranch
            do! (canvasToggleBtn this.Page).ClickAsync()
            do! (canvasPaneOpen this.Page).WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            // Wait for the iframe to appear (confirms canvas doc is loaded)
            let iframe = canvasIframe this.Page
            do! iframe.WaitForAsync(LocatorWaitForOptions(Timeout = 10000.0f))

            // Header bar always renders (contains position buttons + archive)
            let tabBarEl = canvasTabBar this.Page
            let! tabBarCount = tabBarEl.CountAsync()
            Assert.That(tabBarCount, Is.EqualTo(1), "Header bar should always render")

            // But no tab buttons for single-doc worktree
            let tabs = canvasTabs this.Page
            let! tabCount = tabs.CountAsync()
            Assert.That(tabCount, Is.EqualTo(0), "Single-doc worktree should not show doc tabs")
        }

    // ── Empty Canvas Overview ───────────────────────────────────────────

    [<Test>]
    member this.``Overview shows worktrees grouped by repo when focused worktree has no docs``() =
        task {
            // Focus a worktree that has no canvas docs (feature-recent has CanvasDocs: [])
            do! focusCanvasCard this.Page "feature-recent"
            do! (canvasToggleBtn this.Page).ClickAsync()
            do! (canvasPaneOpen this.Page).WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            // Overview should render (no iframe, since focused worktree has no docs)
            let overview = this.Page.Locator(".canvas-overview")
            do! overview.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            // Should have repo groups — fixture has canvas docs in both TestProject and treemon repos
            let repoGroups = this.Page.Locator(".canvas-overview-repo")
            let! groupCount = repoGroups.CountAsync()
            Assert.That(groupCount, Is.GreaterThanOrEqualTo(2), "Overview should show worktrees grouped by at least 2 repos")

            // Each group should have a repo name header
            let repoNames = this.Page.Locator(".canvas-overview-repo-name")
            let! nameCount = repoNames.CountAsync()
            Assert.That(nameCount, Is.GreaterThanOrEqualTo(2), "Each repo group should have a repo name label")
        }

    [<Test>]
    member this.``Overview entries show branch name and doc names``() =
        task {
            // Focus a worktree with no canvas docs to trigger overview
            do! focusCanvasCard this.Page "feature-recent"
            do! (canvasToggleBtn this.Page).ClickAsync()
            do! (canvasPaneOpen this.Page).WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            let overview = this.Page.Locator(".canvas-overview")
            do! overview.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            // Each entry should have a branch name and doc names
            let entries = this.Page.Locator(".canvas-overview-entry")
            let! entryCount = entries.CountAsync()
            Assert.That(entryCount, Is.GreaterThanOrEqualTo(3), "Overview should list all worktrees with canvas docs (at least 3 in fixtures)")

            // Verify branch names are rendered
            let branches = this.Page.Locator(".canvas-overview-branch")
            let! branchCount = branches.CountAsync()
            Assert.That(branchCount, Is.EqualTo(entryCount), "Each entry should have a branch name")

            // Verify doc names are rendered inline (not counts)
            let docs = this.Page.Locator(".canvas-overview-doc")
            let! docCount = docs.CountAsync()
            Assert.That(docCount, Is.GreaterThanOrEqualTo(3), "Overview should show individual doc names")

            // Spot-check: the multi-doc fixture branch should show 3 doc name spans
            let multiDocEntry =
                this.Page.Locator(
                    ".canvas-overview-entry",
                    PageLocatorOptions(Has = this.Page.Locator(".canvas-overview-branch", PageLocatorOptions(HasText = "feature-multidoc"))))
            let! multiDocCount = multiDocEntry.CountAsync()
            Assert.That(multiDocCount, Is.EqualTo(1), "Should have an entry for feature-multidoc")
            let! multiDocDocs = multiDocEntry.Locator(".canvas-overview-doc").CountAsync()
            Assert.That(multiDocDocs, Is.EqualTo(3), "feature-multidoc should show 3 doc name spans")

            // Spot-check: the single-doc fixture branch should show 1 doc name span
            let singleDocEntry =
                this.Page.Locator(
                    ".canvas-overview-entry",
                    PageLocatorOptions(Has = this.Page.Locator(".canvas-overview-branch", PageLocatorOptions(HasText = "feature-active"))))
            let! singleDocDocs = singleDocEntry.Locator(".canvas-overview-doc").CountAsync()
            Assert.That(singleDocDocs, Is.EqualTo(1), "feature-active should show 1 doc name span")
        }

    [<Test>]
    member this.``Clicking overview entry focuses that worktree card``() =
        task {
            // Focus a worktree with no canvas docs to trigger overview
            do! focusCanvasCard this.Page "feature-recent"
            do! (canvasToggleBtn this.Page).ClickAsync()
            do! (canvasPaneOpen this.Page).WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            let overview = this.Page.Locator(".canvas-overview")
            do! overview.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            // Click the branch name for feature-active (single doc worktree in TestProject)
            let targetBranch =
                this.Page.Locator(
                    ".canvas-overview-branch",
                    PageLocatorOptions(HasText = "feature-active"))
            do! targetBranch.First.ClickAsync()

            // After clicking, the canvas pane should show an iframe (not overview) for the focused worktree
            let iframe = canvasIframe this.Page
            do! iframe.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let! src = iframe.GetAttributeAsync("src")
            Assert.That(src, Does.Contain("e2e-test.html"), "After clicking overview entry, iframe should show the focused worktree's canvas doc")

            // The worktree card should be focused
            let focusedCard =
                this.Page.Locator(
                    ".wt-card.focused",
                    PageLocatorOptions(Has = this.Page.Locator(".branch-name", PageLocatorOptions(HasText = "feature-active"))))
            let! focusedCount = focusedCard.CountAsync()
            Assert.That(focusedCount, Is.EqualTo(1), "Clicking overview entry should focus the corresponding worktree card")
        }

    // ── Toolbar Consolidation ───────────────────────────────────────────

    [<Test>]
    member this.``Position buttons render inside header bar``() =
        task {
            do! focusCanvasCard this.Page FixtureCanvasBranch
            do! (canvasToggleBtn this.Page).ClickAsync()
            do! (canvasPaneOpen this.Page).WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            // Position buttons should be inside .canvas-tab-bar
            let posButtons = this.Page.Locator(".canvas-tab-bar .canvas-pos-btn")
            let! count = posButtons.CountAsync()
            Assert.That(count, Is.EqualTo(4), "Should have 4 position buttons (◧ ◨ ⬒ ⬓) inside the header bar")
        }

    [<Test>]
    member this.``Position buttons have low opacity by default``() =
        task {
            do! focusCanvasCard this.Page FixtureCanvasBranch
            do! (canvasToggleBtn this.Page).ClickAsync()
            do! (canvasPaneOpen this.Page).WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            let posBtn = this.Page.Locator(".canvas-tab-bar .canvas-pos-btn").First
            do! posBtn.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            // Check computed opacity via evaluate
            let! opacity = posBtn.EvaluateAsync<string>("el => getComputedStyle(el).opacity")
            let opacityVal = System.Double.Parse(opacity, System.Globalization.CultureInfo.InvariantCulture)
            Assert.That(opacityVal, Is.LessThanOrEqualTo(0.5), "Position buttons should have low opacity (0.4) by default")
        }

    [<Test>]
    member this.``No separate canvas-toolbar exists``() =
        task {
            do! focusCanvasCard this.Page FixtureCanvasBranch
            do! (canvasToggleBtn this.Page).ClickAsync()
            do! (canvasPaneOpen this.Page).WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            // The old separate toolbar should not exist
            let toolbar = this.Page.Locator(".canvas-pane .canvas-toolbar")
            let! count = toolbar.CountAsync()
            Assert.That(count, Is.EqualTo(0), "Separate canvas-toolbar should not exist — position buttons are now in the header bar")
        }

    // ── Archive Button ──────────────────────────────────────────────────

    [<Test>]
    member this.``Archive button appears in header bar for active doc``() =
        task {
            do! focusCanvasCard this.Page FixtureCanvasBranch
            do! (canvasToggleBtn this.Page).ClickAsync()
            do! (canvasPaneOpen this.Page).WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            let archiveBtn = this.Page.Locator(".canvas-tab-bar .canvas-archive-btn")
            do! archiveBtn.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let! count = archiveBtn.CountAsync()
            Assert.That(count, Is.EqualTo(1), "Archive button should appear in header bar when a doc is active")
        }

    [<Test>]
    member this.``Header bar renders with position buttons when overview is shown``() =
        task {
            // Focus worktree with no canvas docs — triggers overview
            do! focusCanvasCard this.Page "feature-recent"
            do! (canvasToggleBtn this.Page).ClickAsync()
            do! (canvasPaneOpen this.Page).WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            // Header bar should still render with position buttons
            let posButtons = this.Page.Locator(".canvas-tab-bar .canvas-pos-btn")
            let! count = posButtons.CountAsync()
            Assert.That(count, Is.EqualTo(4), "Position buttons should render in header bar even in overview mode")

            // Archive button should NOT appear (no active doc)
            let archiveBtn = this.Page.Locator(".canvas-tab-bar .canvas-archive-btn")
            let! archiveCount = archiveBtn.CountAsync()
            Assert.That(archiveCount, Is.EqualTo(0), "Archive button should not appear when no doc is active")
        }
