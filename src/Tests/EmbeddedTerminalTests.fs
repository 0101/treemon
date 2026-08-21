module Tests.EmbeddedTerminalTests

open System
open System.Collections.Concurrent
open System.Diagnostics
open System.IO
open System.Net
open System.Net.Sockets
open System.Text.Json
open System.Text.Json.Nodes
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open global.Microsoft.AspNetCore.Hosting.Server
open global.Microsoft.AspNetCore.Hosting.Server.Features
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open NUnit.Framework
open global.Server
open global.Server.GitWorktree
open global.Server.SchedulerState
open Shared
open Tests.TestUtils

type private FakeTerminal =
    { SessionId: string
      WorktreePath: string
      AttachmentEndpoint: string }

type HealthDto =
    { Pid: int
      ProcessStartTimeUtcTicks: int64
      HostVersion: string
      ControlApiVersion: int }

type TerminalDto =
    { SessionId: string
      WorktreePath: string
      AttachmentEndpoint: string }

type RegistryDto =
    { Revision: int64
      Terminals: TerminalDto list }

type ErrorDto = { Error: string }

type private FakeControlHost() =
    let root = uniquePath "embedded-terminal-client"
    let stateDirectory = Path.Combine(root, "state")
    let token = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFG"
    let gate = obj()
    let oldExecutable = Path.Combine(root, "old", "TerminalHost.exe")

    let currentPid, currentStartTicks =
        use current = Process.GetCurrentProcess()
        current.Id, current.StartTime.ToUniversalTime().Ticks

    // Kestrel may dispatch concurrent requests; mutation is confined to this stateful fake boundary.
    let mutable terminals: FakeTerminal list = []
    let mutable revision = 0L
    let mutable failNextStartResponse = false
    let mutable rejectNextStartResponse = false
    let mutable failNextCloseResponse = false
    let mutable stopped = false
    let mutable logicalShutdown = false
    let mutable online = true
    let mutable hostVersion = "1.0.0-test"
    let mutable stagedVersion: string option = None
    let mutable currentExecutable = oldExecutable
    let mutable registryJsonOverride: string option = None
    let listRequests = ConcurrentQueue<unit>()
    let startRequests = ConcurrentQueue<string>()
    let closeRequests = ConcurrentQueue<string>()
    let shutdownRequests = ConcurrentQueue<unit>()
    let jsonOptions = JsonSerializerOptions(JsonSerializerDefaults.Web)

    do
        Directory.CreateDirectory stateDirectory |> ignore
        Directory.CreateDirectory(Path.GetDirectoryName oldExecutable)
        |> ignore
        File.WriteAllText(oldExecutable, "fake old TerminalHost")

    let snapshot () =
        lock gate (fun () ->
            { Revision = revision
              Terminals =
                terminals
                |> List.map (fun terminal ->
                    { SessionId = terminal.SessionId
                      WorktreePath = terminal.WorktreePath
                      AttachmentEndpoint = terminal.AttachmentEndpoint }) })

    let writeJson status payload (context: HttpContext) =
        task {
            context.Response.StatusCode <- status
            context.Response.ContentType <- "application/json; charset=utf-8"
            let boxed = box payload
            let json =
                JsonSerializer.Serialize(
                    boxed,
                    boxed.GetType(),
                    jsonOptions
                )

            do! context.Response.WriteAsync json
        }

    let authorized (context: HttpContext) =
        context.Request.Headers.Authorization
        |> Seq.toList
        |> function
            | [ value ] -> value = $"Bearer {token}"
            | _ -> false

    let readWorktreePath (context: HttpContext) =
        task {
            use! document = JsonDocument.ParseAsync(context.Request.Body)
            return
                document.RootElement
                    .GetProperty("worktreePath")
                    .GetString()
        }

    let startTerminal path =
        lock gate (fun () ->
            let existing =
                terminals
                |> List.tryFind (fun terminal ->
                    Shared.PathUtils.pathEquals
                        terminal.WorktreePath
                        path)

            match existing with
            | Some _ -> ()
            | None ->
                let sessionId = Guid.NewGuid().ToString("N")

                terminals <-
                    terminals
                    @ [ { SessionId = sessionId
                          WorktreePath = Path.GetFullPath path |> Path.TrimEndingDirectorySeparator
                          AttachmentEndpoint =
                            $"http://127.0.0.1:41001/_treemon/{sessionId}/{token}/" } ]

                revision <- revision + 1L

            let fail = failNextStartResponse
            failNextStartResponse <- false
            fail)

    let closeTerminal sessionId =
        lock gate (fun () ->
            let remaining =
                terminals
                |> List.filter (fun terminal ->
                    terminal.SessionId <> sessionId)

            if remaining.Length <> terminals.Length then
                terminals <- remaining
                revision <- revision + 1L

            let fail = failNextCloseResponse
            failNextCloseResponse <- false
            fail)

    let builder = WebApplication.CreateSlimBuilder()

    do
        builder.Logging.ClearProviders() |> ignore

        builder.WebHost.ConfigureKestrel(fun options ->
            options.AddServerHeader <- false
            options.Listen(IPAddress.Loopback, 0))
        |> ignore

    let application = builder.Build()
    let lifetime = application.Services.GetRequiredService<IHostApplicationLifetime>()

    let handle (context: HttpContext) =
        task {
            if not (authorized context) then
                return!
                    writeJson
                        StatusCodes.Status401Unauthorized
                        { Error = "Authentication required" }
                        context
            elif not (lock gate (fun () -> online)) then
                return!
                    writeJson
                        StatusCodes.Status503ServiceUnavailable
                        { Error = "Host is not running" }
                        context
            else
                let method = context.Request.Method
                let path = context.Request.Path.Value |> Option.ofObj |> Option.defaultValue ""

                match method, path with
                | "GET", "/api/v1/health" ->
                    let version = lock gate (fun () -> hostVersion)

                    return!
                        writeJson
                            StatusCodes.Status200OK
                            { Pid = currentPid
                              ProcessStartTimeUtcTicks = currentStartTicks
                              HostVersion = version
                              ControlApiVersion = 1 }
                            context
                | "GET", "/api/v1/terminals" ->
                    listRequests.Enqueue()

                    match lock gate (fun () -> registryJsonOverride) with
                    | None ->
                        return! writeJson StatusCodes.Status200OK (snapshot ()) context
                    | Some content ->
                        context.Response.StatusCode <- StatusCodes.Status200OK
                        context.Response.ContentType <- "application/json; charset=utf-8"
                        return! context.Response.WriteAsync content
                | "POST", "/api/v1/terminals" ->
                    let! requested = readWorktreePath context

                    match requested |> Option.ofObj with
                    | None ->
                        return!
                            writeJson
                                StatusCodes.Status400BadRequest
                                { Error = "Malformed start request" }
                                context
                    | Some worktreePath ->
                        startRequests.Enqueue worktreePath
                        let reject =
                            lock gate (fun () ->
                                let reject = rejectNextStartResponse
                                rejectNextStartResponse <- false
                                reject)

                        if reject then
                            return!
                                writeJson
                                    StatusCodes.Status400BadRequest
                                    { Error = "Unknown worktree path" }
                                    context
                        else
                            let fail = startTerminal worktreePath

                            if fail then
                                return!
                                    writeJson
                                        StatusCodes.Status503ServiceUnavailable
                                        { Error = "Simulated ambiguous start response" }
                                        context
                            else
                                return! writeJson StatusCodes.Status200OK (snapshot ()) context
                | "DELETE", closePath
                    when closePath.StartsWith(
                        "/api/v1/terminals/",
                        StringComparison.Ordinal
                    ) ->
                    let sessionId =
                        closePath.Substring("/api/v1/terminals/".Length)

                    closeRequests.Enqueue sessionId
                    let fail = closeTerminal sessionId

                    if fail then
                        return!
                            writeJson
                                StatusCodes.Status503ServiceUnavailable
                                { Error = "Simulated ambiguous close response" }
                                context
                    else
                        return! writeJson StatusCodes.Status200OK (snapshot ()) context
                | "POST", "/api/v1/shutdown" ->
                    shutdownRequests.Enqueue()

                    let stopApi =
                        lock gate (fun () ->
                            if logicalShutdown then
                                online <- false
                                terminals <- []
                                revision <- 0L
                                false
                            else
                                true)

                    if stopApi then
                        context.Response.OnCompleted(
                            Func<Task>(fun () ->
                                lifetime.StopApplication()
                                Task.CompletedTask)
                        )
                    else
                        let path = Path.Combine(stateDirectory, "host.json")

                        try
                            File.Delete path
                        with _ ->
                            ()

                    return!
                        writeJson
                            StatusCodes.Status202Accepted
                            {| accepted = true |}
                            context
                | _ ->
                    return!
                        writeJson
                            StatusCodes.Status404NotFound
                            { Error = "Control endpoint not found" }
                            context
        }

    do
        application.Run(
            RequestDelegate(fun context ->
                handle context :> Task)
        )

        application.StartAsync().GetAwaiter().GetResult()

    let endpoint =
        let server = application.Services.GetRequiredService<IServer>()
        let addresses = server.Features.Get<IServerAddressesFeature>().Addresses
        let bound = addresses |> Seq.exactlyOne |> Uri
        $"http://127.0.0.1:{bound.Port}"

    let manifestPath = Path.Combine(stateDirectory, "host.json")

    let createManifest () =
        let version, staged =
            lock gate (fun () -> hostVersion, stagedVersion)

        let manifest = JsonObject()
        manifest["pid"] <- JsonValue.Create currentPid
        manifest["processStartTimeUtcTicks"] <- JsonValue.Create currentStartTicks
        manifest["endpoint"] <- JsonValue.Create endpoint
        manifest["bearerToken"] <- JsonValue.Create token
        manifest["hostVersion"] <- JsonValue.Create version
        manifest["controlApiVersion"] <- JsonValue.Create 1

        staged
        |> Option.iter (fun candidate ->
            manifest["stagedExecutableVersion"] <-
                JsonValue.Create candidate)

        manifest

    member _.Root = root
    member _.StateDirectory = stateDirectory
    member _.Endpoint = endpoint
    member _.Token = token
    member _.ListRequestCount = listRequests.Count
    member _.StartRequestCount = startRequests.Count
    member _.CloseRequestCount = closeRequests.Count
    member _.ShutdownRequestCount = shutdownRequests.Count
    member _.OldExecutable = oldExecutable
    member _.CurrentExecutable = lock gate (fun () -> currentExecutable)
    member _.CurrentHostVersion = lock gate (fun () -> hostVersion)
    member _.IsOnline = lock gate (fun () -> online)

    member _.PublishManifest() =
        let manifest = createManifest ()
        File.WriteAllText(manifestPath, manifest.ToJsonString())

    member _.PublishManifestWithJsonField(fieldName: string, jsonValue: string) =
        let manifest = createManifest ()
        manifest[fieldName] <- JsonNode.Parse jsonValue
        File.WriteAllText(manifestPath, manifest.ToJsonString())

    member _.ReturnRegistryWithJsonField(fieldName: string, jsonValue: string) =
        let registry =
            JsonNode.Parse(
                JsonSerializer.Serialize(snapshot (), jsonOptions)
            )
            |> _.AsObject()

        let terminals = registry["terminals"].AsArray()
        let terminal = terminals[0].AsObject()

        terminal[fieldName] <- JsonNode.Parse jsonValue

        lock gate (fun () ->
            registryJsonOverride <- Some(registry.ToJsonString()))

    member this.Stage(version: string) =
        let directory =
            Path.Combine(stateDirectory, "staged", version)

        Directory.CreateDirectory directory |> ignore
        let executable = Path.Combine(directory, "TerminalHost.exe")
        File.WriteAllText(executable, $"fake staged TerminalHost {version}")
        lock gate (fun () -> stagedVersion <- Some version)
        this.PublishManifest()
        executable

    member _.EnableLogicalReplacement() =
        lock gate (fun () -> logicalShutdown <- true)

    member this.Activate(executablePath: string, version: string) =
        lock gate (fun () ->
            currentExecutable <- Path.GetFullPath executablePath
            hostVersion <- version
            online <- true
            terminals <- []
            revision <- 0L)

        this.PublishManifest()

    member _.ExactProcessIsLive(pid: int, startTicks: int64) =
        Ok(
            pid = currentPid
            && startTicks = currentStartTicks
            && lock gate (fun () -> online)
        )

    member _.ResolveExactProcessExecutable(pid: int, startTicks: int64) =
        if
            pid = currentPid
            && startTicks = currentStartTicks
            && lock gate (fun () -> online)
        then
            Ok(lock gate (fun () -> currentExecutable))
        else
            Error "Fake TerminalHost identity is not live"

    member _.PublishMalformedManifest() =
        File.WriteAllText(
            manifestPath,
            """{"pid":1,"unexpected":"not-a-host"}"""
        )

    member _.FailNextStartResponse() =
        lock gate (fun () -> failNextStartResponse <- true)

    member _.RejectNextStartResponse() =
        lock gate (fun () -> rejectNextStartResponse <- true)

    member _.FailNextCloseResponse() =
        lock gate (fun () -> failNextCloseResponse <- true)

    member _.CurrentTerminals =
        lock gate (fun () -> terminals)

    member _.StopApi() =
        lock gate (fun () ->
            if not stopped then
                application.StopAsync().GetAwaiter().GetResult()
                stopped <- true)

    interface IDisposable with
        member this.Dispose() =
            this.StopApi()
            application.DisposeAsync().AsTask().GetAwaiter().GetResult()

            try
                Directory.Delete(root, recursive = true)
            with _ ->
                ()

let private managerConfig
    (host: FakeControlHost)
    (launchHost: ProcessStartInfo -> Result<unit, string>)
    : TerminalHostProcess.Config =
    let processIdentityMatches pid startTicks =
        try
            use child = Process.GetProcessById pid

            Ok(
                not child.HasExited
                && child.StartTime.ToUniversalTime().Ticks = startTicks
            )
        with
        | :? ArgumentException
        | :? InvalidOperationException ->
            Ok false
        | error -> Error error.Message

    { HostExecutablePath = host.OldExecutable
      HostStateDirectory = host.StateDirectory
      TtydExecutablePath = None
      ShellCommand = "pwsh"
      AllowedOrigins = [ "http://localhost:5174" ]
      StartupTimeout = TimeSpan.FromSeconds 2.0
      ControlRequestTimeout = TimeSpan.FromMilliseconds 500.0
      ProbeInterval = TimeSpan.FromMilliseconds 20.0
      LaunchHost = launchHost
      ProcessIdentityMatches = processIdentityMatches
      ResolveProcessExecutable =
        fun pid startTicks ->
            match processIdentityMatches pid startTicks with
            | Ok true -> Ok host.OldExecutable
            | Ok false -> Error "Fake TerminalHost identity is not live"
            | Error error -> Error error
      SendTerminalCommand =
        fun _ _ ->
            async {
                return
                    Error
                        "The test did not expect a terminal command"
            } }

let private noLaunch (_: ProcessStartInfo) =
    Error "The test did not expect TerminalHost to be launched"

let private replacementManagerConfig
    (host: FakeControlHost)
    launchHost
    sendTerminalCommand
    =
    { managerConfig host launchHost with
        StartupTimeout = TimeSpan.FromSeconds 1.0
        ProcessIdentityMatches =
            fun pid startTicks ->
                host.ExactProcessIsLive(pid, startTicks)
        ResolveProcessExecutable =
            fun pid startTicks ->
                host.ResolveExactProcessExecutable(pid, startTicks)
        SendTerminalCommand = sendTerminalCommand }

let private replacementStoredStatus
    sessionId
    terminalSessionId
    worktreePath
    status
    at
    : SessionActivityStore.StoredStatus =
    { SessionId = SessionActivity.SessionId sessionId
      TerminalSessionId = terminalSessionId
      WorktreePath = worktreePath
      Provider = CopilotCli
      Status =
        { SessionActivity.emptyStatus with
            Status = status }
      UpdatedAt = at
      LastSeen = at
      ContextUsageAt = None }

let private worktree (root: string) (name: string) =
    let path = Path.Combine(root, name)
    Directory.CreateDirectory path |> ignore
    PathUtils.toWorktreePath path

let private requireOk result =
    match result with
    | Ok value -> value
    | Error error ->
        Assert.Fail(error)
        Unchecked.defaultof<_>

let private assertRunningFor
    (expectedPath: WorktreePath)
    (snapshot: EmbeddedTerminalSnapshot)
    =
    let tab =
        snapshot.Tabs
        |> List.find (fun tab ->
            Shared.PathUtils.pathEquals
                (WorktreePath.value tab.Worktree)
                (WorktreePath.value expectedPath))

    match tab.Lifecycle with
    | EmbeddedTerminalLifecycle.Running endpoint ->
        Assert.That(endpoint, Does.StartWith("http://127.0.0.1:41001/_treemon/"))
        endpoint
    | lifecycle ->
        Assert.Fail($"Expected a running terminal, got {lifecycle}")
        ""

let private closeForCleanup
    (manager: EmbeddedTerminal.Manager)
    (path: WorktreePath)
    =
    async {
        match! EmbeddedTerminal.closeStrict manager path with
        | Ok _ -> return Ok()
        | Error error -> return Error error
    }

let private populateAgent
    (agent: MailboxProcessor<StateMsg>)
    (repoId: RepoId)
    (worktrees: WorktreeInfo list)
    =
    async {
        agent.Post(UpdateWorktreeList(repoId, worktrees))
        let! _ = agent.PostAndAsyncReply(GetState)
        return ()
    }

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type EmbeddedTerminalControlClientTests() =
    [<TestCase("http://127.0.0.1:41001/", true)>]
    [<TestCase("http://127.0.0.1:41001/terminal/session/", true)>]
    [<TestCase("http://127.0.0.1:5000/terminal/session/", true)>]
    [<TestCase("https://127.0.0.1:41001/", false)>]
    [<TestCase("http://localhost:41001/", false)>]
    [<TestCase("http://127.0.0.1:0/", false)>]
    [<TestCase("http://127.0.0.1:41001/?token=value", false)>]
    [<TestCase("http://127.0.0.1:41001/#fragment", false)>]
    [<TestCase("http://user@127.0.0.1:41001/", false)>]
    member _.``loopback HTTP validation centralizes the common endpoint shape``
        (
            value: string,
            expected: bool
        ) =
        let endpoint = Uri(value, UriKind.Absolute)

        Assert.That(
            TerminalHostEndpoint.isLoopbackHttpUri endpoint,
            Is.EqualTo expected
        )

    [<TestCase(0x00)>]
    [<TestCase(0x03)>]
    [<TestCase(0x0A)>]
    [<TestCase(0x0D)>]
    [<TestCase(0x15)>]
    [<TestCase(0x1B)>]
    [<TestCase(0x85)>]
    member _.``resume commands containing control characters are rejected before terminal input``
        (characterCode: int)
        =
        task {
            use listener = new TcpListener(IPAddress.Loopback, 0)
            listener.Start()

            let port =
                (listener.LocalEndpoint :?> IPEndPoint).Port

            let command =
                CodingToolCli.build
                    (Some CopilotCli)
                    (CodingToolCli.Resume(
                        Some $"owned-session{string (char characterCode)}Write-Output injected"
                    ))

            let! result =
                TerminalHostClient.sendTerminalCommandDefault
                    $"http://127.0.0.1:{port}/terminal/"
                    command.AsShellString
                |> Async.StartAsTask

            Assert.Multiple(fun () ->
                Assert.That(
                    result,
                    Is.EqualTo(
                        Error "The terminal resume command is invalid"
                        : Result<unit, string>
                    )
                )

                Assert.That(
                    listener.Pending(),
                    Is.False,
                    "invalid resume input must not connect to the terminal"
                ))
        }

    [<Test>]
    member _.``command attachment conversion revalidates the common endpoint shape``() =
        task {
            use listener = new TcpListener(IPAddress.Loopback, 0)
            listener.Start()

            let port =
                (listener.LocalEndpoint :?> IPEndPoint).Port

            let! result =
                TerminalHostClient.sendTerminalCommandDefault
                    $"http://127.0.0.1:{port}/terminal/?unexpected=true"
                    "Write-Output safe"
                |> Async.StartAsTask

            Assert.Multiple(fun () ->
                Assert.That(
                    result,
                    Is.EqualTo(
                        Error "TerminalHost returned an invalid command attachment endpoint"
                        : Result<unit, string>
                    )
                )

                Assert.That(
                    listener.Pending(),
                    Is.False,
                    "an invalid attachment endpoint must not be contacted"
                ))
        }

    [<Test>]
    member _.``control and attachment endpoints retain their caller-specific path and port rules``() =
        task {
            use host = new FakeControlHost()
            let config = managerConfig host noLaunch

            host.PublishManifestWithJsonField(
                "endpoint",
                JsonSerializer.Serialize($"{host.Endpoint}/unexpected")
            )

            match TerminalHostManifest.readManifest config with
            | Error error ->
                Assert.That(
                    error,
                    Is.EqualTo "TerminalHost discovery manifest has an invalid control endpoint"
                )
            | Ok manifest ->
                Assert.Fail($"Expected control endpoint rejection, got {manifest}")

            host.PublishManifest()
            let manager = EmbeddedTerminal.createWithConfig config
            let target = worktree host.Root "endpoint-rules"

            let! started =
                EmbeddedTerminal.start manager target
                |> Async.StartAsTask

            requireOk started |> ignore
            let terminal = host.CurrentTerminals |> List.exactlyOne
            let expectedPath =
                $"/_treemon/{terminal.SessionId}/{host.Token}/"

            let manifest =
                match TerminalHostManifest.readManifest config with
                | Ok(Some manifest) -> manifest
                | result ->
                    Assert.Fail($"Expected a valid manifest, got {result}")
                    Unchecked.defaultof<_>

            let assertAttachmentRejected endpoint =
                async {
                    host.ReturnRegistryWithJsonField(
                        "attachmentEndpoint",
                        JsonSerializer.Serialize endpoint
                    )

                    let! listed =
                        TerminalHostClient.listTerminals config manifest

                    Assert.That(
                        listed,
                        Is.EqualTo(
                            Error "TerminalHost returned an invalid attachment endpoint"
                            : Result<TerminalHostClient.RegistrySnapshot, string>
                        )
                    )
                }

            do!
                assertAttachmentRejected
                    $"http://127.0.0.1:41001/unexpected/"
                |> Async.StartAsTask

            do!
                assertAttachmentRejected
                    $"http://127.0.0.1:5000{expectedPath}"
                |> Async.StartAsTask
        }

    [<TestCase(
        "endpoint",
        "null",
        "TerminalHost discovery manifest has an invalid control endpoint"
    )>]
    [<TestCase(
        "endpoint",
        "42",
        "TerminalHost discovery manifest is malformed"
    )>]
    [<TestCase(
        "bearerToken",
        "null",
        "TerminalHost discovery manifest has an invalid bearer token"
    )>]
    [<TestCase(
        "bearerToken",
        "42",
        "TerminalHost discovery manifest is malformed"
    )>]
    [<TestCase(
        "hostVersion",
        "null",
        "TerminalHost discovery manifest has an invalid host version"
    )>]
    [<TestCase(
        "hostVersion",
        "42",
        "TerminalHost discovery manifest is malformed"
    )>]
    [<TestCase(
        "unexpected",
        "true",
        "TerminalHost discovery manifest has an invalid shape"
    )>]
    member _.``mandatory manifest strings reject null and malformed JSON while properties stay exact``
        (
            fieldName: string,
            jsonValue: string,
            expectedError: string
        ) =
        use host = new FakeControlHost()
        let config = managerConfig host noLaunch
        host.PublishManifestWithJsonField(fieldName, jsonValue)

        let error =
            match TerminalHostManifest.readManifest config with
            | Error error -> error
            | Ok manifest ->
                Assert.Fail($"Expected manifest rejection, got {manifest}")
                ""

        Assert.That(error, Is.EqualTo expectedError)

    [<TestCase(
        "worktreePath",
        "null",
        "TerminalHost returned an invalid worktree path"
    )>]
    [<TestCase(
        "worktreePath",
        "42",
        "TerminalHost terminal record is malformed"
    )>]
    [<TestCase(
        "attachmentEndpoint",
        "null",
        "TerminalHost returned an invalid attachment endpoint"
    )>]
    [<TestCase(
        "attachmentEndpoint",
        "42",
        "TerminalHost terminal record is malformed"
    )>]
    [<TestCase(
        "unexpected",
        "true",
        "TerminalHost terminal record has an invalid shape"
    )>]
    member _.``mandatory terminal strings reject null and malformed JSON while properties stay exact``
        (
            fieldName: string,
            jsonValue: string,
            expectedError: string
        ) =
        task {
            use host = new FakeControlHost()
            host.PublishManifest()
            let config = managerConfig host noLaunch
            let manager = EmbeddedTerminal.createWithConfig config
            let target = worktree host.Root "terminal-wire"

            let! started =
                EmbeddedTerminal.start manager target
                |> Async.StartAsTask

            requireOk started |> ignore
            host.ReturnRegistryWithJsonField(fieldName, jsonValue)

            let manifest =
                match TerminalHostManifest.readManifest config with
                | Ok(Some manifest) -> manifest
                | result ->
                    Assert.Fail($"Expected a valid manifest, got {result}")
                    Unchecked.defaultof<_>

            let! listed =
                TerminalHostClient.listTerminals config manifest
                |> Async.StartAsTask

            let error =
                match listed with
                | Error error -> error
                | Ok registry ->
                    Assert.Fail($"Expected terminal rejection, got {registry}")
                    ""

            Assert.That(error, Is.EqualTo expectedError)
        }

    [<Test>]
    member _.``starts lazily and resolves ambiguous start and close by authoritative relist``() =
        task {
            use host = new FakeControlHost()
            let launches = ConcurrentQueue<unit>()

            let config =
                managerConfig host (fun _ ->
                    launches.Enqueue()
                    host.PublishManifest()
                    Ok())

            let manager = EmbeddedTerminal.createWithConfig config
            let target = worktree host.Root "first"

            host.FailNextStartResponse()

            let! started =
                EmbeddedTerminal.start manager target
                |> Async.StartAsTask

            let snapshot = requireOk started
            let endpoint = assertRunningFor target snapshot

            Assert.Multiple(fun () ->
                Assert.That(launches.Count, Is.EqualTo(1))
                Assert.That(host.StartRequestCount, Is.EqualTo(1))
                Assert.That(host.ListRequestCount, Is.GreaterThanOrEqualTo(1))
                Assert.That(endpoint, Does.EndWith($"{host.Token}/")))

            let! reused =
                EmbeddedTerminal.start manager target
                |> Async.StartAsTask

            Assert.Multiple(fun () ->
                Assert.That((requireOk reused).Tabs.Length, Is.EqualTo(1))
                Assert.That(launches.Count, Is.EqualTo(1)))

            host.FailNextCloseResponse()

            let! closed =
                EmbeddedTerminal.close manager target
                |> Async.StartAsTask

            Assert.Multiple(fun () ->
                Assert.That(closed, Is.EqualTo EmbeddedTerminalSnapshot.empty)
                Assert.That(host.CloseRequestCount, Is.EqualTo(1))
                Assert.That(host.ListRequestCount, Is.GreaterThanOrEqualTo(3)))
        }

    [<Test>]
    member _.``a rejected terminal start leaves other tabs running``() =
        task {
            use host = new FakeControlHost()
            host.PublishManifest()

            let manager =
                EmbeddedTerminal.createWithConfig(managerConfig host noLaunch)

            let running = worktree host.Root "running"
            let rejected = worktree host.Root "rejected"

            let! started =
                EmbeddedTerminal.start manager running
                |> Async.StartAsTask

            requireOk started |> ignore
            host.RejectNextStartResponse()

            let! rejection =
                EmbeddedTerminal.start manager rejected
                |> Async.StartAsTask

            let! cached =
                EmbeddedTerminal.getCached manager
                |> Async.StartAsTask

            let rejectionError =
                match rejection with
                | Error error -> error
                | Ok snapshot ->
                    Assert.Fail($"Expected the start to be rejected, got {snapshot}")
                    ""

            Assert.Multiple(fun () ->
                Assert.That(
                    rejectionError,
                    Is.EqualTo(
                        "TerminalHost returned HTTP 400: Unknown worktree path"
                    )
                )

                Assert.That(cached.Tabs.Length, Is.EqualTo(1))
                assertRunningFor running cached |> ignore)
        }

    [<Test>]
    member _.``rejects a malformed manifest without starting a competing host``() =
        task {
            use host = new FakeControlHost()
            host.PublishMalformedManifest()
            let launches = ConcurrentQueue<unit>()

            let manager =
                EmbeddedTerminal.createWithConfig(
                    managerConfig host (fun _ ->
                        launches.Enqueue()
                        Ok())
                )

            let! result =
                EmbeddedTerminal.start
                    manager
                    (worktree host.Root "malformed")
                |> Async.StartAsTask

            Assert.Multiple(fun () ->
                Assert.That(result |> Result.isError, Is.True)
                Assert.That(launches.Count, Is.Zero)
                Assert.That(host.StartRequestCount, Is.Zero))
        }

    [<Test>]
    member _.``a new server manager reconnects to the exact live host registry``() =
        task {
            use host = new FakeControlHost()
            host.PublishManifest()
            let config = managerConfig host noLaunch
            let firstManager = EmbeddedTerminal.createWithConfig config
            let first = worktree host.Root "first"
            let second = worktree host.Root "second"

            let! firstStarted =
                EmbeddedTerminal.start firstManager first
                |> Async.StartAsTask

            requireOk firstStarted |> ignore

            let! secondStarted =
                EmbeddedTerminal.start firstManager second
                |> Async.StartAsTask

            let beforeRestart = requireOk secondStarted
            let endpointsBefore =
                beforeRestart.Tabs
                |> List.map (fun tab ->
                    match tab.Lifecycle with
                    | EmbeddedTerminalLifecycle.Running endpoint -> endpoint
                    | lifecycle ->
                        Assert.Fail($"Expected running terminal, got {lifecycle}")
                        "")

            let restartedManager =
                EmbeddedTerminal.createWithConfig config

            let! rediscovered =
                EmbeddedTerminal.get restartedManager
                |> Async.StartAsTask

            let endpointsAfter =
                rediscovered.Tabs
                |> List.map (fun tab ->
                    match tab.Lifecycle with
                    | EmbeddedTerminalLifecycle.Running endpoint -> endpoint
                    | lifecycle ->
                        Assert.Fail($"Expected running terminal, got {lifecycle}")
                        "")

            Assert.Multiple(fun () ->
                Assert.That(
                    rediscovered.Tabs |> List.map _.Worktree,
                    Is.EqualTo(beforeRestart.Tabs |> List.map _.Worktree)
                )

                Assert.That(endpointsAfter, Is.EqualTo endpointsBefore)
                Assert.That(host.StartRequestCount, Is.EqualTo(2)))
        }

    [<Test>]
    member _.``host loss keeps the tab visible as interrupted and does not claim a reconnect``() =
        task {
            use host = new FakeControlHost()
            host.PublishManifest()
            let launches = ConcurrentQueue<unit>()

            let manager =
                EmbeddedTerminal.createWithConfig(
                    managerConfig host (fun _ ->
                        launches.Enqueue()
                        host.PublishManifest()
                        Ok())
                )

            let target = worktree host.Root "crashed"

            let! started =
                EmbeddedTerminal.start manager target
                |> Async.StartAsTask

            requireOk started |> ignore
            host.StopApi()

            let! afterCrash =
                EmbeddedTerminal.get manager
                |> Async.StartAsTask

            let lifecycle = afterCrash.Tabs |> List.exactlyOne |> _.Lifecycle

            match lifecycle with
            | EmbeddedTerminalLifecycle.Interrupted error ->
                Assert.That(error, Does.Contain("request"))
            | other ->
                Assert.Fail($"Expected interrupted terminal, got {other}")

            let! restartAttempt =
                EmbeddedTerminal.start manager target
                |> Async.StartAsTask

            Assert.Multiple(fun () ->
                Assert.That(restartAttempt |> Result.isError, Is.True)
                Assert.That(launches.Count, Is.Zero))
        }

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type EmbeddedTerminalReplacementTests() =
    [<Test>]
    member _.``registry race between snapshot and recheck aborts without side effects``() =
        task {
            use host = new FakeControlHost()
            host.EnableLogicalReplacement()
            let stagedVersion = "2.0.0-race"
            let stagedExecutable = host.Stage stagedVersion
            let launches = ConcurrentQueue<string>()

            let config =
                replacementManagerConfig
                    host
                    (fun startInfo ->
                        launches.Enqueue startInfo.FileName
                        host.Activate(startInfo.FileName, stagedVersion)
                        Ok())
                    (fun _ _ -> async { return Ok() })

            let manager = EmbeddedTerminal.createWithConfig config
            let first = worktree host.Root "race-first"
            let raced = worktree host.Root "race-winner"

            let! firstStarted =
                EmbeddedTerminal.start manager first
                |> Async.StartAsTask

            requireOk firstStarted |> ignore

            let query _ _ : Result<SessionActivity.OwnedSessionSnapshot, string> =
                Ok
                    { ActivityEpoch = 4L
                      OpenSessions = []
                      ResumableSessionIds = Map.empty }

            let beforeRecheck () =
                async {
                    let! started = EmbeddedTerminal.start manager raced
                    requireOk started |> ignore
                }

            let! outcome =
                EmbeddedTerminal.tryReplaceHostWith
                    beforeRecheck
                    query
                    manager
                |> Async.StartAsTask

            Assert.Multiple(fun () ->
                Assert.That(
                    outcome,
                    Is.EqualTo TerminalHostReplacement.ReplacementOutcome.RaceLost
                )

                Assert.That(host.ShutdownRequestCount, Is.Zero)
                Assert.That(launches, Is.Empty)
                Assert.That(host.IsOnline, Is.True)
                Assert.That(
                    host.CurrentTerminals |> List.map _.WorktreePath,
                    Is.EqualTo(
                        [ WorktreePath.value first
                          WorktreePath.value raced ]
                    )
                )
                Assert.That(File.Exists stagedExecutable, Is.True))
        }

    [<Test>]
    member _.``WaitingForUser on an exact owned session gates without timeout or launch``() =
        task {
            use host = new FakeControlHost()
            host.EnableLogicalReplacement()
            host.Stage "2.0.0-waiting" |> ignore
            let launches = ConcurrentQueue<string>()

            let manager =
                replacementManagerConfig
                    host
                    (fun startInfo ->
                        launches.Enqueue startInfo.FileName
                        Error "WaitingForUser must prevent launch")
                    (fun _ _ -> async { return Ok() })
                |> EmbeddedTerminal.createWithConfig

            let target = worktree host.Root "waiting"

            let! started =
                EmbeddedTerminal.start manager target
                |> Async.StartAsTask

            requireOk started |> ignore

            let terminal =
                host.CurrentTerminals |> List.exactlyOne

            let query _ _ : Result<SessionActivity.OwnedSessionSnapshot, string> =
                Ok
                    { ActivityEpoch = 9L
                      OpenSessions =
                        [ { TerminalSessionId =
                                SessionActivity.TerminalSessionId
                                    terminal.SessionId
                            CopilotSessionId =
                                SessionActivity.SessionId "waiting-session"
                            Status =
                                SessionActivity.SessionLevelStatus.WaitingForUser } ]
                      ResumableSessionIds = Map.empty }

            let! outcome =
                EmbeddedTerminal.tryReplaceHost query manager
                |> Async.StartAsTask

            Assert.Multiple(fun () ->
                Assert.That(
                    outcome,
                    Is.EqualTo
                        TerminalHostReplacement.ReplacementOutcome.WaitingForIdle
                )

                Assert.That(host.ShutdownRequestCount, Is.Zero)
                Assert.That(launches, Is.Empty)
                Assert.That(host.IsOnline, Is.True))
        }

    [<Test>]
    member _.``replacement ignores unrelated Working sessions and resumes only the exact terminal``() =
        task {
            use host = new FakeControlHost()
            host.EnableLogicalReplacement()
            let stagedVersion = "2.0.0-resume"
            let stagedExecutable = host.Stage stagedVersion
            let launches = ConcurrentQueue<string>()
            let submitted = ConcurrentQueue<string * string>()

            let config =
                replacementManagerConfig
                    host
                    (fun startInfo ->
                        launches.Enqueue startInfo.FileName
                        host.Activate(startInfo.FileName, stagedVersion)
                        Ok())
                    (fun endpoint command ->
                        async {
                            submitted.Enqueue(endpoint, command)
                            return Ok()
                        })

            let manager = EmbeddedTerminal.createWithConfig config
            let resumedPath = worktree host.Root "resume-owned"
            let plainPath = worktree host.Root "plain-shell"

            for path in [ resumedPath; plainPath ] do
                let! started =
                    EmbeddedTerminal.start manager path
                    |> Async.StartAsTask

                requireOk started |> ignore

            let before = host.CurrentTerminals
            let resumedTerminal = before[0]
            let ownedIds =
                before
                |> List.map (_.SessionId >> SessionActivity.TerminalSessionId)
                |> Set.ofList

            let queryTime = DateTimeOffset.UtcNow
            let resumedSessionId = "copilot-owned-resume"
            let wrongTerminalId =
                [ "ffffffffffffffffffffffffffffffff"
                  "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee" ]
                |> List.map SessionActivity.TerminalSessionId
                |> List.find (ownedIds.Contains >> not)

            let statuses =
                [ replacementStoredStatus
                      resumedSessionId
                      (Some(
                          SessionActivity.TerminalSessionId
                              resumedTerminal.SessionId
                      ))
                      (PathUtils.toWorktreePath resumedTerminal.WorktreePath)
                      SessionActivity.SessionLevelStatus.Idle
                      queryTime
                  // Same-worktree activity without the exact terminal origin must not gate.
                  replacementStoredStatus
                      "same-worktree-unowned"
                      None
                      (PathUtils.toWorktreePath resumedTerminal.WorktreePath)
                      SessionActivity.SessionLevelStatus.Working
                      queryTime
                  // Nor may activity attributed to a terminal absent from the current registry.
                  replacementStoredStatus
                      "other-terminal"
                      (Some wrongTerminalId)
                      (PathUtils.toWorktreePath resumedTerminal.WorktreePath)
                      SessionActivity.SessionLevelStatus.Working
                      queryTime ]

            let queriedIds =
                ConcurrentQueue<Set<SessionActivity.TerminalSessionId>>()

            let query now terminalIds =
                queriedIds.Enqueue terminalIds

                SessionActivityService.queryOwnedSessions
                    now
                    terminalIds
                    Map.empty
                    statuses
                |> Ok

            let! outcome =
                EmbeddedTerminal.tryReplaceHost query manager
                |> Async.StartAsTask

            let submittedEndpoint, command =
                submitted.ToArray() |> Array.exactlyOne

            let resumedAfter =
                host.CurrentTerminals
                |> List.find (fun terminal ->
                    terminal.AttachmentEndpoint = submittedEndpoint)

            let! snapshot =
                EmbeddedTerminal.get manager
                |> Async.StartAsTask

            Assert.Multiple(fun () ->
                Assert.That(
                    outcome,
                    Is.EqualTo(
                        TerminalHostReplacement.ReplacementOutcome.Replaced
                            stagedVersion
                    )
                )

                Assert.That(
                    launches.ToArray(),
                    Is.EqualTo [| stagedExecutable |]
                )

                Assert.That(host.ShutdownRequestCount, Is.EqualTo(1))
                Assert.That(submitted.Count, Is.EqualTo(1))
                Assert.That(
                    resumedAfter.WorktreePath,
                    Is.EqualTo(WorktreePath.value resumedPath)
                )
                Assert.That(
                    command,
                    Is.EqualTo(
                        $"copilot --yolo --resume '{resumedSessionId}'"
                    )
                )
                Assert.That(
                    host.CurrentTerminals |> List.map _.WorktreePath,
                    Is.EqualTo(
                        [ WorktreePath.value resumedPath
                          WorktreePath.value plainPath ]
                    ),
                    "tab order and the plain shell must both be recreated"
                )
                Assert.That(
                    queriedIds.ToArray(),
                    Has.All.EqualTo(ownedIds)
                )
                Assert.That(
                    snapshot.Tabs |> List.map _.Worktree,
                    Is.EqualTo([ resumedPath; plainPath ])
                ))
        }

    [<Test>]
    member _.``staged launch failure explicitly reports failure and recovers the old host``() =
        task {
            use host = new FakeControlHost()
            host.EnableLogicalReplacement()
            let stagedVersion = "2.0.0-fails"
            let stagedExecutable = host.Stage stagedVersion
            let launches = ConcurrentQueue<string>()

            let launch (startInfo: ProcessStartInfo) =
                launches.Enqueue startInfo.FileName

                if
                    Shared.PathUtils.pathEquals
                        startInfo.FileName
                        stagedExecutable
                then
                    Error "simulated staged launch failure"
                else
                    host.Activate(
                        startInfo.FileName,
                        "1.0.0-test"
                    )

                    Ok()

            let manager =
                replacementManagerConfig
                    host
                    launch
                    (fun _ _ -> async { return Ok() })
                |> EmbeddedTerminal.createWithConfig

            let target = worktree host.Root "recover-old"

            let! started =
                EmbeddedTerminal.start manager target
                |> Async.StartAsTask

            requireOk started |> ignore

            let query _ _ : Result<SessionActivity.OwnedSessionSnapshot, string> =
                Ok
                    { ActivityEpoch = 12L
                      OpenSessions = []
                      ResumableSessionIds = Map.empty }

            let! outcome =
                EmbeddedTerminal.tryReplaceHost query manager
                |> Async.StartAsTask

            let! recovered =
                EmbeddedTerminal.get manager
                |> Async.StartAsTask

            let failure =
                match outcome with
                | TerminalHostReplacement.ReplacementOutcome.Failed(
                    version,
                    error
                  ) ->
                    Assert.That(version, Is.EqualTo stagedVersion)
                    error
                | other ->
                    Assert.Fail($"Expected explicit replacement failure, got {other}")
                    ""

            Assert.Multiple(fun () ->
                Assert.That(failure, Does.Contain("recovered"))
                Assert.That(
                    launches.ToArray(),
                    Is.EqualTo(
                        [| stagedExecutable
                           host.OldExecutable |]
                    )
                )
                Assert.That(host.ShutdownRequestCount, Is.EqualTo(1))
                Assert.That(host.IsOnline, Is.True)
                Assert.That(
                    host.CurrentExecutable,
                    Is.EqualTo host.OldExecutable
                )
                Assert.That(
                    host.CurrentTerminals |> List.map _.WorktreePath,
                    Is.EqualTo([ WorktreePath.value target ])
                )
                Assert.That(recovered.Tabs.Length, Is.EqualTo(1))

                match recovered.Tabs[0].Lifecycle with
                | EmbeddedTerminalLifecycle.Running _ -> ()
                | lifecycle ->
                    Assert.Fail(
                        $"Expected the recoverably restarted old host, got {lifecycle}"
                    ))
        }

    [<Test>]
    member _.``replacement shutdown wait failure interrupts running tabs``() =
        task {
            use host = new FakeControlHost()
            let stagedVersion = "2.0.0-shutdown-wait"
            host.Stage stagedVersion |> ignore
            let launches = ConcurrentQueue<unit>()

            let config =
                replacementManagerConfig
                    host
                    (fun _ ->
                        launches.Enqueue()
                        Error "Replacement must not launch")
                    (fun _ _ -> async { return Ok() })

            let manager =
                { config with
                    StartupTimeout = TimeSpan.FromMilliseconds 100.0 }
                |> EmbeddedTerminal.createWithConfig

            let target = worktree host.Root "shutdown-wait"

            let! started =
                EmbeddedTerminal.start manager target
                |> Async.StartAsTask

            requireOk started |> ignore

            let query _ _ : Result<SessionActivity.OwnedSessionSnapshot, string> =
                Ok
                    { ActivityEpoch = 15L
                      OpenSessions = []
                      ResumableSessionIds = Map.empty }

            let! outcome =
                EmbeddedTerminal.tryReplaceHost query manager
                |> Async.StartAsTask

            let! cached =
                EmbeddedTerminal.getCached manager
                |> Async.StartAsTask

            let failure =
                match outcome with
                | TerminalHostReplacement.ReplacementOutcome.Failed(
                    version,
                    error
                  ) ->
                    Assert.That(version, Is.EqualTo stagedVersion)
                    error
                | other ->
                    Assert.Fail($"Expected explicit replacement failure, got {other}")
                    ""

            let lifecycle =
                cached.Tabs |> List.exactlyOne |> _.Lifecycle

            Assert.Multiple(fun () ->
                Assert.That(host.ShutdownRequestCount, Is.EqualTo(1))
                Assert.That(launches, Is.Empty)
                Assert.That(failure, Does.Contain("could not be confirmed stopped"))
                Assert.That(failure, Does.Not.Contain("retained"))

                match lifecycle with
                | EmbeddedTerminalLifecycle.Interrupted error ->
                    Assert.That(error, Does.Contain(failure))
                | other ->
                    Assert.Fail($"Expected interrupted terminal, got {other}"))
        }

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type EmbeddedTerminalWorktreeCleanupTests() =
    [<Test>]
    member _.``delete closes only the exact terminal before removing the worktree``() =
        task {
            use host = new FakeControlHost()
            host.PublishManifest()
            let manager =
                EmbeddedTerminal.createWithConfig(managerConfig host noLaunch)

            let repoRoot = Path.Combine(host.Root, "repo")
            Directory.CreateDirectory repoRoot |> ignore
            let target = worktree repoRoot "target"
            let untouched = worktree repoRoot "untouched"

            for path in [ target; untouched ] do
                let! started =
                    EmbeddedTerminal.start manager path
                    |> Async.StartAsTask

                requireOk started |> ignore

            let agent = SchedulerState.createAgent()
            let repoId = PathUtils.toRepoId repoRoot

            let worktrees =
                [ { Path = WorktreePath.value target
                    Head = "target-head"
                    Branch = Some "target" }
                  { Path = WorktreePath.value untouched
                    Head = "untouched-head"
                    Branch = Some "untouched" } ]

            do! populateAgent agent repoId worktrees
            let calls = ConcurrentQueue<string>()

            let! result =
                WorktreeApi.deleteWorktreeWith
                    (fun _ removedPath _ ->
                        async {
                            calls.Enqueue "remove"
                            let! snapshot = EmbeddedTerminal.get manager

                            Assert.That(
                                snapshot.Tabs |> List.map _.Worktree,
                                Is.EqualTo [ untouched ]
                            )

                            Assert.That(
                                removedPath,
                                Is.EqualTo(WorktreePath.value target)
                            )

                            return Ok()
                        })
                    (fun path ->
                        async {
                            calls.Enqueue "close"
                            return! closeForCleanup manager path
                        })
                    (fun _ ->
                        async {
                            calls.Enqueue "state"
                        })
                    agent
                    (Map.ofList [ repoId, repoRoot ])
                    target
                |> Async.StartAsTask

            requireOk result |> ignore

            Assert.Multiple(fun () ->
                Assert.That(
                    calls.ToArray(),
                    Is.EqualTo [| "close"; "remove"; "state" |]
                )

                Assert.That(
                    host.CurrentTerminals |> List.map _.WorktreePath,
                    Is.EqualTo [ WorktreePath.value untouched ]
                ))
        }

    [<Test>]
    member _.``archive closes only the exact terminal before persisting archive state``() =
        task {
            use host = new FakeControlHost()
            host.PublishManifest()
            let manager =
                EmbeddedTerminal.createWithConfig(managerConfig host noLaunch)

            let repoRoot = Path.Combine(host.Root, "archive-repo")
            Directory.CreateDirectory repoRoot |> ignore
            let target = worktree repoRoot "target"
            let untouched = worktree repoRoot "untouched"

            for path in [ target; untouched ] do
                let! started =
                    EmbeddedTerminal.start manager path
                    |> Async.StartAsTask

                requireOk started |> ignore

            let agent = SchedulerState.createAgent()
            let repoId = PathUtils.toRepoId repoRoot

            let worktrees =
                [ { Path = WorktreePath.value target
                    Head = "target-head"
                    Branch = Some "target" }
                  { Path = WorktreePath.value untouched
                    Head = "untouched-head"
                    Branch = Some "untouched" } ]

            do! populateAgent agent repoId worktrees

            let! result =
                WorktreeApi.updateArchivedBranchesWith
                    agent
                    (Map.ofList [ repoId, repoRoot ])
                    (closeForCleanup manager)
                    Set.add
                    target
                |> Async.StartAsTask

            requireOk result |> ignore

            let! remaining =
                EmbeddedTerminal.get manager
                |> Async.StartAsTask

            Assert.Multiple(fun () ->
                Assert.That(
                    remaining.Tabs |> List.map _.Worktree,
                    Is.EqualTo [ untouched ]
                )

                Assert.That(
                    TreemonConfig.readArchivedBranches repoRoot,
                    Does.Contain("target")
                ))
        }
