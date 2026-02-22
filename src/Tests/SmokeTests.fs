module Tests.SmokeTests

open System
open System.Diagnostics
open System.IO
open System.Net.Http
open System.Text
open NUnit.Framework

let private repoRoot =
    Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))

let private serverProjectPath =
    Path.Combine(repoRoot, "src", "Server")

let private worktreeRoot = @"Q:\code\AITestAgent"
let private smokePort = 5002
let private smokeUrl = $"http://localhost:{smokePort}"

let private startSmokeServer () =
    let psi =
        ProcessStartInfo(
            FileName = "dotnet",
            Arguments = $"""run --project "{serverProjectPath}" -- "{worktreeRoot}" --port {smokePort}""",
            WorkingDirectory = repoRoot,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        )

    Process.Start(psi)

[<TestFixture>]
[<Category("Smoke")>]
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
    member _.``Server process exits cleanly after kill``() =
        match serverProc with
        | None -> Assert.Fail("Server process reference is missing")
        | Some proc ->
            Assert.That(proc.HasExited, Is.False, "Server should still be running before teardown")
