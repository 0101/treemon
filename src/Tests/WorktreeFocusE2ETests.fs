module Tests.WorktreeFocusE2ETests

open System
open NUnit.Framework
open Microsoft.Playwright
open Newtonsoft.Json
open Shared
open OverviewData
open Tests.WorktreeFixtures

let private converter = Fable.Remoting.Json.FableJsonConverter()

let private serialize value =
    JsonConvert.SerializeObject(value, converter)

let private responseAt (now: DateTimeOffset) =
    let worktree branch (hours: float option) status planned =
        { baseWt with
            Path = WorktreePath $@"Q:\focus-fixture\{branch}"
            Branch = branch
            CodingTool = status
            SessionActivityAt = hours |> Option.map (fun hours -> now - TimeSpan.FromHours hours)
            Beads = { BeadsSummary.zero with Open = planned }
            Planning = { BeadsPlanning.zero with Planned = planned }
            Sessions =
                match status with
                | NoSession -> []
                | Working
                | WaitingForUser
                | Idle ->
                    [ { InstanceId = SessionInstanceId branch
                        Status = status
                        Skill = None
                        ContextUsage = None } ] }
    let recent = worktree "recent-work" (Some 0.5) Working 2
    let earlier = worktree "earlier-work" (Some 6.0) Idle 3
    let older = worktree "older-work" (Some 48.0) Idle 5
    let unknown = worktree "unknown" None NoSession 0
    let archived = { worktree "archive" (Some 0.5) NoSession 0 with IsArchived = true }

    { Repos =
        [ { RepoId = RepoId "fixture"
            RootFolderName = "fixture"
            Worktrees = [ recent; earlier; older; unknown; archived ]
            IsReady = true
            Provider = None
            BaseBranch = "main" } ]
      SchedulerEvents = []
      LatestByCategory = Map.empty
      AppVersion = "focus-fixture"
      DeployBranch = None
      SystemMetrics = None
      EditorName = "Editor"
      WorktreeSkills = []
      CollapsedRepos = Set.empty
      TerminalPaneOpen = false
      CanvasPaneOpen = false
      OverviewPanelOpen = true
      WorkspaceWidth = WorkspaceWidth.EqualThirds
      TerminalHostUpdate = TerminalHostUpdateState.Unavailable }

[<TestFixture>]
[<Category("E2E")>]
[<NonParallelizable>]
type WorktreeFocusE2ETests() =
    inherit ServerFixture.SharedPageTest()

    // Playwright reports errors asynchronously across the fixture's setup/test/teardown boundary.
    let mutable pageErrors: string list = []

    member private this.Open(response: DashboardResponse) =
        task {
            pageErrors <- []
            this.Page.PageError.Add(fun error -> pageErrors <- error :: pageErrors)
            do! this.Page.RouteAsync("**/IWorktreeApi/getWorktrees", fun route ->
                route.FulfillAsync(RouteFulfillOptions(ContentType = "application/json", Body = serialize response)))
            let history =
                { Anchor = DateTimeOffset.UtcNow
                  Snapshots =
                    [ { Timestamp = DateTimeOffset.UtcNow.AddMinutes(-30.0)
                        Tasks = [ { Kind = TaskBucketKind.Planned; Count = 5 } ]
                        Agents = [ { Kind = AgentGroupKind.Idle; Count = 2 } ] } ] }
            do! this.Page.RouteAsync("**/IWorktreeApi/getOverviewHistory", fun route ->
                route.FulfillAsync(RouteFulfillOptions(ContentType = "application/json", Body = serialize history)))
            let! _ = this.Page.GotoAsync(ServerFixture.viteUrl)
            do! Assertions.Expect(this.Page.Locator(".worktree-focus-btn")).ToHaveTextAsync("Focus: off")
            do! Assertions.Expect(this.Page.Locator(".card-grid .wt-card")).ToHaveCountAsync(
                response.Repos |> List.sumBy (fun repo -> repo.Worktrees |> List.filter (fun wt -> not wt.IsArchived) |> List.length))
        }

    [<TearDown>]
    member _.NoBrowserErrors() =
        Assert.That(pageErrors, Is.Empty)

    [<Test>]
    member this.``Focus cycles scoped cards and Overview together and Off restores all cards``() =
        task {
            do! this.Open(responseAt DateTimeOffset.UtcNow)
            let button = this.Page.Locator(".worktree-focus-btn")

            for label, cards, markers in [ "2h", 1, 1; "8h", 2, 2; "24h", 2, 2; "off", 4, 3 ] do
                do! button.ClickAsync()
                do! Assertions.Expect(button).ToHaveTextAsync($"Focus: {label}")
                do! Assertions.Expect(this.Page.Locator(".card-grid .wt-card")).ToHaveCountAsync(cards)
                do! Assertions.Expect(this.Page.Locator(".overview-circles .overview-circle")).ToHaveCountAsync(markers)
                do! Assertions.Expect(button).ToHaveAttributeAsync("aria-pressed", if label = "off" then "false" else "true")

            do! button.ClickAsync()
            do! this.Page.Locator(".overview-item.activity-working").ClickAsync()
            do! Assertions.Expect(this.Page.Locator(".overview-breakdown .overview-chip")).ToHaveCountAsync(1)
            do! this.Page.Locator(".repo-header").ClickAsync()
            do! Assertions.Expect(this.Page.Locator(".repo-ct-dots .ct-dot")).ToHaveCountAsync(1)
        }

    [<Test>]
    member this.``An empty Focus result is not loading and keeps global history and creation accessible``() =
        task {
            let response = responseAt DateTimeOffset.UtcNow
            let emptyWindow =
                { response with
                    Repos =
                        response.Repos
                        |> List.map (fun repo ->
                            { repo with Worktrees = repo.Worktrees |> List.filter (fun wt -> wt.Branch = "older-work") }) }
            do! this.Open emptyWindow
            do! this.Page.Locator(".worktree-focus-btn").ClickAsync()
            do! Assertions.Expect(this.Page.Locator(".card-grid .wt-card")).ToHaveCountAsync(0)
            do! Assertions.Expect(this.Page.Locator(".focus-empty")).ToBeVisibleAsync()
            do! Assertions.Expect(this.Page.Locator(".overview-focus-empty")).ToBeVisibleAsync()
            do! Assertions.Expect(this.Page.Locator(".wt-card.skeleton")).ToHaveCountAsync(0)
            do! Assertions.Expect(this.Page.Locator(".repo-header .create-wt-btn")).ToBeVisibleAsync()
            do! this.Page.Locator(".history-toggle").ClickAsync()
            do! Assertions.Expect(this.Page.Locator(".overview-history-scope")).ToBeVisibleAsync()
            do! Assertions.Expect(this.Page.Locator(".history-charts")).ToHaveCountAsync(2)
            do! Assertions.Expect(this.Page.Locator(".card-grid .wt-card")).ToHaveCountAsync(0)
        }

    [<Test>]
    member this.``Ctrl P finds hidden work and expands its collapsed repository after clearing Focus``() =
        task {
            do! this.Open(responseAt DateTimeOffset.UtcNow)
            do! this.Page.Locator(".worktree-focus-btn").ClickAsync()
            do! this.Page.Locator(".repo-header").ClickAsync()
            do! Assertions.Expect(this.Page.Locator(".repo-header")).ToHaveClassAsync(
                System.Text.RegularExpressions.Regex("collapsed"))
            do! this.Page.Keyboard.PressAsync("Control+p")
            do! this.Page.Locator(".worktree-search-input").FillAsync("older-work")
            do! Assertions.Expect(this.Page.Locator(".worktree-search-result")).ToHaveCountAsync(1)
            do! this.Page.Keyboard.PressAsync("Enter")
            do! Assertions.Expect(this.Page.Locator(".worktree-focus-btn")).ToHaveTextAsync("Focus: off")
            do! Assertions.Expect(this.Page.Locator(".card-grid .wt-card")).ToHaveCountAsync(4)
            do! Assertions.Expect(this.Page.Locator(".wt-card.focused.ct-idle")).ToHaveCountAsync(1)
            do! Assertions.Expect(this.Page.Locator(".repo-header.collapsed")).ToHaveCountAsync(0)
        }

    [<Test>]
    member this.``Focus remains keyboard-operable beside One-pane tabs on a narrow viewport``() =
        task {
            do! this.Page.SetViewportSizeAsync(320, 720)
            do! this.Open(responseAt DateTimeOffset.UtcNow)
            let button = this.Page.Locator(".workspace-single-header > .worktree-focus-btn")
            do! Assertions.Expect(button).ToBeVisibleAsync()
            do! Assertions.Expect(this.Page.Locator(".workspace-tabs [role=tab]")).ToHaveCountAsync(3)
            do! button.FocusAsync()
            do! this.Page.Keyboard.PressAsync("Enter")
            do! Assertions.Expect(button).ToHaveTextAsync("Focus: 2h")
            do! Assertions.Expect(this.Page.Locator(".card-grid .wt-card")).ToHaveCountAsync(1)
            let! headerFits =
                this.Page.EvaluateAsync<bool>(
                    """() => {
                        const header = document.querySelector('.workspace-single-header');
                        const focus = header.querySelector('.worktree-focus-btn').getBoundingClientRect();
                        const tabs = header.querySelector('.workspace-tabs').getBoundingClientRect();
                        return header.scrollWidth <= header.clientWidth
                            && focus.left >= tabs.right
                            && focus.right <= innerWidth;
                    }""")
            Assert.That(headerFits, Is.True, "tabs and Focus must not overlap or overflow")
        }

    [<TestCase(901)>]
    [<TestCase(1280)>]
    member this.``The desktop toolbar keeps Focus accessible with all pane and update controls``(width: int) =
        task {
            do! this.Page.SetViewportSizeAsync(width, 720)
            let response =
                { responseAt DateTimeOffset.UtcNow with
                    TerminalPaneOpen = true
                    CanvasPaneOpen = true
                    TerminalHostUpdate = TerminalHostUpdateState.Available
                    DeployBranch = Some "focus-mode"
                    SystemMetrics =
                        Some { CpuPercent = 20.0; MemoryUsedMb = 4096; MemoryTotalMb = 16384 } }
            do! this.Open response
            let! layout =
                this.Page.EvaluateAsync<string>(
                    """() => {
                        const header = document.querySelector('.app-header');
                        const focus = header.querySelector('.worktree-focus-btn').getBoundingClientRect();
                        const controls = header.querySelector('.header-controls').getBoundingClientRect();
                        const fits = header.scrollWidth <= header.clientWidth
                            && focus.left >= 0 && focus.right <= innerWidth
                            && controls.bottom <= header.getBoundingClientRect().bottom;
                        return JSON.stringify({
                            fits, headerWidth: header.clientWidth, headerScrollWidth: header.scrollWidth,
                            focusLeft: focus.left, focusRight: focus.right,
                            controlsBottom: controls.bottom, headerBottom: header.getBoundingClientRect().bottom
                        });
                    }""")
            let measured = Newtonsoft.Json.Linq.JObject.Parse layout
            TestContext.Out.WriteLine(layout)
            Assert.That(measured.Value<bool>("fits"), Is.True, "the full toolbar must not overflow or clip Focus")

            let button = this.Page.Locator(".header-controls > .worktree-focus-btn")
            do! button.ClickAsync()
            do! Assertions.Expect(button).ToHaveTextAsync("Focus: 2h")
            do! Assertions.Expect(this.Page.Locator(".card-grid .wt-card")).ToHaveCountAsync(1)
            do! button.FocusAsync()
            do! Assertions.Expect(button).ToBeFocusedAsync()
            do! button.PressAsync("Enter")
            do! Assertions.Expect(button).ToHaveTextAsync("Focus: 8h")
            do! button.PressAsync("Space")
            do! Assertions.Expect(button).ToHaveTextAsync("Focus: 24h")
            do! Assertions.Expect(this.Page.Locator(".card-grid .wt-card")).ToHaveCountAsync(2)
        }
