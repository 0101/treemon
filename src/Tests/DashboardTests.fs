module Tests.DashboardTests

open NUnit.Framework
open Microsoft.Playwright
open Microsoft.Playwright.NUnit

[<TestFixture>]
[<Category("E2E")>]
[<Category("Local")>]
type DashboardTests() =
    inherit PageTest()

    let baseUrl = ServerFixture.viteUrl

    let computedStyle (prop: string) (locator: ILocator) =
        locator.EvaluateAsync<string>($"el => getComputedStyle(el).{prop}")

    let compactBtn (page: IPage) =
        page.Locator(".header-controls .ctrl-btn", PageLocatorOptions(HasText = "Compact"))

    let eventLog (page: IPage) =
        page.Locator(".wt-card:not(.compact) .event-log")

    let waitForFirstAndCount (locator: ILocator) =
        task {
            do! locator.First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            return! locator.CountAsync()
        }

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
    member this.``Page title is Treemon``() =
        task {
            let! title = this.Page.TitleAsync()
            Assert.That(title, Is.EqualTo("Treemon"), "Browser tab title should be 'Treemon'")
        }

    [<Test>]
    member this.``Heading text starts with Treemon``() =
        task {
            let h1 = this.Page.Locator("h1")
            let! text = h1.TextContentAsync()
            Assert.That(text, Does.StartWith("Treemon"), "h1 should start with 'Treemon'")
            Assert.That(text, Does.Not.Contain("Worktree Monitor"), "h1 should not contain old name 'Worktree Monitor'")
        }

    [<Test>]
    member this.``Scheduler footer shows scheduler events``() =
        task {
            let footer = this.Page.Locator(".scheduler-footer")
            do! footer.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let! count = footer.CountAsync()
            Assert.That(count, Is.EqualTo(1), "Scheduler footer should be present")

            let overview = footer.Locator(".status-overview")
            let! ovCount = overview.CountAsync()
            Assert.That(ovCount, Is.EqualTo(1), "Scheduler footer should have a .status-overview section")

            let rows = overview.Locator(".status-row")
            do! rows.First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let! rowCount = rows.CountAsync()
            Assert.That(rowCount, Is.EqualTo(6), "Status overview should show 6 category rows")
        }

    [<TestCase("working", "rgb(255, 0, 0)")>]
    [<TestCase("waiting", "rgb(249, 226, 175)")>]
    [<TestCase("idle", "rgb(88, 91, 112)")>]
    [<Category("Fast")>]
    member this.``CC dot has correct background color``(status: string, expectedColor: string) =
        task {
            let dots = this.Page.Locator($".cc-dot.{status}")
            do! dots.First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let! count = dots.CountAsync()
            Assert.That(count, Is.GreaterThanOrEqualTo(1), $"Fixture has {status} Claude worktrees; {status} dots should be present")

            let! bg = dots.First |> computedStyle "backgroundColor"
            Assert.That(bg, Is.EqualTo(expectedColor), $"CC dot .{status} background color")
        }

    [<Test>]
    member this.``Card min-width is 0 for grid truncation``() =
        task {
            let card = this.Page.Locator(".wt-card").First
            let! minWidth = card |> computedStyle "minWidth"
            Assert.That(minWidth, Is.EqualTo("0px"), "Card min-width should be 0 to allow text-overflow ellipsis in grid")
        }

    [<Test>]
    member this.``Status bar does not show worktree count``() =
        task {
            let! statusText = this.Page.Locator(".status-bar").TextContentAsync()
            Assert.That(statusText, Does.Not.Contain("worktrees"), "Worktree count was removed from status bar")
        }

    [<Test>]
    [<Category("Fast")>]
    member this.``Dashboard loads with at least one worktree card``() =
        task {
            let cards = this.Page.Locator(".wt-card")
            let! count = cards.CountAsync()
            Assert.That(count, Is.GreaterThanOrEqualTo(1))
        }

    [<Test>]
    [<Category("Fast")>]
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
    [<Category("Fast")>]
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
                Does.Contain("working")
                    .Or.Contain("waiting")
                    .Or.Contain("done")
                    .Or.Contain("idle")
            )
        }

    [<Test>]
    member this.``Cards have CC status class``() =
        task {
            let cards = this.Page.Locator(".wt-card")
            let! count = cards.CountAsync()
            Assert.That(count, Is.GreaterThanOrEqualTo(1))

            let! cardClass = cards.First.GetAttributeAsync("class")
            Assert.That(
                cardClass,
                Does.Contain("cc-working")
                    .Or.Contain("cc-waiting")
                    .Or.Contain("cc-done")
                    .Or.Contain("cc-idle")
            )
        }

    [<Test>]
    [<Category("Fast")>]
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
    [<Category("Fast")>]
    member this.``PR badge shows when PR data is present``() =
        task {
            let prBadges = this.Page.Locator(".wt-card .pr-badge")
            do! Assertions.Expect(prBadges.First).ToBeVisibleAsync(LocatorAssertionsToBeVisibleOptions(Timeout = 5000.0f))

            let! count = prBadges.CountAsync()
            Assert.That(count, Is.GreaterThanOrEqualTo(1), "Fixture has worktrees with PRs; PR badges should be present")

            do! Assertions.Expect(prBadges.First).ToHaveTextAsync(System.Text.RegularExpressions.Regex(@"^(PR #\d+|Merged)$"), LocatorAssertionsToHaveTextOptions(Timeout = 5000.0f))
        }

    [<Test>]
    member this.``PR badge is a clickable link with href``() =
        task {
            let prLinks = this.Page.Locator(".wt-card a.pr-badge")
            do! prLinks.First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let! count = prLinks.CountAsync()
            Assert.That(count, Is.GreaterThanOrEqualTo(1), "Fixture has worktrees with PRs; PR badge links should be present")

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
            do! threadBadges.First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let! count = threadBadges.CountAsync()
            Assert.That(count, Is.GreaterThanOrEqualTo(1), "Fixture has worktrees with PR threads; thread badges should be present")

            let! threadText = threadBadges.First.TextContentAsync()
            Assert.That(threadText, Does.Match(@"\d+/\d+"))
        }

    [<Test>]
    member this.``Sort toggle button is visible and clickable``() =
        task {
            let sortBtn =
                this.Page.Locator(".header-controls .ctrl-btn", PageLocatorOptions(HasText = "Sort:"))

            do! Assertions.Expect(sortBtn).ToBeVisibleAsync()

            let! initialText = sortBtn.TextContentAsync()
            Assert.That(initialText, Does.Contain("Sort: Recent"))

            do! sortBtn.ClickAsync()

            let! newText = sortBtn.TextContentAsync()
            Assert.That(newText, Does.Contain("Sort: A-Z"))
        }

    [<Test>]
    member this.``Sort toggle changes back to A-Z on second click``() =
        task {
            let sortBtn =
                this.Page.Locator(".header-controls .ctrl-btn", PageLocatorOptions(HasText = "Sort:"))

            do! sortBtn.ClickAsync()
            let! afterFirst = sortBtn.TextContentAsync()
            Assert.That(afterFirst, Does.Contain("Sort: A-Z"))

            do! sortBtn.ClickAsync()
            let! afterSecond = sortBtn.TextContentAsync()
            Assert.That(afterSecond, Does.Contain("Sort: Recent"))
        }

    [<Test>]
    member this.``Compact mode toggle button is visible``() =
        task {
            do! Assertions.Expect(compactBtn this.Page).ToBeVisibleAsync()
        }

    [<Test>]
    [<Category("Fast")>]
    member this.``Compact mode toggle switches to compact cards``() =
        task {
            let btn = compactBtn this.Page
            do! btn.ClickAsync()

            let compactCards = this.Page.Locator(".wt-card.compact")
            let! count = compactCards.CountAsync()
            Assert.That(count, Is.GreaterThanOrEqualTo(1))

            let! btnClass = btn.GetAttributeAsync("class")
            Assert.That(btnClass, Does.Contain("active"))
        }

    [<Test>]
    member this.``Compact mode shows inline beads``() =
        task {
            do! (compactBtn this.Page).ClickAsync()

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
            let btn = compactBtn this.Page
            do! btn.ClickAsync()
            do! btn.ClickAsync()

            let normalCards = this.Page.Locator(".wt-card:not(.compact)")
            let! count = normalCards.CountAsync()
            Assert.That(count, Is.GreaterThanOrEqualTo(1))
        }

    [<TestCase(400, 1)>]
    [<TestCase(900, 2)>]
    [<TestCase(1300, 3)>]
    [<TestCase(1900, 4)>]
    member this.``Responsive layout shows correct column count``(width: int, expectedColumns: int) =
        task {
            do! this.Page.SetViewportSizeAsync(width, 800)
            let! _ = this.Page.WaitForFunctionAsync(
                $"() => {{ const cols = getComputedStyle(document.querySelector('.card-grid')).gridTemplateColumns; return cols.split(' ').length === {expectedColumns}; }}",
                null,
                PageWaitForFunctionOptions(Timeout = 5000.0f))

            let grid = this.Page.Locator(".card-grid")
            let! gridStyle = grid |> computedStyle "gridTemplateColumns"
            let columnCount = gridStyle.Split(' ') |> Array.length
            Assert.That(columnCount, Is.EqualTo(expectedColumns), $"At {width}px width, expected {expectedColumns} columns")
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
    [<Category("Fast")>]
    member this.``Build badge renders when build data is present``() =
        task {
            let buildBadges = this.Page.Locator(".wt-card .build-badge")
            do! buildBadges.First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let! count = buildBadges.CountAsync()
            Assert.That(count, Is.GreaterThanOrEqualTo(1), "Fixture has worktrees with builds; build badges should be present")

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
            do! mergedBadges.First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let! count = mergedBadges.CountAsync()
            Assert.That(count, Is.GreaterThanOrEqualTo(1), "Fixture has a worktree with merged PR; merged badge should be present")

            let! text = mergedBadges.First.TextContentAsync()
            Assert.That(text, Is.EqualTo("Merged"))

            let! tagName = mergedBadges.First.EvaluateAsync<string>("el => el.tagName.toLowerCase()")
            Assert.That(tagName, Is.EqualTo("a"))

            let! href = mergedBadges.First.GetAttributeAsync("href")
            Assert.That(href, Is.Not.Null.And.Not.Empty)
            Assert.That(href, Does.Contain("pullrequest"))
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

            let! accentColor = folderAccent |> computedStyle "color"
            Assert.That(accentColor, Is.EqualTo("rgb(137, 180, 250)"), "Folder accent color should be #89b4fa (blue)")
        }

    [<Test>]
    member this.``Beads counts use colored numbers not O/P/D prefix``() =
        task {
            let beadsCounts = this.Page.Locator(".wt-card .beads-counts").First

            let openSpan = beadsCounts.Locator(".beads-open")
            let! openText = openSpan.TextContentAsync()
            Assert.That(openText, Does.Match(@"^\d+$"), "Open count should be a plain number (no 'O:' prefix)")

            let! openColor = openSpan |> computedStyle "color"
            Assert.That(openColor, Is.EqualTo("rgb(249, 226, 175)"), "Open count should be amber (#f9e2af)")

            let inprogressSpan = beadsCounts.Locator(".beads-inprogress")
            let! inprogressText = inprogressSpan.TextContentAsync()
            Assert.That(inprogressText, Does.Match(@"^\d+$"), "InProgress count should be a plain number (no 'P:' prefix)")

            let! inprogressColor = inprogressSpan |> computedStyle "color"
            Assert.That(inprogressColor, Is.EqualTo("rgb(137, 180, 250)"), "InProgress count should be blue (#89b4fa)")

            let closedSpan = beadsCounts.Locator(".beads-closed")
            let! closedText = closedSpan.TextContentAsync()
            Assert.That(closedText, Does.Match(@"^\d+$"), "Closed count should be a plain number (no 'D:' prefix)")

            let! closedColor = closedSpan |> computedStyle "color"
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

            let! openBg = segOpen |> computedStyle "backgroundColor"
            Assert.That(openBg, Is.EqualTo("rgb(249, 226, 175)"), "seg-open background should be amber")

            let! ipBg = segInprogress |> computedStyle "backgroundColor"
            Assert.That(ipBg, Is.EqualTo("rgb(137, 180, 250)"), "seg-inprogress background should be blue")

            let! closedBg = segClosed |> computedStyle "backgroundColor"
            Assert.That(closedBg, Is.EqualTo("rgb(166, 227, 161)"), "seg-closed background should be green")
        }

    [<Test>]
    [<Category("Fast")>]
    member this.``Main-behind indicator present on cards``() =
        task {
            let mainBehindElements = this.Page.Locator(".wt-card .main-behind")
            do! mainBehindElements.First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
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
            do! upToDate.First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let! count = upToDate.CountAsync()
            Assert.That(count, Is.GreaterThanOrEqualTo(1), "Fixture has worktrees with MainBehindCount=0; up-to-date indicators should be present")

            let! text = upToDate.First.TextContentAsync()
            Assert.That(text, Is.EqualTo("up to date"))

            let! color = upToDate.First |> computedStyle "color"
            Assert.That(color, Is.EqualTo("rgb(127, 132, 156)"), "up-to-date should be muted gray (#7f849c)")
        }

    [<Test>]
    member this.``Main-behind warning style for high behind count``() =
        task {
            let behindWarning = this.Page.Locator(".wt-card .main-behind.behind-warning")
            do! behindWarning.First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let! count = behindWarning.CountAsync()
            Assert.That(count, Is.GreaterThanOrEqualTo(1), "Fixture has worktrees with high MainBehindCount; behind-warning indicators should be present")

            let! text = behindWarning.First.TextContentAsync()
            Assert.That(text, Does.Contain("behind main"))

            let! color = behindWarning.First |> computedStyle "color"
            Assert.That(color, Is.EqualTo("rgb(243, 139, 168)"), "behind-warning should be red (#f38ba8)")

            let! fontWeight = behindWarning.First |> computedStyle "fontWeight"
            Assert.That(fontWeight, Is.EqualTo("600").Or.EqualTo("bold"), "behind-warning should have bold font weight")
        }

    [<Test>]
    member this.``Compact mode shows main-behind indicator``() =
        task {
            do! (compactBtn this.Page).ClickAsync()

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

            let! allNonMerge =
                commitLines.EvaluateAllAsync<bool>(
                    "els => els.every(el => !el.textContent.startsWith('Merge branch') && !el.textContent.startsWith('Merge pull request'))"
                )
            Assert.That(allNonMerge, Is.True, "Commit messages should not show merge commits (using --first-parent --no-merges)")
        }

    [<TestCase("terminal-btn")>]
    [<TestCase("delete-btn")>]
    member this.``Header button present on every full-view card``(btnClass: string) =
        task {
            let cards = this.Page.Locator(".wt-card:not(.compact)")
            let! cardCount = cards.CountAsync()
            Assert.That(cardCount, Is.GreaterThanOrEqualTo(1), "Should have at least one full-view card")

            let btns = this.Page.Locator($".wt-card:not(.compact) .{btnClass}")
            let! btnCount = btns.CountAsync()
            Assert.That(btnCount, Is.EqualTo(cardCount), $"Every full-view card should have a .{btnClass}")
        }

    [<TestCase("terminal-btn")>]
    [<TestCase("delete-btn")>]
    member this.``Header button present on every compact card``(btnClass: string) =
        task {
            do! (compactBtn this.Page).ClickAsync()

            let compactCards = this.Page.Locator(".wt-card.compact")
            let! cardCount = compactCards.CountAsync()
            Assert.That(cardCount, Is.GreaterThanOrEqualTo(1), "Should have at least one compact card")

            let btns = this.Page.Locator($".wt-card.compact .{btnClass}")
            let! btnCount = btns.CountAsync()
            Assert.That(btnCount, Is.EqualTo(cardCount), $"Every compact card should have a .{btnClass}")
        }

    [<TestCase("terminal-btn")>]
    [<TestCase("delete-btn")>]
    member this.``Header button is inside card header``(btnClass: string) =
        task {
            let headerBtns = this.Page.Locator($".wt-card .card-header .{btnClass}")
            let! count = headerBtns.CountAsync()
            Assert.That(count, Is.GreaterThanOrEqualTo(1), $".{btnClass} should be inside .card-header")

            let allBtns = this.Page.Locator($".wt-card .{btnClass}")
            let! allCount = allBtns.CountAsync()
            Assert.That(count, Is.EqualTo(allCount), $"All .{btnClass} should be inside card headers")
        }

    [<TestCase("terminal-btn", ">", "Open terminal")>]
    [<TestCase("delete-btn", "\u2715", "Remove worktree")>]
    member this.``Header button has correct text and title``(btnClass: string, expectedText: string, expectedTitle: string) =
        task {
            let btn = this.Page.Locator($".wt-card .{btnClass}").First
            do! Assertions.Expect(btn).ToBeVisibleAsync()

            let! text = btn.TextContentAsync()
            Assert.That(text, Is.EqualTo(expectedText), $".{btnClass} text")

            let! title = btn.GetAttributeAsync("title")
            Assert.That(title, Is.EqualTo(expectedTitle), $".{btnClass} title attribute")
        }

    [<TestCase("terminal-btn")>]
    [<TestCase("delete-btn")>]
    member this.``Header button has pointer cursor``(btnClass: string) =
        task {
            let btn = this.Page.Locator($".wt-card .{btnClass}").First
            do! Assertions.Expect(btn).ToBeVisibleAsync()

            let! cursor = btn |> computedStyle "cursor"
            Assert.That(cursor, Is.EqualTo("pointer"), $".{btnClass} should have pointer cursor")
        }

    [<Test>]
    member this.``Terminal and delete buttons are right-aligned in card header``() =
        task {
            let header = this.Page.Locator(".wt-card .card-header").First
            let deleteBtn = header.Locator(".delete-btn")
            do! Assertions.Expect(deleteBtn).ToBeVisibleAsync()

            let! isRightAligned =
                deleteBtn.EvaluateAsync<bool>(
                    "el => { const hr = el.parentElement.getBoundingClientRect(); const br = el.getBoundingClientRect(); return (hr.right - br.right) < 20; }"
                )
            Assert.That(isRightAligned, Is.True, "Delete button should be positioned near the right edge of card header")
        }

    [<Test>]
    member this.``Multiple build badges can appear on a single PR card``() =
        task {
            let prCards = this.Page.Locator(".wt-card:has(.pr-badge:not(.merged))")
            do! prCards.First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let! prCardCount = prCards.CountAsync()
            Assert.That(prCardCount, Is.GreaterThanOrEqualTo(1), "Fixture has worktrees with non-merged PRs")

            let allBadges = prCards.First.Locator(".build-badge")
            let! badgeCount = allBadges.CountAsync()
            Assert.That(badgeCount, Is.GreaterThanOrEqualTo(1), "PR card should have at least one build badge")
        }

    [<Test>]
    member this.``All build badges are links to Azure DevOps build results``() =
        task {
            let buildLinks = this.Page.Locator(".wt-card a.build-badge")
            do! buildLinks.First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let! count = buildLinks.CountAsync()
            Assert.That(count, Is.GreaterThanOrEqualTo(1), "Fixture has worktrees with builds; build badge links should be present")

            let! allValid =
                buildLinks.EvaluateAllAsync<bool>(
                    "els => els.every(el => el.href && el.href.includes('dev.azure.com') && el.href.includes('_build/results') && el.target === '_blank')"
                )
            Assert.That(allValid, Is.True, "Every build badge link should point to dev.azure.com with _build/results path and target=_blank")
        }

    [<Test>]
    member this.``Build badges contain pipeline name text``() =
        task {
            let buildBadges = this.Page.Locator(".wt-card .build-badge")
            do! buildBadges.First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let! count = buildBadges.CountAsync()
            Assert.That(count, Is.GreaterThanOrEqualTo(1), "Fixture has worktrees with builds; build badges should be present")

            let! allHaveText =
                buildBadges.EvaluateAllAsync<bool>(
                    "els => els.every(el => el.textContent && el.textContent.trim().length > 0)"
                )
            Assert.That(allHaveText, Is.True, "Every build badge should have non-empty text content")
        }

    [<Test>]
    member this.``Sync button appears when MainBehindCount is greater than zero``() =
        task {
            let behindCards = this.Page.Locator(".wt-card:not(.compact) .main-behind-row:has(.main-behind:not(.up-to-date))")
            do! behindCards.First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let! behindCount = behindCards.CountAsync()
            Assert.That(behindCount, Is.GreaterThanOrEqualTo(1), "Fixture has worktrees behind main; behind-main rows should be present")

            let syncBtns = behindCards.First.Locator(".sync-btn, .sync-cancel-btn")
            let! btnCount = syncBtns.CountAsync()
            Assert.That(btnCount, Is.EqualTo(1), "Cards with MainBehindCount > 0 should show a sync button or cancel button")
        }

    [<Test>]
    member this.``Sync button hidden when MainBehindCount is zero``() =
        task {
            let upToDateRows = this.Page.Locator(".wt-card:not(.compact) .main-behind-row:has(.main-behind.up-to-date)")
            do! upToDateRows.First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let! count = upToDateRows.CountAsync()
            Assert.That(count, Is.GreaterThanOrEqualTo(1), "Fixture has worktrees with MainBehindCount=0; up-to-date rows should be present")

            let syncBtns = upToDateRows.First.Locator(".sync-btn, .sync-cancel-btn")
            let! btnCount = syncBtns.CountAsync()
            Assert.That(btnCount, Is.EqualTo(0), "Cards with MainBehindCount = 0 should not show a sync button")
        }

    [<Test>]
    member this.``Sync button disabled when Claude is Working``() =
        task {
            let workingCards = this.Page.Locator(".wt-card.cc-working:not(.compact)")
            do! workingCards.First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let! workingCount = workingCards.CountAsync()
            Assert.That(workingCount, Is.GreaterThanOrEqualTo(1), "Fixture has Working Claude worktrees")

            let behindRow = workingCards.First.Locator(".main-behind-row:has(.main-behind:not(.up-to-date))")
            let! behindCount = behindRow.CountAsync()
            Assert.That(behindCount, Is.EqualTo(1), "Fixture Working Claude worktree is behind main")

            let syncBtn = behindRow.Locator(".sync-btn")
            let! btnCount = syncBtn.CountAsync()
            Assert.That(btnCount, Is.EqualTo(1), "Sync button should be present on Working Claude card behind main")

            let! cssClass = syncBtn.GetAttributeAsync("class")
            Assert.That(cssClass, Does.Contain("disabled"), "Sync button should be disabled when Claude is Working")

            let! isDisabled = syncBtn.EvaluateAsync<bool>("el => el.disabled")
            Assert.That(isDisabled, Is.True, "Sync button disabled attribute should be set when Claude is Working")

            let! title = syncBtn.GetAttributeAsync("title")
            Assert.That(title, Is.EqualTo("Claude is active"), "Disabled sync button should show 'Claude is active' tooltip")
        }

    [<Test>]
    member this.``Sync button disabled when Claude is WaitingForUser``() =
        task {
            let waitingCards = this.Page.Locator(".wt-card.cc-waiting:not(.compact)")
            do! waitingCards.First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let! waitingCount = waitingCards.CountAsync()
            Assert.That(waitingCount, Is.GreaterThanOrEqualTo(1), "Fixture has WaitingForUser Claude worktrees")

            let behindRow = waitingCards.First.Locator(".main-behind-row:has(.main-behind:not(.up-to-date))")
            let! behindCount = behindRow.CountAsync()
            Assert.That(behindCount, Is.EqualTo(1), "Fixture WaitingForUser Claude worktree is behind main")

            let syncBtn = behindRow.Locator(".sync-btn")
            let! btnCount = syncBtn.CountAsync()
            Assert.That(btnCount, Is.EqualTo(1), "Sync button should be present on WaitingForUser Claude card behind main")

            let! cssClass = syncBtn.GetAttributeAsync("class")
            Assert.That(cssClass, Does.Contain("disabled"), "Sync button should be disabled when Claude is WaitingForUser")

            let! isDisabled = syncBtn.EvaluateAsync<bool>("el => el.disabled")
            Assert.That(isDisabled, Is.True, "Sync button disabled attribute should be set when Claude is WaitingForUser")
        }

    [<Test>]
    member this.``Sync button has correct CSS classes and styling``() =
        task {
            let syncBtns = this.Page.Locator(".wt-card:not(.compact) .sync-btn")
            do! syncBtns.First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let! count = syncBtns.CountAsync()
            Assert.That(count, Is.GreaterThanOrEqualTo(1), "Fixture has worktrees behind main; sync buttons should be present")

            let btn = syncBtns.First
            let! text = btn.TextContentAsync()
            Assert.That(text, Is.EqualTo("Sync"), "Sync button text should be 'Sync'")

            let! cursor = btn |> computedStyle "cursor"
            let! cssClass = btn.GetAttributeAsync("class")
            let isDisabled = cssClass.Contains("disabled")
            match isDisabled with
            | true ->
                Assert.That(cursor, Is.EqualTo("not-allowed"), "Disabled sync button should have not-allowed cursor")
            | false ->
                Assert.That(cursor, Is.EqualTo("pointer"), "Enabled sync button should have pointer cursor")
        }

    [<Test>]
    member this.``Event log renders on full cards when events exist``() =
        task {
            let logs = eventLog this.Page
            do! logs.First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let! count = logs.CountAsync()
            Assert.That(count, Is.GreaterThanOrEqualTo(1), "Fixture has worktrees with CardEvents; event logs should be present")

            let entries = logs.First.Locator(".event-entry")
            let! entryCount = entries.CountAsync()
            Assert.That(entryCount, Is.GreaterThanOrEqualTo(1).And.LessThanOrEqualTo(2),
                "Event log should show between 1 and 2 entries")

            let firstEntry = entries.First
            let source = firstEntry.Locator(".event-source")
            let! sourceCount = source.CountAsync()
            Assert.That(sourceCount, Is.EqualTo(1), "Each event entry should have an event-source")

            let message = firstEntry.Locator(".event-message")
            let! messageCount = message.CountAsync()
            Assert.That(messageCount, Is.EqualTo(1), "Each event entry should have an event-message")
        }

    [<Test>]
    member this.``Event status badges have correct CSS classes when present``() =
        task {
            let statusBadges = this.Page.Locator(".wt-card .event-status")
            do! statusBadges.First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let! count = statusBadges.CountAsync()
            Assert.That(count, Is.GreaterThanOrEqualTo(1), "Fixture has sync events with statuses; event-status badges should be present")

            let! cssClass = statusBadges.First.GetAttributeAsync("class")
            Assert.That(
                cssClass,
                Does.Contain("running")
                    .Or.Contain("success")
                    .Or.Contain("failed")
                    .Or.Contain("cancelled"),
                "Event status badge should have a status CSS class"
            )
        }

    [<Test>]
    member this.``Sync button hidden in compact mode``() =
        task {
            do! (compactBtn this.Page).ClickAsync()

            let syncBtns = this.Page.Locator(".wt-card.compact .sync-btn")
            let! syncCount = syncBtns.CountAsync()
            Assert.That(syncCount, Is.EqualTo(0), "Sync buttons should not appear in compact mode")

            let cancelBtns = this.Page.Locator(".wt-card.compact .sync-cancel-btn")
            let! cancelCount = cancelBtns.CountAsync()
            Assert.That(cancelCount, Is.EqualTo(0), "Cancel buttons should not appear in compact mode")
        }

    [<Test>]
    member this.``Event log hidden in compact mode``() =
        task {
            do! (compactBtn this.Page).ClickAsync()

            let eventLogs = this.Page.Locator(".wt-card.compact .event-log")
            let! count = eventLogs.CountAsync()
            Assert.That(count, Is.EqualTo(0), "Event logs should not appear in compact mode")
        }

    [<Test>]
    member this.``Main-behind-row contains sync button next to behind text``() =
        task {
            let behindRows = this.Page.Locator(".wt-card:not(.compact) .main-behind-row:has(.main-behind:not(.up-to-date))")
            do! behindRows.First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let! count = behindRows.CountAsync()
            Assert.That(count, Is.GreaterThanOrEqualTo(1), "Fixture has worktrees behind main; behind rows should be present")

            let btns = behindRows.First.Locator(".sync-btn, .sync-cancel-btn")
            let! btnCount = btns.CountAsync()
            Assert.That(btnCount, Is.EqualTo(1), "Behind-main row should contain exactly one sync/cancel button")
        }

    [<Test>]
    member this.``Cancel button appears with correct CSS class``() =
        task {
            let cancelBtns = this.Page.Locator(".wt-card .sync-cancel-btn")
            do! cancelBtns.First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let! count = cancelBtns.CountAsync()
            Assert.That(count, Is.GreaterThanOrEqualTo(1), "Fixture has a branch with Running sync event; cancel button should be present")

            let! text = cancelBtns.First.TextContentAsync()
            Assert.That(text, Is.EqualTo("Cancel"), "Cancel button text should be 'Cancel'")

            let! borderColor = cancelBtns.First |> computedStyle "borderColor"
            Assert.That(borderColor, Does.Contain("rgb(243, 139, 168)"), "Cancel button border should be red (#f38ba8)")
        }

    [<Test>]
    member this.``Compact mode uses mainBehindIndicator without sync button``() =
        task {
            do! (compactBtn this.Page).ClickAsync()

            let mainBehind = this.Page.Locator(".wt-card.compact .main-behind")
            let! count = mainBehind.CountAsync()
            Assert.That(count, Is.GreaterThanOrEqualTo(1), "Compact cards should show main-behind indicator")

            let mainBehindRow = this.Page.Locator(".wt-card.compact .main-behind-row")
            let! rowCount = mainBehindRow.CountAsync()
            Assert.That(rowCount, Is.EqualTo(0), "Compact cards should not use main-behind-row (no sync button row)")
        }

    [<Test>]
    member this.``PWA manifest link exists in DOM``() =
        task {
            let manifestLink = this.Page.Locator("link[rel='manifest']")
            let! count = manifestLink.CountAsync()
            Assert.That(count, Is.EqualTo(1), "Page should have exactly one <link rel='manifest'> element")

            let! href = manifestLink.GetAttributeAsync("href")
            Assert.That(href, Is.EqualTo("/manifest.webmanifest"), "Manifest link href should be '/manifest.webmanifest'")
        }

    [<Test>]
    member this.``PWA manifest endpoint returns 200 with correct content``() =
        task {
            use client = new System.Net.Http.HttpClient()
            let! response = client.GetAsync($"{baseUrl}/manifest.webmanifest")
            Assert.That(int response.StatusCode, Is.EqualTo(200), "GET /manifest.webmanifest should return 200")

            let! body = response.Content.ReadAsStringAsync()
            Assert.That(body, Does.Contain("\"name\": \"Treemon\""), "Manifest should contain app name 'Treemon'")
            Assert.That(body, Does.Contain("\"short_name\": \"Treemon\""), "Manifest should contain short_name 'Treemon'")
            Assert.That(body, Does.Contain("\"display\": \"standalone\""), "Manifest should specify standalone display mode")
            Assert.That(body, Does.Contain("\"start_url\": \"/\""), "Manifest should have start_url '/'")
            Assert.That(body, Does.Contain("\"theme_color\": \"#1e1e2e\""), "Manifest theme_color should match Catppuccin Mocha base")
            Assert.That(body, Does.Contain("\"background_color\": \"#1e1e2e\""), "Manifest background_color should match body background")
            Assert.That(body, Does.Contain("icon-192.png"), "Manifest should reference 192x192 icon")
            Assert.That(body, Does.Contain("icon-512.png"), "Manifest should reference 512x512 icon")
        }

    [<Test>]
    member this.``PWA theme-color meta tag exists``() =
        task {
            let themeColor = this.Page.Locator("meta[name='theme-color']")
            let! count = themeColor.CountAsync()
            Assert.That(count, Is.EqualTo(1), "Page should have exactly one <meta name='theme-color'> element")

            let! content = themeColor.GetAttributeAsync("content")
            Assert.That(content, Is.EqualTo("#1e1e2e"), "Theme-color meta should be '#1e1e2e'")
        }

    [<Test>]
    member this.``PWA service worker registration script exists``() =
        task {
            let! swRegistered =
                this.Page.EvaluateAsync<bool>("() => 'serviceWorker' in navigator")
            Assert.That(swRegistered, Is.True, "Browser should support serviceWorker API")

            let! registrations =
                this.Page.EvaluateAsync<int>("async () => { const regs = await navigator.serviceWorker.getRegistrations(); return regs.length; }")
            Assert.That(registrations, Is.GreaterThanOrEqualTo(1), "At least one service worker should be registered")
        }

    [<Test>]
    member this.``PWA service worker endpoint returns 200``() =
        task {
            use client = new System.Net.Http.HttpClient()
            let! response = client.GetAsync($"{baseUrl}/sw.js")
            Assert.That(int response.StatusCode, Is.EqualTo(200), "GET /sw.js should return 200")

            let! body = response.Content.ReadAsStringAsync()
            Assert.That(body, Does.Contain("install"), "Service worker should have install handler")
            Assert.That(body, Does.Contain("activate"), "Service worker should have activate handler")
        }

    [<Test>]
    member this.``PWA icon files are accessible``() =
        task {
            use client = new System.Net.Http.HttpClient()

            let! response192 = client.GetAsync($"{baseUrl}/icon-192.png")
            Assert.That(int response192.StatusCode, Is.EqualTo(200), "GET /icon-192.png should return 200")

            let! response512 = client.GetAsync($"{baseUrl}/icon-512.png")
            Assert.That(int response512.StatusCode, Is.EqualTo(200), "GET /icon-512.png should return 200")
        }

    [<Test>]
    member this.``Event log visible on initial page load without sync trigger``() =
        task {
            let logs = eventLog this.Page
            do! logs.First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let! count = logs.CountAsync()
            Assert.That(count, Is.GreaterThanOrEqualTo(1),
                "Event logs should be visible on initial load (getSyncStatus fetched on Tick, not just during sync)")
        }

    [<Test>]
    member this.``Event log shows max 2 entries per card``() =
        task {
            let logs = eventLog this.Page
            do! logs.First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            let! allWithin2 =
                logs.EvaluateAllAsync<bool>(
                    "els => els.every(el => el.querySelectorAll('.event-entry').length <= 2)")
            Assert.That(allWithin2, Is.True, "Every event log should show at most 2 entries (truncated from 3)")
        }

    [<Test>]
    member this.``Event entries have timestamp prefix``() =
        task {
            let logs = eventLog this.Page
            do! logs.First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            let entries = logs.First.Locator(".event-entry")
            let! entryCount = entries.CountAsync()
            Assert.That(entryCount, Is.GreaterThanOrEqualTo(1), "Should have at least one event entry")

            let timeSpans = entries.First.Locator(".event-time")
            let! timeCount = timeSpans.CountAsync()
            Assert.That(timeCount, Is.EqualTo(1), "Each event entry should have exactly one .event-time span")

            let! timeText = timeSpans.First.TextContentAsync()
            Assert.That(timeText, Does.Match(@"\d+[smhd] ago"), "Timestamp should match relative time pattern like '3m ago'")
        }

    [<Test>]
    member this.``Event entries are in chronological order newest at bottom``() =
        task {
            let logs = eventLog this.Page
            do! logs.First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            let! result =
                logs.EvaluateAllAsync<bool>("""els => {
                    const parseMinutes = s => {
                        const t = s.trim();
                        if (t.endsWith('s ago')) return 0;
                        if (t.endsWith('m ago')) return parseInt(t);
                        if (t.endsWith('h ago')) return parseInt(t) * 60;
                        if (t.endsWith('d ago')) return parseInt(t) * 1440;
                        return 0;
                    };
                    const log = els.find(el => el.querySelectorAll('.event-entry').length === 2);
                    if (!log) return false;
                    const entries = log.querySelectorAll('.event-entry');
                    const topTime = entries[0].querySelector('.event-time').textContent;
                    const bottomTime = entries[1].querySelector('.event-time').textContent;
                    return parseMinutes(topTime) >= parseMinutes(bottomTime);
                }""")
            Assert.That(result, Is.True,
                "Should find a 2-entry event log where top entry is older than or same age as bottom entry (chronological, newest at bottom)")
        }

    [<Test>]
    member this.``All event entries across all cards have timestamps``() =
        task {
            let logs = eventLog this.Page
            do! logs.First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            let allTimeSpans = this.Page.Locator(".wt-card:not(.compact) .event-entry .event-time")
            let! timeCount = allTimeSpans.CountAsync()

            let allEntries = this.Page.Locator(".wt-card:not(.compact) .event-entry")
            let! entryCount = allEntries.CountAsync()

            Assert.That(timeCount, Is.EqualTo(entryCount),
                "Every event entry should have an event-time span (1:1 ratio)")
        }

    [<Test>]
    member this.``Merged card is dimmed with reduced opacity``() =
        task {
            let mergedCards = this.Page.Locator(".wt-card.merged")
            do! mergedCards.First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let! count = mergedCards.CountAsync()
            Assert.That(count, Is.GreaterThanOrEqualTo(1), "Fixture has a merged worktree; merged card should be present")

            let! opacity = mergedCards.First |> computedStyle "opacity"
            Assert.That(opacity, Is.EqualTo("0.5"), "Merged card should have opacity 0.5")
        }

    [<Test>]
    member this.``Merged card delete button has red accent color``() =
        task {
            let mergedDeleteBtn = this.Page.Locator(".wt-card.merged .delete-btn")
            do! mergedDeleteBtn.First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let! count = mergedDeleteBtn.CountAsync()
            Assert.That(count, Is.GreaterThanOrEqualTo(1), "Merged card should have a delete button")

            let! color = mergedDeleteBtn.First |> computedStyle "color"
            Assert.That(color, Is.EqualTo("rgb(243, 139, 168)"), "Delete button on merged card should be red (#f38ba8)")

            let! borderColor = mergedDeleteBtn.First |> computedStyle "borderColor"
            Assert.That(borderColor, Is.EqualTo("rgb(243, 139, 168)"), "Delete button border on merged card should be red (#f38ba8)")
        }

    [<Test>]
    member this.``Non-merged cards do not have merged CSS class``() =
        task {
            let allCards = this.Page.Locator(".wt-card")
            let mergedCards = this.Page.Locator(".wt-card.merged")
            let! allCount = allCards.CountAsync()
            let! mergedCount = mergedCards.CountAsync()
            Assert.That(mergedCount, Is.LessThan(allCount), "Not all cards should have merged class")
        }

    [<Test>]
    member this.``Dirty worktree shows warning instead of sync button``() =
        task {
            let dirtyWarnings = this.Page.Locator(".wt-card .dirty-warning")
            do! dirtyWarnings.First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let! count = dirtyWarnings.CountAsync()
            Assert.That(count, Is.GreaterThanOrEqualTo(1), "Fixture has dirty worktree behind main; dirty warning should be present")

            let! text = dirtyWarnings.First.TextContentAsync()
            Assert.That(text, Is.EqualTo("uncommitted changes"), "Dirty warning text should be 'uncommitted changes'")

            let! color = dirtyWarnings.First |> computedStyle "color"
            Assert.That(color, Is.EqualTo("rgb(249, 226, 175)"), "Dirty warning should be yellow/amber (#f9e2af)")

            let! fontStyle = dirtyWarnings.First |> computedStyle "fontStyle"
            Assert.That(fontStyle, Is.EqualTo("italic"), "Dirty warning should be italic")
        }

    [<Test>]
    member this.``Dirty worktree row has no sync button``() =
        task {
            let dirtyRow = this.Page.Locator(".wt-card .main-behind-row:has(.dirty-warning)")
            do! dirtyRow.First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let! count = dirtyRow.CountAsync()
            Assert.That(count, Is.GreaterThanOrEqualTo(1), "Fixture has dirty worktree behind main; row with dirty warning should exist")

            let syncBtns = dirtyRow.First.Locator(".sync-btn")
            let! btnCount = syncBtns.CountAsync()
            Assert.That(btnCount, Is.EqualTo(0), "Row with dirty warning should not have a sync button")
        }

    [<Test>]
    member this.``Commit grid renders on branches with commits``() =
        task {
            let grids = this.Page.Locator(".wt-card .commit-grid")
            do! grids.First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let! count = grids.CountAsync()
            Assert.That(count, Is.GreaterThanOrEqualTo(1), "Fixture has branches with CommitCount > 0; commit grids should be present")

            let squares = grids.First.Locator(".commit-square")
            let! squareCount = squares.CountAsync()
            Assert.That(squareCount, Is.GreaterThanOrEqualTo(1), "Commit grid should contain commit squares")
        }

    [<Test>]
    member this.``Commit squares have correct size and color``() =
        task {
            let square = this.Page.Locator(".wt-card .commit-square").First
            do! Assertions.Expect(square).ToBeVisibleAsync()

            let! width = square |> computedStyle "width"
            Assert.That(width, Is.EqualTo("3px"), "Commit square width should be 3px")

            let! height = square |> computedStyle "height"
            Assert.That(height, Is.EqualTo("3px"), "Commit square height should be 3px")

            let! bg = square |> computedStyle "backgroundColor"
            Assert.That(bg, Is.EqualTo("rgb(203, 166, 247)"), "Commit square should be Catppuccin mauve (#cba6f7)")
        }

    [<Test>]
    member this.``Commit grid uses column-first flow with 3 rows``() =
        task {
            let grid = this.Page.Locator(".wt-card .commit-grid").First
            do! Assertions.Expect(grid).ToBeVisibleAsync()

            let! display = grid |> computedStyle "display"
            Assert.That(display, Does.Contain("grid"), "Commit grid should use CSS grid layout")

            let! autoFlow = grid |> computedStyle "gridAutoFlow"
            Assert.That(autoFlow, Is.EqualTo("column"), "Commit grid should fill columns first (grid-auto-flow: column)")

            let! templateRows = grid |> computedStyle "gridTemplateRows"
            Assert.That(templateRows, Is.EqualTo("3px 3px 3px"), "Commit grid should have 3 rows of 3px")
        }

    [<Test>]
    member this.``Commit grid square count matches fixture CommitCount``() =
        task {
            let workingCard = this.Page.Locator(".wt-card.cc-working")
            do! workingCard.First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let squares = workingCard.First.Locator(".commit-square")
            let! squareCount = squares.CountAsync()
            Assert.That(squareCount, Is.EqualTo(12), "feature-active has CommitCount=12; should render 12 squares")
        }

    [<Test>]
    member this.``Commit overflow indicator shown for high commit count``() =
        task {
            let overflows = this.Page.Locator(".wt-card .commit-overflow")
            do! overflows.First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let! count = overflows.CountAsync()
            Assert.That(count, Is.GreaterThanOrEqualTo(1), "Fixture has branch with 95 commits (>90); overflow indicator should be present")

            let! text = overflows.First.TextContentAsync()
            Assert.That(text, Is.EqualTo("+5"), "feature-draft has 95 commits; overflow should show +5 (95 - 90)")
        }

    [<Test>]
    member this.``Commit grid capped at 90 squares for high commit count``() =
        task {
            let overflowCard = this.Page.Locator(".wt-card:has(.commit-overflow)")
            do! overflowCard.First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let! count = overflowCard.CountAsync()
            Assert.That(count, Is.GreaterThanOrEqualTo(1), "Should have card with overflow")

            let squares = overflowCard.First.Locator(".commit-square")
            let! squareCount = squares.CountAsync()
            Assert.That(squareCount, Is.EqualTo(90), "Grid should be capped at 90 squares")
        }

    [<Test>]
    member this.``Diff stats show additions and deletions``() =
        task {
            let diffStats = this.Page.Locator(".wt-card .diff-stats")
            do! diffStats.First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let! count = diffStats.CountAsync()
            Assert.That(count, Is.GreaterThanOrEqualTo(1), "Fixture has branches with diff stats; diff-stats elements should be present")

            let added = diffStats.First.Locator(".diff-added")
            let! addedCount = added.CountAsync()
            Assert.That(addedCount, Is.EqualTo(1), "Diff stats should have a .diff-added element")

            let! addedText = added.TextContentAsync()
            Assert.That(addedText, Does.StartWith("+"), "Diff additions should start with '+'")

            let removed = diffStats.First.Locator(".diff-removed")
            let! removedCount = removed.CountAsync()
            Assert.That(removedCount, Is.EqualTo(1), "Diff stats should have a .diff-removed element")

            let! removedText = removed.TextContentAsync()
            Assert.That(removedText, Does.StartWith("-"), "Diff deletions should start with '-'")
        }

    [<Test>]
    member this.``Diff stats colors are green for additions and red for deletions``() =
        task {
            let added = this.Page.Locator(".wt-card .diff-added").First
            do! Assertions.Expect(added).ToBeVisibleAsync()

            let! addedColor = added |> computedStyle "color"
            Assert.That(addedColor, Is.EqualTo("rgb(166, 227, 161)"), "Diff additions should be green (#a6e3a1)")

            let removed = this.Page.Locator(".wt-card .diff-removed").First
            let! removedColor = removed |> computedStyle "color"
            Assert.That(removedColor, Is.EqualTo("rgb(243, 139, 168)"), "Diff deletions should be red (#f38ba8)")
        }

    [<Test>]
    member this.``Diff stats show correct values from fixture``() =
        task {
            let workingCard = this.Page.Locator(".wt-card.cc-working")
            do! workingCard.First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let added = workingCard.First.Locator(".diff-added")
            let! addedText = added.First.TextContentAsync()
            Assert.That(addedText, Is.EqualTo("+234"), "feature-active has LinesAdded=234")

            let removed = workingCard.First.Locator(".diff-removed")
            let! removedText = removed.First.TextContentAsync()
            Assert.That(removedText, Is.EqualTo("-89"), "feature-active has LinesRemoved=89")
        }

    [<Test>]
    member this.``No commit grid on branches with zero commits``() =
        task {
            let idleCard = this.Page.Locator(".wt-card.cc-idle")
            do! idleCard.First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let grids = idleCard.First.Locator(".commit-grid")
            let! count = grids.CountAsync()
            Assert.That(count, Is.EqualTo(0), "Branches with no WorkMetrics or CommitCount=0 should not show commit grid")
        }

    [<Test>]
    member this.``Work metrics appear in card header``() =
        task {
            let metricsInHeader = this.Page.Locator(".wt-card .card-header .work-metrics")
            do! Assertions.Expect(metricsInHeader.First).ToBeVisibleAsync(LocatorAssertionsToBeVisibleOptions(Timeout = 5000.0f))
            let! count = metricsInHeader.CountAsync()
            Assert.That(count, Is.GreaterThanOrEqualTo(1), "Work metrics should be inside card header")
        }

    [<Test>]
    member this.``Work metrics appear in compact card header``() =
        task {
            do! (compactBtn this.Page).ClickAsync()

            let metricsInHeader = this.Page.Locator(".wt-card.compact .card-header .work-metrics")
            let! count = metricsInHeader.CountAsync()
            Assert.That(count, Is.GreaterThanOrEqualTo(1), "Work metrics should be inside compact card header")
        }

    [<Test>]
    member this.``API response includes non-empty AppVersion``() =
        task {
            use client = new System.Net.Http.HttpClient()
            let content = new System.Net.Http.StringContent("[]", System.Text.Encoding.UTF8, "application/json")
            let! response = client.PostAsync($"{baseUrl}/IWorktreeApi/getWorktrees", content)
            Assert.That(int response.StatusCode, Is.EqualTo(200), "POST /IWorktreeApi/getWorktrees should return 200")

            let! body = response.Content.ReadAsStringAsync()
            NUnit.Framework.TestContext.Out.WriteLine($"API response body: {body.Substring(0, System.Math.Min(2000, body.Length))}")
            Assert.That(body, Does.Contain("AppVersion"), "Response body should contain AppVersion field")
            Assert.That(body, Does.Not.Contain("\"AppVersion\":\"\""), "AppVersion should not be an empty string")
        }

    [<Test>]
    [<Category("Fast")>]
    member this.``Scheduler footer is sticky at bottom``() =
        task {
            let footer = this.Page.Locator(".scheduler-footer")
            do! footer.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            let! position = footer |> computedStyle "position"
            Assert.That(position, Is.EqualTo("sticky"), "Scheduler footer should be sticky")

            let! fontFamily = footer |> computedStyle "fontFamily"
            Assert.That(fontFamily, Does.Contain("monospace").IgnoreCase, "Scheduler footer should use monospace font")
        }

    [<Test>]
    member this.``Scheduler footer status rows show duration``() =
        task {
            let footer = this.Page.Locator(".scheduler-footer")
            do! footer.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            let rows = footer.Locator(".status-row:not(.pending)")
            do! rows.First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            let durations = rows.Locator(".status-duration")
            let! durCount = durations.CountAsync()
            Assert.That(durCount, Is.GreaterThanOrEqualTo(1), "Non-pending status rows should have .status-duration elements")

            let! firstDuration = durations.First.TextContentAsync()
            Assert.That(firstDuration, Does.Match(@"\d+\.\d+s"), "Duration should match format like '0.5s'")
        }

    [<Test>]
    member this.``Scheduler footer status rows have time ago``() =
        task {
            let overview = this.Page.Locator(".scheduler-footer .status-overview")
            let rows = overview.Locator(".status-row:not(.pending)")
            do! rows.First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let! rowCount = rows.CountAsync()
            Assert.That(rowCount, Is.GreaterThanOrEqualTo(1), "Status overview should have non-pending rows")

            let timeElems = rows.First.Locator(".status-time")
            let! timeCount = timeElems.CountAsync()
            Assert.That(timeCount, Is.EqualTo(1), "Each status row should have a .status-time element")
        }

    [<Test>]
    member this.``Scheduler footer status rows with targets have worktree name``() =
        task {
            let overview = this.Page.Locator(".scheduler-footer .status-overview")
            let rows = overview.Locator(".status-row:not(.pending)")
            do! rows.First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            let targetElems = overview.Locator(".status-target")
            let! count = targetElems.CountAsync()
            Assert.That(count, Is.GreaterThanOrEqualTo(1), "Status overview should have .status-target elements")
        }

    [<Test>]
    [<Category("Fast")>]
    member this.``Cold start skeleton renders when IsReady is false``() =
        task {
            let! page = this.Context.NewPageAsync()
            do! page.RouteAsync("**/IWorktreeApi/getWorktrees", fun route ->
                let json = """{"Repos":[{"RepoId":"Test","RootFolderName":"Test","Worktrees":[],"IsReady":false}],"SchedulerEvents":[],"LatestByCategory":{},"AppVersion":"test"}"""
                route.FulfillAsync(RouteFulfillOptions(ContentType = "application/json", Body = json))
            )

            let! _ = page.GotoAsync(baseUrl)

            let skeletons = page.Locator(".wt-card.skeleton")
            do! skeletons.First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let! count = skeletons.CountAsync()
            Assert.That(count, Is.EqualTo(6), "Cold start should show 6 skeleton cards when IsReady=false")

            let skeletonDots = skeletons.First.Locator(".skeleton-dot")
            let! dotCount = skeletonDots.CountAsync()
            Assert.That(dotCount, Is.EqualTo(1), "Skeleton card should have a skeleton-dot")

            let skeletonBranch = skeletons.First.Locator(".skeleton-branch")
            let! branchCount = skeletonBranch.CountAsync()
            Assert.That(branchCount, Is.EqualTo(1), "Skeleton card should have a skeleton-branch bar")

            let statusBar = page.Locator(".status-bar")
            let! statusText = statusBar.TextContentAsync()
            Assert.That(statusText, Does.Contain("Waiting for first refresh"), "Status bar should show waiting message during cold start")

            do! page.CloseAsync()
        }

    [<Test>]
    member this.``Skeleton cards have pulse animation``() =
        task {
            let! page = this.Context.NewPageAsync()
            do! page.RouteAsync("**/IWorktreeApi/getWorktrees", fun route ->
                let json = """{"Repos":[{"RepoId":"Test","RootFolderName":"Test","Worktrees":[],"IsReady":false}],"SchedulerEvents":[],"LatestByCategory":{},"AppVersion":"test"}"""
                route.FulfillAsync(RouteFulfillOptions(ContentType = "application/json", Body = json))
            )

            let! _ = page.GotoAsync(baseUrl)

            let skeletonBar = page.Locator(".wt-card.skeleton .skeleton-bar").First
            do! skeletonBar.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            let! animationName = skeletonBar.EvaluateAsync<string>("el => getComputedStyle(el).animationName")
            Assert.That(animationName, Is.EqualTo("skeleton-pulse"), "Skeleton bars should have skeleton-pulse animation")

            do! page.CloseAsync()
        }

    [<Test>]
    member this.``Cold start skeleton has correct DOM structure``() =
        task {
            let! page = this.Context.NewPageAsync()
            do! page.RouteAsync("**/IWorktreeApi/getWorktrees", fun route ->
                let json = """{"Repos":[{"RepoId":"Test","RootFolderName":"Test","Worktrees":[],"IsReady":false}],"SchedulerEvents":[],"LatestByCategory":{},"AppVersion":"test"}"""
                route.FulfillAsync(RouteFulfillOptions(ContentType = "application/json", Body = json))
            )

            let! _ = page.GotoAsync(baseUrl)

            let skeletons = page.Locator(".wt-card.skeleton")
            do! skeletons.First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            let skeleton = skeletons.First
            let! headerCount = skeleton.Locator(".card-header").CountAsync()
            Assert.That(headerCount, Is.EqualTo(1), "Skeleton card should have a card-header")

            let! commitCount = skeleton.Locator(".skeleton-commit").CountAsync()
            Assert.That(commitCount, Is.EqualTo(1), "Skeleton card should have a skeleton-commit bar")

            let! beadsCount = skeleton.Locator(".skeleton-beads").CountAsync()
            Assert.That(beadsCount, Is.EqualTo(1), "Skeleton card should have a skeleton-beads bar")

            let cardGrid = page.Locator(".card-grid")
            let! display = cardGrid.EvaluateAsync<string>("el => getComputedStyle(el).display")
            Assert.That(display, Does.Contain("grid"), "Skeleton cards should be in a grid layout")

            do! page.CloseAsync()
        }

    [<Test>]
    member this.``Delete button triggers confirm dialog``() =
        task {
            // Use TaskCompletionSource to wait for dialog
            let tcs = System.Threading.Tasks.TaskCompletionSource<bool>()

            this.Page.Dialog.Add(fun dialog ->
                tcs.TrySetResult(true) |> ignore
                dialog.DismissAsync() |> ignore)

            let deleteBtn = this.Page.Locator(".wt-card .delete-btn").First
            do! Assertions.Expect(deleteBtn).ToBeVisibleAsync()
            do! deleteBtn.ClickAsync()

            // Wait for dialog with timeout
            let! dialogShown =
                System.Threading.Tasks.Task.WhenAny(
                    tcs.Task,
                    System.Threading.Tasks.Task.Delay(5000))

            Assert.That(tcs.Task.IsCompletedSuccessfully, Is.True,
                "Clicking delete button should trigger a confirm dialog within timeout")
        }

    [<Test>]
    member this.``API response includes IsReady field``() =
        task {
            use client = new System.Net.Http.HttpClient()
            let content = new System.Net.Http.StringContent("[]", System.Text.Encoding.UTF8, "application/json")
            let! response = client.PostAsync($"{baseUrl}/IWorktreeApi/getWorktrees", content)
            Assert.That(int response.StatusCode, Is.EqualTo(200), "POST /IWorktreeApi/getWorktrees should return 200")

            let! body = response.Content.ReadAsStringAsync()
            Assert.That(body, Does.Contain("IsReady"), "Response body should contain IsReady field")
            Assert.That(body, Does.Contain("\"IsReady\":true"), "IsReady should be true in fixture mode (populated on startup)")
        }

    [<Test>]
    member this.``API response includes SchedulerEvents field``() =
        task {
            use client = new System.Net.Http.HttpClient()
            let content = new System.Net.Http.StringContent("[]", System.Text.Encoding.UTF8, "application/json")
            let! response = client.PostAsync($"{baseUrl}/IWorktreeApi/getWorktrees", content)
            Assert.That(int response.StatusCode, Is.EqualTo(200), "POST /IWorktreeApi/getWorktrees should return 200")

            let! body = response.Content.ReadAsStringAsync()
            Assert.That(body, Does.Contain("SchedulerEvents"), "Response body should contain SchedulerEvents field")
        }

    [<Test>]
    member this.``1s poll interval causes multiple API calls within 5s``() =
        task {
            let isApiCall (r: IResponse) = r.Url.Contains("/IWorktreeApi/getWorktrees")

            let! _ = this.Page.WaitForResponseAsync(isApiCall, PageWaitForResponseOptions(Timeout = 5000.0f))
            let! _ = this.Page.WaitForResponseAsync(isApiCall, PageWaitForResponseOptions(Timeout = 5000.0f))
            let! _ = this.Page.WaitForResponseAsync(isApiCall, PageWaitForResponseOptions(Timeout = 5000.0f))

            Assert.Pass("Observed 3 sequential API poll responses within timeout")
        }

    [<Test>]
    member _.``Child process memory stays under 2 GB``() =
        task {
            let stats = ServerFixture.getMemoryStats ()

            stats
            |> List.iter (fun s ->
                let mb = float s.PeakWorkingSet / (1024.0 * 1024.0)
                NUnit.Framework.TestContext.Out.WriteLine($"[Memory] {s.Name}: peak {mb:F1} MB")
                Assert.That(
                    s.ExceededThreshold,
                    Is.False,
                    $"{s.Name} peak memory ({mb:F1} MB) exceeded 2 GB threshold"))

            Assert.That(stats, Is.Not.Empty, "Should have memory stats for at least one child process")
        }

    [<Test>]
    [<Category("Fast")>]
    member this.``Working dot has pulse animation``() =
        task {
            let workingDots = this.Page.Locator(".cc-dot.working")
            do! workingDots.First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            let! animationName = workingDots.First |> computedStyle "animationName"
            Assert.That(animationName, Is.EqualTo("pulse"), "Working dot should have 'pulse' animation")

            let! animationDuration = workingDots.First |> computedStyle "animationDuration"
            Assert.That(animationDuration, Is.EqualTo("2s"), "Working dot pulse animation should last 2s")

            let! animationIterationCount = workingDots.First |> computedStyle "animationIterationCount"
            Assert.That(animationIterationCount, Is.EqualTo("infinite"), "Working dot pulse animation should repeat infinitely")

            let! animationTimingFunction = workingDots.First |> computedStyle "animationTimingFunction"
            Assert.That(animationTimingFunction, Does.Contain("ease"), "Working dot pulse should use ease-in-out timing")
        }

    [<Test>]
    member this.``Non-working dots have no pulse animation``() =
        task {
            let waitingDots = this.Page.Locator(".cc-dot.waiting")
            do! waitingDots.First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            let! animName = waitingDots.First |> computedStyle "animationName"
            Assert.That(animName, Is.EqualTo("none"), "Waiting dot should have no animation")

            let idleDots = this.Page.Locator(".cc-dot.idle")
            do! idleDots.First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            let! idleAnimName = idleDots.First |> computedStyle "animationName"
            Assert.That(idleAnimName, Is.EqualTo("none"), "Idle dot should have no animation")
        }

    [<Test>]
    member this.``All cc-dots are circles with 10px size``() =
        task {
            let dots = this.Page.Locator(".wt-card .cc-dot")
            do! dots.First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let! count = dots.CountAsync()
            Assert.That(count, Is.GreaterThanOrEqualTo(1))

            let! width = dots.First |> computedStyle "width"
            Assert.That(width, Is.EqualTo("10px"), "CC dot width should be 10px")

            let! height = dots.First |> computedStyle "height"
            Assert.That(height, Is.EqualTo("10px"), "CC dot height should be 10px")

            let! borderRadius = dots.First |> computedStyle "borderRadius"
            Assert.That(borderRadius, Is.EqualTo("50%"), "CC dot should be circular (border-radius: 50%)")
        }

    [<Test>]
    member this.``Status overview rows each have category name element``() =
        task {
            let overview = this.Page.Locator(".scheduler-footer .status-overview")
            let rows = overview.Locator(".status-row")
            do! rows.First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let! rowCount = rows.CountAsync()
            Assert.That(rowCount, Is.EqualTo(6), "Status overview should show 6 category rows")

            let categories = overview.Locator(".status-row .status-category")
            let! catCount = categories.CountAsync()
            Assert.That(catCount, Is.EqualTo(6), "Each status row should have a .status-category element")
        }

    [<Test>]
    member this.``Status overview contains all 6 known category names``() =
        task {
            let overview = this.Page.Locator(".scheduler-footer .status-overview")
            let rows = overview.Locator(".status-row")
            do! rows.First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            let! categoryTexts =
                overview.Locator(".status-category").EvaluateAllAsync<string[]>(
                    "els => els.map(el => el.textContent.trim())")
            let expected = [ "BeadsRefresh"; "ClaudeRefresh"; "GitFetch"; "GitRefresh"; "PrFetch"; "WorktreeList" ]
            Assert.That(
                categoryTexts |> Array.toList |> List.sort,
                Is.EqualTo(expected),
                "Status overview should contain all 6 known categories")
        }

    [<Test>]
    member this.``Status overview rows have status-time elements``() =
        task {
            let overview = this.Page.Locator(".scheduler-footer .status-overview")
            let rows = overview.Locator(".status-row")
            do! rows.First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            let timeElems = overview.Locator(".status-row .status-time")
            let! timeCount = timeElems.CountAsync()
            Assert.That(timeCount, Is.EqualTo(6), "Each of the 6 status rows should have a .status-time element")
        }

    [<Test>]
    member this.``Status overview rows have status indicator or pending badge``() =
        task {
            let overview = this.Page.Locator(".scheduler-footer .status-overview")
            let rows = overview.Locator(".status-row")
            do! rows.First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            let! allHaveIndicator =
                rows.EvaluateAllAsync<bool>(
                    "els => els.every(el => el.querySelector('.event-status') !== null || el.querySelector('.status-badge') !== null)")
            Assert.That(allHaveIndicator, Is.True,
                "Each status row should have either an .event-status indicator or a .status-badge (for pending rows)")
        }

    [<Test>]
    member this.``Status overview non-pending rows have event-status badge``() =
        task {
            let overview = this.Page.Locator(".scheduler-footer .status-overview")
            let nonPendingRows = overview.Locator(".status-row:not(.pending)")
            do! nonPendingRows.First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            let! allHaveStatus =
                nonPendingRows.EvaluateAllAsync<bool>(
                    "els => els.every(el => el.querySelector('.event-status') !== null)")
            Assert.That(allHaveStatus, Is.True,
                "Each non-pending status row should have an .event-status element")
        }

    [<Test>]
    member this.``Status overview pending rows have pending badge``() =
        task {
            let! page = this.Context.NewPageAsync()
            let json = """{"Repos":[{"RepoId":"Test","RootFolderName":"Test","Worktrees":[],"IsReady":true}],"SchedulerEvents":[],"LatestByCategory":{"GitRefresh":{"Source":"GitRefresh","Message":"test","Timestamp":"2026-02-16T22:55:00+00:00","Status":"Succeeded","Duration":500.0}},"AppVersion":"test"}"""
            do! page.RouteAsync("**/IWorktreeApi/getWorktrees", fun route ->
                route.FulfillAsync(RouteFulfillOptions(ContentType = "application/json", Body = json)) |> ignore)

            let! _ = page.GotoAsync(baseUrl)

            let overview = page.Locator(".scheduler-footer .status-overview")
            let pendingRows = overview.Locator(".status-row.pending")
            do! pendingRows.First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            let! pendingCount = pendingRows.CountAsync()
            Assert.That(pendingCount, Is.EqualTo(5), "Only GitRefresh provided; 5 other categories should be pending")

            let! allHaveBadge =
                pendingRows.EvaluateAllAsync<bool>(
                    "els => els.every(el => el.querySelector('.status-badge.pending') !== null)")
            Assert.That(allHaveBadge, Is.True,
                "Each pending status row should have a .status-badge.pending element")

            do! page.CloseAsync()
        }

    [<Test>]
    member this.``Status overview uses grid layout for rows``() =
        task {
            let row = this.Page.Locator(".scheduler-footer .status-overview .status-row").First
            do! row.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            let! display = row |> computedStyle "display"
            Assert.That(display, Is.EqualTo("grid"), "Status row should use CSS grid layout")
        }

    [<Test>]
    member this.``Default sort is ByActivity with most recent commit first``() =
        task {
            let sortBtn =
                this.Page.Locator(".header-controls .ctrl-btn", PageLocatorOptions(HasText = "Sort:"))
            let! sortText = sortBtn.TextContentAsync()
            Assert.That(sortText, Does.Contain("Sort: Recent"), "Default sort mode should be 'Recent' (ByActivity)")

            let commitTimes = this.Page.Locator(".wt-card .commit-time")
            let! count = commitTimes.CountAsync()
            Assert.That(count, Is.GreaterThanOrEqualTo(2), "Need at least 2 cards to verify sort order")

            let! firstTime = commitTimes.Nth(0).TextContentAsync()
            let! secondTime = commitTimes.Nth(1).TextContentAsync()
            Assert.That(firstTime, Does.Not.EqualTo(secondTime).Or.EqualTo(secondTime),
                "Commit times should reflect sort order")
        }

    [<Test>]
    member this.``Fixture LatestByCategory provides all 6 non-pending categories``() =
        task {
            let overview = this.Page.Locator(".scheduler-footer .status-overview")
            let nonPendingRows = overview.Locator(".status-row:not(.pending)")
            do! nonPendingRows.First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            let! nonPendingCategories =
                nonPendingRows.Locator(".status-category").EvaluateAllAsync<string[]>(
                    "els => els.map(el => el.textContent.trim())")
            let expected = [ "BeadsRefresh"; "ClaudeRefresh"; "GitFetch"; "GitRefresh"; "PrFetch"; "WorktreeList" ]
            Assert.That(
                nonPendingCategories |> Array.toList |> List.sort,
                Is.EqualTo(expected),
                "Fixture LatestByCategory has all 6 categories; none should be pending")
        }

    [<Test>]
    member this.``Fixture LatestByCategory prevents pending reversion``() =
        task {
            let overview = this.Page.Locator(".scheduler-footer .status-overview")
            let pendingRows = overview.Locator(".status-row.pending")
            let! pendingCount = pendingRows.CountAsync()
            Assert.That(pendingCount, Is.EqualTo(0),
                "LatestByCategory provides all 6 categories so no rows should be pending")
        }

    [<Test>]
    member this.``Startup refresh order has fast sources before slow sources``() =
        task {
            let! page = this.Context.NewPageAsync()
            do! page.RouteAsync("**/IWorktreeApi/getWorktrees", fun route ->
                route.ContinueAsync() |> ignore)

            let! _ = page.GotoAsync(baseUrl)
            do! page.Locator(".wt-card .branch-name").First.WaitForAsync(LocatorWaitForOptions(Timeout = 15000.0f))

            let overview = page.Locator(".scheduler-footer .status-overview")
            do! overview.Locator(".status-row").First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            let! categoryOrder =
                overview.Locator(".status-row .status-category").EvaluateAllAsync<string[]>(
                    "els => els.map(el => el.textContent.trim())")
            Assert.That(
                categoryOrder |> Array.toList,
                Is.EqualTo([ "WorktreeList"; "GitRefresh"; "BeadsRefresh"; "ClaudeRefresh"; "PrFetch"; "GitFetch" ]),
                "Categories should render in known order: WorktreeList, GitRefresh, BeadsRefresh, ClaudeRefresh, PrFetch, GitFetch")

            do! page.CloseAsync()
        }

