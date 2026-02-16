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

            let openSpan = beadsCounts.First.Locator(".beads-open")
            let! openCount = openSpan.CountAsync()
            Assert.That(openCount, Is.EqualTo(1))

            let inprogressSpan = beadsCounts.First.Locator(".beads-inprogress")
            let! inprogressCount = inprogressSpan.CountAsync()
            Assert.That(inprogressCount, Is.EqualTo(1))

            let closedSpan = beadsCounts.First.Locator(".beads-closed")
            let! closedCount = closedSpan.CountAsync()
            Assert.That(closedCount, Is.EqualTo(1))

            let sepSpans = beadsCounts.First.Locator(".beads-sep")
            let! sepCount = sepSpans.CountAsync()
            Assert.That(sepCount, Is.EqualTo(2))
        }

    [<Test>]
    member this.``Progress bar renders in beads row``() =
        task {
            let progressBars = this.Page.Locator(".wt-card .progress-bar")
            let! count = progressBars.CountAsync()
            Assert.That(count, Is.GreaterThanOrEqualTo(1))

            let segments = this.Page.Locator(".wt-card .progress-segment")
            let! segCount = segments.CountAsync()
            Assert.That(segCount, Is.GreaterThanOrEqualTo(3))
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

            let openSpan = beadsInline.First.Locator(".beads-open")
            let! openCount = openSpan.CountAsync()
            Assert.That(openCount, Is.EqualTo(1))
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

    [<Test>]
    member this.``Dark theme: body has dark background color``() =
        task {
            let! bgColor = this.Page.EvaluateAsync<string>("() => getComputedStyle(document.body).backgroundColor")
            Assert.That(bgColor, Is.EqualTo("rgb(30, 30, 46)"), "Body background should be #1e1e2e (Catppuccin Mocha base)")
        }

    [<Test>]
    member this.``Dark theme: cards have dark background``() =
        task {
            let card = this.Page.Locator(".wt-card").First
            let! cardBg = card.EvaluateAsync<string>("el => getComputedStyle(el).backgroundColor")
            Assert.That(cardBg, Is.EqualTo("rgb(42, 42, 60)"), "Card background should be #2a2a3c")
        }

    [<Test>]
    member this.``Dark theme: body text is light colored``() =
        task {
            let! textColor = this.Page.EvaluateAsync<string>("() => getComputedStyle(document.body).color")
            Assert.That(textColor, Is.EqualTo("rgb(205, 214, 244)"), "Body text should be #cdd6f4 (light on dark)")
        }

    [<Test>]
    member this.``Header contains folder name with accent styling``() =
        task {
            let folderAccent = this.Page.Locator("h1 .folder-accent")
            let! count = folderAccent.CountAsync()
            Assert.That(count, Is.EqualTo(1), "Header should contain a .folder-accent span")

            let! text = folderAccent.TextContentAsync()
            Assert.That(text, Is.Not.Empty, "Folder accent text should not be empty")
            Assert.That(text, Does.StartWith(":"), "Folder accent should start with colon separator")

            let! accentColor = folderAccent.EvaluateAsync<string>("el => getComputedStyle(el).color")
            Assert.That(accentColor, Is.EqualTo("rgb(137, 180, 250)"), "Folder accent color should be #89b4fa (blue)")
        }

    [<Test>]
    member this.``Beads counts use colored numbers not O/P/D prefix``() =
        task {
            let beadsCounts = this.Page.Locator(".wt-card .beads-counts").First

            let openSpan = beadsCounts.Locator(".beads-open")
            let! openText = openSpan.TextContentAsync()
            Assert.That(openText, Does.Match(@"^\d+$"), "Open count should be a plain number (no 'O:' prefix)")

            let! openColor = openSpan.EvaluateAsync<string>("el => getComputedStyle(el).color")
            Assert.That(openColor, Is.EqualTo("rgb(249, 226, 175)"), "Open count should be amber (#f9e2af)")

            let inprogressSpan = beadsCounts.Locator(".beads-inprogress")
            let! inprogressText = inprogressSpan.TextContentAsync()
            Assert.That(inprogressText, Does.Match(@"^\d+$"), "InProgress count should be a plain number (no 'P:' prefix)")

            let! inprogressColor = inprogressSpan.EvaluateAsync<string>("el => getComputedStyle(el).color")
            Assert.That(inprogressColor, Is.EqualTo("rgb(137, 180, 250)"), "InProgress count should be blue (#89b4fa)")

            let closedSpan = beadsCounts.Locator(".beads-closed")
            let! closedText = closedSpan.TextContentAsync()
            Assert.That(closedText, Does.Match(@"^\d+$"), "Closed count should be a plain number (no 'D:' prefix)")

            let! closedColor = closedSpan.EvaluateAsync<string>("el => getComputedStyle(el).color")
            Assert.That(closedColor, Is.EqualTo("rgb(166, 227, 161)"), "Closed count should be green (#a6e3a1)")
        }

    [<Test>]
    member this.``Progress bar has three colored segments``() =
        task {
            let progressBar = this.Page.Locator(".wt-card .progress-bar").First

            let segOpen = progressBar.Locator(".progress-segment.seg-open")
            let! openCount = segOpen.CountAsync()
            Assert.That(openCount, Is.EqualTo(1), "Progress bar should have a seg-open segment")

            let segInprogress = progressBar.Locator(".progress-segment.seg-inprogress")
            let! ipCount = segInprogress.CountAsync()
            Assert.That(ipCount, Is.EqualTo(1), "Progress bar should have a seg-inprogress segment")

            let segClosed = progressBar.Locator(".progress-segment.seg-closed")
            let! closedCount = segClosed.CountAsync()
            Assert.That(closedCount, Is.EqualTo(1), "Progress bar should have a seg-closed segment")

            let! openBg = segOpen.EvaluateAsync<string>("el => getComputedStyle(el).backgroundColor")
            Assert.That(openBg, Is.EqualTo("rgb(249, 226, 175)"), "seg-open background should be amber")

            let! ipBg = segInprogress.EvaluateAsync<string>("el => getComputedStyle(el).backgroundColor")
            Assert.That(ipBg, Is.EqualTo("rgb(137, 180, 250)"), "seg-inprogress background should be blue")

            let! closedBg = segClosed.EvaluateAsync<string>("el => getComputedStyle(el).backgroundColor")
            Assert.That(closedBg, Is.EqualTo("rgb(166, 227, 161)"), "seg-closed background should be green")
        }

    [<Test>]
    member this.``Main-behind indicator present on cards``() =
        task {
            let mainBehindElements = this.Page.Locator(".wt-card .main-behind")
            let! count = mainBehindElements.CountAsync()
            Assert.That(count, Is.GreaterThanOrEqualTo(1), "At least one card should have a main-behind indicator")

            let! text = mainBehindElements.First.TextContentAsync()
            Assert.That(
                text,
                Does.Contain("behind main").Or.EqualTo("up to date"),
                "Main-behind indicator should show 'N behind main' or 'up to date'"
            )
        }

    [<Test>]
    member this.``Main-behind up-to-date has correct CSS class``() =
        task {
            let upToDate = this.Page.Locator(".wt-card .main-behind.up-to-date")
            let! count = upToDate.CountAsync()

            if count > 0 then
                let! text = upToDate.First.TextContentAsync()
                Assert.That(text, Is.EqualTo("up to date"))

                let! color = upToDate.First.EvaluateAsync<string>("el => getComputedStyle(el).color")
                Assert.That(color, Is.EqualTo("rgb(127, 132, 156)"), "up-to-date should be muted gray (#7f849c)")
        }

    [<Test>]
    member this.``Main-behind warning style for high behind count``() =
        task {
            let behindWarning = this.Page.Locator(".wt-card .main-behind.behind-warning")
            let! count = behindWarning.CountAsync()

            if count > 0 then
                let! text = behindWarning.First.TextContentAsync()
                Assert.That(text, Does.Contain("behind main"))

                let! color = behindWarning.First.EvaluateAsync<string>("el => getComputedStyle(el).color")
                Assert.That(color, Is.EqualTo("rgb(243, 139, 168)"), "behind-warning should be red (#f38ba8)")

                let! fontWeight = behindWarning.First.EvaluateAsync<string>("el => getComputedStyle(el).fontWeight")
                Assert.That(fontWeight, Is.EqualTo("600").Or.EqualTo("bold"), "behind-warning should have bold font weight")
        }

    [<Test>]
    member this.``Compact mode shows main-behind indicator``() =
        task {
            let compactBtn =
                this.Page.Locator(".header-controls .ctrl-btn", PageLocatorOptions(HasText = "Compact"))

            do! compactBtn.ClickAsync()

            let mainBehind = this.Page.Locator(".wt-card.compact .main-behind")
            let! count = mainBehind.CountAsync()
            Assert.That(count, Is.GreaterThanOrEqualTo(1), "Compact cards should also show main-behind indicator")
        }

    [<Test>]
    member this.``Last commit message does not show merge commits``() =
        task {
            let commitLines = this.Page.Locator(".wt-card .commit-line")
            let! count = commitLines.CountAsync()
            Assert.That(count, Is.GreaterThanOrEqualTo(1))

            let mutable allNonMerge = true
            let mutable idx = 0
            let maxIdx = count - 1

            while idx <= maxIdx do
                let! text = commitLines.Nth(idx).TextContentAsync()
                if text.StartsWith("Merge branch") || text.StartsWith("Merge pull request") then
                    allNonMerge <- false
                idx <- idx + 1

            Assert.That(allNonMerge, Is.True, "Commit messages should not show merge commits (using --first-parent --no-merges)")
        }

    [<Test>]
    member this.``Beads separator uses muted color``() =
        task {
            let sep = this.Page.Locator(".wt-card .beads-counts .beads-sep").First
            let! color = sep.EvaluateAsync<string>("el => getComputedStyle(el).color")
            Assert.That(color, Is.EqualTo("rgb(88, 91, 112)"), "Separator should be muted (#585b70)")
        }

    [<Test>]
    member this.``Multiple build badges can appear on a single PR card``() =
        task {
            let prCards = this.Page.Locator(".wt-card:has(.pr-badge:not(.merged))")
            let! prCardCount = prCards.CountAsync()

            if prCardCount > 0 then
                let allBadges = prCards.First.Locator(".build-badge")
                let! badgeCount = allBadges.CountAsync()
                Assert.That(badgeCount, Is.GreaterThanOrEqualTo(1), "PR card should have at least one build badge")
        }

    [<Test>]
    member this.``All build badges are links to Azure DevOps build results``() =
        task {
            let buildLinks = this.Page.Locator(".wt-card a.build-badge")
            let! count = buildLinks.CountAsync()

            if count > 0 then
                let! allValid =
                    task {
                        let mutable valid = true
                        let mutable idx = 0

                        while idx < count do
                            let! href = buildLinks.Nth(idx).GetAttributeAsync("href")
                            if href = null || not (href.Contains("dev.azure.com")) || not (href.Contains("_build/results")) then
                                valid <- false
                            let! target = buildLinks.Nth(idx).GetAttributeAsync("target")
                            if target <> "_blank" then
                                valid <- false
                            idx <- idx + 1

                        return valid
                    }

                Assert.That(allValid, Is.True, "Every build badge link should point to dev.azure.com with _build/results path and target=_blank")
        }

    [<Test>]
    member this.``Build badges contain pipeline name text``() =
        task {
            let buildBadges = this.Page.Locator(".wt-card .build-badge")
            let! count = buildBadges.CountAsync()

            if count > 0 then
                let! allHaveText =
                    task {
                        let mutable valid = true
                        let mutable idx = 0

                        while idx < count do
                            let! text = buildBadges.Nth(idx).TextContentAsync()
                            if System.String.IsNullOrWhiteSpace(text) then
                                valid <- false
                            idx <- idx + 1

                        return valid
                    }

                Assert.That(allHaveText, Is.True, "Every build badge should have non-empty text content")
        }
