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

let private terminalOrigin text =
    TerminalSessionId.create text |> Result.defaultWith invalidOp

let private terminalA = terminalOrigin "11111111111111111111111111111111"
let private terminalB = terminalOrigin "22222222222222222222222222222222"

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

type private ResolverControl =
    { Resolver: ProcessIdentityResolver
      Remap: int -> ProcessIdentity -> unit }

/// The reporting identity one fixture posts under: its PID, its isolated worktree, and the terminal
/// it claims as its origin. Both reporters share one durable session id on purpose.
type private Reporter =
    { ProcessId: int
      WorktreePath: string
      Terminal: TerminalSessionId }

/// The acknowledgement flags the activity endpoint returns. Phases assert on the whole record so a
/// wrong-but-adjacent acknowledgement (retryable instead of permanent) cannot pass unnoticed.
type private Acknowledge =
    { Recorded: bool
      Monitored: bool
      Retryable: bool }

let private recordedAck =
    { Recorded = true; Monitored = true; Retryable = false }

let private rejectedAck =
    { Recorded = false; Monitored = true; Retryable = false }

/// One exact instance row reduced to the facts phases assert on. The liveness clock is deliberately
/// excluded so an expected row stays comparable by plain equality.
type private InstanceFacts =
    { Pid: int
      StartTicks: int64
      SessionId: string
      Origin: string
      Status: SessionLevelStatus
      Activity: string
      Closed: bool }

/// One observed exact instance row: its comparable facts plus its liveness clock.
type private InstanceEvidence =
    { Facts: InstanceFacts
      LastSeen: DateTimeOffset }

/// Which view of an exact row a phase reads. The mailbox snapshot proves open in-memory state,
/// including the rebuild after a service restart; the store also retains closed durable history.
type private RowSource =
    | LiveSnapshot
    | DurableStore

/// Whether an exact row is expected to still be open or already closed.
type private RowState =
    | StillOpen
    | AlreadyClosed

/// Durable exact rows recorded for the shared durable session.
type private RowCensus = { Rows: int; Closed: int }

let private ensure condition message =
    if not condition then
        invalidOp message

let private boolText value = if value then "true" else "false"

let private identityText identity =
    let processId, startTicks = ProcessIdentity.sortKey identity
    $"pid={processId} startTicks={startTicks}"

let private renderFacts facts =
    $"pid={facts.Pid} startTicks={facts.StartTicks} sessionId={facts.SessionId} status={facts.Status} "
    + $"origin={facts.Origin} activity={facts.Activity} closed={boolText facts.Closed}"

let private storedFacts (stored: StoredInstance) =
    let processId, startTicks = ProcessIdentity.sortKey stored.ProcessIdentity

    { Pid = processId
      StartTicks = startTicks
      SessionId = SessionId.value stored.SessionId
      Origin =
        stored.TerminalSessionId
        |> Option.map TerminalSessionId.value
        |> Option.defaultValue "(none)"
      Status = effectiveStatus stored.Status
      Activity =
        stored.Status
        |> effectiveActivity
        |> Option.map (AgentActivity.textAndTimestamp >> fst)
        |> Option.defaultValue "(none)"
      Closed = stored.ClosedAt.IsSome }

/// One labelled row expectation: everything but the exact identity is stated by the phase, and the
/// PID, start ticks and durable session id are derived from that identity.
let private expectedRow label identity origin status state activity =
    let processId, startTicks = ProcessIdentity.sortKey identity

    label,
    identity,
    { Pid = processId
      StartTicks = startTicks
      SessionId = durableSessionId
      Origin = TerminalSessionId.value origin
      Status = status
      Activity = activity
      Closed = state = AlreadyClosed }

/// Reports one cleanup step: `None` means it succeeded.
let private cleanupError description action =
    try
        action ()
        None
    with error ->
        Some $"{description}: {error.GetType().Name}: {error.Message}"

/// Turns collected cleanup failures into a phase failure.
let private orFail description errors =
    if not (List.isEmpty errors) then
        let joined = String.concat " | " errors
        invalidOp $"{description}: {joined}"

/// Polls until the attempt succeeds, returning how many attempts that took.
let private expectEventually description attempt =
    let rec poll attemptNumber =
        match attempt () with
        | Ok value -> attemptNumber, value
        | Error _ when attemptNumber < 20 ->
            Thread.Sleep 100
            poll (attemptNumber + 1)
        | Error reason -> invalidOp $"{description} after {attemptNumber} attempts: {reason}"

    poll 1

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

    [ assemblyPath; "--fixture"; name ] |> List.iter startInfo.ArgumentList.Add

    let childProcess =
        Process.Start startInfo
        |> Option.ofObj
        |> Option.defaultWith (fun () -> invalidOp $"Could not start fixture process {name}")

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
                ProcessIdentityResolver.resolve childProcess.Id ProcessIdentityResolverRuntime.defaultResolver
            with
            | Ok(Some value) -> value
            | Ok None -> invalidOp $"Fixture {name} exited before identity resolution"
            | Error error -> invalidOp $"Could not resolve fixture {name} identity: {error}"

        { Name = name
          Process = childProcess
          Identity = identity }
    with error ->
        cleanupError $"Fixture {name} startup cleanup" (fun () ->
            if not childProcess.HasExited then
                childProcess.Kill(entireProcessTree = true)
                childProcess.WaitForExit 5_000 |> ignore)
        |> Option.iter (eprintfn "%s")

        childProcess.Dispose()
        raise error

let private stopFixture fixture =
    cleanupError $"Fixture {fixture.Name} PID {fixture.Process.Id} stop" (fun () ->
        if not fixture.Process.HasExited then
            fixture.Process.StandardInput.WriteLine "stop"
            fixture.Process.StandardInput.Flush()

            if not (fixture.Process.WaitForExit 5_000) then
                fixture.Process.Kill(entireProcessTree = true)

                ensure
                    (fixture.Process.WaitForExit 5_000)
                    $"PID {fixture.Process.Id} survived exact cleanup")
    |> Option.toList

let private disposeFixture fixture =
    stopFixture fixture
    @ (cleanupError $"Fixture {fixture.Name} disposal" (fun () -> fixture.Process.Dispose())
       |> Option.toList)

let private isAlive identity =
    ProcessIdentityResolver.isAlive ProcessIdentityResolverRuntime.defaultResolver identity
    |> Result.defaultWith invalidOp

let private createResolver (identities: Map<int, ProcessIdentity>) =
    let gate = obj ()
    // The resolver changes one PID's exact identity during the deliberate PID-reuse phase.
    let mutable current = identities

    { Resolver = ProcessIdentityResolver.create (fun processId -> lock gate (fun () -> current |> Map.tryFind processId |> Ok))
      Remap = fun processId identity -> lock gate (fun () -> current <- current |> Map.add processId identity) }

let private initializeWorktree path =
    Directory.CreateDirectory path |> ignore

    let startInfo =
        ProcessStartInfo(
            FileName = "git",
            WorkingDirectory = path,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            CreateNoWindow = true
        )

    [ "init"; "--quiet"; "--initial-branch=main" ] |> List.iter startInfo.ArgumentList.Add

    use childProcess =
        Process.Start startInfo
        |> Option.ofObj
        |> Option.defaultWith (fun () -> invalidOp "Could not start git")

    let stdout = childProcess.StandardOutput.ReadToEndAsync()
    let stderr = childProcess.StandardError.ReadToEndAsync()
    childProcess.WaitForExit()
    let output = stdout.GetAwaiter().GetResult() + stderr.GetAwaiter().GetResult()

    ensure
        (childProcess.ExitCode = 0)
        $"git init in '{path}' exited with {childProcess.ExitCode}: {output}"

let private registerWorktrees (scheduler: MailboxProcessor<SchedulerState.StateMsg>) worktreePaths =
    let worktrees =
        worktreePaths
        |> List.map (fun path ->
            { Path = PathUtils.normalizePath path
              Head = ""
              Branch = Some "verify" }
            : GitWorktree.WorktreeInfo)

    let repoId = RepoId "session-isolation-verifier"
    scheduler.Post(SchedulerState.UpdateWorktreeList(repoId, worktrees))
    let state = scheduler.PostAndReply SchedulerState.GetState
    let expected = worktreePaths |> List.map PathUtils.normalizePath |> Set.ofList

    ensure
        (state.Repos[repoId].KnownPaths = expected)
        "The isolated worktrees were not registered with the activity service"

let private startApplication (service: SessionActivityService) requestedPort =
    let builder = WebApplication.CreateSlimBuilder()
    builder.Logging.ClearProviders() |> ignore
    builder.Services.AddGiraffe() |> ignore

    builder.WebHost.ConfigureKestrel(fun options ->
        options.AddServerHeader <- false
        options.Listen(IPAddress.Loopback, requestedPort))
    |> ignore

    let application = builder.Build()
    let next: HttpFunc = fun context -> Task.FromResult(Some context)

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
        application.StartAsync(CancellationToken.None).GetAwaiter().GetResult()

        let server =
            application.Services.GetRequiredService<global.Microsoft.AspNetCore.Hosting.Server.IServer>()

        let address =
            server.Features
                .Get<global.Microsoft.AspNetCore.Hosting.Server.Features.IServerAddressesFeature>()
                .Addresses
            |> Seq.exactlyOne
            |> Uri

        application, address.Port
    with error ->
        application.DisposeAsync().AsTask().GetAwaiter().GetResult()
        raise error

let private startRuntime dbPath worktreePaths resolver requestedPort =
    let scheduler = SchedulerState.createAgent ()
    registerWorktrees scheduler worktreePaths
    let store = new SessionActivityStore(dbPath)
    let service = new SessionActivityService(store, scheduler, resolver)

    try
        service.Start()
        let application, port = startApplication service requestedPort
        ensure (port <> 5000) "The isolated activity server bound production port 5000"

        ensure
            (requestedPort = 0 || port = requestedPort)
            $"The activity server restarted on {port} instead of {requestedPort}"

        { Application = application
          Port = port
          Scheduler = scheduler
          Store = store
          Service = service }
    with error ->
        [ service :> IDisposable; store; scheduler ] |> List.iter _.Dispose()
        raise error

let private stopRuntime runtime =
    [ cleanupError "Activity server stop" (fun () ->
          runtime.Application.StopAsync(CancellationToken.None).GetAwaiter().GetResult())
      cleanupError "Activity server disposal" (fun () ->
          runtime.Application.DisposeAsync().AsTask().GetAwaiter().GetResult())
      cleanupError "Activity service disposal" (fun () -> (runtime.Service :> IDisposable).Dispose())
      cleanupError "Activity store disposal" (fun () -> (runtime.Store :> IDisposable).Dispose())
      cleanupError "Scheduler disposal" (fun () -> (runtime.Scheduler :> IDisposable).Dispose()) ]
    |> List.choose id

let rec private removeDirectory path attempts =
    if not (Directory.Exists path) then
        []
    else
        try
            Directory.Delete(path, recursive = true)

            if Directory.Exists path then
                [ $"Temporary verifier path survived cleanup: {path}" ]
            else
                []
        with
        | :? IOException | :? UnauthorizedAccessException when attempts > 0 ->
            Thread.Sleep 100
            removeDirectory path (attempts - 1)
        | error ->
            [ $"Could not remove temporary verifier path '{path}': {error.GetType().Name}: {error.Message}" ]

type private ActivityEndpoint =
    { Client: HttpClient
      Url: string }

let private report reporter eventId kind messageText =
    let occurredAt = DateTimeOffset.UtcNow.ToString("O", CultureInfo.InvariantCulture)

    { SessionActivityRequest.parentProcessId = reporter.ProcessId
      sessionId = durableSessionId
      terminalSessionId = TerminalSessionId.value reporter.Terminal
      worktreePath = reporter.WorktreePath
      provider = "copilot_cli"
      eventId = eventId
      occurredAt = occurredAt
      kind = kind
      message =
        messageText
        |> Option.map (fun text -> { MessageDto.text = text; at = occurredAt })
        |> Option.defaultValue Unchecked.defaultof<MessageDto>
      skillName = Unchecked.defaultof<string>
      toolCallId = Unchecked.defaultof<string>
      currentTokens = 0
      tokenLimit = 0 }

/// Posts one report, returning the acknowledgement flags with the endpoint's optional reason, or the
/// transport failure that a stopped endpoint produces.
let private tryPost endpoint request =
    let work =
        task {
            let payload = JsonSerializer.Serialize request
            use content = new StringContent(payload, Encoding.UTF8, "application/json")
            use! response = endpoint.Client.PostAsync(endpoint.Url, content)
            let! raw = response.Content.ReadAsStringAsync()

            ensure
                response.IsSuccessStatusCode
                $"Activity endpoint returned {int response.StatusCode}: {raw}"

            use document = JsonDocument.Parse raw
            let root = document.RootElement

            let reason =
                match root.TryGetProperty "reason" with
                | true, value -> value.GetString()
                | _ -> ""

            return
                { Recorded = root.GetProperty("recorded").GetBoolean()
                  Monitored = root.GetProperty("monitored").GetBoolean()
                  Retryable = root.GetProperty("retryable").GetBoolean() },
                reason
        }

    try
        Ok(work.GetAwaiter().GetResult())
    with
    | :? HttpRequestException as error -> Error error.Message
    | :? TaskCanceledException as error -> Error error.Message

/// Posts one report and asserts the endpoint acknowledged it exactly as the phase expects.
let private postExpecting endpoint label expected request =
    match tryPost endpoint request with
    | Ok(acknowledge, reason) ->
        ensure
            (acknowledge = expected)
            $"{label}: expected {expected} but observed {acknowledge} reason={reason}"
    | Error error -> invalidOp $"{label} failed: {error}"

let private send endpoint reporter eventId kind messageText =
    report reporter eventId kind messageText
    |> postExpecting endpoint $"Report {eventId}" recordedAck

/// Reads the named exact rows behind a single mailbox drain, asserts each against its expected facts,
/// prints it as phase evidence, and returns the observed rows for liveness-clock comparisons.
let private expectRows runtime source expectations =
    let snapshot = runtime.Service.ExactSnapshot()

    expectations
    |> List.map (fun (label, identity, expected) ->
        let observed =
            (match source with
             | LiveSnapshot -> snapshot |> Map.tryFind identity
             | DurableStore -> runtime.Store.InstanceByIdentity identity)
            |> Option.map (fun stored ->
                { Facts = storedFacts stored
                  LastSeen = stored.LastSeen })
            |> Option.defaultWith (fun () ->
                invalidOp $"Missing exact instance {identityText identity} in {source}")

        ensure
            (observed.Facts = expected)
            $"{label} mismatch: expected [{renderFacts expected}] observed [{renderFacts observed.Facts}]"

        printfn $"{label} {renderFacts observed.Facts} lastSeen={observed.LastSeen:O}"
        identity, observed)
    |> Map.ofList

let private expectCensus label expected (store: SessionActivityStore) =
    let rows = store.InstancesBySession(SessionId durableSessionId)

    let observed =
        { Rows = rows.Length
          Closed = rows |> List.filter _.ClosedAt.IsSome |> List.length }

    ensure (observed = expected) $"{label}: expected {expected} but observed {observed}"
    observed

/// Rows persisted for one event id, counted per exact reporting identity.
let private eventCensus dbPath eventId =
    use connection =
        new SqliteConnection(SqliteConnectionStringBuilder(DataSource = dbPath, Pooling = false).ConnectionString)

    connection.Open()
    use command = connection.CreateCommand()

    command.CommandText <-
        "SELECT process_id, process_start_ticks, count(*) FROM activity_events \
         WHERE event_id = $eventId GROUP BY process_id, process_start_ticks;"

    command.Parameters.AddWithValue("$eventId", eventId) |> ignore
    use reader = command.ExecuteReader()

    let rec read counts =
        if reader.Read() then
            read (counts |> Map.add (reader.GetInt32 0, reader.GetInt64 1) (reader.GetInt32 2))
        else
            counts

    read Map.empty

let private renderEventCensus counts =
    counts
    |> Map.toList
    |> List.map (fun ((processId, startTicks), count) -> $"{processId}:{startTicks}={count}")
    |> String.concat ","

let private closeProcess runtime identity =
    match runtime.Service.CloseProcess(identity, DateTimeOffset.UtcNow) with
    | ClosureAcknowledge.Closed -> ()
    | ClosureAcknowledge.Missing -> invalidOp $"Could not close missing exact identity {identityText identity}"
    | ClosureAcknowledge.Failed error -> invalidOp error

let private verify () =
    let root =
        Path.Combine(Path.GetTempPath(), $"treemon-session-isolation-{Guid.NewGuid():N}")

    let worktreeA = Path.Combine(root, "worktree-a")
    let worktreeB = Path.Combine(root, "worktree-b")
    let activityStore = Path.Combine(root, "activity", "activity.db")
    let terminalHostState = Path.Combine(root, "terminal-host-state")
    let hostStateVariable = "TREEMON_TERMINAL_HOST_STATE_DIR"

    let previousHostState =
        Environment.GetEnvironmentVariable hostStateVariable |> Option.ofObj

    // Runtime and child-process ownership changes across the restart phase, so failure cleanup
    // retains the currently acquired exact resources at this narrow orchestration boundary.
    let mutable activeRuntime: ActivityRuntime option = None
    let mutable fixtures: FixtureProcess list = []

    [ root; Path.GetDirectoryName activityStore; terminalHostState ]
    |> List.iter (Directory.CreateDirectory >> ignore)

    Environment.SetEnvironmentVariable(hostStateVariable, terminalHostState)

    let acquireFixture name =
        let fixture = startFixture name
        fixtures <- fixture :: fixtures
        fixture

    let stopActiveRuntime () =
        match activeRuntime with
        | None -> []
        | Some runtime ->
            match stopRuntime runtime with
            | [] ->
                activeRuntime <- None
                []
            | errors -> errors

    let execution =
        try
            [ worktreeA; worktreeB ] |> List.iter initializeWorktree

            let fixtureA = acquireFixture "A"
            let fixtureB = acquireFixture "B"

            ensure
                (fixtureA.Process.Id <> fixtureB.Process.Id && fixtureA.Identity <> fixtureB.Identity)
                "The two fixture processes did not obtain distinct exact identities"

            let resolverControl =
                [ fixtureA; fixtureB ]
                |> List.map (fun fixture -> fixture.Process.Id, fixture.Identity)
                |> Map.ofList
                |> createResolver

            let hostConfig =
                TerminalHostClient.defaultConfigWithProcessIdentityResolver resolverControl.Resolver []

            ensure
                (Path.GetFullPath hostConfig.HostStateDirectory = Path.GetFullPath terminalHostState)
                "TerminalHost state did not resolve to the isolated verifier path"

            let firstRuntime =
                startRuntime activityStore [ worktreeA; worktreeB ] resolverControl.Resolver 0

            activeRuntime <- Some firstRuntime
            use client = new HttpClient(Timeout = TimeSpan.FromSeconds 2.0)

            let endpoint =
                { Client = client
                  Url = $"http://127.0.0.1:{firstRuntime.Port}/api/session/activity" }

            let reporterA =
                { ProcessId = ProcessIdentity.processId fixtureA.Identity
                  WorktreePath = worktreeA
                  Terminal = terminalA }

            let reporterB =
                { ProcessId = ProcessIdentity.processId fixtureB.Identity
                  WorktreePath = worktreeB
                  Terminal = terminalB }

            printfn
                $"ISOLATION port={firstRuntime.Port} activityStore={activityStore} terminalHostState={terminalHostState} worktreeA={worktreeA} worktreeB={worktreeB}"

            send endpoint reporterA "presence-a" "session_present" None
            send endpoint reporterB "presence-b" "session_present" None
            send endpoint reporterA "title-a" "title_reported" (Some "first process working")
            send endpoint reporterA "working-a" "turn_started" None
            send endpoint reporterB "title-b" "title_reported" (Some "second process idle")
            send endpoint reporterB "idle-b" "went_idle" None

            expectRows
                firstRuntime
                LiveSnapshot
                [ expectedRow "STEP1_ROW_A" fixtureA.Identity terminalA SessionLevelStatus.Working StillOpen
                      "first process working"
                  expectedRow "STEP1_ROW_B" fixtureB.Identity terminalB SessionLevelStatus.Idle StillOpen
                      "second process idle" ]
            |> ignore

            expectCensus "STEP1 durable rows" { Rows = 2; Closed = 0 } firstRuntime.Store |> ignore
            printfn $"STEP1 PASS durableSessionId={durableSessionId} distinctExactIdentities=true independentOrigins=true"

            let sharedEvent = "shared-event-id"

            let postShared () =
                send endpoint reporterA sharedEvent "intent_reported" (Some "first shared event")
                send endpoint reporterB sharedEvent "intent_reported" (Some "second shared event")
                firstRuntime.Service.ExactSnapshot() |> ignore
                eventCensus activityStore sharedEvent

            let firstPass = postShared ()
            let afterRetry = postShared ()

            let expectedEventRows =
                Map.ofList
                    [ ProcessIdentity.sortKey fixtureA.Identity, 1
                      ProcessIdentity.sortKey fixtureB.Identity, 1 ]

            ensure
                (firstPass = expectedEventRows && afterRetry = expectedEventRows)
                $"Event idempotency was not scoped to exact identity: first=[{renderEventCensus firstPass}] retry=[{renderEventCensus afterRetry}]"

            let totalRows counts = counts |> Map.values |> Seq.sum

            printfn
                $"STEP2 PASS eventId={sharedEvent} totalRows={totalRows afterRetry} rowsPerIdentity={renderEventCensus afterRetry} retriesAdded={totalRows afterRetry - totalRows firstPass}"

            closeProcess firstRuntime fixtureA.Identity

            let beforeLate =
                expectRows
                    firstRuntime
                    DurableStore
                    [ expectedRow "STEP3_CLOSED_A" fixtureA.Identity terminalA SessionLevelStatus.Working AlreadyClosed
                          "first shared event"
                      expectedRow "STEP3_BEFORE_B" fixtureB.Identity terminalB SessionLevelStatus.Idle StillOpen
                          "second shared event" ]

            report reporterA "late-presence-a" "session_present" None
            |> postExpecting endpoint "Late presence on the closed exact identity" rejectedAck

            send endpoint reporterA "late-heartbeat-a" "heartbeat" None
            send endpoint reporterB "survivor-intent-b" "intent_reported" (Some "surviving process working")
            send endpoint reporterB "survivor-working-b" "turn_started" None
            send endpoint reporterB "survivor-heartbeat-b" "heartbeat" None

            let afterLate =
                expectRows
                    firstRuntime
                    DurableStore
                    [ expectedRow "STEP3_STILL_CLOSED_A" fixtureA.Identity terminalA SessionLevelStatus.Working
                          AlreadyClosed "first shared event"
                      expectedRow "STEP3_OPEN_B" fixtureB.Identity terminalB SessionLevelStatus.Working StillOpen
                          "surviving process working" ]

            ensure
                (afterLate[fixtureA.Identity] = beforeLate[fixtureA.Identity])
                "The closed exact row changed after a late presence or heartbeat"

            ensure
                (afterLate[fixtureB.Identity].LastSeen > beforeLate[fixtureB.Identity].LastSeen)
                "The surviving exact row did not advance its liveness clock independently"

            printfn "STEP3 PASS closure stayed exact and the survivor advanced independently"

            stopFixture fixtureA |> orFail "Fixture A stop"
            ensure (not (isAlive fixtureA.Identity)) "Fixture A process survived its exact stop"
            let reusedPid, priorStartTicks = ProcessIdentity.sortKey fixtureA.Identity

            let reusedIdentity =
                ProcessIdentity.create reusedPid (priorStartTicks + TimeSpan.TicksPerSecond)
                |> Result.defaultWith invalidOp

            resolverControl.Remap reusedPid reusedIdentity
            send endpoint reporterA "reused-heartbeat-before-presence" "heartbeat" None
            firstRuntime.Service.ExactSnapshot() |> ignore

            ensure
                (firstRuntime.Store.InstanceByIdentity reusedIdentity).IsNone
                "A heartbeat created the reused PID row before acknowledged presence"

            send endpoint reporterA "reused-presence-a" "session_present" None
            send endpoint reporterA "reused-working-a" "turn_started" None

            let afterReuse =
                expectRows
                    firstRuntime
                    DurableStore
                    [ expectedRow "STEP4_REUSED_OPEN_A" reusedIdentity terminalA SessionLevelStatus.Working StillOpen
                          "(none)"
                      expectedRow "STEP4_PRIOR_CLOSED_A" fixtureA.Identity terminalA SessionLevelStatus.Working
                          AlreadyClosed "first shared event"
                      expectedRow "STEP4_BEFORE_RESTART_B" fixtureB.Identity terminalB SessionLevelStatus.Working
                          StillOpen "surviving process working" ]

            ensure
                (afterReuse[fixtureA.Identity] = beforeLate[fixtureA.Identity])
                "The prior closed exact row changed when its PID was reused"

            closeProcess firstRuntime reusedIdentity
            let restartPort = firstRuntime.Port
            stopActiveRuntime () |> orFail "Activity runtime stop before restart"
            let restartPresence = report reporterB "restart-presence-b" "session_present" None

            match tryPost endpoint restartPresence with
            | Error _ -> ()
            | Ok(acknowledge, _) ->
                invalidOp $"The stopped activity endpoint unexpectedly accepted presence: {acknowledge}"

            let secondRuntime =
                startRuntime activityStore [ worktreeA; worktreeB ] resolverControl.Resolver restartPort

            activeRuntime <- Some secondRuntime

            let restartAttempts, _ =
                expectEventually "Restart presence was not recorded" (fun () ->
                    match tryPost endpoint restartPresence with
                    | Ok(acknowledge, _) when acknowledge = recordedAck -> Ok acknowledge
                    | Ok(acknowledge, reason) when acknowledge.Retryable -> Error $"{acknowledge} reason={reason}"
                    | Ok(acknowledge, reason) -> invalidOp $"Restart presence was refused: {acknowledge} reason={reason}"
                    | Error error -> Error error)

            let recovered =
                expectRows
                    secondRuntime
                    LiveSnapshot
                    [ expectedRow "STEP4_RECOVERED_B" fixtureB.Identity terminalB SessionLevelStatus.Working StillOpen
                          "surviving process working" ]

            ensure
                (secondRuntime.Service.ExactSnapshot().Count = 1)
                "The restart live snapshot retained a closed exact identity"

            expectRows
                secondRuntime
                DurableStore
                [ expectedRow "STEP4_DURABLE_OLD_A" fixtureA.Identity terminalA SessionLevelStatus.Working AlreadyClosed
                      "first shared event"
                  expectedRow "STEP4_DURABLE_REUSED_A" reusedIdentity terminalA SessionLevelStatus.Working AlreadyClosed
                      "(none)" ]
            |> ignore

            ensure
                (recovered[fixtureB.Identity].LastSeen > afterReuse[fixtureB.Identity].LastSeen)
                "The surviving exact identity did not refresh its liveness after the restart"

            let _, terminalInstances, pending =
                secondRuntime.Service.QueryTerminalActivityAt(DateTimeOffset.UtcNow, Set.singleton terminalB)
                |> Result.defaultWith invalidOp

            ensure
                (pending.IsEmpty
                 && terminalInstances |> List.exists (fun instance -> instance.ProcessIdentity = fixtureB.Identity))
                "Acknowledged presence did not clear restart reconciliation"

            printfn
                $"STEP4 PASS reusedPid={reusedPid} distinctStartTicks={priorStartTicks}/{ProcessIdentity.processStartTimeUtcTicks reusedIdentity} heartbeatBeforePresenceCreated=false restartPort={secondRuntime.Port} restartPresenceAttempts={restartAttempts} pendingAfterPresence={pending.Count} activeStateRecovered=true"

            closeProcess secondRuntime fixtureB.Identity
            let exactIdentities = [ fixtureA.Identity; reusedIdentity; fixtureB.Identity ]
            let census = expectCensus "STEP5 durable rows" { Rows = 3; Closed = 3 } secondRuntime.Store
            stopFixture fixtureB |> orFail "Fixture B stop"
            let survivors = exactIdentities |> List.filter isAlive |> List.length
            ensure (survivors = 0) $"{survivors} fixture process identity or identities survived"
            stopActiveRuntime () |> orFail "Activity runtime stop"
            SqliteConnection.ClearAllPools()
            removeDirectory root 20 |> orFail "Temporary verifier state removal"

            printfn
                $"STEP5 PASS closedRows={census.Closed} survivingFixtureProcesses={survivors} temporaryStateRemoved={boolText (not (Directory.Exists root))}"

            printfn "VERDICT PASS"
            Ok()
        with error ->
            Error $"{error.GetType().Name}: {error.Message}"

    let cleanupErrors =
        stopActiveRuntime ()
        @ (fixtures |> List.collect disposeFixture)
        @ (cleanupError "SQLite pool cleanup" (fun () -> SqliteConnection.ClearAllPools())
           |> Option.toList)
        @ removeDirectory root 20
        @ (cleanupError "Environment restoration" (fun () ->
               Environment.SetEnvironmentVariable(
                   hostStateVariable,
                   previousHostState |> Option.defaultValue Unchecked.defaultof<string>
               ))
           |> Option.toList)

    match execution, cleanupErrors with
    | Ok(), [] -> 0
    | outcome, errors ->
        let reason =
            match outcome with
            | Error error -> $" reason={error}"
            | Ok() -> ""

        let joined = String.concat " | " errors
        let cleanup = if List.isEmpty errors then "" else $" cleanup={joined}"
        eprintfn $"VERDICT FAIL{reason}{cleanup}"
        1

[<EntryPoint>]
let main args =
    match args with
    | [| "--fixture"; name |] -> runFixture name
    | [||] -> verify ()
    | _ ->
        eprintfn "Usage: SessionIsolationVerifier [--fixture <name>]"
        2
