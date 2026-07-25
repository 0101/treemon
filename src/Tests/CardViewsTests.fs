module Tests.CardViewsTests

open System
open NUnit.Framework
open Microsoft.Playwright
open Microsoft.Playwright.NUnit
open Newtonsoft.Json
open Shared
open CardViews
open Tests.WorktreeFixtures

let private userMessage glyph text timestamp =
    { Glyph = glyph
      Text = text
      Timestamp = timestamp }

/// The card's activity line (footer line 1) combines the freshest source-tagged activity (SDK
/// `assistant.intent` or `session.title_changed`, carried as `Shared.AgentActivity`) with the running
/// skill as a pill. These tests exercise CardViews.cardActivityLine — the pure decision behind
/// activityLineView — so the activity/skill presence logic is verified without rendering React.
/// `Line` carries at least one of activity/skill (a blank/whitespace skill counts as none); `Empty`
/// when neither is present.
[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type CardActivityLineTests() =

    let ts = DateTimeOffset(2026, 7, 9, 12, 0, 0, TimeSpan.Zero)

    [<Test>]
    member _.``Intent and skill together surface both``() =
        let wt = { baseWt with AgentActivity = Some(AgentActivity.Intent("investigating the fold", ts)); CurrentSkill = Some "investigate" }
        Assert.That(cardActivityLine wt, Is.EqualTo(CardActivityLine.Line(Some(AgentActivity.Intent("investigating the fold", ts)), Some "investigate")))

    [<Test>]
    member _.``Intent with no skill surfaces the intent alone``() =
        let wt = { baseWt with AgentActivity = Some(AgentActivity.Intent("running the tests", ts)); CurrentSkill = None }
        Assert.That(cardActivityLine wt, Is.EqualTo(CardActivityLine.Line(Some(AgentActivity.Intent("running the tests", ts)), None)))

    [<Test>]
    member _.``An intent duplicating the user message is hidden while the skill remains``() =
        let wt =
            { baseWt with
                AgentActivity = Some(AgentActivity.Intent("Use conflict skill to resolve conflicts", ts))
                CurrentSkill = Some "conflict"
                LastUserMessage = Some(userMessage None "  use conflict skill to resolve conflicts  " ts) }
        Assert.That(cardActivityLine wt, Is.EqualTo(CardActivityLine.Line(None, Some "conflict")))

    [<Test>]
    member _.``A session title duplicating the user message leaves no activity line``() =
        let wt =
            { baseWt with
                AgentActivity = Some(AgentActivity.SessionTitle("use conflict skill to resolve conflicts", ts))
                LastUserMessage = Some(userMessage None "use conflict skill to resolve conflicts" ts) }
        Assert.That(cardActivityLine wt, Is.EqualTo CardActivityLine.Empty)

    [<Test>]
    member _.``A skill with no intent surfaces the skill alone``() =
        let wt = { baseWt with AgentActivity = None; CurrentSkill = Some "bd-execute" }
        Assert.That(cardActivityLine wt, Is.EqualTo(CardActivityLine.Line(None, Some "bd-execute")))

    [<Test>]
    member _.``Neither intent nor skill surfaces nothing``() =
        let wt = { baseWt with AgentActivity = None; CurrentSkill = None }
        Assert.That(cardActivityLine wt, Is.EqualTo(CardActivityLine.Empty))

    [<Test>]
    member _.``The skill name is trimmed``() =
        let wt = { baseWt with AgentActivity = None; CurrentSkill = Some "  refactor  " }
        Assert.That(cardActivityLine wt, Is.EqualTo(CardActivityLine.Line(None, Some "refactor")))

    // ----- A blank / whitespace skill is not a skill -----

    [<TestCase("")>]
    [<TestCase("   ")>]
    member _.``A blank or whitespace skill counts as no skill``(skill: string) =
        let wt = { baseWt with AgentActivity = Some(AgentActivity.Intent("thinking", ts)); CurrentSkill = Some skill }
        Assert.That(cardActivityLine wt, Is.EqualTo(CardActivityLine.Line(Some(AgentActivity.Intent("thinking", ts)), None)))

    [<TestCase("")>]
    [<TestCase("   ")>]
    member _.``A blank skill with no intent is Empty``(skill: string) =
        let wt = { baseWt with AgentActivity = None; CurrentSkill = Some skill }
        Assert.That(cardActivityLine wt, Is.EqualTo(CardActivityLine.Empty))

    [<Test>]
    member _.``The intent text is surfaced verbatim``() =
        let intent = "explain the caching approach"
        let wt = { baseWt with AgentActivity = Some(AgentActivity.Intent(intent, ts)); CurrentSkill = None }
        match cardActivityLine wt with
        | CardActivityLine.Line (Some (AgentActivity.Intent (text, _)), None) -> Assert.That(text, Is.EqualTo(intent))
        | other -> Assert.Fail($"Expected an intent-only line, got {other}")

let private dashboardConverter = Fable.Remoting.Json.FableJsonConverter()

let private withChangedCanvasAndFooter branch changedAt (response: DashboardResponse) =
    let updateWorktree wt =
        if wt.Branch <> branch then wt
        else
            { wt with
                AgentActivity = Some(AgentActivity.SessionTitle("Investigate Intent Title Runtime", changedAt))
                LastUserMessage = Some(userMessage (Some MessageGlyph.Canvas) "user prompt" changedAt)
                LastAssistantMessage = Some("assistant response", changedAt)
                CanvasDocs =
                    wt.CanvasDocs
                    |> List.map (fun doc ->
                        { doc with
                            ContentHash = $"{doc.ContentHash}-changed"
                            LastModified = changedAt }) }

    { response with
        Repos =
            response.Repos
            |> List.map (fun repo ->
                { repo with
                    Worktrees = repo.Worktrees |> List.map updateWorktree }) }

[<TestFixture>]
[<Category("E2E")>]
type CardFooterRenderingTests() =
    inherit PageTest()

    [<Test>]
    member this.``Canvas event activity and messages render together in the card footer``() =
        task {
            let! _ = this.Page.GotoAsync(ServerFixture.viteUrl)
            let branch = "feature-active"
            let targetCard =
                this.Page.Locator(
                    ".wt-card",
                    PageLocatorOptions(Has = this.Page.Locator(".branch-name", PageLocatorOptions(HasText = branch))))
            do! targetCard.WaitForAsync(LocatorWaitForOptions(Timeout = 15000.0f))

            let routeHandler =
                Func<IRoute, System.Threading.Tasks.Task>(fun route ->
                    (task {
                        let! upstream = route.FetchAsync()
                        let! json = upstream.TextAsync()
                        let response = JsonConvert.DeserializeObject<DashboardResponse>(json, dashboardConverter)
                        let changed = withChangedCanvasAndFooter branch DateTimeOffset.UtcNow response
                        let body = JsonConvert.SerializeObject(changed, dashboardConverter)
                        do! route.FulfillAsync(RouteFulfillOptions(ContentType = "application/json", Body = body))
                    } :> System.Threading.Tasks.Task))
            do! this.Page.RouteAsync("**/IWorktreeApi/getWorktrees", routeHandler)

            let footer = targetCard.Locator(".card-footer")
            let activityLine = footer.Locator(":scope > .user-prompt.activity-line")
            let userLine = footer.Locator(":scope > .user-prompt:not(.activity-line):not(.assistant-line)")
            let assistantLine = footer.Locator(":scope > .user-prompt.assistant-line")
            let canvasEvent = footer.Locator(":scope > .event-log > .event-entry.canvas-event")

            do! activityLine.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            do! userLine.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            do! assistantLine.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            do! canvasEvent.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            let! activityLineCount = activityLine.CountAsync()
            let! activityTextCount = activityLine.Locator(":scope > .activity-text").CountAsync()
            let! userSpanCount = userLine.Locator(":scope > span").CountAsync()
            let! userGlyphCount = userLine.Locator(":scope > .canvas-message-glyph").CountAsync()
            let! userSourceCount = userLine.Locator(":scope > .event-source").CountAsync()
            let! assistantSpanCount = assistantLine.Locator(":scope > span").CountAsync()
            let! eventTimeCount = assistantLine.Locator(":scope > .event-time").CountAsync()
            let! eventSourceCount = assistantLine.Locator(":scope > .event-source").CountAsync()
            let! canvasEventCount = canvasEvent.CountAsync()

            Assert.Multiple(fun () ->
                Assert.That(activityLineCount, Is.EqualTo(1), "Activity line should remain visible beside a canvas event")
                Assert.That(activityTextCount, Is.EqualTo(1), "Activity line should contain one activity-text span")
                Assert.That(userSpanCount, Is.EqualTo(2), "User line should keep time and message spans")
                Assert.That(userGlyphCount, Is.EqualTo(1), "Canvas user line should contain the easel glyph")
                Assert.That(userSourceCount, Is.Zero, "Canvas user line should not add a text source tag")
                Assert.That(assistantSpanCount, Is.EqualTo(3), "Assistant line should keep its three-span DOM structure")
                Assert.That(eventTimeCount, Is.EqualTo(1), "Assistant line should contain one event-time span")
                Assert.That(eventSourceCount, Is.EqualTo(1), "Assistant line should contain one event-source span")
                Assert.That(canvasEventCount, Is.EqualTo(1), "Canvas event should be a sibling entry in the same footer"))
        }

/// isVisibleCardEvent decides which events reach a card. Post-fork setup is routine noise while it
/// runs or when it succeeds, so only its failures (a genuine failure or a timeout, both `Failed`)
/// stay on the card.
[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type VisibleCardEventTests() =

    let event source status : CardEvent =
        { Source = source
          Message = "setup"
          Timestamp = DateTimeOffset(2026, 7, 9, 12, 0, 0, TimeSpan.Zero)
          Status = status
          Duration = None }

    [<Test>]
    member _.``A running post-fork event is hidden``() =
        Assert.That(isVisibleCardEvent (event EventSource.PostFork (Some StepStatus.Running)), Is.False)

    [<Test>]
    member _.``A succeeded post-fork event is hidden``() =
        Assert.That(isVisibleCardEvent (event EventSource.PostFork (Some StepStatus.Succeeded)), Is.False)

    [<Test>]
    member _.``A failed post-fork event is kept``() =
        Assert.That(isVisibleCardEvent (event EventSource.PostFork (Some(StepStatus.Failed "boom"))), Is.True)

    [<Test>]
    member _.``A timed-out post-fork event is kept (timeout surfaces as a failure)``() =
        Assert.That(isVisibleCardEvent (event EventSource.PostFork (Some(StepStatus.Failed "Timed out after 300000ms"))), Is.True)
    [<Test>]
    member _.``A succeeded non-post-fork event is always kept``() =
        Assert.That(isVisibleCardEvent (event "sync" (Some StepStatus.Succeeded)), Is.True)

let private diffCanvasDoc =
    { Filename = "diff.html"
      ContentHash = "fixture-diff"
      LastModified = DateTimeOffset(2026, 7, 21, 12, 0, 0, TimeSpan.Zero)
      OwnerSessionId = None
      Kind = CanvasDocKind.SystemView }

let private committedWorkMetrics =
    { CommitCount = 2
      LinesAdded = 12
      LinesRemoved = 3 }

let private withDiffDocs (response: DashboardResponse) =
    let addDiff wt =
        let withFixtureState =
            match wt.Branch with
            | "feature-active" ->
                { wt with
                    IsDirty = true
                    HasDiff = true
                    WorkMetrics = None }
            | "feature-recent" ->
                { wt with
                    IsDirty = false
                    HasDiff = true
                    WorkMetrics = Some committedWorkMetrics }
            | "feature-stale" ->
                { wt with
                    IsDirty = true
                    HasDiff = true
                    WorkMetrics = None }
            | "feature-idle" ->
                { wt with
                    IsDirty = false
                    HasDiff = false
                    WorkMetrics = None }
            | _ -> wt

        if withFixtureState.CanvasDocs |> List.exists (fun doc -> doc.Filename = diffCanvasDoc.Filename) then
            withFixtureState
        else
            { withFixtureState with
                CanvasDocs = withFixtureState.CanvasDocs @ [ diffCanvasDoc ] }

    { response with
        Repos =
            response.Repos
            |> List.mapi (fun index repo ->
                let worktrees = repo.Worktrees |> List.map addDiff
                let withArchivedFixture =
                    if index <> 0 || worktrees |> List.exists _.IsArchived then worktrees
                    else
                        worktrees
                        |> List.tryHead
                        |> Option.map (fun wt ->
                            { wt with
                                Path = WorktreePath $"{WorktreePath.value wt.Path}-archived-fixture"
                                Branch = "archived-fixture"
                                IsArchived = true })
                        |> Option.map (fun archived -> worktrees @ [ archived ])
                        |> Option.defaultValue worktrees
                { repo with
                    Worktrees = withArchivedFixture }) }

let private routeDashboard (transform: DashboardResponse -> DashboardResponse) (page: IPage) =
    let routeHandler =
        Func<IRoute, System.Threading.Tasks.Task>(fun route ->
            (task {
                let! upstream = route.FetchAsync()
                let! json = upstream.TextAsync()
                let response = JsonConvert.DeserializeObject<DashboardResponse>(json, dashboardConverter)
                let body = JsonConvert.SerializeObject(transform response, dashboardConverter)
                do! route.FulfillAsync(RouteFulfillOptions(ContentType = "application/json", Body = body))
            } :> System.Threading.Tasks.Task))
    page.RouteAsync("**/IWorktreeApi/getWorktrees", routeHandler)

let private routeDashboardWithDiffDocs = routeDashboard withDiffDocs

let private routeDashboardWithoutDiffDocs =
    routeDashboard (fun response ->
        { response with
            Repos =
                response.Repos
                |> List.map (fun repo ->
                    { repo with
                        Worktrees =
                            repo.Worktrees
                            |> List.map (fun wt ->
                                { wt with
                                    CanvasDocs =
                                        wt.CanvasDocs
                                        |> List.filter (fun doc -> doc.Filename <> diffCanvasDoc.Filename) }) }) })

let private cardByBranch (page: IPage) branch =
    page.Locator(
        ".wt-card",
        PageLocatorOptions(Has = page.Locator(".branch-name", PageLocatorOptions(HasText = branch))))

/// Click a card and wait until it is actually marked focused. Focus is applied by an Elmish update
/// and a re-render, so asserting on the class straight after the click races the render — reliably
/// on a loaded CI machine, intermittently everywhere else.
let private focusCardAndWait (page: IPage) branch =
    task {
        do! (cardByBranch page branch).ClickAsync()

        let focused =
            page.Locator(
                ".wt-card.focused",
                PageLocatorOptions(Has = page.Locator(".branch-name", PageLocatorOptions(HasText = branch))))

        do! focused.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
    }

[<TestFixture>]
[<Category("E2E")>]
type WorktreeDiffActionTests() =
    inherit PageTest()

    override this.ContextOptions() =
        let options = base.ContextOptions()
        options.IgnoreHTTPSErrors <- true
        options

    member private this.NavigateWithDiffDocs() =
        task {
            do! routeDashboardWithDiffDocs this.Page
            let! _ = this.Page.GotoAsync(ServerFixture.viteUrl)
            do! this.Page.Locator(".wt-card .branch-name").First.WaitForAsync(LocatorWaitForOptions(Timeout = 15000.0f))
        }

    [<Test>]
    member this.``Pointer Diff opens and switches the scoped view without changing card focus``() =
        task {
            do! this.NavigateWithDiffDocs()

            let focusedCard = cardByBranch this.Page "feature-active"
            let firstTarget = cardByBranch this.Page "feature-recent"
            let secondTarget = cardByBranch this.Page "feature-stale"
            do! focusCardAndWait this.Page "feature-active"

            let! closedCount = this.Page.Locator(".canvas-pane.open").CountAsync()
            Assert.That(closedCount, Is.EqualTo(0), "Canvas pane should start closed")

            do! firstTarget.Locator(".diff-action-btn").ClickAsync()
            do! this.Page.Locator(".canvas-pane.open").WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let activeIframe = this.Page.Locator(".canvas-pane .canvas-iframe-active")
            do! activeIframe.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let! firstSrc = activeIframe.GetAttributeAsync("src")

            let! focusedAfterFirst = focusedCard.GetAttributeAsync("class")
            let! targetAfterFirst = firstTarget.GetAttributeAsync("class")
            Assert.Multiple(fun () ->
                Assert.That(firstSrc, Does.Contain("feature-recent").And.EndWith("/diff.html"))
                Assert.That(focusedAfterFirst, Does.Contain("focused"), "Diff click must not move card focus")
                Assert.That(targetAfterFirst, Does.Not.Contain("focused"), "Stopped click propagation must keep the target card unfocused"))

            do! secondTarget.Locator(".diff-action-btn").ClickAsync()
            let! _ =
                this.Page.WaitForFunctionAsync(
                    "src => document.querySelector('.canvas-iframe-active')?.getAttribute('src') !== src",
                    firstSrc,
                    PageWaitForFunctionOptions(Timeout = 5000.0f))
            let! secondSrc = activeIframe.GetAttributeAsync("src")
            let! focusedAfterSecond = focusedCard.GetAttributeAsync("class")

            Assert.Multiple(fun () ->
                Assert.That(secondSrc, Does.Contain("feature-stale").And.EndWith("/diff.html"))
                Assert.That(secondSrc, Is.Not.EqualTo(firstSrc), "An already-open pane should switch worktree scope")
                Assert.That(focusedAfterSecond, Does.Contain("focused"), "Switching diff targets must preserve the focused card"))
        }

    [<TestCase("Enter")>]
    [<TestCase("Space")>]
    member this.``Keyboard Diff activation opens the intended worktree without selecting its card``(key: string) =
        task {
            do! this.NavigateWithDiffDocs()

            let focusedCard = cardByBranch this.Page "feature-active"
            let targetCard = cardByBranch this.Page "feature-stale"
            do! focusCardAndWait this.Page "feature-active"

            let diffButton = targetCard.Locator(".diff-action-btn")
            do! diffButton.FocusAsync()
            do! this.Page.Keyboard.PressAsync(key)

            let activeIframe = this.Page.Locator(".canvas-pane .canvas-iframe-active")
            do! activeIframe.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let! src = activeIframe.GetAttributeAsync("src")
            let! focusedClass = focusedCard.GetAttributeAsync("class")
            let! targetClass = targetCard.GetAttributeAsync("class")

            Assert.Multiple(fun () ->
                Assert.That(src, Does.Contain("feature-stale").And.EndWith("/diff.html"))
                Assert.That(focusedClass, Does.Contain("focused"))
                Assert.That(targetClass, Does.Not.Contain("focused")))
        }

    [<Test>]
    member this.``Diff action visibility follows comparison content readiness and archive state in both layouts``() =
        task {
            do! this.NavigateWithDiffDocs()

            let! archivedCount = this.Page.Locator(".archive-card").CountAsync()
            let! archivedDiffCount = this.Page.Locator(".archive-card .diff-action-btn").CountAsync()
            let! untrackedOnly = (cardByBranch this.Page "feature-active").Locator(".diff-action-btn").CountAsync()
            let! committedOnly = (cardByBranch this.Page "feature-recent").Locator(".diff-action-btn").CountAsync()
            let! localOnly = (cardByBranch this.Page "feature-stale").Locator(".diff-action-btn").CountAsync()
            let! netZeroCommits = (cardByBranch this.Page "feature-idle").Locator(".diff-action-btn").CountAsync()
            Assert.Multiple(fun () ->
                Assert.That(archivedCount, Is.GreaterThan(0), "Fixture must exercise an archived card")
                Assert.That(untrackedOnly, Is.EqualTo(1), "Untracked-only content should be actionable")
                Assert.That(committedOnly, Is.EqualTo(1), "Committed-only content should be actionable")
                Assert.That(localOnly, Is.EqualTo(1), "Local-only content should be actionable")
                Assert.That(netZeroCommits, Is.Zero, "Net-zero committed history should not render Diff")
                Assert.That(archivedDiffCount, Is.Zero))

            let compactButton = this.Page.Locator(".header-controls .ctrl-btn", PageLocatorOptions(HasText = "Compact"))
            do! compactButton.ClickAsync()
            do! this.Page.Locator(".wt-card.compact").First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let! compactUntracked = (cardByBranch this.Page "feature-active").Locator(".diff-action-btn").CountAsync()
            let! compactCommitted = (cardByBranch this.Page "feature-recent").Locator(".diff-action-btn").CountAsync()
            let! compactLocal = (cardByBranch this.Page "feature-stale").Locator(".diff-action-btn").CountAsync()
            let! compactNetZero = (cardByBranch this.Page "feature-idle").Locator(".diff-action-btn").CountAsync()
            Assert.That(
                [| compactUntracked; compactCommitted; compactLocal; compactNetZero |],
                Is.EqualTo([| 1; 1; 1; 0 |])
            )
        }

    [<Test>]
    member this.``Diff action uses only an accessible inline SVG glyph on normal and compact cards``() =
        task {
            do! this.NavigateWithDiffDocs()

            let semantics (button: ILocator) =
                button.EvaluateAsync<string array>(
                    """button => [
                        button.getAttribute('aria-label'),
                        button.getAttribute('title'),
                        button.firstElementChild?.tagName.toLowerCase() || '',
                        button.firstElementChild?.getAttribute('aria-hidden') || '',
                        String(button.querySelectorAll(':scope > svg').length),
                        button.textContent.trim(),
                        button.className,
                        getComputedStyle(button).width,
                        getComputedStyle(button).height,
                        getComputedStyle(button).color,
                        getComputedStyle(button).borderTopColor,
                        getComputedStyle(button).backgroundColor
                    ]"""
                )

            let! normal =
                semantics (this.Page.Locator(".wt-card:not(.compact) .diff-action-btn").First)

            let compactButton = this.Page.Locator(".header-controls .ctrl-btn", PageLocatorOptions(HasText = "Compact"))
            do! compactButton.ClickAsync()
            let compactDiff = this.Page.Locator(".wt-card.compact .diff-action-btn").First
            do! compactDiff.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let! compact = semantics compactDiff

            let expected =
                [| "Open worktree diff"
                   "Open worktree diff"
                   "svg"
                   "true"
                   "1"
                   ""
                   "action-btn diff-action-btn"
                   "23px"
                   "21px"
                   "rgb(127, 132, 156)"
                   "rgb(69, 71, 90)"
                   "rgba(0, 0, 0, 0)" |]

            Assert.Multiple(fun () ->
                Assert.That(normal, Is.EqualTo(expected))
                Assert.That(compact, Is.EqualTo(expected)))
        }

    [<Test>]
    member this.``Diff action is absent on normal and compact cards until the SystemView is scanned``() =
        task {
            do! routeDashboardWithoutDiffDocs this.Page
            let! _ = this.Page.GotoAsync(ServerFixture.viteUrl)
            do! this.Page.Locator(".wt-card .branch-name").First.WaitForAsync(LocatorWaitForOptions(Timeout = 15000.0f))

            let normalCards = this.Page.Locator(".wt-card:not(.compact)")
            let! normalCount = normalCards.Locator(".diff-action-btn").CountAsync()
            Assert.That(normalCount, Is.Zero)

            let compactButton = this.Page.Locator(".header-controls .ctrl-btn", PageLocatorOptions(HasText = "Compact"))
            do! compactButton.ClickAsync()
            let compactCards = this.Page.Locator(".wt-card.compact")
            do! compactCards.First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let! compactCount = compactCards.Locator(".diff-action-btn").CountAsync()
            Assert.That(compactCount, Is.Zero)
        }

    [<Test>]
    member this.``Diff SystemView opens its exact iframe URL in a standalone tab``() =
        task {
            do! this.NavigateWithDiffDocs()

            let targetCard = cardByBranch this.Page "feature-active"
            do! targetCard.Locator(".diff-action-btn").ClickAsync()

            let activeIframe = this.Page.Locator(".canvas-pane .canvas-iframe-active")
            do! activeIframe.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let! expectedUrl = activeIframe.GetAttributeAsync("src")
            let diffTab =
                this.Page.Locator(
                    ".canvas-pane .canvas-system-tab[title^='Worktree diff']")
            do! diffTab.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            let popupReady =
                System.Threading.Tasks.TaskCompletionSource<IPage>(
                    System.Threading.Tasks.TaskCreationOptions.RunContinuationsAsynchronously)
            this.Page.Popup.Add(fun popup -> popupReady.TrySetResult(popup) |> ignore)

            do! diffTab.DblClickAsync()
            let! popup = popupReady.Task.WaitAsync(TimeSpan.FromSeconds(5.0))
            let! _ =
                popup.WaitForFunctionAsync(
                    "() => location.pathname.endsWith('/diff.html')",
                    null,
                    PageWaitForFunctionOptions(Timeout = 5000.0f))

            Assert.That(popup.Url, Is.EqualTo(expectedUrl))
            do! popup.CloseAsync()
        }
