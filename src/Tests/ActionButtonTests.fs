module Tests.ActionButtonTests

open NUnit.Framework
open Microsoft.Playwright
open Microsoft.Playwright.NUnit

[<TestFixture>]
[<Category("E2E")>]
type ActionButtonTests() =
    inherit PageTest()

    let baseUrl = ServerFixture.viteUrl

    let compactBtn (page: IPage) =
        page.Locator(".header-controls .ctrl-btn", PageLocatorOptions(HasText = "Compact"))

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

    [<Test>]
    [<Category("Fast")>]
    member this.``Action buttons have action-btn CSS class``() =
        task {
            let actionBtns = this.Page.Locator(".wt-card .action-btn")
            do! actionBtns.First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let! count = actionBtns.CountAsync()
            Assert.That(count, Is.GreaterThanOrEqualTo(1), "Fixture has worktrees with action conditions; action-btn elements should be present")
        }

    [<Test>]
    [<Category("Fast")>]
    member this.``Action buttons are inside wt-card DOM``() =
        task {
            let actionBtns = this.Page.Locator(".wt-card .action-btn")
            do! actionBtns.First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let! count = actionBtns.CountAsync()
            Assert.That(count, Is.GreaterThanOrEqualTo(1), "Action buttons should be nested inside .wt-card")

            let outsideCard = this.Page.Locator(".action-btn:not(.wt-card .action-btn)")
            let! outsideCount = outsideCard.CountAsync()
            Assert.That(outsideCount, Is.EqualTo(0), "No action buttons should exist outside of .wt-card")
        }

    [<Test>]
    [<Category("Fast")>]
    member this.``Create-PR action button appears on cards with no PR badge``() =
        task {
            let noPrCards = this.Page.Locator(".wt-card:not(.compact):not(:has(.pr-badge))")
            do! noPrCards.First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let! noPrCount = noPrCards.CountAsync()

            if noPrCount = 0 then
                Assert.Ignore("No cards without PR badge in live data; skipping create-PR test")
            else
                let actionBtns = noPrCards.Locator(".action-btn")
                let! btnCount = actionBtns.CountAsync()
                Assert.That(btnCount, Is.GreaterThanOrEqualTo(1),
                    "Cards without PR badges should have a create-PR action button")
        }

    [<Test>]
    member this.``Create-PR action button is inside pr-row``() =
        task {
            let prRowsNoPrBadge = this.Page.Locator(".wt-card:not(.compact) .pr-row:not(:has(.pr-badge))")
            let! count = prRowsNoPrBadge.CountAsync()

            if count = 0 then
                Assert.Ignore("No pr-row without pr-badge in live data; skipping create-PR placement test")
            else
                let actionBtns = prRowsNoPrBadge.Locator(".action-btn")
                let! btnCount = actionBtns.CountAsync()
                Assert.That(btnCount, Is.GreaterThanOrEqualTo(1),
                    "Create-PR action button should be inside .pr-row when no PR exists")
        }

    [<Test>]
    member this.``PR-comments action button appears next to thread-badge that is not dimmed``() =
        task {
            let activeThreadBadges = this.Page.Locator(".wt-card .thread-badge:not(.dimmed)")
            let! count = activeThreadBadges.CountAsync()

            if count = 0 then
                Assert.Ignore("No active (non-dimmed) thread badges in live data; skipping PR-comments action test")
            else
                let! hasAdjacentAction =
                    activeThreadBadges.First.EvaluateAsync<bool>(
                        "el => { const next = el.nextElementSibling; return next && next.classList.contains('action-btn'); }"
                    )
                Assert.That(hasAdjacentAction, Is.True,
                    "Active thread badge should have an adjacent action-btn sibling")
        }

    [<Test>]
    member this.``PR-comments action button does NOT appear next to dimmed thread-badge``() =
        task {
            let dimmedThreadBadges = this.Page.Locator(".wt-card .thread-badge.dimmed")
            let! count = dimmedThreadBadges.CountAsync()

            if count = 0 then
                Assert.Ignore("No dimmed thread badges in live data; skipping dimmed-thread test")
            else
                let! hasAdjacentAction =
                    dimmedThreadBadges.EvaluateAllAsync<bool>(
                        "els => els.every(el => { const next = el.nextElementSibling; return !next || !next.classList.contains('action-btn'); })"
                    )
                Assert.That(hasAdjacentAction, Is.True,
                    "Dimmed thread badges should NOT have an adjacent action-btn sibling")
        }

    [<Test>]
    [<Category("Fast")>]
    member this.``Fix-build action button appears next to failed build badge``() =
        task {
            let failedBuildBadges = this.Page.Locator(".wt-card .build-badge.failed")
            do! failedBuildBadges.First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let! count = failedBuildBadges.CountAsync()

            if count = 0 then
                Assert.Ignore("No failed build badges in live data; skipping fix-build action test")
            else
                let! hasAdjacentAction =
                    failedBuildBadges.First.EvaluateAsync<bool>(
                        "el => { const next = el.nextElementSibling; return next && next.classList.contains('action-btn'); }"
                    )
                Assert.That(hasAdjacentAction, Is.True,
                    "Failed build badge should have an adjacent action-btn sibling")
        }

    [<Test>]
    member this.``Fix-build action button does NOT appear next to succeeded build badge``() =
        task {
            let succeededBuildBadges = this.Page.Locator(".wt-card .build-badge.succeeded")
            let! count = succeededBuildBadges.CountAsync()

            if count = 0 then
                Assert.Ignore("No succeeded build badges in live data; skipping succeeded-build test")
            else
                let! noAdjacentAction =
                    succeededBuildBadges.EvaluateAllAsync<bool>(
                        "els => els.every(el => { const next = el.nextElementSibling; return !next || !next.classList.contains('action-btn'); })"
                    )
                Assert.That(noAdjacentAction, Is.True,
                    "Succeeded build badges should NOT have an adjacent action-btn sibling")
        }

    [<Test>]
    member this.``Action buttons render in compact card layout``() =
        task {
            do! (compactBtn this.Page).ClickAsync()

            let compactCards = this.Page.Locator(".wt-card.compact")
            do! compactCards.First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            let compactActionBtns = this.Page.Locator(".wt-card.compact .action-btn")
            let! compactBtnCount = compactActionBtns.CountAsync()
            Assert.That(compactBtnCount, Is.GreaterThanOrEqualTo(1),
                "Action buttons should render in compact card layout")
        }

    [<Test>]
    member this.``Action buttons render in full card layout``() =
        task {
            let fullCards = this.Page.Locator(".wt-card:not(.compact)")
            do! fullCards.First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            let fullActionBtns = this.Page.Locator(".wt-card:not(.compact) .action-btn")
            let! fullBtnCount = fullActionBtns.CountAsync()
            Assert.That(fullBtnCount, Is.GreaterThanOrEqualTo(1),
                "Action buttons should render in full card layout")
        }

    [<Test>]
    member this.``Action button has descriptive tooltip``() =
        task {
            let actionBtns = this.Page.Locator(".wt-card .action-btn")
            do! actionBtns.First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let! count = actionBtns.CountAsync()

            if count = 0 then
                Assert.Ignore("No action buttons in live data; skipping tooltip test")
            else
                let! title = actionBtns.First.GetAttributeAsync("title")
                Assert.That(title, Is.Not.Null.And.Not.Empty,
                    "Action button should have a non-empty title attribute")
        }

    [<Test>]
    member this.``Action button has pointer cursor``() =
        task {
            let actionBtns = this.Page.Locator(".wt-card .action-btn")
            do! actionBtns.First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let! count = actionBtns.CountAsync()

            if count = 0 then
                Assert.Ignore("No action buttons in live data; skipping CSS test")
            else
                let! cursor =
                    actionBtns.First.EvaluateAsync<string>("el => getComputedStyle(el).cursor")
                Assert.That(cursor, Is.EqualTo("pointer"),
                    "Action button should have pointer cursor")
        }

    [<Test>]
    member this.``Main branch cards do not have create-PR action button``() =
        task {
            let mainBranchCards = this.Page.Locator(".wt-card:has(.branch-name:text-is('main')), .wt-card:has(.branch-name:text-is('master'))")
            let! count = mainBranchCards.CountAsync()

            if count = 0 then
                Assert.Ignore("No main/master branch cards in live data; skipping main-branch test")
            else
                let prRows = mainBranchCards.Locator(".pr-row")
                let! prRowCount = prRows.CountAsync()
                Assert.That(prRowCount, Is.EqualTo(0),
                    "Main/master branch cards without PRs should not have a pr-row at all")
        }
