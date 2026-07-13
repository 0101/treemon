module Tests.CopilotDetectorTests

open System
open System.IO
open NUnit.Framework
open Server.CopilotDetector
open Shared

let private fixtureDir =
    Path.Combine(Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location), "fixtures", "copilot")
    |> Path.GetFullPath

let private eventsPath (sessionName: string) =
    Path.Combine(fixtureDir, sessionName, "events.jsonl")

let private workspacePath (sessionName: string) =
    Path.Combine(fixtureDir, sessionName, "workspace.yaml")

let private touchFixtures () =
    Directory.GetFiles(fixtureDir, "events.jsonl", SearchOption.AllDirectories)
    |> Array.iter (fun path -> File.SetLastWriteTimeUtc(path, DateTime.UtcNow))

let private recentTime = DateTimeOffset.UtcNow


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type StatusParsingTests() =

    [<OneTimeSetUp>]
    member _.Setup() = touchFixtures ()

    [<Test>]
    member _.``Last event user.message yields Working``() =
        let status = getStatusFromEventsFile (eventsPath "active-session") recentTime
        Assert.That(status, Is.EqualTo(Working))

    [<Test>]
    member _.``Last event assistant.turn_end yields Done after grace period``() =
        let pastGrace = DateTimeOffset.UtcNow.AddSeconds(20.0)
        let status = getStatusFromEventsFile (eventsPath "done-session") pastGrace
        Assert.That(status, Is.EqualTo(Done))

    [<Test>]
    member _.``Last event assistant.turn_end within grace period yields Working``() =
        let status = getStatusFromEventsFile (eventsPath "done-session") recentTime
        Assert.That(status, Is.EqualTo(Working))

    [<Test>]
    member _.``Assistant.message with tool requests yields Working``() =
        let status = getStatusFromEventsFile (eventsPath "working-with-tools") recentTime
        Assert.That(status, Is.EqualTo(Working))

    [<Test>]
    member _.``Assistant.message with ask_user tool yields WaitingForUser``() =
        let status = getStatusFromEventsFile (eventsPath "ask-user-session") recentTime
        Assert.That(status, Is.EqualTo(WaitingForUser))

    [<Test>]
    member _.``Non-existent events file yields Idle``() =
        let status = getStatusFromEventsFile (Path.Combine(fixtureDir, "nonexistent", "events.jsonl")) recentTime
        Assert.That(status, Is.EqualTo(Idle))


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type StalenessTests() =

    [<OneTimeSetUp>]
    member _.Setup() = touchFixtures ()

    [<Test>]
    member _.``Events file older than 2 hours yields Idle``() =
        let farFuture = DateTimeOffset.UtcNow.AddHours(3.0)
        let status = getStatusFromEventsFile (eventsPath "active-session") farFuture
        Assert.That(status, Is.EqualTo(Idle))

    [<Test>]
    member _.``Events file within 2 hours yields non-Idle status``() =
        let status = getStatusFromEventsFile (eventsPath "active-session") recentTime
        Assert.That(status, Is.Not.EqualTo(Idle))

    [<Test>]
    member _.``Working status goes stale after 30 minutes``() =
        let staleTime = DateTimeOffset.UtcNow.AddMinutes(35.0)
        let status = getStatusFromEventsFile (eventsPath "active-session") staleTime
        Assert.That(status, Is.EqualTo(Idle))


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type WorkspaceParsingTests() =

    [<Test>]
    member _.``Parses cwd from workspace.yaml``() =
        let cwd = parseCwd (workspacePath "active-session")
        Assert.That(cwd, Is.EqualTo(Some @"Q:\code\TestProject"))

    [<Test>]
    member _.``Returns None for non-existent yaml``() =
        let cwd = parseCwd (Path.Combine(fixtureDir, "nonexistent", "workspace.yaml"))
        Assert.That(cwd, Is.EqualTo(None))

    [<Test>]
    member _.``Parses different cwd values from different sessions``() =
        let cwd1 = parseCwd (workspacePath "active-session")
        let cwd2 = parseCwd (workspacePath "done-session")
        Assert.That(cwd1, Is.EqualTo(Some @"Q:\code\TestProject"))
        Assert.That(cwd2, Is.EqualTo(Some @"Q:\code\DoneProject"))


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type LastMessageTests() =

    // The last assistant message now comes off the incremental forward-fold scan rather than a
    // dedicated backward read; getSessionScanForFile.LastAssistantMessage is the (text, timestamp) pair.
    let lastAssistant (name: string) =
        getSessionScanForFile (eventsPath name) |> Option.bind _.LastAssistantMessage

    [<Test>]
    member _.``Extracts last assistant message from done session``() =
        let msg = lastAssistant "done-session"
        Assert.That(msg.IsSome, Is.True)
        Assert.That(fst msg.Value, Does.Contain("simple web server"))

    [<Test>]
    member _.``Extracts last assistant message from working-with-tools session``() =
        let msg = lastAssistant "working-with-tools"
        Assert.That(msg.IsSome, Is.True)
        Assert.That(fst msg.Value, Does.Contain("refactor"))

    [<Test>]
    member _.``Extracts last assistant message from ask-user session``() =
        let msg = lastAssistant "ask-user-session"
        Assert.That(msg.IsSome, Is.True)
        Assert.That(fst msg.Value, Does.Contain("deployment target"))

    [<Test>]
    member _.``Returns None for non-existent file``() =
        let msg =
            getSessionScanForFile (Path.Combine(fixtureDir, "nonexistent", "events.jsonl"))
            |> Option.bind _.LastAssistantMessage
        Assert.That(msg, Is.EqualTo(None))

    [<Test>]
    member _.``Timestamp is parsed from event``() =
        let msg = lastAssistant "done-session"
        Assert.That(msg.IsSome, Is.True)
        Assert.That((snd msg.Value).Year, Is.EqualTo(2026))


let private makeUserEvent (text: string) (timestamp: string) =
    $"""{{"type":"user.message","data":{{"content":"{text}"}},"timestamp":"{timestamp}"}}"""

let private makeAssistantEvent (text: string) (timestamp: string) =
    $"""{{"type":"assistant.message","data":{{"content":"{text}","toolRequests":[]}},"timestamp":"{timestamp}"}}"""

let private makeTurnEnd (timestamp: string) =
    $"""{{"type":"assistant.turn_end","data":{{"turnId":"0"}},"timestamp":"{timestamp}"}}"""

let private makeToolEvent (timestamp: string) =
    $"""{{"type":"tool.execution_complete","data":{{"name":"powershell"}},"timestamp":"{timestamp}"}}"""

let private makeLargeToolEvent (sizeBytes: int) (timestamp: string) =
    let padding = String('x', sizeBytes)
    $"""{{"type":"tool.execution_complete","data":{{"name":"read_agent","output":"{padding}"}},"timestamp":"{timestamp}"}}"""

let private withTempEventsFile content action =
    TestUtils.withTempFile "copilot-test" content action


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type LastUserMessageTests() =

    [<Test>]
    member _.``Finds user message near end of file``() =
        let events =
            [ makeUserEvent "hello copilot" "2026-03-01T10:00:00Z"
              makeAssistantEvent "hi there" "2026-03-01T10:00:01Z"
              makeTurnEnd "2026-03-01T10:00:02Z" ]

        let result = (scanSessionEvents events).LastUserMessage
        Assert.That(result.IsSome, Is.True)
        let text, _ = result.Value
        Assert.That(text, Is.EqualTo("hello copilot"))

    [<Test>]
    member _.``Finds user message buried under many tool events``() =
        let toolEvents =
            Seq.init 200 (fun i ->
                let ts = $"2026-03-01T10:%02d{(i / 60) + 1}:%02d{i % 60}Z"
                makeToolEvent ts)
            |> Seq.toList

        let events =
            [ makeUserEvent "my deep question" "2026-03-01T10:00:00Z" ]
            @ toolEvents
            @ [ makeAssistantEvent "done" "2026-03-01T10:05:00Z"
                makeTurnEnd "2026-03-01T10:05:01Z" ]

        let result = (scanSessionEvents events).LastUserMessage
        Assert.That(result.IsSome, Is.True)
        let text, _ = result.Value
        Assert.That(text, Is.EqualTo("my deep question"))

    [<Test>]
    member _.``Returns None for empty stream``() =
        let result = (scanSessionEvents List.empty).LastUserMessage
        Assert.That(result, Is.EqualTo(None))

    [<Test>]
    member _.``Returns None when no user message exists``() =
        let events =
            [ makeAssistantEvent "orphan message" "2026-03-01T10:00:00Z"
              makeTurnEnd "2026-03-01T10:00:01Z" ]

        let result = (scanSessionEvents events).LastUserMessage
        Assert.That(result, Is.EqualTo(None))


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type LargeToolOutputTests() =

    [<Test>]
    member _.``Status is Working when large tool outputs exceed 64KB buffer``() =
        let content =
            [ makeAssistantEvent "Now let me read all 6 findings" "2026-03-01T10:00:00Z"
              makeLargeToolEvent 20000 "2026-03-01T10:00:01Z"
              makeLargeToolEvent 20000 "2026-03-01T10:00:02Z"
              makeLargeToolEvent 20000 "2026-03-01T10:00:03Z"
              makeLargeToolEvent 20000 "2026-03-01T10:00:04Z" ]
            |> String.concat Environment.NewLine

        withTempEventsFile content (fun path ->
            let status = getStatusFromEventsFile path DateTimeOffset.UtcNow
            Assert.That(status, Is.EqualTo(Working)))

    [<Test>]
    member _.``Status is Done when turn_end follows large tool outputs after grace period``() =
        let content =
            [ makeAssistantEvent "processing results" "2026-03-01T10:00:00Z"
              makeLargeToolEvent 20000 "2026-03-01T10:00:01Z"
              makeLargeToolEvent 20000 "2026-03-01T10:00:02Z"
              makeLargeToolEvent 20000 "2026-03-01T10:00:03Z"
              makeLargeToolEvent 20000 "2026-03-01T10:00:04Z"
              makeAssistantEvent "all done" "2026-03-01T10:00:05Z"
              makeTurnEnd "2026-03-01T10:00:06Z" ]
            |> String.concat Environment.NewLine

        withTempEventsFile content (fun path ->
            let pastGrace = DateTimeOffset.UtcNow.AddSeconds(20.0)
            let status = getStatusFromEventsFile path pastGrace
            Assert.That(status, Is.EqualTo(Done)))


let private makeSkillInvoked (name: string) (timestamp: string) =
    $"""{{"type":"skill.invoked","data":{{"name":"{name}","path":"x/SKILL.md","content":"body"}},"timestamp":"{timestamp}"}}"""

let private makeSkillToolCall (skill: string) (timestamp: string) =
    $"""{{"type":"assistant.message","data":{{"content":"invoking","toolRequests":[{{"toolCallId":"tc1","name":"skill","arguments":{{"skill":"{skill}"}},"type":"function"}}]}},"timestamp":"{timestamp}"}}"""

let private makeSkillToolCallJsonArgs (skill: string) (timestamp: string) =
    // arguments encoded as a JSON string (arguments_json) rather than a nested object
    $"""{{"type":"assistant.message","data":{{"content":"invoking","toolRequests":[{{"toolCallId":"tc1","name":"skill","arguments_json":"{{\"skill\":\"{skill}\"}}","type":"function"}}]}},"timestamp":"{timestamp}"}}"""

let private makeSkillContextEvent (name: string) (timestamp: string) =
    // Copilot injects a skill's context as a synthetic user.message right after skill.invoked:
    // source "skill-<name>" plus a "<skill-context …>" preamble. It must NOT end the skill's run.
    $"""{{"type":"user.message","data":{{"source":"skill-{name}","content":"<skill-context name={name}>"}},"timestamp":"{timestamp}"}}"""

let private makeAskUserEvent (question: string) (timestamp: string) =
    // An assistant.message requesting the ask_user tool: the agent parks on the user (WaitingForUser)
    // mid-skill; the user's next user.message is the reply, not a new request.
    $"""{{"type":"assistant.message","data":{{"content":"{question}","toolRequests":[{{"toolCallId":"tc-ask","name":"ask_user","type":"function"}}]}},"timestamp":"{timestamp}"}}"""

let private makeAskUserToolComplete (timestamp: string) =
    // The ask_user tool-execution row that sits between the ask_user request and the reply; the skill
    // scan must ignore it (only the assistant.message request marks the boundary as an ask_user one).
    $"""{{"type":"tool.execution_complete","data":{{"name":"ask_user"}},"timestamp":"{timestamp}"}}"""


let private makeSubagentStarted (toolCallId: string) (timestamp: string) =
    // A Task/sub-agent's events (assistant/tool/hook rows and even its own skill.invoked) run between
    // its subagent.started and the matching subagent.completed, keyed by an identical toolCallId.
    $"""{{"type":"subagent.started","data":{{"toolCallId":"{toolCallId}","agent":"bd-phase-executor"}},"timestamp":"{timestamp}"}}"""

let private makeSubagentCompleted (toolCallId: string) (timestamp: string) =
    $"""{{"type":"subagent.completed","data":{{"toolCallId":"{toolCallId}"}},"timestamp":"{timestamp}"}}"""

/// The exact event streams of the CurrentSkillTests scenarios, paired with the skill each should
/// report. Used to prove the forward fold is equivalent to the backward scanSkill (and returns the
/// same known-good values) on every no-sub-agent scenario.
let private skillEquivalenceScenarios: (string * string list * string option) list =
    [ "skill.invoked event yields data.name",
      [ makeUserEvent "please fix the build" "2026-03-01T10:00:00Z"
        makeSkillToolCall "fix-build" "2026-03-01T10:00:01Z"
        makeSkillInvoked "fix-build" "2026-03-01T10:00:02Z" ],
      Some "fix-build"

      "skill tool-call arguments.skill used when no skill.invoked follows",
      [ makeUserEvent "investigate this" "2026-03-01T10:00:00Z"
        makeSkillToolCall "investigate" "2026-03-01T10:00:01Z" ],
      Some "investigate"

      "most recent skill wins across multiple invocations",
      [ makeSkillToolCall "investigate" "2026-03-01T10:00:00Z"
        makeSkillInvoked "investigate" "2026-03-01T10:00:01Z"
        makeSkillToolCall "bd-execute" "2026-03-01T10:05:00Z" ],
      Some "bd-execute"

      "non-skill tool-call after skill.invoked is skipped",
      [ makeSkillInvoked "fix-build" "2026-03-01T10:00:00Z"
        makeAssistantEvent "editing a file" "2026-03-01T10:00:01Z"
        makeToolEvent "2026-03-01T10:00:02Z"
        makeTurnEnd "2026-03-01T10:00:03Z" ],
      Some "fix-build"

      "arguments encoded as a JSON string still yields skill",
      [ makeSkillToolCallJsonArgs "refactor" "2026-03-01T10:00:00Z" ],
      Some "refactor"

      "session with no skill signal yields None",
      [ makeUserEvent "hello" "2026-03-01T10:00:00Z"
        makeAssistantEvent "hi" "2026-03-01T10:00:01Z"
        makeTurnEnd "2026-03-01T10:00:02Z" ],
      None

      "a skill that finished before a new user request no longer lingers",
      [ makeUserEvent "plan the feature" "2026-03-01T10:00:00Z"
        makeSkillInvoked "bd-plan" "2026-03-01T10:00:01Z"
        makeAssistantEvent "planning complete" "2026-03-01T10:00:02Z"
        makeTurnEnd "2026-03-01T10:00:03Z"
        makeUserEvent "now something unrelated" "2026-03-01T10:05:00Z"
        makeAssistantEvent "on it" "2026-03-01T10:05:01Z" ],
      None

      "a running skill is reported past its own skill-context injection",
      [ makeUserEvent "/bd-execute my-feature" "2026-03-01T10:00:00Z"
        makeSkillToolCall "bd-execute" "2026-03-01T10:00:01Z"
        makeSkillInvoked "bd-execute" "2026-03-01T10:00:02Z"
        makeSkillContextEvent "bd-execute" "2026-03-01T10:00:03Z"
        makeAssistantEvent "orchestrating subagents" "2026-03-01T10:00:04Z"
        makeTurnEnd "2026-03-01T10:00:05Z" ],
      Some "bd-execute"

      "a skill re-invoked after a new request is reported",
      [ makeSkillInvoked "bd-plan" "2026-03-01T10:00:00Z"
        makeTurnEnd "2026-03-01T10:00:01Z"
        makeUserEvent "please review the branch" "2026-03-01T10:05:00Z"
        makeSkillInvoked "review" "2026-03-01T10:05:01Z"
        makeAssistantEvent "reviewing" "2026-03-01T10:05:02Z" ],
      Some "review"

      "a running skill is reported across an ask_user reply that resumes it",
      [ makeUserEvent "/review the changes" "2026-03-01T10:00:00Z"
        makeSkillToolCall "review" "2026-03-01T10:00:01Z"
        makeSkillInvoked "review" "2026-03-01T10:00:02Z"
        makeSkillContextEvent "review" "2026-03-01T10:00:03Z"
        makeAssistantEvent "let me look at the diff" "2026-03-01T10:00:04Z"
        makeAskUserEvent "which file should I focus on?" "2026-03-01T10:00:05Z"
        makeAskUserToolComplete "2026-03-01T10:01:00Z"
        makeUserEvent "the auth module" "2026-03-01T10:01:01Z"
        makeAssistantEvent "reviewing the auth module" "2026-03-01T10:01:02Z"
        makeTurnEnd "2026-03-01T10:01:03Z" ],
      Some "review"

      "a genuine new request after ordinary work still ends the prior skill",
      [ makeUserEvent "/review the changes" "2026-03-01T10:00:00Z"
        makeSkillInvoked "review" "2026-03-01T10:00:01Z"
        makeAssistantEvent "review complete" "2026-03-01T10:00:02Z"
        makeTurnEnd "2026-03-01T10:00:03Z"
        makeUserEvent "now bump the version" "2026-03-01T10:05:00Z"
        makeAssistantEvent "on it" "2026-03-01T10:05:01Z" ],
      None

      "a user message merely beginning with skill-context text is a genuine boundary",
      [ makeUserEvent "/bd-plan the feature" "2026-03-01T10:00:00Z"
        makeSkillInvoked "bd-plan" "2026-03-01T10:00:01Z"
        makeAssistantEvent "planning complete" "2026-03-01T10:00:02Z"
        makeTurnEnd "2026-03-01T10:00:03Z"
        makeUserEvent "<skill-context> is a tag I want you to document" "2026-03-01T10:05:00Z"
        makeAssistantEvent "sure, documenting it" "2026-03-01T10:05:01Z" ],
      None

      "an unanswered ask_user mid-skill still reports the running skill",
      [ makeUserEvent "/investigate the flake" "2026-03-01T10:00:00Z"
        makeSkillInvoked "investigate" "2026-03-01T10:00:01Z"
        makeSkillContextEvent "investigate" "2026-03-01T10:00:02Z"
        makeAssistantEvent "digging in" "2026-03-01T10:00:03Z"
        makeAskUserEvent "can you share the failing run URL?" "2026-03-01T10:00:04Z" ],
      Some "investigate" ]


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type ForwardFoldTests() =

    [<Test>]
    member _.``Forward fold CurrentSkill matches the expected skill on every CurrentSkill scenario``() =
        // The forward fold (oldest→newest) reports the running skill correctly on every scenario without
        // sub-agent nesting (depth stays 0, so the gating the fold adds is a no-op). skillEquivalenceScenarios
        // carries the known-good expected skill for each — the scenarios the removed backward scan was
        // verified against, now the fold's baseline.
        skillEquivalenceScenarios
        |> List.iter (fun (name, events, expected) ->
            let forward = (scanSessionEvents events).CurrentSkill
            Assert.That(forward, Is.EqualTo(expected), $"forward vs expected mismatch: {name}"))

    [<Test>]
    member _.``skill.invoked megabytes before the tail is still detected by the forward fold``() =
        // The whole point: with ~2 MB of autonomous tool output after it, the skill.invoked would scroll
        // past the ~1 MB tail the removed backward scan read (degrading it to None), but the forward fold
        // sees the entire stream and still reports the skill.
        let events =
            [ makeUserEvent "/investigate the flake" "2026-03-01T10:00:00Z"
              makeSkillInvoked "investigate" "2026-03-01T10:00:01Z" ]
            @ (List.init 1000 (fun _ -> makeLargeToolEvent 2000 "2026-03-01T10:00:05Z"))
            @ [ makeAssistantEvent "still grinding" "2026-03-01T10:30:00Z"
                makeTurnEnd "2026-03-01T10:30:01Z" ]

        let forward = (scanSessionEvents events).CurrentSkill
        Assert.That(forward, Is.EqualTo(Some "investigate"))

    [<Test>]
    member _.``A skill-context injection is never recorded as the last user message``() =
        let events =
            [ makeUserEvent "/bd-execute my-feature" "2026-03-01T10:00:00Z"
              makeSkillToolCall "bd-execute" "2026-03-01T10:00:01Z"
              makeSkillInvoked "bd-execute" "2026-03-01T10:00:02Z"
              makeSkillContextEvent "bd-execute" "2026-03-01T10:00:03Z"
              makeAssistantEvent "orchestrating subagents" "2026-03-01T10:00:04Z"
              makeTurnEnd "2026-03-01T10:00:05Z" ]

        let scan = scanSessionEvents events
        Assert.That(scan.CurrentSkill, Is.EqualTo(Some "bd-execute"))

        match scan.LastUserMessage with
        | Some(text, _) ->
            Assert.That(text, Is.EqualTo("/bd-execute my-feature"))
            Assert.That(text, Does.Not.Contain("skill-context"))
        | None -> Assert.Fail("Expected the genuine pre-skill prompt as LastUserMessage")

    [<Test>]
    member _.``An ask_user reply keeps the running skill and is recorded as the last user message``() =
        let events =
            [ makeUserEvent "/review the changes" "2026-03-01T10:00:00Z"
              makeSkillInvoked "review" "2026-03-01T10:00:01Z"
              makeSkillContextEvent "review" "2026-03-01T10:00:02Z"
              makeAssistantEvent "let me look at the diff" "2026-03-01T10:00:03Z"
              makeAskUserEvent "which file should I focus on?" "2026-03-01T10:00:04Z"
              makeAskUserToolComplete "2026-03-01T10:01:00Z"
              makeUserEvent "the auth module" "2026-03-01T10:01:01Z"
              makeAssistantEvent "reviewing the auth module" "2026-03-01T10:01:02Z" ]

        let scan = scanSessionEvents events
        Assert.That(scan.CurrentSkill, Is.EqualTo(Some "review"))
        Assert.That(scan.LastUserMessage |> Option.map fst, Is.EqualTo(Some "the auth module"))

    [<Test>]
    member _.``A sub-agent's skill.invoked does not overwrite the user's top-level skill``() =
        // Mirrors the Step 0 finding: a sub-agent's skill.invoked bubbles into the parent stream,
        // nested inside its subagent.started/…completed bracket. Depth gating keeps the top-level
        // bd-execute reported rather than the deeper vs-local-development.
        let events =
            [ makeUserEvent "/bd-execute AITestAgent-9ce" "2026-03-01T10:00:00Z"
              makeSkillInvoked "bd-execute" "2026-03-01T10:00:01Z"
              makeSkillContextEvent "bd-execute" "2026-03-01T10:00:02Z"
              makeAssistantEvent "spawning a sub-agent" "2026-03-01T10:00:03Z"
              makeSubagentStarted "toolu_01FNMZ" "2026-03-01T10:05:00Z"
              makeAssistantEvent "sub-agent working" "2026-03-01T10:05:01Z"
              makeSkillInvoked "vs-local-development" "2026-03-01T10:05:02Z"
              makeAssistantEvent "sub-agent still working" "2026-03-01T10:05:03Z"
              makeSubagentCompleted "toolu_01FNMZ" "2026-03-01T10:10:00Z"
              makeAssistantEvent "back at the top level" "2026-03-01T10:10:01Z"
              makeTurnEnd "2026-03-01T10:10:02Z" ]

        let scan = scanSessionEvents events
        Assert.That(scan.CurrentSkill, Is.EqualTo(Some "bd-execute"))

    [<Test>]
    member _.``A top-level skill invoked after a sub-agent block is still reported``() =
        // Depth returns to 0 on subagent.completed, so a genuine top-level skill afterwards is set.
        let events =
            [ makeUserEvent "/bd-execute the feature" "2026-03-01T10:00:00Z"
              makeSubagentStarted "toolu_a" "2026-03-01T10:00:01Z"
              makeSkillInvoked "vs-local-development" "2026-03-01T10:00:02Z"
              makeSubagentCompleted "toolu_a" "2026-03-01T10:00:03Z"
              makeSkillInvoked "bd-execute" "2026-03-01T10:00:04Z"
              makeAssistantEvent "orchestrating" "2026-03-01T10:00:05Z" ]

        let scan = scanSessionEvents events
        Assert.That(scan.CurrentSkill, Is.EqualTo(Some "bd-execute"))

    [<Test>]
    member _.``Sub-agent depth never goes negative on an unmatched completed``() =
        // A subagent.completed with no open bracket (e.g. the started event scrolled off before a full
        // rescan window) must not drive depth below 0 and start gating top-level skills.
        let events =
            [ makeSubagentCompleted "orphan" "2026-03-01T10:00:00Z"
              makeUserEvent "/investigate the flake" "2026-03-01T10:00:01Z"
              makeSkillInvoked "investigate" "2026-03-01T10:00:02Z" ]

        let scan = scanSessionEvents events
        Assert.That(scan.SubagentDepth, Is.EqualTo(0))
        Assert.That(scan.CurrentSkill, Is.EqualTo(Some "investigate"))

    [<Test>]
    member _.``Folding in appended batches equals folding the whole stream at once``() =
        // The incremental cache appends new bytes onto the prior fold state; that must match a full
        // rescan. Split a scenario's events into two batches and fold the second onto the first.
        let events =
            [ makeUserEvent "/review the changes" "2026-03-01T10:00:00Z"
              makeSkillInvoked "review" "2026-03-01T10:00:01Z"
              makeSkillContextEvent "review" "2026-03-01T10:00:02Z"
              makeAssistantEvent "let me look at the diff" "2026-03-01T10:00:03Z"
              makeAskUserEvent "which file should I focus on?" "2026-03-01T10:00:04Z"
              makeUserEvent "the auth module" "2026-03-01T10:01:01Z"
              makeAssistantEvent "reviewing the auth module" "2026-03-01T10:01:02Z"
              makeTurnEnd "2026-03-01T10:01:03Z" ]

        let whole = scanSessionEvents events

        let firstBatch, secondBatch = List.splitAt 4 events
        let incremental = foldSessionEvents (scanSessionEvents firstBatch) secondBatch

        Assert.That(incremental, Is.EqualTo(whole))

    [<Test>]
    member _.``Empty stream yields the empty scan state``() =
        let scan = scanSessionEvents []
        Assert.That(scan.CurrentSkill, Is.EqualTo(None))
        Assert.That(scan.LastUserMessage, Is.EqualTo(None))
        Assert.That(scan.LastAssistantMessage, Is.EqualTo(None))
        Assert.That(scan.RawStatus, Is.EqualTo(Idle))
        Assert.That(scan.SubagentDepth, Is.EqualTo(0))


/// Join event lines the way a real events.jsonl is written: every line, including the last, is
/// terminated by a newline. A line with no trailing newline is a partial (still-being-written) line.
let private joinedLines (events: string list) =
    (String.concat Environment.NewLine events) + Environment.NewLine

let private lastUser (scan: SessionScanCache option) =
    scan |> Option.bind _.LastUserMessage |> Option.map fst


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type IncrementalSessionScanTests() =

    let sampleSession () =
        [ makeUserEvent "/review the changes" "2026-03-01T10:00:00Z"
          makeSkillInvoked "review" "2026-03-01T10:00:01Z"
          makeSkillContextEvent "review" "2026-03-01T10:00:02Z"
          makeAssistantEvent "let me look at the diff" "2026-03-01T10:00:03Z"
          makeAskUserEvent "which file should I focus on?" "2026-03-01T10:00:04Z"
          makeUserEvent "the auth module" "2026-03-01T10:01:01Z"
          makeAssistantEvent "reviewing the auth module" "2026-03-01T10:01:02Z"
          makeTurnEnd "2026-03-01T10:01:03Z" ]

    [<Test>]
    member _.``Incremental fold across appended batches equals a full scan``() =
        // The core guarantee: folding a later batch onto the cached state from an earlier batch must
        // match a full rescan of the whole stream — exercised through the file + cache boundary.
        let events = sampleSession ()
        let batch1, batch2 = List.splitAt 4 events

        withTempEventsFile (joinedLines batch1) (fun path ->
            let first = getSessionScanForFile path
            Assert.That(first, Is.EqualTo(Some(scanSessionEvents batch1)))

            File.AppendAllText(path, joinedLines batch2)
            let second = getSessionScanForFile path

            Assert.That(second, Is.EqualTo(Some(scanSessionEvents events)))
            // The whole file (all lines newline-terminated) has been consumed.
            Assert.That(peekSessionScanCacheLength path, Is.EqualTo(Some(FileInfo(path).Length))))

    [<Test>]
    member _.``A partial trailing line is not folded until its newline arrives``() =
        let batch1 =
            [ makeUserEvent "first question" "2026-03-01T10:00:00Z"
              makeAssistantEvent "first answer" "2026-03-01T10:00:01Z"
              makeTurnEnd "2026-03-01T10:00:02Z" ]

        withTempEventsFile (joinedLines batch1) (fun path ->
            let afterBatch1 = getSessionScanForFile path
            Assert.That(lastUser afterBatch1, Is.EqualTo(Some "first question"))
            let consumedAfterBatch1 = peekSessionScanCacheLength path

            // A line appended WITHOUT its terminating newline is partial: not parsed, offset unmoved.
            File.AppendAllText(path, makeUserEvent "second question" "2026-03-01T10:05:00Z")
            let afterPartial = getSessionScanForFile path
            Assert.That(lastUser afterPartial, Is.EqualTo(Some "first question"))
            Assert.That(peekSessionScanCacheLength path, Is.EqualTo(consumedAfterBatch1))

            // Completing the line with a newline folds it in on the next scan.
            File.AppendAllText(path, Environment.NewLine)
            let afterComplete = getSessionScanForFile path
            Assert.That(lastUser afterComplete, Is.EqualTo(Some "second question")))

    [<Test>]
    member _.``A shrunk (rotated) file triggers a full rescan, not a stale incremental fold``() =
        let original =
            [ makeUserEvent "/review the changes" "2026-03-01T10:00:00Z"
              makeSkillInvoked "review" "2026-03-01T10:00:01Z"
              makeAssistantEvent "reviewing" "2026-03-01T10:00:02Z"
              makeAssistantEvent "still reviewing" "2026-03-01T10:00:03Z"
              makeTurnEnd "2026-03-01T10:00:04Z" ]

        withTempEventsFile (joinedLines original) (fun path ->
            let before = getSessionScanForFile path
            Assert.That(before |> Option.bind _.CurrentSkill, Is.EqualTo(Some "review"))

            // Rotation: the file is replaced by a shorter one. The cached offset now exceeds the new
            // length, so the cached fold must be discarded and the new file scanned from zero — had the
            // shrink gone undetected the read range would be empty and the stale "review" skill linger.
            let rotated = [ makeUserEvent "brand new session" "2026-03-01T11:00:00Z" ]
            File.WriteAllText(path, joinedLines rotated)

            let after = getSessionScanForFile path
            Assert.That(after, Is.EqualTo(Some(scanSessionEvents rotated)))
            Assert.That(after |> Option.bind _.CurrentSkill, Is.EqualTo(None))
            Assert.That(lastUser after, Is.EqualTo(Some "brand new session"))
            Assert.That(peekSessionScanCacheLength path, Is.EqualTo(Some(FileInfo(path).Length))))

    [<Test>]
    member _.``Pruning drops entries whose file has gone stale past the 2h idle cutoff``() =
        withTempEventsFile (joinedLines (sampleSession ())) (fun path ->
            getSessionScanForFile path |> ignore
            Assert.That(peekSessionScanCacheLength path |> Option.isSome, Is.True)

            // A fresh file (mtime ~now) is kept.
            pruneSessionScanCache DateTimeOffset.UtcNow
            Assert.That(peekSessionScanCacheLength path |> Option.isSome, Is.True)

            // Evaluated 3 h past the file's mtime, the entry is older than the 2 h Idle cutoff → pruned.
            pruneSessionScanCache (DateTimeOffset.UtcNow.AddHours(3.0))
            Assert.That(peekSessionScanCacheLength path, Is.EqualTo(None)))

    [<Test>]
    member _.``Pruning drops entries whose file has vanished``() =
        withTempEventsFile (joinedLines (sampleSession ())) (fun path ->
            getSessionScanForFile path |> ignore
            Assert.That(peekSessionScanCacheLength path |> Option.isSome, Is.True)

            File.Delete(path)
            pruneSessionScanCache DateTimeOffset.UtcNow
            Assert.That(peekSessionScanCacheLength path, Is.EqualTo(None)))

