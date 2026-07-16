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
    (status: CodingToolStatus)
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
    member _.``All-Idle sessions yield the blank NoSession card``() =
        let sessions =
            [ stored "a" "wt" Idle None None None "2026-03-01T11:00:00Z"
              stored "b" "wt" Idle None None None "2026-03-01T11:30:00Z" ]
        Assert.That(fromPushSessions now sessions, Is.EqualTo noSessionPushResult)

    [<Test>]
    member _.``The most-recent active session wins and every field comes from it``() =
        let older =
            stored "old" "wt" Working (Some "old-skill")
                (Some(msg "old prompt" "2026-03-01T10:00:00Z"))
                (Some(msg "old reply" "2026-03-01T10:00:01Z"))
                "2026-03-01T11:57:00Z"
        let newer =
            stored "new" "wt" WaitingForUser (Some "review")
                (Some(msg "the auth module" "2026-03-01T11:40:00Z"))
                (Some(msg "which file?" "2026-03-01T11:40:01Z"))
                "2026-03-01T11:59:00Z"

        let result = fromPushSessions now [ older; newer ]

        Assert.That(result.Status, Is.EqualTo WaitingForUser)
        Assert.That(result.Provider, Is.EqualTo(Some Copilot))
        Assert.That(result.CurrentSkill, Is.EqualTo(Some "review"))
        Assert.That(result.LastUserMessage |> Option.map fst, Is.EqualTo(Some "the auth module"))
        Assert.That(result.LastAssistantMessage |> Option.map _.Message, Is.EqualTo(Some "which file?"))

    [<Test>]
    member _.``A just-idled newer session does not hide an actively-working sibling``() =
        // The key collapse rule: drop Idle FIRST, then most-recent active wins — NOT raw latest
        // last_seen. The idle sibling is newer but must not win.
        let active =
            stored "active" "wt" Working (Some "bd-execute")
                (Some(msg "go" "2026-03-01T11:58:00Z")) None
                "2026-03-01T11:58:00Z"
        let justIdled = stored "idle" "wt" Idle None None None "2026-03-01T11:59:00Z"

        let result = fromPushSessions now [ active; justIdled ]

        Assert.That(result.Status, Is.EqualTo Working)
        Assert.That(result.CurrentSkill, Is.EqualTo(Some "bd-execute"))

    [<Test>]
    member _.``A stale (crashed) active session reads as Idle and drops out of the pick``() =
        // No went_idle emitted, but last_seen is older than the staleness timeout → the crash net
        // rewrites it to Idle, so pickActive drops it and the card goes blank.
        let stale =
            stored "stale" "wt" Working (Some "review") None None
                (((now - stalenessTimeout).AddMinutes -1.0).ToString("O"))
        Assert.That(fromPushSessions now [ stale ], Is.EqualTo noSessionPushResult)

    [<Test>]
    member _.``A stale active session loses to a fresh active sibling``() =
        let stale =
            stored "stale" "wt" Working (Some "stale-skill") None None
                (((now - stalenessTimeout).AddMinutes -1.0).ToString("O"))
        let fresh =
            stored "fresh" "wt" Working (Some "fresh-skill") None None
                "2026-03-01T11:58:00Z"

        let result = fromPushSessions now [ stale; fresh ]
        Assert.That(result.CurrentSkill, Is.EqualTo(Some "fresh-skill"))

    [<Test>]
    member _.``The last user message is truncated to the 120-char cap``() =
        // truncateMessage keeps `cap` chars then appends an ellipsis, so a long prompt lands at 123.
        let longText = String('x', 200)
        let session =
            stored "a" "wt" Working None (Some(msg longText "2026-03-01T11:59:00Z")) None
                "2026-03-01T11:59:00Z"
        let result = fromPushSessions now [ session ]
        let truncated = result.LastUserMessage |> Option.map fst |> Option.get
        Assert.That(truncated.Length, Is.EqualTo 123)
        Assert.That(truncated, Does.EndWith "...")

    [<Test>]
    member _.``The last assistant message is truncated to the 80-char cap and tagged copilot``() =
        let longText = String('y', 200)
        let session =
            stored "a" "wt" Working None None (Some(msg longText "2026-03-01T11:59:00Z"))
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
            [ stored "a1" "wt-a" Working (Some "review") None None "2026-03-01T11:58:00Z"
              stored "a2" "wt-a" Idle None None None "2026-03-01T11:59:00Z"
              stored "b1" "wt-b" WaitingForUser (Some "investigate") None None "2026-03-01T11:58:00Z" ]

        let byWt = collapseByWorktree now sessions

        Assert.That(byWt["wt-a"].Status, Is.EqualTo Working)
        Assert.That(byWt["wt-a"].CurrentSkill, Is.EqualTo(Some "review"))
        Assert.That(byWt["wt-b"].Status, Is.EqualTo WaitingForUser)
        Assert.That(byWt["wt-b"].CurrentSkill, Is.EqualTo(Some "investigate"))

    [<Test>]
    member _.``A worktree with only idle sessions collapses to the blank NoSession card``() =
        let sessions = [ stored "a" "wt-a" Idle None None None "2026-03-01T11:59:00Z" ]
        let byWt = collapseByWorktree now sessions
        Assert.That(byWt["wt-a"], Is.EqualTo noSessionPushResult)

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
        let older = stored "older" "wt" Working None None None "2026-03-01T11:00:00Z"
        let newerIdle = stored "newer" "wt" Idle None None None "2026-03-01T11:59:00Z"
        Assert.That(getLastSessionId [ older; newerIdle ], Is.EqualTo(Some "newer"))

    [<Test>]
    member _.``The resume pick differs from the display pick when the newest session just went idle``() =
        // Display (pickActive) drops the newer Idle session and shows the older Working one; resume
        // (getLastSessionId) picks the newer Idle one — the two picks are deliberately distinct.
        let active = stored "active" "wt" Working (Some "review") None None "2026-03-01T11:58:00Z"
        let justIdled = stored "idled" "wt" Idle None None None "2026-03-01T11:59:00Z"

        let display = fromPushSessions now [ active; justIdled ]
        let resume = getLastSessionId [ active; justIdled ]

        Assert.That(display.Status, Is.EqualTo Working, "display pick is the active session")
        Assert.That(resume, Is.EqualTo(Some "idled"), "resume pick is the most-recent (idle) session")
