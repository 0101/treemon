module Tests.SessionActivityServiceTests

open System
open System.IO
open System.Globalization
open NUnit.Framework
open Shared
open Server
open Server.SessionActivity
open Server.SessionActivityStore
open Server.SessionActivityService
open Tests.TestUtils

// Covers the ingestion layer of the push status model: the wire-contract DTO → domain parse (the
// seven kinds, unknown rejected, per-kind message/skill rules), the known-worktree guard
// (tryAcceptReport), and the single-writer mailbox flow (fold → append/dedupe → last-write-wins
// upsert → feed RefreshScheduler), plus the restart rebuild from the store. Fast/in-process — no
// HTTP; the handler is a thin wrapper over these tested seams (its known-worktree guard is exactly
// the CanvasDocServer pattern, tested there too).

let private ts (s: string) = DateTimeOffset.Parse(s, CultureInfo.InvariantCulture)
let private msg text t : Message = { Text = text; At = ts t }

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
      skillName = null }

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

/// A service over a throwaway temp .db, with `knownWorktree` registered as a monitored path on a
/// fresh scheduler agent. `seed` runs against the store before the service is constructed (used by
/// the restart-rebuild test). Disposing the service disposes the store; the dir is then removed.
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
            Assert.That((live |> Map.find (SessionId "s1")).Status.Status, Is.EqualTo Working))

    [<Test>]
    member _.``an ingested status is fed to the scheduler``() =
        withService "C:/wt/a" (fun (svc, agent, _) ->
            svc.Submit(mkReport "s1" "C:/wt/a" "e1" "2026-03-01T10:00:00Z" TurnStarted)
            svc.LiveSnapshot() |> ignore // barrier: the mailbox has posted UpdateSessionStatus by now
            match schedulerStatus agent "s1" with
            | Some stored -> Assert.That(stored.Status.Status, Is.EqualTo Working)
            | None -> Assert.Fail "scheduler never received the session status")

    [<Test>]
    member _.``a folded sequence surfaces the last user + assistant message and status``() =
        withService "C:/wt/a" (fun (svc, _, _) ->
            svc.Submit(mkReport "s1" "C:/wt/a" "e1" "2026-03-01T10:00:00Z" TurnStarted)
            svc.Submit(mkReport "s1" "C:/wt/a" "e2" "2026-03-01T10:00:01Z" (UserPrompt(msg "do it" "2026-03-01T10:00:01Z")))
            svc.Submit(mkReport "s1" "C:/wt/a" "e3" "2026-03-01T10:00:02Z" (AssistantMessage(msg "on it" "2026-03-01T10:00:02Z")))
            let s = (svc.LiveSnapshot() |> Map.find (SessionId "s1")).Status
            Assert.That(s.Status, Is.EqualTo Working)
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
            Assert.That((live |> Map.find (SessionId "s1")).Status.Status, Is.EqualTo Idle, "replay must not resurrect Working")
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
            Assert.That(s.Status, Is.EqualTo Working)
            Assert.That(s.LastAssistantMessage, Is.EqualTo None, "the stale message must not overwrite live state")
            // But the older event IS in the history substrate, and the store row keeps the newer updated_at.
            let events = store.QueryWindow(ts "2026-03-01T09:00:00Z", ts "2026-03-01T11:00:00Z")
            Assert.That(events.Length, Is.EqualTo 2)
            let stored = store.LoadLiveStatuses(ts "2026-03-01T10:05:00Z") |> List.find (fun s -> s.SessionId = SessionId "s1")
            Assert.That(stored.UpdatedAt, Is.EqualTo(ts "2026-03-01T10:00:05Z")))


// ── restart rebuild ───────────────────────────────────────────────────────────
[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type RestartRebuildTests() =

    [<Test>]
    member _.``Start rebuilds live status from the store and feeds the scheduler``() =
        let now = DateTimeOffset.UtcNow

        let seed (store: SessionActivityStore) =
            store.UpsertStatus
                { SessionId = SessionId "s1"
                  WorktreePath = WorktreePath "C:/wt/a"
                  Provider = CopilotCli
                  Status = { emptyStatus with Status = Working; Skill = Some "investigate" }
                  UpdatedAt = now.AddMinutes(-1.0)
                  LastSeen = now.AddMinutes(-1.0) }

        withServiceSeeded "C:/wt/a" seed (fun (svc, agent, _) ->
            svc.Start()
            // The in-memory fold map is primed, so a subsequent event folds onto the rebuilt state.
            let live = svc.LiveSnapshot()
            let restored = live |> Map.find (SessionId "s1")
            Assert.That(restored.Status.Status, Is.EqualTo Working)
            Assert.That(restored.Status.Skill, Is.EqualTo(Some "investigate"))
            // And the card path (scheduler) sees it immediately, before any new event.
            match schedulerStatus agent "s1" with
            | Some stored -> Assert.That(stored.Status.Skill, Is.EqualTo(Some "investigate"))
            | None -> Assert.Fail "restart rebuild did not feed the scheduler")

    [<Test>]
    member _.``a session quiet longer than the idle window is not rebuilt as live``() =
        let now = DateTimeOffset.UtcNow

        let seed (store: SessionActivityStore) =
            store.UpsertStatus
                { SessionId = SessionId "stale"
                  WorktreePath = WorktreePath "C:/wt/a"
                  Provider = CopilotCli
                  Status = { emptyStatus with Status = Working }
                  UpdatedAt = now - idleWindow - TimeSpan.FromMinutes 5.0
                  LastSeen = now - idleWindow - TimeSpan.FromMinutes 5.0 }

        withServiceSeeded "C:/wt/a" seed (fun (svc, _, _) ->
            svc.Start()
            Assert.That((svc.LiveSnapshot()).ContainsKey(SessionId "stale"), Is.False))
