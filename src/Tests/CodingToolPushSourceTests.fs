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
let private footerMessage text t : UserFooterMessage =
    { Glyph = None
      Text = text
      Timestamp = ts t }

let private storedWithClocks
    (sid: string)
    (wt: string)
    (status: SessionLevelStatus)
    (skill: string option)
    (lastUser: Message option)
    (lastAsst: Message option)
    (updatedAt: string)
    (lastSeen: string)
    : StoredStatus =
    { SessionId = SessionId sid
      WorktreePath = WorktreePath wt
      Provider = CopilotCli
      Status =
        { emptyStatus with
            Status = status
            Skill = skill
            LastUserMessage = lastUser
            LastAssistantMessage = lastAsst }
      UpdatedAt = ts updatedAt
      LastSeen = ts lastSeen
      ContextUsageAt = None }

let private stored sid wt status skill lastUser lastAsst seen =
    storedWithClocks sid wt status skill lastUser lastAsst seen seen

let private now = ts "2026-03-01T12:00:00Z"

/// A stored OPEN session carrying a context-usage snapshot — for the per-session donut tests.
let private storedUsage sid wt status usage seen : StoredStatus =
    let s = stored sid wt status None None None seen
    { s with Status.ContextUsage = usage }


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
        Assert.That(result.LastUserMessage |> Option.map _.Text, Is.EqualTo(Some "ship it"))
        Assert.That(result.LastAssistantMessage |> Option.map fst, Is.EqualTo(Some "done, all green"))

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
        Assert.That(result.LastUserMessage |> Option.map _.Text, Is.EqualTo(Some "look at auth"))
        Assert.That(result.LastAssistantMessage |> Option.map fst, Is.EqualTo(Some "which file?"))

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
        Assert.That(result.LastUserMessage |> Option.map _.Text, Is.EqualTo(Some "the auth module"))
        Assert.That(result.LastAssistantMessage |> Option.map fst, Is.EqualTo(Some "which file?"))

    [<Test>]
    member _.``A heartbeat cannot replace the most-recent active session``() =
        let heartbeatNewest =
            storedWithClocks "heartbeat" "wt" SessionLevelStatus.Working (Some "old-skill")
                (Some(msg "old prompt" "2026-03-01T11:57:00Z"))
                (Some(msg "old reply" "2026-03-01T11:57:30Z"))
                "2026-03-01T11:58:00Z"
                "2026-03-01T11:59:30Z"
        let activityNewest =
            storedWithClocks "activity" "wt" SessionLevelStatus.WaitingForUser (Some "new-skill")
                (Some(msg "new prompt" "2026-03-01T11:58:30Z"))
                (Some(msg "new reply" "2026-03-01T11:59:00Z"))
                "2026-03-01T11:59:00Z"
                "2026-03-01T11:59:00Z"

        let result = fromPushSessions now [ heartbeatNewest; activityNewest ]

        Assert.That(result.Status, Is.EqualTo WaitingForUser)
        Assert.That(result.CurrentSkill, Is.EqualTo(Some "new-skill"))
        Assert.That(result.LastUserMessage, Is.EqualTo(Some(footerMessage "new prompt" "2026-03-01T11:58:30Z")))
        Assert.That(result.LastAssistantMessage, Is.EqualTo(Some("new reply", ts "2026-03-01T11:59:00Z")))
        Assert.That(result.LastActivity, Is.EqualTo(Some(ts "2026-03-01T11:59:00Z")))

    [<Test>]
    member _.``A heartbeat cannot replace the most-recent idle footer``() =
        let heartbeatNewest =
            storedWithClocks "heartbeat" "wt" SessionLevelStatus.Idle (Some "old-skill")
                (Some(msg "old prompt" "2026-03-01T11:57:00Z"))
                (Some(msg "old reply" "2026-03-01T11:57:30Z"))
                "2026-03-01T11:58:00Z"
                "2026-03-01T11:59:30Z"
        let activityNewest =
            storedWithClocks "activity" "wt" SessionLevelStatus.Idle (Some "new-skill")
                (Some(msg "new prompt" "2026-03-01T11:58:30Z"))
                (Some(msg "new reply" "2026-03-01T11:59:00Z"))
                "2026-03-01T11:59:00Z"
                "2026-03-01T11:59:00Z"

        let result = fromPushSessions now [ heartbeatNewest; activityNewest ]

        Assert.That(result.Status, Is.EqualTo Idle)
        Assert.That(result.CurrentSkill, Is.EqualTo(Some "new-skill"))
        Assert.That(result.LastUserMessage, Is.EqualTo(Some(footerMessage "new prompt" "2026-03-01T11:58:30Z")))
        Assert.That(result.LastAssistantMessage, Is.EqualTo(Some("new reply", ts "2026-03-01T11:59:00Z")))

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
        let truncated = result.LastUserMessage |> Option.map _.Text |> Option.get
        Assert.That(truncated.Length, Is.EqualTo 123)
        Assert.That(truncated, Does.EndWith "...")

    [<Test>]
    member _.``Canvas display text is truncated after parsing``() =
        let longText = String('x', 200)
        let payload = $"[canvas] {{\"action\":\"comment\",\"text\":\"{longText}\"}}"
        let session =
            stored "a" "wt" SessionLevelStatus.Working None (Some(msg payload "2026-03-01T11:59:00Z")) None
                "2026-03-01T11:59:00Z"
        let message = (fromPushSessions now [ session ]).LastUserMessage |> Option.get
        Assert.Multiple(fun () ->
            Assert.That(message.Glyph, Is.EqualTo(Some MessageGlyph.Canvas))
            Assert.That(message.Text, Is.EqualTo(String('x', 120) + "...")))

    [<Test>]
    member _.``Canvas activity and user footer share display text for live and retained sessions``() =
        let canvasSession seen =
            let timestamp = seen
            let raw = "[canvas] {\"action\":\"comment\",\"text\":\"Why is retry not jittered?\"}"
            stored "a" "wt" SessionLevelStatus.Working None (Some(msg raw timestamp)) None seen
            |> fun value -> { value with Status.Title = Some(msg raw timestamp) }

        let liveResult = fromPushSessions now [ canvasSession "2026-03-01T11:59:00Z" ]
        let retained = Map.ofList [ "wt", canvasSession "2026-03-01T08:00:00Z" ]
        let retainedResult =
            includeRetainedSessions retained []
            |> collapseByWorktree now
            |> Map.find "wt"

        [ liveResult; retainedResult ]
        |> List.iter (fun result ->
            let timestamp = result.LastUserMessage |> Option.map _.Timestamp |> Option.get
            Assert.Multiple(fun () ->
                Assert.That(
                    result.AgentActivity,
                    Is.EqualTo(Some(AgentActivity.SessionTitle("Why is retry not jittered?", timestamp))))
                Assert.That(result.LastUserMessage |> Option.map _.Text, Is.EqualTo(Some "Why is retry not jittered?"))))

    [<Test>]
    member _.``The last assistant message is truncated to the 80-char cap``() =
        let longText = String('y', 200)
        let session =
            stored "a" "wt" SessionLevelStatus.Working None None (Some(msg longText "2026-03-01T11:59:00Z"))
                "2026-03-01T11:59:00Z"
        let result = fromPushSessions now [ session ]
        let text, _ = result.LastAssistantMessage |> Option.get
        Assert.That(text.Length, Is.EqualTo 83)
        Assert.That(text, Does.EndWith "...")


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type SessionStatusesTests() =

    let usage cur lim = Some { CurrentTokens = cur; TokenLimit = lim }

    [<Test>]
    member _.``No open sessions yields an empty per-session list``() =
        Assert.That((fromPushSessions now []).SessionStatuses, Is.Empty)
        let stale = stored "a" "wt" SessionLevelStatus.Idle None None None "2026-03-01T11:00:00Z"
        Assert.That((fromPushSessions now [ stale ]).SessionStatuses, Is.Empty)

    [<Test>]
    member _.``Each open session keeps its OWN context usage (no footer collapse)``() =
        // The regression fix: the WORKING winner has not reported usage yet, but a sibling idle
        // session did. That donut must survive independently of which session wins the status — the
        // old single-footer ContextUsage blanked the whole worktree the moment the winner switched.
        let winner = storedUsage "win" "wt" SessionLevelStatus.Working None "2026-03-01T11:59:00Z"
        let reported = storedUsage "rep" "wt" SessionLevelStatus.Idle (usage 50000 200000) "2026-03-01T11:58:00Z"
        let result = fromPushSessions now [ reported; winner ]
        Assert.That(result.SessionStatuses |> List.map _.Status, Is.EqualTo [ Working; Idle ])
        Assert.That(result.SessionStatuses |> List.map _.ContextUsage, Is.EqualTo [ None; usage 50000 200000 ])

    [<Test>]
    member _.``Per-session dots are ordered Working, Waiting, Idle``() =
        let idle = stored "i" "wt" SessionLevelStatus.Idle None None None "2026-03-01T11:59:00Z"
        let working = stored "w" "wt" SessionLevelStatus.Working None None None "2026-03-01T11:58:00Z"
        let waiting = stored "q" "wt" SessionLevelStatus.WaitingForUser None None None "2026-03-01T11:57:30Z"
        let result = fromPushSessions now [ idle; working; waiting ]
        Assert.That(result.SessionStatuses |> List.map _.Status, Is.EqualTo [ Working; WaitingForUser; Idle ])

    [<Test>]
    member _.``Closed (stale) sessions are excluded from the per-session dots``() =
        let openWorking = storedUsage "o" "wt" SessionLevelStatus.Working (usage 10000 200000) "2026-03-01T11:59:00Z"
        let closed = storedUsage "c" "wt" SessionLevelStatus.Working (usage 99000 200000) "2026-03-01T11:00:00Z"
        let result = fromPushSessions now [ openWorking; closed ]
        Assert.That(result.SessionStatuses |> List.map _.ContextUsage, Is.EqualTo [ usage 10000 200000 ])

    [<Test>]
    member _.``noSessionPushResult carries no per-session dots``() =
        Assert.That(noSessionPushResult.SessionStatuses, Is.Empty)


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
type RetainedSessionsTests() =

    let retainedRow sid wt lastUser seen : string * StoredStatus =
        WorktreePath.value (WorktreePath wt), stored sid wt SessionLevelStatus.Idle None lastUser None seen

    let collapseWithRetained at retained live =
        live
        |> includeRetainedSessions retained
        |> collapseByWorktree at

    [<Test>]
    member _.``A worktree absent from the live map gets a NoSession card carrying the retained footer``() =
        let retained = Map.ofList [ retainedRow "old" "wt-old" (Some(msg "resume me" "2026-03-01T08:00:00Z")) "2026-03-01T08:00:00Z" ]

        let merged = collapseWithRetained now retained []

        Assert.That(merged["wt-old"].Status, Is.EqualTo NoSession, "the dot stays grey — no OPEN session")
        Assert.That(merged["wt-old"].SessionStatuses, Is.Empty)
        Assert.That(
            merged["wt-old"].LastUserMessage |> Option.map _.Text,
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
              LastSeen = ts "2026-03-01T08:00:00Z"
              ContextUsageAt = None }

        let result = collapseWithRetained now (Map.ofList [ "wt-i", intentOnly ]) [] |> Map.find "wt-i"

        Assert.That(
            result.AgentActivity,
            Is.EqualTo(Some(AgentActivity.Intent("investigating the fold", ts "2026-03-01T08:00:00Z"))))
        Assert.That(result.Provider, Is.EqualTo(Some CopilotCli), "an intent-only footer must still carry the provider")

    [<Test>]
    member _.``An active live session keeps the footer over retained data``() =
        let live = [ stored "a" "wt-a" SessionLevelStatus.Working (Some "review") None None "2026-03-01T11:59:00Z" ]
        let retained = Map.ofList [ retainedRow "a" "wt-a" (Some(msg "old" "2026-03-01T08:00:00Z")) "2026-03-01T08:00:00Z" ]

        let merged = collapseWithRetained now retained live

        Assert.That(merged["wt-a"].Status, Is.EqualTo Working)
        Assert.That(merged["wt-a"].CurrentSkill, Is.EqualTo(Some "review"))
        Assert.That(merged["wt-a"].SessionStatuses |> List.map _.Status, Is.EqualTo [ Working ])

    [<Test>]
    member _.``A heartbeat-kept live row cannot suppress the retained activity winner``() =
        let at = ts "2026-03-01T11:31:00Z"
        let retained =
            storedWithClocks "activity" "wt" SessionLevelStatus.Idle None
                (Some(msg "resume me" "2026-03-01T09:30:00Z"))
                None
                "2026-03-01T09:30:00Z"
                "2026-03-01T09:30:00Z"
        let heartbeatKeptLive =
            storedWithClocks "heartbeat" "wt" SessionLevelStatus.Idle None None None
                "2026-03-01T09:00:00Z"
                "2026-03-01T10:30:00Z"

        let result =
            collapseWithRetained at (Map.ofList [ "wt", retained ]) [ heartbeatKeptLive ]
            |> Map.find "wt"

        Assert.That(result.Status, Is.EqualTo NoSession)
        Assert.That(result.SessionStatuses, Is.Empty)
        Assert.That(result.LastUserMessage, Is.EqualTo(Some(footerMessage "resume me" "2026-03-01T09:30:00Z")))

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

    [<Test>]
    member _.``A heartbeat cannot replace the most-recent resume session``() =
        let heartbeatNewest =
            storedWithClocks "heartbeat" "wt" SessionLevelStatus.Idle None None None
                "2026-03-01T11:00:00Z"
                "2026-03-01T11:59:00Z"
        let activityNewest =
            storedWithClocks "activity" "wt" SessionLevelStatus.Idle None None None
                "2026-03-01T11:58:00Z"
                "2026-03-01T11:58:00Z"

        Assert.That(getLastSessionId [ heartbeatNewest; activityNewest ], Is.EqualTo(Some "activity"))

    [<Test>]
    member _.``Equal activity timestamps use session id instead of heartbeat recency``() =
        let a =
            storedWithClocks "a" "wt" SessionLevelStatus.Idle None None None
                "2026-03-01T11:58:00Z"
                "2026-03-01T11:59:00Z"
        let b =
            storedWithClocks "b" "wt" SessionLevelStatus.Idle None None None
                "2026-03-01T11:58:00Z"
                "2026-03-01T11:58:00Z"

        Assert.That(getLastSessionId [ a; b ], Is.EqualTo(Some "b"))
