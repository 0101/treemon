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
    member _.``Last event assistant.turn_end yields Done``() =
        let status = getStatusFromEventsFile (eventsPath "done-session") recentTime
        Assert.That(status, Is.EqualTo(Done))

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
    member _.``Status is Done when turn_end follows large tool outputs``() =
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
            let status = getStatusFromEventsFile path DateTimeOffset.UtcNow
            Assert.That(status, Is.EqualTo(Done)))
