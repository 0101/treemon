module Tests.SessionActivityServiceTests

open System
open System.IO
open System.Text
open System.Text.Json
open System.Threading.Tasks
open Giraffe
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open NUnit.Framework
open Shared
open Server
open Server.SessionActivity
open Server.SessionActivityIngestion
open Server.SessionActivityProtocol
open Server.SessionActivityStore
open Server.SessionActivityService
open Server.TerminalSessionActivity
open Tests.TestUtils

// Covers the ingestion layer of the push status model: the wire-contract DTO → domain parse (the
// closed kind set, unknown rejected, per-kind message/skill rules), the known-worktree guard
// (tryAcceptReport), and the single-writer mailbox flow (fold → persist/dedupe → last-write-wins
// upsert → feed RefreshScheduler), the HTTP binding/error boundary, and restart rebuild from the
// store. Fast/in-process — handler tests use an in-memory HttpContext rather than a socket.

/// Reference "now" for the pure parse tests — just after every baseReq occurredAt used below, so a
/// past/current occurredAt passes the future-skew clamp untouched.
let private refNow = ts "2026-03-01T10:05:00Z"

// --- DTO builders ------------------------------------------------------------------------------

let private noMsg: MessageDto = Unchecked.defaultof<MessageDto>
let private msgDto text at : MessageDto = { text = text; at = at }

let private baseReq kind : SessionActivityRequest =
    { parentProcessId = 10_001
      sessionId = "s1"
      terminalSessionId = null
      worktreePath = "C:/wt/a"
      provider = "copilot_cli"
      eventId = "e1"
      occurredAt = "2026-03-01T10:00:00Z"
      kind = kind
      message = noMsg
      skillName = null
      toolCallId = null
      currentTokens = 0
      tokenLimit = 0 }

let private parseOk req =
    match parseReport refNow req with
    | Ok r -> r
    | Error e ->
        Assert.Fail $"expected Ok, got Error: {e}"
        failwith "unreachable"

let private parseErr req =
    match parseReport refNow req with
    | Ok _ ->
        Assert.Fail "expected Error, got Ok"
        failwith "unreachable"
    | Error e -> e

let private queryOwnedOk
    (service: SessionActivityService)
    now
    terminalSessionIds
    =
    match
        queryOwnedSessions
            (fun ids -> service.QueryTerminalActivity ids)
            now
            terminalSessionIds
    with
    | Ok snapshot -> snapshot
    | Error error ->
        Assert.Fail $"expected owned-session snapshot, got Error: {error}"
        failwith "unreachable"

let private queryActivityOk
    (service: SessionActivityService)
    terminalSessionIds
    : int64 * StoredInstance list =
    match service.QueryTerminalActivity terminalSessionIds with
    | Ok(epoch, instances, _) -> epoch, instances
    | Error error ->
        Assert.Fail $"expected terminal activity snapshot, got Error: {error}"
        failwith "unreachable"

let private replacementTerminal
    terminalSessionId
    worktreePath
    : TerminalHostReplacement.ReplacementTerminal =
    { TerminalSessionId = terminalSessionId
      WorktreePath = worktreePath }

let private replacementResume sessionId command:
    TerminalHostReplacement.ReplacementResumeCommand =
    { CopilotSessionId = SessionId sessionId
      Command = command }

let private queryReplacementPlanOk
    (service: SessionActivityService)
    now
    terminals
    =
    match
        queryReplacementPlan
            CodingToolStatus.readConfiguredProvider
            (fun ids -> service.QueryTerminalActivity ids)
            now
            terminals
    with
    | Ok plan -> plan
    | Error error ->
        Assert.Fail $"expected replacement session plan, got Error: {error}"
        failwith "unreachable"

let private requireReplacementReady =
    function
    | TerminalHostReplacement.ReplacementSessionPlan.Ready(
        epoch,
        shutdownTargets,
        commands
      ) ->
        epoch, shutdownTargets, commands
    | TerminalHostReplacement.ReplacementSessionPlan.WaitingForIdle ->
        Assert.Fail "expected a ready replacement session plan"
        failwith "unreachable"

// --- Service / store fixture -------------------------------------------------------------------

let private processIdentityResolver =
    ProcessIdentityResolver.create (fun processId ->
        processId
        |> syntheticProcessIdentityForProcessId
        |> Some
        |> Ok)

let private mkReport sid wt eid (t: string) ev : SessionActivityReport =
    { ParentProcessId = syntheticProcessIdForSessionId sid
      SessionId = SessionId sid
      TerminalSessionId = None
      WorktreePath = WorktreePath(PathUtils.normalizePath wt)
      Provider = CopilotCli
      EventId = EventId eid
      OccurredAt = ts t
      Event = ev }

let private present
    (service: SessionActivityService)
    sid
    worktree
    (receivedAt: DateTimeOffset)
    =
    let report =
        mkReport
            sid
            worktree
            $"presence-{receivedAt.UtcTicks}"
            (receivedAt.ToString("O"))
            SessionPresent

    match service.Present(report, receivedAt) with
    | PresenceAcknowledge.Recorded identity -> identity
    | PresenceAcknowledge.NotRecorded(_, reason) ->
        Assert.Fail $"expected acknowledged presence, got: {reason}"
        failwith "unreachable"

let private storedWithUsage sid worktree status updatedAt usage usageAt =
    let sessionId = SessionId sid

    { ProcessIdentity = syntheticProcessIdentityForSessionId sid
      SessionId = sessionId
      TerminalSessionId = None
      WorktreePath = WorktreePath(PathUtils.normalizePath worktree)
      Provider = CopilotCli
      Status = { status with ContextUsage = Some usage }
      UpdatedAt = updatedAt
      LifecycleAt = Some updatedAt
      LastSeen = usageAt
      ContextUsageAt = Some usageAt
      ClosedAt = None }

type SessionActivityStore with
    member store.LoadLiveStatuses(now: DateTimeOffset) =
        store.LoadRecentInstances now

    member store.StatusBySession(sessionId: SessionId) =
        store.InstancesBySession sessionId
        |> List.tryHead

    member store.UpsertStatus(stored: StoredInstance) =
        store.UpsertInstance stored |> ignore

    member store.UpsertContextUsage(stored: StoredInstance) =
        store.UpsertInstance stored

type SessionActivityService with
    /// Legacy fixture convenience for tests that intentionally create one process per durable
    /// session. Exact-multiplicity tests use `ExactSnapshot` directly.
    member service.LiveSnapshot() =
        service.ExactSnapshot()
        |> Map.values
        |> Seq.map (fun instance ->
            instance.SessionId, instance)
        |> Map.ofSeq

/// A service over a throwaway temp .db, with `knownWorktree` registered as a monitored path on a
/// fresh scheduler agent. `seed` runs against the store before the service is constructed (used by
/// the restart-rebuild test). Program owns the shared store, so the fixture disposes it after the
/// service.
let private withServiceSeededAndPathUsingResolver
    (knownWorktree: string)
    (seed: SessionActivityStore -> unit)
    (resolver: ProcessIdentityResolver)
    (action:
        SessionActivityService
            * MailboxProcessor<SchedulerState.StateMsg>
            * SessionActivityStore
            * string
            -> unit)
    =
    let dir = Path.Combine(Path.GetTempPath(), $"treemon-svc-test-{Guid.NewGuid()}")
    Directory.CreateDirectory dir |> ignore
    let dbPath = Path.Combine(dir, "activity.db")
    let store = new SessionActivityStore(dbPath)
    seed store

    let agent = SchedulerState.createAgent ()

    let info: GitWorktree.WorktreeInfo =
        { Path = PathUtils.normalizePath knownWorktree
          Head = ""
          Branch = Some "test" }

    agent.Post(SchedulerState.UpdateWorktreeList(RepoId "svc-test-repo", [ info ]))

    let svc =
        new SessionActivityService(
            store,
            agent,
            resolver
        )

    try
        action (svc, agent, store, dbPath)
    finally
        (svc :> IDisposable).Dispose()
        (store :> IDisposable).Dispose()
        try Directory.Delete(dir, true) with _ -> ()

let private withServiceSeededAndPath knownWorktree seed action =
    withServiceSeededAndPathUsingResolver
        knownWorktree
        seed
        processIdentityResolver
        action

let private withServiceSeeded knownWorktree seed action =
    withServiceSeededAndPath
        knownWorktree
        seed
        (fun (service, agent, store, _) ->
            action (service, agent, store))


let private withService knownWorktree action = withServiceSeeded knownWorktree ignore action
let private withServiceAndPath knownWorktree action =
    withServiceSeededAndPath knownWorktree ignore action

let private eventCount dbPath =
    SqliteTestDatabase.scalarInt dbPath "SELECT count(*) FROM activity_events;"

let private eventStatusCount dbPath eventId status =
    use connection = SqliteTestDatabase.openConnection dbPath
    use command = connection.CreateCommand()
    command.CommandText <-
        "SELECT count(*) FROM activity_events WHERE event_id = $eventId AND status = $status;"
    command.Parameters.AddWithValue("$eventId", eventId) |> ignore
    command.Parameters.AddWithValue("$status", status) |> ignore
    Convert.ToInt32(command.ExecuteScalar())

type private PersistedEvent =
    { EventId: string
      Kind: string
      Status: string
      Skill: string option }

let private persistedEvents dbPath =
    use connection = SqliteTestDatabase.openConnection dbPath
    use command = connection.CreateCommand()
    command.CommandText <-
        "SELECT event_id, kind, status, skill FROM activity_events ORDER BY ts, rowid;"
    use reader = command.ExecuteReader()

    let rec read rows =
        if reader.Read() then
            let row =
                { EventId = reader.GetString 0
                  Kind = reader.GetString 1
                  Status = reader.GetString 2
                  Skill = if reader.IsDBNull 3 then None else Some(reader.GetString 3) }

            read (row :: rows)
        else
            List.rev rows

    read []

/// The scheduler's exact instance for a session. These fixtures create one process per durable
/// session; exact-multiplicity behavior is covered separately.
/// so calling it after a LiveSnapshot barrier guarantees the mailbox's feed has been applied.
let private schedulerStatus (agent: MailboxProcessor<SchedulerState.StateMsg>) sid =
    let state = agent.PostAndReply SchedulerState.GetState

    state.SessionInstances
    |> Map.values
    |> Seq.tryFind (fun instance ->
        instance.SessionId = SessionId sid)

let private resumePathEvent path at =
    match path with
    | "status" -> TurnStarted
    | "heartbeat" -> Heartbeat
    | "usage" -> UsageInfo(120000, 200000)
    | "activity" -> TitleReported { Text = "Resumed session"; At = at }
    | "background" -> BackgroundAgentFinished("other-tool", at)
    | other -> invalidArg (nameof path) $"unknown resume path: {other}"

let private idleEvent kind =
    match kind with
    | "turn_ended" -> TurnEnded
    | "went_idle" -> WentIdle
    | other -> invalidArg (nameof kind) $"unknown idle event: {other}"

let private handlerResponse
    (handler: HttpHandler)
    (requestBody: string)
    =
    let services = ServiceCollection()
    services.AddGiraffe() |> ignore
    use provider = services.BuildServiceProvider()
    let context = DefaultHttpContext()
    let requestBytes = Encoding.UTF8.GetBytes requestBody
    use body = new MemoryStream(requestBytes)
    use response = new MemoryStream()
    context.RequestServices <- provider
    context.Request.ContentType <- "application/json"
    context.Request.ContentLength <- requestBytes.LongLength
    context.Request.Body <- body
    context.Response.Body <- response

    let next: HttpFunc =
        fun current -> Task.FromResult(Some current)

    handler next context
    |> _.GetAwaiter().GetResult()
    |> ignore

    response.Position <- 0L
    use reader = new StreamReader(response)
    context.Response.StatusCode, reader.ReadToEnd()


// ── DTO → domain parse ────────────────────────────────────────────────────────
[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type ParseReportTests() =

    [<Test>]
    member _.``a missing request body is rejected``() =
        let request = Unchecked.defaultof<SessionActivityRequest>
        Assert.That(parseErr request, Is.EqualTo "missing body")

    [<Test>]
    member _.``turn_started maps to TurnStarted``() =
        Assert.That((parseOk (baseReq "turn_started")).Event, Is.EqualTo TurnStarted)

    [<Test>]
    member _.``optional terminal origin maps without changing the folded event``() =
        let terminalSessionId = "0123456789ABCDEF0123456789ABCDEF"
        let withoutOrigin = parseOk (baseReq "turn_started")
        let withOrigin =
            parseOk
                { baseReq "turn_started" with
                    terminalSessionId = terminalSessionId }

        Assert.Multiple(fun () ->
            Assert.That(
                withOrigin.TerminalSessionId,
                Is.EqualTo(Some(TerminalSessionId(terminalSessionId.ToLowerInvariant())))
            )
            Assert.That(withoutOrigin.TerminalSessionId, Is.EqualTo None)
            Assert.That(withOrigin.Event, Is.EqualTo withoutOrigin.Event)
            Assert.That(
                fold emptyStatus withOrigin.Event,
                Is.EqualTo(fold emptyStatus withoutOrigin.Event)
            ))

    [<Test>]
    member _.``malformed terminal origin is rejected``() =
        let error =
            parseErr
                { baseReq "turn_started" with
                    terminalSessionId = "not-a-terminal-id" }

        Assert.That(error, Does.Contain "terminalSessionId")

    [<Test>]
    member _.``turn_ended maps to TurnEnded``() =
        Assert.That((parseOk (baseReq "turn_ended")).Event, Is.EqualTo TurnEnded)

    [<Test>]
    member _.``went_idle maps to WentIdle``() =
        Assert.That((parseOk (baseReq "went_idle")).Event, Is.EqualTo WentIdle)

    [<Test>]
    member _.``user_input_completed maps to UserInputCompleted``() =
        Assert.That(
            (parseOk (baseReq "user_input_completed")).Event,
            Is.EqualTo(UserInputCompleted(ts "2026-03-01T10:00:00Z")))

    [<TestCase("background_agent_started")>]
    [<TestCase("background_agent_finished")>]
    member _.``background-agent lifecycle requires and carries toolCallId``(kind: string) =
        let req = { baseReq kind with toolCallId = "tool-42" }
        let expected =
            match kind with
            | "background_agent_started" ->
                BackgroundAgentStarted("tool-42", ts "2026-03-01T10:00:00Z")
            | "background_agent_finished" ->
                BackgroundAgentFinished("tool-42", ts "2026-03-01T10:00:00Z")
            | other -> failwith $"unexpected test kind: {other}"

        Assert.That((parseOk req).Event, Is.EqualTo expected)

    [<TestCase("background_agent_started")>]
    [<TestCase("background_agent_finished")>]
    member _.``background-agent lifecycle without toolCallId is rejected``(kind: string) =
        Assert.That(parseErr (baseReq kind), Does.Contain "toolCallId")

    [<TestCase("background_agent_started")>]
    [<TestCase("background_agent_finished")>]
    member _.``background-agent lifecycle preserves a maximum-length toolCallId``(kind: string) =
        let toolCallId = $" {String('x', maxToolCallIdLength - 2)} "
        let event = (parseOk { baseReq kind with toolCallId = toolCallId }).Event

        let actual =
            match event with
            | BackgroundAgentStarted(id, _)
            | BackgroundAgentFinished(id, _) -> id
            | other -> failwith $"unexpected event: {other}"

        Assert.That(actual, Is.EqualTo toolCallId)

    [<TestCase("background_agent_started")>]
    [<TestCase("background_agent_finished")>]
    member _.``background-agent lifecycle rejects an overlong toolCallId``(kind: string) =
        let req =
            { baseReq kind with
                toolCallId = String('x', maxToolCallIdLength + 1) }

        Assert.That(parseErr req, Does.Contain $"{maxToolCallIdLength}")

    [<Test>]
    member _.``user_prompt with a message maps to UserPrompt carrying that message``() =
        let req = { baseReq "user_prompt" with message = msgDto "hello" "2026-03-01T10:00:00Z" }
        Assert.That((parseOk req).Event, Is.EqualTo(UserPrompt(msg "hello" "2026-03-01T10:00:00Z")))

    [<Test>]
    member _.``assistant_message with a message maps to AssistantMessage``() =
        let req = { baseReq "assistant_message" with message = msgDto "hi there" "2026-03-01T10:00:00Z" }
        Assert.That((parseOk req).Event, Is.EqualTo(AssistantMessage(msg "hi there" "2026-03-01T10:00:00Z")))

    [<Test>]
    member _.``intent_reported with a message maps to IntentReported carrying that message``() =
        let req = { baseReq "intent_reported" with message = msgDto "investigating the fold" "2026-03-01T10:00:00Z" }
        Assert.That((parseOk req).Event, Is.EqualTo(IntentReported(msg "investigating the fold" "2026-03-01T10:00:00Z")))

    [<Test>]
    member _.``title_reported with a message maps to TitleReported carrying that message``() =
        let req = { baseReq "title_reported" with message = msgDto "Investigate Work Item 261312" "2026-03-01T10:00:00Z" }
        Assert.That((parseOk req).Event, Is.EqualTo(TitleReported(msg "Investigate Work Item 261312" "2026-03-01T10:00:00Z")))

    [<Test>]
    member _.``title_bootstrap with a message maps to TitleBootstrap carrying that message``() =
        let req = { baseReq "title_bootstrap" with message = msgDto "Investigate Work Item 261312" "2026-03-01T10:00:00Z" }
        Assert.That((parseOk req).Event, Is.EqualTo(TitleBootstrap(msg "Investigate Work Item 261312" "2026-03-01T10:00:00Z")))

    [<Test>]
    member _.``intent_reported without a message is rejected (never regresses to blank)``() =
        Assert.That(parseErr (baseReq "intent_reported"), Does.Contain "message")

    [<Test>]
    member _.``title_bootstrap without a message is rejected``() =
        Assert.That(parseErr (baseReq "title_bootstrap"), Does.Contain "message")

    [<Test>]
    member _.``skill_invoked with a skillName maps to SkillInvoked``() =
        let req = { baseReq "skill_invoked" with skillName = "investigate" }
        Assert.That((parseOk req).Event, Is.EqualTo(SkillInvoked "investigate"))

    [<Test>]
    member _.``awaiting_user_input carries the question when a message is present``() =
        let req = { baseReq "awaiting_user_input" with message = msgDto "Which file?" "2026-03-01T10:00:00Z" }
        Assert.That(
            (parseOk req).Event,
            Is.EqualTo(
                AwaitingUserInput(
                    Some(msg "Which file?" "2026-03-01T10:00:00Z"),
                    ts "2026-03-01T10:00:00Z")))

    [<Test>]
    member _.``awaiting_user_input with no message maps to AwaitingUserInput None``() =
        Assert.That(
            (parseOk (baseReq "awaiting_user_input")).Event,
            Is.EqualTo(AwaitingUserInput(None, ts "2026-03-01T10:00:00Z")))

    [<Test>]
    member _.``awaiting_user_input with blank message text maps to AwaitingUserInput None``() =
        let req =
            { baseReq "awaiting_user_input" with
                message = msgDto "   " "2026-03-01T10:00:00Z" }

        Assert.That(
            (parseOk req).Event,
            Is.EqualTo(AwaitingUserInput(None, ts "2026-03-01T10:00:00Z")))

    [<Test>]
    member _.``an unknown kind is rejected (no catch-all)``() =
        Assert.That(parseErr (baseReq "session_resumed"), Does.Contain "unknown kind")

    [<Test>]
    member _.``user_prompt without a message is rejected``() =
        Assert.That(parseErr (baseReq "user_prompt"), Does.Contain "message")

    [<Test>]
    member _.``assistant_message with a blank message text is rejected``() =
        let req = { baseReq "assistant_message" with message = msgDto "   " "2026-03-01T10:00:00Z" }
        Assert.That(parseErr req, Does.Contain "message")

    [<Test>]
    member _.``skill_invoked without a skillName is rejected``() =
        Assert.That(parseErr (baseReq "skill_invoked"), Does.Contain "skillName")

    [<Test>]
    member _.``usage_info with tokens maps to UsageInfo``() =
        let req = { baseReq "usage_info" with currentTokens = 120000; tokenLimit = 200000 }
        Assert.That((parseOk req).Event, Is.EqualTo(UsageInfo(120000, 200000)))

    [<Test>]
    member _.``usage_info clamps a negative currentTokens to zero``() =
        let req = { baseReq "usage_info" with currentTokens = -5; tokenLimit = 200000 }
        Assert.That((parseOk req).Event, Is.EqualTo(UsageInfo(0, 200000)))

    [<Test>]
    member _.``usage_info with a non-positive tokenLimit is rejected``() =
        Assert.That(parseErr { baseReq "usage_info" with currentTokens = 100; tokenLimit = 0 }, Does.Contain "tokenLimit")

    [<Test>]
    member _.``an unknown provider is rejected``() =
        Assert.That(parseErr { baseReq "turn_started" with provider = "openai" }, Does.Contain "provider")

    [<Test>]
    member _.``a malformed occurredAt is rejected``() =
        Assert.That(parseErr { baseReq "turn_started" with occurredAt = "not-a-date" }, Does.Contain "timestamp")

    [<Test>]
    member _.``an occurredAt far in the future is clamped to now (so freshness can still decay)``() =
        let req = { baseReq "turn_started" with occurredAt = "2999-01-01T00:00:00Z" }
        Assert.That((parseOk req).OccurredAt, Is.EqualTo refNow)

    [<Test>]
    member _.``an occurredAt within the skew allowance is kept as-is``() =
        // refNow + 2 min, inside the 5-min skew window — minor client/server clock skew is tolerated.
        let within = "2026-03-01T10:07:00Z"
        let req = { baseReq "turn_started" with occurredAt = within }
        Assert.That((parseOk req).OccurredAt, Is.EqualTo(ts within))

    [<TestCase("intent_reported")>]
    [<TestCase("title_reported")>]
    [<TestCase("title_bootstrap")>]
    member _.``a future message timestamp is normalized to the clamped report timestamp``(kind: string) =
        let req =
            { baseReq kind with
                occurredAt = "2999-01-01T00:00:00Z"
                message = msgDto "future activity" "2999-01-01T00:00:00Z" }
        let report = parseOk req
        let messageAt =
            match kind, report.Event with
            | "intent_reported", IntentReported message
            | "title_reported", TitleReported message
            | "title_bootstrap", TitleBootstrap message -> message.At
            | _ -> failwith $"unexpected parsed event for {kind}: {report.Event}"

        Assert.Multiple(fun () ->
            Assert.That(report.OccurredAt, Is.EqualTo refNow)
            Assert.That(messageAt, Is.EqualTo report.OccurredAt))

    [<Test>]
    member _.``a future-skewed intent no longer permanently outranks a genuine later title in effectiveActivity``() =
        let poisoned =
            { baseReq "intent_reported" with
                occurredAt = "2999-01-01T00:00:00Z"
                message = msgDto "runaway clock intent" "2999-01-01T00:00:00Z" }
        let corrected =
            { baseReq "title_reported" with
                occurredAt = "2026-03-01T10:06:00Z"
                message = msgDto "Investigate Work Item 261312" "2026-03-01T10:06:00Z" }

        let status = foldMany emptyStatus [ (parseOk poisoned).Event; (parseOk corrected).Event ]

        Assert.That(
            effectiveActivity status,
            Is.EqualTo(Some(AgentActivity.SessionTitle("Investigate Work Item 261312", ts "2026-03-01T10:06:00Z"))))

    [<Test>]
    member _.``a blank sessionId is rejected``() =
        Assert.That(parseErr { baseReq "turn_started" with sessionId = "  " }, Does.Contain "sessionId")

    [<TestCase("session-123")>]
    [<TestCase("018F7E43-251D-7DD2-BB7D-8949D7A5688A")>]
    [<TestCase("copilot.session_42:resume")>]
    member _.``a supported resume sessionId is preserved``(sessionId: string) =
        let report =
            parseOk
                { baseReq "turn_started" with
                    sessionId = sessionId }

        Assert.That(report.SessionId, Is.EqualTo(SessionId sessionId))

    [<Test>]
    member _.``a sessionId at the bounded identifier limit is preserved``() =
        let sessionId = String('a', maxSessionIdLength)

        let report =
            parseOk
                { baseReq "turn_started" with
                    sessionId = sessionId }

        Assert.That(report.SessionId, Is.EqualTo(SessionId sessionId))

    [<Test>]
    member _.``an oversized sessionId is rejected``() =
        let sessionId = String('a', maxSessionIdLength + 1)

        Assert.That(
            parseErr
                { baseReq "turn_started" with
                    sessionId = sessionId },
            Does.Contain(string maxSessionIdLength)
        )

    [<TestCase("session/id")>]
    [<TestCase("session id")>]
    [<TestCase("session'id")>]
    [<TestCase("session;id")>]
    member _.``a sessionId outside the supported identifier alphabet is rejected``(sessionId: string) =
        Assert.That(
            parseErr
                { baseReq "turn_started" with
                    sessionId = sessionId },
            Does.Contain("[A-Za-z0-9._:-]")
        )

    [<TestCase(0x00)>]
    [<TestCase(0x03)>]
    [<TestCase(0x0A)>]
    [<TestCase(0x0D)>]
    [<TestCase(0x15)>]
    [<TestCase(0x1B)>]
    [<TestCase(0x85)>]
    member _.``a sessionId containing a terminal control character is rejected``(characterCode: int) =
        let sessionId = $"safe{string (char characterCode)}injected"

        Assert.That(
            parseErr
                { baseReq "turn_started" with
                    sessionId = sessionId },
            Does.Contain("[A-Za-z0-9._:-]")
        )

    [<Test>]
    member _.``a blank eventId is rejected``() =
        Assert.That(parseErr { baseReq "turn_started" with eventId = "" }, Does.Contain "eventId")

    [<Test>]
    member _.``a blank worktreePath is rejected``() =
        Assert.That(parseErr { baseReq "turn_started" with worktreePath = "" }, Does.Contain "worktreePath")

    [<Test>]
    member _.``the worktree path is normalized on the parsed report``() =
        let req = { baseReq "turn_started" with worktreePath = "C:\\wt\\a" }
        Assert.That((parseOk req).WorktreePath, Is.EqualTo(WorktreePath(PathUtils.normalizePath "C:\\wt\\a")))

    // --- Server-side max text length (defence-in-depth; independent of the client's 2000-char cap) ---

    [<Test>]
    member _.``a message text over the server cap is truncated (not trusted from the client)``() =
        let long = String('x', maxTextLength + 500)
        let req = { baseReq "user_prompt" with message = msgDto long "2026-03-01T10:00:00Z" }
        match (parseOk req).Event with
        | UserPrompt m -> Assert.That(m.Text.Length, Is.EqualTo maxTextLength)
        | other -> Assert.Fail $"expected UserPrompt, got {other}"

    [<Test>]
    member _.``a message text at exactly the server cap is kept intact``() =
        let atCap = String('x', maxTextLength)
        let req = { baseReq "assistant_message" with message = msgDto atCap "2026-03-01T10:00:00Z" }
        match (parseOk req).Event with
        | AssistantMessage m -> Assert.That(m.Text, Is.EqualTo atCap)
        | other -> Assert.Fail $"expected AssistantMessage, got {other}"

    [<Test>]
    member _.``an ask_user question over the server cap is truncated``() =
        let long = String('q', maxTextLength + 1)
        let req = { baseReq "awaiting_user_input" with message = msgDto long "2026-03-01T10:00:00Z" }
        match (parseOk req).Event with
        | AwaitingUserInput(Some m, _) -> Assert.That(m.Text.Length, Is.EqualTo maxTextLength)
        | other -> Assert.Fail $"expected AwaitingUserInput Some, got {other}"

    [<Test>]
    member _.``a skillName over the server cap is truncated``() =
        let long = String('s', maxTextLength + 42)
        let req = { baseReq "skill_invoked" with skillName = long }
        match (parseOk req).Event with
        | SkillInvoked name -> Assert.That(name.Length, Is.EqualTo maxTextLength)
        | other -> Assert.Fail $"expected SkillInvoked, got {other}"


// ── known-worktree guard ──────────────────────────────────────────────────────
[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type TryAcceptReportTests() =

    [<Test>]
    member _.``a valid report for a monitored worktree is accepted``() =
        withService "C:/wt/a" (fun (_, agent, _) ->
            match runAsync (tryAcceptReport agent (baseReq "turn_started")) with
            | Accepted report -> Assert.That(report.SessionId, Is.EqualTo(SessionId "s1"))
            | other -> Assert.Fail $"expected Accepted, got {other}")

    [<Test>]
    member _.``a valid report for an unmonitored worktree is a soft accept (nothing recorded)``() =
        withService "C:/wt/a" (fun (_, agent, _) ->
            match runAsync (tryAcceptReport agent { baseReq "turn_started" with worktreePath = "C:/wt/elsewhere" }) with
            | Unmonitored _ -> ()
            | other -> Assert.Fail $"expected Unmonitored, got {other}")

    [<Test>]
    member _.``a system reminder for a monitored worktree is ignored before ingestion``() =
        let request =
            { baseReq "user_prompt" with
                message = msgDto "<system_reminder>internal runtime guidance" "2026-03-01T10:00:00Z" }

        withService "C:/wt/a" (fun (_, agent, _) ->
            match runAsync (tryAcceptReport agent request) with
            | IgnoredSystemReminder -> ()
            | other -> Assert.Fail $"expected IgnoredSystemReminder, got {other}")

    [<Test>]
    member _.``an invalid body is rejected before the guard``() =
        withService "C:/wt/a" (fun (_, agent, _) ->
            match runAsync (tryAcceptReport agent (baseReq "bogus_kind")) with
            | Rejected reason -> Assert.That(reason, Does.Contain "unknown kind")
            | other -> Assert.Fail $"expected Rejected, got {other}")

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type HandlerTests() =

    [<Test>]
    member _.``valid JSON with an invalid worktree path is rejected without blaming JSON binding``() =
        let invalidPath =
            "C:\\wt\\" + string (char 0) + "invalid"

        let request =
            { baseReq "turn_started" with
                worktreePath = invalidPath }

        withService "C:/wt/a" (fun (service, _, _) ->
            let statusCode, body =
                request
                |> JsonSerializer.Serialize
                |> handlerResponse service.Handler

            Assert.Multiple(fun () ->
                Assert.That(statusCode, Is.EqualTo StatusCodes.Status400BadRequest)
                Assert.That(body, Does.Contain "invalid worktreePath")
                Assert.That(body, Does.Not.Contain "malformed JSON")))

    [<Test>]
    member _.``post-binding failure returns a retryable server error``() =
        let resolver =
            ProcessIdentityResolver.create (fun _ ->
                raise (InvalidOperationException "injected resolver failure"))

        withServiceSeededAndPathUsingResolver
            "C:/wt/a"
            ignore
            resolver
            (fun (service, _, _, _) ->
                let statusCode, body =
                    baseReq "turn_started"
                    |> JsonSerializer.Serialize
                    |> handlerResponse service.Handler

                use document = JsonDocument.Parse body
                let response = document.RootElement

                Assert.Multiple(fun () ->
                    Assert.That(
                        statusCode,
                        Is.EqualTo StatusCodes.Status500InternalServerError
                    )
                    Assert.That(
                        response.GetProperty("recorded").GetBoolean(),
                        Is.False
                    )
                    Assert.That(
                        response.GetProperty("monitored").GetBoolean(),
                        Is.True
                    )
                    Assert.That(
                        response.GetProperty("retryable").GetBoolean(),
                        Is.True
                    )
                    Assert.That(
                        response.GetProperty("reason").GetString(),
                        Is.EqualTo "session activity processing failed"
                    )))


// ── single-writer mailbox: fold → persist → feed ──────────────────────────────
[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type IngestTests() =

    [<Test>]
    member _.``a history report without presence is dropped``() =
        withServiceAndPath "C:/wt/a" (fun (svc, _, _, dbPath) ->
            svc.Submit(mkReport "s1" "C:/wt/a" "e1" "2026-03-01T10:00:00Z" TurnStarted)
            Assert.That(svc.LiveSnapshot(), Is.Empty)
            Assert.That(eventCount dbPath, Is.Zero))

    [<Test>]
    member _.``ingesting turn_started makes the session Working in the live map``() =
        withService "C:/wt/a" (fun (svc, _, _) ->
            present svc "s1" "C:/wt/a" (ts "2026-03-01T10:00:00Z") |> ignore
            svc.Submit(mkReport "s1" "C:/wt/a" "e1" "2026-03-01T10:00:00Z" TurnStarted)
            let live = svc.LiveSnapshot()
            Assert.That((live |> Map.find (SessionId "s1")).Status.Status, Is.EqualTo SessionLevelStatus.Working))

    [<Test>]
    member _.``an ingested status is fed to the scheduler``() =
        withService "C:/wt/a" (fun (svc, agent, _) ->
            present svc "s1" "C:/wt/a" (ts "2026-03-01T10:00:00Z") |> ignore
            svc.Submit(mkReport "s1" "C:/wt/a" "e1" "2026-03-01T10:00:00Z" TurnStarted)
            svc.LiveSnapshot() |> ignore // barrier: the mailbox has posted the exact instance by now
            match schedulerStatus agent "s1" with
            | Some stored -> Assert.That(stored.Status.Status, Is.EqualTo SessionLevelStatus.Working)
            | None -> Assert.Fail "scheduler never received the session status")

    [<Test>]
    member _.``a folded sequence surfaces the last user + assistant message and status``() =
        withService "C:/wt/a" (fun (svc, _, _) ->
            present svc "s1" "C:/wt/a" (ts "2026-03-01T10:00:00Z") |> ignore
            svc.Submit(mkReport "s1" "C:/wt/a" "e1" "2026-03-01T10:00:00Z" TurnStarted)
            svc.Submit(mkReport "s1" "C:/wt/a" "e2" "2026-03-01T10:00:01Z" (UserPrompt(msg "do it" "2026-03-01T10:00:01Z")))
            svc.Submit(mkReport "s1" "C:/wt/a" "e3" "2026-03-01T10:00:02Z" (AssistantMessage(msg "on it" "2026-03-01T10:00:02Z")))
            let s = (svc.LiveSnapshot() |> Map.find (SessionId "s1")).Status
            Assert.That(s.Status, Is.EqualTo SessionLevelStatus.Working)
            Assert.That(s.LastUserMessage, Is.EqualTo(Some(msg "do it" "2026-03-01T10:00:01Z")))
            Assert.That(s.LastAssistantMessage, Is.EqualTo(Some(msg "on it" "2026-03-01T10:00:02Z"))))

    [<Test>]
    member _.``ask_user state is correct when idle arrives before the earlier request``() =
        withService "C:/wt/a" (fun (svc, _, store) ->
            present svc "s1" "C:/wt/a" (ts "2026-03-01T10:00:01Z") |> ignore
            svc.Submit(mkReport "s1" "C:/wt/a" "e2" "2026-03-01T10:00:01Z" WentIdle)
            svc.Submit(
                mkReport
                    "s1"
                    "C:/wt/a"
                    "e1"
                    "2026-03-01T10:00:00Z"
                    (AwaitingUserInput(None, ts "2026-03-01T10:00:00Z")))

            let waiting = svc.LiveSnapshot() |> Map.find (SessionId "s1")
            Assert.Multiple(fun () ->
                Assert.That(effectiveStatus waiting.Status, Is.EqualTo SessionLevelStatus.WaitingForUser)
                Assert.That(waiting.UpdatedAt, Is.EqualTo(ts "2026-03-01T10:00:01Z")))

            svc.Submit(
                mkReport
                    "s1"
                    "C:/wt/a"
                    "e3"
                    "2026-03-01T10:00:02Z"
                    (UserInputCompleted(ts "2026-03-01T10:00:02Z")))

            let idle = svc.LiveSnapshot() |> Map.find (SessionId "s1")
            let persisted = store.StatusBySession(SessionId "s1") |> Option.get
            Assert.Multiple(fun () ->
                Assert.That(effectiveStatus idle.Status, Is.EqualTo SessionLevelStatus.Idle)
                Assert.That(effectiveStatus persisted.Status, Is.EqualTo SessionLevelStatus.Idle)
                Assert.That(idle.UpdatedAt, Is.EqualTo(ts "2026-03-01T10:00:02Z"))))

    [<Test>]
    member _.``a system reminder cannot release a pending ask_user wait``() =
        withService "C:/wt/a" (fun (svc, agent, _) ->
            present svc "s1" "C:/wt/a" (ts "2026-03-01T10:00:00Z") |> ignore
            svc.Submit(
                mkReport
                    "s1"
                    "C:/wt/a"
                    "e1"
                    "2026-03-01T10:00:00Z"
                    (AwaitingUserInput(None, ts "2026-03-01T10:00:00Z")))

            let reminder =
                { baseReq "user_prompt" with
                    occurredAt = "2026-03-01T10:00:01Z"
                    message = msgDto "<system_reminder>internal runtime guidance" "2026-03-01T10:00:01Z" }

            match runAsync (tryAcceptReport agent reminder) with
            | IgnoredSystemReminder -> ()
            | other -> Assert.Fail $"expected IgnoredSystemReminder, got {other}"

            svc.Submit(mkReport "s1" "C:/wt/a" "e3" "2026-03-01T10:00:02Z" WentIdle)
            let status = svc.LiveSnapshot() |> Map.find (SessionId "s1") |> _.Status |> effectiveStatus
            Assert.That(status, Is.EqualTo SessionLevelStatus.WaitingForUser))

    [<Test>]
    member _.``a first history report after presence creates a Working shell and publishes the authoritative row``() =
        withService "C:/wt/a" (fun (svc, agent, store) ->
            present svc "s1" "C:/wt/a" (ts "2026-03-01T10:00:00Z") |> ignore
            svc.Submit(
                mkReport
                    "s1"
                    "C:/wt/a"
                    "bg-start"
                    "2026-03-01T10:00:00Z"
                    (BackgroundAgentStarted("tool-1", ts "2026-03-01T10:00:00Z")))

            let live = svc.LiveSnapshot() |> Map.find (SessionId "s1")
            let persisted = store.StatusBySession(SessionId "s1") |> Option.get
            let latestSessionId =
                store.LatestSessionIdForWorktree(WorktreePath(PathUtils.normalizePath "C:/wt/a"))
            let retained = store.RetainedByWorktree() |> Map.find (PathUtils.normalizePath "C:/wt/a")

            Assert.Multiple(fun () ->
                Assert.That(live.Status.Status, Is.EqualTo SessionLevelStatus.Idle, "the parent base is an Idle shell")
                Assert.That(effectiveStatus live.Status, Is.EqualTo SessionLevelStatus.Working)
                Assert.That(
                    live.Status.BackgroundAgentClocks,
                    Is.EqualTo(
                        Map.ofList
                            [ "tool-1",
                              { StartedAt = Some(ts "2026-03-01T10:00:00Z")
                                FinishedAt = None } ]))
                Assert.That(live.UpdatedAt, Is.EqualTo(ts "2026-03-01T10:00:00Z"))
                Assert.That(live.LastSeen, Is.EqualTo(ts "2026-03-01T10:00:00Z"))
                Assert.That(persisted, Is.EqualTo live)
                Assert.That(latestSessionId, Is.EqualTo(Some(SessionId "s1")))
                Assert.That(retained.SessionId, Is.EqualTo persisted.SessionId)
                Assert.That(retained.UpdatedAt, Is.EqualTo persisted.UpdatedAt)
                Assert.That(retained.Status.BackgroundAgentClocks, Is.Empty)
                Assert.That(schedulerStatus agent "s1", Is.EqualTo(Some live))))

    [<Test>]
    member _.``a terminal first stays inactive and an older late start cannot resurrect it``() =
        withServiceAndPath "C:/wt/a" (fun (svc, agent, store, dbPath) ->
            present svc "s1" "C:/wt/a" (ts "2026-03-01T10:00:05Z") |> ignore
            svc.Submit(
                mkReport
                    "s1"
                    "C:/wt/a"
                    "bg-finish"
                    "2026-03-01T10:00:05Z"
                    (BackgroundAgentFinished("tool-1", ts "2026-03-01T10:00:05Z")))
            svc.Submit(
                mkReport
                    "s1"
                    "C:/wt/a"
                    "bg-start"
                    "2026-03-01T10:00:04Z"
                    (BackgroundAgentStarted("tool-1", ts "2026-03-01T10:00:04Z")))

            let live = svc.LiveSnapshot() |> Map.find (SessionId "s1")
            let persisted = store.StatusBySession(SessionId "s1") |> Option.get
            let events = persistedEvents dbPath

            Assert.Multiple(fun () ->
                Assert.That(effectiveStatus live.Status, Is.EqualTo SessionLevelStatus.Idle)
                Assert.That(
                    live.Status.BackgroundAgentClocks["tool-1"],
                    Is.EqualTo(
                        { StartedAt = Some(ts "2026-03-01T10:00:04Z")
                          FinishedAt = Some(ts "2026-03-01T10:00:05Z") }))
                Assert.That(live.UpdatedAt, Is.EqualTo(ts "2026-03-01T10:00:05Z"))
                Assert.That(live.LastSeen, Is.EqualTo(ts "2026-03-01T10:00:05Z"))
                Assert.That(persisted, Is.EqualTo live)
                Assert.That(schedulerStatus agent "s1", Is.EqualTo(Some live))
                Assert.That(events |> List.map _.Status, Is.EqualTo([ "working"; "idle" ]))))

    [<Test>]
    member _.``heartbeats expire completed clocks without removing active agents or accepting older starts``() =
        withServiceAndPath "C:/wt/a" (fun (svc, agent, _, dbPath) ->
            present svc "s1" "C:/wt/a" (ts "2026-03-01T10:00:00Z") |> ignore
            svc.Submit(
                mkReport
                    "s1"
                    "C:/wt/a"
                    "completed-start"
                    "2026-03-01T10:00:00Z"
                    (BackgroundAgentStarted("completed", ts "2026-03-01T10:00:00Z")))
            svc.Submit(
                mkReport
                    "s1"
                    "C:/wt/a"
                    "completed-finish"
                    "2026-03-01T10:00:10Z"
                    (BackgroundAgentFinished("completed", ts "2026-03-01T10:00:10Z")))
            svc.Submit(
                mkReport
                    "s1"
                    "C:/wt/a"
                    "active-start"
                    "2026-03-01T10:00:20Z"
                    (BackgroundAgentStarted("active", ts "2026-03-01T10:00:20Z")))
            svc.Submit(mkReport "s1" "C:/wt/a" "heartbeat-1" "2026-03-01T10:04:00Z" Heartbeat)
            svc.Submit(mkReport "s1" "C:/wt/a" "heartbeat-2" "2026-03-01T10:06:00Z" Heartbeat)
            svc.Submit(
                mkReport
                    "s1"
                    "C:/wt/a"
                    "expired-start"
                    "2026-03-01T10:00:05Z"
                    (BackgroundAgentStarted("completed", ts "2026-03-01T10:00:05Z")))

            let live = svc.LiveSnapshot() |> Map.find (SessionId "s1")
            let events = persistedEvents dbPath

            Assert.Multiple(fun () ->
                Assert.That(
                    live.Status.BackgroundAgentClocks,
                    Is.EqualTo(
                        Map.ofList
                            [ "active",
                              { StartedAt = Some(ts "2026-03-01T10:00:20Z")
                                FinishedAt = None } ])
                )
                Assert.That(effectiveStatus live.Status, Is.EqualTo SessionLevelStatus.Working)
                Assert.That(live.LastSeen, Is.EqualTo(ts "2026-03-01T10:06:00Z"))
                Assert.That(events |> List.exists (fun row -> row.EventId = "expired-start"), Is.False)
                Assert.That(schedulerStatus agent "s1", Is.EqualTo(Some live))))

    [<Test>]
    member _.``an older terminal after a newer start cannot finish the active agent``() =
        withServiceAndPath "C:/wt/a" (fun (svc, agent, store, dbPath) ->
            present svc "s1" "C:/wt/a" (ts "2026-03-01T10:00:06Z") |> ignore
            svc.Submit(
                mkReport
                    "s1"
                    "C:/wt/a"
                    "bg-start"
                    "2026-03-01T10:00:06Z"
                    (BackgroundAgentStarted("tool-1", ts "2026-03-01T10:00:06Z")))
            svc.Submit(
                mkReport
                    "s1"
                    "C:/wt/a"
                    "bg-finish"
                    "2026-03-01T10:00:05Z"
                    (BackgroundAgentFinished("tool-1", ts "2026-03-01T10:00:05Z")))

            let live = svc.LiveSnapshot() |> Map.find (SessionId "s1")
            let persisted = store.StatusBySession(SessionId "s1") |> Option.get
            let events = persistedEvents dbPath

            Assert.Multiple(fun () ->
                Assert.That(effectiveStatus live.Status, Is.EqualTo SessionLevelStatus.Working)
                Assert.That(
                    live.Status.BackgroundAgentClocks["tool-1"],
                    Is.EqualTo(
                        { StartedAt = Some(ts "2026-03-01T10:00:06Z")
                          FinishedAt = Some(ts "2026-03-01T10:00:05Z") }))
                Assert.That(live.UpdatedAt, Is.EqualTo(ts "2026-03-01T10:00:06Z"))
                Assert.That(live.LastSeen, Is.EqualTo(ts "2026-03-01T10:00:06Z"))
                Assert.That(
                    persisted.Status.BackgroundAgentClocks,
                    Is.EqualTo live.Status.BackgroundAgentClocks
                )
                Assert.That(schedulerStatus agent "s1", Is.EqualTo(Some live))
                Assert.That(events |> List.map _.Status, Is.EqualTo([ "idle"; "working" ]))))

    [<Test>]
    member _.``a delayed finish within retention records event-time history without regressing newer root work``() =
        withServiceAndPath "C:/wt/a" (fun (svc, agent, store, dbPath) ->
            present svc "s1" "C:/wt/a" (ts "2026-03-01T10:55:00Z") |> ignore
            svc.Submit(
                mkReport
                    "s1"
                    "C:/wt/a"
                    "bg-start"
                    "2026-03-01T10:55:00Z"
                    (BackgroundAgentStarted("tool-1", ts "2026-03-01T10:55:00Z")))
            svc.Submit(
                mkReport
                    "s1"
                    "C:/wt/a"
                    "heartbeat"
                    "2026-03-01T10:59:00Z"
                    Heartbeat)
            svc.Submit(mkReport "s1" "C:/wt/a" "root-start" "2026-03-01T11:00:00Z" TurnStarted)
            svc.Submit(mkReport "s1" "C:/wt/a" "root-skill" "2026-03-01T11:00:01Z" (SkillInvoked "review"))
            svc.Submit(
                mkReport
                    "s1"
                    "C:/wt/a"
                    "bg-finish"
                    "2026-03-01T10:56:00Z"
                    (BackgroundAgentFinished("tool-1", ts "2026-03-01T10:56:00Z")))

            let live = svc.LiveSnapshot() |> Map.find (SessionId "s1")
            let persisted = store.StatusBySession(SessionId "s1") |> Option.get
            let finishRow =
                persistedEvents dbPath
                |> List.find (fun row -> row.EventId = "bg-finish")

            Assert.Multiple(fun () ->
                Assert.That(finishRow.Status, Is.EqualTo "idle")
                Assert.That(finishRow.Skill, Is.EqualTo(None))
                Assert.That(live.Status.Status, Is.EqualTo SessionLevelStatus.Working)
                Assert.That(live.Status.Skill, Is.EqualTo(Some "review"))
                Assert.That(
                    live.Status.BackgroundAgentClocks["tool-1"],
                    Is.EqualTo(
                        { StartedAt = Some(ts "2026-03-01T10:55:00Z")
                          FinishedAt = Some(ts "2026-03-01T10:56:00Z") }))
                Assert.That(live.UpdatedAt, Is.EqualTo(ts "2026-03-01T11:00:01Z"))
                Assert.That(live.LastSeen, Is.EqualTo(ts "2026-03-01T10:59:00Z"))
                Assert.That(persisted, Is.EqualTo live)
                Assert.That(schedulerStatus agent "s1", Is.EqualTo(Some live))))

    [<Test>]
    member _.``a duplicate background event id changes neither lifecycle nor history``() =
        withServiceAndPath "C:/wt/a" (fun (svc, _, _, dbPath) ->
            present svc "s1" "C:/wt/a" (ts "2026-03-01T10:00:00Z") |> ignore
            svc.Submit(
                mkReport
                    "s1"
                    "C:/wt/a"
                    "same-event"
                    "2026-03-01T10:00:00Z"
                    (BackgroundAgentStarted("tool-1", ts "2026-03-01T10:00:00Z")))
            svc.Submit(
                mkReport
                    "s1"
                    "C:/wt/a"
                    "same-event"
                    "2026-03-01T10:10:00Z"
                    (BackgroundAgentFinished("tool-1", ts "2026-03-01T10:10:00Z")))

            let live = svc.LiveSnapshot() |> Map.find (SessionId "s1")
            let events = persistedEvents dbPath

            Assert.Multiple(fun () ->
                Assert.That(effectiveStatus live.Status, Is.EqualTo SessionLevelStatus.Working)
                Assert.That(
                    live.Status.BackgroundAgentClocks,
                    Is.EqualTo(
                        Map.ofList
                            [ "tool-1",
                              { StartedAt = Some(ts "2026-03-01T10:00:00Z")
                                FinishedAt = None } ]))
                Assert.That(live.UpdatedAt, Is.EqualTo(ts "2026-03-01T10:00:00Z"))
                Assert.That(live.LastSeen, Is.EqualTo(ts "2026-03-01T10:00:00Z"))
                Assert.That(events |> List.map _.Kind, Is.EqualTo([ "background_agent_started" ]))))

    [<TestCase("status", "turn_ended")>]
    [<TestCase("status", "went_idle")>]
    [<TestCase("heartbeat", "turn_ended")>]
    [<TestCase("heartbeat", "went_idle")>]
    [<TestCase("usage", "turn_ended")>]
    [<TestCase("usage", "went_idle")>]
    [<TestCase("activity", "turn_ended")>]
    [<TestCase("activity", "went_idle")>]
    [<TestCase("background", "turn_ended")>]
    [<TestCase("background", "went_idle")>]
    member _.``the first history report after renewed presence closes old agents and later idle settles``(
        resumePath: string,
        terminalKind: string
    ) =
        let now = DateTimeOffset.UtcNow
        let worktree = Path.Combine(Path.GetTempPath(), $"treemon-stale-resume-{Guid.NewGuid()}")
        let oldStart = now - stalenessTimeout - TimeSpan.FromMinutes 1.0

        withService worktree (fun (svc, agent, store) ->
            present svc "s1" worktree oldStart |> ignore
            svc.Submit(
                mkReport
                    "s1"
                    worktree
                    "old-start"
                    (oldStart.ToString("O"))
                    (BackgroundAgentStarted("crashed-tool", oldStart)))
            svc.LiveSnapshot() |> ignore
            present svc "s1" worktree now |> ignore
            svc.Submit(mkReport "s1" worktree "resume" (now.ToString("O")) (resumePathEvent resumePath now))
            let resumed = svc.LiveSnapshot() |> Map.find (SessionId "s1")
            let durableAfterResume = store.StatusBySession(SessionId "s1") |> Option.get

            Assert.Multiple(fun () ->
                Assert.That(
                    resumed.Status.BackgroundAgentClocks
                    |> Map.containsKey "crashed-tool",
                    Is.False,
                    "the live fold drops the crashed agent"
                )
                Assert.That(
                    durableAfterResume.Status.BackgroundAgentClocks,
                    Is.EqualTo resumed.Status.BackgroundAgentClocks
                )
                Assert.That(resumed.LastSeen, Is.EqualTo now, "the resume report refreshes liveness only after cleanup")
                Assert.That(schedulerStatus agent "s1", Is.EqualTo(Some resumed)))

            svc.Submit(mkReport "s1" worktree "new-turn" (now.AddSeconds(1.0).ToString("O")) TurnStarted)
            svc.Submit(
                mkReport
                    "s1"
                    worktree
                    "settle"
                    (now.AddSeconds(2.0).ToString("O"))
                    (idleEvent terminalKind))

            let settled = svc.LiveSnapshot() |> Map.find (SessionId "s1")
            let durableSettled = store.StatusBySession(SessionId "s1") |> Option.get

            Assert.Multiple(fun () ->
                Assert.That(effectiveStatus settled.Status, Is.EqualTo SessionLevelStatus.Idle)
                Assert.That(effectiveStatus durableSettled.Status, Is.EqualTo SessionLevelStatus.Idle)
                Assert.That(schedulerStatus agent "s1", Is.EqualTo(Some settled))))

    [<Test>]
    member _.``fresh reports preserve an active background agent``() =
        let now = DateTimeOffset.UtcNow
        let worktree = Path.Combine(Path.GetTempPath(), $"treemon-fresh-background-{Guid.NewGuid()}")
        let startedAt = now.AddSeconds(-30.0)

        withService worktree (fun (svc, _, store) ->
            present svc "s1" worktree startedAt |> ignore
            svc.Submit(
                mkReport
                    "s1"
                    worktree
                    "start"
                    (startedAt.ToString("O"))
                    (BackgroundAgentStarted("tool-1", startedAt)))
            svc.Submit(mkReport "s1" worktree "idle" (now.ToString("O")) TurnEnded)
            let live = svc.LiveSnapshot() |> Map.find (SessionId "s1")
            let durable = store.StatusBySession(SessionId "s1") |> Option.get

            Assert.Multiple(fun () ->
                Assert.That(
                    live.Status.BackgroundAgentClocks,
                    Is.EqualTo(
                        Map.ofList
                            [ "tool-1",
                              { StartedAt = Some startedAt
                                FinishedAt = None } ])
                )
                Assert.That(
                    durable.Status.BackgroundAgentClocks,
                    Is.EqualTo live.Status.BackgroundAgentClocks
                )
                Assert.That(effectiveStatus live.Status, Is.EqualTo SessionLevelStatus.Working)))

    [<Test>]
    member _.``a stale resume may start new background work with the same tool id``() =
        let now = DateTimeOffset.UtcNow
        let worktree = Path.Combine(Path.GetTempPath(), $"treemon-stale-restart-{Guid.NewGuid()}")
        let oldStart = now - stalenessTimeout - TimeSpan.FromMinutes 1.0

        withService worktree (fun (svc, _, store) ->
            present svc "s1" worktree oldStart |> ignore
            svc.Submit(
                mkReport
                    "s1"
                    worktree
                    "old-start"
                    (oldStart.ToString("O"))
                    (BackgroundAgentStarted("tool-1", oldStart)))
            svc.Submit(
                mkReport
                    "s1"
                    worktree
                    "resumed-start"
                    (now.ToString("O"))
                    (BackgroundAgentStarted("tool-1", now)))

            let resumed = svc.LiveSnapshot() |> Map.find (SessionId "s1")
            Assert.That(
                resumed.Status.BackgroundAgentClocks,
                Is.EqualTo(
                    Map.ofList
                        [ "tool-1",
                          { StartedAt = Some now
                            FinishedAt = None } ])
            )

            svc.Submit(
                mkReport
                    "s1"
                    worktree
                    "resumed-finish"
                    (now.AddSeconds(1.0).ToString("O"))
                    (BackgroundAgentFinished("tool-1", now.AddSeconds(1.0))))
            svc.Submit(mkReport "s1" worktree "idle" (now.AddSeconds(2.0).ToString("O")) WentIdle)

            let settled = svc.LiveSnapshot() |> Map.find (SessionId "s1")
            let durable = store.StatusBySession(SessionId "s1") |> Option.get
            Assert.Multiple(fun () ->
                Assert.That(effectiveStatus settled.Status, Is.EqualTo SessionLevelStatus.Idle)
                Assert.That(effectiveStatus durable.Status, Is.EqualTo SessionLevelStatus.Idle)
                Assert.That(
                    durable.Status.BackgroundAgentClocks,
                    Is.EqualTo settled.Status.BackgroundAgentClocks
                )))

    [<Test>]
    member _.``background lifecycle preserves parent activity and footer fields``() =
        let parent =
            { ProcessIdentity = syntheticProcessIdentityForSessionId "s1"
              SessionId = SessionId "s1"
              TerminalSessionId = None
              WorktreePath = WorktreePath(PathUtils.normalizePath "C:/wt/a")
              Provider = CopilotCli
              Status =
                { Status = SessionLevelStatus.Idle
                  Skill = Some "review"
                  Intent = Some(msg "reviewing the implementation" "2026-03-01T09:55:00Z")
                  Title = Some(msg "Review lifecycle integration" "2026-03-01T09:56:00Z")
                  LastUserMessage = Some(msg "review this" "2026-03-01T09:57:00Z")
                  LastAssistantMessage = Some(msg "on it" "2026-03-01T09:58:00Z")
                  ContextUsage = Some { CurrentTokens = 50000; TokenLimit = 200000 }
                  AwaitingUserSince = None
                  UserInputCompletedAt = None
                  BackgroundAgentClocks = Map.empty }
              UpdatedAt = ts "2026-03-01T09:59:00Z"
              LifecycleAt = Some(ts "2026-03-01T09:59:00Z")
              LastSeen = ts "2026-03-01T09:59:00Z"
              ContextUsageAt = Some(ts "2026-03-01T09:58:30Z")
              ClosedAt = None }

        withServiceSeeded
            "C:/wt/a"
            (fun store -> store.UpsertContextUsage parent |> ignore)
            (fun (svc, agent, store) ->
                svc.Submit(
                    mkReport
                        "s1"
                        "C:/wt/a"
                        "bg-start"
                        "2026-03-01T10:00:00Z"
                        (BackgroundAgentStarted("tool-1", ts "2026-03-01T10:00:00Z")))

                let live = svc.LiveSnapshot() |> Map.find (SessionId "s1")
                let persisted = store.StatusBySession(SessionId "s1") |> Option.get

                Assert.Multiple(fun () ->
                    Assert.That(live.Status.Skill, Is.EqualTo parent.Status.Skill)
                    Assert.That(live.Status.Intent, Is.EqualTo parent.Status.Intent)
                    Assert.That(live.Status.Title, Is.EqualTo parent.Status.Title)
                    Assert.That(live.Status.LastUserMessage, Is.EqualTo parent.Status.LastUserMessage)
                    Assert.That(live.Status.LastAssistantMessage, Is.EqualTo parent.Status.LastAssistantMessage)
                    Assert.That(live.Status.ContextUsage, Is.EqualTo parent.Status.ContextUsage)
                    Assert.That(live.ContextUsageAt, Is.EqualTo parent.ContextUsageAt)
                    Assert.That(effectiveStatus live.Status, Is.EqualTo SessionLevelStatus.Working)
                    Assert.That(live.UpdatedAt, Is.EqualTo(ts "2026-03-01T10:00:00Z"))
                    Assert.That(live.LastSeen, Is.EqualTo parent.LastSeen)
                    Assert.That(persisted, Is.EqualTo live)
                    Assert.That(schedulerStatus agent "s1", Is.EqualTo(Some live))))

    [<Test>]
    member _.``background lifecycle history records the resulting effective status``() =
        withServiceAndPath "C:/wt/a" (fun (svc, _, _, dbPath) ->
            present svc "s1" "C:/wt/a" (ts "2026-03-01T10:00:00Z") |> ignore
            svc.Submit(
                mkReport
                    "s1"
                    "C:/wt/a"
                    "bg-start"
                    "2026-03-01T10:00:00Z"
                    (BackgroundAgentStarted("tool-1", ts "2026-03-01T10:00:00Z")))
            svc.Submit(
                mkReport
                    "s1"
                    "C:/wt/a"
                    "bg-finish"
                    "2026-03-01T10:00:05Z"
                    (BackgroundAgentFinished("tool-1", ts "2026-03-01T10:00:05Z")))
            svc.LiveSnapshot() |> ignore

            let history =
                persistedEvents dbPath
                |> List.map (fun row -> row.Kind, row.Status)

            Assert.That(
                history,
                Is.EqualTo(
                    [ "background_agent_started", "working"
                      "background_agent_finished", "idle" ])))

    [<Test>]
    member _.``ingested events are persisted to the durable mirror``() =
        withServiceAndPath "C:/wt/a" (fun (svc, _, store, dbPath) ->
            present svc "s1" "C:/wt/a" (ts "2026-03-01T10:00:00Z") |> ignore
            svc.Submit(mkReport "s1" "C:/wt/a" "e1" "2026-03-01T10:00:00Z" TurnStarted)
            svc.LiveSnapshot() |> ignore
            let loaded = store.LoadLiveStatuses(ts "2026-03-01T10:05:00Z")
            Assert.That(loaded |> List.exists (fun s -> s.SessionId = SessionId "s1"), Is.True)
            Assert.That(eventCount dbPath, Is.EqualTo 1))

    [<Test>]
    member _.``a duplicate event_id is a no-op: no second event row, status unchanged``() =
        withServiceAndPath "C:/wt/a" (fun (svc, _, _, dbPath) ->
            present svc "s1" "C:/wt/a" (ts "2026-03-01T10:00:00Z") |> ignore
            svc.Submit(mkReport "s1" "C:/wt/a" "e1" "2026-03-01T10:00:00Z" TurnStarted)
            svc.Submit(mkReport "s1" "C:/wt/a" "e2" "2026-03-01T10:00:05Z" WentIdle)
            svc.LiveSnapshot() |> ignore
            // Replay the first event verbatim.
            svc.Submit(mkReport "s1" "C:/wt/a" "e1" "2026-03-01T10:00:00Z" TurnStarted)
            let live = svc.LiveSnapshot()
            Assert.That((live |> Map.find (SessionId "s1")).Status.Status, Is.EqualTo SessionLevelStatus.Idle, "replay must not resurrect Working")
            Assert.That(eventCount dbPath, Is.EqualTo 2, "the duplicate event_id must be deduped"))

    [<Test>]
    member _.``an out-of-order event is retained for idempotency but does not regress live state``() =
        withServiceAndPath "C:/wt/a" (fun (svc, _, store, dbPath) ->
            present svc "s1" "C:/wt/a" (ts "2026-03-01T10:00:05Z") |> ignore
            svc.Submit(mkReport "s1" "C:/wt/a" "e2" "2026-03-01T10:00:05Z" TurnStarted)
            svc.LiveSnapshot() |> ignore
            // An older, distinct event arrives late.
            svc.Submit(mkReport "s1" "C:/wt/a" "e1" "2026-03-01T10:00:00Z" (AssistantMessage(msg "stale" "2026-03-01T10:00:00Z")))
            let s = (svc.LiveSnapshot() |> Map.find (SessionId "s1")).Status
            Assert.That(s.Status, Is.EqualTo SessionLevelStatus.Working)
            Assert.That(s.LastAssistantMessage, Is.EqualTo None, "the stale message must not overwrite live state")
            Assert.That(eventCount dbPath, Is.EqualTo 2)
            let stored = store.LoadLiveStatuses(ts "2026-03-01T10:05:00Z") |> List.find (fun s -> s.SessionId = SessionId "s1")
            Assert.That(stored.UpdatedAt, Is.EqualTo(ts "2026-03-01T10:00:05Z")))

    [<Test>]
    member _.``title bootstrap persists without an activity event and cannot block an earlier lifecycle event``() =
        withServiceAndPath "C:/wt/a" (fun (svc, _, store, dbPath) ->
            let title = msg "Investigate Intent Title Runtime" "2026-03-01T10:00:05Z"
            present svc "s1" "C:/wt/a" title.At |> ignore
            svc.Submit(mkReport "s1" "C:/wt/a" "tb1" "2026-03-01T10:00:05Z" (TitleBootstrap title))

            let hydrated = svc.LiveSnapshot() |> Map.find (SessionId "s1")
            Assert.That(hydrated.Status.Title, Is.EqualTo(Some title))
            Assert.That(hydrated.UpdatedAt, Is.EqualTo(DateTimeOffset.MinValue), "bootstrap must not advance the lifecycle clock")
            Assert.That(hydrated.LastSeen, Is.EqualTo(ts "2026-03-01T10:00:05Z"), "a bootstrap-only session is retained durably")
            Assert.That(eventCount dbPath, Is.Zero, "bootstrap is not an activity event")
            let durable = store.LoadLiveStatuses(ts "2026-03-01T09:00:00Z") |> List.find (fun s -> s.SessionId = SessionId "s1")
            Assert.That(durable.Status.Title, Is.EqualTo(Some title), "bootstrap title is persisted on the exact instance")

            // A replayed lifecycle event has an older SDK timestamp but must still apply after the
            // newer join-time hydration report.
            svc.Submit(mkReport "s1" "C:/wt/a" "e1" "2026-03-01T10:00:03Z" TurnStarted)
            let replayed = svc.LiveSnapshot() |> Map.find (SessionId "s1")
            Assert.That(replayed.Status.Status, Is.EqualTo SessionLevelStatus.Working)
            Assert.That(replayed.Status.Title, Is.EqualTo(Some title))
            Assert.That(replayed.UpdatedAt, Is.EqualTo(ts "2026-03-01T10:00:03Z"))
            Assert.That(replayed.LastSeen, Is.EqualTo(ts "2026-03-01T10:00:05Z"), "lifecycle replay must not regress join liveness")
            Assert.That(eventCount dbPath, Is.EqualTo 1))

    [<Test>]
    member _.``title bootstrap revives a retained durable session without losing footer state``() =
        let retained =
            { ProcessIdentity = syntheticProcessIdentityForSessionId "s1"
              SessionId = SessionId "s1"
              TerminalSessionId = None
              WorktreePath = WorktreePath(PathUtils.normalizePath "C:/wt/a")
              Provider = CopilotCli
              Status =
                { Status = SessionLevelStatus.Working
                  Skill = Some "review"
                  Intent = Some(msg "reviewing the fix" "2026-03-01T07:58:00Z")
                  Title = Some(msg "Old title" "2026-03-01T07:59:00Z")
                  LastUserMessage = Some(msg "resume this" "2026-03-01T07:58:30Z")
                  LastAssistantMessage = Some(msg "working on it" "2026-03-01T07:59:30Z")
                  ContextUsage = None
                  AwaitingUserSince = None
                  UserInputCompletedAt = None
                  BackgroundAgentClocks = Map.empty }
              UpdatedAt = ts "2026-03-01T08:00:00Z"
              LifecycleAt = Some(ts "2026-03-01T08:00:00Z")
              LastSeen = ts "2026-03-01T08:00:00Z"
              ContextUsageAt = None
              ClosedAt = None }

        withServiceSeededAndPath
            "C:/wt/a"
            (fun store -> store.UpsertStatus retained)
            (fun (svc, _, store, dbPath) ->
                let title = msg "Current metadata title" "2026-03-01T10:30:00Z"
                present
                    svc
                    "s1"
                    "C:/wt/a"
                    (ts "2026-03-01T10:30:00Z")
                |> ignore
                svc.Submit(mkReport "s1" "C:/wt/a" "tb1" "2026-03-01T10:30:00Z" (TitleBootstrap title))

                let hydrated = svc.LiveSnapshot() |> Map.find (SessionId "s1")
                Assert.Multiple(fun () ->
                    Assert.That(hydrated.Status.Status, Is.EqualTo SessionLevelStatus.Working)
                    Assert.That(hydrated.Status.Skill, Is.EqualTo(Some "review"))
                    Assert.That(hydrated.Status.Intent, Is.EqualTo retained.Status.Intent)
                    Assert.That(hydrated.Status.Title, Is.EqualTo(Some title))
                    Assert.That(hydrated.Status.LastUserMessage, Is.EqualTo retained.Status.LastUserMessage)
                    Assert.That(hydrated.Status.LastAssistantMessage, Is.EqualTo retained.Status.LastAssistantMessage)
                    Assert.That(hydrated.Status.BackgroundAgentClocks, Is.Empty)
                    Assert.That(hydrated.UpdatedAt, Is.EqualTo retained.UpdatedAt)
                    Assert.That(hydrated.LastSeen, Is.EqualTo(ts "2026-03-01T10:30:00Z")))

                let durable = store.StatusBySession(SessionId "s1") |> Option.get
                Assert.That(durable, Is.EqualTo hydrated, "mailbox and durable store must use the same hydrated row")
                Assert.That(eventCount dbPath, Is.Zero)

                svc.Submit(mkReport "s1" "C:/wt/a" "idle" "2026-03-01T10:30:01Z" WentIdle)
                let settled = svc.LiveSnapshot() |> Map.find (SessionId "s1")
                Assert.That(effectiveStatus settled.Status, Is.EqualTo SessionLevelStatus.Idle))

    [<Test>]
    member _.``an older title bootstrap cannot overwrite a newer live title``() =
        withServiceAndPath "C:/wt/a" (fun (svc, _, _, dbPath) ->
            let liveTitle = msg "New live title" "2026-03-01T10:00:10Z"
            let staleSnapshot = msg "Old snapshot" "2026-03-01T10:00:05Z"
            present svc "s1" "C:/wt/a" liveTitle.At |> ignore
            svc.Submit(mkReport "s1" "C:/wt/a" "e1" "2026-03-01T10:00:10Z" (TitleReported liveTitle))
            svc.Submit(mkReport "s1" "C:/wt/a" "tb1" "2026-03-01T10:00:05Z" (TitleBootstrap staleSnapshot))

            let s = svc.LiveSnapshot() |> Map.find (SessionId "s1")
            Assert.That(s.Status.Title, Is.EqualTo(Some liveTitle))
            Assert.That(s.UpdatedAt, Is.EqualTo liveTitle.At, "title activity advances only the activity clock")
            Assert.That(eventCount dbPath, Is.EqualTo 1))

    [<Test>]
    member _.``a newer intent arriving first does not block an older lifecycle transition``() =
        withServiceAndPath "C:/wt/a" (fun (svc, _, store, dbPath) ->
            let intent = msg "Implementing the fix" "2026-03-01T10:00:06Z"
            present svc "s1" "C:/wt/a" intent.At |> ignore
            svc.Submit(mkReport "s1" "C:/wt/a" "i1" "2026-03-01T10:00:06Z" (IntentReported intent))
            svc.Submit(mkReport "s1" "C:/wt/a" "e1" "2026-03-01T10:00:05Z" TurnStarted)

            let live = svc.LiveSnapshot() |> Map.find (SessionId "s1")
            Assert.Multiple(fun () ->
                Assert.That(live.Status.Status, Is.EqualTo SessionLevelStatus.Working)
                Assert.That(live.Status.Intent, Is.EqualTo(Some intent))
                Assert.That(live.UpdatedAt, Is.EqualTo(ts "2026-03-01T10:00:06Z"), "intent advances activity without blocking the older lifecycle event")
                Assert.That(live.LastSeen, Is.EqualTo(ts "2026-03-01T10:00:06Z"), "the newer report still advances openness"))
            Assert.That(store.StatusBySession(SessionId "s1"), Is.EqualTo(Some live))
            Assert.That(eventCount dbPath, Is.EqualTo 2))

    [<Test>]
    member _.``a title arriving after a newer lifecycle event still updates the activity field``() =
        withServiceAndPath "C:/wt/a" (fun (svc, _, store, dbPath) ->
            let oldTitle = msg "Initial title" "2026-03-01T10:00:04Z"
            let newTitle = msg "Updated title" "2026-03-01T10:00:05Z"
            present svc "s1" "C:/wt/a" oldTitle.At |> ignore
            svc.Submit(mkReport "s1" "C:/wt/a" "t1" "2026-03-01T10:00:04Z" (TitleReported oldTitle))
            svc.Submit(mkReport "s1" "C:/wt/a" "e1" "2026-03-01T10:00:06Z" TurnStarted)
            svc.Submit(mkReport "s1" "C:/wt/a" "t2" "2026-03-01T10:00:05Z" (TitleReported newTitle))

            let live = svc.LiveSnapshot() |> Map.find (SessionId "s1")
            Assert.Multiple(fun () ->
                Assert.That(live.Status.Status, Is.EqualTo SessionLevelStatus.Working)
                Assert.That(live.Status.Title, Is.EqualTo(Some newTitle))
                Assert.That(live.UpdatedAt, Is.EqualTo(ts "2026-03-01T10:00:06Z"), "title must preserve the lifecycle clock")
                Assert.That(live.LastSeen, Is.EqualTo(ts "2026-03-01T10:00:04Z"), "activity and lifecycle reports do not refresh presence"))
            Assert.That(store.StatusBySession(SessionId "s1"), Is.EqualTo(Some live))
            Assert.That(eventCount dbPath, Is.EqualTo 3))

    [<Test>]
    member _.``a heartbeat bumps last_seen for openness without appending, moving updated_at, or changing status``() =
        withServiceAndPath "C:/wt/a" (fun (svc, _, store, dbPath) ->
            let terminalSessionId =
                TerminalSessionId "dddddddddddddddddddddddddddddddd"
            present svc "s1" "C:/wt/a" (ts "2026-03-01T10:00:00Z") |> ignore
            svc.Submit(mkReport "s1" "C:/wt/a" "e1" "2026-03-01T10:00:00Z" (AssistantMessage(msg "hi" "2026-03-01T10:00:00Z")))
            svc.LiveSnapshot() |> ignore
            // A later liveness heartbeat: newer timestamp, but pure openness — not a status event.
            svc.Submit
                { mkReport "s1" "C:/wt/a" "hb1" "2026-03-01T10:01:00Z" Heartbeat with
                    TerminalSessionId = Some terminalSessionId }
            let s = svc.LiveSnapshot() |> Map.find (SessionId "s1")
            Assert.That(s.LastSeen, Is.EqualTo(ts "2026-03-01T10:01:00Z"), "heartbeat advances last_seen")
            Assert.That(s.UpdatedAt, Is.EqualTo(ts "2026-03-01T10:00:00Z"), "heartbeat must not move the last-write-wins clock")
            Assert.That(s.Status.Status, Is.EqualTo SessionLevelStatus.Working, "heartbeat preserves status")
            Assert.That(s.Status.LastAssistantMessage, Is.EqualTo(Some(msg "hi" "2026-03-01T10:00:00Z")), "heartbeat preserves content")
            Assert.That(s.TerminalSessionId, Is.EqualTo(Some terminalSessionId), "heartbeat carries terminal attribution")
            Assert.That(eventCount dbPath, Is.EqualTo 1, "a heartbeat must not append to activity_events")
            // The durable row's last_seen was bumped, its updated_at left intact.
            let stored = store.LoadLiveStatuses(ts "2026-03-01T10:05:00Z") |> List.find (fun r -> r.SessionId = SessionId "s1")
            Assert.That(stored.LastSeen, Is.EqualTo(ts "2026-03-01T10:01:00Z"))
            Assert.That(stored.UpdatedAt, Is.EqualTo(ts "2026-03-01T10:00:00Z"))
            Assert.That(stored.TerminalSessionId, Is.EqualTo(Some terminalSessionId)))

    [<Test>]
    member _.``omitted origins preserve initial terminal attribution across every report path``() =
        withService "C:/wt/a" (fun (svc, _, store) ->
            let terminalSessionId =
                TerminalSessionId "eeeeeeeeeeeeeeeeeeeeeeeeeeeeeeee"
            present svc "s1" "C:/wt/a" (ts "2026-03-01T10:00:00Z") |> ignore
            let withOrigin (report: SessionActivityReport) =
                { report with TerminalSessionId = Some terminalSessionId }
            let submitAndAssertOrigin report =
                svc.Submit report
                let live = svc.LiveSnapshot() |> Map.find (SessionId "s1")
                let persisted = store.StatusBySession(SessionId "s1") |> Option.get

                Assert.Multiple(fun () ->
                    Assert.That(live.TerminalSessionId, Is.EqualTo(Some terminalSessionId))
                    Assert.That(persisted.TerminalSessionId, Is.EqualTo(Some terminalSessionId)))

            mkReport "s1" "C:/wt/a" "started" "2026-03-01T10:00:00Z" TurnStarted
            |> withOrigin
            |> submitAndAssertOrigin

            mkReport "s1" "C:/wt/a" "heartbeat" "2026-03-01T10:01:00Z" Heartbeat
            |> submitAndAssertOrigin

            mkReport "s1" "C:/wt/a" "usage" "2026-03-01T10:02:00Z" (UsageInfo(1000, 2000))
            |> submitAndAssertOrigin

            mkReport
                "s1"
                "C:/wt/a"
                "bootstrap"
                "2026-03-01T10:03:00Z"
                (TitleBootstrap(msg "Terminal session" "2026-03-01T10:03:00Z"))
            |> submitAndAssertOrigin

            mkReport
                "s1"
                "C:/wt/a"
                "intent"
                "2026-03-01T10:04:00Z"
                (IntentReported(msg "Preserve ownership" "2026-03-01T10:04:00Z"))
            |> submitAndAssertOrigin

            mkReport "s1" "C:/wt/a" "ended" "2026-03-01T10:05:00Z" TurnEnded
            |> submitAndAssertOrigin)

    [<Test>]
    member _.``a heartbeat rehydrates a retained durable session after restart``() =
        let retained =
            { ProcessIdentity = syntheticProcessIdentityForSessionId "s1"
              SessionId = SessionId "s1"
              TerminalSessionId = None
              WorktreePath = WorktreePath(PathUtils.normalizePath "C:/wt/a")
              Provider = CopilotCli
              Status =
                { emptyStatus with
                    Status = SessionLevelStatus.WaitingForUser
                    LastAssistantMessage = Some(msg "Which option?" "2026-03-01T08:00:00Z") }
              UpdatedAt = ts "2026-03-01T08:00:00Z"
              LifecycleAt = Some(ts "2026-03-01T08:00:00Z")
              LastSeen = ts "2026-03-01T08:00:00Z"
              ContextUsageAt = None
              ClosedAt = None }

        withServiceSeeded
            "C:/wt/a"
            (fun store -> store.UpsertStatus retained)
            (fun (svc, agent, store) ->
                svc.Start()
                Assert.That(
                    svc.LiveSnapshot().ContainsKey(SessionId "s1"),
                    Is.False,
                    "the restart rebuild excludes retained sessions outside the idle window"
                )

                svc.Submit(mkReport "s1" "C:/wt/a" "hb1" "2026-03-01T10:30:00Z" Heartbeat)
                let rehydrated = svc.LiveSnapshot() |> Map.find (SessionId "s1")

                Assert.Multiple(fun () ->
                    Assert.That(rehydrated.Status.Status, Is.EqualTo SessionLevelStatus.WaitingForUser)
                    Assert.That(rehydrated.Status.LastAssistantMessage, Is.EqualTo retained.Status.LastAssistantMessage)
                    Assert.That(rehydrated.UpdatedAt, Is.EqualTo retained.UpdatedAt)
                    Assert.That(rehydrated.LastSeen, Is.EqualTo(ts "2026-03-01T10:30:00Z")))

                Assert.That(store.StatusBySession(SessionId "s1"), Is.EqualTo(Some rehydrated))

                match schedulerStatus agent "s1" with
                | Some fed -> Assert.That(fed, Is.EqualTo rehydrated)
                | None -> Assert.Fail "the rehydrated session was not fed to the scheduler")

    [<Test>]
    member _.``a heartbeat for a session with no prior event is ignored``() =
        withServiceAndPath "C:/wt/a" (fun (svc, _, _, dbPath) ->
            svc.Submit(mkReport "s1" "C:/wt/a" "hb1" "2026-03-01T10:00:00Z" Heartbeat)
            let live = svc.LiveSnapshot()
            Assert.That(live.ContainsKey(SessionId "s1"), Is.False, "a heartbeat never creates a session")
            Assert.That(eventCount dbPath, Is.Zero))

    [<Test>]
    member _.``a real event never regresses last_seen below a fresher heartbeat``() =
        withService "C:/wt/a" (fun (svc, _, store) ->
            // Establish the session, then a heartbeat advances openness to 10:02.
            present svc "s1" "C:/wt/a" (ts "2026-03-01T10:00:00Z") |> ignore
            svc.Submit(mkReport "s1" "C:/wt/a" "e1" "2026-03-01T10:00:00Z" (AssistantMessage(msg "hi" "2026-03-01T10:00:00Z")))
            svc.Submit(mkReport "s1" "C:/wt/a" "hb1" "2026-03-01T10:02:00Z" Heartbeat)
            svc.LiveSnapshot() |> ignore
            // A real, IN-ORDER event (updated_at advances past e1) whose OccurredAt predates the
            // heartbeat: it must fold, but must NOT pull last_seen back before the heartbeat.
            svc.Submit(mkReport "s1" "C:/wt/a" "e2" "2026-03-01T10:01:00Z" (UserPrompt(msg "go" "2026-03-01T10:01:00Z")))
            let s = svc.LiveSnapshot() |> Map.find (SessionId "s1")
            Assert.That(s.LastSeen, Is.EqualTo(ts "2026-03-01T10:02:00Z"), "last_seen stays monotonic (kept at the heartbeat)")
            Assert.That(s.UpdatedAt, Is.EqualTo(ts "2026-03-01T10:01:00Z"), "the real event still advances the write clock")
            Assert.That(s.Status.LastUserMessage, Is.EqualTo(Some(msg "go" "2026-03-01T10:01:00Z")), "the real event still folds")
            let stored = store.LoadLiveStatuses(ts "2026-03-01T10:05:00Z") |> List.find (fun r -> r.SessionId = SessionId "s1")
            Assert.That(stored.LastSeen, Is.EqualTo(ts "2026-03-01T10:02:00Z"), "durable last_seen is monotonic too"))

    [<Test>]
    member _.``an out-of-order event row records its own status, not the newest live status``() =
        withServiceAndPath "C:/wt/a" (fun (svc, _, _, dbPath) ->
            // Newest applied: turn_ended -> Idle.
            present svc "s1" "C:/wt/a" (ts "2026-03-01T10:00:05Z") |> ignore
            svc.Submit(mkReport "s1" "C:/wt/a" "e2" "2026-03-01T10:00:05Z" TurnEnded)
            svc.LiveSnapshot() |> ignore
            // An older assistant_message arrives late; its OWN effect is Working, not the newest Idle.
            svc.Submit(mkReport "s1" "C:/wt/a" "e1" "2026-03-01T10:00:00Z" (AssistantMessage(msg "stale" "2026-03-01T10:00:00Z")))
            svc.LiveSnapshot() |> ignore
            Assert.That(
                eventStatusCount dbPath "e1" "working",
                Is.EqualTo 1,
                "out-of-order row reflects the event's own effect, not the newest Idle"
            ))

    [<Test>]
    member _.``a usage_info gauge updates ContextUsage without moving the status clock or appending an event``() =
        withServiceAndPath "C:/wt/a" (fun (svc, agent, _, dbPath) ->
            present svc "s1" "C:/wt/a" (ts "2026-03-01T10:00:00Z") |> ignore
            svc.Submit(mkReport "s1" "C:/wt/a" "e1" "2026-03-01T10:00:00Z" TurnStarted)
            svc.LiveSnapshot() |> ignore
            svc.Submit(mkReport "s1" "C:/wt/a" "u1" "2026-03-01T10:00:05Z" (UsageInfo(120000, 200000)))
            let s = svc.LiveSnapshot() |> Map.find (SessionId "s1")
            Assert.That(s.Status.ContextUsage, Is.EqualTo(Some { CurrentTokens = 120000; TokenLimit = 200000 }), "the gauge is recorded")
            Assert.That(s.Status.Status, Is.EqualTo SessionLevelStatus.Working, "a gauge never changes status")
            Assert.That(s.UpdatedAt, Is.EqualTo(ts "2026-03-01T10:00:00Z"), "a gauge must not move the status last-write-wins clock")
            Assert.That(s.LastSeen, Is.EqualTo(ts "2026-03-01T10:00:00Z"), "usage never establishes or refreshes presence")
            Assert.That(eventCount dbPath, Is.EqualTo 1, "a usage_info must not append to activity_events")
            // The card path (scheduler) sees the gauge.
            match schedulerStatus agent "s1" with
            | Some fed -> Assert.That(fed.Status.ContextUsage, Is.EqualTo(Some { CurrentTokens = 120000; TokenLimit = 200000 }))
            | None -> Assert.Fail "the gauge was not fed to the scheduler")

    [<Test>]
    member _.``usage rehydrates a retained durable session after restart``() =
        let retained =
            { ProcessIdentity = syntheticProcessIdentityForSessionId "s1"
              SessionId = SessionId "s1"
              TerminalSessionId = None
              WorktreePath = WorktreePath(PathUtils.normalizePath "C:/wt/a")
              Provider = CopilotCli
              Status =
                { emptyStatus with
                    Status = SessionLevelStatus.WaitingForUser
                    LastAssistantMessage = Some(msg "Which option?" "2026-03-01T08:00:00Z") }
              UpdatedAt = ts "2026-03-01T08:00:00Z"
              LifecycleAt = Some(ts "2026-03-01T08:00:00Z")
              LastSeen = ts "2026-03-01T08:00:00Z"
              ContextUsageAt = None
              ClosedAt = None }
        let usage = { CurrentTokens = 120000; TokenLimit = 200000 }

        withServiceSeeded
            "C:/wt/a"
            (fun store -> store.UpsertStatus retained)
            (fun (svc, agent, store) ->
                svc.Start()
                Assert.That(
                    svc.LiveSnapshot().ContainsKey(SessionId "s1"),
                    Is.False,
                    "the restart rebuild excludes retained sessions outside the idle window"
                )

                present
                    svc
                    "s1"
                    "C:/wt/a"
                    (ts "2026-03-01T10:30:00Z")
                |> ignore
                svc.Submit(mkReport "s1" "C:/wt/a" "u1" "2026-03-01T10:30:00Z" (UsageInfo(usage.CurrentTokens, usage.TokenLimit)))
                let rehydrated = svc.LiveSnapshot() |> Map.find (SessionId "s1")

                Assert.Multiple(fun () ->
                    Assert.That(rehydrated.Status.Status, Is.EqualTo SessionLevelStatus.WaitingForUser)
                    Assert.That(rehydrated.Status.LastAssistantMessage, Is.EqualTo retained.Status.LastAssistantMessage)
                    Assert.That(rehydrated.Status.ContextUsage, Is.EqualTo(Some usage))
                    Assert.That(rehydrated.ContextUsageAt, Is.EqualTo(Some(ts "2026-03-01T10:30:00Z")))
                    Assert.That(rehydrated.UpdatedAt, Is.EqualTo retained.UpdatedAt)
                    Assert.That(rehydrated.LastSeen, Is.EqualTo(ts "2026-03-01T10:30:00Z")))

                Assert.That(store.StatusBySession(SessionId "s1"), Is.EqualTo(Some rehydrated))

                match schedulerStatus agent "s1" with
                | Some fed -> Assert.That(fed, Is.EqualTo rehydrated)
                | None -> Assert.Fail "the rehydrated session was not fed to the scheduler")

    [<Test>]
    member _.``a later usage report does not block a slightly-earlier status transition``() =
        withService "C:/wt/a" (fun (svc, _, _) ->
            // The gauge (10:00:05) is NEWER than the turn_ended (10:00:03) but arrives first. Sharing the
            // status clock would reject the turn_ended as out-of-order and leave the card stuck Working.
            present svc "s1" "C:/wt/a" (ts "2026-03-01T10:00:00Z") |> ignore
            svc.Submit(mkReport "s1" "C:/wt/a" "e1" "2026-03-01T10:00:00Z" TurnStarted)
            svc.Submit(mkReport "s1" "C:/wt/a" "u1" "2026-03-01T10:00:05Z" (UsageInfo(120000, 200000)))
            svc.Submit(mkReport "s1" "C:/wt/a" "e2" "2026-03-01T10:00:03Z" TurnEnded)
            let s = (svc.LiveSnapshot() |> Map.find (SessionId "s1")).Status
            Assert.That(s.Status, Is.EqualTo SessionLevelStatus.Idle, "the turn still ends despite the newer gauge")
            Assert.That(s.ContextUsage, Is.EqualTo(Some { CurrentTokens = 120000; TokenLimit = 200000 }), "the gauge is preserved across the transition"))

    [<Test>]
    member _.``a usage snapshot arriving after a newer status event is not discarded``() =
        withService "C:/wt/a" (fun (svc, _, _) ->
            // The gauge (10:00:03) is OLDER than the turn_ended (10:00:05) and arrives after it. Sharing
            // the status clock would reject it as out-of-order and drop the snapshot.
            present svc "s1" "C:/wt/a" (ts "2026-03-01T10:00:00Z") |> ignore
            svc.Submit(mkReport "s1" "C:/wt/a" "e1" "2026-03-01T10:00:00Z" TurnStarted)
            svc.Submit(mkReport "s1" "C:/wt/a" "e2" "2026-03-01T10:00:05Z" TurnEnded)
            svc.Submit(mkReport "s1" "C:/wt/a" "u1" "2026-03-01T10:00:03Z" (UsageInfo(50000, 200000)))
            let s = (svc.LiveSnapshot() |> Map.find (SessionId "s1")).Status
            Assert.That(s.ContextUsage, Is.EqualTo(Some { CurrentTokens = 50000; TokenLimit = 200000 }), "the gauge survives a newer status event")
            Assert.That(s.Status, Is.EqualTo SessionLevelStatus.Idle, "the gauge never changes status"))

    [<Test>]
    member _.``an out-of-order older usage snapshot does not clobber a fresher gauge``() =
        withService "C:/wt/a" (fun (svc, _, _) ->
            present svc "s1" "C:/wt/a" (ts "2026-03-01T10:00:00Z") |> ignore
            svc.Submit(mkReport "s1" "C:/wt/a" "e1" "2026-03-01T10:00:00Z" TurnStarted)
            svc.Submit(mkReport "s1" "C:/wt/a" "u2" "2026-03-01T10:00:10Z" (UsageInfo(150000, 200000)))
            // A delayed OLDER snapshot arrives last; its own usage LWW clock rejects it.
            svc.Submit(mkReport "s1" "C:/wt/a" "u1" "2026-03-01T10:00:05Z" (UsageInfo(80000, 200000)))
            let s = (svc.LiveSnapshot() |> Map.find (SessionId "s1")).Status
            Assert.That(s.ContextUsage, Is.EqualTo(Some { CurrentTokens = 150000; TokenLimit = 200000 }), "the fresher gauge is kept"))

    [<Test>]
    member _.``a usage_info for a session with no prior status is dropped``() =
        withServiceAndPath "C:/wt/a" (fun (svc, _, _, dbPath) ->
            svc.Submit(mkReport "s1" "C:/wt/a" "u1" "2026-03-01T10:00:00Z" (UsageInfo(120000, 200000)))
            let live = svc.LiveSnapshot()
            Assert.That(live.ContainsKey(SessionId "s1"), Is.False, "a gauge never creates a session")
            Assert.That(eventCount dbPath, Is.Zero))

    [<Test>]
    member _.``usage recreates a pruned row from the retained live session``() =
        let now = DateTimeOffset.UtcNow
        let worktree = Path.Combine(Path.GetTempPath(), "treemon-pruned-context-worktree")
        let normalizedWorktree = WorktreePath(PathUtils.normalizePath worktree)
        let report eventId occurredAt event =
            { ParentProcessId = syntheticProcessIdForSessionId "s1"
              SessionId = SessionId "s1"
              TerminalSessionId = None
              WorktreePath = normalizedWorktree
              Provider = CopilotCli
              EventId = EventId eventId
              OccurredAt = occurredAt
              Event = event }

        withService worktree (fun (svc, _, store) ->
            present svc "s1" worktree (now.AddMinutes(-1.0)) |> ignore
            svc.Submit(report "started" (now.AddMinutes(-1.0)) TurnStarted)
            svc.LiveSnapshot() |> ignore
            store.PruneOld now |> ignore
            Assert.That(store.LoadLiveStatuses now, Is.Empty)
            svc.Submit(report "usage" now (UsageInfo(90000, 200000)))

            let live = svc.LiveSnapshot() |> Map.find (SessionId "s1")
            let persisted = store.LoadLiveStatuses now |> List.find (fun row -> row.SessionId = SessionId "s1")
            Assert.That(persisted, Is.EqualTo(live))
            Assert.That(persisted.Status.ContextUsage, Is.EqualTo(Some { CurrentTokens = 90000; TokenLimit = 200000 })))


// ── restart rebuild ───────────────────────────────────────────────────────────
[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type RestartRebuildTests() =

    [<Test>]
    member _.``Start rebuilds live status and context usage from the store and feeds the scheduler``() =
        let now = DateTimeOffset.UtcNow
        let worktree = Path.Combine(Path.GetTempPath(), "treemon-restart-worktree")
        let usage = { CurrentTokens = 120000; TokenLimit = 200000 }
        let usageAt = now.AddSeconds(-30.0)
        let status = { emptyStatus with Status = SessionLevelStatus.Working; Skill = Some "investigate" }

        let seed (store: SessionActivityStore) =
            storedWithUsage "s1" worktree status (now.AddMinutes(-1.0)) usage usageAt
            |> store.UpsertContextUsage
            |> ignore

        withServiceSeeded worktree seed (fun (svc, agent, _) ->
            svc.Start()
            // The in-memory fold map is primed, so a subsequent event folds onto the rebuilt state.
            let live = svc.LiveSnapshot()
            let restored = live |> Map.find (SessionId "s1")
            Assert.That(restored.Status.Status, Is.EqualTo SessionLevelStatus.Working)
            Assert.That(restored.Status.Skill, Is.EqualTo(Some "investigate"))
            Assert.That(restored.Status.ContextUsage, Is.EqualTo(Some usage))
            Assert.That(restored.ContextUsageAt, Is.EqualTo(Some usageAt))
            // And the card path (scheduler) sees it immediately, before any new event.
            match schedulerStatus agent "s1" with
            | Some stored ->
                Assert.That(stored.Status.Skill, Is.EqualTo(Some "investigate"))
                Assert.That(stored.Status.ContextUsage, Is.EqualTo(Some usage))
            | None -> Assert.Fail "restart rebuild did not feed the scheduler")

    [<Test>]
    member _.``Start restores exact background lifecycle with the persisted base status``() =
        let now = DateTimeOffset.UtcNow
        let worktree = Path.Combine(Path.GetTempPath(), "treemon-restart-background-worktree")
        let status =
            fold
                emptyStatus
                (BackgroundAgentStarted("tool-1", now.AddSeconds(-45.0)))

        let seed (store: SessionActivityStore) =
            store.UpsertStatus
                { ProcessIdentity = syntheticProcessIdentityForSessionId "s1"
                  SessionId = SessionId "s1"
                  TerminalSessionId = None
                  WorktreePath = WorktreePath(PathUtils.normalizePath worktree)
                  Provider = CopilotCli
                  Status = status
                  UpdatedAt = now.AddMinutes(-1.0)
                  LifecycleAt = Some(now.AddMinutes(-1.0))
                  LastSeen = now.AddSeconds(-30.0)
                  ContextUsageAt = None
                  ClosedAt = None }

        withServiceSeeded worktree seed (fun (svc, agent, _) ->
            svc.Start()
            let restored = svc.LiveSnapshot() |> Map.find (SessionId "s1")

            Assert.Multiple(fun () ->
                Assert.That(restored.Status.Status, Is.EqualTo SessionLevelStatus.Idle)
                Assert.That(effectiveStatus restored.Status, Is.EqualTo SessionLevelStatus.Working)
                Assert.That(
                    restored.Status.BackgroundAgentClocks,
                    Is.EqualTo status.BackgroundAgentClocks
                )
                Assert.That(schedulerStatus agent "s1", Is.EqualTo(Some restored))))

    [<Test>]
    member _.``An older usage report after restart cannot replace the restored snapshot``() =
        let now = DateTimeOffset.UtcNow
        let worktree = Path.Combine(Path.GetTempPath(), "treemon-restart-worktree")
        let usage = { CurrentTokens = 150000; TokenLimit = 200000 }
        let usageAt = now.AddMinutes(-1.0)
        let status = { emptyStatus with Status = SessionLevelStatus.Working }

        let seed (store: SessionActivityStore) =
            storedWithUsage "s1" worktree status (now.AddMinutes(-2.0)) usage usageAt
            |> store.UpsertContextUsage
            |> ignore

        withServiceSeeded worktree seed (fun (svc, _, _) ->
            svc.Start()
            svc.Submit
                { ParentProcessId = syntheticProcessIdForSessionId "s1"
                  SessionId = SessionId "s1"
                  TerminalSessionId = None
                  WorktreePath = WorktreePath(PathUtils.normalizePath worktree)
                  Provider = CopilotCli
                  EventId = EventId "older-usage"
                  OccurredAt = usageAt.AddSeconds(-30.0)
                  Event = UsageInfo(80000, 200000) }

            let restored = svc.LiveSnapshot() |> Map.find (SessionId "s1")
            Assert.That(restored.Status.ContextUsage, Is.EqualTo(Some usage))
            Assert.That(restored.ContextUsageAt, Is.EqualTo(Some usageAt)))

    [<Test>]
    member _.``A status event revives all retained state outside the live restart window``() =
        let now = DateTimeOffset.UtcNow
        let worktree = Path.Combine(Path.GetTempPath(), "treemon-retained-context-worktree")
        let normalizedWorktree = WorktreePath(PathUtils.normalizePath worktree)
        let usage = { CurrentTokens = 110000; TokenLimit = 200000 }
        let usageAt = now - idleWindow - TimeSpan.FromMinutes 5.0
        let status =
            { Status = SessionLevelStatus.Idle
              Skill = Some "investigate"
              Intent = Some { Text = "diagnosing context persistence"; At = usageAt.AddMinutes(-4.0) }
              Title = Some { Text = "Persist context info"; At = usageAt.AddMinutes(-3.0) }
              LastUserMessage = Some { Text = "keep the context"; At = usageAt.AddMinutes(-2.0) }
              LastAssistantMessage = Some { Text = "working on it"; At = usageAt.AddMinutes(-1.0) }
              ContextUsage = None
              AwaitingUserSince = None
              UserInputCompletedAt = None
              BackgroundAgentClocks = Map.empty }

        let seed (store: SessionActivityStore) =
            storedWithUsage "s1" worktree status (usageAt.AddMinutes(-1.0)) usage usageAt
            |> store.UpsertContextUsage
            |> ignore

        withServiceSeeded worktree seed (fun (svc, agent, _) ->
            svc.Start()
            Assert.That((svc.LiveSnapshot()).ContainsKey(SessionId "s1"), Is.False)

            svc.Submit
                { ParentProcessId = syntheticProcessIdForSessionId "s1"
                  SessionId = SessionId "s1"
                  TerminalSessionId = None
                  WorktreePath = normalizedWorktree
                  Provider = CopilotCli
                  EventId = EventId "revive"
                  OccurredAt = now
                  Event = TurnStarted }

            let revived = svc.LiveSnapshot() |> Map.find (SessionId "s1")
            Assert.That(revived.Status.Status, Is.EqualTo(SessionLevelStatus.Working))
            Assert.That(revived.Status.Skill, Is.EqualTo(status.Skill))
            Assert.That(revived.Status.Intent, Is.EqualTo(status.Intent))
            Assert.That(revived.Status.Title, Is.EqualTo(status.Title))
            Assert.That(revived.Status.LastUserMessage, Is.EqualTo(status.LastUserMessage))
            Assert.That(revived.Status.LastAssistantMessage, Is.EqualTo(status.LastAssistantMessage))
            Assert.That(revived.Status.ContextUsage, Is.EqualTo(Some usage))
            Assert.That(revived.ContextUsageAt, Is.EqualTo(Some usageAt))
            Assert.That(schedulerStatus agent "s1", Is.EqualTo(Some revived)))

    [<Test>]
    member _.``a session quiet longer than the idle window is not rebuilt as live``() =
        let now = DateTimeOffset.UtcNow

        let seed (store: SessionActivityStore) =
            store.UpsertStatus
                { ProcessIdentity = syntheticProcessIdentityForSessionId "stale"
                  SessionId = SessionId "stale"
                  TerminalSessionId = None
                  WorktreePath = WorktreePath "C:/wt/a"
                  Provider = CopilotCli
                  Status = { emptyStatus with Status = SessionLevelStatus.Working }
                  UpdatedAt = now - idleWindow - TimeSpan.FromMinutes 5.0
                  LifecycleAt =
                    Some(now - idleWindow - TimeSpan.FromMinutes 5.0)
                  LastSeen = now - idleWindow - TimeSpan.FromMinutes 5.0
                  ContextUsageAt = None
                  ClosedAt = None }

        withServiceSeeded "C:/wt/a" seed (fun (svc, _, _) ->
            svc.Start()
            Assert.That((svc.LiveSnapshot()).ContainsKey(SessionId "stale"), Is.False))


// ── exact terminal ownership queries ─────────────────────────────────────────
[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type TerminalOwnershipQueryTests() =

    let ownedStored terminalSessionId sessionId lastSeen =
        { ProcessIdentity = syntheticProcessIdentityForSessionId sessionId
          SessionId = SessionId sessionId
          TerminalSessionId = Some terminalSessionId
          WorktreePath = WorktreePath "C:/wt/a"
          Provider = CopilotCli
          Status = { emptyStatus with Status = SessionLevelStatus.Idle }
          UpdatedAt = lastSeen
          LifecycleAt = Some lastSeen
          LastSeen = lastSeen
          ContextUsageAt = None
          ClosedAt = None }

    [<Test>]
    member _.``terminal activity projection materializes only requested origins from a large live map``() =
        let requested =
            TerminalSessionId "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"

        let unrelated =
            TerminalSessionId "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"

        let at = ts "2026-03-01T10:00:00Z"

        let live =
            [ 1..5000 ]
            |> List.map (fun index ->
                let sessionId = $"unrelated-{index}"
                let instance = ownedStored unrelated sessionId at
                instance.ProcessIdentity, instance)
            |> Map.ofList
            |> fun instances ->
                let requestedInstance =
                    ownedStored requested "requested" at

                instances
                |> Map.add
                    requestedInstance.ProcessIdentity
                    requestedInstance

        let projected =
            statusesForTerminalOrigins
                (Set.singleton requested)
                live

        Assert.That(
            projected |> List.map _.SessionId,
            Is.EqualTo([ SessionId "requested" ])
        )

    [<Test>]
    member _.``terminal snapshot titles use each exact terminal's representative activity``() =
        let terminalA =
            TerminalSessionId "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"

        let terminalB =
            TerminalSessionId "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"

        let unrelated =
            TerminalSessionId "cccccccccccccccccccccccccccccccc"

        let now = ts "2026-03-01T10:05:00Z"
        let message text at = Some { Text = text; At = at }

        let stored terminalSessionId sessionId status updatedAt lastSeen intent title =
            { ownedStored terminalSessionId sessionId lastSeen with
                Status =
                    { emptyStatus with
                        Status = status
                        Intent = intent
                        Title = title }
                UpdatedAt = updatedAt }

        let snapshot =
            { Tabs =
                [ { Id = EmbeddedTerminalId(TerminalSessionId.value terminalA)
                    Worktree = WorktreePath "C:/wt/a"
                    ReportedActivity = None
                    Lifecycle = EmbeddedTerminalLifecycle.Running "http://127.0.0.1:61001/" }
                  { Id = EmbeddedTerminalId(TerminalSessionId.value terminalB)
                    Worktree = WorktreePath "C:/wt/a"
                    ReportedActivity = None
                    Lifecycle = EmbeddedTerminalLifecycle.Running "http://127.0.0.1:61002/" } ] }

        let decorated =
            snapshot
            |> withReportedActivity
                now
                [ stored
                      terminalA
                      "idle-a"
                      SessionLevelStatus.Idle
                      (ts "2026-03-01T10:03:00Z")
                      (ts "2026-03-01T10:04:00Z")
                      (message "Idle terminal work" (ts "2026-03-01T10:03:00Z"))
                      None
                  stored
                      terminalA
                      "working-a"
                      SessionLevelStatus.Working
                      (ts "2026-03-01T10:02:00Z")
                      (ts "2026-03-01T10:04:30Z")
                      (message "Implementing exact terminal titles" (ts "2026-03-01T10:04:30Z"))
                      None
                  stored
                      terminalB
                      "working-b"
                      SessionLevelStatus.Working
                      (ts "2026-03-01T10:04:00Z")
                      (ts "2026-03-01T10:04:30Z")
                      None
                      (message "Session title only" (ts "2026-03-01T10:04:00Z"))
                  { stored
                        terminalA
                        "closed-a"
                        SessionLevelStatus.Working
                        (ts "2026-03-01T10:04:50Z")
                        (ts "2026-03-01T10:04:50Z")
                        (message "Closed terminal activity" (ts "2026-03-01T10:04:50Z"))
                        None with
                        ClosedAt = Some(ts "2026-03-01T10:04:55Z") }
                  stored
                      terminalB
                      "stale-b"
                      SessionLevelStatus.Working
                      (ts "2026-03-01T10:04:50Z")
                      (now - openWindow - TimeSpan.FromSeconds 1.0)
                      (message "Stale terminal activity" (ts "2026-03-01T10:04:50Z"))
                      None
                  stored
                      unrelated
                      "unrelated"
                      SessionLevelStatus.Working
                      (ts "2026-03-01T10:04:30Z")
                      (ts "2026-03-01T10:04:30Z")
                      (message "Wrong terminal" (ts "2026-03-01T10:04:30Z"))
                      None ]

        Assert.That(
            decorated.Tabs
            |> List.map (fun tab -> tab.Id, tab.ReportedActivity),
            Is.EqualTo(
                [ (EmbeddedTerminalId(TerminalSessionId.value terminalA),
                   Some "Implementing exact terminal titles")
                  (EmbeddedTerminalId(TerminalSessionId.value terminalB),
                   Some "Session title only") ]
            )
        )

    [<Test>]
    member _.``stale durable terminal history is not a replacement candidate``() =
        let oldTerminal =
            TerminalSessionId "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"

        let freshTerminal =
            TerminalSessionId "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"

        let withOrigin terminalSessionId (report: SessionActivityReport) =
            { report with TerminalSessionId = Some terminalSessionId }

        withService "C:/wt/a" (fun (service, _, _) ->
            present service "old" "C:/wt/a" (ts "2026-03-01T10:00:00Z") |> ignore
            mkReport "old" "C:/wt/a" "old-event" "2026-03-01T10:00:00Z" TurnStarted
            |> withOrigin oldTerminal
            |> service.Submit

            present service "fresh" "C:/wt/a" (ts "2026-03-01T12:01:00Z") |> ignore
            mkReport "fresh" "C:/wt/a" "fresh-event" "2026-03-01T12:01:00Z" TurnStarted
            |> withOrigin freshTerminal
            |> service.Submit

            let live = service.LiveSnapshot()
            let retained =
                queryOwnedOk
                    service
                    (ts "2026-03-01T12:01:00Z")
                    (Set.singleton oldTerminal)
            let _, shutdownTargets, resumeCommands =
                queryReplacementPlanOk
                    service
                    (ts "2026-03-01T12:01:00Z")
                    [ replacementTerminal oldTerminal "C:/wt/a" ]
                |> requireReplacementReady

            Assert.Multiple(fun () ->
                Assert.That(
                    live |> Map.keys |> Seq.toList,
                    Is.EqualTo([ SessionId "fresh" ])
                )
                Assert.That(retained.OpenSessions, Is.Empty)
                Assert.That(shutdownTargets, Is.Empty)
                Assert.That(resumeCommands, Is.Empty)))

    [<Test>]
    member _.``epoch pruning keeps retained and current origins and never reuses sequence values``() =
        let current =
            TerminalSessionId "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"

        let retained =
            TerminalSessionId "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"

        let expired =
            TerminalSessionId "cccccccccccccccccccccccccccccccc"

        let initial =
            emptyTerminalOriginEpochState
            |> recordTerminalOriginActivity (Set.singleton current)

        let firstCurrentEpoch, withCurrent =
            observeCurrentTerminalOrigins (Set.singleton current) initial

        let beforePrune =
            withCurrent
            |> recordTerminalOriginActivity (Set.ofList [ retained; expired ])

        let pruned =
            beforePrune
            |> pruneTerminalOriginEpochs (Set.singleton retained)

        let currentEpoch, _ =
            observeCurrentTerminalOrigins (Set.singleton current) pruned

        let retainedEpoch, _ =
            observeCurrentTerminalOrigins (Set.singleton retained) pruned

        let expiredEpoch, withoutExpired =
            observeCurrentTerminalOrigins (Set.singleton expired) pruned

        let reused =
            withoutExpired
            |> recordTerminalOriginActivity (Set.singleton current)

        let nextCurrentEpoch, _ =
            observeCurrentTerminalOrigins (Set.singleton current) reused

        Assert.Multiple(fun () ->
            Assert.That(firstCurrentEpoch, Is.GreaterThan 0L)
            Assert.That(currentEpoch, Is.EqualTo firstCurrentEpoch)
            Assert.That(retainedEpoch, Is.GreaterThan firstCurrentEpoch)
            Assert.That(expiredEpoch, Is.Zero)
            Assert.That(
                nextCurrentEpoch,
                Is.GreaterThan retainedEpoch,
                "pruning an origin must not reset the monotonic global sequence"
            ))

    [<Test>]
    member _.``retention sweep removes stale live state and inactive terminal epochs``() =
        let current =
            TerminalSessionId "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"

        let expired =
            TerminalSessionId "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"

        let oldAt = ts "2026-01-01T10:00:00Z"
        let now = oldAt + retentionPeriod + TimeSpan.FromDays 1.0

        withService "C:/wt/a" (fun (service, _, store) ->
            queryActivityOk service (Set.singleton current) |> ignore

            mkReport
                "expired"
                "C:/wt/a"
                "expired-event"
                "2026-01-01T10:00:00Z"
                TurnStarted
            |> fun report ->
                { report with TerminalSessionId = Some expired }
            |> service.Submit

            service.LiveSnapshot() |> ignore
            service.RunRetention now

            let activityEpoch, sessions =
                queryActivityOk service (Set.singleton expired)

            Assert.Multiple(fun () ->
                Assert.That(service.LiveSnapshot(), Is.Empty)
                Assert.That(activityEpoch, Is.Zero)
                Assert.That(sessions, Is.Empty)
                Assert.That(store.StatusBySession(SessionId "expired"), Is.EqualTo None)))

    [<Test>]
    member _.``replacement policy binds the provider command to its exact terminal``() =
        let ownedTerminal =
            TerminalSessionId "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
        let plainTerminal =
            TerminalSessionId "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
        let ownedPath = "C:/wt/owned"
        let terminals =
            [ replacementTerminal ownedTerminal ownedPath
              replacementTerminal plainTerminal "C:/wt/plain" ]

        let snapshot: OwnedSessionSnapshot =
            { ActivityEpoch = 17L
              OpenSessions =
                [ { ProcessIdentity =
                        syntheticProcessIdentityForSessionId "provider-owned-session"
                    TerminalSessionId = ownedTerminal
                    CopilotSessionId = SessionId "provider-owned-session"
                    Status = SessionLevelStatus.Idle } ]
              PendingReconciliation = Set.empty
              ReplacementSessionIds =
                Map.ofList
                    [ ownedTerminal,
                      SessionId "provider-owned-session" ] }

        let resolveProvider (path: string) =
            Assert.That(
                path,
                Is.EqualTo ownedPath,
                "only the live replacement terminal selects a provider from its own worktree"
            )

            Some CopilotCli

        let epoch, shutdownTargets, commands =
            replacementSessionPlan resolveProvider terminals snapshot
            |> requireReplacementReady

        let expectedShutdownTarget:
            TerminalHostReplacement.ReplacementShutdownTarget =
            { TerminalSessionId = ownedTerminal
              WorktreePath = ownedPath
              CopilotSessionId =
                SessionId "provider-owned-session"
              ProcessIdentity =
                syntheticProcessIdentityForSessionId
                    "provider-owned-session" }

        Assert.Multiple(fun () ->
            Assert.That(epoch, Is.EqualTo snapshot.ActivityEpoch)
            Assert.That(
                shutdownTargets,
                Is.EqualTo([ expectedShutdownTarget ])
            )
            Assert.That(
                commands,
                Is.EqualTo(
                    Map.ofList
                        [ ownedTerminal,
                          replacementResume
                              "provider-owned-session"
                              "copilot --yolo --resume 'provider-owned-session'" ]
                ),
                "the unrelated terminal remains a plain shell"
            ))

    [<Test>]
    member _.``one terminal keeps every exact shutdown target but selects one resume conversation``() =
        let terminal =
            TerminalSessionId "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
        let now = ts "2026-03-01T10:05:00Z"
        let older =
            { ownedStored terminal "older-conversation" now with
                UpdatedAt = now.AddMinutes(-2.0) }
        let newer =
            { ownedStored terminal "newer-conversation" now with
                UpdatedAt = now.AddMinutes(-1.0) }

        let snapshot =
            ownedSessionSnapshot
                now
                (Set.singleton terminal)
                (19L, [ older; newer ], Set.empty)

        let _, shutdownTargets, resumeCommands =
            replacementSessionPlan
                (fun _ -> Some CopilotCli)
                [ replacementTerminal terminal "C:/wt/a" ]
                snapshot
            |> requireReplacementReady

        Assert.Multiple(fun () ->
            Assert.That(
                snapshot.OpenSessions
                |> List.map _.ProcessIdentity
                |> Set.ofList,
                Is.EqualTo(
                    Set.ofList
                        [ older.ProcessIdentity
                          newer.ProcessIdentity ]
                ),
                "every physical process remains an exact shutdown target"
            )
            Assert.That(
                snapshot.ReplacementSessionIds,
                Is.EqualTo(
                    Map.ofList
                        [ terminal,
                          SessionId "newer-conversation" ]
                ),
                "only the greatest-activity conversation is selected for automatic Resume"
            )
            Assert.That(
                shutdownTargets
                |> List.map _.ProcessIdentity
                |> Set.ofList,
                Is.EqualTo(
                    Set.ofList
                        [ older.ProcessIdentity
                          newer.ProcessIdentity ]
                ),
                "both physical processes must be returned as independent shutdown targets"
            )
            Assert.That(
                resumeCommands,
                Is.EqualTo(
                    Map.ofList
                        [ terminal,
                          replacementResume
                              "newer-conversation"
                              "copilot --yolo --resume 'newer-conversation'" ]
                ),
                "only the selected durable conversation receives an automatic Resume command"
            ))

    [<Test>]
    member _.``replacement queries per-instance reconciliation immediately without a global startup delay``() =
        let now = ts "2026-03-01T10:00:00Z"
        let terminalSessionId =
            TerminalSessionId "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
        let terminal =
            replacementTerminal terminalSessionId "C:/wt/a"

        // Test-boundary mutation records whether the policy query was invoked.
        let mutable queried = false

        let result =
            queryReplacementPlan
                (fun _ -> Some CopilotCli)
                (fun _ ->
                    queried <- true
                    Ok(7L, [], Set.empty))
                now
                [ terminal ]

        Assert.Multiple(fun () ->
            Assert.That(queried, Is.True)
            Assert.That(
                result,
                Is.EqualTo(
                    Ok(
                        TerminalHostReplacement.ReplacementSessionPlan.Ready(
                            7L,
                            [],
                            Map.empty
                        )
                    )
                    : Result<TerminalHostReplacement.ReplacementSessionPlan, string>
                )
            ))

    [<Test>]
    member _.``fresh waiting session gates until input completes``() =
        let terminalSessionId =
            TerminalSessionId "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
        let worktreePath = "C:/wt/a"
        let awaitingAt = ts "2026-03-01T10:00:00Z"
        let completedAt = ts "2026-03-01T10:01:00Z"
        let now = ts "2026-03-01T10:02:30Z"
        let waiting =
            { ownedStored terminalSessionId "waiting" awaitingAt with
                Status =
                    fold
                        emptyStatus
                        (AwaitingUserInput(None, awaitingAt)) }

        let newerIdle =
            ownedStored
                terminalSessionId
                "newer-idle"
                (ts "2026-03-01T10:02:00Z")

        let snapshot sessions =
            ownedSessionSnapshot
                now
                (Set.singleton terminalSessionId)
                (31L, sessions, Set.empty)

        let waitingSnapshot = snapshot [ waiting; newerIdle ]

        Assert.That(
            replacementSessionPlan
                (fun _ -> Some CopilotCli)
                [ replacementTerminal terminalSessionId worktreePath ]
                waitingSnapshot,
            Is.EqualTo
                TerminalHostReplacement.ReplacementSessionPlan.WaitingForIdle,
            "most-recent selection applies to the live resume identity, not to the all-session idle gate"
        )

        let completed =
            { waiting with
                Status =
                    fold
                        waiting.Status
                        (UserInputCompleted completedAt)
                UpdatedAt = completedAt
                LastSeen = completedAt }

        let completedSnapshot = snapshot [ completed; newerIdle ]
        let epoch, shutdownTargets, resumeCommands =
            replacementSessionPlan
                (fun _ -> Some CopilotCli)
                [ replacementTerminal terminalSessionId worktreePath ]
                completedSnapshot
            |> requireReplacementReady

        Assert.Multiple(fun () ->
            Assert.That(epoch, Is.EqualTo completedSnapshot.ActivityEpoch)
            Assert.That(shutdownTargets.Length, Is.EqualTo(2))
            Assert.That(
                resumeCommands,
                Is.EqualTo(
                    Map.ofList
                        [ terminalSessionId,
                          replacementResume
                              "newer-idle"
                              "copilot --yolo --resume 'newer-idle'" ]
                )
            ))

    [<Test>]
    member _.``only exact current terminal origins join and advance their activity epoch``() =
        let terminalA =
            TerminalSessionId "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
        let terminalB =
            TerminalSessionId "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
        let now = ts "2026-03-01T10:00:30Z"
        let replacementTarget =
            replacementTerminal terminalA "C:/wt/a"
        let withOrigin
            terminalSessionId
            (report: SessionActivityReport)
            : SessionActivityReport =
            { report with TerminalSessionId = Some terminalSessionId }

        withService "C:/wt/a" (fun (service, _, store) ->
            present service "owned" "C:/wt/a" (ts "2026-03-01T10:00:00Z") |> ignore
            mkReport "owned" "C:/wt/a" "owned-idle" "2026-03-01T10:00:00Z" TurnEnded
            |> withOrigin terminalA
            |> service.Submit

            mkReport
                "owned"
                "C:/wt/a"
                "owned-background"
                "2026-03-01T10:00:05Z"
                (BackgroundAgentStarted("tool-1", ts "2026-03-01T10:00:05Z"))
            |> withOrigin terminalA
            |> service.Submit

            let working =
                queryOwnedOk service now (Set.singleton terminalA)

            Assert.Multiple(fun () ->
                Assert.That(working.ActivityEpoch, Is.GreaterThan 0L)
                Assert.That(
                    working.OpenSessions,
                    Is.EqualTo(
                        [ { ProcessIdentity =
                                syntheticProcessIdentityForSessionId "owned"
                            TerminalSessionId = terminalA
                            CopilotSessionId = SessionId "owned"
                            Status = SessionLevelStatus.Working } ]
                    )
                )
                Assert.That(
                    working.ReplacementSessionIds,
                    Is.EqualTo(Map.ofList [ terminalA, SessionId "owned" ])
                )
                Assert.That(
                    queryReplacementPlanOk service now [ replacementTarget ],
                    Is.EqualTo
                        TerminalHostReplacement.ReplacementSessionPlan.WaitingForIdle
                ))

            present
                service
                "same-worktree-unowned"
                "C:/wt/a"
                (ts "2026-03-01T10:00:10Z")
            |> ignore
            mkReport "same-worktree-unowned" "C:/wt/a" "unowned" "2026-03-01T10:00:10Z" TurnStarted
            |> service.Submit

            present service "other-terminal" "C:/wt/a" (ts "2026-03-01T10:00:11Z") |> ignore
            mkReport "other-terminal" "C:/wt/a" "other" "2026-03-01T10:00:11Z" TurnStarted
            |> withOrigin terminalB
            |> service.Submit

            let afterUnrelated =
                queryOwnedOk service now (Set.singleton terminalA)

            Assert.Multiple(fun () ->
                Assert.That(afterUnrelated.ActivityEpoch, Is.EqualTo working.ActivityEpoch)
                Assert.That(
                    afterUnrelated.OpenSessions |> List.map _.CopilotSessionId,
                    Is.EqualTo([ SessionId "owned" ])
                )
                Assert.That(
                    queryReplacementPlanOk service now [ replacementTarget ],
                    Is.EqualTo
                        TerminalHostReplacement.ReplacementSessionPlan.WaitingForIdle,
                    "unowned same-worktree and other-terminal sessions cannot change the exact terminal policy"
                ))

            mkReport
                "owned"
                "C:/wt/a"
                "owned-finished"
                "2026-03-01T10:00:15Z"
                (BackgroundAgentFinished("tool-1", ts "2026-03-01T10:00:15Z"))
            |> withOrigin terminalA
            |> service.Submit

            let idle =
                queryOwnedOk service now (Set.singleton terminalA)
            let policyEpoch, shutdownTargets, resumeCommands =
                queryReplacementPlanOk service now [ replacementTarget ]
                |> requireReplacementReady

            Assert.Multiple(fun () ->
                Assert.That(idle.ActivityEpoch, Is.GreaterThan afterUnrelated.ActivityEpoch)
                Assert.That(idle.OpenSessions |> List.map _.Status, Is.EqualTo([ SessionLevelStatus.Idle ]))
                Assert.That(policyEpoch, Is.EqualTo idle.ActivityEpoch)
                Assert.That(
                    shutdownTargets
                    |> List.map _.ProcessIdentity,
                    Is.EqualTo(
                        idle.OpenSessions
                        |> List.map _.ProcessIdentity
                    )
                )
                Assert.That(
                    resumeCommands,
                    Is.EqualTo(
                        Map.ofList
                            [ terminalA,
                              replacementResume
                                  "owned"
                                  "copilot --yolo --resume 'owned'" ]
                    ),
                    "the session orchestration layer selects the provider-specific resume command"
                )
                Assert.That(
                    store.StatusBySession(SessionId "same-worktree-unowned")
                    |> Option.bind _.TerminalSessionId,
                    Is.EqualTo None
                ))

            mkReport "owned" "C:/wt/a" "origin-omitted" "2026-03-01T10:00:20Z" WentIdle
            |> service.Submit

            let retained =
                queryOwnedOk service now (Set.singleton terminalA)
            let retainedEpoch, retainedTargets, retainedCommands =
                queryReplacementPlanOk service now [ replacementTarget ]
                |> requireReplacementReady

            Assert.Multiple(fun () ->
                Assert.That(retained.ActivityEpoch, Is.GreaterThan idle.ActivityEpoch)
                Assert.That(
                    retained.OpenSessions,
                    Is.EqualTo(
                        [ { ProcessIdentity =
                                syntheticProcessIdentityForSessionId "owned"
                            TerminalSessionId = terminalA
                            CopilotSessionId = SessionId "owned"
                            Status = SessionLevelStatus.Idle } ]
                    ),
                    "an omitted origin keeps the session attached to its exact terminal"
                )
                Assert.That(
                    retained.ReplacementSessionIds,
                    Is.EqualTo(Map.ofList [ terminalA, SessionId "owned" ])
                )
                Assert.That(retainedEpoch, Is.EqualTo retained.ActivityEpoch)
                Assert.That(
                    retainedTargets
                    |> List.map _.ProcessIdentity,
                    Is.EqualTo(
                        retained.OpenSessions
                        |> List.map _.ProcessIdentity
                    )
                )
                Assert.That(
                    retainedCommands,
                    Is.EqualTo(
                        Map.ofList
                            [ terminalA,
                              replacementResume
                                  "owned"
                                  "copilot --yolo --resume 'owned'" ]
                    )
                )))

    [<Test>]
    member _.``startup reconciliation lets a surviving session reassert before replacement``() =
        let terminalSessionId =
            TerminalSessionId "cccccccccccccccccccccccccccccccc"
        let now = DateTimeOffset.UtcNow
        let worktree = Path.Combine(Path.GetTempPath(), "treemon-owned-resume-worktree")
        let retained updatedAt lastSeen sessionId =
            { ProcessIdentity = syntheticProcessIdentityForSessionId sessionId
              SessionId = SessionId sessionId
              TerminalSessionId = Some terminalSessionId
              WorktreePath = WorktreePath(PathUtils.normalizePath worktree)
              Provider = CopilotCli
              Status = { emptyStatus with Status = SessionLevelStatus.Idle }
              UpdatedAt = updatedAt
              LifecycleAt = Some updatedAt
              LastSeen = lastSeen
              ContextUsageAt = None
              ClosedAt = None }

        let seed (store: SessionActivityStore) =
            store.UpsertStatus(
                retained
                    (now.AddMinutes(-1.0))
                    (now.AddMinutes(-1.0))
                    "surviving"
            )

        withServiceSeeded worktree seed (fun (service, _, _) ->
            service.Start()
            Assert.That(service.ExactSnapshot().Count, Is.EqualTo 1)
            Assert.That(
                queryReplacementPlanOk
                    service
                    now
                    [ replacementTerminal terminalSessionId worktree ],
                Is.EqualTo
                    TerminalHostReplacement.ReplacementSessionPlan.WaitingForIdle
            )

            let representedAt = now.AddMinutes(1.0)

            mkReport
                "surviving"
                worktree
                "surviving-presence"
                (representedAt.ToString("O"))
                SessionPresent
            |> fun report ->
                { report with TerminalSessionId = Some terminalSessionId }
            |> fun report ->
                match service.Present(report, representedAt) with
                | PresenceAcknowledge.Recorded _ -> ()
                | PresenceAcknowledge.NotRecorded(_, reason) ->
                    Assert.Fail reason

            Assert.That(
                service.LiveSnapshot() |> Map.keys |> Seq.toList,
                Is.EqualTo([ SessionId "surviving" ])
            )
            let snapshot =
                queryOwnedOk
                    service
                    representedAt
                    (Set.singleton terminalSessionId)

            let policyEpoch, shutdownTargets, resumeCommands =
                queryReplacementPlanOk
                    service
                    representedAt
                    [ replacementTerminal terminalSessionId worktree ]
                |> requireReplacementReady

            Assert.Multiple(fun () ->
                Assert.That(snapshot.ActivityEpoch, Is.GreaterThan 0L)
                Assert.That(
                    snapshot.OpenSessions,
                    Is.EqualTo(
                        [ { ProcessIdentity =
                                syntheticProcessIdentityForSessionId "surviving"
                            TerminalSessionId = terminalSessionId
                            CopilotSessionId = SessionId "surviving"
                            Status = SessionLevelStatus.Idle } ]
                    )
                )
                Assert.That(
                    snapshot.ReplacementSessionIds,
                    Is.EqualTo(
                        Map.ofList
                            [ terminalSessionId,
                              SessionId "surviving" ]
                    )
                )
                Assert.That(policyEpoch, Is.EqualTo snapshot.ActivityEpoch)
                Assert.That(
                    shutdownTargets
                    |> List.map _.ProcessIdentity,
                    Is.EqualTo(
                        snapshot.OpenSessions
                        |> List.map _.ProcessIdentity
                    )
                )
                Assert.That(
                    resumeCommands,
                    Is.EqualTo(
                        Map.ofList
                            [ terminalSessionId,
                              replacementResume
                                  "surviving"
                                  "copilot --yolo --resume 'surviving'" ]
                    )
                )))
