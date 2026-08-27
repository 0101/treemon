module Tests.WorkspaceLayoutTests

open System
open System.Threading.Tasks
open NUnit.Framework
open Newtonsoft.Json
open Microsoft.Playwright
open Microsoft.Playwright.NUnit
open Shared
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

    let settle (page: IPage) = page.WaitForTimeoutAsync(400.0f)

    let showTerminal (page: IPage) =
        task {
            let! _ =
                page.EvaluateAsync(
                    "() => { const pane = document.querySelector('.terminal-pane'); pane.hidden = false; pane.classList.add('open'); document.querySelector('.app-layout').classList.remove('terminal-hidden'); }")
            return ()
        }

    let assertShare (actual: float) (total: float) (expected: float) (what: string) =
        Assert.That(actual / total, Is.EqualTo(expected).Within(0.03), $"{what} has the expected workspace share")

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
    member this.``All open workspace supports equal thirds and wide center``() =
        task {
            do! focusFirstCard this.Page
            do! ensureCanvasPaneOpen this.Page
            let buttons = this.Page.Locator(".canvas-tab-bar .canvas-width-btn")

            do! buttons.First.ClickAsync()
            do! showTerminal this.Page
            do! settle this.Page
            let! equalTotal = layoutWidth this.Page
            let! equalTerminal = paneWidth this.Page ".terminal-pane"
            let! equalCanvas = paneWidth this.Page ".canvas-pane"
            let! equalDashboard = paneWidth this.Page ".dashboard"

            do! buttons.Nth(1).ClickAsync()
            do! showTerminal this.Page
            do! settle this.Page
            let! wideTotal = layoutWidth this.Page
            let! wideTerminal = paneWidth this.Page ".terminal-pane"
            let! wideCanvas = paneWidth this.Page ".canvas-pane"
            let! wideDashboard = paneWidth this.Page ".dashboard"

            assertShare equalTerminal equalTotal 0.3333 "Terminal"
            assertShare equalCanvas equalTotal 0.3333 "Canvas"
            assertShare equalDashboard equalTotal 0.3333 "Dashboard"
            assertShare wideTerminal wideTotal 0.25 "Terminal"
            assertShare wideCanvas wideTotal 0.5 "Canvas"
            assertShare wideDashboard wideTotal 0.25 "Dashboard"
        }

    [<Test>]
    member this.``Terminal hidden workspace supports one-to-one and two-to-one``() =
        task {
            do! focusFirstCard this.Page
            do! ensureCanvasPaneOpen this.Page
            let buttons = this.Page.Locator(".canvas-tab-bar .canvas-width-btn")
            let! labels = buttons.AllTextContentsAsync()

            do! buttons.First.ClickAsync()
            do! settle this.Page
            let! equalTotal = layoutWidth this.Page
            let! equalCanvas = paneWidth this.Page ".canvas-pane"
            let! equalDashboard = paneWidth this.Page ".dashboard"

            do! buttons.Nth(1).ClickAsync()
            do! settle this.Page
            let! wideTotal = layoutWidth this.Page
            let! wideCanvas = paneWidth this.Page ".canvas-pane"
            let! wideDashboard = paneWidth this.Page ".dashboard"

            Assert.That(labels, Is.EqualTo([| "1:1"; "2:1" |]))
            assertShare equalCanvas equalTotal 0.5 "Canvas"
            assertShare equalDashboard equalTotal 0.5 "Dashboard"
            assertShare wideCanvas wideTotal 0.6667 "Canvas"
            assertShare wideDashboard wideTotal 0.3333 "Dashboard"
        }

    [<Test>]
    member this.``Narrow workspace stacks panes without horizontal overflow``() =
        task {
            do! this.Page.SetViewportSizeAsync(720, 900)
            do! focusFirstCard this.Page
            do! ensureCanvasPaneOpen this.Page
            do! this.Page.Locator(".canvas-tab-bar .canvas-width-btn").Nth(1).ClickAsync()
            do! showTerminal this.Page
            do! settle this.Page

            let! tops =
                this.Page.EvaluateAsync<float[]>(
                    "() => ['.terminal-pane', '.canvas-pane', '.dashboard'].map(s => document.querySelector(s).getBoundingClientRect().top)")
            Assert.That(tops[0], Is.LessThan(tops[1]))
            Assert.That(tops[1], Is.LessThan(tops[2]))

            let! overflow =
                this.Page.EvaluateAsync<bool>(
                    "() => document.documentElement.scrollWidth > document.documentElement.clientWidth")
            Assert.That(overflow, Is.False)
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

let private firstTerminalIntent =
    "Implementing terminal lifecycle"

let private firstAlternateTerminalIntent =
    "Reviewing host replacement"

let private runningTerminal terminalId path port =
    { Id = terminalId
      Worktree = path
      ReportedIntent = None
      Lifecycle =
        EmbeddedTerminalLifecycle.Running
            $"http://127.0.0.1:{port}/" }

let private initialTerminalSnapshot =
    { Tabs =
        [ { runningTerminal firstTerminalId firstTerminalPath 61234 with
                ReportedIntent = Some firstTerminalIntent }
          { runningTerminal firstAlternateTerminalId firstTerminalPath 61237 with
                ReportedIntent = Some firstAlternateTerminalIntent }
          runningTerminal secondTerminalId secondTerminalPath 61235
          { Id = EmbeddedTerminalId "00000000000000000000000000000004"
            Worktree = failedTerminalPath
            ReportedIntent = None
            Lifecycle =
                EmbeddedTerminalLifecycle.Interrupted
                    "ttyd exited with code 1" }
          { Id = EmbeddedTerminalId "00000000000000000000000000000005"
            Worktree = WorktreePath "Q:/code/TestProject/feature-stale"
            ReportedIntent = None
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
</head>
<body>
  <div data-terminal-marker="__MARKER__" class="xterm-viewport"><div class="scrollback"></div></div>
</body>
</html>"""
        .Replace("__MARKER__", marker, StringComparison.Ordinal)

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

    let cardFor (page: IPage) branch =
        page.Locator(
            ".wt-card",
            PageLocatorOptions(
                Has = page.Locator(
                    ".branch-name",
                    PageLocatorOptions(HasText = branch))))

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

                        let result: Result<EmbeddedTerminalSnapshot, string> =
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

                                Ok registry

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
                        [| firstTerminalIntent
                           firstAlternateTerminalIntent |])
                )
                Assert.That(selectedLabel, Is.EqualTo(firstTerminalIntent))
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

            let secondTab = tabFor this.Page firstAlternateTerminalIntent
            do! secondTab.ClickAsync()
            do!
                secondTab.WaitForAsync(
                    LocatorWaitForOptions(Timeout = 5000.0f))
            let! secondSelected =
                secondTab.GetAttributeAsync("aria-selected")
            let! mountedAfterClick = framesStillMounted this.Page

            let firstTab = tabFor this.Page firstTerminalIntent
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
                Assert.That(focusedLabel, Is.EqualTo(firstTerminalIntent))
                Assert.That(mountedAfterKeyboard, Is.True)
                Assert.That(recentTabCount, Is.EqualTo(1))
                Assert.That(selectedFromCard, Is.EqualTo("Terminal 1"))
                Assert.That(rememberedSelection, Is.EqualTo(firstAlternateTerminalIntent))
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
    member this.``Hide preserves frames while reopening can add another selected-worktree terminal``() =
        task {
            do! rememberFrames this.Page
            let! _ =
                this.Page.EvaluateAsync(
                    $"""() => {{
                        window.__secondTerminalFrame =
                            document.querySelector('[data-terminal-id="{EmbeddedTerminalId.value secondTerminalId}"]');
                    }}""")

            do!
                this.Page
                    .GetByRole(
                        AriaRole.Button,
                        PageGetByRoleOptions(Name = "Hide terminal pane"))
                    .ClickAsync()
            let! _ =
                this.Page.WaitForFunctionAsync(
                    "() => document.querySelector('.terminal-pane').hidden")

            let! hiddenFrameCount =
                this.Page.Locator(".terminal-iframe").CountAsync()
            let! mountedWhileHidden = framesStillMounted this.Page

            do! focusCanvasCard this.Page "feature-recent"
            let! selectedWhileHidden =
                (selectedTab this.Page)
                    .Locator(".terminal-tab-label")
                    .TextContentAsync()

            do!
                (cardFor this.Page "feature-recent")
                    .Locator(".embedded-terminal-btn")
                    .ClickAsync()
            do!
                this.Page
                    .Locator(".terminal-pane.open")
                    .WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let! selectedAfterReopen =
                (selectedTab this.Page)
                    .Locator(".terminal-tab-label")
                    .TextContentAsync()
            let! visibleTabsAfterReopen =
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
                Assert.That(selectedWhileHidden, Is.EqualTo("Terminal 1"))
                Assert.That(startCalls, Is.EqualTo(1))
                Assert.That(visibleTabsAfterReopen, Is.EqualTo(2))
                Assert.That(selectedAfterReopen, Is.EqualTo("Terminal 2"))
                Assert.That(originalFramePreserved, Is.True))
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
                (tabFor this.Page firstTerminalIntent)
                    .Locator(".terminal-tab-close")
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
                (tabFor this.Page firstAlternateTerminalIntent)
                    .Locator(".terminal-tab-close")
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
                Assert.That(neighbour, Is.EqualTo(firstAlternateTerminalIntent))
                Assert.That(alternatePreserved, Is.True)
                Assert.That(remainingTabs, Is.EqualTo(0))
                Assert.That(paneHidden, Is.False)
                Assert.That(emptyText, Does.Contain("feature-active")))
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
