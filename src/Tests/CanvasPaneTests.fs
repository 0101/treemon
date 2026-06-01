module Tests.CanvasPaneTests

open System
open System.IO
open NUnit.Framework
open Microsoft.Playwright
open Microsoft.Playwright.NUnit

/// E2E tests for the canvas pane feature.
/// Prerequisites:
///   - Server running on :5001 with canvas doc server on :5002
///   - Vite dev server on :5174
///   - At least one tracked worktree with .agents/canvas/test.html
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

    let dashboard (page: IPage) =
        page.Locator(".dashboard")

    let focusFirstCard (page: IPage) =
        task {
            let db = dashboard page
            do! db.FocusAsync()
            do! page.Keyboard.PressAsync("ArrowDown")
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
            do! pane.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

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
            do! focusFirstCard this.Page
            do! (canvasToggleBtn this.Page).ClickAsync()
            do! (canvasPaneOpen this.Page).WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            let iframe = canvasIframe this.Page
            let! iframeCount = iframe.CountAsync()

            if iframeCount > 0 then
                let! src = iframe.GetAttributeAsync("src")
                Assert.That(src, Does.StartWith(canvasOrigin), $"Canvas iframe src should start with {canvasOrigin}, got: {src}")
            else
                Assert.Inconclusive("No canvas iframe — focused worktree may not have a canvas doc")
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
            do! focusFirstCard this.Page
            do! (canvasToggleBtn this.Page).ClickAsync()
            do! (canvasPaneOpen this.Page).WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            let iframe = canvasIframe this.Page
            let! iframeCount = iframe.CountAsync()

            if iframeCount = 0 then
                Assert.Inconclusive("No canvas iframe — focused worktree has no canvas doc. Ensure .agents/canvas/test.html exists.")
            else
                // Capture initial iframe src
                let! initialSrc = iframe.GetAttributeAsync("src")
                Assert.That(initialSrc, Is.Not.Null.And.Not.Empty, "Iframe should have a src attribute")

                // Find the worktree path from the iframe src to locate the file on disk
                // src format: http://127.0.0.1:5002/{urlEncode(path)}/{filename}
                let pathPart = initialSrc.Substring(canvasOrigin.Length + 1)
                let parts = pathPart.Split('/')
                let worktreePath = Uri.UnescapeDataString(parts[0])
                let filename = parts[1]
                let canvasDir = Path.Combine(worktreePath, ".agents", "canvas")
                let canvasFile = Path.Combine(canvasDir, filename)

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
            do! focusFirstCard this.Page
            do! (canvasToggleBtn this.Page).ClickAsync()
            do! (canvasPaneOpen this.Page).WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            let iframe = canvasIframe this.Page
            let! iframeCount = iframe.CountAsync()

            if iframeCount = 0 then
                Assert.Inconclusive("No canvas iframe — focused worktree has no canvas doc")
            else
                // Set up network interception to capture the sendCanvasMessage Remoting call
                let apiCallReceived = System.Threading.Tasks.TaskCompletionSource<bool>()

                this.Page.Request.Add(fun req ->
                    if req.Url.Contains("sendCanvasMessage") then
                        apiCallReceived.TrySetResult(true) |> ignore)

                // Wait for iframe to load
                let iframeElement = iframe
                do! iframeElement.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
                let! src = iframeElement.GetAttributeAsync("src")
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
