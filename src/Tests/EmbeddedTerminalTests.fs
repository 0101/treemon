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
open global.Server.SessionActivity
open global.Server.SessionActivityStore
open global.Server.SchedulerState
open global.Server.TerminalSessionActivity
open Shared
open Tests.TestUtils
open Treemon.TerminalHosting

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

type private ReplacementCommitMessage =
    TerminalHostReplacement.ReplacementPlan
        * TerminalHostReplacement.ReplacementPolicyQuery
        * AsyncReplyChannel<TerminalHostReplacement.ReplacementOutcome>

type private FakeControlHost
    (
        ?onTerminalStarted: string -> unit,
        ?onTerminalClosing: string -> unit
    ) =
    let root = uniquePath "embedded-terminal-client"
    let stateDirectory = Path.Combine(root, "state")
    let token = "0123456789abcdefghijklmnopqrstuvwxyzABCDEFG"
    let gate = obj()
    let terminalStarted = defaultArg onTerminalStarted ignore
    let terminalClosing = defaultArg onTerminalClosing ignore
    let oldDirectory = Path.Combine(root, "old")
    let oldExecutable =
        Path.Combine(oldDirectory, TerminalHostLayout.HostExecutableName)

    let writeBundle directory content =
        Directory.CreateDirectory directory |> ignore

        TerminalHostLayout.RequiredBundleFileNames
        |> List.iter (fun name ->
            File.WriteAllText(
                Path.Combine(directory, name),
                $"{content}: {name}"
            ))

        Path.Combine(directory, TerminalHostLayout.HostExecutableName)

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
    let mutable controlApiVersion = 2
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
        writeBundle oldDirectory "fake old TerminalHost" |> ignore

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
        let startedPath, fail =
            lock gate (fun () ->
                let sessionId = Guid.NewGuid().ToString("N")
                let canonical =
                    Path.GetFullPath path
                    |> Path.TrimEndingDirectorySeparator

                terminals <-
                    terminals
                    @ [ { SessionId = sessionId
                          WorktreePath = canonical
                          AttachmentEndpoint =
                            $"http://127.0.0.1:41001/_treemon/{sessionId}/{token}/" } ]

                revision <- revision + 1L

                let fail = failNextStartResponse
                failNextStartResponse <- false
                canonical, fail)

        terminalStarted startedPath
        fail

    let closeTerminal sessionId =
        let closingPath =
            lock gate (fun () ->
                terminals
                |> List.tryFind (fun terminal ->
                    terminal.SessionId = sessionId)
                |> Option.map _.WorktreePath)

        closingPath |> Option.iter terminalClosing

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
                let version, apiVersion =
                    lock gate (fun () ->
                        hostVersion, controlApiVersion)
                let apiRoot = $"/api/v{apiVersion}"

                match method, path with
                | "GET", requestPath when requestPath = $"{apiRoot}/health" ->
                    return!
                        writeJson
                            StatusCodes.Status200OK
                            { Pid = currentPid
                              ProcessStartTimeUtcTicks = currentStartTicks
                              HostVersion = version
                              ControlApiVersion = apiVersion }
                            context
                | "GET", requestPath when requestPath = $"{apiRoot}/terminals" ->
                    listRequests.Enqueue()

                    match lock gate (fun () -> registryJsonOverride) with
                    | None ->
                        return! writeJson StatusCodes.Status200OK (snapshot ()) context
                    | Some content ->
                        context.Response.StatusCode <- StatusCodes.Status200OK
                        context.Response.ContentType <- "application/json; charset=utf-8"
                        return! context.Response.WriteAsync content
                | "POST", requestPath when requestPath = $"{apiRoot}/terminals" ->
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
                        $"{apiRoot}/terminals/",
                        StringComparison.Ordinal
                    ) ->
                    let sessionId =
                        closePath.Substring($"{apiRoot}/terminals/".Length)

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
                | "POST", requestPath when requestPath = $"{apiRoot}/shutdown" ->
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
        let version, apiVersion, staged =
            lock gate (fun () ->
                hostVersion, controlApiVersion, stagedVersion)

        let manifest = JsonObject()
        manifest["pid"] <- JsonValue.Create currentPid
        manifest["processStartTimeUtcTicks"] <- JsonValue.Create currentStartTicks
        manifest["endpoint"] <- JsonValue.Create endpoint
        manifest["bearerToken"] <- JsonValue.Create token
        manifest["hostVersion"] <- JsonValue.Create version
        manifest["controlApiVersion"] <- JsonValue.Create apiVersion

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
            TerminalHostLayout.forStateDirectory stateDirectory
            |> fun layout ->
                TerminalHostLayout.versionDirectory layout version

        let executable =
            writeBundle directory $"fake staged TerminalHost {version}"

        lock gate (fun () -> stagedVersion <- Some version)
        this.PublishManifest()
        executable

    member _.EnableLogicalReplacement() =
        lock gate (fun () -> logicalShutdown <- true)

    member _.SetControlApiVersion(version: int) =
        lock gate (fun () ->
            controlApiVersion <- version)

    member this.Activate(executablePath: string, version: string) =
        lock gate (fun () ->
            currentExecutable <- Path.GetFullPath executablePath
            hostVersion <- version
            online <- true
            terminals <- []
            revision <- 0L)

        this.PublishManifest()

    member private _.IsCurrentProcessLive(pid: int, startTicks: int64) =
        pid = currentPid
        && startTicks = currentStartTicks
        && lock gate (fun () -> online)

    member this.ExactProcessIsLive(pid: int, startTicks: int64) =
        Ok(this.IsCurrentProcessLive(pid, startTicks))

    member this.ResolveExactProcessExecutable(pid: int, startTicks: int64) =
        if this.IsCurrentProcessLive(pid, startTicks) then
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

let private noTerminalCommand _ _ =
    async {
        return
            Error
                "The test did not expect a terminal command"
    }

let private argumentValue name (startInfo: ProcessStartInfo) =
    startInfo.ArgumentList
    |> Seq.toList
    |> List.windowed 2
    |> List.tryPick (function
        | [ option; value ] when option = name -> Some value
        | _ -> None)

let private managerConfig
    (host: FakeControlHost)
    (launchHost: ProcessStartInfo -> Result<unit, string>)
    : TerminalHostProcess.Config =
    { HostExecutablePath = host.OldExecutable
      HostStateDirectory = host.StateDirectory
      TtydExecutablePath = None
      ShellCommand = "pwsh"
      AllowedOrigins = [ "http://localhost:5174" ]
      StartupTimeout = TimeSpan.FromSeconds 2.0
      ControlRequestTimeout = TimeSpan.FromMilliseconds 500.0
      ProbeInterval = TimeSpan.FromMilliseconds 20.0
      LaunchHost = launchHost
      ProcessIdentityMatches =
        TerminalHostProcess.processIdentityMatchesDefault
      ResolveProcessExecutable =
        fun pid startTicks ->
            match
                TerminalHostProcess.processIdentityMatchesDefault
                    pid
                    startTicks
            with
            | Ok true -> Ok host.OldExecutable
            | Ok false -> Error "Fake TerminalHost identity is not live"
            | Error error -> Error error
      SendTerminalCommand = noTerminalCommand }

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

let private requireError result =
    match result with
    | Error error -> error
    | Ok _ ->
        Assert.Fail("Expected an error")
        ""

let private runningEndpoint (tab: EmbeddedTerminalTab) =
    match tab.Lifecycle with
    | EmbeddedTerminalLifecycle.Running endpoint -> endpoint
    | lifecycle ->
        Assert.Fail($"Expected running terminal, got {lifecycle}")
        ""

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

    let endpoint = runningEndpoint tab
    Assert.That(endpoint, Does.StartWith("http://127.0.0.1:41001/_treemon/"))
    endpoint

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
type TerminalHostProcessConfigurationTests() =
    [<TestCase("Debug")>]
    [<TestCase("Release")>]
    member _.``source-tree host binaries are selected only when explicitly configured``
        (configuration: string)
        =
        withTempDir "terminal-host-resolution" (fun root ->
            let baseDirectory = Path.Combine(root, "app")
            let publishedExecutable =
                Path.Combine(
                    baseDirectory,
                    "terminal-host",
                    TerminalHostProcess.hostExecutableName
                )
                |> Path.GetFullPath

            let directExecutable =
                Path.Combine(
                    baseDirectory,
                    TerminalHostProcess.hostExecutableName
                )

            let sourceTreeExecutable =
                Path.Combine(
                    root,
                    "src",
                    "TerminalHost",
                    "bin",
                    configuration,
                    "net10.0",
                    TerminalHostProcess.hostExecutableName
                )
                |> Path.GetFullPath

            [ directExecutable; sourceTreeExecutable ]
            |> List.iter (fun path ->
                Directory.CreateDirectory(Path.GetDirectoryName path)
                |> ignore

                File.WriteAllText(path, "fixture"))

            let implicitlyResolved =
                TerminalHostProcess.resolveHostExecutable
                    baseDirectory
                    None

            let explicitlyResolved =
                TerminalHostProcess.resolveHostExecutable
                    baseDirectory
                    (Some sourceTreeExecutable)

            Assert.Multiple(fun () ->
                Assert.That(
                    implicitlyResolved,
                    Is.EqualTo publishedExecutable,
                    "Missing published layout must fail closed at its deployment path"
                )

                Assert.That(
                    File.Exists implicitlyResolved,
                    Is.False,
                    "An existing direct or source-tree binary must not become an implicit fallback"
                )

                Assert.That(
                    explicitlyResolved,
                    Is.EqualTo sourceTreeExecutable,
                    "Development may select a source-tree build only through explicit startup configuration"
                )))

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
    member _.``commands containing control characters are rejected before terminal input``
        (characterCode: int)
        =
        task {
            use listener = new TcpListener(IPAddress.Loopback, 0)
            listener.Start()

            let port =
                (listener.LocalEndpoint :?> IPEndPoint).Port

            let command =
                $"opaque-command{string (char characterCode)}Write-Output injected"

            let! result =
                TerminalHostClient.sendTerminalCommandDefault
                    $"http://127.0.0.1:{port}/terminal/"
                    command
                |> Async.StartAsTask

            Assert.Multiple(fun () ->
                Assert.That(
                    result,
                    Is.EqualTo(
                        Error "The terminal command is invalid"
                        : Result<unit, string>
                    )
                )

                Assert.That(
                    listener.Pending(),
                    Is.False,
                    "invalid command input must not connect to the terminal"
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
        "sessionId",
        "null",
        "TerminalHost returned an invalid terminal session ID"
    )>]
    [<TestCase(
        "sessionId",
        "42",
        "TerminalHost terminal record is malformed"
    )>]
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

    [<TestCase(false, false)>]
    [<TestCase(false, true)>]
    [<TestCase(true, false)>]
    [<TestCase(true, true)>]
    member _.``deployment preflight covers compatible and incompatible hosts with empty and nonempty registries``
        (
            incompatible: bool,
            hasTerminal: bool
        ) =
        task {
            use host = new FakeControlHost()
            host.PublishManifest()

            let config =
                replacementManagerConfig
                    host
                    noLaunch
                    noTerminalCommand

            if hasTerminal then
                let manager =
                    EmbeddedTerminal.createWithConfig config

                let target =
                    worktree host.Root "preflight-terminal"

                let! started =
                    EmbeddedTerminal.start manager target
                    |> Async.StartAsTask

                requireOk started |> ignore

            if incompatible then
                host.SetControlApiVersion 1

                if not hasTerminal then
                    host.EnableLogicalReplacement()

                host.PublishManifest()

            let listRequestsBefore = host.ListRequestCount

            let! result =
                TerminalHostClient.preflightDeploymentWith config
                |> Async.StartAsTask

            Assert.That(
                host.ListRequestCount,
                Is.EqualTo(listRequestsBefore + 1),
                "Preflight must read the authoritative registry"
            )

            match incompatible, hasTerminal, result with
            | true, true, Error error ->
                Assert.Multiple(fun () ->
                    Assert.That(
                        error,
                        Is.EqualTo(
                            "TerminalHost control API version 1 is not supported (expected 2)"
                        )
                    )

                    Assert.That(host.IsOnline, Is.True)
                    Assert.That(host.ShutdownRequestCount, Is.Zero))
            | true, true, Ok preflight ->
                Assert.Fail(
                    $"An incompatible host with terminals must fail closed, got {preflight}"
                )
            | true, false, Ok None ->
                Assert.Multiple(fun () ->
                    Assert.That(host.IsOnline, Is.False)
                    Assert.That(host.ShutdownRequestCount, Is.EqualTo(1)))
            | true, false, result ->
                Assert.Fail(
                    $"An incompatible empty host should stop cleanly, got {result}"
                )
            | _, _, Error error ->
                Assert.Fail($"Expected deployment preflight success, got {error}")
            | _, _, Ok None ->
                Assert.Fail("The exact fixture host should remain live")
            | _, _, Ok(Some liveHost) ->
                Assert.Multiple(fun () ->
                    Assert.That(
                        liveHost.ExecutablePath,
                        Is.EqualTo host.OldExecutable
                    )

                    Assert.That(
                        liveHost.TerminalCount,
                        Is.EqualTo(if hasTerminal then 1 else 0)
                    )

                    Assert.That(liveHost.Pid, Is.GreaterThan(0))
                    Assert.That(
                        liveHost.ProcessStartTimeUtcTicks,
                        Is.GreaterThan(0L)
                    ))
        }

    [<Test>]
    member _.``incompatible empty host fails closed when exact shutdown is not confirmed``() =
        task {
            use host = new FakeControlHost()
            host.SetControlApiVersion 1
            host.PublishManifest()

            let config =
                { replacementManagerConfig
                    host
                    noLaunch
                    noTerminalCommand with
                    StartupTimeout = TimeSpan.FromMilliseconds 100.0 }

            let! result =
                TerminalHostClient.preflightDeploymentWith config
                |> Async.StartAsTask

            match result with
            | Error error ->
                Assert.Multiple(fun () ->
                    Assert.That(
                        error,
                        Does.StartWith(
                            "The incompatible empty TerminalHost could not be stopped:"
                        )
                    )

                    Assert.That(host.ShutdownRequestCount, Is.EqualTo(1)))
            | Ok preflight ->
                Assert.Fail(
                    $"Unconfirmed incompatible-host shutdown must fail, got {preflight}"
                )
        }

    [<Test>]
    member _.``deployment preflight does not trust a registry after an unsupported manifest API route``() =
        task {
            use host = new FakeControlHost()

            host.PublishManifestWithJsonField(
                "controlApiVersion",
                "3"
            )

            let! result =
                TerminalHostClient.preflightDeploymentWith(
                    managerConfig host noLaunch
                )
                |> Async.StartAsTask

            Assert.Multiple(fun () ->
                Assert.That(
                    result,
                    Is.EqualTo(
                        Error
                            "TerminalHost returned HTTP 404: Control endpoint not found"
                        : Result<TerminalHostClient.DeploymentPreflightResult option, string>
                    )
                )

                Assert.That(host.ListRequestCount, Is.Zero)
                Assert.That(host.ShutdownRequestCount, Is.Zero))
        }

    [<Test>]
    member _.``starts distinct terminals lazily and resolves ambiguous mutations by relist``() =
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

            let firstStart = requireOk started
            let endpoint = assertRunningFor target firstStart
            let firstTerminalId =
                firstStart.Tabs |> List.exactlyOne |> _.Id

            Assert.Multiple(fun () ->
                Assert.That(launches.Count, Is.EqualTo(1))
                Assert.That(host.StartRequestCount, Is.EqualTo(1))
                Assert.That(host.ListRequestCount, Is.GreaterThanOrEqualTo(1))
                Assert.That(endpoint, Does.EndWith($"{host.Token}/")))

            let! second =
                EmbeddedTerminal.start manager target
                |> Async.StartAsTask

            Assert.Multiple(fun () ->
                Assert.That((requireOk second).Tabs.Length, Is.EqualTo(2))
                Assert.That(launches.Count, Is.EqualTo(1)))

            host.FailNextCloseResponse()

            let! closed =
                EmbeddedTerminal.close manager firstTerminalId
                |> Async.StartAsTask

            Assert.Multiple(fun () ->
                Assert.That((requireOk closed).Tabs.Length, Is.EqualTo(1))
                Assert.That(host.CloseRequestCount, Is.EqualTo(1))
                Assert.That(host.ListRequestCount, Is.GreaterThanOrEqualTo(5)))
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
                |> List.map runningEndpoint

            let restartedManager =
                EmbeddedTerminal.createWithConfig config

            let! rediscovered =
                EmbeddedTerminal.get restartedManager
                |> Async.StartAsTask

            let endpointsAfter =
                rediscovered.Tabs
                |> List.map runningEndpoint

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
    member _.``coordinator keeps polling and retries a failed version after its cooldown``() =
        task {
            let stagedVersion = "2.0.0-retry"

            let startedAt =
                DateTimeOffset(2026, 8, 21, 5, 0, 0, TimeSpan.Zero)

            // Mutation models the externally advancing clock at this test boundary.
            let mutable timestamps =
                [ startedAt
                  startedAt
                  startedAt.AddSeconds 30.0
                  startedAt.AddSeconds 30.0
                  startedAt.AddMinutes 2.0
                  startedAt.AddMinutes 2.0 ]

            let utcNow () =
                match timestamps with
                | timestamp :: remaining ->
                    timestamps <- remaining
                    timestamp
                | [] -> failwith "The coordinator read the clock too many times"

            let observedIgnoredVersions = ConcurrentQueue<string option>()

            let tryReplace ignoredStagedVersion =
                async {
                    observedIgnoredVersions.Enqueue ignoredStagedVersion

                    return
                        match observedIgnoredVersions.Count with
                        | 1 ->
                            TerminalHostReplacement.ReplacementOutcome.Failed(
                                stagedVersion,
                                "transient failure"
                            )
                        | 2 ->
                            TerminalHostReplacement.ReplacementOutcome.NoCandidate
                        | 3 ->
                            TerminalHostReplacement.ReplacementOutcome.Replaced
                                stagedVersion
                        | count ->
                            failwith
                                $"The coordinator performed unexpected replacement attempt {count}"
                }

            let waitForNextPoll _ =
                async {
                    return observedIgnoredVersions.Count < 3
                }

            do!
                TerminalHostReplacement.runCoordinatorWith
                    utcNow
                    waitForNextPoll
                    tryReplace
                    System.Threading.CancellationToken.None
                |> Async.StartAsTask

            Assert.That(
                observedIgnoredVersions.ToArray(),
                Is.EqualTo(
                    [| None
                       Some stagedVersion
                       None |]
                )
            )
        }

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

            let query _ _ =
                Ok(
                    TerminalHostReplacement.ReplacementSessionPlan.Ready(
                        4L,
                        Map.empty
                    )
                )

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
    member _.``owned-session activity epoch race aborts before host shutdown``() =
        task {
            use host = new FakeControlHost()
            host.EnableLogicalReplacement()
            let stagedVersion = "2.0.0-activity-race"
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
            let target = worktree host.Root "activity-race"

            let! started =
                EmbeddedTerminal.start manager target
                |> Async.StartAsTask

            requireOk started |> ignore

            // Callback invocation count is mutable fixture state around the two replacement reads.
            let mutable queryCount = 0

            let query _ _ =
                let invocation =
                    System.Threading.Interlocked.Increment(&queryCount)

                let epoch = if invocation = 1 then 4L else 5L

                Ok(
                    TerminalHostReplacement.ReplacementSessionPlan.Ready(
                        epoch,
                        Map.empty
                    )
                )

            let! outcome =
                EmbeddedTerminal.tryReplaceHostWith
                    (fun () -> async.Return())
                    query
                    manager
                |> Async.StartAsTask

            Assert.Multiple(fun () ->
                Assert.That(
                    outcome,
                    Is.EqualTo TerminalHostReplacement.ReplacementOutcome.RaceLost
                )
                Assert.That(queryCount, Is.EqualTo 2)
                Assert.That(host.ShutdownRequestCount, Is.Zero)
                Assert.That(launches, Is.Empty)
                Assert.That(host.IsOnline, Is.True)
                Assert.That(host.CurrentTerminals.Length, Is.EqualTo(1))
                Assert.That(File.Exists stagedExecutable, Is.True))
        }

    [<Test>]
    member _.``timed out mailbox commit is rechecked after its late completion``() =
        task {
            use host = new FakeControlHost()
            host.EnableLogicalReplacement()
            let stagedVersion = "2.0.0-late-reply"
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
            let target = worktree host.Root "late-mailbox-reply"

            let! started =
                EmbeddedTerminal.start manager target
                |> Async.StartAsTask

            requireOk started |> ignore

            let query _ _ =
                Ok(
                    TerminalHostReplacement.ReplacementSessionPlan.Ready(
                        5L,
                        Map.empty
                    )
                )

            let commitStarted =
                TaskCompletionSource<unit>(
                    TaskCreationOptions.RunContinuationsAsynchronously
                )

            let releaseCommit =
                TaskCompletionSource<unit>(
                    TaskCreationOptions.RunContinuationsAsynchronously
                )

            let commitCompleted =
                TaskCompletionSource<TerminalHostReplacement.ReplacementOutcome>(
                    TaskCreationOptions.RunContinuationsAsynchronously
                )

            let commitAgent =
                MailboxProcessor.Start(fun (inbox: MailboxProcessor<ReplacementCommitMessage>) ->
                    async {
                        let! plan, activityQuery, reply = inbox.Receive()
                        commitStarted.TrySetResult() |> ignore
                        do! releaseCommit.Task |> Async.AwaitTask

                        let! commit =
                            TerminalHostReplacement.commitReplacement
                                config
                                plan
                                activityQuery

                        let outcome =
                            match commit with
                            | TerminalHostReplacement.ReplacementCommit.KeepState value
                            | TerminalHostReplacement.ReplacementCommit.InterruptState(
                                _,
                                value
                              )
                            | TerminalHostReplacement.ReplacementCommit.ApplyRegistry(
                                _,
                                _,
                                value
                              ) ->
                                value

                        reply.Reply outcome
                        commitCompleted.TrySetResult outcome |> ignore
                    })

            let postCommit plan activityQuery =
                commitAgent.PostAndAsyncReply(
                    (fun reply -> plan, activityQuery, reply),
                    timeout = 20
                )

            let replacementAttempt =
                TerminalHostReplacement.tryReplaceHostIgnoring
                    None
                    (fun () -> async.Return())
                    query
                    config
                    postCommit
                |> Async.StartAsTask

            do!
                commitStarted.Task.WaitAsync(TimeSpan.FromSeconds 5.0)

            let! timedOut = replacementAttempt
            releaseCommit.TrySetResult() |> ignore

            let! lateOutcome =
                commitCompleted.Task.WaitAsync(TimeSpan.FromSeconds 5.0)

            let duplicateCommits = ConcurrentQueue<unit>()

            let rejectDuplicateCommit _ _ =
                async {
                    duplicateCommits.Enqueue()

                    return
                        TerminalHostReplacement.ReplacementOutcome.Failed(
                            stagedVersion,
                            "A completed replacement must not be repeated"
                        )
                }

            let! rechecked =
                TerminalHostReplacement.tryReplaceHostIgnoring
                    None
                    (fun () -> async.Return())
                    query
                    config
                    rejectDuplicateCommit
                |> Async.StartAsTask

            Assert.Multiple(fun () ->
                Assert.That(
                    timedOut,
                    Is.EqualTo TerminalHostReplacement.ReplacementOutcome.RaceLost
                )

                Assert.That(
                    lateOutcome,
                    Is.EqualTo(
                        TerminalHostReplacement.ReplacementOutcome.Replaced
                            stagedVersion
                    )
                )

                Assert.That(
                    rechecked,
                    Is.EqualTo TerminalHostReplacement.ReplacementOutcome.NoCandidate
                )

                Assert.That(duplicateCommits, Is.Empty)
                Assert.That(host.ShutdownRequestCount, Is.EqualTo(1))
                Assert.That(launches.ToArray(), Is.EqualTo [| stagedExecutable |])
                Assert.That(host.CurrentExecutable, Is.EqualTo stagedExecutable)
                Assert.That(host.CurrentTerminals.Length, Is.EqualTo(1)))
        }

    [<Test>]
    member _.``manager stays responsive and rejects lifecycle mutations during replacement``() =
        task {
            use host = new FakeControlHost()
            host.EnableLogicalReplacement()
            let stagedVersion = "2.0.0-held-lifecycle"
            let stagedExecutable = host.Stage stagedVersion

            let launchStarted =
                TaskCompletionSource<unit>(
                    TaskCreationOptions.RunContinuationsAsynchronously
                )

            use releaseLaunch =
                new System.Threading.ManualResetEventSlim(false)

            let config =
                replacementManagerConfig
                    host
                    (fun startInfo ->
                        launchStarted.TrySetResult() |> ignore

                        if releaseLaunch.Wait(TimeSpan.FromSeconds 5.0) then
                            host.Activate(startInfo.FileName, stagedVersion)
                            Ok()
                        else
                            Error "Timed out waiting to release the staged host launch")
                    (fun _ _ -> async { return Ok() })

            let manager = EmbeddedTerminal.createWithConfig config
            let running = worktree host.Root "held-running"
            let rejectedStart = worktree host.Root "held-start"

            let! started =
                EmbeddedTerminal.start manager running
                |> Async.StartAsTask

            let runningStart = requireOk started
            let originalRunningTerminalId =
                runningStart.Tabs |> List.exactlyOne |> _.Id

            let query _ _ =
                Ok(
                    TerminalHostReplacement.ReplacementSessionPlan.Ready(
                        31L,
                        Map.empty
                    )
                )

            let replacement =
                EmbeddedTerminal.tryReplaceHost query manager
                |> Async.StartAsTask

            do!
                launchStarted.Task.WaitAsync(TimeSpan.FromSeconds 5.0)

            let listRequestsDuringHold = host.ListRequestCount
            let startRequestsDuringHold = host.StartRequestCount
            let closeRequestsDuringHold = host.CloseRequestCount

            let!
                (listed,
                 rejectedStartResult,
                 rejectedCloseResult,
                 listRequestsAfterCalls,
                 startRequestsAfterCalls,
                 closeRequestsAfterCalls) =
                task {
                    try
                        let getTask =
                            EmbeddedTerminal.get manager
                            |> Async.StartAsTask

                        let startTask =
                            EmbeddedTerminal.start manager rejectedStart
                            |> Async.StartAsTask

                        let closeTask =
                            EmbeddedTerminal.close
                                manager
                                originalRunningTerminalId
                            |> Async.StartAsTask

                        let! snapshot =
                            getTask.WaitAsync(TimeSpan.FromSeconds 2.0)

                        let! startResult =
                            startTask.WaitAsync(TimeSpan.FromSeconds 2.0)

                        let! closeResult =
                            closeTask.WaitAsync(TimeSpan.FromSeconds 2.0)

                        return
                            snapshot,
                            startResult,
                            closeResult,
                            host.ListRequestCount,
                            host.StartRequestCount,
                            host.CloseRequestCount
                    finally
                        releaseLaunch.Set()
                }

            let! outcome =
                replacement.WaitAsync(TimeSpan.FromSeconds 5.0)

            Assert.Multiple(fun () ->
                Assert.That(
                    listed.Tabs |> List.map _.Worktree,
                    Is.EqualTo [ running ],
                    "polls must use the last authoritative snapshot while replacement owns the host"
                )

                Assert.That(
                    requireError rejectedStartResult,
                    Does.Contain("replacement is in progress")
                )

                Assert.That(
                    requireError rejectedCloseResult,
                    Does.Contain("replacement is in progress")
                )

                Assert.That(
                    listRequestsAfterCalls,
                    Is.EqualTo listRequestsDuringHold,
                    "the held poll must not contact a host that is between generations"
                )

                Assert.That(
                    startRequestsAfterCalls,
                    Is.EqualTo startRequestsDuringHold,
                    "the rejected start must not reach the old host"
                )

                Assert.That(
                    closeRequestsAfterCalls,
                    Is.EqualTo closeRequestsDuringHold,
                    "the rejected close must not reach the old host"
                )

                Assert.That(
                    outcome,
                    Is.EqualTo(
                        TerminalHostReplacement.ReplacementOutcome.Replaced
                            stagedVersion
                    )
                )

                Assert.That(
                    host.CurrentTerminals |> List.map _.WorktreePath,
                    Is.EqualTo [ WorktreePath.value running ],
                    "rejected operations must not remain queued after replacement"
                )

                Assert.That(host.CurrentExecutable, Is.EqualTo stagedExecutable))

            let! retriedStart =
                EmbeddedTerminal.start manager rejectedStart
                |> Async.StartAsTask

            requireOk retriedStart |> ignore

            let! current =
                EmbeddedTerminal.get manager
                |> Async.StartAsTask

            let runningTerminalId =
                current.Tabs
                |> List.find (fun tab ->
                    tab.Worktree = running)
                |> _.Id

            let! retriedClose =
                EmbeddedTerminal.close manager runningTerminalId
                |> Async.StartAsTask

            requireOk retriedClose |> ignore

            Assert.That(
                host.CurrentTerminals |> List.map _.WorktreePath,
                Is.EqualTo [ WorktreePath.value rejectedStart ],
                "lifecycle requests must recover after replacement completes"
            )
        }

    [<Test>]
    member _.``aged WaitingForUser on an exact owned session prevents replacement commit``() =
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

            let terminal = host.CurrentTerminals |> List.exactlyOne
            let awaitingAt = DateTimeOffset.UtcNow
            let backdatedLastSeen = awaitingAt - TimeSpan.FromMinutes 15.0
            let waitingSession: StoredStatus =
                { SessionId = SessionId "exact-owned-waiting-session"
                  TerminalSessionId =
                    Some(TerminalSessionId terminal.SessionId)
                  WorktreePath = target
                  Provider = CopilotCli
                  Status =
                    { emptyStatus with
                        Status = SessionLevelStatus.Working
                        AwaitingUserSince = Some awaitingAt }
                  UpdatedAt = awaitingAt
                  LastSeen = backdatedLastSeen
                  ContextUsageAt = None }

            let query now terminals =
                Assert.That(
                    now - waitingSession.LastSeen,
                    Is.GreaterThan stalenessTimeout,
                    "the regression must exercise both generic openness and freshness decay"
                )

                queryReplacementPlan
                    (fun _ -> Some CopilotCli)
                    (fun terminalSessionIds ->
                        Assert.That(
                            terminalSessionIds,
                            Is.EqualTo(
                                Set.singleton(
                                    TerminalSessionId terminal.SessionId
                                )
                            )
                        )

                        Ok(1L, [ waitingSession ]))
                    now
                    terminals

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
    member _.``replacement delivers only the policy command and recreates a plain shell``() =
        task {
            use host = new FakeControlHost()
            host.EnableLogicalReplacement()
            let stagedVersion = "2.0.0-resume"
            let stagedExecutable = host.Stage stagedVersion
            let launches = ConcurrentQueue<string * string option>()
            let submitted = ConcurrentQueue<string * string>()
            let staleTtyd = Path.Combine(host.Root, "stale", "ttyd.exe")

            let config =
                { replacementManagerConfig
                      host
                      (fun startInfo ->
                          launches.Enqueue(
                              startInfo.FileName,
                              argumentValue "--ttyd" startInfo
                          )

                          host.Activate(startInfo.FileName, stagedVersion)
                          Ok())
                      (fun endpoint command ->
                          async {
                              submitted.Enqueue(endpoint, command)
                              return Ok()
                          }) with
                    TtydExecutablePath = Some staleTtyd }

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
            let policyTerminals:
                TerminalHostReplacement.ReplacementTerminal list =
                before
                |> List.map (fun terminal ->
                    { TerminalSessionId = terminal.SessionId
                      WorktreePath = terminal.WorktreePath })

            let replacementCommand = "opaque replacement command"
            let queriedTerminals =
                ConcurrentQueue<TerminalHostReplacement.ReplacementTerminal list>()

            let query _ terminals =
                queriedTerminals.Enqueue terminals

                Ok(
                    TerminalHostReplacement.ReplacementSessionPlan.Ready(
                        21L,
                        Map.ofList
                            [ resumedTerminal.SessionId, replacementCommand ]
                    )
                )

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
                    Is.EqualTo(
                        [| (stagedExecutable,
                            TerminalHostLayout.adjacentTtydExecutablePath
                                stagedExecutable) |]
                    ),
                    "the staged host must use ttyd from its own bundle"
                )

                Assert.That(host.ShutdownRequestCount, Is.EqualTo(1))
                Assert.That(submitted.Count, Is.EqualTo(1))
                Assert.That(
                    resumedAfter.WorktreePath,
                    Is.EqualTo(WorktreePath.value resumedPath)
                )
                Assert.That(
                    command,
                    Is.EqualTo replacementCommand
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
                    queriedTerminals.ToArray(),
                    Has.All.EqualTo(policyTerminals)
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
            let launches = ConcurrentQueue<string * string option>()
            let staleTtyd = Path.Combine(host.Root, "stale", "ttyd.exe")

            let launch (startInfo: ProcessStartInfo) =
                launches.Enqueue(
                    startInfo.FileName,
                    argumentValue "--ttyd" startInfo
                )

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
                { replacementManagerConfig
                      host
                      launch
                      (fun _ _ -> async { return Ok() }) with
                    TtydExecutablePath = Some staleTtyd }
                |> EmbeddedTerminal.createWithConfig

            let target = worktree host.Root "recover-old"

            let! started =
                EmbeddedTerminal.start manager target
                |> Async.StartAsTask

            requireOk started |> ignore

            let query _ _ =
                Ok(
                    TerminalHostReplacement.ReplacementSessionPlan.Ready(
                        12L,
                        Map.empty
                    )
                )

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
                        [| (stagedExecutable,
                            TerminalHostLayout.adjacentTtydExecutablePath
                                stagedExecutable)
                           (host.OldExecutable,
                            TerminalHostLayout.adjacentTtydExecutablePath
                                host.OldExecutable) |]
                    ),
                    "staged launch and rollback must each use their own sibling ttyd"
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
    member _.``incomplete staged bundle is rejected before the live host is stopped``() =
        task {
            use host = new FakeControlHost()
            host.EnableLogicalReplacement()
            let stagedVersion = "2.0.0-incomplete"
            let stagedExecutable = host.Stage stagedVersion

            stagedExecutable
            |> TerminalHostLayout.adjacentTtydExecutablePath
            |> Option.iter File.Delete

            let launches = ConcurrentQueue<unit>()

            let manager =
                replacementManagerConfig
                    host
                    (fun _ ->
                        launches.Enqueue()
                        Error "An incomplete bundle must not launch")
                    (fun _ _ -> async { return Ok() })
                |> EmbeddedTerminal.createWithConfig

            let target = worktree host.Root "incomplete-stage"

            let! started =
                EmbeddedTerminal.start manager target
                |> Async.StartAsTask

            requireOk started |> ignore

            let query _ _ =
                Ok(
                    TerminalHostReplacement.ReplacementSessionPlan.Ready(
                        13L,
                        Map.empty
                    )
                )

            let! outcome =
                EmbeddedTerminal.tryReplaceHost query manager
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
                    Assert.Fail($"Expected incomplete bundle failure, got {other}")
                    ""

            Assert.Multiple(fun () ->
                Assert.That(failure, Does.Contain("bundle member"))
                Assert.That(
                    failure,
                    Does.Contain(TerminalHostLayout.TtydExecutableName)
                )
                Assert.That(host.ShutdownRequestCount, Is.Zero)
                Assert.That(launches, Is.Empty)
                Assert.That(host.IsOnline, Is.True))
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

            let query _ _ =
                Ok(
                    TerminalHostReplacement.ReplacementSessionPlan.Ready(
                        15L,
                        Map.empty
                    )
                )

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
    member _.``cleanup reservation rejects the same canonical path while unrelated starts remain available``() =
        task {
            use host = new FakeControlHost()
            host.PublishManifest()
            let manager =
                EmbeddedTerminal.createWithConfig(managerConfig host noLaunch)

            let target = worktree host.Root "reserved-target"
            let unrelated = worktree host.Root "reserved-unrelated"

            let! initial =
                EmbeddedTerminal.start manager target
                |> Async.StartAsTask

            requireOk initial |> ignore

            let! alternate =
                EmbeddedTerminal.start manager target
                |> Async.StartAsTask

            requireOk alternate |> ignore

            let operationEntered =
                TaskCompletionSource<unit>(
                    TaskCreationOptions.RunContinuationsAsynchronously
                )

            let releaseOperation =
                TaskCompletionSource<unit>(
                    TaskCreationOptions.RunContinuationsAsynchronously
                )

            let firstCleanup =
                EmbeddedTerminal.withReservedCleanup
                    manager
                    target
                    (fun () ->
                        async {
                            operationEntered.TrySetResult() |> ignore
                            do! releaseOperation.Task |> Async.AwaitTask
                            return Ok()
                        })
                |> Async.StartAsTask

            do!
                operationEntered.Task.WaitAsync(
                    TimeSpan.FromSeconds 5.0
                )

            let alias =
                WorktreePath(
                    WorktreePath.value target
                    + string Path.DirectorySeparatorChar
                )

            let secondOperationEntered =
                TaskCompletionSource<unit>(
                    TaskCreationOptions.RunContinuationsAsynchronously
                )

            let! secondCleanup =
                EmbeddedTerminal.withReservedCleanup
                    manager
                    alias
                    (fun () ->
                        async {
                            secondOperationEntered.TrySetResult()
                            |> ignore

                            return Ok()
                        })
                |> Async.StartAsTask
                |> _.WaitAsync(TimeSpan.FromSeconds 2.0)

            let! samePathStart =
                EmbeddedTerminal.start manager alias
                |> Async.StartAsTask
                |> _.WaitAsync(TimeSpan.FromSeconds 2.0)

            let! unrelatedStart =
                EmbeddedTerminal.start manager unrelated
                |> Async.StartAsTask
                |> _.WaitAsync(TimeSpan.FromSeconds 2.0)

            releaseOperation.TrySetResult() |> ignore

            let! firstResult =
                firstCleanup.WaitAsync(TimeSpan.FromSeconds 5.0)

            let! restarted =
                EmbeddedTerminal.start manager target
                |> Async.StartAsTask
                |> _.WaitAsync(TimeSpan.FromSeconds 2.0)

            Assert.Multiple(fun () ->
                Assert.That(
                    requireError secondCleanup,
                    Does.Contain("cleanup is in progress")
                )

                Assert.That(
                    secondOperationEntered.Task.IsCompleted,
                    Is.False,
                    "a rejected cleanup must not run its mutation"
                )

                Assert.That(
                    requireError samePathStart,
                    Does.Contain("cleanup is in progress")
                )

                requireOk unrelatedStart |> ignore
                requireOk firstResult |> ignore
                requireOk restarted |> ignore
                Assert.That(
                    host.CloseRequestCount,
                    Is.EqualTo(2),
                    "cleanup must close every terminal owned by the worktree"
                )

                Assert.That(
                    host.CurrentTerminals |> List.map _.WorktreePath,
                    Is.EquivalentTo(
                        [ WorktreePath.value target
                          WorktreePath.value unrelated ]
                    )
                ))
        }

    [<Test>]
    member _.``failed cleanup releases its canonical path reservation``() =
        task {
            use host = new FakeControlHost()
            host.PublishManifest()
            let manager =
                EmbeddedTerminal.createWithConfig(managerConfig host noLaunch)
            let target = worktree host.Root "failed-cleanup"

            let! initial =
                EmbeddedTerminal.start manager target
                |> Async.StartAsTask

            requireOk initial |> ignore

            let! failed =
                EmbeddedTerminal.withReservedCleanup
                    manager
                    target
                    (fun () ->
                        async {
                            return Error "mutation failed"
                        })
                |> Async.StartAsTask

            let! restarted =
                EmbeddedTerminal.start manager target
                |> Async.StartAsTask
                |> _.WaitAsync(TimeSpan.FromSeconds 2.0)

            Assert.Multiple(fun () ->
                Assert.That(
                    requireError failed,
                    Is.EqualTo("mutation failed")
                )

                requireOk restarted |> ignore
                Assert.That(
                    host.CurrentTerminals |> List.map _.WorktreePath,
                    Is.EqualTo [ WorktreePath.value target ]
                ))
        }

    [<Test>]
    member _.``cancelled cleanup releases its canonical path reservation``() =
        task {
            use host = new FakeControlHost()
            host.PublishManifest()
            let manager =
                EmbeddedTerminal.createWithConfig(managerConfig host noLaunch)
            let target = worktree host.Root "cancelled-cleanup"

            let! initial =
                EmbeddedTerminal.start manager target
                |> Async.StartAsTask

            requireOk initial |> ignore

            let operationEntered =
                TaskCompletionSource<unit>(
                    TaskCreationOptions.RunContinuationsAsynchronously
                )

            use cancellation = new System.Threading.CancellationTokenSource()

            let cleanup =
                EmbeddedTerminal.withReservedCleanup
                    manager
                    target
                    (fun () ->
                        async {
                            operationEntered.TrySetResult() |> ignore
                            do! Async.Sleep(TimeSpan.FromMinutes 5.0)
                            return Ok()
                        })
                |> fun workflow ->
                    Async.StartAsTask(
                        workflow,
                        cancellationToken = cancellation.Token
                    )

            do!
                operationEntered.Task.WaitAsync(
                    TimeSpan.FromSeconds 5.0
                )

            cancellation.Cancel()

            try
                let! result =
                    cleanup.WaitAsync(TimeSpan.FromSeconds 5.0)

                Assert.Fail(
                    $"Expected cleanup cancellation, got {result}"
                )
            with :? OperationCanceledException ->
                ()

            let! restarted =
                EmbeddedTerminal.start manager target
                |> Async.StartAsTask
                |> _.WaitAsync(TimeSpan.FromSeconds 2.0)

            requireOk restarted |> ignore
        }

    [<Test>]
    member _.``partial multi-terminal close failure keeps the worktree mutation blocked``() =
        task {
            // Kestrel callbacks may close two fixture terminals; mutation stays at this test boundary.
            let mutable closeAttempts = 0
            use host =
                new FakeControlHost(
                    onTerminalClosing = fun _ ->
                        closeAttempts <- closeAttempts + 1

                        if closeAttempts = 2 then
                            raise (
                                InvalidOperationException(
                                    "simulated second terminal close failure"
                                )
                            )
                )

            host.PublishManifest()
            let manager =
                EmbeddedTerminal.createWithConfig(managerConfig host noLaunch)
            let target = worktree host.Root "partial-close"

            for _ in 1..2 do
                let! started =
                    EmbeddedTerminal.start manager target
                    |> Async.StartAsTask

                requireOk started |> ignore

            let mutationEntered =
                TaskCompletionSource<unit>(
                    TaskCreationOptions.RunContinuationsAsynchronously
                )

            let! result =
                EmbeddedTerminal.withReservedCleanup
                    manager
                    target
                    (fun () ->
                        async {
                            mutationEntered.TrySetResult() |> ignore
                            return Ok()
                        })
                |> Async.StartAsTask

            Assert.Multiple(fun () ->
                Assert.That(requireError result, Is.Not.Empty)
                Assert.That(closeAttempts, Is.EqualTo(2))
                Assert.That(
                    host.CurrentTerminals
                    |> List.filter (fun terminal ->
                        terminal.WorktreePath = WorktreePath.value target)
                    |> List.length,
                    Is.EqualTo(1)
                )
                Assert.That(mutationEntered.Task.IsCompleted, Is.False))
        }

    [<Test>]
    member _.``strict close failure releases its canonical path reservation``() =
        task {
            use host =
                new FakeControlHost(
                    onTerminalClosing = fun _ ->
                        raise (
                            InvalidOperationException(
                                "simulated terminal close failure"
                            )
                        )
                )

            host.PublishManifest()
            let manager =
                EmbeddedTerminal.createWithConfig(managerConfig host noLaunch)
            let target = worktree host.Root "failed-close"

            let! initial =
                EmbeddedTerminal.start manager target
                |> Async.StartAsTask

            requireOk initial |> ignore

            let mutationEntered =
                TaskCompletionSource<unit>(
                    TaskCreationOptions.RunContinuationsAsynchronously
                )

            let! failed =
                EmbeddedTerminal.withReservedCleanup
                    manager
                    target
                    (fun () ->
                        async {
                            mutationEntered.TrySetResult() |> ignore
                            return Ok()
                        })
                |> Async.StartAsTask

            let! reused =
                EmbeddedTerminal.start manager target
                |> Async.StartAsTask
                |> _.WaitAsync(TimeSpan.FromSeconds 2.0)

            Assert.Multiple(fun () ->
                Assert.That(requireError failed, Is.Not.Empty)

                Assert.That(
                    mutationEntered.Task.IsCompleted,
                    Is.False,
                    "a failed strict close must not run the mutation"
                )

                requireOk reused |> ignore)
        }

    [<TestCase(null)>]
    [<TestCase("\u0000")>]
    member _.``malformed terminal ID is rejected without stopping the lifecycle mailbox``(invalidId: string) =
        task {
            use host = new FakeControlHost()
            host.PublishManifest()
            let manager =
                EmbeddedTerminal.createWithConfig(managerConfig host noLaunch)

            let! malformedClose =
                EmbeddedTerminal.close
                    manager
                    (EmbeddedTerminalId invalidId)
                |> Async.StartAsTask
                |> _.WaitAsync(TimeSpan.FromSeconds 2.0)

            let target = worktree host.Root "after-malformed-close"

            let! started =
                EmbeddedTerminal.start manager target
                |> Async.StartAsTask
                |> _.WaitAsync(TimeSpan.FromSeconds 2.0)

            Assert.Multiple(fun () ->
                Assert.That(
                    requireError malformedClose,
                    Is.EqualTo("Invalid embedded terminal ID")
                )
                requireOk started |> ignore)
        }

    [<Test>]
    [<Platform("Win")>]
    member _.``cleanup blocks a replacement terminal process whose CWD is inside the worktree``() =
        task {
            let targetDirectory =
                uniquePath "terminal-cleanup-cwd"

            Directory.CreateDirectory targetDirectory |> ignore
            let target = PathUtils.toWorktreePath targetDirectory
            let processGate = obj()
            // Kestrel callbacks own this one fixture process, so mutation stays at the fake-host boundary.
            let mutable terminalProcess: Process option = None

            let matchingTarget path =
                Shared.PathUtils.pathEquals
                    (PathUtils.normalizePath path)
                    (WorktreePath.value target)

            let startTerminalProcess path =
                if matchingTarget path then
                    let startInfo =
                        ProcessStartInfo(
                            FileName = "pwsh.exe",
                            WorkingDirectory = targetDirectory,
                            UseShellExecute = false,
                            CreateNoWindow = true
                        )

                    [ "-NoLogo"
                      "-NoProfile"
                      "-NonInteractive"
                      "-Command"
                      "Start-Sleep -Seconds 300" ]
                    |> List.iter startInfo.ArgumentList.Add

                    let started = Process.Start startInfo

                    if isNull started then
                        failwith "The fixture terminal process did not start"

                    lock processGate (fun () ->
                        terminalProcess <- Some started)

            let closeTerminalProcess path =
                if matchingTarget path then
                    let current =
                        lock processGate (fun () ->
                            terminalProcess)

                    match current with
                    | None -> ()
                    | Some running ->
                        if not running.HasExited then
                            running.Kill(entireProcessTree = true)

                        if not (running.WaitForExit 5_000) then
                            failwith
                                "The fixture terminal process did not exit"

                        running.Dispose()

                        lock processGate (fun () ->
                            terminalProcess <- None)

            try
                use host =
                    new FakeControlHost(
                        onTerminalStarted = startTerminalProcess,
                        onTerminalClosing = closeTerminalProcess
                    )

                host.PublishManifest()
                let manager =
                    EmbeddedTerminal.createWithConfig(
                        managerConfig host noLaunch
                    )

                let! initial =
                    EmbeddedTerminal.start manager target
                    |> Async.StartAsTask

                requireOk initial |> ignore

                let operationEntered =
                    TaskCompletionSource<unit>(
                        TaskCreationOptions.RunContinuationsAsynchronously
                    )

                let releaseOperation =
                    TaskCompletionSource<unit>(
                        TaskCreationOptions.RunContinuationsAsynchronously
                    )

                let cleanup =
                    EmbeddedTerminal.withReservedCleanup
                        manager
                        target
                        (fun () ->
                            async {
                                operationEntered.TrySetResult()
                                |> ignore

                                do!
                                    releaseOperation.Task
                                    |> Async.AwaitTask

                                try
                                    Directory.Delete(
                                        targetDirectory,
                                        recursive = true
                                    )

                                    return Ok()
                                with ex ->
                                    return Error ex.Message
                            })
                    |> Async.StartAsTask

                do!
                    operationEntered.Task.WaitAsync(
                        TimeSpan.FromSeconds 5.0
                    )

                let alias =
                    WorktreePath(
                        targetDirectory
                        + string Path.DirectorySeparatorChar
                    )

                let! concurrentStart =
                    EmbeddedTerminal.start manager alias
                    |> Async.StartAsTask
                    |> _.WaitAsync(TimeSpan.FromSeconds 2.0)

                releaseOperation.TrySetResult() |> ignore

                let! cleanupResult =
                    cleanup.WaitAsync(TimeSpan.FromSeconds 5.0)

                Assert.Multiple(fun () ->
                    Assert.That(
                        requireError concurrentStart,
                        Does.Contain("cleanup is in progress")
                    )

                    requireOk cleanupResult |> ignore

                    Assert.That(
                        Directory.Exists targetDirectory,
                        Is.False,
                        "the closed terminal must not retain its worktree CWD"
                    ))
            finally
                let remaining =
                    lock processGate (fun () ->
                        terminalProcess)

                match remaining with
                | Some running ->
                    if not running.HasExited then
                        running.Kill(entireProcessTree = true)

                    running.WaitForExit 5_000 |> ignore
                    running.Dispose()
                | None -> ()

                if Directory.Exists targetDirectory then
                    Directory.Delete(
                        targetDirectory,
                        recursive = true
                    )
        }

    [<Test>]
    member _.``delete and archive fail cleanly during replacement and succeed when retried``() =
        task {
            use host = new FakeControlHost()
            host.EnableLogicalReplacement()
            let stagedVersion = "2.0.0-held-cleanup"
            host.Stage stagedVersion |> ignore

            let launchStarted =
                TaskCompletionSource<unit>(
                    TaskCreationOptions.RunContinuationsAsynchronously
                )

            use releaseLaunch =
                new System.Threading.ManualResetEventSlim(false)

            let manager =
                replacementManagerConfig
                    host
                    (fun startInfo ->
                        launchStarted.TrySetResult() |> ignore

                        if releaseLaunch.Wait(TimeSpan.FromSeconds 5.0) then
                            host.Activate(startInfo.FileName, stagedVersion)
                            Ok()
                        else
                            Error "Timed out waiting to release the staged host launch")
                    (fun _ _ -> async { return Ok() })
                |> EmbeddedTerminal.createWithConfig

            let repoRoot = Path.Combine(host.Root, "held-cleanup-repo")
            Directory.CreateDirectory repoRoot |> ignore
            let deleteTarget = worktree repoRoot "delete-target"
            let archiveTarget = worktree repoRoot "archive-target"
            let untouched = worktree repoRoot "untouched"

            for path in [ deleteTarget; archiveTarget; untouched ] do
                let! started =
                    EmbeddedTerminal.start manager path
                    |> Async.StartAsTask

                requireOk started |> ignore

            let agent = SchedulerState.createAgent()
            let repoId = PathUtils.toRepoId repoRoot

            let worktrees =
                [ { Path = WorktreePath.value deleteTarget
                    Head = "delete-head"
                    Branch = Some "delete-target" }
                  { Path = WorktreePath.value archiveTarget
                    Head = "archive-head"
                    Branch = Some "archive-target" }
                  { Path = WorktreePath.value untouched
                    Head = "untouched-head"
                    Branch = Some "untouched" } ]

            do! populateAgent agent repoId worktrees

            let removeCalls = ConcurrentQueue<string>()
            let stateCleanupCalls = ConcurrentQueue<string>()

            let delete () =
                WorktreeApi.deleteWorktreeWith
                    (fun _ path _ ->
                        async {
                            removeCalls.Enqueue path
                            return Ok()
                        })
                    (EmbeddedTerminal.withReservedCleanup manager)
                    (fun path ->
                        async {
                            stateCleanupCalls.Enqueue path
                        })
                    agent
                    (Map.ofList [ repoId, repoRoot ])
                    deleteTarget

            let archive () =
                WorktreeApi.updateArchivedBranchesWith
                    agent
                    (Map.ofList [ repoId, repoRoot ])
                    (EmbeddedTerminal.withReservedCleanup manager)
                    Set.add
                    archiveTarget

            let query _ _ =
                Ok(
                    TerminalHostReplacement.ReplacementSessionPlan.Ready(
                        32L,
                        Map.empty
                    )
                )

            let replacement =
                EmbeddedTerminal.tryReplaceHost query manager
                |> Async.StartAsTask

            do!
                launchStarted.Task.WaitAsync(TimeSpan.FromSeconds 5.0)

            let! rejectedDelete, rejectedArchive, retainedBeforeRetry =
                task {
                    try
                        let! deleteResult =
                            delete ()
                            |> Async.StartAsTask
                            |> _.WaitAsync(TimeSpan.FromSeconds 2.0)

                        let! archiveResult =
                            archive ()
                            |> Async.StartAsTask
                            |> _.WaitAsync(TimeSpan.FromSeconds 2.0)

                        let! state =
                            agent.PostAndAsyncReply(SchedulerState.StateMsg.GetState)
                            |> Async.StartAsTask

                        let retained =
                            state.Repos[repoId].WorktreeList
                            |> List.map _.Path

                        return deleteResult, archiveResult, retained
                    finally
                        releaseLaunch.Set()
                }

            let! outcome =
                replacement.WaitAsync(TimeSpan.FromSeconds 5.0)

            Assert.Multiple(fun () ->
                Assert.That(
                    requireError rejectedDelete,
                    Does.Contain("replacement is in progress")
                )

                Assert.That(
                    requireError rejectedArchive,
                    Does.Contain("replacement is in progress")
                )

                Assert.That(
                    retainedBeforeRetry,
                    Is.EquivalentTo(worktrees |> List.map _.Path),
                    "a rejected delete must leave scheduler state visible to the client"
                )

                Assert.That(removeCalls, Is.Empty)
                Assert.That(stateCleanupCalls, Is.Empty)
                Assert.That(
                    TreemonConfig.readArchivedBranches repoRoot,
                    Does.Not.Contain("archive-target")
                )

                Assert.That(
                    outcome,
                    Is.EqualTo(
                        TerminalHostReplacement.ReplacementOutcome.Replaced
                            stagedVersion
                    )
                )

                Assert.That(
                    host.CurrentTerminals |> List.map _.WorktreePath,
                    Is.EquivalentTo(worktrees |> List.map _.Path),
                    "rejected cleanup requests must not execute after replacement"
                ))

            let! retriedDelete =
                delete ()
                |> Async.StartAsTask

            let! retriedArchive =
                archive ()
                |> Async.StartAsTask

            requireOk retriedDelete |> ignore
            requireOk retriedArchive |> ignore

            let! stateAfterRetry =
                agent.PostAndAsyncReply(SchedulerState.StateMsg.GetState)
                |> Async.StartAsTask

            Assert.Multiple(fun () ->
                Assert.That(
                    stateAfterRetry.Repos[repoId].WorktreeList
                    |> List.map _.Path,
                    Does.Not.Contain(WorktreePath.value deleteTarget)
                )

                Assert.That(
                    TreemonConfig.readArchivedBranches repoRoot,
                    Does.Contain("archive-target")
                )

                Assert.That(
                    removeCalls.ToArray(),
                    Is.EqualTo [| WorktreePath.value deleteTarget |]
                )

                Assert.That(
                    stateCleanupCalls.ToArray(),
                    Is.EqualTo [| WorktreePath.value deleteTarget |]
                )

                Assert.That(
                    host.CurrentTerminals |> List.map _.WorktreePath,
                    Is.EqualTo [ WorktreePath.value untouched ]
                ))
        }

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
                    (fun path operation ->
                        EmbeddedTerminal.withReservedCleanup
                            manager
                            path
                            (fun () ->
                                async {
                                    calls.Enqueue "close"
                                    return! operation ()
                                }))
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
                    (EmbeddedTerminal.withReservedCleanup manager)
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
