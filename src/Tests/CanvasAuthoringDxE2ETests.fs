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
