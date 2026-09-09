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

/// Distinct exact terminal origins shared by the ownership/replacement fixtures.
let private terminalA = TerminalSessionId "aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa"
let private terminalB = TerminalSessionId "bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb"
let private terminalC = TerminalSessionId "cccccccccccccccccccccccccccccccc"

/// An open exact session as the ownership snapshot projects it for one terminal.
let private openSession terminalSessionId sessionId status =
    { ProcessIdentity = syntheticProcessIdentityForSessionId sessionId
      TerminalSessionId = terminalSessionId
      CopilotSessionId = SessionId sessionId
      Status = status }

/// The provider-specific Resume command the session orchestration layer must emit for a durable
/// conversation, keyed by the exact terminal that owns it.
let private resumeCommandsFor entries =
    entries
    |> List.map (fun (terminalSessionId, sessionId) ->
        terminalSessionId,
        replacementResume sessionId $"copilot --experimental --yolo --session-id='{sessionId}'")
    |> Map.ofList

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

/// A durable exact instance for one synthetic session, with no terminal origin and no usage gauge.
let private instanceOf sid worktree status updatedAt lastSeen : StoredInstance =
    { ProcessIdentity = syntheticProcessIdentityForSessionId sid
      SessionId = SessionId sid
      TerminalSessionId = None
      WorktreePath = WorktreePath(PathUtils.normalizePath worktree)
      Provider = CopilotCli
      Status = status
      UpdatedAt = updatedAt
      LifecycleAt = Some updatedAt
      LastSeen = lastSeen
      ContextUsageAt = None
      ClosedAt = None }

let private storedWithUsage sid worktree status updatedAt usage usageAt =
    { instanceOf sid worktree { status with ContextUsage = Some usage } updatedAt usageAt with
        ContextUsageAt = Some usageAt }

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

let private persistedEventIds dbPath =
    use connection = SqliteTestDatabase.openConnection dbPath
    use command = connection.CreateCommand()
    command.CommandText <-
        "SELECT event_id FROM activity_events ORDER BY ts, rowid;"
    use reader = command.ExecuteReader()

    let rec read rows =
        if reader.Read() then
            read (reader.GetString 0 :: rows)
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

    static member ValidMappingCases: obj array seq =
        seq {
            yield [| box "turn_started maps to TurnStarted"; box (baseReq "turn_started"); box TurnStarted |]
            yield [| box "turn_ended maps to TurnEnded"; box (baseReq "turn_ended"); box TurnEnded |]
            yield [| box "went_idle maps to WentIdle"; box (baseReq "went_idle"); box WentIdle |]
            yield [| box "user_input_completed maps to UserInputCompleted"
                     box (baseReq "user_input_completed")
                     box (UserInputCompleted(ts "2026-03-01T10:00:00Z")) |]
            yield [| box "user_prompt with a message maps to UserPrompt"
                     box { baseReq "user_prompt" with message = msgDto "hello" "2026-03-01T10:00:00Z" }
                     box (UserPrompt(msg "hello" "2026-03-01T10:00:00Z")) |]
            yield [| box "assistant_message with a message maps to AssistantMessage"
                     box { baseReq "assistant_message" with message = msgDto "hi there" "2026-03-01T10:00:00Z" }
                     box (AssistantMessage(msg "hi there" "2026-03-01T10:00:00Z")) |]
            yield [| box "intent_reported with a message maps to IntentReported"
                     box { baseReq "intent_reported" with message = msgDto "investigating the fold" "2026-03-01T10:00:00Z" }
                     box (IntentReported(msg "investigating the fold" "2026-03-01T10:00:00Z")) |]
            yield [| box "title_reported with a message maps to TitleReported"
                     box { baseReq "title_reported" with message = msgDto "Investigate Work Item 261312" "2026-03-01T10:00:00Z" }
                     box (TitleReported(msg "Investigate Work Item 261312" "2026-03-01T10:00:00Z")) |]
            yield [| box "title_bootstrap with a message maps to TitleBootstrap"
                     box { baseReq "title_bootstrap" with message = msgDto "Investigate Work Item 261312" "2026-03-01T10:00:00Z" }
                     box (TitleBootstrap(msg "Investigate Work Item 261312" "2026-03-01T10:00:00Z")) |]
            yield [| box "skill_invoked with a skillName maps to SkillInvoked"
                     box { baseReq "skill_invoked" with skillName = "investigate" }
                     box (SkillInvoked "investigate") |]
            yield [| box "awaiting_user_input carries the question when a message is present"
                     box { baseReq "awaiting_user_input" with message = msgDto "Which file?" "2026-03-01T10:00:00Z" }
                     box (AwaitingUserInput(Some(msg "Which file?" "2026-03-01T10:00:00Z"), ts "2026-03-01T10:00:00Z")) |]
            yield [| box "awaiting_user_input with no message maps to AwaitingUserInput None"
                     box (baseReq "awaiting_user_input")
                     box (AwaitingUserInput(None, ts "2026-03-01T10:00:00Z")) |]
            yield [| box "awaiting_user_input with blank message text maps to AwaitingUserInput None"
                     box { baseReq "awaiting_user_input" with message = msgDto "   " "2026-03-01T10:00:00Z" }
                     box (AwaitingUserInput(None, ts "2026-03-01T10:00:00Z")) |]
            yield [| box "usage_info with tokens maps to UsageInfo"
                     box { baseReq "usage_info" with currentTokens = 120000; tokenLimit = 200000 }
                     box (UsageInfo(120000, 200000)) |]
            yield [| box "usage_info clamps a negative currentTokens to zero"
                     box { baseReq "usage_info" with currentTokens = -5; tokenLimit = 200000 }
                     box (UsageInfo(0, 200000)) |]
        }

    [<TestCaseSource("ValidMappingCases")>]
    member _.``a report maps to the expected domain event``
        (name: string, req: SessionActivityRequest, expected: SessionEvent)
        =
        Assert.That((parseOk req).Event, Is.EqualTo expected, name)

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

    static member RequiredFieldRejectionCases: obj array seq =
        seq {
            yield [| box "intent_reported without a message is rejected (never regresses to blank)"
                     box (baseReq "intent_reported")
                     box "message" |]
            yield [| box "title_bootstrap without a message is rejected"
                     box (baseReq "title_bootstrap")
                     box "message" |]
            yield [| box "an unknown kind is rejected (no catch-all)"
                     box (baseReq "session_resumed")
                     box "unknown kind" |]
            yield [| box "user_prompt without a message is rejected"
                     box (baseReq "user_prompt")
                     box "message" |]
            yield [| box "assistant_message with a blank message text is rejected"
                     box { baseReq "assistant_message" with message = msgDto "   " "2026-03-01T10:00:00Z" }
                     box "message" |]
            yield [| box "skill_invoked without a skillName is rejected"
                     box (baseReq "skill_invoked")
                     box "skillName" |]
            yield [| box "usage_info with a non-positive tokenLimit is rejected"
                     box { baseReq "usage_info" with currentTokens = 100; tokenLimit = 0 }
                     box "tokenLimit" |]
            yield [| box "an unknown provider is rejected"
                     box { baseReq "turn_started" with provider = "openai" }
                     box "provider" |]
            yield [| box "a malformed occurredAt is rejected"
                     box { baseReq "turn_started" with occurredAt = "not-a-date" }
                     box "timestamp" |]
            yield [| box "a blank sessionId is rejected"
                     box { baseReq "turn_started" with sessionId = "  " }
                     box "sessionId" |]
            yield [| box "an oversized sessionId is rejected"
                     box { baseReq "turn_started" with sessionId = String('a', maxSessionIdLength + 1) }
                     box (string maxSessionIdLength) |]
            yield [| box "a blank eventId is rejected"
                     box { baseReq "turn_started" with eventId = "" }
                     box "eventId" |]
            yield [| box "a blank worktreePath is rejected"
                     box { baseReq "turn_started" with worktreePath = "" }
                     box "worktreePath" |]
        }

    [<TestCaseSource("RequiredFieldRejectionCases")>]
    member _.``an invalid report is rejected with a diagnostic fragment``
        (name: string, req: SessionActivityRequest, expectedFragment: string)
        =
        Assert.That(parseErr req, Does.Contain expectedFragment, name)

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

    static member SupportedSessionIdCases: string seq =
        seq {
            "session-123"
            "018F7E43-251D-7DD2-BB7D-8949D7A5688A"
            "copilot.session_42:resume"
            String('a', maxSessionIdLength)
        }

    [<TestCaseSource("SupportedSessionIdCases")>]
    member _.``a supported resume sessionId is preserved``(sessionId: string) =
        let report =
            parseOk
                { baseReq "turn_started" with
                    sessionId = sessionId }

        Assert.That(report.SessionId, Is.EqualTo(SessionId sessionId))

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

/// The whole observable outcome of one ingested report sequence: the folded exact instance plus the
/// durable history it appended. Scenarios state it as data, so a sequence that disturbs a field it
/// should not touch fails the compare instead of slipping past a handful of assertions.
type InstanceProjection =
    { Status: SessionLevelStatus
      Effective: SessionLevelStatus
      Skill: string option
      Intent: Message option
      Title: Message option
      LastUser: Message option
      LastAssistant: Message option
      AwaitingSince: DateTimeOffset option
      InputCompletedAt: DateTimeOffset option
      Usage: ContextUsage option
      Clocks: Map<string, BackgroundAgentLifecycle>
      UpdatedAt: DateTimeOffset
      LastSeen: DateTimeOffset
      Events: string list }

/// Acknowledged presence, then a report sequence submitted in the listed — deliberately not always
/// chronological — order.
type IngestScenario =
    { Name: string
      PresentAt: string
      Reports: (string * string * SessionEvent) list
      Expected: InstanceProjection }

let private projectionOf (instance: StoredInstance) events =
    { Status = instance.Status.Status
      Effective = effectiveStatus instance.Status
      Skill = instance.Status.Skill
      Intent = instance.Status.Intent
      Title = instance.Status.Title
      LastUser = instance.Status.LastUserMessage
      LastAssistant = instance.Status.LastAssistantMessage
      AwaitingSince = instance.Status.AwaitingUserSince
      InputCompletedAt = instance.Status.UserInputCompletedAt
      Usage = instance.Status.ContextUsage
      Clocks = instance.Status.BackgroundAgentClocks
      UpdatedAt = instance.UpdatedAt
      LastSeen = instance.LastSeen
      Events = events }

/// The projection of a session that only acknowledged presence: an Idle shell with no content, no
/// clocks and no history. Each scenario overrides exactly the fields its sequence changes.
let private projected updatedAt lastSeen =
    { Status = SessionLevelStatus.Idle
      Effective = SessionLevelStatus.Idle
      Skill = None
      Intent = None
      Title = None
      LastUser = None
      LastAssistant = None
      AwaitingSince = None
      InputCompletedAt = None
      Usage = None
      Clocks = Map.empty
      UpdatedAt = ts updatedAt
      LastSeen = ts lastSeen
      Events = [] }

let private bgStarted eventId occurredAt toolCallId =
    eventId, occurredAt, BackgroundAgentStarted(toolCallId, ts occurredAt)

let private bgFinished eventId occurredAt toolCallId =
    eventId, occurredAt, BackgroundAgentFinished(toolCallId, ts occurredAt)

let private clocks entries =
    entries
    |> List.map (fun (toolCallId, startedAt, finishedAt) ->
        toolCallId,
        { StartedAt = startedAt |> Option.map ts
          FinishedAt = finishedAt |> Option.map ts })
    |> Map.ofList

let private usageOf currentTokens tokenLimit =
    { CurrentTokens = currentTokens; TokenLimit = tokenLimit }

/// Sequences that differ only by event kind, order and timing. Each names the ordering or
/// idempotency rule it proves; the shared runner asserts the projection, the durable mirror and the
/// scheduler feed together.
let private ingestSequences =
    [ { Name = "turn_started makes the session Working"
        PresentAt = "2026-03-01T10:00:00Z"
        Reports = [ "e1", "2026-03-01T10:00:00Z", TurnStarted ]
        Expected =
          { projected "2026-03-01T10:00:00Z" "2026-03-01T10:00:00Z" with
              Status = SessionLevelStatus.Working
              Effective = SessionLevelStatus.Working
              Events = [ "e1" ] } }

      { Name = "a folded sequence surfaces the last user and assistant message"
        PresentAt = "2026-03-01T10:00:00Z"
        Reports =
          [ "e1", "2026-03-01T10:00:00Z", TurnStarted
            "e2", "2026-03-01T10:00:01Z", UserPrompt(msg "do it" "2026-03-01T10:00:01Z")
            "e3", "2026-03-01T10:00:02Z", AssistantMessage(msg "on it" "2026-03-01T10:00:02Z") ]
        Expected =
          { projected "2026-03-01T10:00:02Z" "2026-03-01T10:00:00Z" with
              Status = SessionLevelStatus.Working
              Effective = SessionLevelStatus.Working
              LastUser = Some(msg "do it" "2026-03-01T10:00:01Z")
              LastAssistant = Some(msg "on it" "2026-03-01T10:00:02Z")
              InputCompletedAt = Some(ts "2026-03-01T10:00:02Z")
              Events =
                [ "e1"
                  "e2"
                  "e3" ] } }

      { Name = "an earlier ask_user still parks the session after a newer idle"
        PresentAt = "2026-03-01T10:00:01Z"
        Reports =
          [ "e2", "2026-03-01T10:00:01Z", WentIdle
            "e1", "2026-03-01T10:00:00Z", AwaitingUserInput(None, ts "2026-03-01T10:00:00Z") ]
        Expected =
          { projected "2026-03-01T10:00:01Z" "2026-03-01T10:00:01Z" with
              Effective = SessionLevelStatus.WaitingForUser
              AwaitingSince = Some(ts "2026-03-01T10:00:00Z")
              Events =
                [ "e1"
                  "e2" ] } }

      { Name = "completing the ask_user wait settles the session Idle"
        PresentAt = "2026-03-01T10:00:01Z"
        Reports =
          [ "e2", "2026-03-01T10:00:01Z", WentIdle
            "e1", "2026-03-01T10:00:00Z", AwaitingUserInput(None, ts "2026-03-01T10:00:00Z")
            "e3", "2026-03-01T10:00:02Z", UserInputCompleted(ts "2026-03-01T10:00:02Z") ]
        Expected =
          { projected "2026-03-01T10:00:02Z" "2026-03-01T10:00:01Z" with
              AwaitingSince = Some(ts "2026-03-01T10:00:00Z")
              InputCompletedAt = Some(ts "2026-03-01T10:00:02Z")
              Events =
                [ "e1"
                  "e2"
                  "e3" ] } }

      { Name = "a background start keeps an Idle base with a Working effect"
        PresentAt = "2026-03-01T10:00:00Z"
        Reports = [ bgStarted "bg-start" "2026-03-01T10:00:00Z" "tool-1" ]
        Expected =
          { projected "2026-03-01T10:00:00Z" "2026-03-01T10:00:00Z" with
              Effective = SessionLevelStatus.Working
              Clocks = clocks [ "tool-1", Some "2026-03-01T10:00:00Z", None ]
              Events = [ "bg-start" ] } }

      { Name = "a terminal first stays inactive and an older late start cannot resurrect it"
        PresentAt = "2026-03-01T10:00:05Z"
        Reports =
          [ bgFinished "bg-finish" "2026-03-01T10:00:05Z" "tool-1"
            bgStarted "bg-start" "2026-03-01T10:00:04Z" "tool-1" ]
        Expected =
          { projected "2026-03-01T10:00:05Z" "2026-03-01T10:00:05Z" with
              Clocks =
                clocks [ "tool-1", Some "2026-03-01T10:00:04Z", Some "2026-03-01T10:00:05Z" ]
              Events =
                [ "bg-start"
                  "bg-finish" ] } }

      { Name = "an older terminal after a newer start cannot finish the active agent"
        PresentAt = "2026-03-01T10:00:06Z"
        Reports =
          [ bgStarted "bg-start" "2026-03-01T10:00:06Z" "tool-1"
            bgFinished "bg-finish" "2026-03-01T10:00:05Z" "tool-1" ]
        Expected =
          { projected "2026-03-01T10:00:06Z" "2026-03-01T10:00:06Z" with
              Effective = SessionLevelStatus.Working
              Clocks =
                clocks [ "tool-1", Some "2026-03-01T10:00:06Z", Some "2026-03-01T10:00:05Z" ]
              Events =
                [ "bg-finish"
                  "bg-start" ] } }

      { Name = "heartbeats expire completed clocks but keep active agents and reject older starts"
        PresentAt = "2026-03-01T10:00:00Z"
        Reports =
          [ bgStarted "completed-start" "2026-03-01T10:00:00Z" "completed"
            bgFinished "completed-finish" "2026-03-01T10:00:10Z" "completed"
            bgStarted "active-start" "2026-03-01T10:00:20Z" "active"
            "heartbeat-1", "2026-03-01T10:04:00Z", Heartbeat
            "heartbeat-2", "2026-03-01T10:06:00Z", Heartbeat
            bgStarted "expired-start" "2026-03-01T10:00:05Z" "completed" ]
        Expected =
          { projected "2026-03-01T10:00:20Z" "2026-03-01T10:06:00Z" with
              Effective = SessionLevelStatus.Working
              Clocks = clocks [ "active", Some "2026-03-01T10:00:20Z", None ]
              Events =
                [ "completed-start"
                  "completed-finish"
                  "active-start" ] } }

      { Name = "a delayed finish records event-time history without regressing newer root work"
        PresentAt = "2026-03-01T10:55:00Z"
        Reports =
          [ bgStarted "bg-start" "2026-03-01T10:55:00Z" "tool-1"
            "heartbeat", "2026-03-01T10:59:00Z", Heartbeat
            "root-start", "2026-03-01T11:00:00Z", TurnStarted
            "root-skill", "2026-03-01T11:00:01Z", SkillInvoked "review"
            bgFinished "bg-finish" "2026-03-01T10:56:00Z" "tool-1" ]
        Expected =
          { projected "2026-03-01T11:00:01Z" "2026-03-01T10:59:00Z" with
              Status = SessionLevelStatus.Working
              Effective = SessionLevelStatus.Working
              Skill = Some "review"
              Clocks =
                clocks [ "tool-1", Some "2026-03-01T10:55:00Z", Some "2026-03-01T10:56:00Z" ]
              Events =
                [ "bg-start"
                  "bg-finish"
                  "root-start"
                  "root-skill" ] } }

      { Name = "a duplicate background event id changes neither lifecycle nor history"
        PresentAt = "2026-03-01T10:00:00Z"
        Reports =
          [ bgStarted "same-event" "2026-03-01T10:00:00Z" "tool-1"
            bgFinished "same-event" "2026-03-01T10:10:00Z" "tool-1" ]
        Expected =
          { projected "2026-03-01T10:00:00Z" "2026-03-01T10:00:00Z" with
              Effective = SessionLevelStatus.Working
              Clocks = clocks [ "tool-1", Some "2026-03-01T10:00:00Z", None ]
              Events = [ "same-event" ] } }

      { Name = "a replayed event_id neither appends a row nor resurrects the earlier status"
        PresentAt = "2026-03-01T10:00:00Z"
        Reports =
          [ "e1", "2026-03-01T10:00:00Z", TurnStarted
            "e2", "2026-03-01T10:00:05Z", WentIdle
            "e1", "2026-03-01T10:00:00Z", TurnStarted ]
        Expected =
          { projected "2026-03-01T10:00:05Z" "2026-03-01T10:00:00Z" with
              Events =
                [ "e1"
                  "e2" ] } }

      { Name = "an out-of-order event is retained for idempotency but does not regress live state"
        PresentAt = "2026-03-01T10:00:05Z"
        Reports =
          [ "e2", "2026-03-01T10:00:05Z", TurnStarted
            "e1", "2026-03-01T10:00:00Z", AssistantMessage(msg "stale" "2026-03-01T10:00:00Z") ]
        Expected =
          { projected "2026-03-01T10:00:05Z" "2026-03-01T10:00:05Z" with
              Status = SessionLevelStatus.Working
              Effective = SessionLevelStatus.Working
              Events =
                [ "e1"
                  "e2" ] } }

      { Name = "an older title bootstrap cannot overwrite a newer live title"
        PresentAt = "2026-03-01T10:00:10Z"
        Reports =
          [ "e1", "2026-03-01T10:00:10Z", TitleReported(msg "New live title" "2026-03-01T10:00:10Z")
            "tb1", "2026-03-01T10:00:05Z", TitleBootstrap(msg "Old snapshot" "2026-03-01T10:00:05Z") ]
        Expected =
          { projected "2026-03-01T10:00:10Z" "2026-03-01T10:00:10Z" with
              Title = Some(msg "New live title" "2026-03-01T10:00:10Z")
              Events = [ "e1" ] } }

      { Name = "a newer intent arriving first does not block an older lifecycle transition"
        PresentAt = "2026-03-01T10:00:06Z"
        Reports =
          [ "i1", "2026-03-01T10:00:06Z", IntentReported(msg "Implementing the fix" "2026-03-01T10:00:06Z")
            "e1", "2026-03-01T10:00:05Z", TurnStarted ]
        Expected =
          { projected "2026-03-01T10:00:06Z" "2026-03-01T10:00:06Z" with
              Status = SessionLevelStatus.Working
              Effective = SessionLevelStatus.Working
              Intent = Some(msg "Implementing the fix" "2026-03-01T10:00:06Z")
              Events =
                [ "e1"
                  "i1" ] } }

      { Name = "a title arriving after a newer lifecycle event still updates the activity field"
        PresentAt = "2026-03-01T10:00:04Z"
        Reports =
          [ "t1", "2026-03-01T10:00:04Z", TitleReported(msg "Initial title" "2026-03-01T10:00:04Z")
            "e1", "2026-03-01T10:00:06Z", TurnStarted
            "t2", "2026-03-01T10:00:05Z", TitleReported(msg "Updated title" "2026-03-01T10:00:05Z") ]
        Expected =
          { projected "2026-03-01T10:00:06Z" "2026-03-01T10:00:04Z" with
              Status = SessionLevelStatus.Working
              Effective = SessionLevelStatus.Working
              Title = Some(msg "Updated title" "2026-03-01T10:00:05Z")
              Events =
                [ "t1"
                  "t2"
                  "e1" ] } }

      { Name = "a real event never regresses last_seen below a fresher heartbeat"
        PresentAt = "2026-03-01T10:00:00Z"
        Reports =
          [ "e1", "2026-03-01T10:00:00Z", AssistantMessage(msg "hi" "2026-03-01T10:00:00Z")
            "hb1", "2026-03-01T10:02:00Z", Heartbeat
            "e2", "2026-03-01T10:01:00Z", UserPrompt(msg "go" "2026-03-01T10:01:00Z") ]
        Expected =
          { projected "2026-03-01T10:01:00Z" "2026-03-01T10:02:00Z" with
              Status = SessionLevelStatus.Working
              Effective = SessionLevelStatus.Working
              LastUser = Some(msg "go" "2026-03-01T10:01:00Z")
              LastAssistant = Some(msg "hi" "2026-03-01T10:00:00Z")
              InputCompletedAt = Some(ts "2026-03-01T10:01:00Z")
              Events =
                [ "e1"
                  "e2" ] } }

      { Name = "a usage gauge records ContextUsage without moving the status clock or appending"
        PresentAt = "2026-03-01T10:00:00Z"
        Reports =
          [ "e1", "2026-03-01T10:00:00Z", TurnStarted
            "u1", "2026-03-01T10:00:05Z", UsageInfo(120000, 200000) ]
        Expected =
          { projected "2026-03-01T10:00:00Z" "2026-03-01T10:00:00Z" with
              Status = SessionLevelStatus.Working
              Effective = SessionLevelStatus.Working
              Usage = Some(usageOf 120000 200000)
              Events = [ "e1" ] } }

      { Name = "a later usage report does not block a slightly-earlier status transition"
        PresentAt = "2026-03-01T10:00:00Z"
        Reports =
          [ "e1", "2026-03-01T10:00:00Z", TurnStarted
            "u1", "2026-03-01T10:00:05Z", UsageInfo(120000, 200000)
            "e2", "2026-03-01T10:00:03Z", TurnEnded ]
        Expected =
          { projected "2026-03-01T10:00:03Z" "2026-03-01T10:00:00Z" with
              Usage = Some(usageOf 120000 200000)
              Events =
                [ "e1"
                  "e2" ] } }

      { Name = "a usage snapshot arriving after a newer status event is not discarded"
        PresentAt = "2026-03-01T10:00:00Z"
        Reports =
          [ "e1", "2026-03-01T10:00:00Z", TurnStarted
            "e2", "2026-03-01T10:00:05Z", TurnEnded
            "u1", "2026-03-01T10:00:03Z", UsageInfo(50000, 200000) ]
        Expected =
          { projected "2026-03-01T10:00:05Z" "2026-03-01T10:00:00Z" with
              Usage = Some(usageOf 50000 200000)
              Events =
                [ "e1"
                  "e2" ] } }

      { Name = "an out-of-order older usage snapshot does not clobber a fresher gauge"
        PresentAt = "2026-03-01T10:00:00Z"
        Reports =
          [ "e1", "2026-03-01T10:00:00Z", TurnStarted
            "u2", "2026-03-01T10:00:10Z", UsageInfo(150000, 200000)
            "u1", "2026-03-01T10:00:05Z", UsageInfo(80000, 200000) ]
        Expected =
          { projected "2026-03-01T10:00:00Z" "2026-03-01T10:00:00Z" with
              Status = SessionLevelStatus.Working
              Effective = SessionLevelStatus.Working
              Usage = Some(usageOf 150000 200000)
              Events = [ "e1" ] } } ]

/// A retained durable row the restart rebuild leaves out of the live map, plus the report that must
/// revive it. The two revival paths differ only in the report kind and whether the reporter
/// re-announces presence first.
type RehydrationScenario =
    { Name: string
      PresentAt: string option
      Report: string * string * SessionEvent
      ExpectedUsage: ContextUsage option }

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type IngestTests() =

    static member SequenceCases: TestCaseData seq =
        ingestSequences
        |> Seq.map (fun scenario -> TestCaseData(scenario).SetName(scenario.Name))

    static member RehydrationCases: TestCaseData seq =
        [ { Name = "a heartbeat rehydrates a retained durable session after restart"
            PresentAt = None
            Report = "hb1", "2026-03-01T10:30:00Z", Heartbeat
            ExpectedUsage = None }
          { Name = "usage rehydrates a retained durable session after restart"
            PresentAt = Some "2026-03-01T10:30:00Z"
            Report = "u1", "2026-03-01T10:30:00Z", UsageInfo(120000, 200000)
            ExpectedUsage = Some(usageOf 120000 200000) } ]
        |> Seq.map (fun scenario -> TestCaseData(scenario).SetName(scenario.Name))

    [<TestCaseSource("SequenceCases")>]
    member _.``an ingested report sequence projects the expected instance and history``
        (scenario: IngestScenario)
        =
        withServiceAndPath "C:/wt/a" (fun (svc, agent, store, dbPath) ->
            present svc "s1" "C:/wt/a" (ts scenario.PresentAt) |> ignore

            scenario.Reports
            |> List.iter (fun (eventId, occurredAt, event) ->
                svc.Submit(mkReport "s1" "C:/wt/a" eventId occurredAt event))

            let live = svc.LiveSnapshot() |> Map.find (SessionId "s1")

            Assert.Multiple(fun () ->
                Assert.That(projectionOf live (persistedEventIds dbPath), Is.EqualTo scenario.Expected)
                Assert.That(
                    store.StatusBySession(SessionId "s1"),
                    Is.EqualTo(Some live),
                    "the durable mirror holds the same exact instance"
                )
                Assert.That(
                    schedulerStatus agent "s1",
                    Is.EqualTo(Some live),
                    "the card path is fed the same exact instance"
                )))

    [<Test>]
    member _.``a history report without presence is dropped``() =
        withServiceAndPath "C:/wt/a" (fun (svc, _, _, dbPath) ->
            svc.Submit(mkReport "s1" "C:/wt/a" "e1" "2026-03-01T10:00:00Z" TurnStarted)
            Assert.That(svc.LiveSnapshot(), Is.Empty)
            Assert.That(eventCount dbPath, Is.Zero))

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
    member _.``the first history report publishes the authoritative worktree representative``() =
        withService "C:/wt/a" (fun (svc, _, store) ->
            let worktree = WorktreePath(PathUtils.normalizePath "C:/wt/a")
            present svc "s1" "C:/wt/a" (ts "2026-03-01T10:00:00Z") |> ignore
            svc.Submit(
                mkReport
                    "s1"
                    "C:/wt/a"
                    "bg-start"
                    "2026-03-01T10:00:00Z"
                    (BackgroundAgentStarted("tool-1", ts "2026-03-01T10:00:00Z")))
            svc.LiveSnapshot() |> ignore

            let persisted = store.StatusBySession(SessionId "s1") |> Option.get
            let retained = store.RetainedByWorktree() |> Map.find (WorktreePath.value worktree)

            Assert.Multiple(fun () ->
                Assert.That(
                    store.LatestSessionIdForWorktree worktree,
                    Is.EqualTo(Some(SessionId "s1"))
                )
                Assert.That(retained.SessionId, Is.EqualTo persisted.SessionId)
                Assert.That(retained.UpdatedAt, Is.EqualTo persisted.UpdatedAt)
                Assert.That(
                    retained.Status.BackgroundAgentClocks,
                    Is.Empty,
                    "the retained worktree representative carries no per-process clocks"
                )))

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
                    resumed.Status.BackgroundAgentClocks |> Map.containsKey "crashed-tool",
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
                        Map.ofList [ "tool-1", { StartedAt = Some startedAt; FinishedAt = None } ])
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
                Is.EqualTo(Map.ofList [ "tool-1", { StartedAt = Some now; FinishedAt = None } ])
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
            { instanceOf
                "s1"
                "C:/wt/a"
                { Status = SessionLevelStatus.Idle
                  Skill = Some "review"
                  Intent = Some(msg "reviewing the implementation" "2026-03-01T09:55:00Z")
                  Title = Some(msg "Review lifecycle integration" "2026-03-01T09:56:00Z")
                  LastUserMessage = Some(msg "review this" "2026-03-01T09:57:00Z")
                  LastAssistantMessage = Some(msg "on it" "2026-03-01T09:58:00Z")
                  ContextUsage = Some(usageOf 50000 200000)
                  AwaitingUserSince = None
                  UserInputCompletedAt = None
                  BackgroundAgentClocks = Map.empty }
                (ts "2026-03-01T09:59:00Z")
                (ts "2026-03-01T09:59:00Z") with
                ContextUsageAt = Some(ts "2026-03-01T09:58:30Z") }

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
                    Assert.That(
                        { live.Status with BackgroundAgentClocks = Map.empty },
                        Is.EqualTo parent.Status,
                        "a background start leaves every parent field untouched"
                    )
                    Assert.That(live.ContextUsageAt, Is.EqualTo parent.ContextUsageAt)
                    Assert.That(effectiveStatus live.Status, Is.EqualTo SessionLevelStatus.Working)
                    Assert.That(live.UpdatedAt, Is.EqualTo(ts "2026-03-01T10:00:00Z"))
                    Assert.That(live.LastSeen, Is.EqualTo parent.LastSeen)
                    Assert.That(persisted, Is.EqualTo live)
                    Assert.That(schedulerStatus agent "s1", Is.EqualTo(Some live))))

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
            instanceOf
                "s1"
                "C:/wt/a"
                { emptyStatus with
                    Status = SessionLevelStatus.Working
                    Skill = Some "review"
                    Intent = Some(msg "reviewing the fix" "2026-03-01T07:58:00Z")
                    Title = Some(msg "Old title" "2026-03-01T07:59:00Z")
                    LastUserMessage = Some(msg "resume this" "2026-03-01T07:58:30Z")
                    LastAssistantMessage = Some(msg "working on it" "2026-03-01T07:59:30Z") }
                (ts "2026-03-01T08:00:00Z")
                (ts "2026-03-01T08:00:00Z")

        withServiceSeededAndPath
            "C:/wt/a"
            (fun store -> store.UpsertStatus retained)
            (fun (svc, _, store, dbPath) ->
                let title = msg "Current metadata title" "2026-03-01T10:30:00Z"
                present svc "s1" "C:/wt/a" (ts "2026-03-01T10:30:00Z") |> ignore
                svc.Submit(mkReport "s1" "C:/wt/a" "tb1" "2026-03-01T10:30:00Z" (TitleBootstrap title))

                let hydrated = svc.LiveSnapshot() |> Map.find (SessionId "s1")
                Assert.Multiple(fun () ->
                    Assert.That(
                        hydrated.Status,
                        Is.EqualTo { retained.Status with Title = Some title },
                        "bootstrap replaces only the title on the retained fold"
                    )
                    Assert.That(hydrated.UpdatedAt, Is.EqualTo retained.UpdatedAt)
                    Assert.That(hydrated.LastSeen, Is.EqualTo(ts "2026-03-01T10:30:00Z")))

                let durable = store.StatusBySession(SessionId "s1") |> Option.get
                Assert.That(durable, Is.EqualTo hydrated, "mailbox and durable store must use the same hydrated row")
                Assert.That(eventCount dbPath, Is.Zero)

                svc.Submit(mkReport "s1" "C:/wt/a" "e1" "2026-03-01T10:31:00Z" WentIdle)
                let settled = svc.LiveSnapshot() |> Map.find (SessionId "s1")
                Assert.That(effectiveStatus settled.Status, Is.EqualTo SessionLevelStatus.Idle))

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
            let submitAndAssertOrigin report =
                svc.Submit report
                let live = svc.LiveSnapshot() |> Map.find (SessionId "s1")
                let persisted = store.StatusBySession(SessionId "s1") |> Option.get

                Assert.Multiple(fun () ->
                    Assert.That(live.TerminalSessionId, Is.EqualTo(Some terminalSessionId))
                    Assert.That(persisted.TerminalSessionId, Is.EqualTo(Some terminalSessionId)))

            { mkReport "s1" "C:/wt/a" "started" "2026-03-01T10:00:00Z" TurnStarted with
                TerminalSessionId = Some terminalSessionId }
            |> submitAndAssertOrigin

            [ mkReport "s1" "C:/wt/a" "heartbeat" "2026-03-01T10:01:00Z" Heartbeat
              mkReport "s1" "C:/wt/a" "usage" "2026-03-01T10:02:00Z" (UsageInfo(1000, 2000))
              mkReport
                  "s1"
                  "C:/wt/a"
                  "bootstrap"
                  "2026-03-01T10:03:00Z"
                  (TitleBootstrap(msg "Terminal session" "2026-03-01T10:03:00Z"))
              mkReport
                  "s1"
                  "C:/wt/a"
                  "intent"
                  "2026-03-01T10:04:00Z"
                  (IntentReported(msg "Preserve ownership" "2026-03-01T10:04:00Z"))
              mkReport "s1" "C:/wt/a" "ended" "2026-03-01T10:05:00Z" TurnEnded ]
            |> List.iter submitAndAssertOrigin)

    [<TestCaseSource("RehydrationCases")>]
    member _.``a retained session outside the restart window is revived by its next report``
        (scenario: RehydrationScenario)
        =
        let retained =
            instanceOf
                "s1"
                "C:/wt/a"
                { emptyStatus with
                    Status = SessionLevelStatus.WaitingForUser
                    LastAssistantMessage = Some(msg "Which option?" "2026-03-01T08:00:00Z") }
                (ts "2026-03-01T08:00:00Z")
                (ts "2026-03-01T08:00:00Z")

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

                scenario.PresentAt
                |> Option.iter (fun at -> present svc "s1" "C:/wt/a" (ts at) |> ignore)

                let eventId, occurredAt, event = scenario.Report
                svc.Submit(mkReport "s1" "C:/wt/a" eventId occurredAt event)
                let rehydrated = svc.LiveSnapshot() |> Map.find (SessionId "s1")

                Assert.Multiple(fun () ->
                    Assert.That(rehydrated.Status.Status, Is.EqualTo SessionLevelStatus.WaitingForUser)
                    Assert.That(
                        rehydrated.Status.LastAssistantMessage,
                        Is.EqualTo retained.Status.LastAssistantMessage
                    )
                    Assert.That(rehydrated.Status.ContextUsage, Is.EqualTo scenario.ExpectedUsage)
                    Assert.That(
                        rehydrated.ContextUsageAt,
                        Is.EqualTo(scenario.ExpectedUsage |> Option.map (fun _ -> ts occurredAt))
                    )
                    Assert.That(rehydrated.UpdatedAt, Is.EqualTo retained.UpdatedAt)
                    Assert.That(rehydrated.LastSeen, Is.EqualTo(ts occurredAt))
                    Assert.That(store.StatusBySession(SessionId "s1"), Is.EqualTo(Some rehydrated))
                    Assert.That(
                        schedulerStatus agent "s1",
                        Is.EqualTo(Some rehydrated),
                        "the rehydrated session is fed to the scheduler"
                    )))

    [<Test>]
    member _.``a heartbeat for a session with no prior event is ignored``() =
        withServiceAndPath "C:/wt/a" (fun (svc, _, _, dbPath) ->
            svc.Submit(mkReport "s1" "C:/wt/a" "hb1" "2026-03-01T10:00:00Z" Heartbeat)
            Assert.That(svc.LiveSnapshot().ContainsKey(SessionId "s1"), Is.False, "a heartbeat never creates a session")
            Assert.That(eventCount dbPath, Is.Zero))

    [<Test>]
    member _.``a usage_info for a session with no prior status is dropped``() =
        withServiceAndPath "C:/wt/a" (fun (svc, _, _, dbPath) ->
            svc.Submit(mkReport "s1" "C:/wt/a" "u1" "2026-03-01T10:00:00Z" (UsageInfo(120000, 200000)))
            Assert.That(svc.LiveSnapshot().ContainsKey(SessionId "s1"), Is.False, "a gauge never creates a session")
            Assert.That(eventCount dbPath, Is.Zero))

    [<Test>]
    member _.``usage recreates a pruned row from the retained live session``() =
        let now = DateTimeOffset.UtcNow
        let worktree = Path.Combine(Path.GetTempPath(), "treemon-pruned-context-worktree")
        let report eventId occurredAt event =
            { ParentProcessId = syntheticProcessIdForSessionId "s1"
              SessionId = SessionId "s1"
              TerminalSessionId = None
              WorktreePath = WorktreePath(PathUtils.normalizePath worktree)
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
            Assert.That(persisted.Status.ContextUsage, Is.EqualTo(Some(usageOf 90000 200000))))

// ── restart rebuild ───────────────────────────────────────────────────────────
[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type RestartRebuildTests() =

    [<Test>]
    member _.``Start rebuilds live status and context usage from the store and feeds the scheduler``() =
        let now = DateTimeOffset.UtcNow
        let worktree = Path.Combine(Path.GetTempPath(), "treemon-restart-worktree")
        let usage = usageOf 120000 200000
        let usageAt = now.AddSeconds(-30.0)
        let status = { emptyStatus with Status = SessionLevelStatus.Working; Skill = Some "investigate" }
        let seeded = storedWithUsage "s1" worktree status (now.AddMinutes(-1.0)) usage usageAt

        withServiceSeeded
            worktree
            (fun store -> store.UpsertContextUsage seeded |> ignore)
            (fun (svc, agent, _) ->
                svc.Start()
                // The in-memory fold map is primed, so a later event folds onto the rebuilt state —
                // and the card path (scheduler) sees the rebuilt row before any new event arrives.
                let restored = svc.LiveSnapshot() |> Map.find (SessionId "s1")

                Assert.Multiple(fun () ->
                    Assert.That(restored, Is.EqualTo seeded)
                    Assert.That(schedulerStatus agent "s1", Is.EqualTo(Some seeded))))

    [<Test>]
    member _.``Start restores exact background lifecycle with the persisted base status``() =
        let now = DateTimeOffset.UtcNow
        let worktree = Path.Combine(Path.GetTempPath(), "treemon-restart-background-worktree")
        let status = fold emptyStatus (BackgroundAgentStarted("tool-1", now.AddSeconds(-45.0)))
        let seeded = instanceOf "s1" worktree status (now.AddMinutes(-1.0)) (now.AddSeconds(-30.0))

        withServiceSeeded worktree (fun store -> store.UpsertStatus seeded) (fun (svc, agent, _) ->
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
        let usage = usageOf 150000 200000
        let usageAt = now.AddMinutes(-1.0)
        let status = { emptyStatus with Status = SessionLevelStatus.Working }

        let seed (store: SessionActivityStore) =
            storedWithUsage "s1" worktree status (now.AddMinutes(-2.0)) usage usageAt
            |> store.UpsertContextUsage
            |> ignore

        withServiceSeeded worktree seed (fun (svc, _, _) ->
            svc.Start()
            svc.Submit(
                mkReport
                    "s1"
                    worktree
                    "older-usage"
                    (usageAt.AddSeconds(-30.0).ToString("O"))
                    (UsageInfo(80000, 200000)))

            let restored = svc.LiveSnapshot() |> Map.find (SessionId "s1")

            Assert.Multiple(fun () ->
                Assert.That(restored.Status.ContextUsage, Is.EqualTo(Some usage))
                Assert.That(restored.ContextUsageAt, Is.EqualTo(Some usageAt))))

    [<Test>]
    member _.``A status event revives all retained state outside the live restart window``() =
        let now = DateTimeOffset.UtcNow
        let worktree = Path.Combine(Path.GetTempPath(), "treemon-retained-context-worktree")
        let usage = usageOf 110000 200000
        let usageAt = now - idleWindow - TimeSpan.FromMinutes 5.0

        let status =
            { emptyStatus with
                Skill = Some "investigate"
                Intent = Some { Text = "diagnosing context persistence"; At = usageAt.AddMinutes(-4.0) }
                Title = Some { Text = "Persist context info"; At = usageAt.AddMinutes(-3.0) }
                LastUserMessage = Some { Text = "keep the context"; At = usageAt.AddMinutes(-2.0) }
                LastAssistantMessage = Some { Text = "working on it"; At = usageAt.AddMinutes(-1.0) } }

        let seed (store: SessionActivityStore) =
            storedWithUsage "s1" worktree status (usageAt.AddMinutes(-1.0)) usage usageAt
            |> store.UpsertContextUsage
            |> ignore

        withServiceSeeded worktree seed (fun (svc, agent, _) ->
            svc.Start()
            Assert.That(svc.LiveSnapshot().ContainsKey(SessionId "s1"), Is.False)

            svc.Submit(mkReport "s1" worktree "revive" (now.ToString("O")) TurnStarted)
            let revived = svc.LiveSnapshot() |> Map.find (SessionId "s1")

            Assert.Multiple(fun () ->
                Assert.That(
                    revived.Status,
                    Is.EqualTo
                        { status with
                            Status = SessionLevelStatus.Working
                            ContextUsage = Some usage },
                    "the revived fold keeps every retained field"
                )
                Assert.That(revived.ContextUsageAt, Is.EqualTo(Some usageAt))
                Assert.That(schedulerStatus agent "s1", Is.EqualTo(Some revived))))

    [<Test>]
    member _.``a session quiet longer than the idle window is not rebuilt as live``() =
        let quietAt = DateTimeOffset.UtcNow - idleWindow - TimeSpan.FromMinutes 5.0

        let seed (store: SessionActivityStore) =
            instanceOf
                "stale"
                "C:/wt/a"
                { emptyStatus with Status = SessionLevelStatus.Working }
                quietAt
                quietAt
            |> store.UpsertStatus

        withServiceSeeded "C:/wt/a" seed (fun (svc, _, _) ->
            svc.Start()
            Assert.That(svc.LiveSnapshot().ContainsKey(SessionId "stale"), Is.False))


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
        let now = ts "2026-03-01T10:05:00Z"
        let at clock = ts $"2026-03-01T{clock}Z"
        let message text clock = Some { Text = text; At = at clock }

        let stored terminalSessionId sessionId status updatedAt lastSeen intent title =
            { ownedStored terminalSessionId sessionId lastSeen with
                Status = { emptyStatus with Status = status; Intent = intent; Title = title }
                UpdatedAt = updatedAt }

        let tab terminalSessionId port =
            { Id = EmbeddedTerminalId(TerminalSessionId.value terminalSessionId)
              Worktree = WorktreePath "C:/wt/a"
              ReportedActivity = None
              Lifecycle = EmbeddedTerminalLifecycle.Running $"http://127.0.0.1:{port}/" }

        let closed =
            { stored terminalA "closed-a" SessionLevelStatus.Working (at "10:04:50") (at "10:04:50")
                  (message "Closed terminal activity" "10:04:50") None with
                ClosedAt = Some(at "10:04:55") }

        let decorated =
            { Tabs = [ tab terminalA 61001; tab terminalB 61002 ] }
            |> withReportedActivity
                now
                [ stored terminalA "idle-a" SessionLevelStatus.Idle (at "10:03:00") (at "10:04:00")
                      (message "Idle terminal work" "10:03:00") None
                  stored terminalA "working-a" SessionLevelStatus.Working (at "10:02:00") (at "10:04:30")
                      (message "Implementing exact terminal titles" "10:04:30") None
                  stored terminalB "working-b" SessionLevelStatus.Working (at "10:04:00") (at "10:04:30")
                      None (message "Session title only" "10:04:00")
                  closed
                  stored terminalB "stale-b" SessionLevelStatus.Working (at "10:04:50")
                      (now - openWindow - TimeSpan.FromSeconds 1.0)
                      (message "Stale terminal activity" "10:04:50") None
                  stored terminalC "unrelated" SessionLevelStatus.Working (at "10:04:30") (at "10:04:30")
                      (message "Wrong terminal" "10:04:30") None ]

        Assert.That(
            decorated.Tabs |> List.map (fun tab -> tab.Id, tab.ReportedActivity),
            Is.EqualTo(
                [ EmbeddedTerminalId(TerminalSessionId.value terminalA),
                  Some "Implementing exact terminal titles"
                  EmbeddedTerminalId(TerminalSessionId.value terminalB), Some "Session title only" ]
            )
        )

    [<Test>]
    member _.``stale durable terminal history is not a replacement candidate``() =
        let oldTerminal = terminalA
        let freshTerminal = terminalB
        let at = ts "2026-03-01T12:01:00Z"

        let withOrigin terminalSessionId (report: SessionActivityReport) =
            { report with TerminalSessionId = Some terminalSessionId }

        withService "C:/wt/a" (fun (service, _, _) ->
            present service "old" "C:/wt/a" (ts "2026-03-01T10:00:00Z") |> ignore
            mkReport "old" "C:/wt/a" "old-event" "2026-03-01T10:00:00Z" TurnStarted
            |> withOrigin oldTerminal
            |> service.Submit

            present service "fresh" "C:/wt/a" at |> ignore
            mkReport "fresh" "C:/wt/a" "fresh-event" "2026-03-01T12:01:00Z" TurnStarted
            |> withOrigin freshTerminal
            |> service.Submit

            let live = service.LiveSnapshot()
            let retained = queryOwnedOk service at (Set.singleton oldTerminal)

            let _, shutdownTargets, resumeCommands =
                queryReplacementPlanOk service at [ replacementTerminal oldTerminal "C:/wt/a" ]
                |> requireReplacementReady

            Assert.Multiple(fun () ->
                Assert.That(live |> Map.keys |> Seq.toList, Is.EqualTo([ SessionId "fresh" ]))
                Assert.That(retained.OpenSessions, Is.Empty)
                Assert.That(shutdownTargets, Is.Empty)
                Assert.That(resumeCommands, Is.Empty)))

    [<Test>]
    member _.``epoch pruning keeps retained and current origins and never reuses sequence values``() =
        let current = terminalA
        let retained = terminalB
        let expired = terminalC

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
        let current = terminalA
        let expired = terminalB
        let oldAt = ts "2026-01-01T10:00:00Z"
        let now = oldAt + retentionPeriod + TimeSpan.FromDays 1.0

        withService "C:/wt/a" (fun (service, _, store) ->
            queryActivityOk service (Set.singleton current) |> ignore

            { mkReport "expired" "C:/wt/a" "expired-event" "2026-01-01T10:00:00Z" TurnStarted with
                TerminalSessionId = Some expired }
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
        let ownedTerminal = terminalA
        let plainTerminal = terminalB
        let ownedPath = "C:/wt/owned"
        let terminals =
            [ replacementTerminal ownedTerminal ownedPath
              replacementTerminal plainTerminal "C:/wt/plain" ]

        let snapshot: OwnedSessionSnapshot =
            { ActivityEpoch = 17L
              OpenSessions =
                [ openSession ownedTerminal "provider-owned-session" SessionLevelStatus.Idle ]
              PendingReconciliation = Set.empty
              ReplacementSessionIds =
                Map.ofList [ ownedTerminal, SessionId "provider-owned-session" ] }

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

        let expectedShutdownTarget: TerminalHostReplacement.ReplacementShutdownTarget =
            { TerminalSessionId = ownedTerminal
              WorktreePath = ownedPath
              CopilotSessionId = SessionId "provider-owned-session"
              ProcessIdentity = syntheticProcessIdentityForSessionId "provider-owned-session" }

        Assert.Multiple(fun () ->
            Assert.That(epoch, Is.EqualTo snapshot.ActivityEpoch)
            Assert.That(shutdownTargets, Is.EqualTo([ expectedShutdownTarget ]))
            Assert.That(
                commands,
                Is.EqualTo(resumeCommandsFor [ ownedTerminal, "provider-owned-session" ]),
                "the unrelated terminal remains a plain shell"
            ))

    [<Test>]
    member _.``one terminal keeps every exact shutdown target but selects one resume conversation``() =
        let terminal = terminalA
        let now = ts "2026-03-01T10:05:00Z"
        let older =
            { ownedStored terminal "older-conversation" now with
                UpdatedAt = now.AddMinutes(-2.0) }
        let newer =
            { ownedStored terminal "newer-conversation" now with
                UpdatedAt = now.AddMinutes(-1.0) }

        let snapshot =
            ownedSessionSnapshot now (Set.singleton terminal) (19L, [ older; newer ], Set.empty)

        let _, shutdownTargets, resumeCommands =
            replacementSessionPlan
                (fun _ -> Some CopilotCli)
                [ replacementTerminal terminal "C:/wt/a" ]
                snapshot
            |> requireReplacementReady

        let bothProcesses = Set.ofList [ older.ProcessIdentity; newer.ProcessIdentity ]

        Assert.Multiple(fun () ->
            Assert.That(
                snapshot.OpenSessions |> List.map _.ProcessIdentity |> Set.ofList,
                Is.EqualTo bothProcesses,
                "every physical process remains an exact shutdown target"
            )
            Assert.That(
                snapshot.ReplacementSessionIds,
                Is.EqualTo(Map.ofList [ terminal, SessionId "newer-conversation" ]),
                "only the greatest-activity conversation is selected for automatic Resume"
            )
            Assert.That(
                shutdownTargets |> List.map _.ProcessIdentity |> Set.ofList,
                Is.EqualTo bothProcesses,
                "both physical processes must be returned as independent shutdown targets"
            )
            Assert.That(
                resumeCommands,
                Is.EqualTo(resumeCommandsFor [ terminal, "newer-conversation" ]),
                "only the selected durable conversation receives an automatic Resume command"
            ))

    [<Test>]
    member _.``replacement queries per-instance reconciliation immediately without a global startup delay``() =
        let now = ts "2026-03-01T10:00:00Z"
        let terminal = replacementTerminal terminalA "C:/wt/a"

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
                    Ok(TerminalHostReplacement.ReplacementSessionPlan.Ready(7L, [], Map.empty))
                    : Result<TerminalHostReplacement.ReplacementSessionPlan, string>
                )
            ))

    [<Test>]
    member _.``fresh waiting session gates until input completes``() =
        let terminalSessionId = terminalA
        let worktreePath = "C:/wt/a"
        let awaitingAt = ts "2026-03-01T10:00:00Z"
        let completedAt = ts "2026-03-01T10:01:00Z"
        let now = ts "2026-03-01T10:02:30Z"
        let waiting =
            { ownedStored terminalSessionId "waiting" awaitingAt with
                Status = fold emptyStatus (AwaitingUserInput(None, awaitingAt)) }

        let newerIdle =
            ownedStored terminalSessionId "newer-idle" (ts "2026-03-01T10:02:00Z")

        let planFor sessions =
            ownedSessionSnapshot now (Set.singleton terminalSessionId) (31L, sessions, Set.empty)
            |> fun snapshot ->
                snapshot,
                replacementSessionPlan
                    (fun _ -> Some CopilotCli)
                    [ replacementTerminal terminalSessionId worktreePath ]
                    snapshot

        let _, waitingPlan = planFor [ waiting; newerIdle ]

        Assert.That(
            waitingPlan,
            Is.EqualTo TerminalHostReplacement.ReplacementSessionPlan.WaitingForIdle,
            "most-recent selection applies to the live resume identity, not to the all-session idle gate"
        )

        let completed =
            { waiting with
                Status = fold waiting.Status (UserInputCompleted completedAt)
                UpdatedAt = completedAt
                LastSeen = completedAt }

        let completedSnapshot, completedPlan = planFor [ completed; newerIdle ]
        let epoch, shutdownTargets, resumeCommands = requireReplacementReady completedPlan

        Assert.Multiple(fun () ->
            Assert.That(epoch, Is.EqualTo completedSnapshot.ActivityEpoch)
            Assert.That(shutdownTargets.Length, Is.EqualTo(2))
            Assert.That(
                resumeCommands,
                Is.EqualTo(resumeCommandsFor [ terminalSessionId, "newer-idle" ])
            ))

    [<Test>]
    member _.``only exact current terminal origins join and advance their activity epoch``() =
        let now = ts "2026-03-01T10:00:30Z"
        let replacementTarget = replacementTerminal terminalA "C:/wt/a"

        let withOrigin terminalSessionId (report: SessionActivityReport) =
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
                    Is.EqualTo([ openSession terminalA "owned" SessionLevelStatus.Working ])
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
                    shutdownTargets |> List.map _.ProcessIdentity,
                    Is.EqualTo(idle.OpenSessions |> List.map _.ProcessIdentity)
                )
                Assert.That(
                    resumeCommands,
                    Is.EqualTo(resumeCommandsFor [ terminalA, "owned" ]),
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
                    Is.EqualTo([ openSession terminalA "owned" SessionLevelStatus.Idle ]),
                    "an omitted origin keeps the session attached to its exact terminal"
                )
                Assert.That(
                    retained.ReplacementSessionIds,
                    Is.EqualTo(Map.ofList [ terminalA, SessionId "owned" ])
                )
                Assert.That(retainedEpoch, Is.EqualTo retained.ActivityEpoch)
                Assert.That(
                    retainedTargets |> List.map _.ProcessIdentity,
                    Is.EqualTo(retained.OpenSessions |> List.map _.ProcessIdentity)
                )
                Assert.That(retainedCommands, Is.EqualTo(resumeCommandsFor [ terminalA, "owned" ]))))

    [<Test>]
    member _.``startup reconciliation lets a surviving session reassert before replacement``() =
        let terminalSessionId = terminalC
        let now = DateTimeOffset.UtcNow
        let worktree = Path.Combine(Path.GetTempPath(), "treemon-owned-resume-worktree")
        let replacementTarget = replacementTerminal terminalSessionId worktree

        let seed (store: SessionActivityStore) =
            { instanceOf
                "surviving"
                worktree
                { emptyStatus with Status = SessionLevelStatus.Idle }
                (now.AddMinutes(-1.0))
                (now.AddMinutes(-1.0)) with
                TerminalSessionId = Some terminalSessionId }
            |> store.UpsertStatus

        withServiceSeeded worktree seed (fun (service, _, _) ->
            service.Start()
            Assert.That(service.ExactSnapshot().Count, Is.EqualTo 1)
            Assert.That(
                queryReplacementPlanOk service now [ replacementTarget ],
                Is.EqualTo TerminalHostReplacement.ReplacementSessionPlan.WaitingForIdle
            )

            let representedAt = now.AddMinutes(1.0)

            let presence =
                { mkReport
                    "surviving"
                    worktree
                    "surviving-presence"
                    (representedAt.ToString("O"))
                    SessionPresent with
                    TerminalSessionId = Some terminalSessionId }

            match service.Present(presence, representedAt) with
            | PresenceAcknowledge.Recorded _ -> ()
            | PresenceAcknowledge.NotRecorded(_, reason) -> Assert.Fail reason

            Assert.That(
                service.LiveSnapshot() |> Map.keys |> Seq.toList,
                Is.EqualTo([ SessionId "surviving" ])
            )

            let snapshot =
                queryOwnedOk service representedAt (Set.singleton terminalSessionId)

            let policyEpoch, shutdownTargets, resumeCommands =
                queryReplacementPlanOk service representedAt [ replacementTarget ]
                |> requireReplacementReady

            Assert.Multiple(fun () ->
                Assert.That(snapshot.ActivityEpoch, Is.GreaterThan 0L)
                Assert.That(
                    snapshot.OpenSessions,
                    Is.EqualTo([ openSession terminalSessionId "surviving" SessionLevelStatus.Idle ])
                )
                Assert.That(
                    snapshot.ReplacementSessionIds,
                    Is.EqualTo(Map.ofList [ terminalSessionId, SessionId "surviving" ])
                )
                Assert.That(policyEpoch, Is.EqualTo snapshot.ActivityEpoch)
                Assert.That(
                    shutdownTargets |> List.map _.ProcessIdentity,
                    Is.EqualTo(snapshot.OpenSessions |> List.map _.ProcessIdentity)
                )
                Assert.That(
                    resumeCommands,
                    Is.EqualTo(resumeCommandsFor [ terminalSessionId, "surviving" ])
                )))
