module Tests.CodingToolPushSourceTests

open System
open System.Globalization
open NUnit.Framework
open Shared
open Server.SessionActivity
open Server.SessionActivityStore
open Server.CodingToolStatus

// Covers the push-only repoint of the worktree card's coding-tool fields onto the SessionActivity
// live state: fromPushSessions (the pickActive collapse → CodingToolResult), collapseByWorktree
// (group a flat live set by worktree), and getLastSessionId (the DISTINCT resume pick —
// most-recent-any, not the display pick). Pure/in-process — no HTTP, no store IO.

let private ts (s: string) = DateTimeOffset.Parse(s, CultureInfo.InvariantCulture)
let private msg text t : Message = { Text = text; At = ts t }

/// A stored live session for one worktree. `UpdatedAt` and `LastSeen` are set together (an event is
/// also the heartbeat), exactly as the ingestion service writes them.
let private stored
    (sid: string)
    (wt: string)
    (status: SessionLevelStatus)
    (skill: string option)
    (lastUser: Message option)
    (lastAsst: Message option)
    (seen: string)
    : StoredStatus =
    { SessionId = SessionId sid
      WorktreePath = WorktreePath wt
      Provider = CopilotCli
      Status =
        { Status = status
          Skill = skill
          Intent = None
          Title = None
          LastUserMessage = lastUser
          LastAssistantMessage = lastAsst }
      UpdatedAt = ts seen
      LastSeen = ts seen }

let private now = ts "2026-03-01T12:00:00Z"


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type FromPushSessionsTests() =

    [<Test>]
    member _.``No sessions yields the blank NoSession card``() =
        Assert.That(fromPushSessions now [], Is.EqualTo noSessionPushResult)

    [<Test>]
    member _.``Stale idle sessions past the open window collapse to grey NoSession``() =
        // Both were last seen well over openWindow (~3 min) ago, so neither is an OPEN session — the
        // worktree has no live CLI and reads as NoSession (grey). They carry no footer data, so the
        // card renders blank.
        let sessions =
            [ stored "a" "wt" SessionLevelStatus.Idle None None None "2026-03-01T11:00:00Z"
              stored "b" "wt" SessionLevelStatus.Idle None None None "2026-03-01T11:30:00Z" ]
        let result = fromPushSessions now sessions
        Assert.That(result.Status, Is.EqualTo NoSession)
        Assert.That(result.CurrentSkill, Is.EqualTo None)
        Assert.That(result.LastUserMessage, Is.EqualTo None)
        Assert.That(result.LastAssistantMessage, Is.EqualTo None)

    [<Test>]
    member _.``An open idle session collapses to blue Idle``() =
        // Seen ~1 min ago (inside openWindow) but Idle → the CLI is open and the agent is parked:
        // blue Idle, not grey NoSession.
        let session = stored "a" "wt" SessionLevelStatus.Idle None None None "2026-03-01T11:59:00Z"
        Assert.That((fromPushSessions now [ session ]).Status, Is.EqualTo Idle)

    [<Test>]
    member _.``A session exactly at the open-window edge is not open (grey)``() =
        // now - last_seen = openWindow exactly; the strict `< openWindow` predicate excludes it, so it
        // is NOT open → NoSession.
        let atEdge =
            stored "a" "wt" SessionLevelStatus.Idle None None None ((now - openWindow).ToString("O"))
        Assert.That((fromPushSessions now [ atEdge ]).Status, Is.EqualTo NoSession)

    [<Test>]
    member _.``An open idle worktree keeps its retained footer (skill + messages)``() =
        // The bug fix: going Idle must NOT blank the footer. The just-idled session retains its last
        // user/assistant messages and skill through the fold, so the card footer stays populated.
        let session =
            stored "a" "wt" SessionLevelStatus.Idle (Some "bd-execute")
                (Some(msg "ship it" "2026-03-01T11:58:00Z"))
                (Some(msg "done, all green" "2026-03-01T11:58:30Z"))
                "2026-03-01T11:59:00Z"
        let result = fromPushSessions now [ session ]
        Assert.That(result.Status, Is.EqualTo Idle)
        Assert.That(result.CurrentSkill, Is.EqualTo(Some "bd-execute"))
        Assert.That(result.LastUserMessage |> Option.map fst, Is.EqualTo(Some "ship it"))
        Assert.That(result.LastAssistantMessage |> Option.map _.Message, Is.EqualTo(Some "done, all green"))

    [<Test>]
    member _.``A NoSession worktree with retained data keeps its footer``() =
        // No OPEN session (last seen ~1 h ago, past openWindow) → grey NoSession dot, but the retained
        // session still carries the last prompt/reply so the footer/event-log does not vanish.
        let session =
            stored "a" "wt" SessionLevelStatus.Idle (Some "review")
                (Some(msg "look at auth" "2026-03-01T10:58:00Z"))
                (Some(msg "which file?" "2026-03-01T10:58:30Z"))
                "2026-03-01T11:00:00Z"
        let result = fromPushSessions now [ session ]
        Assert.That(result.Status, Is.EqualTo NoSession)
        Assert.That(result.CurrentSkill, Is.EqualTo(Some "review"))
        Assert.That(result.LastUserMessage |> Option.map fst, Is.EqualTo(Some "look at auth"))
        Assert.That(result.LastAssistantMessage |> Option.map _.Message, Is.EqualTo(Some "which file?"))

    [<Test>]
    member _.``The most-recent active session wins and every field comes from it``() =
        let older =
            stored "old" "wt" SessionLevelStatus.Working (Some "old-skill")
                (Some(msg "old prompt" "2026-03-01T10:00:00Z"))
                (Some(msg "old reply" "2026-03-01T10:00:01Z"))
                "2026-03-01T11:58:00Z"
        let newer =
            stored "new" "wt" SessionLevelStatus.WaitingForUser (Some "review")
                (Some(msg "the auth module" "2026-03-01T11:40:00Z"))
                (Some(msg "which file?" "2026-03-01T11:40:01Z"))
                "2026-03-01T11:59:00Z"

        let result = fromPushSessions now [ older; newer ]

        Assert.That(result.Status, Is.EqualTo WaitingForUser)
        Assert.That(result.Provider, Is.EqualTo(Some CopilotCli))
        Assert.That(result.CurrentSkill, Is.EqualTo(Some "review"))
        Assert.That(result.LastUserMessage |> Option.map fst, Is.EqualTo(Some "the auth module"))
        Assert.That(result.LastAssistantMessage |> Option.map _.Message, Is.EqualTo(Some "which file?"))

    [<Test>]
    member _.``A just-idled newer session does not hide an actively-working sibling``() =
        // The key collapse rule: drop Idle FIRST, then most-recent active wins — NOT raw latest
        // last_seen. The idle sibling is newer but must not win the STATUS, and the FOOTER stays on
        // the active session (the active winner, not the newer idle one).
        let active =
            stored "active" "wt" SessionLevelStatus.Working (Some "bd-execute")
                (Some(msg "go" "2026-03-01T11:58:00Z")) None
                "2026-03-01T11:58:00Z"
        let justIdled = stored "idle" "wt" SessionLevelStatus.Idle None None None "2026-03-01T11:59:00Z"

        let result = fromPushSessions now [ active; justIdled ]

        Assert.That(result.Status, Is.EqualTo Working)
        Assert.That(result.CurrentSkill, Is.EqualTo(Some "bd-execute"))

    [<Test>]
    member _.``A stale (crashed) active session is not open, so the worktree is grey NoSession``() =
        // No went_idle emitted and last_seen is well past both openWindow and the staleness timeout →
        // the session is not OPEN, so it drops out of the status collapse and the worktree reads as
        // grey NoSession (a dead agent goes straight to grey, never a lingering blue).
        let stale =
            stored "stale" "wt" SessionLevelStatus.Working (Some "review") None None
                (((now - stalenessTimeout).AddMinutes -1.0).ToString("O"))
        Assert.That((fromPushSessions now [ stale ]).Status, Is.EqualTo NoSession)

    [<Test>]
    member _.``A stale active session loses to a fresh active sibling``() =
        let stale =
            stored "stale" "wt" SessionLevelStatus.Working (Some "stale-skill") None None
                (((now - stalenessTimeout).AddMinutes -1.0).ToString("O"))
        let fresh =
            stored "fresh" "wt" SessionLevelStatus.Working (Some "fresh-skill") None None
                "2026-03-01T11:58:00Z"

        let result = fromPushSessions now [ stale; fresh ]
        Assert.That(result.CurrentSkill, Is.EqualTo(Some "fresh-skill"))

    [<Test>]
    member _.``The last user message is truncated to the 120-char cap``() =
        // truncateMessage keeps `cap` chars then appends an ellipsis, so a long prompt lands at 123.
        let longText = String('x', 200)
        let session =
            stored "a" "wt" SessionLevelStatus.Working None (Some(msg longText "2026-03-01T11:59:00Z")) None
                "2026-03-01T11:59:00Z"
        let result = fromPushSessions now [ session ]
        let truncated = result.LastUserMessage |> Option.map fst |> Option.get
        Assert.That(truncated.Length, Is.EqualTo 123)
        Assert.That(truncated, Does.EndWith "...")

    [<Test>]
    member _.``The last assistant message is truncated to the 80-char cap and tagged copilot``() =
        let longText = String('y', 200)
        let session =
            stored "a" "wt" SessionLevelStatus.Working None None (Some(msg longText "2026-03-01T11:59:00Z"))
                "2026-03-01T11:59:00Z"
        let result = fromPushSessions now [ session ]
        let event = result.LastAssistantMessage |> Option.get
        Assert.That(event.Message.Length, Is.EqualTo 83)
        Assert.That(event.Message, Does.EndWith "...")
        Assert.That(event.Source, Is.EqualTo "copilot")


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type CollapseByWorktreeTests() =

    [<Test>]
    member _.``Sessions are grouped by worktree and collapsed independently``() =
        let sessions =
            [ stored "a1" "wt-a" SessionLevelStatus.Working (Some "review") None None "2026-03-01T11:58:00Z"
              stored "a2" "wt-a" SessionLevelStatus.Idle None None None "2026-03-01T11:59:00Z"
              stored "b1" "wt-b" SessionLevelStatus.WaitingForUser (Some "investigate") None None "2026-03-01T11:58:00Z" ]

        let byWt = collapseByWorktree now sessions

        Assert.That(byWt["wt-a"].Status, Is.EqualTo Working)
        Assert.That(byWt["wt-a"].CurrentSkill, Is.EqualTo(Some "review"))
        Assert.That(byWt["wt-b"].Status, Is.EqualTo WaitingForUser)
        Assert.That(byWt["wt-b"].CurrentSkill, Is.EqualTo(Some "investigate"))

    [<Test>]
    member _.``A worktree with only an OPEN idle session collapses to blue Idle``() =
        let sessions = [ stored "a" "wt-a" SessionLevelStatus.Idle None None None "2026-03-01T11:59:00Z" ]
        let byWt = collapseByWorktree now sessions
        Assert.That(byWt["wt-a"].Status, Is.EqualTo Idle)


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type WithRetainedFallbackTests() =

    let retainedRow sid wt lastUser seen : string * StoredStatus =
        WorktreePath.value (WorktreePath wt), stored sid wt SessionLevelStatus.Idle None lastUser None seen

    [<Test>]
    member _.``A worktree absent from the live map gets a NoSession card carrying the retained footer``() =
        // wt-old has no live session (aged out of the idle window), only a durable row with a message.
        let retained = Map.ofList [ retainedRow "old" "wt-old" (Some(msg "resume me" "2026-03-01T08:00:00Z")) "2026-03-01T08:00:00Z" ]

        let merged = Map.empty |> withRetainedFallback retained

        Assert.That(merged["wt-old"].Status, Is.EqualTo NoSession, "the dot stays grey — no OPEN session")
        Assert.That(
            merged["wt-old"].LastUserMessage |> Option.map fst,
            Is.EqualTo(Some "resume me"),
            "the retained footer/resume message is surfaced so the resume button is reachable"
        )

    [<Test>]
    member _.``An intent-only retained session still carries the provider indicator``() =
        // Regression: hasFooter must count Intent. A session folded from IntentReported alone still has
        // footer content (its intent line renders), so its retained card must carry the provider.
        let intentOnly: StoredStatus =
            { SessionId = SessionId "i"
              WorktreePath = WorktreePath "wt-i"
              Provider = CopilotCli
              Status = { emptyStatus with Intent = Some(msg "investigating the fold" "2026-03-01T08:00:00Z") }
              UpdatedAt = ts "2026-03-01T08:00:00Z"
              LastSeen = ts "2026-03-01T08:00:00Z" }

        let result = retainedFooterResult intentOnly

        Assert.That(result.AgentIntent |> Option.map fst, Is.EqualTo(Some "investigating the fold"))
        Assert.That(result.Provider, Is.EqualTo(Some CopilotCli), "an intent-only footer must still carry the provider")

    [<Test>]
    member _.``A live worktree keeps its live card (retained fallback only fills gaps)``() =
        let live = collapseByWorktree now [ stored "a" "wt-a" SessionLevelStatus.Working (Some "review") None None "2026-03-01T11:59:00Z" ]
        let retained = Map.ofList [ retainedRow "stale" "wt-a" (Some(msg "old" "2026-03-01T08:00:00Z")) "2026-03-01T08:00:00Z" ]

        let merged = withRetainedFallback retained live

        Assert.That(merged["wt-a"].Status, Is.EqualTo Working, "the live result wins over the retained fallback")
        Assert.That(merged["wt-a"].CurrentSkill, Is.EqualTo(Some "review"))

    [<Test>]
    member _.``A worktree with only a STALE idle session collapses to grey NoSession``() =
        let sessions = [ stored "a" "wt-a" SessionLevelStatus.Idle None None None "2026-03-01T11:00:00Z" ]
        let byWt = collapseByWorktree now sessions
        Assert.That(byWt["wt-a"].Status, Is.EqualTo NoSession)

    [<Test>]
    member _.``An empty live set yields an empty map``() =
        Assert.That(collapseByWorktree now [] |> Map.isEmpty, Is.True)


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type GetLastSessionIdTests() =

    [<Test>]
    member _.``No sessions yields None (CLI --continue fallback)``() =
        Assert.That(getLastSessionId [], Is.EqualTo None)

    [<Test>]
    member _.``The most-recent session wins regardless of active or idle``() =
        // Resume = most-recent-ANY. The newest session here is Idle, yet it is still the resume pick.
        let older = stored "older" "wt" SessionLevelStatus.Working None None None "2026-03-01T11:00:00Z"
        let newerIdle = stored "newer" "wt" SessionLevelStatus.Idle None None None "2026-03-01T11:59:00Z"
        Assert.That(getLastSessionId [ older; newerIdle ], Is.EqualTo(Some "newer"))

    [<Test>]
    member _.``The resume pick differs from the display pick when the newest session just went idle``() =
        // Display (pickActive) drops the newer Idle session and shows the older Working one; resume
        // (getLastSessionId) picks the newer Idle one — the two picks are deliberately distinct.
        let active = stored "active" "wt" SessionLevelStatus.Working (Some "review") None None "2026-03-01T11:58:00Z"
        let justIdled = stored "idled" "wt" SessionLevelStatus.Idle None None None "2026-03-01T11:59:00Z"

        let display = fromPushSessions now [ active; justIdled ]
        let resume = getLastSessionId [ active; justIdled ]

        Assert.That(display.Status, Is.EqualTo Working, "display pick is the active session")
        Assert.That(resume, Is.EqualTo(Some "idled"), "resume pick is the most-recent (idle) session")
