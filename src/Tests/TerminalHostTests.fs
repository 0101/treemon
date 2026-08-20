module Tests.TerminalHostTests

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Net.Http.Json
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open NUnit.Framework
open TerminalHost
open Tests.GitTestHelpers
open Tests.TestUtils

let private getTask (task: Task<'a>) =
    task.GetAwaiter().GetResult()

let private waitUntil timeout predicate =
    let deadline = DateTimeOffset.UtcNow + timeout

    let rec wait () =
        if predicate () then
            true
        elif DateTimeOffset.UtcNow >= deadline then
            false
        else
            Thread.Sleep 50
            wait ()

    wait ()

let private killExactPidFromFile path =
    if File.Exists path then
        match Int32.TryParse(File.ReadAllText(path).Trim()) with
        | true, pid ->
            try
                use owned = Process.GetProcessById pid

                if not owned.HasExited then
                    owned.Kill(entireProcessTree = true)
                    owned.WaitForExit()
            with :? ArgumentException ->
                ()
        | false, _ -> ()

let private requireOk result =
    match result with
    | Ok value -> value
    | Error error ->
        Assert.Fail(error)
        Unchecked.defaultof<_>

let private executableOnPath name =
    Environment.GetEnvironmentVariable("PATH")
    |> Option.ofObj
    |> Option.map _.Split(Path.PathSeparator)
    |> Option.defaultValue Array.empty
    |> Array.map (fun directory -> Path.Combine(directory, name))
    |> Array.tryFind File.Exists
    |> Option.defaultWith (fun () ->
        Assert.Fail($"Could not find {name} on PATH")
        "")

let private responseDocument (response: HttpResponseMessage) =
    response.Content.ReadAsStringAsync()
    |> getTask
    |> JsonDocument.Parse

let private terminalIds (document: JsonDocument) =
    document.RootElement.GetProperty("terminals").EnumerateArray()
    |> Seq.map (fun terminal -> terminal.GetProperty("sessionId").GetString())
    |> Seq.choose Option.ofObj
    |> Seq.toList

type private ApiFixture() =
    let root = uniquePath "terminal-host-api"
    let worktree = Path.Combine(root, "repo")
    let starts = ConcurrentQueue<string>()
    let closes = ConcurrentQueue<string>()
    let token = "test-token-with-fixed-value"

    do
        Directory.CreateDirectory root |> ignore
        initRepo worktree

    let starter sessionId canonicalWorktree =
        async {
            starts.Enqueue sessionId

            return
                Ok
                    { ProcessId = 20_001 + starts.Count
                      ProcessStartTimeUtcTicks = int64 (30_001 + starts.Count)
                      TtydPort = 40_001 + starts.Count
                      HasExited = fun () -> false
                      Close = fun () -> closes.Enqueue sessionId }
        }

    let registry = TerminalRegistry.create starter

    let running =
        ControlApi.start
            { Port = 0
              AllowedOrigins = [ "http://localhost:5174" ] }
            token
            12_345
            638_900_000_000_000_000L
            "test-version"
            registry
        |> getTask

    let client = new HttpClient(BaseAddress = Uri running.Endpoint)

    do
        client.DefaultRequestHeaders.Authorization <-
            AuthenticationHeaderValue("Bearer", token)

    member _.Client = client
    member _.Endpoint = running.Endpoint
    member _.Registry = registry
    member _.Running = running
    member _.StartCount = starts.Count
    member _.CloseCount = closes.Count
    member _.Token = token
    member _.Worktree = worktree
    member _.UnknownDirectory = root

    interface IDisposable with
        member _.Dispose() =
            client.Dispose()
            TerminalRegistry.shutdown registry |> Async.RunSynchronously
            ControlApi.stop running |> getTask

            try
                Directory.Delete(root, recursive = true)
            with _ ->
                ()

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
[<Category("TerminalHost")>]
type TerminalHostControlApiTests() =
    [<Test>]
    member _.``start reuses one stable session and close returns the authoritative list``() =
        task {
            use fixture = new ApiFixture()

            use! health = fixture.Client.GetAsync("/api/v1/health")
            use healthDocument = responseDocument health

            Assert.Multiple(fun () ->
                Assert.That(health.StatusCode, Is.EqualTo(HttpStatusCode.OK))
                Assert.That(
                    healthDocument.RootElement.GetProperty("pid").GetInt32(),
                    Is.EqualTo(12_345)
                )

                Assert.That(
                    healthDocument.RootElement.GetProperty("controlApiVersion").GetInt32(),
                    Is.EqualTo(Protocol.ControlApiVersion)
                ))

            use! first =
                fixture.Client.PostAsJsonAsync(
                    "/api/v1/terminals",
                    {| worktreePath = fixture.Worktree |}
                )

            Assert.That(first.StatusCode, Is.EqualTo(HttpStatusCode.OK))
            use firstDocument = responseDocument first
            let firstIds = terminalIds firstDocument
            Assert.That(List.length firstIds, Is.EqualTo(1))
            let sessionId = firstIds.Head

            use! reused =
                fixture.Client.PostAsJsonAsync(
                    "/api/v1/terminals",
                    {| worktreePath = Path.Combine(fixture.Worktree, ".") |}
                )

            use reusedDocument = responseDocument reused

            Assert.Multiple(fun () ->
                Assert.That(reused.StatusCode, Is.EqualTo(HttpStatusCode.OK))
                Assert.That(terminalIds reusedDocument, Is.EqualTo([ sessionId ]))
                Assert.That(fixture.StartCount, Is.EqualTo(1)))

            use! listed = fixture.Client.GetAsync("/api/v1/terminals")
            use listDocument = responseDocument listed

            Assert.Multiple(fun () ->
                Assert.That(listed.StatusCode, Is.EqualTo(HttpStatusCode.OK))
                Assert.That(terminalIds listDocument, Is.EqualTo([ sessionId ])))

            use! closed =
                fixture.Client.DeleteAsync($"/api/v1/terminals/{sessionId}")

            use closeDocument = responseDocument closed

            Assert.Multiple(fun () ->
                Assert.That(closed.StatusCode, Is.EqualTo(HttpStatusCode.OK))
                Assert.That(terminalIds closeDocument, Is.Empty)
                Assert.That(fixture.CloseCount, Is.EqualTo(1))
                Assert.That(
                    closeDocument.RootElement.GetProperty("revision").GetInt64(),
                    Is.EqualTo(2L)
                ))
        }
        :> Task

    [<Test>]
    member _.``control validation rejects untrusted malformed and unknown requests before lifecycle``() =
        task {
            use fixture = new ApiFixture()
            use unauthenticated = new HttpClient(BaseAddress = Uri fixture.Endpoint)
            use! missingToken = unauthenticated.GetAsync("/api/v1/health")
            Assert.That(missingToken.StatusCode, Is.EqualTo(HttpStatusCode.Unauthorized))

            use wrongHost = new HttpRequestMessage(HttpMethod.Get, "/api/v1/health")
            wrongHost.Headers.Host <- $"localhost:{Uri(fixture.Endpoint).Port}"
            use! hostRejected = fixture.Client.SendAsync wrongHost
            Assert.That(hostRejected.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            use wrongOrigin = new HttpRequestMessage(HttpMethod.Get, "/api/v1/health")
            wrongOrigin.Headers.Add("Origin", "http://attacker.example")
            use! originRejected = fixture.Client.SendAsync wrongOrigin
            Assert.That(originRejected.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden))

            use allowedOrigin = new HttpRequestMessage(HttpMethod.Get, "/api/v1/health")
            allowedOrigin.Headers.Add("Origin", "http://localhost:5174")
            use! originAccepted = fixture.Client.SendAsync allowedOrigin
            Assert.That(originAccepted.StatusCode, Is.EqualTo(HttpStatusCode.OK))

            use malformed =
                new StringContent("{\"worktreePath\":", Encoding.UTF8, "application/json")

            use! malformedRejected =
                fixture.Client.PostAsync("/api/v1/terminals", malformed)

            Assert.That(malformedRejected.StatusCode, Is.EqualTo(HttpStatusCode.BadRequest))

            use! unknownRejected =
                fixture.Client.PostAsJsonAsync(
                    "/api/v1/terminals",
                    {| worktreePath = fixture.UnknownDirectory |}
                )

            Assert.That(unknownRejected.StatusCode, Is.EqualTo(HttpStatusCode.NotFound))

            use oversized =
                new StringContent(
                    String('x', int Protocol.MaximumRequestBodyBytes + 1),
                    Encoding.UTF8,
                    "application/json"
                )

            use! oversizedRejected =
                fixture.Client.PostAsync("/api/v1/terminals", oversized)

            Assert.That(
                oversizedRejected.StatusCode,
                Is.EqualTo(HttpStatusCode.RequestEntityTooLarge)
            )

            use! extraEndpoint = fixture.Client.GetAsync("/api/v1/version")
            Assert.That(extraEndpoint.StatusCode, Is.EqualTo(HttpStatusCode.NotFound))
            Assert.That(fixture.StartCount, Is.Zero)
        }
        :> Task

    [<Test>]
    member _.``shutdown is authenticated and stops the control host``() =
        task {
            use fixture = new ApiFixture()
            use emptyBody = new ByteArrayContent(Array.empty)
            use! response = fixture.Client.PostAsync("/api/v1/shutdown", emptyBody)
            Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.Accepted))

            let shutdown = ControlApi.waitForShutdown fixture.Running
            let! completed = Task.WhenAny(shutdown, Task.Delay 5_000)
            Assert.That(completed, Is.SameAs(shutdown))
        }
        :> Task

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
[<Category("TerminalHost")>]
type TerminalHostSecurityTests() =
    [<Test>]
    member _.``non-loopback peer is rejected even with valid host origin and token``() =
        let metadata =
            { RemoteAddress = Some(IPAddress.Parse "192.168.1.10")
              LocalAddress = Some IPAddress.Loopback
              LocalPort = 32_123
              HostHeaders = [ "127.0.0.1:32123" ]
              OriginHeaders = [ "http://127.0.0.1:32123" ]
              AuthorizationHeaders = [ "Bearer expected-token" ]
              ContentLength = None }

        match RequestSecurity.validate [] "expected-token" metadata with
        | Error RequestRejection.Forbidden -> ()
        | result -> Assert.Fail($"Expected non-loopback rejection, got {result}")

    [<Test>]
    member _.``terminal launch specification injects the stable terminal session ID``() =
        let worktreePath =
            Path.Combine(Path.GetTempPath(), "fixture-worktree")
            |> Path.GetFullPath

        let worktree =
            CanonicalWorktree.create
                worktreePath
                "fixture-key"

        let specification =
            TerminalLauncher.startSpecification
                { TtydExecutable = "ttyd.exe"
                  ShellCommand = "pwsh"
                  StartupTimeout = TimeSpan.FromSeconds 1.0 }
                "stable-session-id"
                worktree
                31_234

        Assert.Multiple(fun () ->
            Assert.That(
                specification.Environment,
                Does.Contain(("TREEMON_TERMINAL_SESSION_ID", "stable-session-id"))
            )

            Assert.That(specification.Arguments, Does.Contain("127.0.0.1"))
            Assert.That(specification.Arguments, Does.Contain(worktreePath)))

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
[<Category("TerminalHost")>]
type TerminalHostManifestTests() =
    [<Test>]
    member _.``manifest contains only discovery identity token versions and staged executable``() =
        withTempDir "terminal-host-manifest" (fun root ->
            let staging = Path.Combine(root, "staged")
            let staged = Path.Combine(staging, "2.4.6")
            Directory.CreateDirectory staged |> ignore

            let executableName =
                if OperatingSystem.IsWindows() then
                    "TerminalHost.exe"
                else
                    "TerminalHost"

            File.WriteAllText(Path.Combine(staged, executableName), "fixture")

            let identity =
                { Pid = 12_345
                  ProcessStartTimeUtcTicks = 638_900_000_000_000_000L
                  Endpoint = "http://127.0.0.1:32123"
                  HostVersion = "1.2.3"
                  ControlApiVersion = 1 }

            let stagedVersion =
                Manifest.readStagedExecutableVersion staging

            Manifest.write
                root
                { Identity = identity
                  BearerToken = "secret-token"
                  StagedExecutableVersion = stagedVersion }
            |> requireOk

            use document = JsonDocument.Parse(File.ReadAllBytes(Manifest.path root))

            let properties =
                document.RootElement.EnumerateObject()
                |> Seq.map _.Name
                |> Set.ofSeq

            Assert.Multiple(fun () ->
                Assert.That(
                    properties,
                    Is.EqualTo(
                        set
                            [ "pid"
                              "processStartTimeUtcTicks"
                              "endpoint"
                              "bearerToken"
                              "hostVersion"
                              "controlApiVersion"
                              "stagedExecutableVersion" ]
                    )
                )

                Assert.That(
                    document.RootElement.GetProperty("stagedExecutableVersion").GetString(),
                    Is.EqualTo("2.4.6")
                )

                Assert.That(
                    document.RootElement.GetProperty("bearerToken").GetString(),
                    Is.EqualTo("secret-token")
                ))

            use cancellation = new CancellationTokenSource()

            let monitor =
                Manifest.monitor
                    root
                    staging
                    identity
                    "secret-token"
                    stagedVersion
                    cancellation.Token

            Directory.Delete(staged, recursive = true)

            let stagedVersionRemoved =
                waitUntil (TimeSpan.FromSeconds 3.0) (fun () ->
                    try
                        use updated =
                            JsonDocument.Parse(File.ReadAllBytes(Manifest.path root))

                        updated.RootElement.EnumerateObject()
                        |> Seq.exists (fun property ->
                            property.Name = "stagedExecutableVersion")
                        |> not
                    with _ ->
                        false)

            cancellation.Cancel()
            monitor |> getTask
            Assert.That(stagedVersionRemoved, Is.True)

            Manifest.removeIfOwned root identity
            Assert.That(File.Exists(Manifest.path root), Is.False))

[<TestFixture>]
[<Category("Unit")>]
[<Category("TerminalHost")>]
[<Platform("Win")>]
type TerminalHostJobObjectTests() =
    let powershell = executableOnPath "pwsh.exe"

    [<Test>]
    member _.``closing one retained Job Object kills its exact ttyd process tree``() =
        withTempDir "terminal-host-job-close" (fun root ->
            let pidFile = Path.Combine(root, "child.pid")
            let descendantPidFile = Path.Combine(root, "descendant.pid")
            let environmentFile = Path.Combine(root, "session.txt")
            let sessionId = $"terminal-{Guid.NewGuid():N}"

            let owned =
                JobProcess.start
                    { Executable = powershell
                      Arguments =
                        [ "-NoLogo"
                          "-NoProfile"
                          "-NonInteractive"
                          "-Command"
                          "$descendant = Start-Process -FilePath $env:TM_POWERSHELL -ArgumentList @('-NoLogo','-NoProfile','-NonInteractive','-Command','Start-Sleep -Seconds 300') -PassThru; $PID | Set-Content -LiteralPath $env:TM_PID_FILE; $descendant.Id | Set-Content -LiteralPath $env:TM_DESCENDANT_PID_FILE; $env:TREEMON_TERMINAL_SESSION_ID | Set-Content -LiteralPath $env:TM_SESSION_FILE; Start-Sleep -Seconds 300" ]
                      WorkingDirectory = root
                      Environment =
                        [ "TM_POWERSHELL", powershell
                          "TM_PID_FILE", pidFile
                          "TM_DESCENDANT_PID_FILE", descendantPidFile
                          "TM_SESSION_FILE", environmentFile
                          "TREEMON_TERMINAL_SESSION_ID", sessionId ] }
                |> requireOk

            try
                Assert.That(
                    waitUntil (TimeSpan.FromSeconds 10.0) (fun () ->
                        File.Exists pidFile
                        && File.Exists descendantPidFile
                        && File.Exists environmentFile),
                    Is.True,
                    "owned child did not start"
                )

                let childPid = File.ReadAllText(pidFile).Trim() |> int
                let descendantPid =
                    File.ReadAllText(descendantPidFile).Trim() |> int

                use child = Process.GetProcessById childPid
                use descendant = Process.GetProcessById descendantPid

                Assert.Multiple(fun () ->
                    Assert.That(childPid, Is.EqualTo(JobProcess.processId owned))
                    Assert.That(File.ReadAllText(environmentFile).Trim(), Is.EqualTo(sessionId)))

                JobProcess.close owned

                Assert.Multiple(fun () ->
                    Assert.That(child.WaitForExit 5_000, Is.True, "Job Object close did not kill ttyd")

                    Assert.That(
                        descendant.WaitForExit 5_000,
                        Is.True,
                        "Job Object close did not kill the ttyd process tree"
                    ))
            finally
                JobProcess.close owned
                killExactPidFromFile descendantPidFile)

    [<Test>]
    member _.``terminating the host process closes the Job Object and kills ttyd``() =
        withTempDir "terminal-host-job-exit" (fun root ->
            let readyFile = Path.Combine(root, "ready.pid")
            let errorFile = Path.Combine(root, "error.txt")
            let scriptPath = Path.Combine(root, "owner.fsx")
            let assemblyPath = typeof<JobProcessStart>.Assembly.Location

            let verbatim (value: string) =
                value.Replace("\"", "\"\"")

            let script =
                $"""#r @"{verbatim assemblyPath}"
open System
open System.IO
open System.Threading
open TerminalHost

let specification: JobProcessStart =
    {{ Executable = @"{verbatim powershell}"
      Arguments = [ "-NoLogo"; "-NoProfile"; "-NonInteractive"; "-Command"; "Start-Sleep -Seconds 300" ]
      WorkingDirectory = @"{verbatim root}"
      Environment = [] }}

match JobProcess.start specification with
| Error error ->
    File.WriteAllText(@"{verbatim errorFile}", error)
    Environment.Exit 2
| Ok owned ->
    File.WriteAllText(@"{verbatim readyFile}", string (JobProcess.processId owned))
    let rec keepAlive () =
        Thread.Sleep 250
        GC.KeepAlive owned
        keepAlive ()
    keepAlive ()
"""

            File.WriteAllText(scriptPath, script)

            let startInfo =
                ProcessStartInfo(
                    FileName = "dotnet",
                    WorkingDirectory = root,
                    UseShellExecute = false,
                    RedirectStandardError = true,
                    CreateNoWindow = true
                )

            [ "fsi"; "--nologo"; "--exec"; scriptPath ]
            |> List.iter startInfo.ArgumentList.Add

            use owner = Process.Start startInfo

            try
                Assert.That(
                    waitUntil (TimeSpan.FromSeconds 15.0) (fun () ->
                        File.Exists readyFile || File.Exists errorFile || owner.HasExited),
                    Is.True,
                    "fixture owner did not publish its child PID"
                )

                if File.Exists errorFile then
                    Assert.Fail(File.ReadAllText errorFile)

                if owner.HasExited then
                    Assert.Fail(owner.StandardError.ReadToEnd())

                let childPid = File.ReadAllText(readyFile).Trim() |> int
                use child = Process.GetProcessById childPid

                owner.Kill()
                Assert.That(owner.WaitForExit 5_000, Is.True, "fixture owner did not exit")

                Assert.That(
                    child.WaitForExit 5_000,
                    Is.True,
                    "ttyd survived the process that owned its Job Object"
                )
            finally
                if not owner.HasExited then
                    owner.Kill()
                    owner.WaitForExit()

                killExactPidFromFile readyFile)
