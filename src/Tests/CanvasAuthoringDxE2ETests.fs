module Tests.CanvasAuthoringDxE2ETests

open System
open System.IO
open NUnit.Framework
open Microsoft.Playwright
open Microsoft.Playwright.NUnit
open Shared
open Tests.CanvasTestHelpers

/// Splice the REAL server injection (Server.CanvasDocServer.buildInjection) into a doc exactly the
/// way CanvasDocServer.handleCanvasRequest does — before </head>, or prepended when there is none —
/// so a browser renders the genuine injected output (base reset, canvasSend, error overlay, …).
let private injectInto (kind: CanvasDocKind) (filename: string) (docHtml: string) =
    let injection = Server.CanvasDocServer.buildInjection kind filename
    if docHtml.Contains("</head>", StringComparison.OrdinalIgnoreCase)
    then docHtml.Replace("</head>", injection + "</head>", StringComparison.OrdinalIgnoreCase)
    else injection + docHtml

// ============================================================================
// Item 1 (base dark-theme reset) + Item 5 (cascade guards)
//
// Self-contained, modeled on BeadspaceCanvasTests: Playwright route interception
// serves the injected doc (no fixture worktree on disk needed). We then read the
// genuine computed background-color the injected reset produces in a real browser.
// ============================================================================
[<TestFixture>]
[<Category("E2E")>]
[<Category("Canvas")>]
[<Category("AuthoringDxE2E")>]
type CanvasInjectionThemeE2ETests() =
    inherit PageTest()

    override this.ContextOptions() =
        let opts = base.ContextOptions()
        opts.IgnoreHTTPSErrors <- true
        opts

    /// Intercept `urlGlob` and serve `html`, then navigate to `url` and wait for load.
    member private this.ServeAndGoto (urlGlob: string) (url: string) (html: string) =
        task {
            do! this.Page.RouteAsync(urlGlob, fun route ->
                route.FulfillAsync(RouteFulfillOptions(ContentType = "text/html; charset=utf-8", Body = html)))
            let! _ = this.Page.GotoAsync(url, PageGotoOptions(WaitUntil = WaitUntilState.Load))
            ()
        }

    [<Test>]
    member this.``Item 1: a plain-text canvas doc renders on the dark theme, not default white``() =
        task {
            let doc = "<!doctype html><html><head><title>plain</title></head><body>plain text</body></html>"
            let served = injectInto AgentDoc "plain.html" doc
            do! this.ServeAndGoto "**/plain.html" $"{ServerFixture.canvasUrl}/wt/plain.html" served

            let! bg = this.Page.EvaluateAsync<string>("() => getComputedStyle(document.body).backgroundColor")
            let! color = this.Page.EvaluateAsync<string>("() => getComputedStyle(document.body).color")
            // Injected reset paints :where(body){background:var(--bg-deep);color:var(--text-primary)},
            // resolving via the :where(:root) tokens to #1e1e2e / #cdd6f4.
            Assert.That(bg, Is.EqualTo("rgb(30, 30, 46)"), $"body background must be the dark reset #1e1e2e (was {bg})")
            Assert.That(bg, Is.Not.EqualTo("rgb(255, 255, 255)"), "body must NOT be default white")
            Assert.That(bg, Is.Not.EqualTo("rgba(0, 0, 0, 0)"), "body must NOT be transparent")
            Assert.That(color, Is.EqualTo("rgb(205, 214, 244)"), $"text colour must be the light reset #cdd6f4 for readability (was {color})")
        }

    [<Test>]
    member this.``Item 1: the base bakes in a readable type scale and single-column page (typography over boxes)``() =
        task {
            let doc = "<!doctype html><html><head><title>typo</title></head><body><h1>Title</h1><p id=p>Body copy long enough to fill the single column comfortably across the pane.</p></body></html>"
            let served = injectInto AgentDoc "typo.html" doc
            do! this.ServeAndGoto "**/typo.html" $"{ServerFixture.canvasUrl}/wt/typo.html" served

            let! bodyFont = this.Page.EvaluateAsync<string>("() => getComputedStyle(document.body).fontSize")
            let! bodyLine = this.Page.EvaluateAsync<string>("() => getComputedStyle(document.body).lineHeight")
            let! h1Font = this.Page.EvaluateAsync<string>("() => getComputedStyle(document.querySelector('h1')).fontSize")
            let! bodyMaxWidth = this.Page.EvaluateAsync<string>("() => getComputedStyle(document.body).maxWidth")
            let! pMaxWidth = this.Page.EvaluateAsync<string>("() => getComputedStyle(document.getElementById('p')).maxWidth")
            let! h1Family = this.Page.EvaluateAsync<string>("() => getComputedStyle(document.querySelector('h1')).fontFamily")
            let! h1Weight = this.Page.EvaluateAsync<string>("() => getComputedStyle(document.querySelector('h1')).fontWeight")
            // 15px / line-height 1.55 base + serif headings at weight 500 (h1 = 1.85rem = 29.6px) + a
            // ~800px single-column page (text fills the width), so a plain doc reads via type and whitespace rather than boxes.
            Assert.That(bodyFont, Is.EqualTo("15px"), $"body should default to a readable 15px (was {bodyFont})")
            Assert.That(bodyLine, Is.EqualTo("23.25px"), $"body line-height should be 1.55x = 23.25px (was {bodyLine})")
            Assert.That(h1Font, Is.EqualTo("29.6px"), $"h1 should be 1.85rem = 29.6px from the type scale (was {h1Font})")
            Assert.That(h1Family.ToLower(), Does.Contain("serif"), $"headings should use the serif stack (was {h1Family})")
            Assert.That(h1Weight, Is.EqualTo("500"), $"headings should not be bold — weight 500 (was {h1Weight})")
            Assert.That(bodyMaxWidth, Is.EqualTo("800px"), $"the page should cap at the ~800px single column (was {bodyMaxWidth})")
            Assert.That(pMaxWidth, Is.EqualTo("none"), $"paragraphs should fill the column, not carry a separate measure cap (was {pMaxWidth})")
        }

    [<Test>]
    member this.``Item 5a: a doc's own body background rule overrides the zero-specificity reset``() =
        task {
            let doc = "<!doctype html><html><head><style>body{background:rgb(0,128,0)}</style></head><body>owned</body></html>"
            let served = injectInto AgentDoc "owned.html" doc
            do! this.ServeAndGoto "**/owned.html" $"{ServerFixture.canvasUrl}/wt/owned.html" served

            let! bg = this.Page.EvaluateAsync<string>("() => getComputedStyle(document.body).backgroundColor")
            // The doc's own element rule body{} (0,0,1) must beat the injected :where(body) reset (0,0,0)
            // even though the reset is spliced AFTER the doc's <head> style.
            Assert.That(bg, Is.EqualTo("rgb(0, 128, 0)"), $"doc's own body rule must win over the :where() reset (was {bg})")
            Assert.That(bg, Is.Not.EqualTo("rgb(30, 30, 46)"), "the reset dark colour must NOT win over the doc's own rule")
        }

    [<Test>]
    member this.``Item 5b: the beads SystemView body background stays var(--bg-deep) under the reset``() =
        task {
            let templatePath =
                Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "Server", "BeadspaceTemplate.html"))
            let! template = File.ReadAllTextAsync(templatePath)
            let served = injectInto SystemView "beads.html" template

            // The dashboard fetches beads-data on load; stub it so the page settles cleanly.
            do! this.Page.RouteAsync("**/beads-data", fun route ->
                route.FulfillAsync(RouteFulfillOptions(ContentType = "application/json", Body = "[]")))
            do! this.ServeAndGoto "**/beads.html" $"{ServerFixture.canvasUrl}/wt/beads.html" served

            let! bg = this.Page.EvaluateAsync<string>("() => getComputedStyle(document.body).backgroundColor")
            // BeadspaceTemplate.html declares :root{--bg-deep:#1e1e2e} and body{background:var(--bg-deep)};
            // the template's element rule must keep painting the dashboard, unchanged by the reset.
            Assert.That(bg, Is.EqualTo("rgb(30, 30, 46)"), $"SystemView body must stay var(--bg-deep) #1e1e2e (was {bg})")

            // The body box reset must survive too: the template zeroes padding on its `body` selector
            // directly, so the injected zero-specificity :where(body){padding:2rem 2.25rem} (which would win the
            // source-order tiebreak against the universal *{padding:0}) can't add a gap.
            let! padding = this.Page.EvaluateAsync<string>("() => getComputedStyle(document.body).padding")
            Assert.That(padding, Is.EqualTo("0px"), $"SystemView body padding must stay 0 under the reset (was {padding})")
        }

// ============================================================================
// Item 2 (canvasSend tab switch) + Item 3 (doc JS error banner)
//
// Full-app pane E2E (server + Fable + Vite via ServerFixture.GlobalSetup). We
// intercept the canvas-doc-server iframe requests (ServerFixture.canvasUrl/.../<doc>)
// and serve the REAL injected doc, so the genuine in-iframe window.canvasSend /
// window.onerror / unhandledrejection drive the genuine Elmish pane.
// ============================================================================
[<TestFixture>]
[<Category("E2E")>]
[<Category("Canvas")>]
[<Category("AuthoringDxE2E")>]
type CanvasAuthoringDxPaneE2ETests() =
    inherit PageTest()

    let baseUrl = ServerFixture.viteUrl
    let [<Literal>] MultiDocBranch = "feature-multidoc"

    /// The HTML a real AgentDoc iframe receives: full injection at </head> plus `bodyScript` running
    /// in <body> AFTER the injected helpers (canvasSend / window.onerror / unhandledrejection) exist.
    let injectedDoc (filename: string) (bodyScript: string) =
        let head = Server.CanvasDocServer.buildInjection AgentDoc filename
        String.concat "" [
            "<!doctype html><html><head><title>"; filename; "</title>"; head; "</head>"
            "<body><p>doc body: "; filename; "</p><script>"; bodyScript; "</script></body></html>" ]

    override this.ContextOptions() =
        let opts = base.ContextOptions()
        opts.IgnoreHTTPSErrors <- true
        opts

    [<SetUp>]
    member this.NavigateToDashboard() =
        task {
            let! _ = this.Page.GotoAsync(baseUrl)
            do! this.Page.Locator(".wt-card .branch-name").First.WaitForAsync(LocatorWaitForOptions(Timeout = 15000.0f))
        }

    /// Register canvas-doc-server iframe interception BEFORE the pane opens: overview.html runs
    /// `overviewScript`; the sibling docs are inert (just the injection) so navigating to them loads.
    member private this.RouteDocs (overviewScript: string) =
        task {
            do! this.Page.RouteAsync("**/overview.html", fun route ->
                route.FulfillAsync(RouteFulfillOptions(
                    ContentType = "text/html; charset=utf-8",
                    Body = injectedDoc "overview.html" overviewScript)))
            do! this.Page.RouteAsync("**/details.html", fun route ->
                route.FulfillAsync(RouteFulfillOptions(
                    ContentType = "text/html; charset=utf-8",
                    Body = injectedDoc "details.html" "")))
            do! this.Page.RouteAsync("**/metrics.html", fun route ->
                route.FulfillAsync(RouteFulfillOptions(
                    ContentType = "text/html; charset=utf-8",
                    Body = injectedDoc "metrics.html" "")))
        }

    member private this.OpenMultiDocPane() =
        task {
            do! focusCanvasCard this.Page MultiDocBranch
            do! ensureCanvasPaneOpen this.Page
            do! (this.Page.Locator(".canvas-pane .canvas-iframe").First).WaitForAsync(LocatorWaitForOptions(Timeout = 10000.0f))
        }

    [<Test>]
    member this.``Item 2: an in-doc canvasSend('navigate-canvas-doc') switches the active tab``() =
        task {
            do! this.RouteDocs "window.canvasSend('navigate-canvas-doc',{filename:'details.html'});"
            do! this.OpenMultiDocPane()

            // Overview iframe loads and calls the injected canvasSend → pane switches active tab to details.
            let! _ = this.Page.WaitForFunctionAsync(
                        "() => { const t = document.querySelector('.canvas-pane .canvas-tab.active'); return !!t && t.textContent.indexOf('details') >= 0; }",
                        null, PageWaitForFunctionOptions(Timeout = 10000.0f))
            let! activeText = (this.Page.Locator(".canvas-pane .canvas-tab.active").First).TextContentAsync()
            Assert.That(activeText, Does.Contain("details"), "canvasSend('navigate-canvas-doc') must switch the active tab to details")
        }

    [<Test>]
    member this.``Item 2 control: a raw window.parent.postMessage('navigate-canvas-doc') switches the active tab identically``() =
        task {
            do! this.RouteDocs "window.parent.postMessage({action:'navigate-canvas-doc',filename:'details.html'},'*');"
            do! this.OpenMultiDocPane()

            let! _ = this.Page.WaitForFunctionAsync(
                        "() => { const t = document.querySelector('.canvas-pane .canvas-tab.active'); return !!t && t.textContent.indexOf('details') >= 0; }",
                        null, PageWaitForFunctionOptions(Timeout = 10000.0f))
            let! activeText = (this.Page.Locator(".canvas-pane .canvas-tab.active").First).TextContentAsync()
            Assert.That(activeText, Does.Contain("details"), "raw postMessage navigate must switch the active tab to details — identical to canvasSend")
        }

    [<Test>]
    member this.``Item 3: a doc that throws on load shows a dismissible doc-error banner and the pane stays interactive``() =
        task {
            do! this.RouteDocs "throw new Error('boom-on-load');"
            do! this.OpenMultiDocPane()

            // Banner appears carrying the thrown message (attributed to the emitting doc, overview).
            let banner = this.Page.Locator(".canvas-pane .canvas-doc-error-banner")
            do! banner.WaitForAsync(LocatorWaitForOptions(Timeout = 10000.0f))
            let! bannerText = banner.First.TextContentAsync()
            Assert.That(bannerText, Does.Contain("boom-on-load"), "doc-error banner must contain the thrown error text")

            // Banner must not cover the doc: the active iframe stays rendered/visible.
            let! iframeVisible = (this.Page.Locator(".canvas-pane .canvas-iframe-active").First).IsVisibleAsync()
            Assert.That(iframeVisible, Is.True, "the doc iframe must remain visible (banner must not cover content)")

            // Pane stays interactive while the banner is up: changing the dock position still works.
            do! (this.Page.Locator(".canvas-tab-bar .canvas-pos-btn").Nth(0)).ClickAsync() // Dock left
            do! this.Page.Locator(".app-layout.canvas-left").WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            // Banner is dismissible.
            do! (this.Page.Locator(".canvas-doc-error-dismiss").First).ClickAsync()
            do! banner.WaitForAsync(LocatorWaitForOptions(State = WaitForSelectorState.Detached, Timeout = 5000.0f))
            let! afterDismiss = banner.CountAsync()
            Assert.That(afterDismiss, Is.EqualTo(0), "doc-error banner must be dismissible")
        }

    [<Test>]
    member this.``Item 3: an unhandled promise rejection in a doc surfaces the doc-error banner``() =
        task {
            do! this.RouteDocs "Promise.reject(new Error('boom-reject'));"
            do! this.OpenMultiDocPane()

            let banner = this.Page.Locator(".canvas-pane .canvas-doc-error-banner")
            do! banner.WaitForAsync(LocatorWaitForOptions(Timeout = 10000.0f))
            let! bannerText = banner.First.TextContentAsync()
            Assert.That(bannerText, Does.Contain("boom-reject"), "an unhandledrejection must surface in the doc-error banner")
        }

    [<Test>]
    member this.``Item 3: switching tabs clears the doc-error banner (SelectDoc)``() =
        task {
            do! this.RouteDocs "throw new Error('boom-on-load');"
            do! this.OpenMultiDocPane()

            let banner = this.Page.Locator(".canvas-pane .canvas-doc-error-banner")
            do! banner.WaitForAsync(LocatorWaitForOptions(Timeout = 10000.0f))

            // Switch to the details tab → SelectDoc clears the doc error (and doc-scoped gating hides it).
            do! (this.Page.Locator(".canvas-pane .canvas-tab").Nth(1)).ClickAsync()
            do! banner.WaitForAsync(LocatorWaitForOptions(State = WaitForSelectorState.Detached, Timeout = 5000.0f))
            let! afterSwitch = banner.CountAsync()
            Assert.That(afterSwitch, Is.EqualTo(0), "doc-error banner must be cleared after switching tabs")
        }

    [<Test>]
    member this.``a doc message with no top-level 'action' field surfaces the dismissible doc-error banner``() =
        task {
            do! this.RouteDocs "window.parent.postMessage({ids:['a'],button:'run',text:'x',run_id:7},'*');"
            do! this.OpenMultiDocPane()

            let banner = this.Page.Locator(".canvas-pane .canvas-doc-error-banner")
            do! banner.WaitForAsync(LocatorWaitForOptions(Timeout = 10000.0f))
            let! bannerText = banner.First.TextContentAsync()
            Assert.That(bannerText, Does.Contain("action"),
                "a message with no `action` field must raise the doc-error banner explaining the missing action")

            // It reuses the existing dismissible banner (DismissCanvasDocError), no new component.
            do! (this.Page.Locator(".canvas-doc-error-dismiss").First).ClickAsync()
            do! banner.WaitForAsync(LocatorWaitForOptions(State = WaitForSelectorState.Detached, Timeout = 5000.0f))
            let! afterDismiss = banner.CountAsync()
            Assert.That(afterDismiss, Is.EqualTo(0), "the surfaced banner must be dismissible like any doc error")
        }

    [<Test>]
    member this.``a well-formed doc message (string action) still routes and raises no malformed banner``() =
        task {
            do! this.RouteDocs "window.parent.postMessage({action:'navigate-canvas-doc',filename:'details.html'},'*');"
            do! this.OpenMultiDocPane()

            let! _ = this.Page.WaitForFunctionAsync(
                        "() => { const t = document.querySelector('.canvas-pane .canvas-tab.active'); return !!t && t.textContent.indexOf('details') >= 0; }",
                        null, PageWaitForFunctionOptions(Timeout = 10000.0f))
            let! bannerCount = (this.Page.Locator(".canvas-pane .canvas-doc-error-banner")).CountAsync()
            Assert.That(bannerCount, Is.EqualTo(0),
                "a well-formed string action must route normally and never raise the missing-action banner")
        }


// ============================================================================
// Escape focus-reclaim bridge (reclaimFocusScript)
//
// The doc-side half of the cross-origin Escape reclaim: the injected keydown
// listener must post {action:'reclaim-focus'} to the parent ONLY for Escape and
// ONLY when the key did not originate in an editable field. Served top-level
// (parent === self), so the page can capture the message it posts to itself —
// this exercises the real injected script in a browser, catching regressions the
// injection-string unit test can't (non-Escape keys, broken editable exemption).
// ============================================================================
[<TestFixture>]
[<Category("E2E")>]
[<Category("Canvas")>]
[<Category("AuthoringDxE2E")>]
type CanvasReclaimBridgeE2ETests() =
    inherit PageTest()

    override this.ContextOptions() =
        let opts = base.ContextOptions()
        opts.IgnoreHTTPSErrors <- true
        opts

    /// Serve the injected doc top-level and install a counter for the reclaim-focus messages the
    /// injected bridge posts to `parent` (the page itself when loaded top-level). A `__sentinel`
    /// flag lets tests settle deterministically (see Settle).
    member private this.ServeDoc() =
        task {
            let doc =
                "<!doctype html><html><head><title>reclaim</title></head>"
                + "<body><input id=\"field\"><button id=\"btn\">b</button></body></html>"
            let served = injectInto SystemView "reclaim.html" doc
            do! this.Page.RouteAsync("**/reclaim.html", fun route ->
                route.FulfillAsync(RouteFulfillOptions(ContentType = "text/html; charset=utf-8", Body = served)))
            let! _ = this.Page.GotoAsync($"{ServerFixture.canvasUrl}/wt/reclaim.html", PageGotoOptions(WaitUntil = WaitUntilState.Load))
            let! _ = this.Page.EvaluateAsync(
                        "() => { window.__reclaims = 0; window.__sentinelSeen = false; window.addEventListener('message', function(e){ if (e.data && e.data.action === 'reclaim-focus') window.__reclaims++; if (e.data && e.data.action === '__sentinel') window.__sentinelSeen = true; }); }")
            ()
        }

    /// Post a sentinel after the key event and wait for it. Message delivery to the same window is
    /// FIFO, so once the sentinel lands, any reclaim-focus the keydown posted has already been
    /// counted — a deterministic settle with no fixed sleep.
    member private this.Settle() =
        task {
            let! _ = this.Page.EvaluateAsync("() => window.postMessage({action:'__sentinel'}, '*')")
            let! _ = this.Page.WaitForFunctionAsync("() => window.__sentinelSeen === true", null, PageWaitForFunctionOptions(Timeout = 5000.0f))
            ()
        }

    [<Test>]
    member this.``Escape outside an editable field posts a reclaim-focus message``() =
        task {
            do! this.ServeDoc()
            do! this.Page.Locator("#btn").FocusAsync()
            do! this.Page.Keyboard.PressAsync("Escape")
            do! this.Settle()
            let! n = this.Page.EvaluateAsync<int>("() => window.__reclaims")
            Assert.That(n, Is.EqualTo(1), "Escape from a non-editable element must post reclaim-focus to the pane")
        }

    [<Test>]
    member this.``Escape inside an editable field does not post reclaim-focus``() =
        task {
            do! this.ServeDoc()
            do! this.Page.Locator("#field").FocusAsync()
            do! this.Page.Keyboard.PressAsync("Escape")
            do! this.Settle()
            let! n = this.Page.EvaluateAsync<int>("() => window.__reclaims")
            Assert.That(n, Is.EqualTo(0), "Escape originating in an editable field must be left to the field, not reclaimed")
        }

    [<Test>]
    member this.``a non-Escape key does not post reclaim-focus``() =
        task {
            do! this.ServeDoc()
            do! this.Page.Locator("#btn").FocusAsync()
            do! this.Page.Keyboard.PressAsync("ArrowDown")
            do! this.Settle()
            let! n = this.Page.EvaluateAsync<int>("() => window.__reclaims")
            Assert.That(n, Is.EqualTo(0), "Only Escape may post reclaim-focus")
        }
// Selected-text contextual actions
type private SelectionHost =
    | TreemonHost
    | BrowserHost
    | MissingTransport

let private selectionLifecycleBody =
    "<section data-section=\"first\"><span id=\"first\">First selection</span></section>"
    + "<section data-section=\"second\"><span id=\"second\">Second selection</span></section>"
    + "<div id=\"editable\" contenteditable=\"true\">Editable selection</div>"

[<TestFixture>]
[<Category("E2E")>]
[<Category("Canvas")>]
[<Category("AuthoringDxE2E")>]
type CanvasSelectionContextE2ETests() =
    inherit PageTest()

    override this.ContextOptions() =
        let opts = base.ContextOptions()
        opts.IgnoreHTTPSErrors <- true
        opts.ReducedMotion <- ReducedMotion.Reduce
        opts

    member private this.ServeDoc (body: string) (host: SelectionHost) =
        task {
            let capture =
                "<script>window.__messages=[];window.__selectionMessages=[];window.__reclaims=0;window.__errors=[];"
                + "window.addEventListener('message',function(e){if(e.data&&typeof e.data.action==='string')window.__messages.push(e.data);if(e.data&&e.data.action==='canvas-selection')window.__selectionMessages.push(e.data);if(e.data&&e.data.action==='reclaim-focus')window.__reclaims++});"
                + "window.addEventListener('error',function(e){window.__errors.push(e.message)});"
                + "</script>"
            let injection =
                match host with
                | TreemonHost -> Server.CanvasDocServer.buildInjection AgentDoc "selection.html"
                | BrowserHost -> Server.CanvasSendScript.script + Server.CanvasSelectionScript.script
                | MissingTransport -> Server.CanvasSelectionScript.script
            let html =
                "<!doctype html><html><head><title>selection</title>"
                + capture
                + injection
                + "</head><body>"
                + body
                + "</body></html>"

            do! this.Page.RouteAsync("**/selection.html", fun route ->
                route.FulfillAsync(RouteFulfillOptions(ContentType = "text/html; charset=utf-8", Body = html)))
            let! _ = this.Page.GotoAsync($"{ServerFixture.canvasUrl}/wt/selection.html", PageGotoOptions(WaitUntil = WaitUntilState.Load))
            ()
        }

    member private this.SelectText (selector: string) =
        task {
            let! _ =
                this.Page.EvaluateAsync(
                    """(selector) => {
                        const element = document.querySelector(selector);
                        element.scrollIntoView({block:'center'});
                        const range = document.createRange();
                        range.selectNodeContents(element);
                        const selection = window.getSelection();
                        selection.removeAllRanges();
                        selection.addRange(range);
                    }""",
                    selector)
            return ()
        }

    member private this.WaitForToolbar() =
        this.Page.WaitForFunctionAsync(
            "() => { const h=document.querySelector('canvas-selection-context'); return !!h && h.style.display==='block' && h.style.visibility==='visible'; }",
            null,
            PageWaitForFunctionOptions(Timeout = 5000.0f))

    member private this.SelectAndWaitForToolbar selector =
        task {
            do! this.SelectText selector
            let! _ = this.WaitForToolbar()
            return ()
        }

    member private this.WaitForAnimationFrame() =
        task {
            let! _ =
                this.Page.EvaluateAsync(
                    "() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))")
            return ()
        }

    [<Test>]
    member this.``selection toolbar hides when the selection is cleared``() =
        task {
            do! this.ServeDoc selectionLifecycleBody TreemonHost
            do! this.SelectAndWaitForToolbar "#first"
            let toolbar = this.Page.Locator("canvas-selection-context .box")
            let! toolbarRole = toolbar.GetAttributeAsync("role")
            Assert.That(toolbarRole, Is.EqualTo("toolbar"))

            let! _ = this.Page.EvaluateAsync("() => window.getSelection().removeAllRanges()")
            let! _ = this.Page.WaitForFunctionAsync(
                "() => document.querySelector('canvas-selection-context').style.display==='none'",
                null,
                PageWaitForFunctionOptions(Timeout = 5000.0f))
            return ()
        }

    [<Test>]
    member this.``Escape hides the selection toolbar and reclaims dashboard focus``() =
        task {
            do! this.ServeDoc selectionLifecycleBody TreemonHost
            do! this.SelectAndWaitForToolbar "#first"
            do! this.Page.Keyboard.PressAsync("Escape")
            let! _ = this.Page.WaitForFunctionAsync(
                "() => document.querySelector('canvas-selection-context').style.display==='none' && window.__reclaims===1",
                null,
                PageWaitForFunctionOptions(Timeout = 5000.0f))
            return ()
        }

    [<Test>]
    member this.``comment mode stays pinned until a replacement selection``() =
        task {
            do! this.ServeDoc selectionLifecycleBody TreemonHost
            do! this.SelectAndWaitForToolbar "#first"
            let toolbar = this.Page.Locator("canvas-selection-context .box")
            do! this.Page.Locator("canvas-selection-context button[data-comment]").ClickAsync()
            let input = this.Page.Locator("canvas-selection-context input")
            do! input.FillAsync("draft")
            let! _ = this.Page.EvaluateAsync("() => window.getSelection().removeAllRanges()")
            let! dialogRole = toolbar.GetAttributeAsync("role")
            let! draft = input.InputValueAsync()
            Assert.That(dialogRole, Is.EqualTo("dialog"))
            Assert.That(draft, Is.EqualTo("draft"))

            do! this.SelectText "#second"
            let! _ = this.Page.WaitForFunctionAsync(
                "() => document.querySelector('canvas-selection-context')?.shadowRoot.querySelector('.box').getAttribute('role')==='toolbar'",
                null,
                PageWaitForFunctionOptions(Timeout = 5000.0f))
            let! replacedDraft = input.InputValueAsync()
            Assert.That(replacedDraft, Is.Empty)
        }

    [<Test>]
    member this.``composing Escape keeps a comment open while ordinary Escape closes it``() =
        task {
            do! this.ServeDoc selectionLifecycleBody TreemonHost
            do! this.SelectAndWaitForToolbar "#first"
            let toolbar = this.Page.Locator("canvas-selection-context .box")
            do! this.Page.Locator("canvas-selection-context button[data-comment]").ClickAsync()
            let input = this.Page.Locator("canvas-selection-context input")
            do! input.FillAsync("composing")
            let! _ =
                input.EvaluateAsync(
                    "element => element.dispatchEvent(new KeyboardEvent('keydown',{key:'Escape',bubbles:true,composed:true,isComposing:true}))")
            do! this.WaitForAnimationFrame()
            let! composingRole = toolbar.GetAttributeAsync("role")
            let! composingDraft = input.InputValueAsync()
            Assert.That(composingRole, Is.EqualTo("dialog"))
            Assert.That(composingDraft, Is.EqualTo("composing"))

            do! input.PressAsync("Escape")
            let! _ = this.Page.WaitForFunctionAsync(
                "() => document.querySelector('canvas-selection-context').style.display==='none'",
                null,
                PageWaitForFunctionOptions(Timeout = 5000.0f))
            let! reclaimCount = this.Page.EvaluateAsync<int>("() => window.__reclaims")
            Assert.That(reclaimCount, Is.Zero, "Escape in the comment input closes it without reclaiming dashboard focus")
        }

    [<Test>]
    member this.``editable field selection does not show the toolbar``() =
        task {
            do! this.ServeDoc selectionLifecycleBody TreemonHost
            do! this.SelectText "#editable"
            do! this.WaitForAnimationFrame()
            let! display =
                this.Page.EvaluateAsync<string>(
                    "() => document.querySelector('canvas-selection-context')?.style.display ?? 'none'")
            Assert.That(display, Is.EqualTo("none"))
        }

    [<Test>]
    member this.``clearing selection state before a queued position frame does not throw``() =
        task {
            do! (this.ServeDoc
                "<section data-section=\"race\"><span id=\"selected\">Race selection</span></section>"
                TreemonHost)

            let! _ = this.Page.EvaluateAsync(
                """() => {
                    const element = document.querySelector('#selected');
                    const range = document.createRange();
                    range.selectNodeContents(element);
                    const selection = window.getSelection();
                    selection.removeAllRanges();
                    selection.addRange(range);
                    window.postMessage({action:'content-updated'}, '*');
                }""")
            do! this.WaitForAnimationFrame()
            let! errors = this.Page.EvaluateAsync<string>("() => JSON.stringify(window.__errors)")
            Assert.That(errors, Is.EqualTo("[]"))
        }

    [<Test>]
    member this.``Explain Remove and Comment send the exact ordered payload without duplicating the comment``() =
        task {
            do! (this.ServeDoc
                "<section data-section=\"alpha\">Before <span id=\"selected\">Selected phrase</span> after</section>"
                TreemonHost)

            let send intent =
                task {
                    do! this.SelectAndWaitForToolbar "#selected"
                    do! this.Page.Locator($"canvas-selection-context button[data-intent=\"{intent}\"]").ClickAsync()
                }

            do! send "explain"
            let! _ = this.Page.WaitForFunctionAsync("() => window.__selectionMessages.length===1")
            do! send "remove"
            let! _ = this.Page.WaitForFunctionAsync("() => window.__selectionMessages.length===2")

            do! this.SelectAndWaitForToolbar "#selected"
            do! this.Page.Locator("canvas-selection-context button[data-comment]").ClickAsync()
            let input = this.Page.Locator("canvas-selection-context input")
            do! input.FillAsync("Please clarify")
            let! _ =
                input.EvaluateAsync(
                    "element => element.dispatchEvent(new KeyboardEvent('keydown',{key:'Enter',bubbles:true,composed:true,isComposing:true}))")
            do! this.WaitForAnimationFrame()
            let! composingMessageCount = this.Page.EvaluateAsync<int>("() => window.__selectionMessages.length")
            let! composingComment = input.InputValueAsync()
            Assert.That(composingMessageCount, Is.EqualTo(2))
            Assert.That(composingComment, Is.EqualTo("Please clarify"))

            do! input.PressAsync("Enter")
            let! _ = this.Page.WaitForFunctionAsync("() => window.__selectionMessages.length===3")

            let! messages = this.Page.EvaluateAsync<string>("() => JSON.stringify(window.__selectionMessages)")
            let expected =
                """[{"intent":"explain","doc":"selection.html","contextBefore":"Before ","selectedText":"Selected phrase","contextAfter":" after","section":"alpha","request":"User asked to explain/expand this","action":"canvas-selection"},{"intent":"remove","doc":"selection.html","contextBefore":"Before ","selectedText":"Selected phrase","contextAfter":" after","section":"alpha","request":"User asked to remove this","action":"canvas-selection"},{"intent":"comment","doc":"selection.html","contextBefore":"Before ","selectedText":"Selected phrase","contextAfter":" after","section":"alpha","request":"User commented: Please clarify","action":"canvas-selection"}]"""
            Assert.That(messages, Is.EqualTo(expected))

            let pulse = this.Page.Locator("canvas-selection-processing .pulse").First
            do! pulse.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let! animation = pulse.EvaluateAsync<string>("element => getComputedStyle(element).animationName")
            Assert.That(animation, Is.EqualTo("canvas-selection-processing-pulse"))

            let! _ = this.Page.EvaluateAsync("() => window.postMessage({action:'content-updated'},'*')")
            let! _ = this.Page.WaitForFunctionAsync(
                "() => { const h=document.querySelector('canvas-selection-processing'); return !h || h.style.display==='none'; }",
                null,
                PageWaitForFunctionOptions(Timeout = 5000.0f))
            return ()
        }

    [<Test>]
    member this.``browser fallback exposes canvasSend and posts selection actions through it``() =
        task {
            do! (this.ServeDoc
                "<section data-section=\"fallback\">Before <span id=\"selected\">Fallback selection</span> after</section>"
                BrowserHost)

            let! helperType = this.Page.EvaluateAsync<string>("() => typeof window.canvasSend")
            Assert.That(helperType, Is.EqualTo("function"))
            let! sent = this.Page.EvaluateAsync<bool>("() => window.canvasSend('custom-action',{value:1})")
            Assert.That(sent, Is.True)
            let! _ = this.Page.WaitForFunctionAsync(
                "() => window.__messages.some(message => message.action === 'custom-action')")
            let! customMessage =
                this.Page.EvaluateAsync<string>(
                    "() => JSON.stringify(window.__messages.find(message => message.action === 'custom-action'))")
            Assert.That(customMessage, Is.EqualTo("""{"value":1,"action":"custom-action"}"""))

            do! this.SelectAndWaitForToolbar "#selected"
            do! this.Page.Locator("canvas-selection-context button[data-intent=\"explain\"]").ClickAsync()
            let! _ = this.Page.WaitForFunctionAsync("() => window.__selectionMessages.length===1")

            let! message = this.Page.EvaluateAsync<string>("() => JSON.stringify(window.__selectionMessages[0])")
            let expected =
                """{"intent":"explain","doc":"selection.html","contextBefore":"Before ","selectedText":"Fallback selection","contextAfter":" after","section":"fallback","request":"User asked to explain/expand this","action":"canvas-selection"}"""
            Assert.That(message, Is.EqualTo(expected))
        }

    [<Test>]
    member this.``a selection spanning editable content is rejected``() =
        task {
            do! (this.ServeDoc
                ("<span id=\"before\">Before</span>"
                 + "<div id=\"editable\" contenteditable=\"true\">Unsaved draft</div>"
                 + "<span id=\"after\">After</span>")
                BrowserHost)

            let! _ =
                this.Page.EvaluateAsync(
                    """() => {
                        const before = document.querySelector('#before').firstChild;
                        const after = document.querySelector('#after').firstChild;
                        const range = document.createRange();
                        range.setStart(before, 0);
                        range.setEnd(after, after.textContent.length);
                        const selection = window.getSelection();
                        selection.removeAllRanges();
                        selection.addRange(range);
                    }""")
            do! this.WaitForAnimationFrame()
            let! outcome =
                this.Page.EvaluateAsync<string>(
                    """() => JSON.stringify({
                        toolbar: document.querySelector('canvas-selection-context')?.style.display ?? 'none',
                        messages: window.__selectionMessages
                    })""")
            Assert.That(outcome, Is.EqualTo("""{"toolbar":"none","messages":[]}"""))
        }

    [<Test>]
    member this.``processing highlight renders at most two hundred visible rectangles``() =
        task {
            let body =
                [ 1..400 ]
                |> List.map (fun index -> $"<div>Line {index}</div>")
                |> String.concat ""
                |> fun lines -> $"<div id=\"large\">{lines}</div>"
            do! this.ServeDoc body BrowserHost
            do! this.SelectAndWaitForToolbar "#large"
            let! _ =
                this.Page.Locator("canvas-selection-context button[data-intent=\"explain\"]")
                    .EvaluateAsync("button => button.click()")
            let! _ = this.Page.WaitForFunctionAsync(
                "() => document.querySelector('canvas-selection-processing')?.shadowRoot.querySelectorAll('.pulse').length > 0")
            let! pulseCount =
                this.Page.EvaluateAsync<int>(
                    "() => document.querySelector('canvas-selection-processing').shadowRoot.querySelectorAll('.pulse').length")
            Assert.That(pulseCount, Is.InRange(1, 200))
        }

    [<Test>]
    member this.``unsafe data-section falls back to a safe id``() =
        task {
            do! (this.ServeDoc
                "<section data-section=\"release notes\"><span id=\"release_notes\">Target text</span></section>"
                BrowserHost)
            do! this.SelectAndWaitForToolbar "#release_notes"
            do! this.Page.Locator("canvas-selection-context button[data-intent=\"explain\"]").ClickAsync()
            let! _ = this.Page.WaitForFunctionAsync("() => window.__selectionMessages.length===1")
            let! message = this.Page.EvaluateAsync<string>("() => JSON.stringify(window.__selectionMessages[0])")
            let expected =
                """{"intent":"explain","doc":"selection.html","contextBefore":"","selectedText":"Target text","contextAfter":"","section":"release_notes","request":"User asked to explain/expand this","action":"canvas-selection"}"""
            Assert.That(message, Is.EqualTo(expected))
        }

    [<Test>]
    member this.``missing transport reports that canvas messaging is unavailable``() =
        task {
            do! (this.ServeDoc
                "<span id=\"selected\">Unavailable transport</span>"
                MissingTransport)
            do! this.SelectAndWaitForToolbar "#selected"
            do! this.Page.Locator("canvas-selection-context button[data-intent=\"explain\"]").ClickAsync()
            let expected = "Canvas messaging is unavailable in this document."
            let! _ = this.Page.WaitForFunctionAsync(
                "(expected) => document.querySelector('canvas-selection-context')?.shadowRoot.querySelector('.error').textContent === expected",
                expected)
            let! error = this.Page.Locator("canvas-selection-context .error").TextContentAsync()
            Assert.That(error, Is.EqualTo(expected))
        }

    [<Test>]
    member this.``a new editable selection clears the previous processing highlight``() =
        task {
            do! this.ServeDoc selectionLifecycleBody BrowserHost
            do! this.SelectAndWaitForToolbar "#first"
            do! this.Page.Locator("canvas-selection-context button[data-intent=\"explain\"]").ClickAsync()
            let! _ = this.Page.WaitForFunctionAsync(
                "() => document.querySelector('canvas-selection-processing')?.style.display === 'block'")

            do! this.SelectText "#editable"
            let! _ = this.Page.WaitForFunctionAsync(
                "() => document.querySelector('canvas-selection-processing')?.style.display === 'none'")
            let! outcome =
                this.Page.EvaluateAsync<string>(
                    """() => JSON.stringify({
                        processing: document.querySelector('canvas-selection-processing').style.display,
                        toolbar: document.querySelector('canvas-selection-context').style.display
                    })""")
            Assert.That(outcome, Is.EqualTo("""{"processing":"none","toolbar":"none"}"""))
        }

    [<Test>]
    member this.``processing highlight clears after morph completion rather than update request``() =
        task {
            do! (this.ServeDoc
                "<span id=\"selected\">Morph lifecycle</span>"
                BrowserHost)
            do! this.SelectAndWaitForToolbar "#selected"
            do! this.Page.Locator("canvas-selection-context button[data-intent=\"explain\"]").ClickAsync()
            let! _ = this.Page.WaitForFunctionAsync(
                "() => document.querySelector('canvas-selection-processing')?.style.display === 'block'")

            let! _ = this.Page.EvaluateAsync("() => window.postMessage({action:'content-updated'},'*')")
            do! this.WaitForAnimationFrame()
            let! beforeCompletion =
                this.Page.EvaluateAsync<string>(
                    "() => document.querySelector('canvas-selection-processing').style.display")
            Assert.That(beforeCompletion, Is.EqualTo("block"))

            let! _ = this.Page.EvaluateAsync("() => window.dispatchEvent(new Event('canvas-morph-complete'))")
            let! _ = this.Page.WaitForFunctionAsync(
                "() => document.querySelector('canvas-selection-processing')?.style.display === 'none'")
            return ()
        }
