module Tests.CanvasTestHelpers

open System
open Microsoft.Playwright

let dashboard (page: IPage) =
    page.Locator(".dashboard")

let canvasToggleBtn (page: IPage) =
    page.Locator(".header-controls .ctrl-btn", PageLocatorOptions(HasText = "Canvas"))

let canvasPaneOpen (page: IPage) =
    page.Locator(".canvas-pane.open")

let waitForCanvasPaneOpen (page: IPage) =
    (canvasPaneOpen page).WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

let waitForCanvasPaneClosed (page: IPage) =
    task {
        let! _ =
            page.WaitForFunctionAsync(
                "() => !document.querySelector('.canvas-pane.open')",
                null,
                PageWaitForFunctionOptions(Timeout = 5000.0f))
        ()
    }

let ensureCanvasPaneOpen (page: IPage) =
    task {
        let btn = canvasToggleBtn page
        do! btn.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

        let! openCount = (canvasPaneOpen page).CountAsync()
        if openCount = 0 then
            do! btn.ClickAsync()

        try
            do! waitForCanvasPaneOpen page
        with
        | :? TimeoutException ->
            let! retryOpenCount = (canvasPaneOpen page).CountAsync()
            if retryOpenCount = 0 then
                do! btn.ClickAsync()
            do! waitForCanvasPaneOpen page
    }

let ensureCanvasPaneClosed (page: IPage) =
    task {
        let! openCount = (canvasPaneOpen page).CountAsync()
        if openCount > 0 then
            do! (canvasToggleBtn page).ClickAsync()
            do! waitForCanvasPaneClosed page
    }

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
