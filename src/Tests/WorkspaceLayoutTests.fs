module Tests.WorkspaceLayoutTests

open System
open System.Threading.Tasks
open NUnit.Framework
open Newtonsoft.Json
open Microsoft.Playwright
open Microsoft.Playwright.NUnit
open Shared
open TerminalHost
open Tests.CanvasTestHelpers

[<TestFixture>]
[<Category("E2E")>]
[<Category("Terminal")>]
type WorkspaceLayoutTests() =
    inherit PageTest()

    let paneOrder (page: IPage) =
        page.EvaluateAsync<string[]>(
            "() => Array.from(document.querySelector('.app-layout').children).map(el => el.className.split(' ')[0])")

    let paneWidth (page: IPage) selector =
        task {
            let! box = page.Locator(selector).First.BoundingBoxAsync()
            return if isNull (box :> obj) then 0.0 else float box.Width
        }

    let layoutWidth (page: IPage) =
        page.EvaluateAsync<float>("() => document.querySelector('.app-layout').getBoundingClientRect().width")

    let widthButtons (page: IPage) =
        page.Locator(".header-controls .workspace-width-btn")

    let waitForWorkspaceWidthSave (page: IPage) (action: unit -> Task) =
        page.RunAndWaitForResponseAsync(
            Func<Task>(action),
            Func<IResponse, bool>(fun response -> response.Url.EndsWith("/IWorktreeApi/saveWorkspaceWidth")))

    let assertShares (page: IPage) shares =
        task {
            for selector, expected in shares do
                let! _ =
                    page.WaitForFunctionAsync(
                        """({selector, expected}) => {
                            const total = document.querySelector('.app-layout').getBoundingClientRect().width;
                            const width = document.querySelector(selector).getBoundingClientRect().width;
                            return Math.abs(width / total - expected) < 0.01;
                        }""",
                        {| selector = selector; expected = expected |},
                        PageWaitForFunctionOptions(Timeout = 5000.0f))
                let! total = layoutWidth page
                let! actual = paneWidth page selector
                Assert.That(actual / total, Is.EqualTo(expected).Within(0.01), $"{selector} workspace share")
        }

    let terminalToggleBtn (page: IPage) =
        page.Locator(
            ".header-controls .ctrl-btn",
            PageLocatorOptions(HasText = "Terminal"))

    let showTerminal (page: IPage) =
        task {
            let pane = page.Locator(".terminal-pane.open")
            let! openCount = pane.CountAsync()

            if openCount = 0 then
                do! (terminalToggleBtn page).ClickAsync()

            do!
                pane.WaitForAsync(
                    LocatorWaitForOptions(Timeout = 5000.0f))
        }

    override this.ContextOptions() =
        let options = base.ContextOptions()
        options.IgnoreHTTPSErrors <- true
        options

    [<SetUp>]
    member this.NavigateToDashboard() =
        task {
            let! _ = this.Page.GotoAsync(ServerFixture.viteUrl)
            do! this.Page.Locator(".wt-card .branch-name").First.WaitForAsync(LocatorWaitForOptions(Timeout = 15000.0f))
        }

    [<Test>]
    member this.``Workspace keeps Terminal Canvas Dashboard DOM order``() =
        task {
            let! closed = paneOrder this.Page
            Assert.That(closed, Is.EqualTo([| "terminal-pane"; "canvas-pane"; "dashboard" |]))
            do! focusFirstCard this.Page
            do! ensureCanvasPaneOpen this.Page
            let! opened = paneOrder this.Page
            Assert.That(opened, Is.EqualTo(closed))
        }

    [<Test>]
    member this.``Terminal top-bar button toggles the pane and replaces its local Hide action``() =
        task {
            let toggle = terminalToggleBtn this.Page
            let pane = this.Page.Locator(".terminal-pane")
            let! initiallyOpen = this.Page.Locator(".terminal-pane.open").CountAsync()
            let! initiallyActive =
                toggle.EvaluateAsync<bool>(
                    "button => button.classList.contains('active')")

            do! toggle.ClickAsync()
            do!
                this.Page
                    .Locator(".terminal-pane.open")
                    .WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let! openActive =
                toggle.EvaluateAsync<bool>(
                    "button => button.classList.contains('active')")
            let! localHideCount =
                pane
                    .GetByRole(
                        AriaRole.Button,
                        LocatorGetByRoleOptions(Name = "Hide terminal pane"))
                    .CountAsync()

            do! toggle.ClickAsync()
            let! _ =
                this.Page.WaitForFunctionAsync(
                    "() => document.querySelector('.terminal-pane').hidden")
            let! closedActive =
                toggle.EvaluateAsync<bool>(
                    "button => button.classList.contains('active')")

            Assert.Multiple(fun () ->
                Assert.That(initiallyOpen, Is.EqualTo(0))
                Assert.That(initiallyActive, Is.False)
                Assert.That(openActive, Is.True)
                Assert.That(localHideCount, Is.EqualTo(0))
                Assert.That(closedActive, Is.False))
        }

    [<TestCase(0, 1.0, 1.0, 1.0)>]
    [<TestCase(1, 1.0, 2.0, 1.0)>]
    [<TestCase(2, 2.0, 2.0, 1.0)>]
    member this.``All open workspace supports each top-bar ratio``(index: int, terminal: float, canvas: float, dashboard: float) =
        task {
            do! focusFirstCard this.Page
            do! ensureCanvasPaneOpen this.Page
            do! showTerminal this.Page
            let buttons = widthButtons this.Page
            let! labels = buttons.AllTextContentsAsync()
            Assert.That(labels, Is.EqualTo([| "1:1:1"; "1:2:1"; "2:2:1" |]))
            let! saved =
                waitForWorkspaceWidthSave this.Page (fun () -> buttons.Nth(index).ClickAsync())
            Assert.That(saved.Ok, Is.True, "The selected ratio must be accepted by the persistence API")
            let total = terminal + canvas + dashboard
            do! assertShares this.Page [
                ".terminal-pane", terminal / total
                ".canvas-pane", canvas / total
                ".dashboard", dashboard / total
            ]
            let! active = this.Page.Locator(".workspace-width-btn.active").AllTextContentsAsync()
            Assert.That(active, Is.EqualTo([| labels[index] |]))
        }

    [<TestCase(true)>]
    [<TestCase(false)>]
    member this.``Either single pane supports one-to-one and two-to-one``(canvasVisible: bool) =
        task {
            do! focusFirstCard this.Page
            if canvasVisible then
                do! ensureCanvasPaneOpen this.Page
            else
                do! showTerminal this.Page
            let buttons = widthButtons this.Page
            let! labels = buttons.AllTextContentsAsync()
            Assert.That(labels, Is.EqualTo([| "1:1"; "2:1" |]))
            let pane = if canvasVisible then ".canvas-pane" else ".terminal-pane"
            do! buttons.First.ClickAsync()
            do! assertShares this.Page [ pane, 0.5; ".dashboard", 0.5 ]
            do! buttons.Nth(1).ClickAsync()
            do! assertShares this.Page [ pane, 2.0 / 3.0; ".dashboard", 1.0 / 3.0 ]
        }

    [<Test>]
    member this.``Terminal-only default wide choice restores two-two-one when Canvas opens``() =
        task {
            do! focusFirstCard this.Page
            do! showTerminal this.Page
            let buttons = widthButtons this.Page
            let! initial = this.Page.Locator(".workspace-width-btn.active").AllTextContentsAsync()
            Assert.That(initial, Is.EqualTo([| "1:1" |]))

            let! saved =
                waitForWorkspaceWidthSave this.Page (fun () -> buttons.Nth(1).ClickAsync())
            Assert.That(saved.Ok, Is.True, "The terminal-wide selection must be accepted by the persistence API")

            do! ensureCanvasPaneOpen this.Page
            let! labels = buttons.AllTextContentsAsync()
            let! active = this.Page.Locator(".workspace-width-btn.active").AllTextContentsAsync()
            Assert.Multiple(fun () ->
                Assert.That(labels, Is.EqualTo([| "1:1:1"; "1:2:1"; "2:2:1" |]))
                Assert.That(active, Is.EqualTo([| "2:2:1" |])))
            do! assertShares this.Page [
                ".terminal-pane", 0.4
                ".canvas-pane", 0.4
                ".dashboard", 0.2
            ]
        }

    [<Test>]
    member this.``Workspace ratio buttons support keyboard activation and pressed state``() =
        task {
            do! focusFirstCard this.Page
            do! ensureCanvasPaneOpen this.Page
            do! showTerminal this.Page
            let buttons = widthButtons this.Page
            let! ratioButtonsAreFocusable =
                buttons.EvaluateAllAsync<bool>(
                    "buttons => buttons.length === 3 && buttons.every(button => button.tabIndex === 0)")
            let! unrelatedButtonsStayOutOfTabOrder =
                this.Page
                    .Locator(".header-controls > .ctrl-btn")
                    .EvaluateAllAsync<bool>(
                        "buttons => buttons.length > 0 && buttons.every(button => button.tabIndex === -1)")
            Assert.Multiple(fun () ->
                Assert.That(ratioButtonsAreFocusable, Is.True)
                Assert.That(unrelatedButtonsStayOutOfTabOrder, Is.True))

            do! Assertions.Expect(buttons.First).ToHaveAttributeAsync("aria-pressed", "true")
            do! Assertions.Expect(buttons.Nth(2)).ToHaveAttributeAsync("aria-pressed", "false")
            do! buttons.Nth(2).FocusAsync()
            do! Assertions.Expect(buttons.Nth(2)).ToBeFocusedAsync()
            let! enterSaved =
                waitForWorkspaceWidthSave this.Page (fun () -> buttons.Nth(2).PressAsync("Enter"))
            Assert.That(enterSaved.Ok, Is.True)
            do! Assertions.Expect(buttons.First).ToHaveAttributeAsync("aria-pressed", "false")
            do! Assertions.Expect(buttons.Nth(2)).ToHaveAttributeAsync("aria-pressed", "true")

            do! buttons.First.FocusAsync()
            do! Assertions.Expect(buttons.First).ToBeFocusedAsync()
            let! spaceSaved =
                waitForWorkspaceWidthSave this.Page (fun () -> buttons.First.PressAsync("Space"))
            Assert.That(spaceSaved.Ok, Is.True)
            do! Assertions.Expect(buttons.First).ToHaveAttributeAsync("aria-pressed", "true")
            do! Assertions.Expect(buttons.Nth(2)).ToHaveAttributeAsync("aria-pressed", "false")
        }

    [<TestCase(true, 1)>]
    [<TestCase(false, 1)>]
    [<TestCase(true, 2)>]
    [<TestCase(false, 2)>]
    member this.``Hiding either pane preserves the selected wide mode``(hideCanvas: bool, index: int) =
        task {
            do! focusFirstCard this.Page
            do! ensureCanvasPaneOpen this.Page
            do! showTerminal this.Page
            let buttons = widthButtons this.Page
            do! buttons.Nth(index).ClickAsync()
            let! selected = buttons.Nth(index).TextContentAsync()
            let toggle = if hideCanvas then canvasToggleBtn this.Page else terminalToggleBtn this.Page
            do! toggle.ClickAsync()
            let! labels = buttons.AllTextContentsAsync()
            Assert.That(labels, Is.EqualTo([| "1:1"; "2:1" |]))
            let! active = this.Page.Locator(".workspace-width-btn.active").AllTextContentsAsync()
            Assert.That(active, Is.EqualTo([| "2:1" |]))
            let pane = if hideCanvas then ".terminal-pane" else ".canvas-pane"
            do! assertShares this.Page [ pane, 2.0 / 3.0; ".dashboard", 1.0 / 3.0 ]
            do! buttons.Nth(1).ClickAsync()
            do! toggle.ClickAsync()
            let! restored = this.Page.Locator(".workspace-width-btn.active").AllTextContentsAsync()
            Assert.That(restored, Is.EqualTo([| selected |]), "The two-pane control retains the three-pane preference")
            let terminal, canvas, dashboard =
                if index = 1 then 0.25, 0.5, 0.25 else 0.4, 0.4, 0.2
            do! assertShares this.Page [
                ".terminal-pane", terminal
                ".canvas-pane", canvas
                ".dashboard", dashboard
            ]
        }

    [<Test>]
    member this.``Dashboard alone hides ratio controls and fills the workspace``() =
        task {
            do! focusFirstCard this.Page
            do! ensureCanvasPaneOpen this.Page
            do! showTerminal this.Page
            do! (widthButtons this.Page).Nth(2).ClickAsync()
            do! (canvasToggleBtn this.Page).ClickAsync()
            do! (terminalToggleBtn this.Page).ClickAsync()
            let! count = (widthButtons this.Page).CountAsync()
            Assert.That(count, Is.Zero)
            do! assertShares this.Page [ ".dashboard", 1.0 ]
        }

    [<Test>]
    member this.``Header hides centered mascot before controls overlap without changing desktop layout``() =
        task {
            do! this.Page.SetViewportSizeAsync(960, 900)
            do! focusFirstCard this.Page
            do! ensureCanvasPaneOpen this.Page
            do! showTerminal this.Page
            do! Assertions.Expect(this.Page.Locator(".header-center")).ToHaveCSSAsync("display", "none")
            do! Assertions.Expect(widthButtons this.Page).ToHaveCountAsync(3)
            do! Assertions.Expect(this.Page.Locator(".app-layout")).ToHaveCSSAsync("flex-direction", "row")
        }

    [<TestCase(0)>]
    [<TestCase(1)>]
    [<TestCase(2)>]
    member this.``Narrow workspace stacks panes without horizontal overflow``(index: int) =
        task {
            do! this.Page.SetViewportSizeAsync(720, 900)
            do! focusFirstCard this.Page
            do! ensureCanvasPaneOpen this.Page
            do! showTerminal this.Page
            do! (widthButtons this.Page).Nth(index).ClickAsync()
            do! assertShares this.Page [
                ".terminal-pane", 1.0
                ".canvas-pane", 1.0
                ".dashboard", 1.0
            ]

            let! tops =
                this.Page.EvaluateAsync<float[]>(
                    "() => ['.terminal-pane', '.canvas-pane', '.dashboard'].map(s => document.querySelector(s).getBoundingClientRect().top)")
            Assert.That(tops[0], Is.LessThan(tops[1]))
            Assert.That(tops[1], Is.LessThan(tops[2]))

            let! overflow =
                this.Page.EvaluateAsync<bool>(
                    "() => document.documentElement.scrollWidth > document.documentElement.clientWidth")
            Assert.That(overflow, Is.False)

            for selector in [ ".terminal-pane"; ".canvas-pane"; ".dashboard" ] do
                do! Assertions.Expect(this.Page.Locator(selector)).ToHaveCSSAsync("flex", "1 1 0px")
        }

    [<Test>]
    member this.``Workspace removes legacy Canvas docking controls and classes``() =
        task {
            do! focusFirstCard this.Page
            do! ensureCanvasPaneOpen this.Page
            let! dockButtons = this.Page.Locator(".canvas-pos-btn").CountAsync()
            let! dockClasses =
                this.Page.EvaluateAsync<int>(
                    "() => document.querySelectorAll('.app-layout.canvas-left, .app-layout.canvas-right, .app-layout.canvas-top, .app-layout.canvas-bottom').length")
            Assert.That(dockButtons, Is.EqualTo(0))
            Assert.That(dockClasses, Is.EqualTo(0))
        }

let private terminalConverter =
    Fable.Remoting.Json.FableJsonConverter()

let private firstTerminalPath =
    WorktreePath "Q:/code/TestProject/feature-active"

let private secondTerminalPath =
    WorktreePath "Q:/code/TestProject/feature-recent"

let private failedTerminalPath =
    WorktreePath "Q:/code/TestProject/feature-idle"

let private startableTerminalPath =
    WorktreePath "Q:/code/TestProject/feature-multidoc"

let private firstTerminalId =
    EmbeddedTerminalId "00000000000000000000000000000001"

let private firstAlternateTerminalId =
    EmbeddedTerminalId "00000000000000000000000000000002"

let private secondTerminalId =
    EmbeddedTerminalId "00000000000000000000000000000003"

let private firstTerminalActivity =
    "Implementing terminal lifecycle"

let private firstAlternateTerminalActivity =
    "Reviewing host replacement"

let private runningTerminal terminalId path port =
    { Id = terminalId
      Worktree = path
      ReportedActivity = None
      Lifecycle =
        EmbeddedTerminalLifecycle.Running
            $"http://127.0.0.1:{port}/" }

let private initialTerminalSnapshot =
    { Tabs =
        [ { runningTerminal firstTerminalId firstTerminalPath 61234 with
                ReportedActivity = Some firstTerminalActivity }
          { runningTerminal firstAlternateTerminalId firstTerminalPath 61237 with
                ReportedActivity = Some firstAlternateTerminalActivity }
          runningTerminal secondTerminalId secondTerminalPath 61235
          { Id = EmbeddedTerminalId "00000000000000000000000000000004"
            Worktree = failedTerminalPath
            ReportedActivity = None
            Lifecycle =
                EmbeddedTerminalLifecycle.Interrupted
                    "ttyd exited with code 1" }
          { Id = EmbeddedTerminalId "00000000000000000000000000000005"
            Worktree = WorktreePath "Q:/code/TestProject/feature-stale"
            ReportedActivity = None
            Lifecycle =
                EmbeddedTerminalLifecycle.Running
                    "https://example.com/unsafe-terminal" } ] }

let private terminalDocument (marker: string) =
    """<!doctype html>
<html>
<head>
  <style>
    html, body { width: 100%; height: 100%; margin: 0; overflow: hidden; }
    .xterm-viewport { width: 100%; height: 80px; overflow-y: auto; }
    .scrollback { height: 600px; }
  </style>
  <script>
    window.__terminalVisibleMessages = 0;
    window.__terminalInactiveMessages = 0;
    window.__terminalInputFocuses = 0;
    window.addEventListener('message', function(event) {
      if (!event.data || event.data.action !== '__ACTION__') return;
      if (event.data.active === true) window.__terminalVisibleMessages++;
      if (event.data.active === false) window.__terminalInactiveMessages++;
    });
    document.addEventListener('focusin', function(event) {
      if (event.target?.classList.contains('xterm-helper-textarea')) {
        window.__terminalInputFocuses++;
      }
    });
  </script>
</head>
<body>
  <textarea class="xterm-helper-textarea" aria-label="Terminal input"></textarea>
  <div data-terminal-marker="__MARKER__" class="xterm-viewport"><div class="scrollback"></div></div>
</body>
</html>"""
        .Replace("__MARKER__", marker, StringComparison.Ordinal)
        .Replace(
            "__ACTION__",
            TerminalPane.TerminalVisibleAction,
            StringComparison.Ordinal
        )
    |> TerminalProxy.customizeTerminalPage [
        Uri(ServerFixture.viteUrl).GetLeftPart(UriPartial.Authority)
    ]

[<TestFixture>]
[<Category("E2E")>]
[<Category("Terminal")>]
type TerminalPaneDomTests() =
    inherit PageTest()

    // Route handlers model the server registry across requests, so this mutation is confined to
    // the Playwright fixture boundary and reset before every test.
    let mutable registry = initialTerminalSnapshot
    let mutable startCalls = 0
    let mutable closeCalls = 0

    let serialize value =
        JsonConvert.SerializeObject(value, terminalConverter)

    let selectedTab (page: IPage) =
        page.Locator(".terminal-tab.selected")

    let framesStillMounted (page: IPage) =
        page.EvaluateAsync<bool>(
            """() => {
                const current = Array.from(document.querySelectorAll('.terminal-iframe'));
                return current.length === window.__terminalFrames.length
                    && current.every((frame, index) =>
                        frame === window.__terminalFrames[index] && frame.isConnected);
            }""")

    let rememberFrames (page: IPage) =
        task {
            let! _ =
                page.EvaluateAsync(
                    "() => { window.__terminalFrames = Array.from(document.querySelectorAll('.terminal-iframe')); }")
            return ()
        }

    let tabFor (page: IPage) label =
        page.Locator(
            ".terminal-tab",
            PageLocatorOptions(
                Has = page.Locator(
                    ".terminal-tab-label",
                    PageLocatorOptions(HasText = label))))

    // The close button is a sibling of `.terminal-tab` (not a descendant — it must stay outside
    // the focusable role="tab" element for accessibility), so it is located via the shared
    // `.terminal-tab-item` wrapper instead of chaining off tabFor.
    let closeButtonFor (page: IPage) label =
        page.Locator(
            ".terminal-tab-item",
            PageLocatorOptions(
                Has = page.Locator(
                    ".terminal-tab-label",
                    PageLocatorOptions(HasText = label))))
            .Locator(".terminal-tab-close")

    let cardFor (page: IPage) branch =
        page.Locator(
            ".wt-card",
            PageLocatorOptions(
                Has = page.Locator(
                    ".branch-name",
                    PageLocatorOptions(HasText = branch))))

    let activeTerminalInput (page: IPage) =
        page
            .FrameLocator("iframe.terminal-iframe-active")
            .Locator(".xterm-helper-textarea")

    let expectActiveTerminalInputFocused (page: IPage) stage =
        task {
            let input = activeTerminalInput page

            try
                do! Assertions.Expect(input).ToBeFocusedAsync()
            with ex ->
                let! focusEvents =
                    input.EvaluateAsync<int>(
                        "_ => window.__terminalInputFocuses"
                    )

                Assert.Fail(
                    $"{stage}: terminal input was not focused after {focusEvents} input focus event(s). {ex.Message}"
                )
        }

    let waitForTerminalSignal
        (page: IPage)
        (frame: IFrame)
        stage
        expression
        =
        task {
            try
                let! _ =
                    frame.WaitForFunctionAsync(
                        expression,
                        (null :> obj),
                        FrameWaitForFunctionOptions(
                            PollingInterval = 50.0f,
                            Timeout = 5000.0f
                        )
                    )

                return ()
            with :? TimeoutException as ex ->
                let! terminal =
                    frame.EvaluateAsync<string>(
                        """() => JSON.stringify({
                            visibleMessages: window.__terminalVisibleMessages ?? null,
                            inactiveMessages: window.__terminalInactiveMessages ?? null,
                            inputFocuses: window.__terminalInputFocuses ?? null,
                            activeElement: document.activeElement?.className
                                || document.activeElement?.tagName
                                || null
                        })"""
                    )
                let! dashboard =
                    page.EvaluateAsync<string>(
                        """() => JSON.stringify({
                            visibility: document.visibilityState,
                            hasFocus: document.hasFocus(),
                            paneHidden: document.querySelector('.terminal-pane')?.hidden ?? null,
                            activeTerminal: document.querySelector('.terminal-iframe-active')
                                ?.getAttribute('data-terminal-id') ?? null,
                            activeElement: document.activeElement?.className
                                || document.activeElement?.tagName
                                || null
                        })"""
                    )

                return
                    Assert.Fail(
                        $"{stage}: terminal frame condition timed out. Frame={frame.Url}; terminal={terminal}; dashboard={dashboard}. {ex.Message}"
                    )
        }

    override this.ContextOptions() =
        let options = base.ContextOptions()
        options.IgnoreHTTPSErrors <- true
        options

    [<SetUp>]
    member this.RouteTerminalRegistry() =
        task {
            registry <- initialTerminalSnapshot
            startCalls <- 0
            closeCalls <- 0

            do!
                this.Page.RouteAsync(
                    "**/IWorktreeApi/getWorktrees",
                    Func<IRoute, Task>(fun route ->
                        task {
                            let! upstream = route.FetchAsync()
                            let! json = upstream.TextAsync()
                            let response =
                                JsonConvert.DeserializeObject<DashboardResponse>(
                                    json,
                                    terminalConverter)
                            let opened =
                                { response with
                                    TerminalPaneOpen = true }
                            do!
                                route.FulfillAsync(
                                    RouteFulfillOptions(
                                        ContentType = "application/json",
                                        Body = serialize opened))
                        }))

            do!
                this.Page.RouteAsync(
                    "**/IWorktreeApi/getEmbeddedTerminals",
                    fun route ->
                        route.FulfillAsync(
                            RouteFulfillOptions(
                                ContentType = "application/json",
                                Body = serialize registry)))

            do!
                this.Page.RouteAsync(
                    "**/IWorktreeApi/startEmbeddedTerminal",
                    fun route ->
                        startCalls <- startCalls + 1
                        let requestedPath =
                            [ firstTerminalPath
                              secondTerminalPath
                              failedTerminalPath
                              startableTerminalPath ]
                            |> List.tryFind (fun path ->
                                route.Request.PostData
                                |> Option.ofObj
                                |> Option.exists _.Contains(
                                    WorktreePath.displayName path,
                                    StringComparison.Ordinal))

                        let result: Result<EmbeddedTerminalStartResult, string> =
                            match requestedPath with
                            | None ->
                                Error "Unknown terminal worktree"
                            | Some path ->
                                let terminalId =
                                    EmbeddedTerminalId(
                                        (100 + startCalls).ToString("D32"))

                                registry <-
                                    { Tabs =
                                        registry.Tabs
                                        @ [ runningTerminal terminalId path 61236 ] }

                                Ok
                                    { Snapshot = registry
                                      TerminalId = terminalId }

                        route.FulfillAsync(
                            RouteFulfillOptions(
                                ContentType = "application/json",
                                Body = serialize result)))

            do!
                this.Page.RouteAsync(
                    "**/IWorktreeApi/closeEmbeddedTerminal",
                    fun route ->
                        closeCalls <- closeCalls + 1
                        let closingId =
                            match closeCalls with
                            | 1 -> Some firstTerminalId
                            | 2 -> Some firstAlternateTerminalId
                            | _ -> None

                        registry <-
                            { Tabs =
                                registry.Tabs
                                |> List.filter (fun tab ->
                                    closingId <> Some tab.Id) }

                        route.FulfillAsync(
                            RouteFulfillOptions(
                                ContentType = "application/json",
                                Body =
                                    serialize
                                        (Ok registry:
                                            Result<EmbeddedTerminalSnapshot, string>))))

            for port, marker in
                [ 61234, "first"
                  61235, "second"
                  61236, "started"
                  61237, "first-alternate" ] do
                do!
                    this.Page.RouteAsync(
                        $"http://127.0.0.1:{port}/**",
                        fun route ->
                            route.FulfillAsync(
                                RouteFulfillOptions(
                                    ContentType = "text/html; charset=utf-8",
                                    Body = terminalDocument marker)))

            let! _ = this.Page.GotoAsync(ServerFixture.viteUrl)
            do!
                this.Page
                    .Locator(".wt-card .branch-name")
                    .First
                    .WaitForAsync(LocatorWaitForOptions(Timeout = 15000.0f))
            do!
                this.Page
                    .Locator(".terminal-pane.open")
                    .WaitForAsync(LocatorWaitForOptions(Timeout = 10000.0f))
            do! focusCanvasCard this.Page "feature-active"

            for port in [ 61234; 61235; 61237 ] do
                do!
                    this.Page
                        .FrameLocator(
                            $"iframe[src^='http://127.0.0.1:{port}/']"
                        )
                        .Locator(".xterm-helper-textarea")
                        .WaitForAsync(
                            LocatorWaitForOptions(Timeout = 5000.0f)
                        )
        }

    [<Test>]
    member this.``Terminal strip exposes accessible state and stable workspace geometry``() =
        task {
            let pane = this.Page.Locator(".terminal-pane")
            let tabs = this.Page.Locator(".terminal-tab")
            let labels = this.Page.Locator(".terminal-tab-label")
            let tabList = this.Page.GetByRole(AriaRole.Tablist)
            let iframes = this.Page.Locator(".terminal-iframe")

            let! paneRole = pane.GetAttributeAsync("role")
            let! tabListLabel = tabList.GetAttributeAsync("aria-label")
            let! tabCount = tabs.CountAsync()
            let! tabLabels = labels.AllTextContentsAsync()
            let selected = selectedTab this.Page
            let! selectedLabel =
                selected.Locator(".terminal-tab-label").TextContentAsync()
            let! selectedAria = selected.GetAttributeAsync("aria-selected")
            let! iframeCount = iframes.CountAsync()
            let! activeIframeCount =
                this.Page.Locator(".terminal-iframe-active").CountAsync()
            let! scrollingValues =
                iframes.EvaluateAllAsync<string[]>(
                    "frames => frames.map(frame => frame.getAttribute('scrolling'))")
            let! geometry =
                this.Page.EvaluateAsync<float[]>(
                    """() => {
                        const pane = document.querySelector('.terminal-pane').getBoundingClientRect();
                        const layout = document.querySelector('.app-layout').getBoundingClientRect();
                        const head = document.querySelector('.terminal-pane-header').getBoundingClientRect();
                        const tabTops = Array.from(document.querySelectorAll('.terminal-tab'))
                            .map(tab => tab.getBoundingClientRect().top);
                        return [pane.width / layout.width, head.height, ...tabTops];
                    }""")

            Assert.Multiple(fun () ->
                Assert.That(paneRole, Is.EqualTo("region"))
                Assert.That(
                    tabListLabel,
                    Is.EqualTo("Terminals for the selected worktree")
                )
                Assert.That(tabCount, Is.EqualTo(2))
                Assert.That(
                    tabLabels,
                    Is.EqualTo(
                        [| firstTerminalActivity
                           firstAlternateTerminalActivity |])
                )
                Assert.That(selectedLabel, Is.EqualTo(firstTerminalActivity))
                Assert.That(selectedAria, Is.EqualTo("true"))
                Assert.That(iframeCount, Is.EqualTo(3))
                Assert.That(activeIframeCount, Is.EqualTo(1))
                Assert.That(scrollingValues, Is.All.EqualTo("no"))
                Assert.That(geometry[0], Is.EqualTo(0.5).Within(0.03))
                Assert.That(geometry[1], Is.InRange(34.0, 48.0))
                Assert.That(geometry[2], Is.EqualTo(geometry[3]).Within(1.0)))
        }

    [<Test>]
    member this.``Tab selection is remembered per worktree while frames stay mounted``() =
        task {
            do! rememberFrames this.Page

            let secondTab = tabFor this.Page firstAlternateTerminalActivity
            do! secondTab.ClickAsync()
            do!
                secondTab.WaitForAsync(
                    LocatorWaitForOptions(Timeout = 5000.0f))
            let! secondSelected =
                secondTab.GetAttributeAsync("aria-selected")
            let! mountedAfterClick = framesStillMounted this.Page

            let firstTab = tabFor this.Page firstTerminalActivity
            do! secondTab.FocusAsync()
            do! secondTab.PressAsync("ArrowLeft")
            let! firstSelected =
                firstTab.GetAttributeAsync("aria-selected")
            let! focusedLabel =
                this.Page.EvaluateAsync<string>(
                    "() => document.activeElement.querySelector('.terminal-tab-label').textContent")
            let! mountedAfterKeyboard = framesStillMounted this.Page
            do! secondTab.ClickAsync()

            do! focusCanvasCard this.Page "feature-recent"
            let! recentTabCount =
                this.Page.Locator(".terminal-tab").CountAsync()
            let! selectedFromCard =
                (selectedTab this.Page)
                    .Locator(".terminal-tab-label")
                    .TextContentAsync()

            do! focusCanvasCard this.Page "feature-active"
            let! rememberedSelection =
                (selectedTab this.Page)
                    .Locator(".terminal-tab-label")
                    .TextContentAsync()

            do! focusCanvasCard this.Page "feature-multidoc"
            let! selectedCount = (selectedTab this.Page).CountAsync()
            let emptyState = this.Page.Locator(".terminal-pane-empty")
            let! emptyText = emptyState.TextContentAsync()
            let! mountedInEmptyState = framesStillMounted this.Page

            do!
                emptyState
                    .GetByRole(AriaRole.Button, LocatorGetByRoleOptions(Name = "Start terminal"))
                    .ClickAsync()
            do!
                (tabFor this.Page "Terminal 1")
                    .WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let! startedSelected =
                (selectedTab this.Page)
                    .Locator(".terminal-tab-label")
                    .TextContentAsync()
            let! startedFrameCount =
                this.Page.Locator(".terminal-iframe").CountAsync()

            Assert.Multiple(fun () ->
                Assert.That(secondSelected, Is.EqualTo("true"))
                Assert.That(mountedAfterClick, Is.True)
                Assert.That(firstSelected, Is.EqualTo("true"))
                Assert.That(focusedLabel, Is.EqualTo(firstTerminalActivity))
                Assert.That(mountedAfterKeyboard, Is.True)
                Assert.That(recentTabCount, Is.EqualTo(1))
                Assert.That(selectedFromCard, Is.EqualTo("Terminal 1"))
                Assert.That(rememberedSelection, Is.EqualTo(firstAlternateTerminalActivity))
                Assert.That(selectedCount, Is.EqualTo(0))
                Assert.That(emptyText, Does.Contain("feature-multidoc"))
                Assert.That(mountedInEmptyState, Is.True)
                Assert.That(startCalls, Is.EqualTo(1))
                Assert.That(startedSelected, Is.EqualTo("Terminal 1"))
                Assert.That(startedFrameCount, Is.EqualTo(4)))
        }

    [<Test>]
    member this.``Interrupted tabs show their own error without disconnecting live terminals``() =
        task {
            do! rememberFrames this.Page
            do! focusCanvasCard this.Page "feature-idle"

            let! error =
                this.Page
                    .Locator(".terminal-pane-error")
                    .TextContentAsync()
            let! activeFrameCount =
                this.Page.Locator(".terminal-iframe-active").CountAsync()
            let! mounted = framesStillMounted this.Page
            let! visibleTabs =
                (tabFor this.Page "Terminal 1").CountAsync()

            Assert.Multiple(fun () ->
                Assert.That(error, Does.Contain("ttyd exited with code 1"))
                Assert.That(visibleTabs, Is.EqualTo(1))
                Assert.That(activeFrameCount, Is.EqualTo(0))
                Assert.That(mounted, Is.True))
        }

    [<Test>]
    member this.``Unsafe running endpoint renders an error instead of an iframe``() =
        task {
            do! focusCanvasCard this.Page "feature-stale"

            let! error =
                this.Page
                    .Locator(".terminal-pane-error")
                    .TextContentAsync()
            let! unsafeIframeCount =
                this.Page
                    .Locator(
                        "[data-terminal-worktree=\"Q:/code/TestProject/feature-stale\"]")
                    .CountAsync()

            Assert.Multiple(fun () ->
                Assert.That(error, Does.Contain("unsafe endpoint"))
                Assert.That(unsafeIframeCount, Is.EqualTo(0)))
        }

    [<Test>]
    member this.``Card terminal action reuses an existing tab while New creates another``() =
        task {
            do! rememberFrames this.Page
            let terminalToggle =
                this.Page.Locator(
                    ".header-controls .ctrl-btn",
                    PageLocatorOptions(HasText = "Terminal"))
            let! _ =
                this.Page.EvaluateAsync(
                    $"""() => {{
                        window.__secondTerminalFrame =
                            document.querySelector('[data-terminal-id="{EmbeddedTerminalId.value secondTerminalId}"]');
                    }}""")

            do! terminalToggle.ClickAsync()
            let! _ =
                this.Page.WaitForFunctionAsync(
                    "() => document.querySelector('.terminal-pane').hidden")

            let! hiddenFrameCount =
                this.Page.Locator(".terminal-iframe").CountAsync()
            let! mountedWhileHidden = framesStillMounted this.Page

            do! focusCanvasCard this.Page "feature-active"
            let! selectedWhileHidden =
                (selectedTab this.Page)
                    .Locator(".terminal-tab-label")
                    .TextContentAsync()

            do! terminalToggle.ClickAsync()
            do!
                this.Page
                    .Locator(".terminal-pane.open")
                    .WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let! selectedAfterToggle =
                (selectedTab this.Page)
                    .Locator(".terminal-tab-label")
                    .TextContentAsync()

            do!
                (cardFor this.Page "feature-recent")
                    .Locator(".embedded-terminal-btn")
                    .ClickAsync()
            let! _ =
                this.Page.WaitForFunctionAsync(
                    """() => {
                        const activeFrame = document.querySelector('.terminal-iframe-active');
                        return document.querySelectorAll('.terminal-tab').length === 1
                            && !!activeFrame
                            && document.activeElement === activeFrame;
                    }""")
            let! selectedAfterCardAction =
                (selectedTab this.Page)
                    .Locator(".terminal-tab-label")
                    .TextContentAsync()
            let! visibleTabsAfterCardAction =
                this.Page.Locator(".terminal-tab").CountAsync()
            let startCallsAfterCardAction = startCalls
            do!
                expectActiveTerminalInputFocused
                    this.Page
                    "Card embedded-terminal action"

            do! this.Page.Locator(".terminal-new-btn").ClickAsync()
            let! _ =
                this.Page.WaitForFunctionAsync(
                    "() => document.querySelectorAll('.terminal-tab').length === 2")
            do!
                expectActiveTerminalInputFocused
                    this.Page
                    "New terminal action"
            let! selectedAfterNew =
                (selectedTab this.Page)
                    .Locator(".terminal-tab-label")
                    .TextContentAsync()
            let! visibleTabsAfterNew =
                this.Page.Locator(".terminal-tab").CountAsync()
            let! originalFramePreserved =
                this.Page.EvaluateAsync<bool>(
                    $"""() => {{
                        const current = document.querySelector(
                            '[data-terminal-id="{EmbeddedTerminalId.value secondTerminalId}"]');
                        return current === window.__secondTerminalFrame && current.isConnected;
                    }}""")

            Assert.Multiple(fun () ->
                Assert.That(hiddenFrameCount, Is.EqualTo(3))
                Assert.That(mountedWhileHidden, Is.True)
                Assert.That(selectedWhileHidden, Is.EqualTo(firstTerminalActivity))
                Assert.That(selectedAfterToggle, Is.EqualTo(firstTerminalActivity))
                Assert.That(startCallsAfterCardAction, Is.Zero)
                Assert.That(visibleTabsAfterCardAction, Is.EqualTo(1))
                Assert.That(selectedAfterCardAction, Is.EqualTo("Terminal 1"))
                Assert.That(startCalls, Is.EqualTo(1))
                Assert.That(visibleTabsAfterNew, Is.EqualTo(2))
                Assert.That(selectedAfterNew, Is.EqualTo("Terminal 2"))
                Assert.That(originalFramePreserved, Is.True))
        }

    [<Test>]
    member this.``Terminal activation follows visibility and the active iframe``() =
        task {
            let firstBrowserFrame =
                this.Page.Frames
                |> Seq.find _.Url.StartsWith(
                    "http://127.0.0.1:61234/",
                    StringComparison.Ordinal
                )

            let alternateBrowserFrame =
                this.Page.Frames
                |> Seq.find _.Url.StartsWith(
                    "http://127.0.0.1:61237/",
                    StringComparison.Ordinal
                )

            let otherWorktreeFrame =
                this.Page.Frames
                |> Seq.find _.Url.StartsWith(
                    "http://127.0.0.1:61235/",
                    StringComparison.Ordinal
                )

            do! this.Page.BringToFrontAsync()
            do! this.Page.Locator(".dashboard").FocusAsync()
            let! _ =
                this.Page.WaitForFunctionAsync(
                    "() => document.visibilityState === 'visible' && document.hasFocus()",
                    null,
                    PageWaitForFunctionOptions(Timeout = 5000.0f)
                )
            let! _ =
                this.Page.EvaluateAsync(
                    "() => window.dispatchEvent(new Event('focus'))"
                )

            do!
                waitForTerminalSignal
                    this.Page
                    firstBrowserFrame
                    "Initial active-terminal activation"
                    "() => window.__terminalVisibleMessages >= 1"

            let! firstBefore =
                firstBrowserFrame.EvaluateAsync<int>(
                    "() => window.__terminalVisibleMessages"
                )
            let! alternateBefore =
                alternateBrowserFrame.EvaluateAsync<int>(
                    "() => window.__terminalVisibleMessages"
                )
            let! otherWorktreeBefore =
                otherWorktreeFrame.EvaluateAsync<int>(
                    "() => window.__terminalVisibleMessages"
                )

            registry <-
                { Tabs =
                    registry.Tabs
                    |> List.filter (fun tab ->
                        tab.Id <> firstTerminalId) }

            let waitForNext stage expected =
                waitForTerminalSignal
                    this.Page
                    alternateBrowserFrame
                    stage
                    $"() => window.__terminalVisibleMessages > {expected}"

            let! _ =
                this.Page.WaitForFunctionAsync(
                    $"""() => document.querySelector('.terminal-iframe-active')
                        ?.getAttribute('data-terminal-id') ===
                        '{EmbeddedTerminalId.value firstAlternateTerminalId}'""",
                    null,
                    PageWaitForFunctionOptions(Timeout = 5000.0f)
                )

            do!
                waitForNext
                    "Registry fallback activated the alternate terminal"
                    alternateBefore

            let! afterSelection =
                alternateBrowserFrame.EvaluateAsync<int>(
                    "() => window.__terminalVisibleMessages"
                )

            let! _ =
                this.Page.EvaluateAsync(
                    "() => document.dispatchEvent(new Event('visibilitychange'))"
                )

            do!
                waitForNext
                    "Visible-document signal reactivated the alternate terminal"
                    afterSelection
            let! afterVisibility =
                alternateBrowserFrame.EvaluateAsync<int>(
                    "() => window.__terminalVisibleMessages"
                )

            let! _ =
                this.Page.EvaluateAsync(
                    "() => window.dispatchEvent(new Event('focus'))"
                )

            do!
                waitForNext
                    "Window-focus signal reactivated the alternate terminal"
                    afterVisibility
            let! afterFocus =
                alternateBrowserFrame.EvaluateAsync<int>(
                    "() => window.__terminalVisibleMessages"
                )

            let terminalToggle =
                this.Page.Locator(
                    ".header-controls .ctrl-btn",
                    PageLocatorOptions(HasText = "Terminal")
                )
            do! terminalToggle.ClickAsync()
            let! _ =
                this.Page.WaitForFunctionAsync(
                    "() => document.querySelector('.terminal-pane').hidden"
                )
            do!
                waitForTerminalSignal
                    this.Page
                    alternateBrowserFrame
                    "Terminal pane hide deactivated the alternate terminal"
                    "() => window.__terminalInactiveMessages >= 1"

            do! terminalToggle.ClickAsync()

            do!
                waitForNext
                    "Terminal pane reopen reactivated the alternate terminal"
                    afterFocus
            let! otherWorktreeAfter =
                otherWorktreeFrame.EvaluateAsync<int>(
                    "() => window.__terminalVisibleMessages"
                )

            Assert.Multiple(fun () ->
                Assert.That(firstBefore, Is.GreaterThanOrEqualTo(1))
                Assert.That(alternateBefore, Is.Zero)
                Assert.That(otherWorktreeBefore, Is.Zero)
                Assert.That(otherWorktreeAfter, Is.EqualTo(otherWorktreeBefore))
                Assert.That(afterSelection, Is.GreaterThan(alternateBefore))
                Assert.That(afterVisibility, Is.GreaterThan(afterSelection))
                Assert.That(afterFocus, Is.GreaterThan(afterVisibility)))
        }

    [<Test>]
    member this.``T focuses an existing terminal or starts one when absent``() =
        task {
            let terminalToggle =
                this.Page.Locator(
                    ".header-controls .ctrl-btn",
                    PageLocatorOptions(HasText = "Terminal"))

            do! terminalToggle.ClickAsync()
            let! _ =
                this.Page.WaitForFunctionAsync(
                    "() => document.querySelector('.terminal-pane').hidden")

            do! focusCanvasCard this.Page "feature-recent"
            do! this.Page.Locator(".dashboard").FocusAsync()
            do! this.Page.Keyboard.PressAsync("t")
            do!
                this.Page
                    .Locator(".terminal-pane.open")
                    .WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            do!
                expectActiveTerminalInputFocused
                    this.Page
                    "T shortcut for an existing terminal"

            let! existingTabCount =
                this.Page.Locator(".terminal-tab").CountAsync()
            let! existingSelected =
                (selectedTab this.Page)
                    .Locator(".terminal-tab-label")
                    .TextContentAsync()
            let startCallsAfterExisting = startCalls

            do! terminalToggle.ClickAsync()
            let! _ =
                this.Page.WaitForFunctionAsync(
                    "() => document.querySelector('.terminal-pane').hidden")

            do! focusCanvasCard this.Page "feature-multidoc"
            do! this.Page.Locator(".dashboard").FocusAsync()
            do! this.Page.Keyboard.PressAsync("t")
            let! _ =
                this.Page.WaitForFunctionAsync(
                    "() => document.querySelectorAll('.terminal-tab').length === 1")
            do!
                expectActiveTerminalInputFocused
                    this.Page
                    "T shortcut for a new terminal"
            let! startedSelected =
                (selectedTab this.Page)
                    .Locator(".terminal-tab-label")
                    .TextContentAsync()

            Assert.Multiple(fun () ->
                Assert.That(startCallsAfterExisting, Is.Zero)
                Assert.That(existingTabCount, Is.EqualTo(1))
                Assert.That(existingSelected, Is.EqualTo("Terminal 1"))
                Assert.That(startCalls, Is.EqualTo(1))
                Assert.That(startedSelected, Is.EqualTo("Terminal 1")))
        }

    [<Test>]
    member this.``Closing tabs stays within the selected worktree and leaves the pane open``() =
        task {
            let! _ =
                this.Page.EvaluateAsync(
                    $"""() => {{
                        window.__alternateTerminalFrame =
                            document.querySelector('[data-terminal-id="{EmbeddedTerminalId.value firstAlternateTerminalId}"]');
                    }}""")

            do!
                (closeButtonFor this.Page firstTerminalActivity)
                    .ClickAsync()
            let! _ =
                this.Page.WaitForFunctionAsync(
                    "() => document.querySelectorAll('.terminal-tab').length === 1")
            let! neighbour =
                (selectedTab this.Page)
                    .Locator(".terminal-tab-label")
                    .TextContentAsync()
            let! alternatePreserved =
                this.Page.EvaluateAsync<bool>(
                    $"""() => {{
                        const current = document.querySelector(
                            '[data-terminal-id="{EmbeddedTerminalId.value firstAlternateTerminalId}"]');
                        return current === window.__alternateTerminalFrame && current.isConnected;
                    }}""")

            do!
                (closeButtonFor this.Page firstAlternateTerminalActivity)
                    .ClickAsync()
            let! _ =
                this.Page.WaitForFunctionAsync(
                    "() => document.querySelectorAll('.terminal-tab').length === 0")
            let! remainingTabs =
                this.Page.Locator(".terminal-tab").CountAsync()
            let! paneHidden =
                this.Page.Locator(".terminal-pane").IsHiddenAsync()
            let! emptyText =
                this.Page
                    .Locator(".terminal-pane-empty")
                    .TextContentAsync()

            Assert.Multiple(fun () ->
                Assert.That(closeCalls, Is.EqualTo(2))
                Assert.That(neighbour, Is.EqualTo(firstAlternateTerminalActivity))
                Assert.That(alternatePreserved, Is.True)
                Assert.That(remainingTabs, Is.EqualTo(0))
                Assert.That(paneHidden, Is.False)
                Assert.That(emptyText, Does.Contain("feature-active")))
        }

    [<Test>]
    member this.``Middle-clicking a terminal tab closes it``() =
        task {
            do!
                (tabFor this.Page firstTerminalActivity)
                    .ClickAsync(
                        LocatorClickOptions(Button = MouseButton.Middle)
                    )

            let! _ =
                this.Page.WaitForFunctionAsync(
                    "() => document.querySelectorAll('.terminal-tab').length === 1")
            let! remainingLabels =
                this.Page
                    .Locator(".terminal-tab-label")
                    .AllTextContentsAsync()
            let! selectedLabel =
                (selectedTab this.Page)
                    .Locator(".terminal-tab-label")
                    .TextContentAsync()

            Assert.Multiple(fun () ->
                Assert.That(closeCalls, Is.EqualTo(1))
                Assert.That(
                    remainingLabels,
                    Is.EqualTo([| firstAlternateTerminalActivity |])
                )
                Assert.That(selectedLabel, Is.EqualTo(firstAlternateTerminalActivity)))
        }

    [<Test>]
    member this.``Outer workspace overflow stays hidden while dashboard and xterm remain scrollable``() =
        task {
            let layout = this.Page.Locator(".app-layout")
            let dashboard = this.Page.Locator(".dashboard")
            let iframe =
                this.Page.Locator(
                    $"[data-terminal-id=\"{EmbeddedTerminalId.value firstTerminalId}\"]")
            let! layoutOverflow =
                layout.EvaluateAsync<string[]>(
                    "element => { const style = getComputedStyle(element); return [style.overflowX, style.overflowY]; }")
            let! dashboardOverflow =
                dashboard.EvaluateAsync<string>(
                    "element => getComputedStyle(element).overflowY")
            let! scrolling = iframe.GetAttributeAsync("scrolling")
            let frame =
                this.Page.Frames
                |> Seq.find (fun candidate ->
                    candidate.Url.StartsWith(
                        "http://127.0.0.1:61234/",
                        StringComparison.Ordinal))
            let viewport = frame.Locator(".xterm-viewport")
            do!
                viewport.WaitForAsync(
                    LocatorWaitForOptions(Timeout = 5000.0f))
            let! before =
                viewport.EvaluateAsync<int[]>(
                    "element => [element.clientHeight, element.scrollHeight, element.scrollTop]")
            let! _ =
                viewport.EvaluateAsync(
                    "element => { element.scrollTop = 120; }")
            let! after =
                viewport.EvaluateAsync<int>(
                    "element => element.scrollTop")

            Assert.Multiple(fun () ->
                Assert.That(layoutOverflow[0], Is.EqualTo("hidden"))
                Assert.That(layoutOverflow[1], Is.EqualTo("hidden"))
                Assert.That(dashboardOverflow, Is.EqualTo("auto"))
                Assert.That(scrolling, Is.EqualTo("no"))
                Assert.That(before[1], Is.GreaterThan(before[0]))
                Assert.That(after, Is.GreaterThan(0)))
        }
