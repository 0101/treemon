module Tests.WorkspaceLayoutTests

open NUnit.Framework
open Microsoft.Playwright
open Microsoft.Playwright.NUnit
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
