module Tests.CanvasTestHelpers

open System
open Microsoft.Playwright
open NUnit.Framework

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
        let branchName =
            page.Locator(".wt-card .branch-name", PageLocatorOptions(HasText = branch))
        let focusedCard =
            page.Locator(
                ".wt-card.focused",
                PageLocatorOptions(Has = page.Locator(".branch-name", PageLocatorOptions(HasText = branch))))
        do! card.First.ScrollIntoViewIfNeededAsync()
        do! branchName.First.ClickAsync()
        do! focusedCard.First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
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

let assertStandaloneSelectionUnavailable (page: IPage) (target: ILocator) =
    task {
        do! target.WaitForAsync()
        let! _ =
            target.EvaluateAsync(
                """element => {
                    window.__standaloneMessages = [];
                    window.addEventListener('message', event => {
                        if (event.data?.action === 'canvas-selection') {
                            window.__standaloneMessages.push(event.data);
                        }
                    });
                    const range = document.createRange();
                    range.selectNodeContents(element);
                    const selection = window.getSelection();
                    selection.removeAllRanges();
                    selection.addRange(range);
                }"""
            )
        let! _ =
            page.WaitForFunctionAsync(
                "() => document.querySelector('canvas-selection-context')?.style.visibility === 'visible'"
            )
        let! _ =
            page.EvaluateAsync(
                "() => { window.__standaloneDirectSend = window.canvasSend('canvas-selection', { selectedText: 'probe' }); }"
            )
        do!
            page.Locator(
                "canvas-selection-context button[data-intent='explain']"
            ).ClickAsync()
        let expected = "Canvas messaging is unavailable in this document."
        let! _ =
            page.WaitForFunctionAsync(
                "(expected) => document.querySelector('canvas-selection-context')?.shadowRoot.querySelector('.error').textContent === expected",
                expected
            )
        let! outcome =
            page.EvaluateAsync<string>(
                """() => JSON.stringify({
                    directSend: window.__standaloneDirectSend,
                    messages: window.__standaloneMessages.length,
                    toolbar: document.querySelector('canvas-selection-context').style.display,
                    processing: document.querySelector('canvas-selection-processing')?.style.display ?? 'none',
                    error: document.querySelector('canvas-selection-context').shadowRoot.querySelector('.error').textContent
                })"""
            )
        Assert.That(
            outcome,
            Is.EqualTo(
                """{"directSend":false,"messages":0,"toolbar":"block","processing":"none","error":"Canvas messaging is unavailable in this document."}"""
            )
        )
    }
