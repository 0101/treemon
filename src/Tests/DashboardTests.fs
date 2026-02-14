module Tests.DashboardTests

open NUnit.Framework
open Microsoft.Playwright
open Microsoft.Playwright.NUnit

[<TestFixture>]
type DashboardTests() =
    inherit PageTest()

    let baseUrl = ServerFixture.viteUrl

    override this.ContextOptions() =
        let opts = base.ContextOptions()
        opts.IgnoreHTTPSErrors <- true
        opts

    [<SetUp>]
    member this.NavigateToDashboard() =
        task {
            let! _ = this.Page.GotoAsync(baseUrl)
            do! this.Page.Locator(".wt-card").First.WaitForAsync(LocatorWaitForOptions(Timeout = 45000.0f))
            return ()
        }

    [<Test>]
    member this.``Dashboard loads with title``() =
        task {
            let! heading = this.Page.Locator("h1").TextContentAsync()
            Assert.That(heading, Does.StartWith("Worktree Monitor"))
            Assert.That(heading, Does.Contain(":"))
        }

    [<Test>]
    member this.``Dashboard shows worktree count in status bar``() =
        task {
            let! statusText = this.Page.Locator(".status-bar").TextContentAsync()
            Assert.That(statusText, Does.Contain("worktrees"))
        }

    [<Test>]
    member this.``Dashboard loads with at least one worktree card``() =
        task {
            let cards = this.Page.Locator(".wt-card")
            let! count = cards.CountAsync()
            Assert.That(count, Is.GreaterThanOrEqualTo(1))
        }

    [<Test>]
    member this.``Worktree cards display branch names``() =
        task {
            let branchNames = this.Page.Locator(".wt-card .branch-name")
            let! count = branchNames.CountAsync()
            Assert.That(count, Is.GreaterThanOrEqualTo(1))

            let! firstBranch = branchNames.First.TextContentAsync()
            Assert.That(firstBranch, Is.Not.Empty)
        }

    [<Test>]
    member this.``Worktree cards display commit info``() =
        task {
            let commitLines = this.Page.Locator(".wt-card .commit-line")
            let! count = commitLines.CountAsync()
            Assert.That(count, Is.GreaterThanOrEqualTo(1))

            let! commitText = commitLines.First.TextContentAsync()
            Assert.That(commitText, Is.Not.Empty)
        }

    [<Test>]
    member this.``Worktree cards display commit time``() =
        task {
            let commitTimes = this.Page.Locator(".wt-card .commit-time")
            let! count = commitTimes.CountAsync()
            Assert.That(count, Is.GreaterThanOrEqualTo(1))

            let! timeText = commitTimes.First.TextContentAsync()
            Assert.That(timeText, Does.Contain("ago").Or.EqualTo("just now"))
        }

    [<Test>]
    member this.``CC status indicators render on cards``() =
        task {
            let ccDots = this.Page.Locator(".wt-card .cc-dot")
            let! count = ccDots.CountAsync()
            Assert.That(count, Is.GreaterThanOrEqualTo(1))

            let! cssClass = ccDots.First.GetAttributeAsync("class")
            Assert.That(
                cssClass,
                Does.Contain("active")
                    .Or.Contain("recent")
                    .Or.Contain("idle")
                    .Or.Contain("unknown")
            )
        }

    [<Test>]
    member this.``CC status dot has correct border-left color on card``() =
        task {
            let cards = this.Page.Locator(".wt-card")
            let! count = cards.CountAsync()
            Assert.That(count, Is.GreaterThanOrEqualTo(1))

            let! cardClass = cards.First.GetAttributeAsync("class")
            Assert.That(
                cardClass,
                Does.Contain("cc-active")
                    .Or.Contain("cc-recent")
                    .Or.Contain("cc-idle")
                    .Or.Contain("cc-unknown")
            )
        }

    [<Test>]
    member this.``Beads counts appear on cards``() =
        task {
            let beadsCounts = this.Page.Locator(".wt-card .beads-counts")
            let! count = beadsCounts.CountAsync()
            Assert.That(count, Is.GreaterThanOrEqualTo(1))

            let! beadsText = beadsCounts.First.TextContentAsync()
            Assert.That(beadsText, Does.Contain("O:"))
            Assert.That(beadsText, Does.Contain("P:"))
            Assert.That(beadsText, Does.Contain("D:"))
        }

    [<Test>]
    member this.``Progress bar renders in beads row``() =
        task {
            let progressBars = this.Page.Locator(".wt-card .progress-bar")
            let! count = progressBars.CountAsync()
            Assert.That(count, Is.GreaterThanOrEqualTo(1))

            let progressFills = this.Page.Locator(".wt-card .progress-fill")
            let! fillCount = progressFills.CountAsync()
            Assert.That(fillCount, Is.GreaterThanOrEqualTo(1))
        }

    [<Test>]
    member this.``PR badge shows when PR data is present``() =
        task {
            let prBadges = this.Page.Locator(".wt-card .pr-badge")
            let! count = prBadges.CountAsync()

            if count > 0 then
                let! prText = prBadges.First.TextContentAsync()
                Assert.That(prText, Does.StartWith("PR #"))
        }

    [<Test>]
    member this.``PR badge is a clickable link with href``() =
        task {
            let prLinks = this.Page.Locator(".wt-card a.pr-badge")
            let! count = prLinks.CountAsync()

            if count > 0 then
                let! href = prLinks.First.GetAttributeAsync("href")
                Assert.That(href, Is.Not.Null.And.Not.Empty)
                Assert.That(href, Does.Contain("pullrequest"))

                let! target = prLinks.First.GetAttributeAsync("target")
                Assert.That(target, Is.EqualTo("_blank"))
        }

    [<Test>]
    member this.``Thread badge shows on PR cards``() =
        task {
            let threadBadges = this.Page.Locator(".wt-card .thread-badge")
            let! count = threadBadges.CountAsync()

            if count > 0 then
                let! threadText = threadBadges.First.TextContentAsync()
                Assert.That(threadText, Does.Contain("thread").Or.Match("\\d+"))
        }

    [<Test>]
    member this.``Sort toggle button is visible and clickable``() =
        task {
            let sortBtn =
                this.Page.Locator(".header-controls .ctrl-btn", PageLocatorOptions(HasText = "Sort:"))

            do! Assertions.Expect(sortBtn).ToBeVisibleAsync()

            let! initialText = sortBtn.TextContentAsync()
            Assert.That(initialText, Does.Contain("Sort: A-Z"))

            do! sortBtn.ClickAsync()

            let! newText = sortBtn.TextContentAsync()
            Assert.That(newText, Does.Contain("Sort: Recent"))
        }

    [<Test>]
    member this.``Sort toggle changes back to A-Z on second click``() =
        task {
            let sortBtn =
                this.Page.Locator(".header-controls .ctrl-btn", PageLocatorOptions(HasText = "Sort:"))

            do! sortBtn.ClickAsync()
            let! afterFirst = sortBtn.TextContentAsync()
            Assert.That(afterFirst, Does.Contain("Sort: Recent"))

            do! sortBtn.ClickAsync()
            let! afterSecond = sortBtn.TextContentAsync()
            Assert.That(afterSecond, Does.Contain("Sort: A-Z"))
        }

    [<Test>]
    member this.``Compact mode toggle button is visible``() =
        task {
            let compactBtn =
                this.Page.Locator(".header-controls .ctrl-btn", PageLocatorOptions(HasText = "Compact"))

            do! Assertions.Expect(compactBtn).ToBeVisibleAsync()
        }

    [<Test>]
    member this.``Compact mode toggle switches to compact cards``() =
        task {
            let compactBtn =
                this.Page.Locator(".header-controls .ctrl-btn", PageLocatorOptions(HasText = "Compact"))

            do! compactBtn.ClickAsync()

            let compactCards = this.Page.Locator(".wt-card.compact")
            let! count = compactCards.CountAsync()
            Assert.That(count, Is.GreaterThanOrEqualTo(1))

            let! btnClass = compactBtn.GetAttributeAsync("class")
            Assert.That(btnClass, Does.Contain("active"))
        }

    [<Test>]
    member this.``Compact mode shows inline beads``() =
        task {
            let compactBtn =
                this.Page.Locator(".header-controls .ctrl-btn", PageLocatorOptions(HasText = "Compact"))

            do! compactBtn.ClickAsync()

            let beadsInline = this.Page.Locator(".wt-card.compact .beads-inline")
            let! count = beadsInline.CountAsync()
            Assert.That(count, Is.GreaterThanOrEqualTo(1))

            let! beadsText = beadsInline.First.TextContentAsync()
            Assert.That(beadsText, Does.Contain("O:"))
        }

    [<Test>]
    member this.``Compact mode toggle deactivates on second click``() =
        task {
            let compactBtn =
                this.Page.Locator(".header-controls .ctrl-btn", PageLocatorOptions(HasText = "Compact"))

            do! compactBtn.ClickAsync()
            do! compactBtn.ClickAsync()

            let normalCards = this.Page.Locator(".wt-card:not(.compact)")
            let! count = normalCards.CountAsync()
            Assert.That(count, Is.GreaterThanOrEqualTo(1))
        }

    [<Test>]
    member this.``Responsive layout shows single column at narrow viewport``() =
        task {
            do! this.Page.SetViewportSizeAsync(400, 800)
            do! this.Page.WaitForTimeoutAsync(200.0f)

            let grid = this.Page.Locator(".card-grid")
            let! gridStyle = grid.EvaluateAsync<string>("el => getComputedStyle(el).gridTemplateColumns")
            let columnCount = gridStyle.Split(' ') |> Array.length
            Assert.That(columnCount, Is.EqualTo(1))
        }

    [<Test>]
    member this.``Responsive layout shows two columns at medium viewport``() =
        task {
            do! this.Page.SetViewportSizeAsync(900, 800)
            do! this.Page.WaitForTimeoutAsync(200.0f)

            let grid = this.Page.Locator(".card-grid")
            let! gridStyle = grid.EvaluateAsync<string>("el => getComputedStyle(el).gridTemplateColumns")
            let columnCount = gridStyle.Split(' ') |> Array.length
            Assert.That(columnCount, Is.EqualTo(2))
        }

    [<Test>]
    member this.``Responsive layout shows three columns at wide viewport``() =
        task {
            do! this.Page.SetViewportSizeAsync(1300, 800)
            do! this.Page.WaitForTimeoutAsync(200.0f)

            let grid = this.Page.Locator(".card-grid")
            let! gridStyle = grid.EvaluateAsync<string>("el => getComputedStyle(el).gridTemplateColumns")
            let columnCount = gridStyle.Split(' ') |> Array.length
            Assert.That(columnCount, Is.EqualTo(3))
        }

    [<Test>]
    member this.``Responsive layout shows four columns at extra wide viewport``() =
        task {
            do! this.Page.SetViewportSizeAsync(1900, 800)
            do! this.Page.WaitForTimeoutAsync(200.0f)

            let grid = this.Page.Locator(".card-grid")
            let! gridStyle = grid.EvaluateAsync<string>("el => getComputedStyle(el).gridTemplateColumns")
            let columnCount = gridStyle.Split(' ') |> Array.length
            Assert.That(columnCount, Is.EqualTo(4))
        }

    [<Test>]
    member this.``Card header contains both CC dot and branch name``() =
        task {
            let header = this.Page.Locator(".wt-card .card-header").First

            let ccDot = header.Locator(".cc-dot")
            let! ccDotCount = ccDot.CountAsync()
            Assert.That(ccDotCount, Is.EqualTo(1))

            let branchName = header.Locator(".branch-name")
            let! branchCount = branchName.CountAsync()
            Assert.That(branchCount, Is.EqualTo(1))
        }

    [<Test>]
    member this.``Build badge renders when build data is present``() =
        task {
            let buildBadges = this.Page.Locator(".wt-card .build-badge")
            let! count = buildBadges.CountAsync()

            if count > 0 then
                let! badgeClass = buildBadges.First.GetAttributeAsync("class")
                Assert.That(
                    badgeClass,
                    Does.Contain("succeeded")
                        .Or.Contain("failed")
                        .Or.Contain("building")
                        .Or.Contain("partial")
                        .Or.Contain("canceled")
                )
        }

    [<Test>]
    member this.``Thread badge text matches N/M threads pattern``() =
        task {
            let threadBadges = this.Page.Locator(".wt-card .thread-badge")
            let! count = threadBadges.CountAsync()

            if count > 0 then
                let! text = threadBadges.First.TextContentAsync()
                Assert.That(text, Does.Match(@"\d+/\d+"))
        }

    [<Test>]
    member this.``Build badge is a clickable link to Azure DevOps``() =
        task {
            let buildLinks = this.Page.Locator(".wt-card a.build-badge")
            let! count = buildLinks.CountAsync()

            if count > 0 then
                let! href = buildLinks.First.GetAttributeAsync("href")
                Assert.That(href, Is.Not.Null.And.Not.Empty)
                Assert.That(href, Does.Contain("dev.azure.com"))
                Assert.That(href, Does.Contain("_build/results"))

                let! target = buildLinks.First.GetAttributeAsync("target")
                Assert.That(target, Is.EqualTo("_blank"))
        }

    [<Test>]
    member this.``No vote summary elements exist on cards``() =
        task {
            let voteSummaries = this.Page.Locator(".wt-card .vote-summary")
            let! count = voteSummaries.CountAsync()
            Assert.That(count, Is.EqualTo(0))
        }

    [<Test>]
    member this.``Merged PR badge has correct class when present``() =
        task {
            let mergedBadges = this.Page.Locator(".wt-card .pr-badge.merged")
            let! count = mergedBadges.CountAsync()

            if count > 0 then
                let! text = mergedBadges.First.TextContentAsync()
                Assert.That(text, Is.EqualTo("Merged"))

                let! tagName = mergedBadges.First.EvaluateAsync<string>("el => el.tagName.toLowerCase()")
                Assert.That(tagName, Is.EqualTo("a"))

                let! href = mergedBadges.First.GetAttributeAsync("href")
                Assert.That(href, Is.Not.Null.And.Not.Empty)
                Assert.That(href, Does.Contain("pullrequest"))
        }
