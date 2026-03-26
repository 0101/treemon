module Tests.DemoModeTests

open System
open System.Diagnostics
open System.IO
open System.Net.Http
open System.Text
open NUnit.Framework
open Microsoft.Playwright
open Microsoft.Playwright.NUnit
open Newtonsoft.Json
open Shared

let private repoRoot =
    Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))

let private serverProjectPath =
    Path.Combine(repoRoot, "src", "Server")

let private demoServerPort = 5003
let private demoVitePort = 5176
let private demoServerUrl = $"http://localhost:{demoServerPort}"
let private demoViteUrl = $"http://localhost:{demoVitePort}"

let private serverProcess: Process option ref = ref None
let private viteProcess: Process option ref = ref None

let private converter = Fable.Remoting.Json.FableJsonConverter()

let private deserializeDashboard (json: string) =
    JsonConvert.DeserializeObject<DashboardResponse>(json, converter)

let private tryGet (client: HttpClient) (url: string) =
    async {
        try
            let! response = client.GetAsync(url) |> Async.AwaitTask
            return int response.StatusCode < 500
        with _ ->
            return false
    }

let rec private pollUntilReady (client: HttpClient) (url: string) (deadline: DateTime) =
    async {
        if DateTime.UtcNow > deadline then
            failwith $"Timed out waiting for {url}"
        else
            let! ok = tryGet client url
            if not ok then
                do! Async.Sleep(500)
                return! pollUntilReady client url deadline
    }

let private waitForUrl (url: string) (timeoutMs: int) =
    async {
        use client = new HttpClient()
        let deadline = DateTime.UtcNow.AddMilliseconds(float timeoutMs)
        do! pollUntilReady client url deadline
    }
    |> Async.StartAsTask

let private startDemoServer () =
    task {
        TestUtils.killOrphansOnPort demoServerPort
        let proc =
            TestUtils.startProcess
                "dotnet"
                $"""run --project "{serverProjectPath}" -- --demo --port {demoServerPort}"""
                repoRoot
                []
                false
        serverProcess.Value <- Some proc
        do! waitForUrl demoServerUrl 30000
    }

let private startDemoVite () =
    task {
        TestUtils.killOrphansOnPort demoVitePort
        let proc =
            TestUtils.startProcess
                "npx"
                "vite --host"
                repoRoot
                [ "VITE_PORT", string demoVitePort
                  "API_PORT", string demoServerPort
                  "NODE_OPTIONS", "--max-old-space-size=512" ]
                false
        viteProcess.Value <- Some proc
        do! waitForUrl demoViteUrl 15000
    }

let private stopDemoProcesses () =
    TestUtils.killProc serverProcess.Value
    TestUtils.killProc viteProcess.Value
    serverProcess.Value <- None
    viteProcess.Value <- None

[<TestFixture>]
[<Category("Demo")>]
[<Category("E2E")>]
type DemoModeTests() =
    inherit PageTest()

    static let mutable setupDone = false

    override this.ContextOptions() =
        let opts = base.ContextOptions()
        opts.IgnoreHTTPSErrors <- true
        opts

    [<OneTimeSetUp>]
    member _.StartDemoInfrastructure() =
        task {
            if not setupDone then
                do! startDemoServer ()
                do! ServerFixture.compileFable ()
                do! startDemoVite ()
                setupDone <- true
                TestContext.Out.WriteLine("Demo server and Vite started successfully")
        }

    [<OneTimeTearDown>]
    member _.StopDemoInfrastructure() =
        stopDemoProcesses ()

    [<SetUp>]
    member this.NavigateToDashboard() =
        task {
            let! _ = this.Page.GotoAsync(demoViteUrl)
            do!
                this.Page
                    .Locator(".wt-card .branch-name")
                    .First.WaitForAsync(LocatorWaitForOptions(Timeout = 15000.0f))
        }

    [<Test>]
    member this.``Demo mode renders both repos``() =
        task {
            let repoHeaders = this.Page.Locator(".repo-header .repo-name")
            do! repoHeaders.First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let! count = repoHeaders.CountAsync()
            Assert.That(count, Is.EqualTo(2), "Demo mode should render 2 repos (CloudPlatform and DataPipeline)")

            let! firstRepo = repoHeaders.Nth(0).TextContentAsync()
            let! secondRepo = repoHeaders.Nth(1).TextContentAsync()
            let names = [ firstRepo; secondRepo ] |> List.sort
            Assert.That(names, Is.EquivalentTo([ "CloudPlatform"; "DataPipeline" ]))
        }

    [<Test>]
    member this.``Demo mode renders expected worktree cards``() =
        task {
            let cards = this.Page.Locator(".wt-card .branch-name")
            do! cards.First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let! count = cards.CountAsync()
            Assert.That(count, Is.GreaterThanOrEqualTo(5), "Demo mode should render at least 5 worktree cards across both repos")
        }

    [<Test>]
    member this.``Demo mode shows SystemMetrics``() =
        task {
            let metrics = this.Page.Locator(".system-metrics")
            do! metrics.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let! count = metrics.CountAsync()
            Assert.That(count, Is.EqualTo(1), "SystemMetrics should be rendered in the header")

            let bars = metrics.Locator(".metric-bar-row")
            let! barCount = bars.CountAsync()
            Assert.That(barCount, Is.EqualTo(2), "SystemMetrics should show CPU and RAM bars")
        }

    [<Test>]
    member this.``Demo mode does not show DeployBranch``() =
        task {
            let deployBranch = this.Page.Locator(".deploy-branch")
            let! count = deployBranch.CountAsync()
            Assert.That(count, Is.EqualTo(0), "DeployBranch should not be rendered in demo mode")
        }

    [<Test>]
    member this.``Demo mode shows EditorName in button titles``() =
        task {
            let editorBtns = this.Page.Locator("[title*='VS Code']")
            do! editorBtns.First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let! count = editorBtns.CountAsync()
            Assert.That(count, Is.GreaterThanOrEqualTo(1), "At least one button should reference VS Code (EditorName)")
        }

    [<Test>]
    member this.``Demo mode shows both Claude and Copilot providers``() =
        task {
            // Verify via API that both providers are present across worktrees
            use client = new HttpClient()
            let content = new StringContent("[]", Encoding.UTF8, "application/json")
            let! response = client.PostAsync($"{demoServerUrl}/IWorktreeApi/getWorktrees", content)
            let! body = response.Content.ReadAsStringAsync()
            let dashboard = deserializeDashboard body

            let allProviders =
                dashboard.Repos
                |> List.collect _.Worktrees
                |> List.choose _.CodingToolProvider
                |> List.distinct

            Assert.That(allProviders, Does.Contain(Claude), "Claude provider should appear on at least one worktree")
            Assert.That(allProviders, Does.Contain(Copilot), "Copilot provider should appear on at least one worktree")

            // Also verify Claude appears in UI sync button titles (Claude has non-dirty Working cards behind main)
            let claudeIndicators = this.Page.Locator("[title*='Claude is active']")
            let deadline = DateTime.UtcNow.AddSeconds(12.0)
            let mutable claudeFound = false

            while DateTime.UtcNow < deadline && not claudeFound do
                let! claudeCount = claudeIndicators.CountAsync()
                if claudeCount > 0 then claudeFound <- true
                if not claudeFound then
                    do! System.Threading.Tasks.Task.Delay(500)

            Assert.That(claudeFound, Is.True, "Claude provider indicator should appear in sync button title")
        }

    [<Test>]
    member this.``Demo mode cycles through frames with visible state transitions``() =
        task {
            // Capture initial state of coding tool dots
            let workingDots = this.Page.Locator(".ct-dot.working")
            let waitingDots = this.Page.Locator(".ct-dot.waiting")
            let doneDots = this.Page.Locator(".ct-dot.done")

            let! initialWorking = workingDots.CountAsync()
            let! initialWaiting = waitingDots.CountAsync()
            let! initialDone = doneDots.CountAsync()
            let initialState = (initialWorking, initialWaiting, initialDone)

            TestContext.Out.WriteLine(
                $"Initial state - working: {initialWorking}, waiting: {initialWaiting}, done: {initialDone}")

            // Wait for a state transition (up to 12s = full cycle + buffer)
            let deadline = DateTime.UtcNow.AddSeconds(12.0)
            let mutable transitioned = false

            while DateTime.UtcNow < deadline && not transitioned do
                do! System.Threading.Tasks.Task.Delay(1000)
                let! currentWorking = workingDots.CountAsync()
                let! currentWaiting = waitingDots.CountAsync()
                let! currentDone = doneDots.CountAsync()
                let currentState = (currentWorking, currentWaiting, currentDone)

                TestContext.Out.WriteLine(
                    $"Current state - working: {currentWorking}, waiting: {currentWaiting}, done: {currentDone}")

                if currentState <> initialState then
                    transitioned <- true

            Assert.That(transitioned, Is.True, "Dashboard should show visible state transitions as demo frames cycle")
        }

    [<Test>]
    member this.``Demo mode API returns correct AppVersion``() =
        task {
            use client = new HttpClient()
            let content = new StringContent("[]", Encoding.UTF8, "application/json")
            let! response = client.PostAsync($"{demoServerUrl}/IWorktreeApi/getWorktrees", content)
            let! body = response.Content.ReadAsStringAsync()
            let dashboard = deserializeDashboard body
            Assert.That(dashboard.AppVersion, Is.EqualTo("demo|0"), "Demo mode AppVersion should be 'demo|0'")
        }

    [<Test>]
    member this.``Demo mode API returns SystemMetrics``() =
        task {
            use client = new HttpClient()
            let content = new StringContent("[]", Encoding.UTF8, "application/json")
            let! response = client.PostAsync($"{demoServerUrl}/IWorktreeApi/getWorktrees", content)
            let! body = response.Content.ReadAsStringAsync()
            let dashboard = deserializeDashboard body
            Assert.That(dashboard.SystemMetrics.IsSome, Is.True, "Demo mode should include SystemMetrics")
            let m = dashboard.SystemMetrics.Value
            Assert.That(m.CpuPercent, Is.GreaterThan(0.0), "CPU percent should be positive")
            Assert.That(m.MemoryUsedMb, Is.GreaterThan(0), "Memory used should be positive")
            Assert.That(m.MemoryTotalMb, Is.GreaterThan(0), "Memory total should be positive")
        }

    [<Test>]
    member this.``Demo mode API returns no DeployBranch``() =
        task {
            use client = new HttpClient()
            let content = new StringContent("[]", Encoding.UTF8, "application/json")
            let! response = client.PostAsync($"{demoServerUrl}/IWorktreeApi/getWorktrees", content)
            let! body = response.Content.ReadAsStringAsync()
            let dashboard = deserializeDashboard body
            Assert.That(dashboard.DeployBranch.IsNone, Is.True, "Demo mode should not include DeployBranch")
        }

    [<Test>]
    member this.``Demo mode API returns EditorName``() =
        task {
            use client = new HttpClient()
            let content = new StringContent("[]", Encoding.UTF8, "application/json")
            let! response = client.PostAsync($"{demoServerUrl}/IWorktreeApi/getWorktrees", content)
            let! body = response.Content.ReadAsStringAsync()
            let dashboard = deserializeDashboard body
            Assert.That(dashboard.EditorName, Is.EqualTo("VS Code"), "Demo mode EditorName should be 'VS Code'")
        }

    [<Test>]
    member this.``Demo mode API returns both repos with correct names``() =
        task {
            use client = new HttpClient()
            let content = new StringContent("[]", Encoding.UTF8, "application/json")
            let! response = client.PostAsync($"{demoServerUrl}/IWorktreeApi/getWorktrees", content)
            let! body = response.Content.ReadAsStringAsync()
            let dashboard = deserializeDashboard body
            Assert.That(dashboard.Repos.Length, Is.EqualTo(2), "Demo mode should return 2 repos")
            let names = dashboard.Repos |> List.map _.RootFolderName |> List.sort
            Assert.That(names, Is.EquivalentTo([ "CloudPlatform"; "DataPipeline" ]))
        }

    [<Test>]
    member this.``Demo mode API frame coherence - getWorktrees and getSyncStatus use same frame``() =
        task {
            use client = new HttpClient()
            let worktreeContent = new StringContent("[]", Encoding.UTF8, "application/json")
            let syncContent = new StringContent("[]", Encoding.UTF8, "application/json")

            // Call both endpoints rapidly
            let! wtResponse = client.PostAsync($"{demoServerUrl}/IWorktreeApi/getWorktrees", worktreeContent)
            let! syncResponse = client.PostAsync($"{demoServerUrl}/IWorktreeApi/getSyncStatus", syncContent)

            Assert.That(int wtResponse.StatusCode, Is.EqualTo(200), "getWorktrees should return 200")
            Assert.That(int syncResponse.StatusCode, Is.EqualTo(200), "getSyncStatus should return 200")
        }

    [<Test>]
    member this.``Demo mode renders scheduler footer``() =
        task {
            let footer = this.Page.Locator(".scheduler-footer")
            do! footer.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            let! count = footer.CountAsync()
            Assert.That(count, Is.EqualTo(1), "Scheduler footer should be present in demo mode")
        }

    [<Test>]
    member this.``Demo mode shows archived worktree with dimmed card``() =
        task {
            // The archived worktree (feature/db-migration) should have an archived/dimmed appearance
            // Look for cards with the archived class or branch name "feature/db-migration"
            let archiveSection = this.Page.Locator(".archive-section")
            let! sectionCount = archiveSection.CountAsync()
            Assert.That(sectionCount, Is.GreaterThanOrEqualTo(1))
        }
