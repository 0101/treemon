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

let private recentTime = DateTimeOffset.UtcNow


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type StatusParsingTests() =

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
