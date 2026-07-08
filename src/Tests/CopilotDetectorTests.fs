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

    [<Test>]
    member _.``Extracts last assistant message from done session``() =
        let msg = getLastMessageFromEventsFile (eventsPath "done-session")
        Assert.That(msg.IsSome, Is.True)
        Assert.That(msg.Value.Message, Does.Contain("simple web server"))
        Assert.That(msg.Value.Source, Is.EqualTo("copilot"))

    [<Test>]
    member _.``Extracts last assistant message from working-with-tools session``() =
        let msg = getLastMessageFromEventsFile (eventsPath "working-with-tools")
        Assert.That(msg.IsSome, Is.True)
        Assert.That(msg.Value.Message, Does.Contain("refactor"))

    [<Test>]
    member _.``Extracts last assistant message from ask-user session``() =
        let msg = getLastMessageFromEventsFile (eventsPath "ask-user-session")
        Assert.That(msg.IsSome, Is.True)
        Assert.That(msg.Value.Message, Does.Contain("deployment target"))

    [<Test>]
    member _.``Returns None for non-existent file``() =
        let msg = getLastMessageFromEventsFile (Path.Combine(fixtureDir, "nonexistent", "events.jsonl"))
        Assert.That(msg, Is.EqualTo(None))

    [<Test>]
    member _.``Timestamp is parsed from event``() =
        let msg = getLastMessageFromEventsFile (eventsPath "done-session")
        Assert.That(msg.IsSome, Is.True)
        Assert.That(msg.Value.Timestamp.Year, Is.EqualTo(2026))


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
type ScanForUserMessageTests() =

    [<Test>]
    member _.``Finds user message near end of file``() =
        let content =
            [ makeUserEvent "hello copilot" "2026-03-01T10:00:00Z"
              makeAssistantEvent "hi there" "2026-03-01T10:00:01Z"
              makeTurnEnd "2026-03-01T10:00:02Z" ]
            |> String.concat Environment.NewLine

        withTempEventsFile content (fun path ->
            let result = scanForUserMessage path
            Assert.That(result.IsSome, Is.True)
            let text, _ = result.Value
            Assert.That(text, Is.EqualTo("hello copilot")))

    [<Test>]
    member _.``Finds user message buried under many tool events``() =
        let toolEvents =
            Seq.init 200 (fun i ->
                let ts = $"2026-03-01T10:%02d{(i / 60) + 1}:%02d{i % 60}Z"
                makeToolEvent ts)
            |> Seq.toList

        let content =
            [ makeUserEvent "my deep question" "2026-03-01T10:00:00Z" ]
            @ toolEvents
            @ [ makeAssistantEvent "done" "2026-03-01T10:05:00Z"
                makeTurnEnd "2026-03-01T10:05:01Z" ]
            |> String.concat Environment.NewLine

        withTempEventsFile content (fun path ->
            let result = scanForUserMessage path
            Assert.That(result.IsSome, Is.True)
            let text, _ = result.Value
            Assert.That(text, Is.EqualTo("my deep question")))

    [<Test>]
    member _.``Returns None for empty file``() =
        withTempEventsFile "" (fun path ->
            let result = scanForUserMessage path
            Assert.That(result, Is.EqualTo(None)))

    [<Test>]
    member _.``Returns None when no user message exists``() =
        let content =
            [ makeAssistantEvent "orphan message" "2026-03-01T10:00:00Z"
              makeTurnEnd "2026-03-01T10:00:01Z" ]
            |> String.concat Environment.NewLine

        withTempEventsFile content (fun path ->
            let result = scanForUserMessage path
            Assert.That(result, Is.EqualTo(None)))


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


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type CurrentSkillTests() =

    [<Test>]
    member _.``skill.invoked event yields data.name``() =
        let content =
            [ makeUserEvent "please fix the build" "2026-03-01T10:00:00Z"
              makeSkillToolCall "fix-build" "2026-03-01T10:00:01Z"
              makeSkillInvoked "fix-build" "2026-03-01T10:00:02Z" ]
            |> String.concat Environment.NewLine

        withTempEventsFile content (fun path ->
            Assert.That(getCurrentSkillFromEventsFile path, Is.EqualTo(Some "fix-build")))

    [<Test>]
    member _.``skill tool-call arguments.skill is used when no skill.invoked follows``() =
        let content =
            [ makeUserEvent "investigate this" "2026-03-01T10:00:00Z"
              makeSkillToolCall "investigate" "2026-03-01T10:00:01Z" ]
            |> String.concat Environment.NewLine

        withTempEventsFile content (fun path ->
            Assert.That(getCurrentSkillFromEventsFile path, Is.EqualTo(Some "investigate")))

    [<Test>]
    member _.``Most recent skill wins across multiple invocations``() =
        let content =
            [ makeSkillToolCall "investigate" "2026-03-01T10:00:00Z"
              makeSkillInvoked "investigate" "2026-03-01T10:00:01Z"
              makeSkillToolCall "bd-execute" "2026-03-01T10:05:00Z" ]
            |> String.concat Environment.NewLine

        withTempEventsFile content (fun path ->
            Assert.That(getCurrentSkillFromEventsFile path, Is.EqualTo(Some "bd-execute")))

    [<Test>]
    member _.``Non-skill tool-call after skill.invoked is skipped``() =
        // A regular assistant.message with a non-skill tool-call sits between the skill signal and
        // EOF; the backward scan must step past it and still return the running skill.
        let content =
            [ makeSkillInvoked "fix-build" "2026-03-01T10:00:00Z"
              makeAssistantEvent "editing a file" "2026-03-01T10:00:01Z"
              makeToolEvent "2026-03-01T10:00:02Z"
              makeTurnEnd "2026-03-01T10:00:03Z" ]
            |> String.concat Environment.NewLine

        withTempEventsFile content (fun path ->
            Assert.That(getCurrentSkillFromEventsFile path, Is.EqualTo(Some "fix-build")))

    [<Test>]
    member _.``arguments encoded as a JSON string still yields skill``() =
        let content =
            [ makeSkillToolCallJsonArgs "refactor" "2026-03-01T10:00:00Z" ]
            |> String.concat Environment.NewLine

        withTempEventsFile content (fun path ->
            Assert.That(getCurrentSkillFromEventsFile path, Is.EqualTo(Some "refactor")))

    [<Test>]
    member _.``Session with no skill signal yields None``() =
        let content =
            [ makeUserEvent "hello" "2026-03-01T10:00:00Z"
              makeAssistantEvent "hi" "2026-03-01T10:00:01Z"
              makeTurnEnd "2026-03-01T10:00:02Z" ]
            |> String.concat Environment.NewLine

        withTempEventsFile content (fun path ->
            Assert.That(getCurrentSkillFromEventsFile path, Is.EqualTo(None)))

    [<Test>]
    member _.``A skill that finished before a new user request no longer lingers``() =
        // v1.1 (i): a skill invoked earlier in a still-active session that has since finished must
        // not be reported. A genuine user.message after the skill signals a new request → None.
        let content =
            [ makeUserEvent "plan the feature" "2026-03-01T10:00:00Z"
              makeSkillInvoked "bd-plan" "2026-03-01T10:00:01Z"
              makeAssistantEvent "planning complete" "2026-03-01T10:00:02Z"
              makeTurnEnd "2026-03-01T10:00:03Z"
              makeUserEvent "now something unrelated" "2026-03-01T10:05:00Z"
              makeAssistantEvent "on it" "2026-03-01T10:05:01Z" ]
            |> String.concat Environment.NewLine

        withTempEventsFile content (fun path ->
            Assert.That(getCurrentSkillFromEventsFile path, Is.EqualTo(None)))

    [<Test>]
    member _.``A running skill is reported past its own skill-context injection``() =
        // The synthetic "<skill-context>" user.message Copilot writes right after skill.invoked is
        // part of the skill starting, not a new request, so the scan steps past it and reports the
        // running skill (e.g. a bd-execute orchestration with no genuine user.message since).
        let content =
            [ makeUserEvent "/bd-execute my-feature" "2026-03-01T10:00:00Z"
              makeSkillToolCall "bd-execute" "2026-03-01T10:00:01Z"
              makeSkillInvoked "bd-execute" "2026-03-01T10:00:02Z"
              makeSkillContextEvent "bd-execute" "2026-03-01T10:00:03Z"
              makeAssistantEvent "orchestrating subagents" "2026-03-01T10:00:04Z"
              makeTurnEnd "2026-03-01T10:00:05Z" ]
            |> String.concat Environment.NewLine

        withTempEventsFile content (fun path ->
            Assert.That(getCurrentSkillFromEventsFile path, Is.EqualTo(Some "bd-execute")))

    [<Test>]
    member _.``A skill re-invoked after a new request is reported, not the earlier finished one``() =
        let content =
            [ makeSkillInvoked "bd-plan" "2026-03-01T10:00:00Z"
              makeTurnEnd "2026-03-01T10:00:01Z"
              makeUserEvent "please review the branch" "2026-03-01T10:05:00Z"
              makeSkillInvoked "review" "2026-03-01T10:05:01Z"
              makeAssistantEvent "reviewing" "2026-03-01T10:05:02Z" ]
            |> String.concat Environment.NewLine

        withTempEventsFile content (fun path ->
            Assert.That(getCurrentSkillFromEventsFile path, Is.EqualTo(Some "review")))

    [<Test>]
    member _.``A running skill is reported across an ask_user reply that resumes it``() =
        // focused-review F5: mid-skill the agent asks the user a question (ask_user → WaitingForUser);
        // the user's reply is a plain user.message with no new skill.invoked, and the SAME skill
        // resumes on the next turn. That reply must NOT be treated as a request boundary — the skill
        // is still running, so it is still reported (not collapsed to generic Working).
        let content =
            [ makeUserEvent "/review the changes" "2026-03-01T10:00:00Z"
              makeSkillToolCall "review" "2026-03-01T10:00:01Z"
              makeSkillInvoked "review" "2026-03-01T10:00:02Z"
              makeSkillContextEvent "review" "2026-03-01T10:00:03Z"
              makeAssistantEvent "let me look at the diff" "2026-03-01T10:00:04Z"
              makeAskUserEvent "which file should I focus on?" "2026-03-01T10:00:05Z"
              makeAskUserToolComplete "2026-03-01T10:01:00Z"
              makeUserEvent "the auth module" "2026-03-01T10:01:01Z"
              makeAssistantEvent "reviewing the auth module" "2026-03-01T10:01:02Z"
              makeTurnEnd "2026-03-01T10:01:03Z" ]
            |> String.concat Environment.NewLine

        withTempEventsFile content (fun path ->
            Assert.That(getCurrentSkillFromEventsFile path, Is.EqualTo(Some "review")))

    [<Test>]
    member _.``A genuine new request after ordinary work still ends the prior skill``() =
        // The ask_user-awareness must not over-fire: when the assistant.message just before the new
        // user.message was ordinary work (no ask_user), the user.message IS a genuine boundary and
        // the earlier skill's run is over.
        let content =
            [ makeUserEvent "/review the changes" "2026-03-01T10:00:00Z"
              makeSkillInvoked "review" "2026-03-01T10:00:01Z"
              makeAssistantEvent "review complete" "2026-03-01T10:00:02Z"
              makeTurnEnd "2026-03-01T10:00:03Z"
              makeUserEvent "now bump the version" "2026-03-01T10:05:00Z"
              makeAssistantEvent "on it" "2026-03-01T10:05:01Z" ]
            |> String.concat Environment.NewLine

        withTempEventsFile content (fun path ->
            Assert.That(getCurrentSkillFromEventsFile path, Is.EqualTo(None)))

    [<Test>]
    member _.``A user message merely beginning with skill-context text is a genuine boundary``() =
        // focused-review F4: a normal user.message whose text happens to start with "<skill-context"
        // (but with no system-set skill source) must NOT masquerade as a skill's context injection.
        // If it did, it would skip the request boundary and resurrect the finished skill below it.
        let content =
            [ makeUserEvent "/bd-plan the feature" "2026-03-01T10:00:00Z"
              makeSkillInvoked "bd-plan" "2026-03-01T10:00:01Z"
              makeAssistantEvent "planning complete" "2026-03-01T10:00:02Z"
              makeTurnEnd "2026-03-01T10:00:03Z"
              makeUserEvent "<skill-context> is a tag I want you to document" "2026-03-01T10:05:00Z"
              makeAssistantEvent "sure, documenting it" "2026-03-01T10:05:01Z" ]
            |> String.concat Environment.NewLine

        withTempEventsFile content (fun path ->
            Assert.That(getCurrentSkillFromEventsFile path, Is.EqualTo(None)))

    [<Test>]
    member _.``An unanswered ask_user mid-skill still reports the running skill``() =
        // The agent invoked a skill and is now parked on the user (ask_user is the last thing, no
        // reply yet → WaitingForUser). The skill is paused, not finished, so it is still reported;
        // the Waiting grouping is handled separately by CodingTool status, not by CurrentSkill.
        let content =
            [ makeUserEvent "/investigate the flake" "2026-03-01T10:00:00Z"
              makeSkillInvoked "investigate" "2026-03-01T10:00:01Z"
              makeSkillContextEvent "investigate" "2026-03-01T10:00:02Z"
              makeAssistantEvent "digging in" "2026-03-01T10:00:03Z"
              makeAskUserEvent "can you share the failing run URL?" "2026-03-01T10:00:04Z" ]
            |> String.concat Environment.NewLine

        withTempEventsFile content (fun path ->
            Assert.That(getCurrentSkillFromEventsFile path, Is.EqualTo(Some "investigate")))

    [<Test>]
    member _.``Non-existent file yields None``() =
        Assert.That(getCurrentSkillFromEventsFile (Path.Combine(fixtureDir, "nonexistent", "events.jsonl")), Is.EqualTo(None))
