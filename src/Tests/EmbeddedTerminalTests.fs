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
open global.Server.SessionActivityService
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
    let startRequestBodies = ConcurrentQueue<string>()
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
            startRequestBodies.Enqueue(document.RootElement.GetRawText())

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
    member _.StartRequestBodies = startRequestBodies.ToArray() |> Array.toList
    member _.CloseRequestCount = closeRequests.Count
    member _.ClosedSessionIds = closeRequests.ToArray() |> Array.toList
    member _.ShutdownRequestCount = shutdownRequests.Count
    member _.OldExecutable = oldExecutable
    member _.CurrentExecutable = lock gate (fun () -> currentExecutable)
    member _.CurrentHostVersion = lock gate (fun () -> hostVersion)
    member _.IsOnline = lock gate (fun () -> online)

    member _.PublishManifest() =
        let manifest = createManifest ()
        File.WriteAllText(manifestPath, manifest.ToJsonString())

    member _.RemoveManifest() =
        File.Delete manifestPath

    member _.PublishManifestWithJsonField(fieldName: string, jsonValue: string) =
        let manifest = createManifest ()
        manifest[fieldName] <- JsonNode.Parse jsonValue
        File.WriteAllText(manifestPath, manifest.ToJsonString())

    /// Serves `content` for every later registry read, so a replacement host can report a body the
    /// client cannot parse or terminals that generation never created.
    member _.OverrideRegistryResponse(content: string) =
        lock gate (fun () -> registryJsonOverride <- Some content)

    /// Freezes the current registry, which a later host generation reports as unexpected terminals.
    member this.FreezeRegistryResponse() =
        this.OverrideRegistryResponse(
            JsonSerializer.Serialize(snapshot (), jsonOptions)
        )

    member this.ReturnRegistryWithJsonField(fieldName: string, jsonValue: string) =
        let registry =
            JsonNode.Parse(
                JsonSerializer.Serialize(snapshot (), jsonOptions)
            )
            |> _.AsObject()

        let terminals = registry["terminals"].AsArray()
        let terminal = terminals[0].AsObject()

        terminal[fieldName] <- JsonNode.Parse jsonValue

        this.OverrideRegistryResponse(registry.ToJsonString())

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

    member this.ResolveProcessIdentity(pid: int) =
        if this.IsCurrentProcessLive(pid, currentStartTicks) then
            ProcessIdentity.create pid currentStartTicks
            |> Result.map Some
        else
            Ok None

    member this.ResolveExactProcessExecutable(pid: int, startTicks: int64) =
        if this.IsCurrentProcessLive(pid, startTicks) then
            Ok(lock gate (fun () -> currentExecutable))
        else
            Error "Fake TerminalHost identity is not live"

    member _.SimulateProcessExit() =
        lock gate (fun () -> online <- false)

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

    member _.RemoveTerminal sessionId =
        closeTerminal sessionId |> ignore

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

let private typedSessionId value =
    SessionId.create value
    |> Result.defaultWith invalidOp

let private typedTerminalSessionId value =
    TerminalSessionId.create value
    |> Result.defaultWith invalidOp

let private replacementResume sessionId command:
    TerminalHostReplacement.ReplacementResumeCommand =
    { CopilotSessionId = typedSessionId sessionId
      Command = command }

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
      ProcessExitTimeout = TimeSpan.FromSeconds 30.0
      LaunchHost = launchHost
      ProcessIdentityResolver =
        ProcessIdentityResolverRuntime.defaultResolver
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
        ProcessIdentityResolver =
            ProcessIdentityResolver.create
                host.ResolveProcessIdentity
        ResolveProcessExecutable =
            fun pid startTicks ->
                host.ResolveExactProcessExecutable(pid, startTicks)
        SendTerminalCommand = sendTerminalCommand }

/// Replacement config whose launch records the started bundle and activates it as the new host.
let private activatingReplacementConfig
    (host: FakeControlHost)
    stagedVersion
    (launches: ConcurrentQueue<string>)
    sendTerminalCommand
    =
    replacementManagerConfig
        host
        (fun startInfo ->
            launches.Enqueue startInfo.FileName
            host.Activate(startInfo.FileName, stagedVersion)
            Ok())
        sendTerminalCommand

let private shutdownSessionsUsing shutdown targets =
    async {
        let! outcomes =
            targets
            |> List.map (fun target ->
                async {
                    let! outcome = shutdown target

                    return
                        ({ Target = target
                           Outcome = outcome }
                         : TerminalHostReplacement.ReplacementShutdownAttempt)
                })
            |> Async.Sequential

        return outcomes |> Array.toList
    }

let private defaultReplacementOperations =
    TerminalHostReplacement.defaultOperations
        (fun () -> async { return Ok Set.empty })

let private exactIdentity processId startTicks =
    ProcessIdentity.create processId startTicks
    |> Result.defaultWith invalidOp

/// `FakeControlHost` publishes the test process as the live host identity.
let private currentProcessIdentity =
    use current = Process.GetCurrentProcess()

    exactIdentity
        current.Id
        (current.StartTime.ToUniversalTime().Ticks)

let private runReplacementCommitWithDiagnostics
    diagnostics
    config
    query
    operations
    =
    task {
        let resolved =
            TaskCompletionSource<TerminalHostReplacement.ReplacementResolution>(
                TaskCreationOptions.RunContinuationsAsynchronously
            )

        let commit plan activityQuery =
            async {
                let! resolution =
                    TerminalHostReplacement.commitReplacementWithDiagnostics
                        diagnostics
                        operations
                        config
                        plan
                        activityQuery

                resolved.TrySetResult resolution |> ignore

                return
                    TerminalHostReplacement.replacementResolutionOutcome
                        resolution
            }

        let! outcome =
            TerminalHostReplacement.tryReplaceHostIgnoring
                None
                (fun () -> async.Return())
                query
                config
                commit
            |> Async.StartAsTask

        let! resolution =
            resolved.Task.WaitAsync(TimeSpan.FromSeconds 5.0)

        return outcome, resolution
    }

let private replacementStages
    (diagnostics: ConcurrentQueue<LifecycleDiagnostics.Diagnostic>)
    =
    diagnostics.ToArray()
    |> Array.choose (function
        | LifecycleDiagnostics.Diagnostic.ReplacementTransition stage ->
            Some stage
        | _ -> None)

let private teardownStages
    (diagnostics: ConcurrentQueue<LifecycleDiagnostics.Diagnostic>)
    =
    diagnostics.ToArray()
    |> Array.choose (function
        | LifecycleDiagnostics.Diagnostic.TeardownTransition stage -> Some stage
        | _ -> None)

let private replacementTarget
    (terminal: FakeTerminal)
    copilotSessionId
    processIdentity
    :
    TerminalHostReplacement.ReplacementShutdownTarget =
    { TerminalSessionId = typedTerminalSessionId terminal.SessionId
      WorktreePath = terminal.WorktreePath
      CopilotSessionId = typedSessionId copilotSessionId
      ProcessIdentity = processIdentity }

let private worktree (root: string) (name: string) =
    let path = Path.Combine(root, name)
    Directory.CreateDirectory path |> ignore
    PathUtils.toWorktreePath path

/// A one-shot rendezvous a test can await while a callback completes it.
let private signal () =
    TaskCompletionSource<unit>(TaskCreationOptions.RunContinuationsAsynchronously)

/// Stops only the fixture-owned exact PIDs it was handed, never a shared host process.
let rec private stopFixtureProcesses (processes: ConcurrentQueue<Process>) =
    match processes.TryDequeue() with
    | false, _ -> ()
    | true, running ->
        if not running.HasExited then
            running.Kill(entireProcessTree = true)

        running.WaitForExit 5_000 |> ignore
        running.Dispose()
        stopFixtureProcesses processes

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

type private ReplacementScenarioFixture =
    { Manager: EmbeddedTerminal.Manager
      Terminals: FakeTerminal list }

module private ReplacementScenarioFixture =
    /// Creates a manager for `config`, opens each named worktree under `host.Root` in
    /// opening order, and captures the resulting terminals for scenario-specific setup.
    let create (host: FakeControlHost) config worktreeNames =
        task {
            let manager = EmbeddedTerminal.createWithConfig config

            for path in worktreeNames |> List.map (worktree host.Root) do
                let! started = EmbeddedTerminal.start manager path |> Async.StartAsTask
                requireOk started |> ignore

            return
                { Manager = manager
                  Terminals = host.CurrentTerminals }
        }

let private closeManagedTerminal manager terminalId =
    WorktreeCleanup.closeEmbeddedTerminalWith
        WorktreeCleanup.noSessionClose
        manager
        terminalId

let private withManagedTerminalCleanup manager worktreePath operation =
    WorktreeCleanup.withTerminalCleanup
        WorktreeCleanup.noSessionClose
        manager
        worktreePath
        operation

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

/// One published fake host with `terminalCount` terminals open on a single worktree: the shared
/// starting point of every explicit-teardown scenario. `onTerminalClosing` injects host-side close
/// failures.
let private withHostScenario
    onTerminalClosing
    buildConfig
    name
    terminalCount
    (scenario:
        FakeControlHost -> EmbeddedTerminal.Manager -> WorktreePath -> Task<unit>)
    =
    task {
        use host = new FakeControlHost(onTerminalClosing = onTerminalClosing)
        host.PublishManifest()
        let manager = EmbeddedTerminal.createWithConfig(buildConfig host)
        let target = worktree host.Root name

        for _ in 1..terminalCount do
            let! started =
                EmbeddedTerminal.start manager target
                |> Async.StartAsTask

            requireOk started |> ignore

        do! scenario host manager target
    }

let private withClosingHost onTerminalClosing name terminalCount scenario =
    withHostScenario
        onTerminalClosing
        (fun host -> managerConfig host noLaunch)
        name
        terminalCount
        scenario

let private withCleanupScenario name terminalCount scenario =
    withClosingHost ignore name terminalCount scenario

/// The same scenario under the replacement-capable config, whose exact process-identity resolver
/// makes recorded-host liveness observable.
let private withRecordedHostScenario name scenario =
    withHostScenario
        ignore
        (fun host -> replacementManagerConfig host noLaunch noTerminalCommand)
        name
        1
        scenario

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
    [<Test>]
    member _.``host exit waiting uses the dedicated process timeout``() =
        task {
            use host = new FakeControlHost()
            host.PublishManifest()
            let baseConfig = managerConfig host noLaunch

            let manifest =
                match TerminalHostManifest.readManifest baseConfig with
                | Ok(Some value) -> value
                | other ->
                    Assert.Fail($"Expected a valid manifest, got {other}")
                    Unchecked.defaultof<_>

            let identity =
                TerminalHostManifest.tryProcessIdentity manifest
                |> Option.defaultWith (fun () ->
                    Assert.Fail("Expected a valid process identity")
                    Unchecked.defaultof<_>)

            let probes = ConcurrentQueue<unit>()

            let resolver =
                ProcessIdentityResolver.create (fun _ ->
                    probes.Enqueue()

                    if probes.Count = 1 then
                        Ok(Some identity)
                    else
                        Ok None)

            let config =
                { baseConfig with
                    StartupTimeout = TimeSpan.Zero
                    ProcessExitTimeout = TimeSpan.FromSeconds 1.0
                    ProbeInterval = TimeSpan.FromMilliseconds 1.0
                    ProcessIdentityResolver = resolver }

            let! result =
                TerminalHostClient.waitForHostExit config manifest
                |> Async.StartAsTask

            match result with
            | Error error -> Assert.Fail($"Expected confirmed process exit, got: {error}")
            | Ok () -> Assert.That(probes.Count, Is.EqualTo(2))
        }

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
    member _.``plain start returns the reconciled snapshot and exact terminal identity``() =
        task {
            use host = new FakeControlHost()
            host.PublishManifest()

            let manager =
                EmbeddedTerminal.createWithConfig(managerConfig host noLaunch)

            let target = worktree host.Root "plain-start"

            let! result =
                EmbeddedTerminal.start manager target
                |> Async.StartAsTask

            let started = requireOk result
            let terminal = host.CurrentTerminals |> List.exactlyOne

            Assert.Multiple(fun () ->
                Assert.That(
                    started.TerminalId,
                    Is.EqualTo(EmbeddedTerminalId terminal.SessionId)
                )

                Assert.That(
                    started.Snapshot.Tabs |> List.map _.Id,
                    Is.EqualTo([ started.TerminalId ])
                )

                Assert.That(host.StartRequestCount, Is.EqualTo(1))
                Assert.That(host.CloseRequestCount, Is.Zero))
        }

    [<Test>]
    member _.``command start uses the attachment transport and returns the exact new sibling``() =
        task {
            use host = new FakeControlHost()
            host.PublishManifest()
            let submissions = ConcurrentQueue<string * string>()

            let config =
                { managerConfig host noLaunch with
                    SendTerminalCommand =
                        fun endpoint command ->
                            async {
                                submissions.Enqueue((endpoint, command))
                                return Ok()
                            } }

            let manager = EmbeddedTerminal.createWithConfig config
            let target = worktree host.Root "command-start"

            let! existing =
                EmbeddedTerminal.start manager target
                |> Async.StartAsTask

            let existingId = (requireOk existing).TerminalId
            let command = "Write-Output command-started"

            let! result =
                EmbeddedTerminal.startWithCommand manager target command
                |> Async.StartAsTask

            let started = requireOk result
            let terminals = host.CurrentTerminals
            let exact =
                terminals
                |> List.find (fun terminal ->
                    terminal.SessionId = EmbeddedTerminalId.value started.TerminalId)

            let submittedEndpoint, submittedCommand =
                submissions.ToArray() |> Array.exactlyOne

            Assert.Multiple(fun () ->
                Assert.That(started.TerminalId, Is.Not.EqualTo existingId)
                Assert.That(submittedEndpoint, Is.EqualTo exact.AttachmentEndpoint)
                Assert.That(submittedCommand, Is.EqualTo command)

                Assert.That(
                    started.Snapshot.Tabs |> List.map _.Id,
                    Is.EqualTo(
                        terminals
                        |> List.map (fun terminal ->
                            EmbeddedTerminalId terminal.SessionId)
                    )
                )

                for body in host.StartRequestBodies do
                    use document = JsonDocument.Parse body

                    Assert.That(
                        document.RootElement.EnumerateObject()
                        |> Seq.map _.Name
                        |> Seq.toList,
                        Is.EqualTo([ "worktreePath" ]),
                        "TerminalHost control API v2 start must remain limited to worktreePath"
                    ))
        }

    [<Test>]
    member _.``invalid command is rejected before starting or attaching a terminal``() =
        task {
            use host = new FakeControlHost()
            host.PublishManifest()
            let submissions = ConcurrentQueue<string * string>()

            let config =
                { managerConfig host noLaunch with
                    SendTerminalCommand =
                        fun endpoint command ->
                            async {
                                submissions.Enqueue((endpoint, command))
                                return Ok()
                            } }

            let manager = EmbeddedTerminal.createWithConfig config
            let target = worktree host.Root "invalid-command"

            let! result =
                EmbeddedTerminal.startWithCommand
                    manager
                    target
                    "Write-Output first\nWrite-Output second"
                |> Async.StartAsTask

            let! cached =
                EmbeddedTerminal.getCached manager
                |> Async.StartAsTask

            Assert.Multiple(fun () ->
                Assert.That(
                    result,
                    Is.EqualTo(
                        Error "The terminal command is invalid"
                        : Result<EmbeddedTerminalStartResult, string>
                    )
                )

                Assert.That(host.StartRequestCount, Is.Zero)
                Assert.That(host.CloseRequestCount, Is.Zero)
                Assert.That(host.CurrentTerminals, Is.Empty)
                Assert.That(cached.Tabs, Is.Empty)
                Assert.That(submissions, Is.Empty))
        }

    [<Test>]
    member _.``delivery failure closes only the exact new terminal and preserves siblings``() =
        task {
            use host = new FakeControlHost()
            host.PublishManifest()
            let submissions = ConcurrentQueue<string * string>()

            let config =
                { managerConfig host noLaunch with
                    SendTerminalCommand =
                        fun endpoint command ->
                            async {
                                submissions.Enqueue((endpoint, command))
                                return Error "Simulated command delivery failure"
                            } }

            let manager = EmbeddedTerminal.createWithConfig config
            let target = worktree host.Root "delivery-failure"

            let! existing =
                EmbeddedTerminal.start manager target
                |> Async.StartAsTask

            let existingId = (requireOk existing).TerminalId

            let! result =
                EmbeddedTerminal.startWithCommand
                    manager
                    target
                    "Write-Output should-fail"
                |> Async.StartAsTask

            let! cached =
                EmbeddedTerminal.getCached manager
                |> Async.StartAsTask

            let submittedEndpoint, _ =
                submissions.ToArray() |> Array.exactlyOne

            let closedId =
                host.ClosedSessionIds |> List.exactlyOne

            Assert.Multiple(fun () ->
                Assert.That(
                    result,
                    Is.EqualTo(
                        Error "Simulated command delivery failure"
                        : Result<EmbeddedTerminalStartResult, string>
                    )
                )

                Assert.That(closedId, Is.Not.EqualTo(EmbeddedTerminalId.value existingId))
                Assert.That(submittedEndpoint, Does.Contain(closedId))

                Assert.That(
                    host.CurrentTerminals |> List.map _.SessionId,
                    Is.EqualTo([ EmbeddedTerminalId.value existingId ])
                )

                Assert.That(
                    cached.Tabs |> List.map _.Id,
                    Is.EqualTo([ existingId ])
                )

                Assert.That(host.StartRequestCount, Is.EqualTo(2))
                Assert.That(host.CloseRequestCount, Is.EqualTo(1)))
        }

    [<Test>]
    member _.``command start fails when the authoritative host drops the new terminal after delivery``() =
        task {
            use host = new FakeControlHost()
            host.PublishManifest()

            let config =
                { managerConfig host noLaunch with
                    SendTerminalCommand =
                        fun _ _ ->
                            async {
                                let terminal =
                                    host.CurrentTerminals |> List.last

                                host.RemoveTerminal terminal.SessionId
                                return Ok()
                            } }

            let manager = EmbeddedTerminal.createWithConfig config
            let target = worktree host.Root "lost-after-delivery"

            let! result =
                EmbeddedTerminal.startWithCommand
                    manager
                    target
                    "Write-Output should-not-succeed"
                |> Async.StartAsTask

            let! cached =
                EmbeddedTerminal.getCached manager
                |> Async.StartAsTask

            match result with
            | Error error ->
                Assert.Multiple(fun () ->
                    Assert.That(
                        error,
                        Is.EqualTo(
                            "TerminalHost did not retain the started terminal after command delivery"
                        )
                    )

                    Assert.That(host.CurrentTerminals, Is.Empty)
                    Assert.That(cached.Tabs, Is.Empty)
                    Assert.That(host.CloseRequestCount, Is.Zero))
            | Ok started ->
                Assert.Fail(
                    $"A lost terminal was reported as started: {EmbeddedTerminalId.value started.TerminalId}"
                )
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
            let endpoint = assertRunningFor target firstStart.Snapshot
            let firstTerminalId =
                firstStart.Snapshot.Tabs |> List.exactlyOne |> _.Id

            Assert.Multiple(fun () ->
                Assert.That(launches.Count, Is.EqualTo(1))
                Assert.That(host.StartRequestCount, Is.EqualTo(1))
                Assert.That(host.ListRequestCount, Is.GreaterThanOrEqualTo(1))
                Assert.That(endpoint, Does.EndWith($"{host.Token}/")))

            let! second =
                EmbeddedTerminal.start manager target
                |> Async.StartAsTask

            Assert.Multiple(fun () ->
                Assert.That((requireOk second).Snapshot.Tabs.Length, Is.EqualTo(2))
                Assert.That(launches.Count, Is.EqualTo(1)))

            host.FailNextCloseResponse()

            let! closed =
                closeManagedTerminal manager firstTerminalId
                |> Async.StartAsTask

            Assert.Multiple(fun () ->
                Assert.That((requireOk closed).Tabs.Length, Is.EqualTo(1))
                Assert.That(host.CloseRequestCount, Is.EqualTo(1))
                Assert.That(host.ListRequestCount, Is.GreaterThanOrEqualTo(5)))
        }

    [<Test>]
    member _.``explicit close monotonically closes only exact terminal instances and refreshes final no-session state``() =
        withCleanupScenario "exact-close" 2 (fun host manager target ->
            task {
                let! opened =
                    EmbeddedTerminal.getCached manager |> Async.StartAsTask

                let firstTerminalId, secondTerminalId =
                    match opened.Tabs |> List.map _.Id with
                    | [ first; second ] -> first, second
                    | tabs -> failwith $"Expected two opened terminals, got {tabs}"

                let processIdentities =
                    [ 61_001; 61_002; 61_003; 61_004 ]
                    |> List.map (fun processId ->
                        processId,
                        ProcessIdentity.create processId (int64 processId * 1_000L)
                        |> Result.defaultWith invalidOp)
                    |> Map.ofList

                let resolver =
                    ProcessIdentityResolver.create (fun processId ->
                        processIdentities |> Map.tryFind processId |> Ok)

                let agent = SchedulerState.createAgent()

                do!
                    populateAgent
                        agent
                        (PathUtils.toRepoId host.Root)
                        [ { Path = WorktreePath.value target
                            Head = "exact-close-head"
                            Branch = Some "exact-close" } ]

                use store =
                    new SessionActivityStore(Path.Combine(host.Root, "exact-close.db"))

                use service = new SessionActivityService(store, agent, resolver)
                service.Start()
                let now = DateTimeOffset.UtcNow

                let recordSession index processId sessionId terminalId eventAt =
                    let at = now.AddMilliseconds(float index)

                    let report suffix event =
                        { ParentProcessId = processId
                          SessionId = SessionId sessionId
                          TerminalSessionId =
                            Some(
                                terminalId
                                |> EmbeddedTerminalId.value
                                |> TerminalSessionId
                            )
                          WorktreePath = target
                          Provider = CopilotCli
                          EventId = EventId $"{suffix}-{processId}"
                          OccurredAt = at
                          Event = event }

                    match service.Present(report "presence" SessionPresent, at) with
                    | PresenceAcknowledge.Recorded identity ->
                        service.Submit(report "activity" (eventAt at))
                        identity
                    | PresenceAcknowledge.NotRecorded(_, error) ->
                        Assert.Fail(error)
                        Unchecked.defaultof<_>

                let targetIdentities =
                    [ 61_001, "shared-session", fun (_: DateTimeOffset) -> TurnStarted
                      61_002, "waiting-session", fun at -> AwaitingUserInput(None, at)
                      61_003, "idle-session", fun _ -> WentIdle ]
                    |> List.mapi (fun index (processId, sessionId, eventAt) ->
                        recordSession
                            (index + 1)
                            processId
                            sessionId
                            firstTerminalId
                            eventAt)

                let siblingIdentity =
                    recordSession 4 61_004 "shared-session" secondTerminalId (fun _ ->
                        TurnStarted)

                service.ExactSnapshot() |> ignore
                let diagnostics = ConcurrentQueue<LifecycleDiagnostics.Diagnostic>()

                let cleanup =
                    TerminalSessionCleanup.terminalSessionCleanupWithDiagnostics
                        diagnostics.Enqueue
                        service

                let! firstClose =
                    WorktreeCleanup.closeEmbeddedTerminalWith
                        cleanup
                        manager
                        firstTerminalId
                    |> Async.StartAsTask

                requireOk firstClose |> ignore

                let! stateAfterFirst =
                    agent.PostAndAsyncReply(GetState) |> Async.StartAsTask

                let recordedClosures =
                    diagnostics.ToArray()
                    |> Array.choose (function
                        | LifecycleDiagnostics.Diagnostic.ExactClosure closure when
                            closure.Outcome =
                                LifecycleDiagnostics.ExactClosureOutcome.Recorded
                            ->
                            Some closure.ProcessIdentity
                        | _ -> None)

                Assert.Multiple(fun () ->
                    targetIdentities
                    |> List.iter (fun identity ->
                        Assert.That(
                            store.InstanceByIdentity identity |> Option.bind _.ClosedAt,
                            Is.Not.EqualTo(None)
                        ))

                    Assert.That(
                        store.InstanceByIdentity siblingIdentity
                        |> Option.bind _.ClosedAt,
                        Is.EqualTo(None),
                        "the same durable SessionId in another exact terminal must remain open"
                    )
                    Assert.That(stateAfterFirst.SessionInstances.Count, Is.EqualTo(1))
                    Assert.That(host.CurrentTerminals.Length, Is.EqualTo(1))
                    Assert.That(recordedClosures, Is.EquivalentTo(targetIdentities))
                    Assert.That(recordedClosures, Does.Not.Contain(siblingIdentity)))

                let! secondClose =
                    WorktreeCleanup.closeEmbeddedTerminalWith
                        cleanup
                        manager
                        secondTerminalId
                    |> Async.StartAsTask

                requireOk secondClose |> ignore

                let! finalState =
                    agent.PostAndAsyncReply(GetState) |> Async.StartAsTask

                Assert.Multiple(fun () ->
                    Assert.That(
                        store.InstanceByIdentity siblingIdentity
                        |> Option.bind _.ClosedAt,
                        Is.Not.EqualTo(None)
                    )
                    Assert.That(finalState.SessionInstances, Is.Empty)
                    Assert.That(
                        finalState.CodingToolStatusByWorktree.ContainsKey(
                            WorktreePath.value target
                        ),
                        Is.False,
                        "the final exact closure must publish NoSession before close returns"
                    )
                    Assert.That(host.CurrentTerminals, Is.Empty))
            })

    [<Test>]
    member _.``graceful close wait leaves unrelated lifecycle requests available and host close remains authoritative``() =
        withCleanupScenario "graceful-target" 1 (fun host manager target ->
            task {
                let unrelated = worktree host.Root "graceful-unrelated"

                let! snapshot =
                    EmbeddedTerminal.getCached manager |> Async.StartAsTask

                let terminalId = snapshot.Tabs |> List.exactlyOne |> _.Id
                let terminalOrigin =
                    terminalId
                    |> EmbeddedTerminalId.value
                    |> TerminalSessionId

                let shutdownEntered = signal ()
                let releaseShutdown = signal ()

                let beforeCalls = ConcurrentQueue<Set<TerminalSessionId>>()
                let afterCalls = ConcurrentQueue<Set<TerminalSessionId>>()
                let diagnostics =
                    ConcurrentQueue<LifecycleDiagnostics.Diagnostic>()

                let prepare
                    (_: Map<TerminalSessionId, WorktreePath>)
                    : WorktreeCleanup.SessionClosePlan =
                    { BeforeHostClose =
                        fun terminalIds ->
                            async {
                                beforeCalls.Enqueue terminalIds
                                shutdownEntered.TrySetResult() |> ignore
                                do! releaseShutdown.Task |> Async.AwaitTask
                                raise (
                                    InvalidOperationException(
                                        "simulated graceful shutdown failure"
                                    )
                                )
                            }
                      AfterHostClose =
                        fun terminalIds ->
                            afterCalls.Enqueue terminalIds
                            Ok() }

                let close =
                    WorktreeCleanup.closeEmbeddedTerminalWithDiagnostics
                        diagnostics.Enqueue
                        prepare
                        manager
                        terminalId
                    |> Async.StartAsTask

                do!
                    shutdownEntered.Task.WaitAsync(
                        TimeSpan.FromSeconds 5.0
                    )

                let! samePathStart =
                    EmbeddedTerminal.start manager target
                    |> Async.StartAsTask
                    |> _.WaitAsync(TimeSpan.FromSeconds 2.0)

                let! unrelatedStart =
                    EmbeddedTerminal.start manager unrelated
                    |> Async.StartAsTask
                    |> _.WaitAsync(TimeSpan.FromSeconds 2.0)

                Assert.Multiple(fun () ->
                    Assert.That(
                        requireError samePathStart,
                        Does.Contain("cleanup is in progress")
                    )
                    requireOk unrelatedStart |> ignore
                    Assert.That(
                        host.CloseRequestCount,
                        Is.Zero,
                        "TerminalHost close must wait for the graceful attempt"
                    ))

                releaseShutdown.TrySetResult() |> ignore

                let! result =
                    close.WaitAsync(TimeSpan.FromSeconds 5.0)

                Assert.Multiple(fun () ->
                    requireOk result |> ignore
                    Assert.That(
                        beforeCalls.ToArray(),
                        Is.EqualTo [| Set.singleton terminalOrigin |]
                    )
                    Assert.That(
                        afterCalls.ToArray(),
                        Is.EqualTo [| Set.singleton terminalOrigin |]
                    )
                    Assert.That(host.CloseRequestCount, Is.EqualTo(1))
                    Assert.That(
                        host.CurrentTerminals |> List.map _.WorktreePath,
                        Is.EqualTo [ WorktreePath.value unrelated ]
                    )
                    Assert.That(
                        teardownStages diagnostics,
                        Is.EqualTo(
                            [| LifecycleDiagnostics.TeardownStage.Started(
                                   LifecycleDiagnostics.TeardownTarget.Terminal,
                                   [ terminalOrigin ]
                               )
                               LifecycleDiagnostics.TeardownStage.GracefulShutdownAttempted
                                   1
                               LifecycleDiagnostics.TeardownStage.HostCloseCompleted(
                                   LifecycleDiagnostics.HostCloseOutcome.Confirmed,
                                   1,
                                   0
                               )
                               LifecycleDiagnostics.TeardownStage.Completed |]
                        )
                    ))
            })

    [<Test>]
    member _.``cleanup reconciliation preserves a newer unrelated terminal``() =
        task {
            use host = new FakeControlHost()
            host.PublishManifest()
            let config = managerConfig host noLaunch
            let manager = EmbeddedTerminal.createWithConfig config
            let target = worktree host.Root "stale-cleanup-target"
            let unrelated = worktree host.Root "stale-cleanup-unrelated"

            let! started =
                EmbeddedTerminal.start manager target
                |> Async.StartAsTask

            let terminalId = requireOk started |> _.TerminalId

            let! reservation =
                EmbeddedTerminal.reserveCleanup
                    manager
                    (EmbeddedTerminal.OneTerminal terminalId)
                    None
                |> Async.StartAsTask

            let lease =
                match reservation with
                | Ok(Some lease) -> lease
                | other ->
                    Assert.Fail($"Expected cleanup reservation, got {other}")
                    Unchecked.defaultof<_>

            try
                let! connection =
                    async {
                        match! TerminalHostClient.discoverHost config with
                        | TerminalHostClient.HealthyHost connection -> return connection
                        | discovery ->
                            return
                                failwith $"Expected healthy fixture host, got {discovery}"
                    }
                    |> Async.StartAsTask

                host.RemoveTerminal(EmbeddedTerminalId.value terminalId)

                let! staleRegistry =
                    TerminalHostClient.listTerminals config connection
                    |> Async.StartAsTask

                let staleRegistry = requireOk staleRegistry

                let! unrelatedStart =
                    EmbeddedTerminal.start manager unrelated
                    |> Async.StartAsTask

                let unrelatedId = requireOk unrelatedStart |> _.TerminalId

                let! reconciled =
                    EmbeddedTerminal.applyCleanup
                        manager
                        { Registry = Some(connection, staleRegistry)
                          ClosedTerminalIds = lease.CachedTerminalIds
                          Interruption = None }
                    |> Async.StartAsTask

                let unrelatedTab =
                    reconciled.Tabs |> List.find (fun tab -> tab.Id = unrelatedId)

                Assert.Multiple(fun () ->
                    Assert.That(
                        reconciled.Tabs |> List.map _.Id,
                        Is.EqualTo [ unrelatedId ]
                    )

                    match unrelatedTab.Lifecycle with
                    | EmbeddedTerminalLifecycle.Running _ -> ()
                    | lifecycle ->
                        Assert.Fail(
                            $"The newer unrelated terminal was regressed to {lifecycle}"
                        ))
            finally
                EmbeddedTerminal.releaseCleanup manager lease
        }

    [<Test>]
    member _.``unresolved terminal survivor keeps registry entry and skips exact closure``() =
        let failClose _ =
            raise (InvalidOperationException "simulated unresolved survivor")

        withClosingHost failClose "unresolved-close" 1 (fun host manager target ->
            task {
                let! snapshot =
                    EmbeddedTerminal.getCached manager |> Async.StartAsTask

                let terminalId = snapshot.Tabs |> List.exactlyOne |> _.Id
                let closureCalls = ConcurrentQueue<Set<TerminalSessionId>>()

                let prepare
                    (_: Map<TerminalSessionId, WorktreePath>)
                    : WorktreeCleanup.SessionClosePlan =
                    { BeforeHostClose = fun _ -> async.Return()
                      AfterHostClose =
                        fun terminalIds ->
                            closureCalls.Enqueue terminalIds
                            Ok() }

                let! result =
                    WorktreeCleanup.closeEmbeddedTerminalWith prepare manager terminalId
                    |> Async.StartAsTask

                let! cached =
                    EmbeddedTerminal.getCached manager |> Async.StartAsTask

                Assert.Multiple(fun () ->
                    Assert.That(requireError result, Is.Not.Empty)
                    Assert.That(host.CurrentTerminals.Length, Is.EqualTo(1))
                    Assert.That(cached.Tabs.Length, Is.EqualTo(1))
                    Assert.That(cached.Tabs |> List.map _.Worktree, Is.EqualTo [ target ])
                    Assert.That(closureCalls, Is.Empty))
            })

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
                beforeRestart.Snapshot.Tabs
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
                    Is.EqualTo(beforeRestart.Snapshot.Tabs |> List.map _.Worktree)
                )

                Assert.That(endpointsAfter, Is.EqualTo endpointsBefore)
                Assert.That(host.StartRequestCount, Is.EqualTo(2)))
        }

    [<Test>]
    member _.``explicit close discovers a live terminal before a cold manager has polled``() =
        withCleanupScenario "cold-close" 1 (fun host manager _ ->
            task {
                let! snapshot =
                    EmbeddedTerminal.getCached manager |> Async.StartAsTask

                let terminalId = snapshot.Tabs |> List.exactlyOne |> _.Id

                let coldManager =
                    EmbeddedTerminal.createWithConfig(managerConfig host noLaunch)

                let! closed =
                    closeManagedTerminal coldManager terminalId |> Async.StartAsTask

                Assert.Multiple(fun () ->
                    Assert.That((requireOk closed).Tabs, Is.Empty)
                    Assert.That(host.CurrentTerminals, Is.Empty)
                    Assert.That(host.CloseRequestCount, Is.EqualTo(1)))
            })

    [<Test>]
    member _.``missing manifest while the exact recorded host is live blocks start and cleanup with one error``() =
        withRecordedHostScenario "missing-live-host" (fun host manager _ ->
            task {
                let other = worktree host.Root "missing-live-host-other"

                let! opened =
                    EmbeddedTerminal.getCached manager |> Async.StartAsTask

                let terminalId = opened.Tabs |> List.exactlyOne |> _.Id
                host.RemoveManifest()

                let! startResult =
                    EmbeddedTerminal.start manager other |> Async.StartAsTask

                let! closeResult =
                    closeManagedTerminal manager terminalId |> Async.StartAsTask

                let! cached =
                    EmbeddedTerminal.getCached manager |> Async.StartAsTask

                let expected =
                    "The TerminalHost discovery manifest disappeared while the exact recorded host is still running"

                Assert.Multiple(fun () ->
                    Assert.That(requireError startResult, Is.EqualTo expected)
                    Assert.That(requireError closeResult, Is.EqualTo expected)
                    Assert.That(host.StartRequestCount, Is.EqualTo(1))
                    Assert.That(host.CloseRequestCount, Is.Zero)
                    Assert.That(
                        cached.Tabs |> List.map _.Id,
                        Is.EqualTo [ terminalId ]
                    )

                    match cached.Tabs |> List.exactlyOne |> _.Lifecycle with
                    | EmbeddedTerminalLifecycle.Interrupted error ->
                        Assert.That(error, Is.EqualTo expected)
                    | lifecycle ->
                        Assert.Fail($"Expected interrupted terminal, got {lifecycle}"))
            })

    [<TestCase(false)>]
    [<TestCase(true)>]
    member _.``unavailable host cleanup carries its classified reason into the remaining tabs``(removeManifest: bool) =
        withRecordedHostScenario "unavailable-host-target" (fun host manager _ ->
            task {
                let! opened =
                    EmbeddedTerminal.getCached manager |> Async.StartAsTask

                let targetId = opened.Tabs |> List.exactlyOne |> _.Id
                let sibling = worktree host.Root "unavailable-host-sibling"

                let! siblingStarted =
                    EmbeddedTerminal.start manager sibling |> Async.StartAsTask

                let siblingId = requireOk siblingStarted |> _.TerminalId
                host.SimulateProcessExit()

                if removeManifest then
                    host.RemoveManifest()

                let! closed =
                    closeManagedTerminal manager targetId |> Async.StartAsTask

                let remaining = (requireOk closed).Tabs |> List.exactlyOne

                Assert.Multiple(fun () ->
                    Assert.That(remaining.Id, Is.EqualTo siblingId)
                    Assert.That(host.CloseRequestCount, Is.Zero)

                    match remaining.Lifecycle with
                    | EmbeddedTerminalLifecycle.Interrupted reason when removeManifest ->
                        Assert.That(
                            reason,
                            Is.EqualTo(
                                "TerminalHost is not running; no live terminal remains to close."
                            )
                        )
                    | EmbeddedTerminalLifecycle.Interrupted reason ->
                        Assert.That(
                            reason,
                            Does
                                .Contain("is no longer the exact live process")
                                .And.EndWith(". Its terminals were interrupted.")
                        )
                    | lifecycle ->
                        Assert.Fail(
                            $"Expected interrupted sibling terminal, got {lifecycle}"
                        ))
            })

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

    [<Test>]
    member _.``an exited terminal disappears without affecting its siblings``() =
        task {
            use host = new FakeControlHost()
            host.PublishManifest()

            let manager =
                EmbeddedTerminal.createWithConfig(managerConfig host noLaunch)

            let target = worktree host.Root "shared-worktree"

            let! firstStarted =
                EmbeddedTerminal.start manager target
                |> Async.StartAsTask

            let first = requireOk firstStarted

            let! secondStarted =
                EmbeddedTerminal.start manager target
                |> Async.StartAsTask

            let second = requireOk secondStarted
            host.RemoveTerminal(EmbeddedTerminalId.value first.TerminalId)

            let! afterExit =
                EmbeddedTerminal.get manager
                |> Async.StartAsTask

            Assert.Multiple(fun () ->
                Assert.That(
                    afterExit.Tabs |> List.map _.Id,
                    Is.EqualTo([ second.TerminalId ])
                )

                match afterExit.Tabs |> List.exactlyOne |> _.Lifecycle with
                | EmbeddedTerminalLifecycle.Running _ -> ()
                | lifecycle ->
                    Assert.Fail($"Expected the sibling terminal to remain running, got {lifecycle}"))
        }

type private FailureKind = LifecycleDiagnostics.ReplacementFailureKind
type private HostOutcome = LifecycleDiagnostics.ReplacementHostOutcome
type private Stage = LifecycleDiagnostics.ReplacementStage
type private CommitResolution = TerminalHostReplacement.ReplacementResolution
type private Outcome = TerminalHostReplacement.ReplacementOutcome
type private SessionPlan = TerminalHostReplacement.ReplacementSessionPlan

/// A TerminalHost bundle, named by generation so scenario expectations never carry paths.
type private ObservedExecutable =
    | OldExecutable
    | StagedExecutable
    | OtherExecutable of string

type private ObservedHost =
    | HostOffline
    | HostOnline of ObservedExecutable

/// One replacement operation the runner observed, in invocation order. Recreations are recorded
/// only when they succeeded; deliveries are recorded even when they failed, so a retried Resume
/// would show up twice.
type private ReplacementStep =
    | SessionShutdown of processId: int
    | HostStopRequested of ObservedExecutable
    | HostLaunchRequested of ObservedExecutable
    | TerminalRecreated of worktree: string
    | CommandDelivered of worktree: string * command: string

type private ObservedResolution =
    | RegistryApplied of terminalCount: int
    | OldStateKept
    | HostRetained of ProcessIdentity option
    | NoHostRetained

/// Everything a deterministic replacement scenario asserts, compared as one value.
type private ReplacementObservation =
    { Resolution: ObservedResolution
      Steps: ReplacementStep list
      ProcessesStarted: ObservedExecutable list
      Host: ObservedHost
      FinalStage: Stage
      SameSessionMultiplicity: (SessionId * ProcessIdentity list) list }

/// The single fault a scenario injects; every other replacement operation behaves normally.
type private InjectedFault =
    | NoFault
    | SessionShutdownTimesOut of processId: int
    | OldHostStopFailsWithLiveHost of error: string
    | OldHostStopFailsWithHostGone of error: string
    | OldHostStopFailsWithUnresolvedLiveness of error: string
    | StagedLaunchRejected of error: string
    | StagedLaunchUnhealthy of error: string
    | StagedLaunchUnhealthyWithManifest of error: string
    | StagedLaunchPublishesUnexpectedExecutable
    | StagedRegistryUnreadable
    | StagedRegistryReportsUnexpectedTerminals
    | TerminalRecreationFails of worktree: string * error: string
    | CommandDeliveryFails of worktree: string * error: string

type private StagedHostStop =
    | StagedHostStops
    | StagedHostStopFails of error: string

/// One exact process the policy plans to stop, with the Resume command it selected for that
/// terminal's durable conversation.
type private PlannedSession =
    { Terminal: string
      Conversation: string
      ProcessId: int
      Resume: string option }

type private ReplacementScenario =
    { Name: string
      Worktrees: string list
      ActivityEpoch: int64
      Sessions: PlannedSession list
      Fault: InjectedFault
      StagedHostStop: StagedHostStop
      Expected: ReplacementObservation
      ErrorFragments: string list
      ExpectedStages: Stage list option }

let private plannedIdentity processId =
    exactIdentity processId (int64 processId * 10L)

let private failedStage kind hostOutcome retained =
    Stage.Failed(kind, hostOutcome, retained)

/// A failure before the old host is confirmed stopped leaves the original host running.
let private oldHostKept kind =
    { Resolution = OldStateKept
      Steps = []
      ProcessesStarted = []
      Host = HostOnline OldExecutable
      FinalStage =
        failedStage kind HostOutcome.OldHostRunning (Some currentProcessIdentity)
      SameSessionMultiplicity = [] }

/// A failure after the old host exited that leaves no TerminalHost running.
let private noHostLeft kind =
    { oldHostKept kind with
        Resolution = NoHostRetained
        Host = HostOffline
        FinalStage = failedStage kind HostOutcome.NoHostRunning None }

/// A failure that retains one generation as the only known TerminalHost.
let private hostRetained kind hostOutcome executable =
    { oldHostKept kind with
        Resolution = HostRetained(Some currentProcessIdentity)
        Host = HostOnline executable
        FinalStage = failedStage kind hostOutcome (Some currentProcessIdentity) }

/// A failure discovered after a healthy staged host launched, where the staged host is stopped.
let private stagedHostStopped kind =
    { noHostLeft kind with
        Steps =
          [ HostStopRequested OldExecutable
            HostLaunchRequested StagedExecutable
            HostStopRequested StagedExecutable ]
        ProcessesStarted = [ StagedExecutable ] }

let private replacementApplied terminalCount =
    { Resolution = RegistryApplied terminalCount
      Steps = []
      ProcessesStarted = [ StagedExecutable ]
      Host = HostOnline StagedExecutable
      FinalStage = Stage.Completed
      SameSessionMultiplicity = [] }

let private replacementScenario name worktrees =
    { Name = name
      Worktrees = worktrees
      ActivityEpoch = 51L
      Sessions = []
      Fault = NoFault
      StagedHostStop = StagedHostStops
      Expected = replacementApplied worktrees.Length
      ErrorFragments = []
      ExpectedStages = None }

let private worktreeName (path: string) =
    Path.GetFileName(Path.TrimEndingDirectorySeparator path)

/// A policy plan with no owned sessions to shut down and no Resume commands.
let private plainReadyPlan activityEpoch =
    fun _ (_: TerminalHostReplacement.ReplacementTerminal list) ->
        Ok(SessionPlan.Ready(activityEpoch, [], Map.empty))

let private runManagerReplacement query operations manager =
    EmbeddedTerminal.tryReplaceHostWithOperations
        (fun () -> async.Return())
        query
        operations
        manager
    |> Async.StartAsTask

let private launchRejected error =
    TerminalHostReplacement.HostLaunchFailed(
        TerminalHostReplacement.LaunchRejected error
    )

let private launchUnhealthy error =
    TerminalHostReplacement.HostLaunchFailed(
        TerminalHostReplacement.LaunchStartedButUnhealthy error
    )

/// Drives one deterministic replacement attempt through the real commit path, instrumenting every
/// replacement operation and normalizing the result into a single comparable observation.
let private runReplacementScenario (scenario: ReplacementScenario) =
    task {
        use host = new FakeControlHost()
        host.EnableLogicalReplacement()
        let stagedVersion = "2.0.0-scenario"
        let stagedExecutable = host.Stage stagedVersion

        let classify path =
            if TerminalHostManifest.samePath path stagedExecutable then
                StagedExecutable
            elif TerminalHostManifest.samePath path host.OldExecutable then
                OldExecutable
            else
                OtherExecutable path

        let steps = ConcurrentQueue<ReplacementStep>()
        let processStarts = ConcurrentQueue<ObservedExecutable>()
        let diagnostics = ConcurrentQueue<LifecycleDiagnostics.Diagnostic>()
        let queriedTerminals = ConcurrentQueue<string list>()

        let oldHostStopAttempted =
            TaskCompletionSource<unit>(
                TaskCreationOptions.RunContinuationsAsynchronously
            )

        let launchProcess (startInfo: ProcessStartInfo) =
            processStarts.Enqueue(classify startInfo.FileName)

            let published =
                match scenario.Fault with
                | StagedLaunchPublishesUnexpectedExecutable -> host.OldExecutable
                | _ -> startInfo.FileName

            host.Activate(published, stagedVersion)
            Ok()

        let config =
            let baseConfig =
                replacementManagerConfig
                    host
                    launchProcess
                    (fun _ _ -> async { return Ok() })

            match scenario.Fault with
            | OldHostStopFailsWithUnresolvedLiveness error ->
                { baseConfig with
                    ProcessIdentityResolver =
                        ProcessIdentityResolver.create (fun processId ->
                            if oldHostStopAttempted.Task.IsCompleted then
                                Error error
                            else
                                host.ResolveProcessIdentity processId) }
            | _ -> baseConfig

        let! fixture =
            ReplacementScenarioFixture.create host config scenario.Worktrees

        match scenario.Fault with
        | StagedRegistryReportsUnexpectedTerminals -> host.FreezeRegistryResponse()
        | _ -> ()

        let terminalNamed name =
            fixture.Terminals
            |> List.find (fun terminal ->
                worktreeName terminal.WorktreePath = name)

        let shutdownTargets =
            scenario.Sessions
            |> List.map (fun session ->
                replacementTarget
                    (terminalNamed session.Terminal)
                    session.Conversation
                    (plannedIdentity session.ProcessId))

        let resumeCommands =
            scenario.Sessions
            |> List.choose (fun session ->
                session.Resume
                |> Option.map (fun command ->
                    typedTerminalSessionId (terminalNamed session.Terminal).SessionId,
                    replacementResume session.Conversation command))
            |> Map.ofList

        let query _ (terminals: TerminalHostReplacement.ReplacementTerminal list) =
            queriedTerminals.Enqueue(
                terminals |> List.map (_.WorktreePath >> worktreeName)
            )

            Ok(
                SessionPlan.Ready(
                    scenario.ActivityEpoch,
                    shutdownTargets,
                    resumeCommands
                )
            )

        let defaults = defaultReplacementOperations

        let operations =
            { defaults with
                ShutdownSessions =
                    shutdownSessionsUsing (fun target ->
                        async {
                            let processId =
                                ProcessIdentity.processId target.ProcessIdentity

                            steps.Enqueue(SessionShutdown processId)

                            return
                                match scenario.Fault with
                                | SessionShutdownTimesOut failing
                                    when failing = processId ->
                                    Error SessionBridge.ShutdownFailure.TimedOut
                                | _ ->
                                    Ok SessionBridge.ShutdownCompletion.ExactClosure
                        })
                StopHost =
                    fun stopConfig manifest ->
                        async {
                            let stopped = classify stopConfig.HostExecutablePath
                            steps.Enqueue(HostStopRequested stopped)

                            match stopped, scenario.StagedHostStop, scenario.Fault with
                            | StagedExecutable, StagedHostStopFails error, _ ->
                                return Error error
                            | StagedExecutable, StagedHostStops, _ ->
                                return! defaults.StopHost stopConfig manifest
                            | _, _, OldHostStopFailsWithLiveHost error ->
                                return Error error
                            | _, _, OldHostStopFailsWithHostGone error ->
                                host.SimulateProcessExit()
                                return Error error
                            | _, _, OldHostStopFailsWithUnresolvedLiveness error ->
                                oldHostStopAttempted.TrySetResult() |> ignore
                                return Error error
                            | _ -> return! defaults.StopHost stopConfig manifest
                        }
                LaunchHost =
                    fun launchConfig ->
                        async {
                            classify launchConfig.HostExecutablePath
                            |> HostLaunchRequested
                            |> steps.Enqueue

                            match scenario.Fault with
                            | StagedLaunchRejected error ->
                                return launchRejected error
                            | StagedLaunchUnhealthy error ->
                                return launchUnhealthy error
                            | StagedLaunchUnhealthyWithManifest error ->
                                host.Activate(
                                    launchConfig.HostExecutablePath,
                                    stagedVersion
                                )

                                return launchUnhealthy error
                            | StagedRegistryUnreadable ->
                                let! launched = defaults.LaunchHost launchConfig
                                host.OverrideRegistryResponse "{ not a registry"
                                return launched
                            | _ -> return! defaults.LaunchHost launchConfig
                        }
                RecreateTerminal =
                    fun recreateConfig connection terminal ->
                        async {
                            let name = worktreeName terminal.WorktreePath

                            match scenario.Fault with
                            | TerminalRecreationFails(failing, error)
                                when failing = name ->
                                return
                                    Error(
                                        TerminalHostClient.MutationRejected(
                                            { Revision = 0L; Terminals = [] },
                                            error
                                        )
                                    )
                            | _ ->
                                let! recreated =
                                    defaults.RecreateTerminal
                                        recreateConfig
                                        connection
                                        terminal

                                if Result.isOk recreated then
                                    steps.Enqueue(TerminalRecreated name)

                                return recreated
                        }
                DeliverCommand =
                    fun deliverConfig terminal command ->
                        async {
                            let name = worktreeName terminal.WorktreePath
                            steps.Enqueue(CommandDelivered(name, command))

                            match scenario.Fault with
                            | CommandDeliveryFails(failing, error)
                                when failing = name ->
                                return Error error
                            | _ ->
                                return!
                                    defaults.DeliverCommand
                                        deliverConfig
                                        terminal
                                        command
                        } }

        let! outcome, resolution =
            runReplacementCommitWithDiagnostics
                diagnostics.Enqueue
                config
                query
                operations

        let observed =
            { Resolution =
                match resolution with
                | CommitResolution.ApplyRegistry(_, registry, _) ->
                    RegistryApplied registry.Terminals.Length
                | CommitResolution.KeepState _ -> OldStateKept
                | CommitResolution.InterruptWithHost(manifest, _, _) ->
                    HostRetained(TerminalHostManifest.tryProcessIdentity manifest)
                | CommitResolution.InterruptWithoutHost _ -> NoHostRetained
              Steps = steps.ToArray() |> Array.toList
              ProcessesStarted = processStarts.ToArray() |> Array.toList
              Host =
                if host.IsOnline then HostOnline(classify host.CurrentExecutable)
                else HostOffline
              FinalStage = replacementStages diagnostics |> Array.last
              SameSessionMultiplicity =
                diagnostics.ToArray()
                |> Array.choose (function
                    | LifecycleDiagnostics.Diagnostic.SameSessionMultiplicityObserved multiplicity
                        when multiplicity.Boundary =
                             LifecycleDiagnostics.ObservationBoundary.Replacement ->
                        Some(multiplicity.SessionId, multiplicity.ProcessIdentities)
                    | _ -> None)
                |> Array.toList }

        let failure =
            match
                TerminalHostReplacement.replacementResolutionOutcome resolution
            with
            | Outcome.Failed(_, error) -> error
            | _ -> ""

        let expectedOutcome =
            match scenario.Expected.Resolution with
            | RegistryApplied _ -> Outcome.Replaced stagedVersion
            | _ -> Outcome.Failed(stagedVersion, failure)

        /// The failing terminal is named by its worktree, so its exact session ID is only known
        /// once the scenario's terminals exist.
        let boundFragments =
            match scenario.Fault with
            | TerminalRecreationFails(name, error) ->
                [ $"Could not recreate terminal {(terminalNamed name).SessionId}: {error}" ]
            | CommandDeliveryFails(name, error) ->
                [ $"Could not deliver the replacement command for terminal {(terminalNamed name).SessionId}: {error}" ]
            | _ -> []

        /// Only a failure past the old host's exit is irreversible, and only those may tell the
        /// user that replacement can no longer be undone.
        let irreversible =
            match scenario.Expected.FinalStage with
            | Stage.Failed(
                _,
                (HostOutcome.NoHostRunning | HostOutcome.StagedHostRetained),
                _
              ) -> true
            | _ -> false

        let irreversibleMarker = "after the previous TerminalHost exited"

        Assert.Multiple(fun () ->
            Assert.That(observed, Is.EqualTo scenario.Expected)
            Assert.That(outcome, Is.EqualTo expectedOutcome)

            Assert.That(
                queriedTerminals.ToArray(),
                Is.All.EqualTo scenario.Worktrees,
                "the policy must see every captured terminal in opening order"
            )

            if irreversible then
                Assert.That(failure, Does.Contain irreversibleMarker)

                Assert.That(
                    failure,
                    Does.Contain
                        "restart Treemon from an external PowerShell window"
                )
            elif failure <> "" then
                Assert.That(failure, Does.Not.Contain irreversibleMarker)

            scenario.ExpectedStages
            |> Option.iter (fun stages ->
                Assert.That(
                    replacementStages diagnostics,
                    Is.EqualTo(Array.ofList stages)
                ))

            for fragment in scenario.ErrorFragments @ boundFragments do
                Assert.That(failure, Does.Contain fragment))
    }

let private replacementScenarios =
    [ { replacementScenario
            "successful replacement stops every exact process before host launch and resumes once per terminal"
            [ "exact-first"; "exact-second" ] with
          Sessions =
            [ { Terminal = "exact-first"
                Conversation = "shared-conversation"
                ProcessId = 4101
                Resume = Some "resume-first" }
              { Terminal = "exact-first"
                Conversation = "shared-conversation"
                ProcessId = 4102
                Resume = None }
              { Terminal = "exact-second"
                Conversation = "second-conversation"
                ProcessId = 4103
                Resume = Some "resume-second" } ]
          Expected =
            { replacementApplied 2 with
                Steps =
                  [ SessionShutdown 4101
                    SessionShutdown 4102
                    SessionShutdown 4103
                    HostStopRequested OldExecutable
                    HostLaunchRequested StagedExecutable
                    TerminalRecreated "exact-first"
                    CommandDelivered("exact-first", "resume-first")
                    TerminalRecreated "exact-second"
                    CommandDelivered("exact-second", "resume-second") ]
                SameSessionMultiplicity =
                  [ SessionId "shared-conversation",
                    [ plannedIdentity 4101; plannedIdentity 4102 ] ] } }

      { replacementScenario
            "graceful shutdown addresses every exact target and keeps the old host healthy on failure"
            [ "graceful-failure" ] with
          Sessions =
            [ { Terminal = "graceful-failure"
                Conversation = "duplicate-session"
                ProcessId = 4301
                Resume = Some "resume-duplicate" }
              { Terminal = "graceful-failure"
                Conversation = "duplicate-session"
                ProcessId = 4302
                Resume = None } ]
          Fault = SessionShutdownTimesOut 4302
          Expected =
            { oldHostKept FailureKind.GracefulShutdown with
                Steps = [ SessionShutdown 4301; SessionShutdown 4302 ]
                SameSessionMultiplicity =
                  [ SessionId "duplicate-session",
                    [ plannedIdentity 4301; plannedIdentity 4302 ] ] }
          ErrorFragments = [ "1 of 2 exact sessions"; "PID 4302" ]
          ExpectedStages =
            Some
                [ Stage.Captured(1, 2, 1)
                  Stage.RecheckStarted
                  Stage.GracefulShutdownStarted 2
                  Stage.GracefulShutdownCompleted(1, 1)
                  failedStage
                      FailureKind.GracefulShutdown
                      HostOutcome.OldHostRunning
                      (Some currentProcessIdentity) ] }

      { replacementScenario
            "old host stop failure with a live exact old host keeps state and prevents staged launch"
            [ "old-stop-failure" ] with
          Fault = OldHostStopFailsWithLiveHost "simulated exact survivor"
          Expected =
            { oldHostKept FailureKind.OldHostStop with
                Steps = [ HostStopRequested OldExecutable ] }
          ErrorFragments =
            [ "The previous TerminalHost could not be confirmed stopped: simulated exact survivor" ] }

      { replacementScenario
            "old host confirmed gone after a stop error never launches another host"
            [ "old-stop-gone" ] with
          Sessions =
            [ { Terminal = "old-stop-gone"
                Conversation = "gone-selected"
                ProcessId = 5101
                Resume = Some "resume-gone-selected" } ]
          Fault = OldHostStopFailsWithHostGone "simulated unconfirmed exit"
          Expected =
            { noHostLeft FailureKind.OldHostStop with
                Steps =
                  [ SessionShutdown 5101
                    HostStopRequested OldExecutable ] }
          ErrorFragments = [ "could not be confirmed stopped" ] }

      { replacementScenario
            "unresolved old host liveness after a stop error retains the old host"
            [ "old-stop-unresolved" ] with
          Fault =
            OldHostStopFailsWithUnresolvedLiveness "simulated identity probe failure"
          Expected =
            { hostRetained
                FailureKind.OldHostStop
                HostOutcome.OldHostUnresolved
                OldExecutable with
                Steps = [ HostStopRequested OldExecutable ] }
          ErrorFragments =
            [ "could not be confirmed stopped or alive"
              "retained as the only known host"
              "simulated identity probe failure" ] }

      { replacementScenario
            "staged launch rejection after the old host exited interrupts with no current host"
            [ "recover-old" ] with
          Sessions =
            [ { Terminal = "recover-old"
                Conversation = "selected-session"
                ProcessId = 4201
                Resume = Some "resume-selected" } ]
          Fault = StagedLaunchRejected "simulated staged launch failure"
          Expected =
            { noHostLeft FailureKind.StagedHostLaunch with
                Steps =
                  [ SessionShutdown 4201
                    HostStopRequested OldExecutable
                    HostLaunchRequested StagedExecutable ] }
          ErrorFragments =
            [ "The staged host could not be launched: simulated staged launch failure" ] }

      { replacementScenario
            "staged host that starts unhealthy reports no current host and never launches old"
            [ "staged-unhealthy" ] with
          Fault = StagedLaunchUnhealthy "simulated unhealthy staged host"
          Expected =
            { noHostLeft FailureKind.StagedHostLaunch with
                Steps =
                  [ HostStopRequested OldExecutable
                    HostLaunchRequested StagedExecutable ] }
          ErrorFragments = [ "started but did not become healthy" ] }

      { replacementScenario
            "unhealthy staged host with an exact manifest is stopped and never launches old"
            [ "staged-unhealthy-published" ] with
          Fault =
            StagedLaunchUnhealthyWithManifest "simulated unhealthy staged host with manifest"
          Expected =
            { noHostLeft FailureKind.StagedHostLaunch with
                Steps =
                  [ HostStopRequested OldExecutable
                    HostLaunchRequested StagedExecutable
                    HostStopRequested StagedExecutable ] }
          ErrorFragments = [ "started but did not become healthy" ] }

      { replacementScenario
            "an unexpected staged executable stops the exact staged host"
            [ "staged-mismatch" ] with
          Fault = StagedLaunchPublishesUnexpectedExecutable
          Expected = stagedHostStopped FailureKind.StagedHostVerification
          ErrorFragments = [ "unexpected TerminalHost executable" ] }

      { replacementScenario
            "an unreadable staged registry stops the exact staged host"
            [ "staged-registry-unreadable" ] with
          Fault = StagedRegistryUnreadable
          Expected = stagedHostStopped FailureKind.StagedRegistry
          ErrorFragments =
            [ "The replacement TerminalHost registry could not be read" ] }

      { replacementScenario
            "a staged host reporting unexpected terminals is stopped before recreation"
            [ "staged-registry-nonempty" ] with
          Fault = StagedRegistryReportsUnexpectedTerminals
          Expected = stagedHostStopped FailureKind.StagedRegistry
          ErrorFragments = [ "started with 1 unexpected terminals" ] }

      { replacementScenario
            "terminal recreation failure stops the exact staged host and never launches the old host"
            [ "recreate-fails-first"; "recreate-fails-second" ] with
          Fault =
            TerminalRecreationFails("recreate-fails-first", "simulated recreation failure")
          Expected = stagedHostStopped FailureKind.TerminalRecreation }

      { replacementScenario
            "terminal recreation failure after a partial recreation stops the exact staged host"
            [ "partial-recreate-first"; "partial-recreate-second" ] with
          Fault =
            TerminalRecreationFails(
                "partial-recreate-second",
                "simulated late recreation failure")
          Expected =
            { noHostLeft FailureKind.TerminalRecreation with
                Steps =
                  [ HostStopRequested OldExecutable
                    HostLaunchRequested StagedExecutable
                    TerminalRecreated "partial-recreate-first"
                    HostStopRequested StagedExecutable ]
                ProcessesStarted = [ StagedExecutable ] } }

      { replacementScenario
            "command delivery failure stops the exact staged host without a second delivery attempt"
            [ "command-fails" ] with
          Sessions =
            [ { Terminal = "command-fails"
                Conversation = "selected-command-session"
                ProcessId = 4501
                Resume = Some "resume-selected-command" } ]
          Fault =
            CommandDeliveryFails(
                "command-fails",
                "simulated command delivery failure")
          Expected =
            { noHostLeft FailureKind.CommandDelivery with
                Steps =
                  [ SessionShutdown 4501
                    HostStopRequested OldExecutable
                    HostLaunchRequested StagedExecutable
                    TerminalRecreated "command-fails"
                    CommandDelivered("command-fails", "resume-selected-command")
                    HostStopRequested StagedExecutable ]
                ProcessesStarted = [ StagedExecutable ] } }

      { replacementScenario
            "unconfirmed staged host stop retains only the staged host and interrupts"
            [ "staged-stop-survivor" ] with
          Sessions =
            [ { Terminal = "staged-stop-survivor"
                Conversation = "survivor-selected"
                ProcessId = 5001
                Resume = Some "resume-survivor-selected" } ]
          Fault =
            TerminalRecreationFails(
                "staged-stop-survivor",
                "simulated staged recreation failure")
          StagedHostStop = StagedHostStopFails "simulated staged host survivor"
          Expected =
            { hostRetained
                FailureKind.TerminalRecreation
                HostOutcome.StagedHostRetained
                StagedExecutable with
                Steps =
                  [ SessionShutdown 5001
                    HostStopRequested OldExecutable
                    HostLaunchRequested StagedExecutable
                    HostStopRequested StagedExecutable ]
                ProcessesStarted = [ StagedExecutable ] }
          ErrorFragments =
            [ "simulated staged host survivor"
              "retained as the only known host" ] } ]

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type EmbeddedTerminalReplacementTests() =
    static member ReplacementScenarios =
        replacementScenarios
        |> List.map (fun scenario ->
            TestCaseData(scenario.Name).SetName scenario.Name)

    [<TestCaseSource(nameof EmbeddedTerminalReplacementTests.ReplacementScenarios)>]
    member _.ForwardOnlyReplacement(name: string) =
        replacementScenarios
        |> List.find (fun scenario -> scenario.Name = name)
        |> runReplacementScenario

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
    member _.``waiting policy performs no session shutdown host stop or staged launch``() =
        task {
            use host = new FakeControlHost()
            host.EnableLogicalReplacement()
            let stagedVersion = "2.0.0-waiting"
            host.Stage stagedVersion |> ignore
            let launches = ConcurrentQueue<unit>()

            let config =
                replacementManagerConfig
                    host
                    (fun _ ->
                        launches.Enqueue()
                        Error "waiting replacement must not launch")
                    noTerminalCommand

            let manager = EmbeddedTerminal.createWithConfig config
            let target = worktree host.Root "waiting-policy"

            let! started =
                EmbeddedTerminal.start manager target
                |> Async.StartAsTask

            requireOk started |> ignore

            let query _ _ =
                Ok
                    TerminalHostReplacement.ReplacementSessionPlan.WaitingForIdle

            let! outcome =
                runManagerReplacement query defaultReplacementOperations manager


            Assert.Multiple(fun () ->
                Assert.That(
                    outcome,
                    Is.EqualTo
                        TerminalHostReplacement.ReplacementOutcome.WaitingForIdle
                )
                Assert.That(host.ShutdownRequestCount, Is.Zero)
                Assert.That(launches, Is.Empty)
                Assert.That(host.IsOnline, Is.True)
                Assert.That(host.CurrentTerminals.Length, Is.EqualTo 1))
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
                activatingReplacementConfig
                    host
                    stagedVersion
                    launches
                    (fun _ _ -> async { return Ok() })

            let manager = EmbeddedTerminal.createWithConfig config
            let first = worktree host.Root "race-first"
            let raced = worktree host.Root "race-winner"

            let! firstStarted =
                EmbeddedTerminal.start manager first
                |> Async.StartAsTask

            requireOk firstStarted |> ignore

            let query = plainReadyPlan 4L


            let beforeRecheck () =
                async {
                    let! started = EmbeddedTerminal.start manager raced
                    requireOk started |> ignore
                }

            let! outcome =
                EmbeddedTerminal.tryReplaceHostWithOperations
                    beforeRecheck
                    query
                    defaultReplacementOperations
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
                activatingReplacementConfig
                    host
                    stagedVersion
                    launches
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
                        [],
                        Map.empty
                    )
                )

            let! outcome =
                runManagerReplacement query defaultReplacementOperations manager


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
                activatingReplacementConfig
                    host
                    stagedVersion
                    launches
                    (fun _ _ -> async { return Ok() })

            let manager = EmbeddedTerminal.createWithConfig config
            let target = worktree host.Root "late-mailbox-reply"

            let! started =
                EmbeddedTerminal.start manager target
                |> Async.StartAsTask

            requireOk started |> ignore

            let query = plainReadyPlan 5L


            let commitStarted =
                TaskCompletionSource<unit>(
                    TaskCreationOptions.RunContinuationsAsynchronously
                )

            let releaseCommit =
                TaskCompletionSource<unit>(
                    TaskCreationOptions.RunContinuationsAsynchronously
                )

            let operations = defaultReplacementOperations

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

                        let! resolution =
                            TerminalHostReplacement.commitReplacementWith
                                operations
                                config
                                plan
                                activityQuery

                        let outcome =
                            TerminalHostReplacement.replacementResolutionOutcome
                                resolution

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
                runningStart.Snapshot.Tabs |> List.exactlyOne |> _.Id

            let query = plainReadyPlan 31L


            let replacement =
                runManagerReplacement query defaultReplacementOperations manager


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
                            closeManagedTerminal
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
                closeManagedTerminal manager runningTerminalId
                |> Async.StartAsTask

            requireOk retriedClose |> ignore

            Assert.That(
                host.CurrentTerminals |> List.map _.WorktreePath,
                Is.EqualTo [ WorktreePath.value rejectedStart ],
                "lifecycle requests must recover after replacement completes"
            )
        }

    [<Test>]
    member _.``stale WaitingForUser is neither a replacement gate nor a resume candidate``() =
        task {
            use host = new FakeControlHost()
            host.EnableLogicalReplacement()
            let stagedVersion = "2.0.0-stale-waiting"
            let stagedExecutable = host.Stage stagedVersion
            let launches = ConcurrentQueue<string>()
            let submitted = ConcurrentQueue<string>()

            let manager =
                activatingReplacementConfig
                    host
                    stagedVersion
                    launches
                    (fun _ command ->
                        async {
                            submitted.Enqueue command
                            return Ok()
                        })
                |> EmbeddedTerminal.createWithConfig

            let target = worktree host.Root "waiting"

            let! started =
                EmbeddedTerminal.start manager target
                |> Async.StartAsTask

            requireOk started |> ignore

            let terminal = host.CurrentTerminals |> List.exactlyOne
            let awaitingAt = DateTimeOffset.UtcNow
            let backdatedLastSeen = awaitingAt - TimeSpan.FromMinutes 15.0
            let identity =
                ProcessIdentity.create 42 42L
                |> Result.defaultWith invalidOp

            let waitingSession: StoredInstance =
                { ProcessIdentity = identity
                  SessionId = SessionId "exact-owned-waiting-session"
                  TerminalSessionId =
                    Some(TerminalSessionId terminal.SessionId)
                  WorktreePath = target
                  Provider = CopilotCli
                  Status =
                    { emptyStatus with
                        Status = SessionLevelStatus.Working
                        AwaitingUserSince = Some awaitingAt }
                  UpdatedAt = awaitingAt
                  LifecycleAt = Some awaitingAt
                  LastSeen = backdatedLastSeen
                  ContextUsageAt = None
                  ClosedAt = None }

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

                        Ok(1L, [ waitingSession ], Set.empty))
                    now
                    terminals

            let! outcome =
                runManagerReplacement query defaultReplacementOperations manager


            Assert.Multiple(fun () ->
                Assert.That(
                    outcome,
                    Is.EqualTo
                        (TerminalHostReplacement.ReplacementOutcome.Replaced stagedVersion)
                )

                Assert.That(host.ShutdownRequestCount, Is.EqualTo(1))
                Assert.That(launches.ToArray(), Is.EqualTo([| stagedExecutable |]))
                Assert.That(submitted, Is.Empty)
                Assert.That(
                    host.CurrentTerminals |> List.map _.WorktreePath,
                    Is.EqualTo([ WorktreePath.value target ])
                )
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
                    { TerminalSessionId =
                        typedTerminalSessionId terminal.SessionId
                      WorktreePath = terminal.WorktreePath })

            let replacementCommand = "opaque replacement command"
            let queriedTerminals =
                ConcurrentQueue<TerminalHostReplacement.ReplacementTerminal list>()

            let shutdownTarget:
                TerminalHostReplacement.ReplacementShutdownTarget =
                { TerminalSessionId =
                    typedTerminalSessionId resumedTerminal.SessionId
                  WorktreePath = resumedTerminal.WorktreePath
                  CopilotSessionId =
                    typedSessionId "replacement-session"
                  ProcessIdentity = exactIdentity 4401 5401L }

            let query _ terminals =
                queriedTerminals.Enqueue terminals

                Ok(
                    TerminalHostReplacement.ReplacementSessionPlan.Ready(
                        21L,
                        [ shutdownTarget ],
                        Map.ofList
                            [ typedTerminalSessionId resumedTerminal.SessionId,
                              replacementResume
                                  "replacement-session"
                                  replacementCommand ]
                    )
                )

            let operations =
                { defaultReplacementOperations with
                    ShutdownSessions =
                        shutdownSessionsUsing
                        <| fun _ ->
                            async {
                                return
                                    Ok
                                        SessionBridge.ShutdownCompletion.ExactClosure
                            } }

            let! outcome =
                EmbeddedTerminal.tryReplaceHostWithOperations
                    (fun () -> async.Return())
                    query
                    operations
                    manager
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

            let config =
                replacementManagerConfig
                    host
                    (fun _ ->
                        launches.Enqueue()
                        Error "An incomplete bundle must not launch")
                    (fun _ _ -> async { return Ok() })

            let! fixture =
                ReplacementScenarioFixture.create host config [ "incomplete-stage" ]

            let manager = fixture.Manager

            let query = plainReadyPlan 13L


            let! outcome =
                runManagerReplacement query defaultReplacementOperations manager


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
    member _.``unconfirmed old host stop keeps the last authoritative terminal running``() =
        task {
            use host = new FakeControlHost()
            let stagedVersion = "2.0.0-shutdown-wait"
            host.Stage stagedVersion |> ignore
            let launches = ConcurrentQueue<unit>()

            let config =
                { replacementManagerConfig
                    host
                    (fun _ ->
                        launches.Enqueue()
                        Error "Replacement must not launch")
                    (fun _ _ -> async { return Ok() }) with
                    StartupTimeout = TimeSpan.FromMilliseconds 100.0 }

            let! fixture =
                ReplacementScenarioFixture.create host config [ "shutdown-wait" ]

            let manager = fixture.Manager

            let query = plainReadyPlan 15L


            let! outcome =
                runManagerReplacement query defaultReplacementOperations manager


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
                | EmbeddedTerminalLifecycle.Running _ -> ()
                | other ->
                    Assert.Fail(
                        $"Expected the last authoritative terminal to remain running, got {other}"
                    ))
        }

/// A repository whose `Target` and `Untouched` worktrees each own one terminal and are visible to
/// the scheduler: the shared starting point of the delete and archive ordering proofs.
type private WorktreeMutationScenario =
    { Host: FakeControlHost
      Manager: EmbeddedTerminal.Manager
      Agent: MailboxProcessor<StateMsg>
      RepoRoot: string
      RootPaths: Map<RepoId, string>
      Target: WorktreePath
      Untouched: WorktreePath }

let private withWorktreeMutationScenario
    name
    (scenario: WorktreeMutationScenario -> Task<unit>)
    =
    task {
        use host = new FakeControlHost()
        host.PublishManifest()

        let manager =
            EmbeddedTerminal.createWithConfig(managerConfig host noLaunch)

        let repoRoot = Path.Combine(host.Root, name)
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

        do!
            populateAgent
                agent
                repoId
                [ { Path = WorktreePath.value target
                    Head = "target-head"
                    Branch = Some "target" }
                  { Path = WorktreePath.value untouched
                    Head = "untouched-head"
                    Branch = Some "untouched" } ]

        do!
            scenario
                { Host = host
                  Manager = manager
                  Agent = agent
                  RepoRoot = repoRoot
                  RootPaths = Map.ofList [ repoId, repoRoot ]
                  Target = target
                  Untouched = untouched }
    }

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type EmbeddedTerminalWorktreeCleanupTests() =
    [<Test>]
    member _.``constructing terminal close without starting it leaves the path available``() =
        withCleanupScenario "unstarted-cleanup" 1 (fun _ manager target ->
            task {
                let! snapshot =
                    EmbeddedTerminal.getCached manager |> Async.StartAsTask

                let terminalId = snapshot.Tabs |> List.exactlyOne |> _.Id
                let _unstartedClose = closeManagedTerminal manager terminalId
                do! Task.Delay(TimeSpan.FromMilliseconds 250.0)

                let! started =
                    EmbeddedTerminal.start manager target
                    |> Async.StartAsTask
                    |> _.WaitAsync(TimeSpan.FromSeconds 2.0)

                requireOk started |> ignore
            })

    [<Test>]
    member _.``cleanup reservation rejects the same canonical path while unrelated starts remain available``() =
        withCleanupScenario "reserved-target" 2 (fun host manager target ->
            task {
                let unrelated = worktree host.Root "reserved-unrelated"

                let operationEntered = signal ()
                let releaseOperation = signal ()

                let firstCleanup =
                    withManagedTerminalCleanup
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

                let secondOperationEntered = signal ()

                let! secondCleanup =
                    withManagedTerminalCleanup
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
            })

    [<Test>]
    member _.``failed cleanup releases its canonical path reservation``() =
        withCleanupScenario "failed-cleanup" 1 (fun host manager target ->
            task {
                let! failed =
                    withManagedTerminalCleanup
                        manager
                        target
                        (fun () -> async { return Error "mutation failed" })
                    |> Async.StartAsTask

                let! restarted =
                    EmbeddedTerminal.start manager target
                    |> Async.StartAsTask
                    |> _.WaitAsync(TimeSpan.FromSeconds 2.0)

                Assert.Multiple(fun () ->
                    Assert.That(requireError failed, Is.EqualTo("mutation failed"))
                    requireOk restarted |> ignore
                    Assert.That(
                        host.CurrentTerminals |> List.map _.WorktreePath,
                        Is.EqualTo [ WorktreePath.value target ]
                    ))
            })

    [<Test>]
    member _.``cancelled cleanup releases its canonical path reservation``() =
        withCleanupScenario "cancelled-cleanup" 1 (fun _ manager target ->
            task {
                let operationEntered = signal ()

                use cancellation = new System.Threading.CancellationTokenSource()

                let cleanup =
                    withManagedTerminalCleanup
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

                do! operationEntered.Task.WaitAsync(TimeSpan.FromSeconds 5.0)
                cancellation.Cancel()

                try
                    let! result = cleanup.WaitAsync(TimeSpan.FromSeconds 5.0)
                    Assert.Fail($"Expected cleanup cancellation, got {result}")
                with :? OperationCanceledException ->
                    ()

                let! restarted =
                    EmbeddedTerminal.start manager target
                    |> Async.StartAsTask
                    |> _.WaitAsync(TimeSpan.FromSeconds 2.0)

                requireOk restarted |> ignore
            })

    /// Every owned terminal is still attempted, but one surviving close failure withholds the
    /// worktree mutation and leaves the survivor registered.
    [<TestCase(1)>]
    [<TestCase(2)>]
    member _.``a host close failure keeps the worktree mutation blocked``(terminalCount: int) =
        let closeAttempts = ConcurrentQueue<unit>()

        let failFirstClose _ =
            closeAttempts.Enqueue()

            if closeAttempts.Count = 1 then
                raise (
                    InvalidOperationException "simulated terminal close failure"
                )

        withClosingHost failFirstClose "blocked-close" terminalCount (fun host manager target ->
            task {
                let mutationEntered = signal ()

                let! failed =
                    withManagedTerminalCleanup
                        manager
                        target
                        (fun () ->
                            async {
                                mutationEntered.TrySetResult() |> ignore
                                return Ok()
                            })
                    |> Async.StartAsTask

                let survivors =
                    host.CurrentTerminals
                    |> List.filter (fun terminal ->
                        terminal.WorktreePath = WorktreePath.value target)
                    |> List.length

                let! reused =
                    EmbeddedTerminal.start manager target
                    |> Async.StartAsTask
                    |> _.WaitAsync(TimeSpan.FromSeconds 2.0)

                Assert.Multiple(fun () ->
                    Assert.That(requireError failed, Is.Not.Empty)
                    Assert.That(
                        closeAttempts.Count,
                        Is.EqualTo(terminalCount),
                        "every terminal owned by the worktree must still be attempted"
                    )
                    Assert.That(survivors, Is.EqualTo(1))
                    Assert.That(
                        mutationEntered.Task.IsCompleted,
                        Is.False,
                        "an unconfirmed close must not run the mutation"
                    )
                    requireOk reused |> ignore)
            })

    /// Host close succeeded, so the terminal is gone, but the exact activity instances could not be
    /// closed: the worktree must survive and the failure must be reported.
    [<Test>]
    member _.``exact closure failure keeps the worktree mutation blocked``() =
        withCleanupScenario "failed-exact-closure" 1 (fun host manager target ->
            task {
                let prepare
                    (_: Map<TerminalSessionId, WorktreePath>)
                    : WorktreeCleanup.SessionClosePlan =
                    { BeforeHostClose = fun _ -> async.Return()
                      AfterHostClose =
                        fun _ -> Error "exact closure could not be recorded" }

                let mutationEntered = signal ()

                let! failed =
                    WorktreeCleanup.withTerminalCleanupWithDiagnostics
                        LifecycleDiagnostics.ignore
                        prepare
                        manager
                        target
                        (fun () ->
                            async {
                                mutationEntered.TrySetResult() |> ignore
                                return Ok()
                            })
                    |> Async.StartAsTask

                Assert.Multiple(fun () ->
                    Assert.That(
                        requireError failed,
                        Is.EqualTo("exact closure could not be recorded")
                    )
                    Assert.That(
                        mutationEntered.Task.IsCompleted,
                        Is.False,
                        "an unrecorded exact closure must not run the mutation"
                    )
                    Assert.That(host.CurrentTerminals, Is.Empty))
            })

    [<TestCase(null)>]
    [<TestCase("\u0000")>]
    member _.``malformed terminal ID is rejected without stopping the lifecycle mailbox``(invalidId: string) =
        task {
            use host = new FakeControlHost()
            host.PublishManifest()
            let manager =
                EmbeddedTerminal.createWithConfig(managerConfig host noLaunch)

            let! malformedClose =
                closeManagedTerminal
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
            // Kestrel callbacks own these fixture processes; the queue keeps that boundary immutable.
            let terminalProcesses = ConcurrentQueue<Process>()

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

                    match Process.Start startInfo with
                    | null -> failwith "The fixture terminal process did not start"
                    | started -> terminalProcesses.Enqueue started

            let closeTerminalProcess path =
                if matchingTarget path then
                    stopFixtureProcesses terminalProcesses

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

                let operationEntered = signal ()
                let releaseOperation = signal ()

                let cleanup =
                    withManagedTerminalCleanup
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
                stopFixtureProcesses terminalProcesses

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

            let launchStarted = signal ()

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
            let rootPaths = Map.ofList [ repoId, repoRoot ]

            let delete () =
                WorktreeApi.deleteWorktreeWith
                    (fun _ path _ -> async { removeCalls.Enqueue path; return Ok() })
                    (withManagedTerminalCleanup manager)
                    (fun path -> async { stateCleanupCalls.Enqueue path })
                    agent
                    rootPaths
                    deleteTarget

            let archive () =
                WorktreeApi.updateArchivedBranchesWith
                    agent
                    rootPaths
                    (withManagedTerminalCleanup manager)
                    Set.add
                    archiveTarget

            let query _ _ =
                Ok(
                    TerminalHostReplacement.ReplacementSessionPlan.Ready(
                        32L,
                        [],
                        Map.empty
                    )
                )

            let replacement =
                runManagerReplacement query defaultReplacementOperations manager

            do! launchStarted.Task.WaitAsync(TimeSpan.FromSeconds 5.0)

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

            let! outcome = replacement.WaitAsync(TimeSpan.FromSeconds 5.0)

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
                        TerminalHostReplacement.ReplacementOutcome.Replaced stagedVersion
                    )
                )
                Assert.That(
                    host.CurrentTerminals |> List.map _.WorktreePath,
                    Is.EquivalentTo(worktrees |> List.map _.Path),
                    "rejected cleanup requests must not execute after replacement"
                ))

            let! retriedDelete = delete () |> Async.StartAsTask
            let! retriedArchive = archive () |> Async.StartAsTask
            requireOk retriedDelete |> ignore
            requireOk retriedArchive |> ignore

            let! stateAfterRetry =
                agent.PostAndAsyncReply(SchedulerState.StateMsg.GetState)
                |> Async.StartAsTask

            Assert.Multiple(fun () ->
                Assert.That(
                    stateAfterRetry.Repos[repoId].WorktreeList |> List.map _.Path,
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
        withWorktreeMutationScenario "repo" (fun scenario ->
            task {
                let calls = ConcurrentQueue<string>()

                let! result =
                    WorktreeApi.deleteWorktreeWith
                        (fun _ removedPath _ ->
                            async {
                                calls.Enqueue "remove"
                                let! snapshot = EmbeddedTerminal.get scenario.Manager

                                Assert.That(
                                    snapshot.Tabs |> List.map _.Worktree,
                                    Is.EqualTo [ scenario.Untouched ]
                                )

                                Assert.That(
                                    removedPath,
                                    Is.EqualTo(WorktreePath.value scenario.Target)
                                )

                                return Ok()
                            })
                        (fun path operation ->
                            withManagedTerminalCleanup
                                scenario.Manager
                                path
                                (fun () ->
                                    async {
                                        calls.Enqueue "close"
                                        return! operation ()
                                    }))
                        (fun _ -> async { calls.Enqueue "state" })
                        scenario.Agent
                        scenario.RootPaths
                        scenario.Target
                    |> Async.StartAsTask

                requireOk result |> ignore

                Assert.Multiple(fun () ->
                    Assert.That(
                        calls.ToArray(),
                        Is.EqualTo [| "close"; "remove"; "state" |]
                    )

                    Assert.That(
                        scenario.Host.CurrentTerminals |> List.map _.WorktreePath,
                        Is.EqualTo [ WorktreePath.value scenario.Untouched ]
                    ))
            })

    [<Test>]
    member _.``archive closes only the exact terminal before persisting archive state``() =
        withWorktreeMutationScenario "archive-repo" (fun scenario ->
            task {
                let! result =
                    WorktreeApi.updateArchivedBranchesWith
                        scenario.Agent
                        scenario.RootPaths
                        (withManagedTerminalCleanup scenario.Manager)
                        Set.add
                        scenario.Target
                    |> Async.StartAsTask

                requireOk result |> ignore

                let! remaining =
                    EmbeddedTerminal.get scenario.Manager |> Async.StartAsTask

                Assert.Multiple(fun () ->
                    Assert.That(
                        remaining.Tabs |> List.map _.Worktree,
                        Is.EqualTo [ scenario.Untouched ]
                    )

                    Assert.That(
                        TreemonConfig.readArchivedBranches scenario.RepoRoot,
                        Does.Contain("target")
                    ))
            })
