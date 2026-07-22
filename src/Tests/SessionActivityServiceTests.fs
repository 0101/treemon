module Tests.SessionActivityServiceTests

open System
open System.IO
open NUnit.Framework
open Shared
open Server
open Server.SessionActivity
open Server.SessionActivityStore
open Server.SessionActivityService
open Tests.TestUtils

// Covers the ingestion layer of the push status model: the wire-contract DTO → domain parse (the
// closed kind set, unknown rejected, per-kind message/skill rules), the known-worktree guard
// (tryAcceptReport), and the single-writer mailbox flow (fold → append/dedupe → last-write-wins
// upsert → feed RefreshScheduler), plus the restart rebuild from the store. Fast/in-process — no
// HTTP; the handler is a thin wrapper over these tested seams (its known-worktree guard is exactly
// the CanvasDocServer pattern, tested there too).

/// Reference "now" for the pure parse tests — just after every baseReq occurredAt used below, so a
/// past/current occurredAt passes the future-skew clamp untouched.
let private refNow = ts "2026-03-01T10:05:00Z"

// --- DTO builders ------------------------------------------------------------------------------

let private noMsg: MessageDto = Unchecked.defaultof<MessageDto>
let private msgDto text at : MessageDto = { text = text; at = at }

let private baseReq kind : SessionActivityRequest =
    { sessionId = "s1"
      worktreePath = "C:/wt/a"
      provider = "copilot_cli"
      eventId = "e1"
      occurredAt = "2026-03-01T10:00:00Z"
      kind = kind
      message = noMsg
      skillName = null
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

// --- Service / store fixture -------------------------------------------------------------------

let private mkReport sid wt eid (t: string) ev : SessionActivityReport =
    { SessionId = SessionId sid
      WorktreePath = WorktreePath(PathUtils.normalizePath wt)
      Provider = CopilotCli
      EventId = EventId eid
      OccurredAt = ts t
      Event = ev }

let private storedWithUsage sid worktree status updatedAt usage usageAt =
    { SessionId = SessionId sid
      WorktreePath = WorktreePath(PathUtils.normalizePath worktree)
      Provider = CopilotCli
      Status = { status with ContextUsage = Some usage }
      UpdatedAt = updatedAt
      LastSeen = usageAt
      ContextUsageAt = Some usageAt }

/// A service over a throwaway temp .db, with `knownWorktree` registered as a monitored path on a
/// fresh scheduler agent. `seed` runs against the store before the service is constructed (used by
/// the restart-rebuild test). Program owns the shared store, so the fixture disposes it after the
/// service.
let private withServiceSeeded
    (knownWorktree: string)
    (seed: SessionActivityStore -> unit)
    (action: SessionActivityService * MailboxProcessor<RefreshScheduler.StateMsg> * SessionActivityStore -> unit)
    =
    let dir = Path.Combine(Path.GetTempPath(), $"treemon-svc-test-{Guid.NewGuid()}")
    Directory.CreateDirectory dir |> ignore
    let store = new SessionActivityStore(Path.Combine(dir, "activity.db"))
    seed store

    let agent = RefreshScheduler.createAgent ()

    let info: GitWorktree.WorktreeInfo =
        { Path = PathUtils.normalizePath knownWorktree
          Head = ""
          Branch = Some "test" }

    agent.Post(RefreshScheduler.UpdateWorktreeList(RepoId "svc-test-repo", [ info ]))

    let svc = new SessionActivityService(store, agent)

    try
        action (svc, agent, store)
    finally
        (svc :> IDisposable).Dispose()
        (store :> IDisposable).Dispose()
        try Directory.Delete(dir, true) with _ -> ()

let private withService knownWorktree action = withServiceSeeded knownWorktree ignore action

/// The scheduler's live status for a session (fed via UpdateSessionStatus). GetState is a barrier,
/// so calling it after a LiveSnapshot barrier guarantees the mailbox's feed has been applied.
let private schedulerStatus (agent: MailboxProcessor<RefreshScheduler.StateMsg>) sid =
    let state = agent.PostAndReply RefreshScheduler.GetState
    state.SessionStatuses |> Map.tryFind (SessionId sid)


// ── DTO → domain parse ────────────────────────────────────────────────────────
[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type ParseReportTests() =

    [<Test>]
    member _.``turn_started maps to TurnStarted``() =
        Assert.That((parseOk (baseReq "turn_started")).Event, Is.EqualTo TurnStarted)

    [<Test>]
    member _.``turn_ended maps to TurnEnded``() =
        Assert.That((parseOk (baseReq "turn_ended")).Event, Is.EqualTo TurnEnded)

    [<Test>]
    member _.``went_idle maps to WentIdle``() =
        Assert.That((parseOk (baseReq "went_idle")).Event, Is.EqualTo WentIdle)

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
        Assert.That((parseOk req).Event, Is.EqualTo(AwaitingUserInput(Some(msg "Which file?" "2026-03-01T10:00:00Z"))))

    [<Test>]
    member _.``awaiting_user_input with no message maps to AwaitingUserInput None``() =
        Assert.That((parseOk (baseReq "awaiting_user_input")).Event, Is.EqualTo(AwaitingUserInput None))

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
        | AwaitingUserInput(Some m) -> Assert.That(m.Text.Length, Is.EqualTo maxTextLength)
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
    member _.``an invalid body is rejected before the guard``() =
        withService "C:/wt/a" (fun (_, agent, _) ->
            match runAsync (tryAcceptReport agent (baseReq "bogus_kind")) with
            | Rejected reason -> Assert.That(reason, Does.Contain "unknown kind")
            | other -> Assert.Fail $"expected Rejected, got {other}")


// ── single-writer mailbox: fold → persist → feed ──────────────────────────────
[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type IngestTests() =

    [<Test>]
    member _.``ingesting turn_started makes the session Working in the live map``() =
        withService "C:/wt/a" (fun (svc, _, _) ->
            svc.Submit(mkReport "s1" "C:/wt/a" "e1" "2026-03-01T10:00:00Z" TurnStarted)
            let live = svc.LiveSnapshot()
            Assert.That((live |> Map.find (SessionId "s1")).Status.Status, Is.EqualTo SessionLevelStatus.Working))

    [<Test>]
    member _.``an ingested status is fed to the scheduler``() =
        withService "C:/wt/a" (fun (svc, agent, _) ->
            svc.Submit(mkReport "s1" "C:/wt/a" "e1" "2026-03-01T10:00:00Z" TurnStarted)
            svc.LiveSnapshot() |> ignore // barrier: the mailbox has posted UpdateSessionStatus by now
            match schedulerStatus agent "s1" with
            | Some stored -> Assert.That(stored.Status.Status, Is.EqualTo SessionLevelStatus.Working)
            | None -> Assert.Fail "scheduler never received the session status")

    [<Test>]
    member _.``a folded sequence surfaces the last user + assistant message and status``() =
        withService "C:/wt/a" (fun (svc, _, _) ->
            svc.Submit(mkReport "s1" "C:/wt/a" "e1" "2026-03-01T10:00:00Z" TurnStarted)
            svc.Submit(mkReport "s1" "C:/wt/a" "e2" "2026-03-01T10:00:01Z" (UserPrompt(msg "do it" "2026-03-01T10:00:01Z")))
            svc.Submit(mkReport "s1" "C:/wt/a" "e3" "2026-03-01T10:00:02Z" (AssistantMessage(msg "on it" "2026-03-01T10:00:02Z")))
            let s = (svc.LiveSnapshot() |> Map.find (SessionId "s1")).Status
            Assert.That(s.Status, Is.EqualTo SessionLevelStatus.Working)
            Assert.That(s.LastUserMessage, Is.EqualTo(Some(msg "do it" "2026-03-01T10:00:01Z")))
            Assert.That(s.LastAssistantMessage, Is.EqualTo(Some(msg "on it" "2026-03-01T10:00:02Z"))))

    [<Test>]
    member _.``ingested events are persisted to the durable mirror``() =
        withService "C:/wt/a" (fun (svc, _, store) ->
            svc.Submit(mkReport "s1" "C:/wt/a" "e1" "2026-03-01T10:00:00Z" TurnStarted)
            svc.LiveSnapshot() |> ignore
            let loaded = store.LoadLiveStatuses(ts "2026-03-01T10:05:00Z")
            Assert.That(loaded |> List.exists (fun s -> s.SessionId = SessionId "s1"), Is.True)
            let events = store.QueryWindow(ts "2026-03-01T09:00:00Z", ts "2026-03-01T11:00:00Z")
            Assert.That(events.Length, Is.EqualTo 1))

    [<Test>]
    member _.``a duplicate event_id is a no-op: no second event row, status unchanged``() =
        withService "C:/wt/a" (fun (svc, _, store) ->
            svc.Submit(mkReport "s1" "C:/wt/a" "e1" "2026-03-01T10:00:00Z" TurnStarted)
            svc.Submit(mkReport "s1" "C:/wt/a" "e2" "2026-03-01T10:00:05Z" WentIdle)
            svc.LiveSnapshot() |> ignore
            // Replay the first event verbatim.
            svc.Submit(mkReport "s1" "C:/wt/a" "e1" "2026-03-01T10:00:00Z" TurnStarted)
            let live = svc.LiveSnapshot()
            Assert.That((live |> Map.find (SessionId "s1")).Status.Status, Is.EqualTo SessionLevelStatus.Idle, "replay must not resurrect Working")
            let events = store.QueryWindow(ts "2026-03-01T09:00:00Z", ts "2026-03-01T11:00:00Z")
            Assert.That(events.Length, Is.EqualTo 2, "the duplicate event_id must be deduped"))

    [<Test>]
    member _.``an out-of-order (older) event is recorded in history but does not regress live state``() =
        withService "C:/wt/a" (fun (svc, _, store) ->
            svc.Submit(mkReport "s1" "C:/wt/a" "e2" "2026-03-01T10:00:05Z" TurnStarted)
            svc.LiveSnapshot() |> ignore
            // An older, distinct event arrives late.
            svc.Submit(mkReport "s1" "C:/wt/a" "e1" "2026-03-01T10:00:00Z" (AssistantMessage(msg "stale" "2026-03-01T10:00:00Z")))
            let s = (svc.LiveSnapshot() |> Map.find (SessionId "s1")).Status
            Assert.That(s.Status, Is.EqualTo SessionLevelStatus.Working)
            Assert.That(s.LastAssistantMessage, Is.EqualTo None, "the stale message must not overwrite live state")
            // But the older event IS in the history substrate, and the store row keeps the newer updated_at.
            let events = store.QueryWindow(ts "2026-03-01T09:00:00Z", ts "2026-03-01T11:00:00Z")
            Assert.That(events.Length, Is.EqualTo 2)
            let stored = store.LoadLiveStatuses(ts "2026-03-01T10:05:00Z") |> List.find (fun s -> s.SessionId = SessionId "s1")
            Assert.That(stored.UpdatedAt, Is.EqualTo(ts "2026-03-01T10:00:05Z")))

    [<Test>]
    member _.``title bootstrap persists without history and cannot block an earlier lifecycle event``() =
        withService "C:/wt/a" (fun (svc, _, store) ->
            let title = msg "Investigate Intent Title Runtime" "2026-03-01T10:00:05Z"
            svc.Submit(mkReport "s1" "C:/wt/a" "tb1" "2026-03-01T10:00:05Z" (TitleBootstrap title))

            let hydrated = svc.LiveSnapshot() |> Map.find (SessionId "s1")
            Assert.That(hydrated.Status.Title, Is.EqualTo(Some title))
            Assert.That(hydrated.UpdatedAt, Is.EqualTo(DateTimeOffset.MinValue), "bootstrap must not advance the lifecycle clock")
            Assert.That(hydrated.LastSeen, Is.EqualTo(ts "2026-03-01T10:00:05Z"), "a bootstrap-only session is retained durably")
            Assert.That(store.QueryWindow(ts "2026-03-01T09:00:00Z", ts "2026-03-01T11:00:00Z"), Is.Empty, "bootstrap is not source history")
            let durable = store.LoadLiveStatuses(ts "2026-03-01T09:00:00Z") |> List.find (fun s -> s.SessionId = SessionId "s1")
            Assert.That(durable.Status.Title, Is.EqualTo(Some title), "bootstrap title is persisted in session_status")

            // A replayed lifecycle event has an older SDK timestamp but must still apply after the
            // newer join-time hydration report.
            svc.Submit(mkReport "s1" "C:/wt/a" "e1" "2026-03-01T10:00:03Z" TurnStarted)
            let replayed = svc.LiveSnapshot() |> Map.find (SessionId "s1")
            Assert.That(replayed.Status.Status, Is.EqualTo SessionLevelStatus.Working)
            Assert.That(replayed.Status.Title, Is.EqualTo(Some title))
            Assert.That(replayed.UpdatedAt, Is.EqualTo(ts "2026-03-01T10:00:03Z"))
            Assert.That(replayed.LastSeen, Is.EqualTo(ts "2026-03-01T10:00:05Z"), "lifecycle replay must not regress join liveness")
            Assert.That(store.QueryWindow(ts "2026-03-01T09:00:00Z", ts "2026-03-01T11:00:00Z").Length, Is.EqualTo 1))

    [<Test>]
    member _.``title bootstrap revives a retained durable session without losing footer state``() =
        let retained =
            { SessionId = SessionId "s1"
              WorktreePath = WorktreePath(PathUtils.normalizePath "C:/wt/a")
              Provider = CopilotCli
              Status =
                { Status = SessionLevelStatus.Working
                  Skill = Some "review"
                  Intent = Some(msg "reviewing the fix" "2026-03-01T07:58:00Z")
                  Title = Some(msg "Old title" "2026-03-01T07:59:00Z")
                  LastUserMessage = Some(msg "resume this" "2026-03-01T07:58:30Z")
                  LastAssistantMessage = Some(msg "working on it" "2026-03-01T07:59:30Z")
                  ContextUsage = None }
              UpdatedAt = ts "2026-03-01T08:00:00Z"
              LastSeen = ts "2026-03-01T08:00:00Z"
              ContextUsageAt = None }

        withServiceSeeded
            "C:/wt/a"
            (fun store -> store.UpsertStatus retained)
            (fun (svc, _, store) ->
                let title = msg "Current metadata title" "2026-03-01T10:30:00Z"
                svc.Submit(mkReport "s1" "C:/wt/a" "tb1" "2026-03-01T10:30:00Z" (TitleBootstrap title))

                let hydrated = svc.LiveSnapshot() |> Map.find (SessionId "s1")
                Assert.Multiple(fun () ->
                    Assert.That(hydrated.Status.Status, Is.EqualTo SessionLevelStatus.Working)
                    Assert.That(hydrated.Status.Skill, Is.EqualTo(Some "review"))
                    Assert.That(hydrated.Status.Intent, Is.EqualTo retained.Status.Intent)
                    Assert.That(hydrated.Status.Title, Is.EqualTo(Some title))
                    Assert.That(hydrated.Status.LastUserMessage, Is.EqualTo retained.Status.LastUserMessage)
                    Assert.That(hydrated.Status.LastAssistantMessage, Is.EqualTo retained.Status.LastAssistantMessage)
                    Assert.That(hydrated.UpdatedAt, Is.EqualTo retained.UpdatedAt)
                    Assert.That(hydrated.LastSeen, Is.EqualTo(ts "2026-03-01T10:30:00Z")))

                let durable = store.StatusBySession(SessionId "s1") |> Option.get
                Assert.That(durable, Is.EqualTo hydrated, "mailbox and durable store must use the same hydrated row")
                Assert.That(store.QueryWindow(ts "2026-03-01T07:00:00Z", ts "2026-03-01T11:00:00Z"), Is.Empty))

    [<Test>]
    member _.``an older title bootstrap cannot overwrite a newer live title``() =
        withService "C:/wt/a" (fun (svc, _, store) ->
            let liveTitle = msg "New live title" "2026-03-01T10:00:10Z"
            let staleSnapshot = msg "Old snapshot" "2026-03-01T10:00:05Z"
            svc.Submit(mkReport "s1" "C:/wt/a" "e1" "2026-03-01T10:00:10Z" (TitleReported liveTitle))
            svc.Submit(mkReport "s1" "C:/wt/a" "tb1" "2026-03-01T10:00:05Z" (TitleBootstrap staleSnapshot))

            let s = svc.LiveSnapshot() |> Map.find (SessionId "s1")
            Assert.That(s.Status.Title, Is.EqualTo(Some liveTitle))
            Assert.That(s.UpdatedAt, Is.EqualTo DateTimeOffset.MinValue, "title reports do not advance the lifecycle clock")
            Assert.That(store.QueryWindow(ts "2026-03-01T09:00:00Z", ts "2026-03-01T11:00:00Z").Length, Is.EqualTo 1))

    [<Test>]
    member _.``a newer intent arriving first does not block an older lifecycle transition``() =
        withService "C:/wt/a" (fun (svc, _, store) ->
            let intent = msg "Implementing the fix" "2026-03-01T10:00:06Z"
            svc.Submit(mkReport "s1" "C:/wt/a" "i1" "2026-03-01T10:00:06Z" (IntentReported intent))
            svc.Submit(mkReport "s1" "C:/wt/a" "e1" "2026-03-01T10:00:05Z" TurnStarted)

            let live = svc.LiveSnapshot() |> Map.find (SessionId "s1")
            Assert.Multiple(fun () ->
                Assert.That(live.Status.Status, Is.EqualTo SessionLevelStatus.Working)
                Assert.That(live.Status.Intent, Is.EqualTo(Some intent))
                Assert.That(live.UpdatedAt, Is.EqualTo(ts "2026-03-01T10:00:05Z"), "intent must not advance the lifecycle clock")
                Assert.That(live.LastSeen, Is.EqualTo(ts "2026-03-01T10:00:06Z"), "the newer report still advances openness"))
            Assert.That(store.StatusBySession(SessionId "s1"), Is.EqualTo(Some live))
            Assert.That(store.QueryWindow(ts "2026-03-01T09:00:00Z", ts "2026-03-01T11:00:00Z").Length, Is.EqualTo 2))

    [<Test>]
    member _.``a title arriving after a newer lifecycle event still updates the activity field``() =
        withService "C:/wt/a" (fun (svc, _, store) ->
            let oldTitle = msg "Initial title" "2026-03-01T10:00:04Z"
            let newTitle = msg "Updated title" "2026-03-01T10:00:05Z"
            svc.Submit(mkReport "s1" "C:/wt/a" "t1" "2026-03-01T10:00:04Z" (TitleReported oldTitle))
            svc.Submit(mkReport "s1" "C:/wt/a" "e1" "2026-03-01T10:00:06Z" TurnStarted)
            svc.Submit(mkReport "s1" "C:/wt/a" "t2" "2026-03-01T10:00:05Z" (TitleReported newTitle))

            let live = svc.LiveSnapshot() |> Map.find (SessionId "s1")
            Assert.Multiple(fun () ->
                Assert.That(live.Status.Status, Is.EqualTo SessionLevelStatus.Working)
                Assert.That(live.Status.Title, Is.EqualTo(Some newTitle))
                Assert.That(live.UpdatedAt, Is.EqualTo(ts "2026-03-01T10:00:06Z"), "title must preserve the lifecycle clock")
                Assert.That(live.LastSeen, Is.EqualTo(ts "2026-03-01T10:00:06Z")))
            Assert.That(store.StatusBySession(SessionId "s1"), Is.EqualTo(Some live))
            Assert.That(store.QueryWindow(ts "2026-03-01T09:00:00Z", ts "2026-03-01T11:00:00Z").Length, Is.EqualTo 3))

    [<Test>]
    member _.``a heartbeat bumps last_seen for openness without appending, moving updated_at, or changing status``() =
        withService "C:/wt/a" (fun (svc, _, store) ->
            svc.Submit(mkReport "s1" "C:/wt/a" "e1" "2026-03-01T10:00:00Z" (AssistantMessage(msg "hi" "2026-03-01T10:00:00Z")))
            svc.LiveSnapshot() |> ignore
            // A later liveness heartbeat: newer timestamp, but pure openness — not a status event.
            svc.Submit(mkReport "s1" "C:/wt/a" "hb1" "2026-03-01T10:01:00Z" Heartbeat)
            let s = svc.LiveSnapshot() |> Map.find (SessionId "s1")
            Assert.That(s.LastSeen, Is.EqualTo(ts "2026-03-01T10:01:00Z"), "heartbeat advances last_seen")
            Assert.That(s.UpdatedAt, Is.EqualTo(ts "2026-03-01T10:00:00Z"), "heartbeat must not move the last-write-wins clock")
            Assert.That(s.Status.Status, Is.EqualTo SessionLevelStatus.Working, "heartbeat preserves status")
            Assert.That(s.Status.LastAssistantMessage, Is.EqualTo(Some(msg "hi" "2026-03-01T10:00:00Z")), "heartbeat preserves content")
            // No synthetic row appended to the history stream (only the one real event is there).
            let events = store.QueryWindow(ts "2026-03-01T09:00:00Z", ts "2026-03-01T11:00:00Z")
            Assert.That(events.Length, Is.EqualTo 1, "a heartbeat must not append to activity_events")
            let liveness = store.QueryLiveness(ts "2026-03-01T09:00:00Z", ts "2026-03-01T11:00:00Z")
            Assert.That(liveness, Is.EqualTo [ SessionId "s1", ts "2026-03-01T10:01:00Z" ])
            let historyEvents = store.QueryHistoryWindow(ts "2026-03-01T09:00:00Z", ts "2026-03-01T10:03:00Z")
            let history =
                OverviewHistory.sample
                    (ts "2026-03-01T10:03:00Z")
                    (TimeSpan.FromHours 1.0)
                    []
                    historyEvents
                    liveness
            let expected : OverviewData.AgentCount list =
                [ { Kind = OverviewData.AgentGroupKind.Activity CurrentActivity.Working
                    Count = 1 } ]
            Assert.That(
                history |> List.last |> _.Agents,
                Is.EqualTo expected,
                "the heartbeat keeps the historical agent open past the original event's openWindow"
            )
            // The durable row's last_seen was bumped, its updated_at left intact.
            let stored = store.LoadLiveStatuses(ts "2026-03-01T10:05:00Z") |> List.find (fun r -> r.SessionId = SessionId "s1")
            Assert.That(stored.LastSeen, Is.EqualTo(ts "2026-03-01T10:01:00Z"))
            Assert.That(stored.UpdatedAt, Is.EqualTo(ts "2026-03-01T10:00:00Z")))

    [<Test>]
    member _.``a heartbeat rehydrates a retained durable session after restart``() =
        let retained =
            { SessionId = SessionId "s1"
              WorktreePath = WorktreePath(PathUtils.normalizePath "C:/wt/a")
              Provider = CopilotCli
              Status =
                { emptyStatus with
                    Status = SessionLevelStatus.WaitingForUser
                    LastAssistantMessage = Some(msg "Which option?" "2026-03-01T08:00:00Z") }
              UpdatedAt = ts "2026-03-01T08:00:00Z"
              LastSeen = ts "2026-03-01T08:00:00Z"
              ContextUsageAt = None }

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

                let liveness = store.QueryLiveness(ts "2026-03-01T10:00:00Z", ts "2026-03-01T11:00:00Z")
                Assert.That(liveness, Is.EqualTo [ SessionId "s1", ts "2026-03-01T10:30:00Z" ])
                Assert.That(store.StatusBySession(SessionId "s1"), Is.EqualTo(Some rehydrated))

                match schedulerStatus agent "s1" with
                | Some fed -> Assert.That(fed, Is.EqualTo rehydrated)
                | None -> Assert.Fail "the rehydrated session was not fed to the scheduler")

    [<Test>]
    member _.``a heartbeat for a session with no prior event is ignored``() =
        withService "C:/wt/a" (fun (svc, _, store) ->
            svc.Submit(mkReport "s1" "C:/wt/a" "hb1" "2026-03-01T10:00:00Z" Heartbeat)
            let live = svc.LiveSnapshot()
            Assert.That(live.ContainsKey(SessionId "s1"), Is.False, "a heartbeat never creates a session")
            let events = store.QueryWindow(ts "2026-03-01T09:00:00Z", ts "2026-03-01T11:00:00Z")
            Assert.That(events.Length, Is.EqualTo 0))

    [<Test>]
    member _.``a real event never regresses last_seen below a fresher heartbeat``() =
        withService "C:/wt/a" (fun (svc, _, store) ->
            // Establish the session, then a heartbeat advances openness to 10:02.
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
    member _.``an out-of-order event's history row records its own status, not the newest live status``() =
        withService "C:/wt/a" (fun (svc, _, store) ->
            // Newest applied: turn_ended -> Idle.
            svc.Submit(mkReport "s1" "C:/wt/a" "e2" "2026-03-01T10:00:05Z" TurnEnded)
            svc.LiveSnapshot() |> ignore
            // An older assistant_message arrives late; its OWN effect is Working, not the newest Idle.
            svc.Submit(mkReport "s1" "C:/wt/a" "e1" "2026-03-01T10:00:00Z" (AssistantMessage(msg "stale" "2026-03-01T10:00:00Z")))
            svc.LiveSnapshot() |> ignore
            let older =
                store.QueryWindow(ts "2026-03-01T09:00:00Z", ts "2026-03-01T11:00:00Z")
                |> List.find (fun r -> r.EventId = EventId "e1")
            Assert.That(older.Status, Is.EqualTo SessionLevelStatus.Working, "out-of-order row reflects the event's own effect, not the newest Idle"))

    [<Test>]
    member _.``a usage_info gauge updates ContextUsage without moving the status clock or appending history``() =
        withService "C:/wt/a" (fun (svc, agent, store) ->
            svc.Submit(mkReport "s1" "C:/wt/a" "e1" "2026-03-01T10:00:00Z" TurnStarted)
            svc.LiveSnapshot() |> ignore
            svc.Submit(mkReport "s1" "C:/wt/a" "u1" "2026-03-01T10:00:05Z" (UsageInfo(120000, 200000)))
            let s = svc.LiveSnapshot() |> Map.find (SessionId "s1")
            Assert.That(s.Status.ContextUsage, Is.EqualTo(Some { CurrentTokens = 120000; TokenLimit = 200000 }), "the gauge is recorded")
            Assert.That(s.Status.Status, Is.EqualTo SessionLevelStatus.Working, "a gauge never changes status")
            Assert.That(s.UpdatedAt, Is.EqualTo(ts "2026-03-01T10:00:00Z"), "a gauge must not move the status last-write-wins clock")
            Assert.That(s.LastSeen, Is.EqualTo(ts "2026-03-01T10:00:05Z"), "the gauge bumps openness")
            // No synthetic row appended (like a heartbeat) — only the one real status event is in history.
            let events = store.QueryWindow(ts "2026-03-01T09:00:00Z", ts "2026-03-01T11:00:00Z")
            Assert.That(events.Length, Is.EqualTo 1, "a usage_info must not append to activity_events")
            // The card path (scheduler) sees the gauge.
            match schedulerStatus agent "s1" with
            | Some fed -> Assert.That(fed.Status.ContextUsage, Is.EqualTo(Some { CurrentTokens = 120000; TokenLimit = 200000 }))
            | None -> Assert.Fail "the gauge was not fed to the scheduler")

    [<Test>]
    member _.``a later usage report does not block a slightly-earlier status transition``() =
        withService "C:/wt/a" (fun (svc, _, _) ->
            // The gauge (10:00:05) is NEWER than the turn_ended (10:00:03) but arrives first. Sharing the
            // status clock would reject the turn_ended as out-of-order and leave the card stuck Working.
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
            svc.Submit(mkReport "s1" "C:/wt/a" "e1" "2026-03-01T10:00:00Z" TurnStarted)
            svc.Submit(mkReport "s1" "C:/wt/a" "e2" "2026-03-01T10:00:05Z" TurnEnded)
            svc.Submit(mkReport "s1" "C:/wt/a" "u1" "2026-03-01T10:00:03Z" (UsageInfo(50000, 200000)))
            let s = (svc.LiveSnapshot() |> Map.find (SessionId "s1")).Status
            Assert.That(s.ContextUsage, Is.EqualTo(Some { CurrentTokens = 50000; TokenLimit = 200000 }), "the gauge survives a newer status event")
            Assert.That(s.Status, Is.EqualTo SessionLevelStatus.Idle, "the gauge never changes status"))

    [<Test>]
    member _.``an out-of-order older usage snapshot does not clobber a fresher gauge``() =
        withService "C:/wt/a" (fun (svc, _, _) ->
            svc.Submit(mkReport "s1" "C:/wt/a" "e1" "2026-03-01T10:00:00Z" TurnStarted)
            svc.Submit(mkReport "s1" "C:/wt/a" "u2" "2026-03-01T10:00:10Z" (UsageInfo(150000, 200000)))
            // A delayed OLDER snapshot arrives last; its own usage LWW clock rejects it.
            svc.Submit(mkReport "s1" "C:/wt/a" "u1" "2026-03-01T10:00:05Z" (UsageInfo(80000, 200000)))
            let s = (svc.LiveSnapshot() |> Map.find (SessionId "s1")).Status
            Assert.That(s.ContextUsage, Is.EqualTo(Some { CurrentTokens = 150000; TokenLimit = 200000 }), "the fresher gauge is kept"))

    [<Test>]
    member _.``a usage_info for a session with no prior status is dropped``() =
        withService "C:/wt/a" (fun (svc, _, store) ->
            svc.Submit(mkReport "s1" "C:/wt/a" "u1" "2026-03-01T10:00:00Z" (UsageInfo(120000, 200000)))
            let live = svc.LiveSnapshot()
            Assert.That(live.ContainsKey(SessionId "s1"), Is.False, "a gauge never creates a session")
            let events = store.QueryWindow(ts "2026-03-01T09:00:00Z", ts "2026-03-01T11:00:00Z")
            Assert.That(events.Length, Is.EqualTo 0))

    [<Test>]
    member _.``usage recreates a pruned row from the retained live session``() =
        let now = DateTimeOffset.UtcNow
        let worktree = Path.Combine(Path.GetTempPath(), "treemon-pruned-context-worktree")
        let normalizedWorktree = WorktreePath(PathUtils.normalizePath worktree)
        let report eventId occurredAt event =
            { SessionId = SessionId "s1"
              WorktreePath = normalizedWorktree
              Provider = CopilotCli
              EventId = EventId eventId
              OccurredAt = occurredAt
              Event = event }

        withService worktree (fun (svc, _, store) ->
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
                { SessionId = SessionId "s1"
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
              ContextUsage = None }

        let seed (store: SessionActivityStore) =
            storedWithUsage "s1" worktree status (usageAt.AddMinutes(-1.0)) usage usageAt
            |> store.UpsertContextUsage
            |> ignore

        withServiceSeeded worktree seed (fun (svc, agent, _) ->
            svc.Start()
            Assert.That((svc.LiveSnapshot()).ContainsKey(SessionId "s1"), Is.False)

            svc.Submit
                { SessionId = SessionId "s1"
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
                { SessionId = SessionId "stale"
                  WorktreePath = WorktreePath "C:/wt/a"
                  Provider = CopilotCli
                  Status = { emptyStatus with Status = SessionLevelStatus.Working }
                  UpdatedAt = now - idleWindow - TimeSpan.FromMinutes 5.0
                  LastSeen = now - idleWindow - TimeSpan.FromMinutes 5.0
                  ContextUsageAt = None }

        withServiceSeeded "C:/wt/a" seed (fun (svc, _, _) ->
            svc.Start()
            Assert.That((svc.LiveSnapshot()).ContainsKey(SessionId "stale"), Is.False))
