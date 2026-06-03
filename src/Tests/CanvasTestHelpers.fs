module Tests.CanvasTestHelpers

open Microsoft.Playwright

let dashboard (page: IPage) =
    page.Locator(".dashboard")

let canvasToggleBtn (page: IPage) =
    page.Locator(".header-controls .ctrl-btn", PageLocatorOptions(HasText = "Canvas"))

let canvasPaneOpen (page: IPage) =
    page.Locator(".canvas-pane.open")

/// Focus the card for a specific branch.
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

/// Press ArrowDown from the dashboard until a wt-card receives focus.
let focusFirstCard (page: IPage) =
    task {
        let db = dashboard page
        do! db.FocusAsync()
        do! page.Keyboard.PressAsync("ArrowDown")
        do! page.Keyboard.PressAsync("ArrowDown")
        let! _ = page.WaitForFunctionAsync(
            "() => document.querySelector('.wt-card.focused') !== null",
            null, PageWaitForFunctionOptions(Timeout = 5000.0f))
        ()
    }
