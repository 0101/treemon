module Tests.CardViewsTests

open System
open NUnit.Framework
open Microsoft.Playwright
open Microsoft.Playwright.NUnit
open Newtonsoft.Json
open Shared
open CardViews
open Tests.WorktreeFixtures

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
                LastUserMessage = Some("user prompt", changedAt)
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
            let! assistantSpanCount = assistantLine.Locator(":scope > span").CountAsync()
            let! eventTimeCount = assistantLine.Locator(":scope > .event-time").CountAsync()
            let! eventSourceCount = assistantLine.Locator(":scope > .event-source").CountAsync()
            let! canvasEventCount = canvasEvent.CountAsync()

            Assert.Multiple(fun () ->
                Assert.That(activityLineCount, Is.EqualTo(1), "Activity line should remain visible beside a canvas event")
                Assert.That(activityTextCount, Is.EqualTo(1), "Activity line should contain one activity-text span")
                Assert.That(userSpanCount, Is.EqualTo(2), "User line should keep its two-span DOM structure")
                Assert.That(assistantSpanCount, Is.EqualTo(3), "Assistant line should keep its three-span DOM structure")
                Assert.That(eventTimeCount, Is.EqualTo(1), "Assistant line should contain one event-time span")
                Assert.That(eventSourceCount, Is.EqualTo(1), "Assistant line should contain one event-source span")
                Assert.That(canvasEventCount, Is.EqualTo(1), "Canvas event should be a sibling entry in the same footer"))
        }

/// isVisibleCardEvent decides which events reach a card. Post-fork setup is routine noise while it
/// runs or when it succeeds, so only its failures (a genuine failure or a timeout, both `Failed`)
/// stay on the card; events from every other source always show.
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
    member _.``A succeeded sync event is always kept``() =
        Assert.That(isVisibleCardEvent (event EventSource.Sync (Some StepStatus.Succeeded)), Is.True)
