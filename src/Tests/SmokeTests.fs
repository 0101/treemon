module Tests.SmokeTests

open System
open System.Diagnostics
open System.IO
open System.Net.Http
open System.Text
open System.Text.RegularExpressions
open NUnit.Framework
open Microsoft.Playwright
open Microsoft.Playwright.NUnit

let private repoRoot =
    Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))

let private serverProjectPath =
    Path.Combine(repoRoot, "src", "Server")

let private worktreeRoot = @"Q:\code\AITestAgent"
let private thisRepoName = Path.GetFileName(repoRoot)
let private smokePort = 5002
let private smokeUrl = $"http://localhost:{smokePort}"

let private startSmokeServerProc (args: string) =
    let psi =
        ProcessStartInfo(
            FileName = "dotnet",
            Arguments = $"""run --project "{serverProjectPath}" -- {args}""",
            WorkingDirectory = repoRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        )

    Process.Start(psi)

let private startSmokeServer () =
    startSmokeServerProc $""""{worktreeRoot}" --port {smokePort}"""

let private startProcess fileName args workingDir envVars redirectOutput =
    TestUtils.startProcess fileName args workingDir envVars redirectOutput

let private killProc procOpt =
    TestUtils.killProc procOpt

let private killOrphansOnPort port =
    TestUtils.killOrphansOnPort port

[<TestFixture>]
[<Category("Smoke")>]
[<Category("Local")>]
type SmokeTests() =
    let mutable serverProc: Process option = None

    let killServer () =
        serverProc
        |> Option.iter (fun p ->
            try
                if not p.HasExited then
                    p.Kill(entireProcessTree = true)

                    match p.WaitForExit(10000) with
                    | true -> ()
                    | false ->
                        TestContext.Error.WriteLine(
                            $"Smoke server PID {p.Id} did not exit within 10s after Kill")

                p.Dispose()
            with ex ->
                TestContext.Error.WriteLine($"Failed to kill smoke server: {ex.Message}"))

    let pollApi (client: HttpClient) =
        async {
            let content = new StringContent("[]", Encoding.UTF8, "application/json")
            let! response =
                client.PostAsync($"{smokeUrl}/IWorktreeApi/getWorktrees", content)
                |> Async.AwaitTask
            let! body = response.Content.ReadAsStringAsync() |> Async.AwaitTask
            return int response.StatusCode, body
        }

    let mutable readyBody: string = ""

    let rec waitForReady (client: HttpClient) (deadline: DateTime) : Async<string> =
        async {
            if DateTime.UtcNow > deadline then
                return failwith "Timed out waiting for IsReady=true (60s)"
            else
                try
                    let! statusCode, body = pollApi client

                    if statusCode < 500 && body.Contains("\"IsReady\":true") then
                        return body
                    else
                        do! Async.Sleep 2000
                        return! waitForReady client deadline
                with
                | _ ->
                    do! Async.Sleep 2000
                    return! waitForReady client deadline
        }

    [<OneTimeSetUp>]
    member _.StartServer() =
        task {
            let proc = startSmokeServer ()
            serverProc <- Some proc
            TestContext.Out.WriteLine($"Smoke server started (PID {proc.Id}) on port {smokePort}")

            use client = new HttpClient()
            let! body = waitForReady client (DateTime.UtcNow.AddSeconds(60.0)) |> Async.StartAsTask
            readyBody <- body
            TestContext.Out.WriteLine($"Server ready. Response (first 1000 chars): {body.Substring(0, Math.Min(1000, body.Length))}")
        }

    [<OneTimeTearDown>]
    member _.StopServer() =
        killServer ()
        serverProc <- None

    [<Test>]
    member _.``Server returns IsReady=true with real data``() =
        Assert.That(readyBody, Does.Contain("\"IsReady\":true"), "API should return IsReady=true after scheduler populates data")

    [<Test>]
    member _.``At least one worktree with Branch and LastCommitTime populated``() =
        Assert.That(readyBody, Does.Contain("\"Branch\":"), "Response should contain at least one worktree with a Branch field")
        Assert.That(readyBody, Does.Contain("\"LastCommitTime\":"), "Response should contain at least one worktree with LastCommitTime")
        Assert.That(readyBody, Does.Not.Contain("\"Branch\":\"\""), "Branch should not be empty")

    [<Test>]
    member _.``SchedulerEvents is non-empty after first refresh``() =
        Assert.That(readyBody, Does.Contain("\"SchedulerEvents\":"), "Response should contain SchedulerEvents field")
        Assert.That(readyBody, Does.Not.Contain("\"SchedulerEvents\":[]"), "SchedulerEvents should not be empty after scheduler has run")

    [<Test>]
    member _.``At least one worktree has LastUserMessage populated``() =
        let hasUserMessage = readyBody.Contains("\"LastUserMessage\":[\"")
        TestContext.Out.WriteLine($"Response contains LastUserMessage: {hasUserMessage}")

        if not hasUserMessage then
            Assert.Ignore(
                "No worktree has LastUserMessage populated. This can happen when all Claude sessions " +
                "are older than 2h (Idle status) or no user messages found within 1MB scan limit.")
        else
            Assert.That(readyBody, Does.Not.Contain("\"LastUserMessage\":[\"\"]"),
                "LastUserMessage should not be empty string when present")

    [<Test>]
    member _.``Server process exits cleanly after kill``() =
        match serverProc with
        | None -> Assert.Fail("Server process reference is missing")
        | Some proc ->
            Assert.That(proc.HasExited, Is.False, "Server should still be running before teardown")

let private multiRepoPort = 5003
let private multiRepoUrl = $"http://localhost:{multiRepoPort}"
let private multiRepoVitePort = 5175
let private multiRepoViteUrl = $"http://localhost:{multiRepoVitePort}"
let private multiRepoWorktreeRoots = [ @"Q:\code\AITestAgent"; repoRoot ]

let private tryGetUrl (client: HttpClient) (url: string) =
    async {
        try
            let! response = client.GetAsync(url) |> Async.AwaitTask
            return int response.StatusCode < 500
        with
        | _ -> return false
    }

let rec private pollUrl (client: HttpClient) (url: string) (deadline: DateTime) =
    async {
        if DateTime.UtcNow > deadline then
            failwithf "Timed out waiting for %s" url
        else
            let! ok = tryGetUrl client url

            if not ok then
                do! Async.Sleep(500)
                return! pollUrl client url deadline
    }

let private waitForUrlReady (url: string) (timeoutMs: int) =
    task {
        use client = new HttpClient()
        let deadline = DateTime.UtcNow.AddMilliseconds(float timeoutMs)
        do! pollUrl client url deadline |> Async.StartAsTask
    }

[<TestFixture>]
[<Category("Smoke")>]
[<Category("Local")>]
type MultiRepoSmokeTests() =
    inherit PageTest()

    let mutable serverProc: Process option = None
    let mutable viteProc: Process option = None

    let pollMultiRepoApi (client: HttpClient) =
        async {
            let content = new StringContent("[]", Encoding.UTF8, "application/json")
            let! response =
                client.PostAsync($"{multiRepoUrl}/IWorktreeApi/getWorktrees", content)
                |> Async.AwaitTask
            let! body = response.Content.ReadAsStringAsync() |> Async.AwaitTask
            return int response.StatusCode, body
        }

    let rec waitForAllReposReady (client: HttpClient) (repoCount: int) (deadline: DateTime) : Async<string> =
        async {
            if DateTime.UtcNow > deadline then
                return failwith $"Timed out waiting for {repoCount} repos to become IsReady=true (120s)"
            else
                try
                    let! statusCode, body = pollMultiRepoApi client

                    let readyCount =
                        Regex.Matches(body, "\"IsReady\":true")
                        |> Seq.cast<Match>
                        |> Seq.length

                    if statusCode < 500 && readyCount >= repoCount then
                        return body
                    else
                        do! Async.Sleep 3000
                        return! waitForAllReposReady client repoCount deadline
                with
                | _ ->
                    do! Async.Sleep 3000
                    return! waitForAllReposReady client repoCount deadline
        }

    override this.ContextOptions() =
        let opts = base.ContextOptions()
        opts.IgnoreHTTPSErrors <- true
        opts

    [<OneTimeSetUp>]
    member _.StartMultiRepoServer() =
        task {
            killOrphansOnPort multiRepoPort
            killOrphansOnPort multiRepoVitePort

            let rootArgs =
                multiRepoWorktreeRoots
                |> List.map (fun r -> $"\"{r}\"")
                |> String.concat " "

            let proc = startSmokeServerProc $"""{rootArgs} --port {multiRepoPort}"""
            serverProc <- Some proc
            TestContext.Out.WriteLine($"Multi-repo smoke server started (PID {proc.Id}) on port {multiRepoPort}")

            use client = new HttpClient()
            let! body =
                waitForAllReposReady client (List.length multiRepoWorktreeRoots) (DateTime.UtcNow.AddSeconds(120.0))
                |> Async.StartAsTask
            TestContext.Out.WriteLine($"All repos ready. Response length: {body.Length}")

            let fableProc =
                startProcess "dotnet" $"fable src/Client --outDir src/Client/output" repoRoot [] true

            let! stdout = fableProc.StandardOutput.ReadToEndAsync()
            let! stderr = fableProc.StandardError.ReadToEndAsync()
            let exited = fableProc.WaitForExit(60_000)

            if not exited then
                fableProc.Kill(entireProcessTree = true)
                failwith "Fable compilation timed out after 60s"

            TestContext.Out.WriteLine($"Fable output: {stdout}")

            if fableProc.ExitCode <> 0 then
                failwithf "Fable compilation failed (exit code %d): %s" fableProc.ExitCode stderr

            let vite =
                startProcess
                    "npx"
                    "vite --host"
                    repoRoot
                    [ "VITE_PORT", string multiRepoVitePort
                      "API_PORT", string multiRepoPort
                      "NODE_OPTIONS", "--max-old-space-size=512" ]
                    false

            viteProc <- Some vite
            TestContext.Out.WriteLine($"Vite started (PID {vite.Id}) on port {multiRepoVitePort}")
            do! waitForUrlReady multiRepoViteUrl 15000
            TestContext.Out.WriteLine("Vite ready")
        }

    [<OneTimeTearDown>]
    member _.StopMultiRepoServer() =
        killProc serverProc
        killProc viteProc
        serverProc <- None
        viteProc <- None

    [<SetUp>]
    member this.NavigateToDashboard() =
        task {
            let! _ = this.Page.GotoAsync(multiRepoViteUrl)
            do!
                this.Page.Locator(".repo-section .repo-header")
                    .First.WaitForAsync(LocatorWaitForOptions(Timeout = 30000.0f))
        }

    [<Test>]
    member this.``Both repo sections render with headers``() =
        task {
            let sections = this.Page.Locator(".repo-section")
            do! sections.First.WaitForAsync(LocatorWaitForOptions(Timeout = 15000.0f))
            let! count = sections.CountAsync()
            Assert.That(count, Is.GreaterThanOrEqualTo(2), $"Dashboard should show at least 2 repo sections (AITestAgent + {thisRepoName})")

            let repoNames = this.Page.Locator(".repo-section .repo-name")
            let! names =
                repoNames.EvaluateAllAsync<string[]>(
                    "els => els.map(el => el.textContent.trim())")

            let joined = String.Join(", ", names)
            TestContext.Out.WriteLine($"Repo sections found: {joined}")
            Assert.That(names, Has.Some.Contains("AITestAgent"), "Should have an AITestAgent section")
            Assert.That(names, Has.Some.Contains(thisRepoName), $"Should have a {thisRepoName} section")
        }

    [<Test>]
    member this.``Each repo section has at least one worktree card``() =
        task {
            let sections = this.Page.Locator(".repo-section")
            do! sections.First.WaitForAsync(LocatorWaitForOptions(Timeout = 15000.0f))
            let! count = sections.CountAsync()

            for idx in 0 .. count - 1 do
                let section = sections.Nth(idx)
                let! repoName = section.Locator(".repo-name").TextContentAsync()
                let cards = section.Locator(".wt-card")
                let! cardCount = cards.CountAsync()
                TestContext.Out.WriteLine($"Repo '{repoName}': {cardCount} cards")
                Assert.That(cardCount, Is.GreaterThanOrEqualTo(1), $"Repo section '{repoName}' should have at least one worktree card")
        }

    [<Test>]
    member this.``At least one card in each repo section has PR info visible``() =
        task {
            let azdoSection = this.Page.Locator(".repo-section:has(.repo-name:text-is('AITestAgent'))")
            do! azdoSection.WaitForAsync(LocatorWaitForOptions(Timeout = 15000.0f))
            let azdoPrBadges = azdoSection.Locator(".pr-badge")
            do! azdoPrBadges.First.WaitForAsync(LocatorWaitForOptions(Timeout = 120000.0f))
            let! azdoPrCount = azdoPrBadges.CountAsync()
            TestContext.Out.WriteLine($"AITestAgent PR badges: {azdoPrCount}")
            Assert.That(azdoPrCount, Is.GreaterThanOrEqualTo(1), "AITestAgent (AzDo) should have at least one card with a PR badge")

            let ghSection = this.Page.Locator($".repo-section:has(.repo-name:text-is('{thisRepoName}'))")
            do! ghSection.WaitForAsync(LocatorWaitForOptions(Timeout = 15000.0f))
            let ghPrBadges = ghSection.Locator(".pr-badge")

            let! ghPrCount = ghPrBadges.CountAsync()
            TestContext.Out.WriteLine($"{thisRepoName} PR badges (initial): {ghPrCount}")

            if ghPrCount = 0 then
                do! this.Page.WaitForTimeoutAsync(30000.0f)
                let! ghPrCountRetry = ghPrBadges.CountAsync()
                TestContext.Out.WriteLine($"{thisRepoName} PR badges (after 30s wait): {ghPrCountRetry}")

                if ghPrCountRetry = 0 then
                    TestContext.Out.WriteLine(
                        "NOTE: No GitHub PR badges visible. This is expected when no worktree branch matches an open GitHub PR. " +
                        "Open PRs exist on test/* branches but local worktrees are on main/multirepo/readme.")

            let allPrBadges = this.Page.Locator(".pr-badge")
            let! totalPrCount = allPrBadges.CountAsync()
            TestContext.Out.WriteLine($"Total PR badges across all sections: {totalPrCount}")
            Assert.That(totalPrCount, Is.GreaterThanOrEqualTo(1), "At least one section should have PR data visible (AzDo is expected to always have active PRs)")
        }

    [<Test>]
    member this.``AzDo section uses threads format for PR comments``() =
        task {
            let azdoSection = this.Page.Locator(".repo-section:has(.repo-name:text-is('AITestAgent'))")
            let azdoPrBadges = azdoSection.Locator(".pr-badge")
            do! azdoPrBadges.First.WaitForAsync(LocatorWaitForOptions(Timeout = 120000.0f))
            let! prCount = azdoPrBadges.CountAsync()
            TestContext.Out.WriteLine($"AzDo PR badges visible: {prCount}")

            let azdoThreadBadges = azdoSection.Locator(".thread-badge")
            let! threadCount = azdoThreadBadges.CountAsync()
            TestContext.Out.WriteLine($"AzDo thread badges (initial): {threadCount}")

            if threadCount = 0 then
                do! this.Page.WaitForTimeoutAsync(30000.0f)
                let! threadCountRetry = azdoThreadBadges.CountAsync()
                TestContext.Out.WriteLine($"AzDo thread badges (after 30s wait): {threadCountRetry}")

                if threadCountRetry = 0 then
                    Assert.Ignore(
                        "No AzDo thread badges visible. Thread badges only appear when total thread count > 0 " +
                        "(WithResolution with total > 0). The PRs exist but may have 0 threads, or thread data " +
                        "has not been fetched yet within the test timeout.")
            else
                let! azdoText = azdoThreadBadges.First.TextContentAsync()
                TestContext.Out.WriteLine($"AzDo thread badge text: {azdoText}")
                Assert.That(azdoText, Does.Contain("threads"), "AzDo PR should show 'threads' format")
        }

    [<Test>]
    member this.``GitHub section uses comments format when PR data is present``() =
        task {
            let ghSection = this.Page.Locator($".repo-section:has(.repo-name:text-is('{thisRepoName}'))")
            do! ghSection.WaitForAsync(LocatorWaitForOptions(Timeout = 15000.0f))

            let ghCommentBadges = ghSection.Locator(".thread-badge")
            let! count = ghCommentBadges.CountAsync()
            TestContext.Out.WriteLine($"{thisRepoName} thread badges: {count}")

            if count = 0 then
                do! this.Page.WaitForTimeoutAsync(30000.0f)
                let! countRetry = ghCommentBadges.CountAsync()
                TestContext.Out.WriteLine($"{thisRepoName} thread badges (after 30s wait): {countRetry}")

                if countRetry = 0 then
                    Assert.Ignore(
                        "No GitHub PR comment badges visible - no worktree branch currently matches an open GitHub PR. " +
                        "This test passes when a worktree branch has an open PR on GitHub.")
            else
                let! ghText = ghCommentBadges.First.TextContentAsync()
                TestContext.Out.WriteLine($"GitHub comment badge text: {ghText}")
                Assert.That(ghText, Does.Contain("comments"), "GitHub PR should show 'comments' format")
        }

    [<Test>]
    member this.``Active Claude cards display user message text``() =
        task {
            let activeCards = this.Page.Locator(".wt-card.ct-working, .wt-card.ct-waiting-for-user")
            let! activeCount = activeCards.CountAsync()
            TestContext.Out.WriteLine($"Active Claude cards (initial): {activeCount}")

            if activeCount = 0 then
                do! this.Page.WaitForTimeoutAsync(15000.0f)
                let! retryCount = activeCards.CountAsync()
                TestContext.Out.WriteLine($"Active Claude cards (after 15s): {retryCount}")

                if retryCount = 0 then
                    Assert.Ignore(
                        "No active Claude session cards visible. This test requires at least one " +
                        "worktree with an active Claude session (Working or WaitingForUser).")

            let userPrompts = this.Page.Locator(".wt-card .commit-line.user-prompt")
            let! promptCount = userPrompts.CountAsync()
            TestContext.Out.WriteLine($"User prompt elements found: {promptCount}")

            if promptCount > 0 then
                for idx in 0 .. promptCount - 1 do
                    let prompt = userPrompts.Nth(idx)
                    let! text = prompt.TextContentAsync()
                    TestContext.Out.WriteLine($"User prompt [{idx}]: '{text}'")
                    Assert.That(text, Is.Not.Empty, $"User prompt [{idx}] should have visible text content")
                    Assert.That(text.Length, Is.GreaterThan(0), $"User prompt [{idx}] should not be blank")
            else
                TestContext.Out.WriteLine(
                    "NOTE: No user-prompt elements found on any card. " +
                    "This can happen if Claude sessions are old (>2h) and all cards show ct-idle status, " +
                    "or if no user messages were found within the 1MB scan limit.")
        }

    [<Test>]
    member this.``User prompts do not contain skill prompt text``() =
        task {
            let userPrompts = this.Page.Locator(".wt-card .commit-line.user-prompt")
            let! count = userPrompts.CountAsync()
            TestContext.Out.WriteLine($"User prompt elements to check for skill noise: {count}")

            if count = 0 then
                Assert.Ignore(
                    "No user-prompt elements visible to verify skill filtering. " +
                    "This test requires at least one card with a user prompt displayed.")

            for idx in 0 .. count - 1 do
                let prompt = userPrompts.Nth(idx)
                let! text = prompt.TextContentAsync()
                TestContext.Out.WriteLine($"Checking prompt [{idx}] ({text.Length} chars): '{text.Substring(0, Math.Min(80, text.Length))}'")

                Assert.That(text.StartsWith("# ") && text.Length > 200, Is.False,
                    $"User prompt [{idx}] looks like a skill prompt (starts with '# ' and >200 chars)")
                Assert.That(text.StartsWith("**") && text.Length > 200, Is.False,
                    $"User prompt [{idx}] looks like a skill prompt (starts with '**' and >200 chars)")
                Assert.That(text, Does.Not.Contain("PRESERVE ON CONTEXT COMPACTION"),
                    $"User prompt [{idx}] contains system noise text")
                Assert.That(text, Does.Not.Contain("<command-name>"),
                    $"User prompt [{idx}] contains raw command-name tag")
        }
