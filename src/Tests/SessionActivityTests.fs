module Tests.SessionActivityTests

open System
open NUnit.Framework
open Server.SessionActivity
open Shared
open Tests.TestUtils

// These port the CopilotDetector forward-fold tests onto the push-model `fold`. The old fold parsed
// JSONL and carried sub-agent depth + injection filtering; the new fold consumes already-classified
// `SessionEvent`s (the extension filters sub-agent/injection events at the source), so the ported
// tests build SessionEvent lists directly instead of JSONL. Sub-agent/injection scenarios are gone
// by construction — the fold has no branch for them — so those tests are intentionally not ported.

let private skillOf (events: SessionEvent list) =
    (foldMany emptyStatus events).Skill

/// The CurrentSkill equivalence scenarios from the old backward/forward fold, re-expressed on the new
/// event union (skill-context injections dropped — the extension never forwards them; the "skill tool
/// call" and "skill.invoked" distinction collapses to a single SkillInvoked event).
let private skillScenarios: (string * SessionEvent list * string option) list =
    [ "skill.invoked sets the running skill",
      [ UserPrompt(msg "please fix the build" "2026-03-01T10:00:00Z")
        SkillInvoked "fix-build" ],
      Some "fix-build"

      "most recent skill wins across multiple invocations",
      [ SkillInvoked "investigate"; SkillInvoked "bd-execute" ],
      Some "bd-execute"

      "session with no skill signal yields None",
      [ UserPrompt(msg "hello" "2026-03-01T10:00:00Z")
        AssistantMessage(msg "hi" "2026-03-01T10:00:01Z")
        TurnEnded ],
      None

      "a skill that finished before a new user request no longer lingers",
      [ UserPrompt(msg "plan the feature" "2026-03-01T10:00:00Z")
        SkillInvoked "bd-plan"
        AssistantMessage(msg "planning complete" "2026-03-01T10:00:02Z")
        TurnEnded
        UserPrompt(msg "now something unrelated" "2026-03-01T10:05:00Z")
        AssistantMessage(msg "on it" "2026-03-01T10:05:01Z") ],
      None

      "a running skill is reported past intervening assistant work",
      [ UserPrompt(msg "/bd-execute my-feature" "2026-03-01T10:00:00Z")
        SkillInvoked "bd-execute"
        AssistantMessage(msg "orchestrating subagents" "2026-03-01T10:00:04Z")
        TurnEnded ],
      Some "bd-execute"

      "a skill re-invoked after a new request is reported",
      [ SkillInvoked "bd-plan"
        TurnEnded
        UserPrompt(msg "please review the branch" "2026-03-01T10:05:00Z")
        SkillInvoked "review"
        AssistantMessage(msg "reviewing" "2026-03-01T10:05:02Z") ],
      Some "review"

      "a running skill is reported across an ask_user reply that resumes it",
      [ UserPrompt(msg "/review the changes" "2026-03-01T10:00:00Z")
        SkillInvoked "review"
        AssistantMessage(msg "let me look at the diff" "2026-03-01T10:00:04Z")
        AwaitingUserInput(Some(msg "which file should I focus on?" "2026-03-01T10:00:05Z"))
        UserPrompt(msg "the auth module" "2026-03-01T10:01:01Z")
        AssistantMessage(msg "reviewing the auth module" "2026-03-01T10:01:02Z")
        TurnEnded ],
      Some "review"

      "a genuine new request after ordinary work still ends the prior skill",
      [ UserPrompt(msg "/review the changes" "2026-03-01T10:00:00Z")
        SkillInvoked "review"
        AssistantMessage(msg "review complete" "2026-03-01T10:00:02Z")
        TurnEnded
        UserPrompt(msg "now bump the version" "2026-03-01T10:05:00Z")
        AssistantMessage(msg "on it" "2026-03-01T10:05:01Z") ],
      None

      "an unanswered ask_user mid-skill still reports the running skill",
      [ UserPrompt(msg "/investigate the flake" "2026-03-01T10:00:00Z")
        SkillInvoked "investigate"
        AssistantMessage(msg "digging in" "2026-03-01T10:00:03Z")
        AwaitingUserInput(Some(msg "can you share the failing run URL?" "2026-03-01T10:00:04Z")) ],
      Some "investigate" ]


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type FoldStatusTests() =

    [<Test>]
    member _.``TurnStarted yields Working``() =
        let s = fold emptyStatus TurnStarted
        Assert.That(s.Status, Is.EqualTo(SessionLevelStatus.Working))

    [<Test>]
    member _.``AssistantMessage yields Working and records the last assistant message``() =
        let m = msg "on it" "2026-03-01T10:00:00Z"
        let s = fold emptyStatus (AssistantMessage m)
        Assert.That(s.Status, Is.EqualTo(SessionLevelStatus.Working))
        Assert.That(s.LastAssistantMessage, Is.EqualTo(Some m))

    [<Test>]
    member _.``UserPrompt yields Working and records the last user message``() =
        let m = msg "please fix the build" "2026-03-01T10:00:00Z"
        let s = fold emptyStatus (UserPrompt m)
        Assert.That(s.Status, Is.EqualTo(SessionLevelStatus.Working))
        Assert.That(s.LastUserMessage, Is.EqualTo(Some m))

    [<Test>]
    member _.``AwaitingUserInput yields WaitingForUser``() =
        let s = fold emptyStatus (AwaitingUserInput None)
        Assert.That(s.Status, Is.EqualTo(SessionLevelStatus.WaitingForUser))

    [<Test>]
    member _.``AwaitingUserInput surfaces the question as the last assistant message``() =
        let q = msg "which file?" "2026-03-01T10:00:00Z"
        let s = fold emptyStatus (AwaitingUserInput(Some q))
        Assert.That(s.LastAssistantMessage, Is.EqualTo(Some q))

    [<Test>]
    member _.``AwaitingUserInput with no question keeps the prior assistant message``() =
        let prior = msg "let me look" "2026-03-01T10:00:00Z"
        let s = foldMany emptyStatus [ AssistantMessage prior; AwaitingUserInput None ]
        Assert.That(s.Status, Is.EqualTo(SessionLevelStatus.WaitingForUser))
        Assert.That(s.LastAssistantMessage, Is.EqualTo(Some prior))

    [<Test>]
    member _.``TurnEnded yields Idle``() =
        let s = foldMany emptyStatus [ TurnStarted; TurnEnded ]
        Assert.That(s.Status, Is.EqualTo(SessionLevelStatus.Idle))

    [<Test>]
    member _.``WentIdle yields Idle``() =
        let s = foldMany emptyStatus [ TurnStarted; WentIdle ]
        Assert.That(s.Status, Is.EqualTo(SessionLevelStatus.Idle))

    [<Test>]
    member _.``SkillInvoked does not change status``() =
        // skill.invoked sets the running skill only; the status is whatever the surrounding events say.
        let s = foldMany emptyStatus [ TurnStarted; SkillInvoked "fix-build" ]
        Assert.That(s.Status, Is.EqualTo(SessionLevelStatus.Working))
        Assert.That(s.Skill, Is.EqualTo(Some "fix-build"))


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type FoldSkillTests() =

    [<Test>]
    member _.``Skill matches the expected skill on every scenario``() =
        skillScenarios
        |> List.iter (fun (name, events, expected) ->
            Assert.That(skillOf events, Is.EqualTo(expected), $"skill mismatch: {name}"))

    [<Test>]
    member _.``An ask_user reply keeps the running skill and is recorded as the last user message``() =
        let events =
            [ UserPrompt(msg "/review the changes" "2026-03-01T10:00:00Z")
              SkillInvoked "review"
              AssistantMessage(msg "let me look at the diff" "2026-03-01T10:00:03Z")
              AwaitingUserInput(Some(msg "which file should I focus on?" "2026-03-01T10:00:04Z"))
              UserPrompt(msg "the auth module" "2026-03-01T10:01:01Z")
              AssistantMessage(msg "reviewing the auth module" "2026-03-01T10:01:02Z") ]

        let s = foldMany emptyStatus events
        Assert.That(s.Skill, Is.EqualTo(Some "review"))
        Assert.That(s.LastUserMessage |> Option.map _.Text, Is.EqualTo(Some "the auth module"))

    [<Test>]
    member _.``A new request after a completed turn ends the prior skill``() =
        // Status is Idle (not WaitingForUser) when the prompt arrives, so keepSkill is false.
        let events =
            [ SkillInvoked "review"
              AssistantMessage(msg "review complete" "2026-03-01T10:00:02Z")
              TurnEnded
              UserPrompt(msg "now bump the version" "2026-03-01T10:05:00Z") ]

        Assert.That((foldMany emptyStatus events).Skill, Is.EqualTo(None))

    [<Test>]
    member _.``Only a genuine user prompt is recorded as the last user message``() =
        // Every UserPrompt the fold sees is genuine (injections are filtered at the source), so the
        // last user message is always the last real prompt.
        let events =
            [ UserPrompt(msg "/bd-execute my-feature" "2026-03-01T10:00:00Z")
              SkillInvoked "bd-execute"
              AssistantMessage(msg "orchestrating" "2026-03-01T10:00:04Z")
              TurnEnded ]

        let s = foldMany emptyStatus events
        Assert.That(s.LastUserMessage |> Option.map _.Text, Is.EqualTo(Some "/bd-execute my-feature"))


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type FoldAppendTests() =

    [<Test>]
    member _.``Folding appended batches equals folding the whole stream at once``() =
        // The core append-friendliness guarantee: the ingestion path folds one event at a time onto
        // the cached per-session state, and that must equal a full re-fold of the whole stream.
        let events =
            [ UserPrompt(msg "/review the changes" "2026-03-01T10:00:00Z")
              SkillInvoked "review"
              AssistantMessage(msg "let me look at the diff" "2026-03-01T10:00:03Z")
              AwaitingUserInput(Some(msg "which file?" "2026-03-01T10:00:04Z"))
              UserPrompt(msg "the auth module" "2026-03-01T10:01:01Z")
              AssistantMessage(msg "reviewing the auth module" "2026-03-01T10:01:02Z")
              TurnEnded ]

        let whole = foldMany emptyStatus events

        let firstBatch, secondBatch = List.splitAt 3 events
        let incremental = foldMany (foldMany emptyStatus firstBatch) secondBatch

        Assert.That(incremental, Is.EqualTo(whole))

    [<Test>]
    member _.``The empty stream yields the empty status``() =
        let s = foldMany emptyStatus []
        Assert.That(s, Is.EqualTo(emptyStatus))
        Assert.That(s.Status, Is.EqualTo(SessionLevelStatus.Idle))
        Assert.That(s.Skill, Is.EqualTo(None))
        Assert.That(s.LastUserMessage, Is.EqualTo(None))
        Assert.That(s.LastAssistantMessage, Is.EqualTo(None))


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type FreshnessTests() =

    let now = ts "2026-03-01T12:00:00Z"
    let working = { emptyStatus with Status = SessionLevelStatus.Working }

    [<Test>]
    member _.``A non-Idle status older than the staleness timeout reads as Idle``() =
        let lastSeen = now - stalenessTimeout - TimeSpan.FromMinutes 1.0
        let adjusted = freshnessAdjusted now lastSeen working
        Assert.That(adjusted.Status, Is.EqualTo(SessionLevelStatus.Idle))

    [<Test>]
    member _.``A non-Idle status within the staleness timeout is unchanged``() =
        let lastSeen = now - TimeSpan.FromMinutes 1.0
        let adjusted = freshnessAdjusted now lastSeen working
        Assert.That(adjusted, Is.EqualTo(working))

    [<Test>]
    member _.``An already-Idle status is never resurrected by freshness``() =
        // WentIdle set Idle explicitly; freshness must leave it (and its other fields) untouched.
        let idle = { emptyStatus with Status = SessionLevelStatus.Idle; Skill = Some "review" }
        let lastSeen = now - TimeSpan.FromSeconds 1.0
        Assert.That(freshnessAdjusted now lastSeen idle, Is.EqualTo(idle))

    [<Test>]
    member _.``Freshness only rewrites Status, preserving skill and messages``() =
        let rich =
            { Status = SessionLevelStatus.WaitingForUser
              Skill = Some "review"
              LastUserMessage = Some(msg "the auth module" "2026-03-01T10:00:00Z")
              LastAssistantMessage = Some(msg "which file?" "2026-03-01T10:00:01Z") }
        let lastSeen = now - stalenessTimeout - TimeSpan.FromMinutes 1.0
        let adjusted = freshnessAdjusted now lastSeen rich
        Assert.That(adjusted.Status, Is.EqualTo(SessionLevelStatus.Idle))
        Assert.That(adjusted.Skill, Is.EqualTo(rich.Skill))
        Assert.That(adjusted.LastUserMessage, Is.EqualTo(rich.LastUserMessage))
        Assert.That(adjusted.LastAssistantMessage, Is.EqualTo(rich.LastAssistantMessage))


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type PickActiveTests() =

    let statusAt (status: SessionLevelStatus) (skill: string option) =
        { emptyStatus with Status = status; Skill = skill }

    [<Test>]
    member _.``Empty list yields None``() =
        Assert.That(pickActive [], Is.EqualTo(None))

    [<Test>]
    member _.``All Idle yields None``() =
        let sessions =
            [ statusAt SessionLevelStatus.Idle None, ts "2026-03-01T10:00:00Z"
              statusAt SessionLevelStatus.Idle None, ts "2026-03-01T11:00:00Z" ]
        Assert.That(pickActive sessions, Is.EqualTo(None))

    [<Test>]
    member _.``The most-recent active session wins``() =
        let older = statusAt SessionLevelStatus.Working (Some "old")
        let newer = statusAt SessionLevelStatus.WaitingForUser (Some "new")
        let sessions =
            [ older, ts "2026-03-01T10:00:00Z"
              newer, ts "2026-03-01T11:00:00Z" ]
        Assert.That(pickActive sessions, Is.EqualTo(Some newer))

    [<Test>]
    member _.``A more-recently-idled session does not hide an actively-working sibling``() =
        // The key rule: NOT raw latest-update. The Idle session has the newer last_seen but must be
        // dropped, so the older-but-active sibling wins.
        let active = statusAt SessionLevelStatus.Working (Some "review")
        let justIdled = statusAt SessionLevelStatus.Idle None
        let sessions =
            [ active, ts "2026-03-01T10:00:00Z"
              justIdled, ts "2026-03-01T11:59:00Z" ]
        Assert.That(pickActive sessions, Is.EqualTo(Some active))

    [<Test>]
    member _.``The whole winning record is returned, not cherry-picked fields``() =
        let winner =
            { Status = SessionLevelStatus.Working
              Skill = Some "bd-execute"
              LastUserMessage = Some(msg "go" "2026-03-01T10:00:00Z")
              LastAssistantMessage = Some(msg "on it" "2026-03-01T10:00:01Z") }
        let sessions =
            [ statusAt SessionLevelStatus.Idle (Some "stale-skill"), ts "2026-03-01T11:00:00Z"
              winner, ts "2026-03-01T10:30:00Z" ]
        Assert.That(pickActive sessions, Is.EqualTo(Some winner))


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type DebounceIdleTests() =

    let now = ts "2026-03-01T12:00:00Z"
    let grace = idleDebounceWindow

    [<Test>]
    member _.``Idle within the grace window is held as Working``() =
        // The inter-turn blink: just went Idle, so hold the dot red instead of flickering blue.
        let idleSince = now - TimeSpan.FromSeconds 3.0
        Assert.That(debounceIdle grace now (Some idleSince) Idle, Is.EqualTo(Working))

    [<Test>]
    member _.``Idle past the grace window surfaces as Idle``() =
        let idleSince = now - grace - TimeSpan.FromSeconds 1.0
        Assert.That(debounceIdle grace now (Some idleSince) Idle, Is.EqualTo(Idle))

    [<Test>]
    member _.``Idle exactly at the grace boundary surfaces as Idle``() =
        // now - since = grace is NOT < grace, so the hold has already expired.
        let idleSince = now - grace
        Assert.That(debounceIdle grace now (Some idleSince) Idle, Is.EqualTo(Idle))

    [<Test>]
    member _.``Idle with no stamp falls through to the real Idle status``() =
        Assert.That(debounceIdle grace now None Idle, Is.EqualTo(Idle))

    [<Test>]
    member _.``Working within the grace window passes through unheld``() =
        // A live active session is never altered; the debounce only touches the Idle edge.
        let recent = now - TimeSpan.FromSeconds 1.0
        Assert.That(debounceIdle grace now (Some recent) Working, Is.EqualTo(Working))

    [<Test>]
    member _.``WaitingForUser passes through unchanged even with a fresh stamp``() =
        let recent = now - TimeSpan.FromSeconds 1.0
        Assert.That(debounceIdle grace now (Some recent) WaitingForUser, Is.EqualTo(WaitingForUser))

    [<Test>]
    member _.``NoSession passes through unchanged even with a fresh stamp``() =
        let recent = now - TimeSpan.FromSeconds 1.0
        Assert.That(debounceIdle grace now (Some recent) NoSession, Is.EqualTo(NoSession))
