module SessionIsolationVerifier.Program

open System
open System.Diagnostics
open System.Globalization
open System.IO
open System.Net
open System.Net.Http
open System.Reflection
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Giraffe
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Microsoft.Data.Sqlite
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Logging
open Shared
open global.Server
open global.Server.SessionActivity
open global.Server.SessionActivityProtocol
open global.Server.SessionActivityService
open global.Server.SessionActivityStore

[<Literal>]
let private durableSessionId = "same-session"

let private terminalA =
    TerminalSessionId.create "11111111111111111111111111111111"
    |> Result.defaultWith invalidOp

let private terminalB =
    TerminalSessionId.create "22222222222222222222222222222222"
    |> Result.defaultWith invalidOp

type private FixtureProcess =
    { Name: string
      Process: Process
      Identity: ProcessIdentity }

type private ActivityRuntime =
    { Application: WebApplication
      Port: int
      Scheduler: MailboxProcessor<SchedulerState.StateMsg>
      Store: SessionActivityStore
      Service: SessionActivityService }

type private ActivityAcknowledge =
    { Recorded: bool
      Monitored: bool
      Retryable: bool
      Raw: string }

type private ResolverControl =
    { Resolver: ProcessIdentityResolver
      Remap: int -> ProcessIdentity -> unit }

let private ensure condition message =
    if not condition then
        invalidOp message

let private boolText value =
    if value then "true" else "false"

let private activityText status =
    status
    |> effectiveActivity
    |> Option.map (AgentActivity.textAndTimestamp >> fst)
    |> Option.defaultValue "(none)"

let private emitInstance label (stored: StoredInstance) =
    let processId, startTicks =
        ProcessIdentity.sortKey stored.ProcessIdentity

    let origin =
        stored.TerminalSessionId
        |> Option.map TerminalSessionId.value
        |> Option.defaultValue "(none)"

    let status = effectiveStatus stored.Status
    let activity = activityText stored.Status
    let closed = stored.ClosedAt.IsSome

    printfn
        $"{label} pid={processId} startTicks={startTicks} sessionId={SessionId.value stored.SessionId} status={status} lastSeen={stored.LastSeen:O} origin={origin} activity={activity} closed={boolText closed}"

let private runFixture name =
    printfn $"FIXTURE_READY name={name} pid={Environment.ProcessId}"
    Console.Out.Flush()
    Console.ReadLine() |> ignore
    0

let private startFixture name =
    let assemblyPath = Assembly.GetExecutingAssembly().Location
    ensure (File.Exists assemblyPath) $"Verifier assembly not found: {assemblyPath}"

    let startInfo =
        ProcessStartInfo(
            FileName = "dotnet",
            WorkingDirectory = Environment.CurrentDirectory,
            UseShellExecute = false,
            RedirectStandardInput = true,
            RedirectStandardOutput = true,
            CreateNoWindow = true
        )

    [ assemblyPath; "--fixture"; name ]
    |> List.iter startInfo.ArgumentList.Add

    let childProcess =
        Process.Start startInfo
        |> Option.ofObj
        |> Option.defaultWith (fun () ->
            invalidOp $"Could not start fixture process {name}")

    try
        let ready =
            childProcess.StandardOutput
                .ReadLineAsync()
                .WaitAsync(TimeSpan.FromSeconds 10.0)
                .GetAwaiter()
                .GetResult()

        ensure
            (ready = $"FIXTURE_READY name={name} pid={childProcess.Id}")
            $"Fixture {name} did not become ready: {ready}"

        let identity =
            match
                ProcessIdentityResolver.resolve
                    childProcess.Id
                    ProcessIdentityResolverRuntime.defaultResolver
            with
            | Ok(Some value) -> value
            | Ok None ->
                invalidOp $"Fixture {name} exited before identity resolution"
            | Error error ->
                invalidOp
                    $"Could not resolve fixture {name} identity: {error}"

        { Name = name
          Process = childProcess
          Identity = identity }
    with error ->
        try
            if not childProcess.HasExited then
                childProcess.Kill(entireProcessTree = true)
                childProcess.WaitForExit(5_000) |> ignore
        with cleanupError ->
            eprintfn
                $"Fixture {name} startup cleanup failed: {cleanupError.GetType().Name}: {cleanupError.Message}"

        childProcess.Dispose()
        raise error

let private stopFixture fixture =
    try
        if not fixture.Process.HasExited then
            fixture.Process.StandardInput.WriteLine("stop")
            fixture.Process.StandardInput.Flush()

            if not (fixture.Process.WaitForExit 5_000) then
                fixture.Process.Kill(entireProcessTree = true)

                ensure
                    (fixture.Process.WaitForExit 5_000)
                    $"Fixture {fixture.Name} PID {fixture.Process.Id} survived exact cleanup"

        Ok()
    with error ->
        Error
            $"Fixture {fixture.Name} PID {fixture.Process.Id} cleanup failed: {error.GetType().Name}: {error.Message}"

let private disposeFixture fixture =
    let stopped = stopFixture fixture

    try
        fixture.Process.Dispose()
        stopped
    with error ->
        Error
            $"Fixture {fixture.Name} PID {fixture.Process.Id} disposal failed: {error.GetType().Name}: {error.Message}"

let private isAlive identity =
    ProcessIdentityResolver.isAlive
        ProcessIdentityResolverRuntime.defaultResolver
        identity
    |> Result.defaultWith invalidOp

let private createResolver (identities: Map<int, ProcessIdentity>) =
    let gate = obj ()
    // The resolver changes one PID's exact identity during the deliberate PID-reuse phase.
    let mutable current = identities

    { Resolver =
        ProcessIdentityResolver.create (fun processId ->
            lock gate (fun () ->
                current
                |> Map.tryFind processId
                |> Ok))
      Remap =
        fun processId identity ->
            lock gate (fun () ->
                current <- current |> Map.add processId identity) }

let private runProcess
    (fileName: string)
    (arguments: string list)
    (workingDirectory: string)
    =
    let startInfo =
        ProcessStartInfo(
            FileName = fileName,
            WorkingDirectory = workingDirectory,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        )

    arguments |> List.iter startInfo.ArgumentList.Add

    use childProcess =
        Process.Start startInfo
        |> Option.ofObj
        |> Option.defaultWith (fun () ->
            invalidOp $"Could not start {fileName}")

    let stdout = childProcess.StandardOutput.ReadToEndAsync()
    let stderr = childProcess.StandardError.ReadToEndAsync()
    childProcess.WaitForExit()
    let output = stdout.GetAwaiter().GetResult()
    let error = stderr.GetAwaiter().GetResult()

    ensure
        (childProcess.ExitCode = 0)
        $"{fileName} exited with {childProcess.ExitCode}: {error}{output}"

let private initializeWorktree path =
    Directory.CreateDirectory path |> ignore

    runProcess
        "git"
        [ "init"; "--quiet"; "--initial-branch=main" ]
        path

let private registerWorktrees
    (scheduler: MailboxProcessor<SchedulerState.StateMsg>)
    (worktreePaths: string list)
    =
    let worktrees =
        worktreePaths
        |> List.map (fun path ->
            { Path = PathUtils.normalizePath path
              Head = ""
              Branch = Some "verify" }
            : GitWorktree.WorktreeInfo)

    let repoId = RepoId "session-isolation-verifier"
    scheduler.Post(SchedulerState.UpdateWorktreeList(repoId, worktrees))

    let state =
        scheduler.PostAndReply SchedulerState.GetState

    let registered =
        state.Repos[repoId].KnownPaths

    let expected =
        worktreePaths
        |> List.map PathUtils.normalizePath
        |> Set.ofList

    ensure
        (registered = expected)
        "The isolated worktrees were not registered with the activity service"

let private startApplication
    (service: SessionActivityService)
    (requestedPort: int)
    =
    let builder = WebApplication.CreateSlimBuilder()
    builder.Logging.ClearProviders() |> ignore
    builder.Services.AddGiraffe() |> ignore

    builder.WebHost.ConfigureKestrel(fun options ->
        options.AddServerHeader <- false
        options.Listen(IPAddress.Loopback, requestedPort))
    |> ignore

    let application = builder.Build()

    let next: HttpFunc =
        fun context -> Task.FromResult(Some context)

    application.MapPost(
        "/api/session/activity",
        RequestDelegate(fun context ->
            task {
                let! _ = service.Handler next context
                return ()
            })
    )
    |> ignore

    try
        application
            .StartAsync(CancellationToken.None)
            .GetAwaiter()
            .GetResult()

        let server =
            application.Services.GetRequiredService<
                global.Microsoft.AspNetCore.Hosting.Server.IServer
             >()

        let address =
            server.Features
                .Get<
                    global.Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature
                 >()
                .Addresses
            |> Seq.exactlyOne
            |> Uri

        application, address.Port
    with error ->
        application.DisposeAsync().AsTask().GetAwaiter().GetResult()
        raise error

let private startRuntime
    (dbPath: string)
    (worktreePaths: string list)
    (resolver: ProcessIdentityResolver)
    (requestedPort: int)
    =
    let scheduler = SchedulerState.createAgent ()
    registerWorktrees scheduler worktreePaths
    let store = new SessionActivityStore(dbPath)
    let service =
        new SessionActivityService(
            store,
            scheduler,
            resolver
        )

    try
        service.Start()
        let application, port =
            startApplication service requestedPort

        ensure
            (port <> 5000)
            "The isolated activity server bound production port 5000"

        if requestedPort <> 0 then
            ensure
                (port = requestedPort)
                $"The activity server restarted on {port} instead of {requestedPort}"

        { Application = application
          Port = port
          Scheduler = scheduler
          Store = store
          Service = service }
    with error ->
        (service :> IDisposable).Dispose()
        (store :> IDisposable).Dispose()
        (scheduler :> IDisposable).Dispose()
        raise error

let private cleanupError description action =
    try
        action ()
        None
    with error ->
        Some
            $"{description}: {error.GetType().Name}: {error.Message}"

let private stopRuntime runtime =
    [ cleanupError "Activity server stop" (fun () ->
          runtime.Application
              .StopAsync(CancellationToken.None)
              .GetAwaiter()
              .GetResult())
      cleanupError "Activity server disposal" (fun () ->
          runtime.Application
              .DisposeAsync()
              .AsTask()
              .GetAwaiter()
              .GetResult())
      cleanupError "Activity service disposal" (fun () ->
          (runtime.Service :> IDisposable).Dispose())
      cleanupError "Activity store disposal" (fun () ->
          (runtime.Store :> IDisposable).Dispose())
      cleanupError "Scheduler disposal" (fun () ->
          (runtime.Scheduler :> IDisposable).Dispose()) ]
    |> List.choose id
    |> function
        | [] -> Ok()
        | errors -> Error(String.concat Environment.NewLine errors)

let private request
    processId
    worktreePath
    terminalSessionId
    eventId
    kind
    messageText
    =
    let occurredAt =
        DateTimeOffset.UtcNow.ToString(
            "O",
            CultureInfo.InvariantCulture
        )

    let message =
        messageText
        |> Option.map (fun text ->
            { MessageDto.text = text
              at = occurredAt })
        |> Option.defaultValue Unchecked.defaultof<MessageDto>

    { SessionActivityRequest.parentProcessId = processId
      sessionId = durableSessionId
      terminalSessionId =
        terminalSessionId
        |> Option.map TerminalSessionId.value
        |> Option.defaultValue Unchecked.defaultof<string>
      worktreePath = worktreePath
      provider = "copilot_cli"
      eventId = eventId
      occurredAt = occurredAt
      kind = kind
      message = message
      skillName = Unchecked.defaultof<string>
      toolCallId = Unchecked.defaultof<string>
      currentTokens = 0
      tokenLimit = 0 }

let private postReportAsync
    (client: HttpClient)
    (endpoint: string)
    (report: SessionActivityRequest)
    =
    task {
        let payload = JsonSerializer.Serialize report
        use content =
            new StringContent(
                payload,
                Encoding.UTF8,
                "application/json"
            )

        use! response = client.PostAsync(endpoint, content)
        let! raw = response.Content.ReadAsStringAsync()

        ensure
            response.IsSuccessStatusCode
            $"Activity endpoint returned {(int response.StatusCode)}: {raw}"

        use document = JsonDocument.Parse raw
        let root = document.RootElement

        return
            { Recorded =
                root.GetProperty("recorded").GetBoolean()
              Monitored =
                root.GetProperty("monitored").GetBoolean()
              Retryable =
                root.GetProperty("retryable").GetBoolean()
              Raw = raw }
    }

let private postReport client endpoint report =
    postReportAsync client endpoint report
    |> _.GetAwaiter().GetResult()

let private tryPostReport client endpoint report =
    try
        postReport client endpoint report |> Ok
    with
    | :? HttpRequestException as error -> Error error.Message
    | :? TaskCanceledException as error -> Error error.Message

let private postRecorded client endpoint report =
    let acknowledge = postReport client endpoint report

    ensure
        acknowledge.Monitored
        $"Activity endpoint did not monitor the fixture: {acknowledge.Raw}"

    ensure
        acknowledge.Recorded
        $"Activity endpoint did not record the fixture: {acknowledge.Raw}"

    acknowledge

let private postPresenceAfterRestart
    client
    endpoint
    report
    initialAttempt
    =
    let rec retry attempt =
        match tryPostReport client endpoint report with
        | Ok acknowledge when acknowledge.Recorded ->
            attempt, acknowledge
        | Ok acknowledge when acknowledge.Retryable && attempt < 20 ->
            Thread.Sleep 100
            retry (attempt + 1)
        | Error _ when attempt < 20 ->
            Thread.Sleep 100
            retry (attempt + 1)
        | Ok acknowledge ->
            invalidOp
                $"Restart presence was not recorded: {acknowledge.Raw}"
        | Error error ->
            invalidOp
                $"Restart presence did not reconnect after {attempt} attempts: {error}"

    retry initialAttempt

let private requireInstance
    (store: SessionActivityStore)
    identity
    =
    store.InstanceByIdentity identity
    |> Option.defaultWith (fun () ->
        let processId, startTicks =
            ProcessIdentity.sortKey identity

        invalidOp
            $"Missing exact instance pid={processId} startTicks={startTicks}")

let private openSqlite dbPath =
    let connection =
        new SqliteConnection(
            SqliteConnectionStringBuilder(
                DataSource = dbPath,
                Pooling = false
            ).ConnectionString
        )

    connection.Open()
    connection

let private totalEventCount dbPath eventId =
    use connection = openSqlite dbPath
    use command = connection.CreateCommand()
    command.CommandText <-
        "SELECT count(*) FROM activity_events WHERE event_id = $eventId;"

    command.Parameters.AddWithValue("$eventId", eventId)
    |> ignore

    Convert.ToInt32(command.ExecuteScalar())

let private instanceEventCount dbPath eventId identity =
    let processId, startTicks =
        ProcessIdentity.sortKey identity

    use connection = openSqlite dbPath
    use command = connection.CreateCommand()
    command.CommandText <-
        """
SELECT count(*)
FROM activity_events
WHERE event_id = $eventId
  AND process_id = $processId
  AND process_start_ticks = $processStartTicks;
"""

    command.Parameters.AddWithValue("$eventId", eventId)
    |> ignore

    command.Parameters.AddWithValue("$processId", processId)
    |> ignore

    command.Parameters.AddWithValue(
        "$processStartTicks",
        startTicks
    )
    |> ignore

    Convert.ToInt32(command.ExecuteScalar())

let private closeProcess runtime identity =
    match
        runtime.Service.CloseProcess(
            identity,
            DateTimeOffset.UtcNow
        )
    with
    | ClosureAcknowledge.Closed -> ()
    | ClosureAcknowledge.Missing ->
        let processId, startTicks =
            ProcessIdentity.sortKey identity

        invalidOp
            $"Could not close missing exact identity pid={processId} startTicks={startTicks}"
    | ClosureAcknowledge.Failed error -> invalidOp error

let rec private deleteDirectory path attempts =
    if not (Directory.Exists path) then
        Ok()
    else
        try
            Directory.Delete(path, recursive = true)

            if Directory.Exists path then
                Error $"Temporary verifier path survived cleanup: {path}"
            else
                Ok()
        with
        | :? IOException as error when attempts > 0 ->
            Thread.Sleep 100
            deleteDirectory path (attempts - 1)
        | :? UnauthorizedAccessException as error when attempts > 0 ->
            Thread.Sleep 100
            deleteDirectory path (attempts - 1)
        | error ->
            Error
                $"Could not remove temporary verifier path '{path}': {error.GetType().Name}: {error.Message}"

let private restoreEnvironment name previousValue =
    Environment.SetEnvironmentVariable(
        name,
        previousValue |> Option.defaultValue Unchecked.defaultof<string>
    )

let private verify () =
    let root =
        Path.Combine(
            Path.GetTempPath(),
            $"treemon-session-isolation-{Guid.NewGuid():N}"
        )

    let worktreeA = Path.Combine(root, "worktree-a")
    let worktreeB = Path.Combine(root, "worktree-b")
    let activityDirectory = Path.Combine(root, "activity")
    let activityStore = Path.Combine(activityDirectory, "activity.db")
    let terminalHostState = Path.Combine(root, "terminal-host-state")
    let hostStateVariable = "TREEMON_TERMINAL_HOST_STATE_DIR"

    let previousHostState =
        Environment.GetEnvironmentVariable hostStateVariable
        |> Option.ofObj

    // Runtime and child-process ownership changes across the restart phase, so failure cleanup
    // retains the currently acquired exact resources at this narrow orchestration boundary.
    let mutable activeRuntime: ActivityRuntime option = None
    let mutable fixtures: FixtureProcess list = []

    Directory.CreateDirectory root |> ignore
    Directory.CreateDirectory activityDirectory |> ignore
    Directory.CreateDirectory terminalHostState |> ignore
    Environment.SetEnvironmentVariable(hostStateVariable, terminalHostState)

    let acquireFixture name =
        let fixture = startFixture name
        fixtures <- fixture :: fixtures
        fixture

    let stopActiveRuntime () =
        match activeRuntime with
        | None -> Ok()
        | Some runtime ->
            let result = stopRuntime runtime

            match result with
            | Ok() -> activeRuntime <- None
            | Error _ -> ()

            result

    let execution =
        try
            initializeWorktree worktreeA
            initializeWorktree worktreeB

            let firstFixture = acquireFixture "A"
            let secondFixture = acquireFixture "B"

            ensure
                (firstFixture.Process.Id <> secondFixture.Process.Id)
                "The two fixture processes unexpectedly share one PID"

            ensure
                (firstFixture.Identity <> secondFixture.Identity)
                "The two fixture processes unexpectedly share one exact identity"

            let resolverControl =
                [ firstFixture; secondFixture ]
                |> List.map (fun fixture ->
                    fixture.Process.Id,
                    fixture.Identity)
                |> Map.ofList
                |> createResolver

            let hostConfig =
                TerminalHostClient.defaultConfigWithProcessIdentityResolver
                    resolverControl.Resolver
                    []

            ensure
                (Path.GetFullPath hostConfig.HostStateDirectory = Path.GetFullPath terminalHostState)
                "TerminalHost state did not resolve to the isolated verifier path"

            let firstRuntime =
                startRuntime
                    activityStore
                    [ worktreeA; worktreeB ]
                    resolverControl.Resolver
                    0

            activeRuntime <- Some firstRuntime

            use client =
                new HttpClient(
                    Timeout = TimeSpan.FromSeconds 2.0
                )

            let endpoint =
                $"http://127.0.0.1:{firstRuntime.Port}/api/session/activity"

            printfn
                $"ISOLATION port={firstRuntime.Port} activityStore={activityStore} terminalHostState={terminalHostState} worktreeA={worktreeA} worktreeB={worktreeB}"

            let firstProcessId =
                ProcessIdentity.processId firstFixture.Identity

            let secondProcessId =
                ProcessIdentity.processId secondFixture.Identity

            postRecorded
                client
                endpoint
                (request
                    firstProcessId
                    worktreeA
                    (Some terminalA)
                    "presence-a"
                    "session_present"
                    None)
            |> ignore

            postRecorded
                client
                endpoint
                (request
                    secondProcessId
                    worktreeB
                    (Some terminalB)
                    "presence-b"
                    "session_present"
                    None)
            |> ignore

            [ request
                  firstProcessId
                  worktreeA
                  (Some terminalA)
                  "title-a"
                  "title_reported"
                  (Some "first process working")
              request
                  firstProcessId
                  worktreeA
                  (Some terminalA)
                  "working-a"
                  "turn_started"
                  None
              request
                  secondProcessId
                  worktreeB
                  (Some terminalB)
                  "title-b"
                  "title_reported"
                  (Some "second process idle")
              request
                  secondProcessId
                  worktreeB
                  (Some terminalB)
                  "idle-b"
                  "went_idle"
                  None ]
            |> List.iter (postRecorded client endpoint >> ignore)

            let step1 = firstRuntime.Service.ExactSnapshot()
            let firstStep1 = step1[firstFixture.Identity]
            let secondStep1 = step1[secondFixture.Identity]

            ensure
                (step1.Count = 2)
                $"Expected two exact rows, found {step1.Count}"

            ensure
                (effectiveStatus firstStep1.Status = SessionLevelStatus.Working)
                "Fixture A did not retain Working state"

            ensure
                (effectiveStatus secondStep1.Status = SessionLevelStatus.Idle)
                "Fixture B did not retain Idle state"

            ensure
                (firstStep1.TerminalSessionId = Some terminalA)
                "Fixture A lost its terminal origin"

            ensure
                (secondStep1.TerminalSessionId = Some terminalB)
                "Fixture B lost its terminal origin"

            emitInstance "STEP1_ROW_A" firstStep1
            emitInstance "STEP1_ROW_B" secondStep1
            printfn "STEP1 PASS independent exact rows retained"

            let sharedEventA =
                request
                    firstProcessId
                    worktreeA
                    (Some terminalA)
                    "shared-event-id"
                    "intent_reported"
                    (Some "first shared event")

            let sharedEventB =
                request
                    secondProcessId
                    worktreeB
                    (Some terminalB)
                    "shared-event-id"
                    "intent_reported"
                    (Some "second shared event")

            [ sharedEventA; sharedEventB ]
            |> List.iter (postRecorded client endpoint >> ignore)

            firstRuntime.Service.ExactSnapshot() |> ignore
            let beforeRetry =
                totalEventCount activityStore "shared-event-id"

            [ sharedEventA; sharedEventB ]
            |> List.iter (postRecorded client endpoint >> ignore)

            firstRuntime.Service.ExactSnapshot() |> ignore
            let afterRetry =
                totalEventCount activityStore "shared-event-id"

            let firstEventRows =
                instanceEventCount
                    activityStore
                    "shared-event-id"
                    firstFixture.Identity

            let secondEventRows =
                instanceEventCount
                    activityStore
                    "shared-event-id"
                    secondFixture.Identity

            ensure
                (beforeRetry = 2 &&
                 afterRetry = 2 &&
                 firstEventRows = 1 &&
                 secondEventRows = 1)
                "Event idempotency was not scoped to exact process identity"

            printfn
                $"STEP2 PASS eventId=shared-event-id totalRows={afterRetry} firstRows={firstEventRows} secondRows={secondEventRows} retriesAdded={afterRetry - beforeRetry}"

            closeProcess firstRuntime firstFixture.Identity
            let closedBefore =
                requireInstance
                    firstRuntime.Store
                    firstFixture.Identity

            let secondBefore =
                requireInstance
                    firstRuntime.Store
                    secondFixture.Identity

            let latePresence =
                postReport
                    client
                    endpoint
                    (request
                        firstProcessId
                        worktreeA
                        (Some terminalA)
                        "late-presence-a"
                        "session_present"
                        None)

            ensure
                (not latePresence.Recorded &&
                 latePresence.Monitored &&
                 not latePresence.Retryable)
                $"Closed identity presence was not rejected permanently: {latePresence.Raw}"

            postRecorded
                client
                endpoint
                (request
                    firstProcessId
                    worktreeA
                    (Some terminalA)
                    "late-heartbeat-a"
                    "heartbeat"
                    None)
            |> ignore

            [ request
                  secondProcessId
                  worktreeB
                  (Some terminalB)
                  "survivor-intent-b"
                  "intent_reported"
                  (Some "surviving process working")
              request
                  secondProcessId
                  worktreeB
                  (Some terminalB)
                  "survivor-working-b"
                  "turn_started"
                  None
              request
                  secondProcessId
                  worktreeB
                  (Some terminalB)
                  "survivor-heartbeat-b"
                  "heartbeat"
                  None ]
            |> List.iter (postRecorded client endpoint >> ignore)

            firstRuntime.Service.ExactSnapshot() |> ignore

            let closedAfter =
                requireInstance
                    firstRuntime.Store
                    firstFixture.Identity

            let secondAfter =
                requireInstance
                    firstRuntime.Store
                    secondFixture.Identity

            ensure
                (closedAfter = closedBefore)
                "Closed fixture A changed after late heartbeat or presence"

            ensure
                (secondAfter.ClosedAt.IsNone &&
                 effectiveStatus secondAfter.Status =
                    SessionLevelStatus.Working &&
                 secondAfter.LastSeen > secondBefore.LastSeen &&
                 secondAfter.TerminalSessionId = Some terminalB)
                "Fixture B did not advance independently after fixture A closed"

            emitInstance "STEP3_CLOSED_A" closedAfter
            emitInstance "STEP3_OPEN_B" secondAfter
            printfn
                "STEP3 PASS closure remained exact and survivor liveness advanced independently"

            stopFixture firstFixture
            |> Result.defaultWith invalidOp

            ensure
                (not (isAlive firstFixture.Identity))
                "Fixture A process survived its exact stop"

            let oldProcessId, oldStartTicks =
                ProcessIdentity.sortKey firstFixture.Identity

            let reusedIdentity =
                ProcessIdentity.create
                    oldProcessId
                    (oldStartTicks + TimeSpan.TicksPerSecond)
                |> Result.defaultWith invalidOp

            resolverControl.Remap oldProcessId reusedIdentity

            postRecorded
                client
                endpoint
                (request
                    oldProcessId
                    worktreeA
                    (Some terminalA)
                    "reused-heartbeat-before-presence"
                    "heartbeat"
                    None)
            |> ignore

            firstRuntime.Service.ExactSnapshot() |> ignore

            ensure
                (firstRuntime.Store.InstanceByIdentity reusedIdentity).IsNone
                "A heartbeat created a reused PID identity before presence"

            postRecorded
                client
                endpoint
                (request
                    oldProcessId
                    worktreeA
                    (Some terminalA)
                    "reused-presence-a"
                    "session_present"
                    None)
            |> ignore

            postRecorded
                client
                endpoint
                (request
                    oldProcessId
                    worktreeA
                    (Some terminalA)
                    "reused-working-a"
                    "turn_started"
                    None)
            |> ignore

            firstRuntime.Service.ExactSnapshot() |> ignore
            closeProcess firstRuntime reusedIdentity

            let secondBeforeRestart =
                requireInstance
                    firstRuntime.Store
                    secondFixture.Identity

            stopActiveRuntime ()
            |> Result.defaultWith invalidOp

            let restartPresence =
                request
                    secondProcessId
                    worktreeB
                    (Some terminalB)
                    "restart-presence-b"
                    "session_present"
                    None

            match
                tryPostReport
                    client
                    endpoint
                    restartPresence
            with
            | Error _ -> ()
            | Ok acknowledge ->
                invalidOp
                    $"The stopped activity endpoint unexpectedly accepted presence: {acknowledge.Raw}"

            let secondRuntime =
                startRuntime
                    activityStore
                    [ worktreeA; worktreeB ]
                    resolverControl.Resolver
                    firstRuntime.Port

            activeRuntime <- Some secondRuntime

            let restartAttempts, restartAcknowledge =
                postPresenceAfterRestart
                    client
                    endpoint
                    restartPresence
                    2

            ensure
                (restartAcknowledge.Recorded &&
                 restartAcknowledge.Monitored)
                $"Restart presence was not acknowledged: {restartAcknowledge.Raw}"

            let step4 = secondRuntime.Service.ExactSnapshot()
            let oldAfterRestart = step4[firstFixture.Identity]
            let reusedAfterRestart = step4[reusedIdentity]
            let recoveredSecond = step4[secondFixture.Identity]

            let _, terminalInstances, pending =
                secondRuntime.Service.QueryTerminalActivityAt(
                    DateTimeOffset.UtcNow,
                    Set.singleton terminalB
                )
                |> Result.defaultWith invalidOp

            ensure
                (oldAfterRestart.ClosedAt.IsSome &&
                 reusedAfterRestart.ClosedAt.IsSome)
                "Closed old or reused identities reopened after restart"

            ensure
                (recoveredSecond.ClosedAt.IsNone &&
                 recoveredSecond.LastSeen > secondBeforeRestart.LastSeen &&
                 effectiveStatus recoveredSecond.Status = SessionLevelStatus.Working &&
                 activityText recoveredSecond.Status = "surviving process working" &&
                 recoveredSecond.TerminalSessionId = Some terminalB)
                "The surviving exact identity did not recover its persisted state"

            ensure
                (pending.IsEmpty &&
                 terminalInstances
                 |> List.exists (fun instance ->
                     instance.ProcessIdentity = secondFixture.Identity))
                "Acknowledged presence did not clear restart reconciliation"

            emitInstance "STEP4_OLD_A" oldAfterRestart
            emitInstance "STEP4_REUSED_A" reusedAfterRestart
            emitInstance "STEP4_RECOVERED_B" recoveredSecond
            printfn
                $"STEP4 PASS reusedPid={oldProcessId} distinctStartTicks={oldStartTicks}/{ProcessIdentity.processStartTimeUtcTicks reusedIdentity} heartbeatBeforePresenceCreated=false restartPresenceAttempts={restartAttempts} pendingAfterPresence={pending.Count} activeStateRecovered=true"

            [ firstFixture.Identity
              reusedIdentity
              secondFixture.Identity ]
            |> List.iter (closeProcess secondRuntime)

            let allRows =
                secondRuntime.Store.InstancesBySession(
                    SessionId durableSessionId
                )

            ensure
                (allRows.Length = 3 &&
                 allRows
                 |> List.forall _.ClosedAt.IsSome)
                "Not every exact fixture row was closed"

            stopFixture secondFixture
            |> Result.defaultWith invalidOp

            let survivingFixtureProcesses =
                [ firstFixture.Identity
                  reusedIdentity
                  secondFixture.Identity ]
                |> List.filter isAlive
                |> List.length

            ensure
                (survivingFixtureProcesses = 0)
                $"{survivingFixtureProcesses} fixture process identity or identities survived"

            stopActiveRuntime ()
            |> Result.defaultWith invalidOp

            SqliteConnection.ClearAllPools()

            deleteDirectory root 20
            |> Result.defaultWith invalidOp

            printfn
                $"STEP5 PASS closedRows={allRows.Length} survivingFixtureProcesses={survivingFixtureProcesses} temporaryStateRemoved={boolText (not (Directory.Exists root))}"

            printfn "VERDICT PASS"
            Ok()
        with error ->
            Error
                $"{error.GetType().Name}: {error.Message}"

    let cleanupErrors =
        [ match stopActiveRuntime () with
          | Ok() -> ()
          | Error error -> yield error

          yield!
              fixtures
              |> List.choose (fun fixture ->
                  match disposeFixture fixture with
                  | Ok() -> None
                  | Error error -> Some error)

          try
              SqliteConnection.ClearAllPools()
          with error ->
              yield
                  $"SQLite pool cleanup failed: {error.GetType().Name}: {error.Message}"

          match deleteDirectory root 20 with
          | Ok() -> ()
          | Error error -> yield error

          try
              restoreEnvironment
                  hostStateVariable
                  previousHostState
          with error ->
              yield
                  $"Environment restoration failed: {error.GetType().Name}: {error.Message}" ]

    match execution, cleanupErrors with
    | Ok(), [] -> 0
    | Ok(), errors ->
        let cleanup = String.concat " | " errors
        eprintfn $"VERDICT FAIL cleanup={cleanup}"

        1
    | Error error, [] ->
        eprintfn $"VERDICT FAIL reason={error}"
        1
    | Error error, errors ->
        let cleanup = String.concat " | " errors
        eprintfn $"VERDICT FAIL reason={error} cleanup={cleanup}"

        1

[<EntryPoint>]
let main args =
    match args with
    | [| "--fixture"; name |] -> runFixture name
    | [||] -> verify ()
    | _ ->
        eprintfn
            "Usage: SessionIsolationVerifier [--fixture <name>]"

        2
