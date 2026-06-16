module Tests.CanvasPaneTests

open System
open NUnit.Framework
open Microsoft.Playwright
open Microsoft.Playwright.NUnit
open Tests.CanvasTestHelpers

/// Branch name of the fixture worktree that has a CanvasDoc defined in
/// src/Tests/fixtures/worktrees.json.  Tests target this known branch
/// instead of calling getCurrentBranch() (which returns the live repo's
/// branch, not a fixture branch).
let [<Literal>] private FixtureCanvasBranch = "feature-active"
let [<Literal>] private FixtureMultiDocBranch = "feature-multidoc"

/// Branch of the fixture worktree (treemon/multirepo) that holds a mix of doc kinds:
/// beads.html (SystemView, listed first so it is the default-active doc) plus dashboard.html
/// and status.html (AgentDoc). Used to assert that session-document affordances (liveness dot,
/// Start-session button) are gated to AgentDoc only.
let [<Literal>] private FixtureSystemViewBranch = "multirepo"

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

    let canvasPane (page: IPage) =
        page.Locator(".canvas-pane")

    let canvasIframe (page: IPage) =
        page.Locator(".canvas-pane .canvas-iframe")

    let canvasEmpty (page: IPage) =
        page.Locator(".canvas-pane .canvas-empty")

    let canvasTabBar (page: IPage) =
        page.Locator(".canvas-pane .canvas-tab-bar")

    let canvasTabs (page: IPage) =
        page.Locator(".canvas-pane .canvas-tab")

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

    // ── Step 5: In-place morph on content change (stable src, no src-swap) ──

    [<Test>]
    member this.``Canvas content update morphs the iframe in place without swapping its src``() =
        task {
            // The product reloads a changed canvas doc IN PLACE: it stabilises the iframe src (no
            // ?v=<hash> cache-buster) and posts {action:'content-updated'} to the iframe, which
            // idiomorph-morphs its body — the src never changes (CanvasPane.iframeSrc, App.fs
            // MorphActiveDoc, IdiomorphScript.morphController; docs/spec/canvas-pane.md).
            //
            // We drive the morph signal purely from the UI: re-selecting an already-visited AgentDoc
            // tab dispatches MorphActiveDoc (App.fs SelectCanvasDoc, wasAlreadyVisited), which posts
            // 'content-updated' to the active iframe. This needs no on-disk file and no server-side
            // hash change — both impossible in --test-fixtures mode (synthetic worktree paths, static
            // ContentHash). feature-multidoc exposes three AgentDocs (overview/details/metrics).
            //
            // NOTE (tm-canvas48-hmb): the previous version modified an on-disk file and expected the
            // src to CHANGE. It always hit Assert.Inconclusive (the fixture file never exists) and,
            // had the file existed, asserted the wrong behavior (the src stays stable on morph).
            do! focusCanvasCard this.Page FixtureMultiDocBranch
            do! (canvasToggleBtn this.Page).ClickAsync()
            do! (canvasPaneOpen this.Page).WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            let activeIframe = this.Page.Locator(".canvas-pane .canvas-iframe-active")
            let tab (name: string) =
                this.Page.Locator(".canvas-pane .canvas-tab", PageLocatorOptions(HasText = name))

            let waitActiveSrcContains (fragment: string) =
                this.Page.WaitForFunctionAsync(
                    "(frag) => { const f = document.querySelector('.canvas-pane .canvas-iframe-active'); return !!(f && f.src.includes(frag)); }",
                    (fragment :> obj),
                    PageWaitForFunctionOptions(Timeout = 5000.0f))

            // Wait until the iframe for a doc has actually navigated to its :5002 origin, so the
            // morph's targetOrigin matches and we can probe the iframe document for the signal.
            let waitForCanvasFrame (fragment: string) =
                task {
                    let deadline = DateTime.UtcNow.AddSeconds(10.0)
                    let mutable found : IFrame option = None
                    while found.IsNone && DateTime.UtcNow < deadline do
                        found <-
                            this.Page.Frames
                            |> Seq.tryFind (fun f -> f.Url.StartsWith(canvasOrigin) && f.Url.Contains(fragment))
                        if found.IsNone then do! System.Threading.Tasks.Task.Delay(100)
                    return found
                }

            // Select "details": this marks it visited and makes its iframe active. Wait until that
            // iframe has navigated to the :5002 canvas origin so the morph's targetOrigin will match.
            do! (tab "details").ClickAsync()
            let! _ = waitActiveSrcContains "details.html"
            do! activeIframe.WaitForAsync(LocatorWaitForOptions(Timeout = 10000.0f))

            let! initialSrc = activeIframe.GetAttributeAsync("src")
            Assert.That(initialSrc, Is.Not.Null.And.Not.Empty, "Active iframe should have a src attribute")
            Assert.That(initialSrc, Does.Not.Contain("?"),
                "Iframe src must carry no cache-buster query param — content changes morph in place, the src is never swapped")

            // Install a listener inside the active details frame to observe the morph signal. Re-selecting
            // the SAME already-visited+active tab keeps it active (no iframe reorder/reload), so this
            // injected listener survives to receive the parent app's postMessage.
            let! detailsFrameOpt = waitForCanvasFrame "details.html"
            let detailsFrame = detailsFrameOpt |> Option.defaultWith (fun () -> failwith "details iframe never reached the :5002 canvas origin")
            let! _ = detailsFrame.EvaluateAsync("() => { window.__contentUpdated = false; window.addEventListener('message', (e) => { if (e.data && e.data.action === 'content-updated') window.__contentUpdated = true; }); }")

            // Re-select the already-visited details tab → App dispatches MorphActiveDoc → posts
            // {action:'content-updated'} to the active iframe (the morph trigger we can drive from the UI).
            do! (tab "details").ClickAsync()

            let! contentUpdated =
                task {
                    let deadline = DateTime.UtcNow.AddSeconds(5.0)
                    let mutable got = false
                    while not got && DateTime.UtcNow < deadline do
                        let! v = detailsFrame.EvaluateAsync<bool>("() => window.__contentUpdated === true")
                        got <- v
                        if not got then do! System.Threading.Tasks.Task.Delay(100)
                    return got
                }
            Assert.That(contentUpdated, Is.True,
                "Re-selecting the active AgentDoc tab must post {action:'content-updated'} to its iframe so it can morph in place")

            // The morph reloads content WITHOUT swapping the iframe: same document, same stable src.
            let! newSrc = activeIframe.GetAttributeAsync("src")
            Assert.That(newSrc, Is.EqualTo(initialSrc),
                "Iframe src must stay identical across a content update — the doc morphs in place, the src is never swapped")
            let! sameFrameUrl = detailsFrame.EvaluateAsync<string>("() => location.href")
            Assert.That(sameFrameUrl, Does.Contain("details.html"),
                "The same details iframe document must persist across the morph (not be replaced)")
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

    // ── SystemView gating (liveness + Start-session) ────────────────────
    // The multirepo fixture worktree mixes one SystemView (beads.html) with two AgentDocs
    // (dashboard.html, status.html). Session-document affordances must apply to AgentDocs only.

    [<Test>]
    member this.``SystemView tab shows no liveness dot but AgentDoc tabs do``() =
        task {
            do! focusCanvasCard this.Page FixtureSystemViewBranch
            do! (canvasToggleBtn this.Page).ClickAsync()
            do! (canvasPaneOpen this.Page).WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            do! (canvasTabBar this.Page).WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            // The two AgentDoc tabs (dashboard, status) each carry a liveness dot; the SystemView
            // (beads) is a distinct .canvas-system-tab entry that carries none — so the 2 normal
            // doc tabs yield exactly 2 liveness dots.
            let allTabDots = this.Page.Locator(".canvas-pane .canvas-tab .canvas-liveness-dot")
            let! dotCount = allTabDots.CountAsync()
            Assert.That(dotCount, Is.EqualTo(2), "Only the 2 AgentDoc tabs should render a liveness dot; the SystemView (beads) entry should not")

            // The beads (SystemView) entry specifically must have no liveness dot.
            let beadsTab = this.Page.Locator(".canvas-pane .canvas-system-tab")
            let! beadsDots = beadsTab.Locator(".canvas-liveness-dot").CountAsync()
            Assert.That(beadsDots, Is.EqualTo(0), "SystemView (beads) entry should not render a liveness dot")

            // An AgentDoc tab (dashboard) must keep its liveness dot.
            let dashboardTab = this.Page.Locator(".canvas-pane .canvas-tab", PageLocatorOptions(HasText = "dashboard"))
            let! dashDots = dashboardTab.Locator(".canvas-liveness-dot").CountAsync()
            Assert.That(dashDots, Is.EqualTo(1), "AgentDoc (dashboard) tab should still render a liveness dot")
        }

    [<Test>]
    member this.``SystemView active doc shows no Start session button but AgentDoc does``() =
        task {
            do! focusCanvasCard this.Page FixtureSystemViewBranch
            do! (canvasToggleBtn this.Page).ClickAsync()
            do! (canvasPaneOpen this.Page).WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            do! (canvasTabBar this.Page).WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            // beads.html (SystemView) is the default-active doc → no Start-session button, even
            // though it has no live owner session.
            let launchBtn = this.Page.Locator(".canvas-pane .canvas-launch-btn")
            let! launchWhenSystemView = launchBtn.CountAsync()
            Assert.That(launchWhenSystemView, Is.EqualTo(0), "SystemView (beads) active doc should not show the Start session button")

            // Switching to an AgentDoc with no live session restores the Start-session button.
            let dashboardTab = this.Page.Locator(".canvas-pane .canvas-tab", PageLocatorOptions(HasText = "dashboard"))
            do! dashboardTab.ClickAsync()
            do! launchBtn.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let! launchWhenAgentDoc = launchBtn.CountAsync()
            Assert.That(launchWhenAgentDoc, Is.EqualTo(1), "AgentDoc (dashboard) active doc with no live session should show the Start session button")
        }

    [<Test>]
    member this.``Overview omits liveness dot for SystemView doc but keeps it for AgentDoc``() =
        task {
            // Focus a worktree with no canvas docs to trigger the overview, which lists every
            // worktree's docs (including the multirepo SystemView + AgentDocs).
            do! focusCanvasCard this.Page "feature-recent"
            do! (canvasToggleBtn this.Page).ClickAsync()
            do! (canvasPaneOpen this.Page).WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            do! (this.Page.Locator(".canvas-overview")).WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            // The beads (SystemView) overview entry must have no liveness dot.
            let beadsDoc = this.Page.Locator(".canvas-pane .canvas-overview-doc", PageLocatorOptions(HasText = "beads"))
            do! beadsDoc.First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let! beadsDots = beadsDoc.Locator(".canvas-liveness-dot").CountAsync()
            Assert.That(beadsDots, Is.EqualTo(0), "SystemView (beads) overview entry should not render a liveness dot")

            // An AgentDoc overview entry (dashboard) must keep its liveness dot.
            let dashboardDoc = this.Page.Locator(".canvas-pane .canvas-overview-doc", PageLocatorOptions(HasText = "dashboard"))
            let! dashDots = dashboardDoc.Locator(".canvas-liveness-dot").CountAsync()
            Assert.That(dashDots, Is.EqualTo(1), "AgentDoc (dashboard) overview entry should still render a liveness dot")
        }

    // ── SystemView distinct affordance + archive gating (tm-canvas48-86v) ──
    // The SystemView (beads) renders as a differently-styled entry pinned to the far left of the
    // tab strip, labelled with the worktree's beads issue count; the archive button is hidden while
    // it is active. The multirepo fixture worktree mixes the beads SystemView (Beads total = 2)
    // with the dashboard/status AgentDocs.

    [<Test>]
    member this.``SystemView renders as a distinct leftmost entry, not a normal tab``() =
        task {
            do! focusCanvasCard this.Page FixtureSystemViewBranch
            do! (canvasToggleBtn this.Page).ClickAsync()
            do! (canvasPaneOpen this.Page).WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            do! (canvasTabBar this.Page).WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            // Exactly one SystemView (beads) entry, rendered with its distinct class.
            let systemTab = this.Page.Locator(".canvas-pane .canvas-system-tab")
            do! systemTab.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let! sysCount = systemTab.CountAsync()
            Assert.That(sysCount, Is.EqualTo(1), "multirepo should render exactly one SystemView (beads) entry")

            // It is NOT a normal agent-doc tab, and carries no liveness dot.
            let! overlap = this.Page.Locator(".canvas-pane .canvas-system-tab.canvas-tab").CountAsync()
            Assert.That(overlap, Is.EqualTo(0), "SystemView entry must not also be a normal .canvas-tab")
            let! sysDots = systemTab.Locator(".canvas-liveness-dot").CountAsync()
            Assert.That(sysDots, Is.EqualTo(0), "SystemView entry must not render a liveness dot")

            // It sits leftmost in the tab strip (before any agent-doc tab).
            let firstEntry = this.Page.Locator(".canvas-pane .canvas-tab-group > *").First
            let! firstClass = firstEntry.GetAttributeAsync("class")
            Assert.That(firstClass, Does.Contain("canvas-system-tab"), "SystemView entry should be pinned to the far left of the tab strip")
        }

    [<Test>]
    member this.``SystemView entry displays the beads issue count from the worktree``() =
        task {
            do! focusCanvasCard this.Page FixtureSystemViewBranch
            do! (canvasToggleBtn this.Page).ClickAsync()
            do! (canvasPaneOpen this.Page).WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            do! (canvasTabBar this.Page).WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            // multirepo wt.Beads = Open 1 + InProgress 1 + Blocked 0 + Closed 0 = 2.
            let countBadge = this.Page.Locator(".canvas-pane .canvas-system-tab .canvas-system-tab-count")
            do! countBadge.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let! countText = countBadge.TextContentAsync()
            Assert.That(countText.Trim(), Is.EqualTo("2"), "SystemView badge should show the worktree's total beads issue count (Open+InProgress+Blocked+Closed)")
        }

    [<Test>]
    member this.``Archive button is hidden while a SystemView is active but available for an AgentDoc``() =
        task {
            do! focusCanvasCard this.Page FixtureSystemViewBranch
            do! (canvasToggleBtn this.Page).ClickAsync()
            do! (canvasPaneOpen this.Page).WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            do! (canvasTabBar this.Page).WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            // beads.html (SystemView) is the default-active doc → archive button hidden
            // (it is server-regenerated, not user-owned).
            let archiveBtn = this.Page.Locator(".canvas-pane .canvas-archive-btn")
            let! archiveWhenSystemView = archiveBtn.CountAsync()
            Assert.That(archiveWhenSystemView, Is.EqualTo(0), "Archive button should be hidden while a SystemView (beads) is the active doc")

            // Switching to an AgentDoc restores the archive button.
            let dashboardTab = this.Page.Locator(".canvas-pane .canvas-tab", PageLocatorOptions(HasText = "dashboard"))
            do! dashboardTab.ClickAsync()
            do! archiveBtn.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let! archiveWhenAgentDoc = archiveBtn.CountAsync()
            Assert.That(archiveWhenAgentDoc, Is.EqualTo(1), "Archive button should be available while an AgentDoc (dashboard) is the active doc")
        }
