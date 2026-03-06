module Tests.ClaudeSessionReplayTests

open System
open System.IO
open NUnit.Framework
open Server.ClaudeDetector
open Shared

let private fixtureDir =
    Path.Combine(Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location), "fixtures", "claude", "multi-session")
    |> Path.GetFullPath

let private readLastLinesReversed (fileName: string) (maxLines: int) =
    let path = Path.Combine(fixtureDir, fileName)
    File.ReadAllLines(path)
    |> Array.map _.Trim()
    |> Array.filter (fun s -> s.Length > 0)
    |> Array.rev
    |> Array.truncate maxLines
    |> Array.toList

let private readLinesReversedUpTo (fileName: string) (maxLineIndex: int) (maxLines: int) =
    let path = Path.Combine(fixtureDir, fileName)
    File.ReadAllLines(path)
    |> Array.truncate (maxLineIndex + 1)
    |> Array.map _.Trim()
    |> Array.filter (fun s -> s.Length > 0)
    |> Array.rev
    |> Array.truncate maxLines
    |> Array.toList

let private kindFromFileName (fileName: string) =
    if fileName.StartsWith("subagent-") then Subagent else Parent

let private makeFileData (kind: SessionFileKind) (lastWrite: DateTimeOffset) (lines: string list) =
    { Kind = kind; LastWriteUtc = lastWrite; LastLinesReversed = lines }

let private executorLines = readLastLinesReversed "subagent-executor.jsonl" 20
let private reviewerClaudeLines = readLastLinesReversed "subagent-reviewer-claude.jsonl" 20
let private reviewerGeminiLines = readLastLinesReversed "subagent-reviewer-gemini.jsonl" 20
let private parentCompletedLines = readLastLinesReversed "parent-session.jsonl" 20
let private parentActiveLines = readLinesReversedUpTo "parent-session.jsonl" 124 20

let private now = DateTimeOffset(2026, 3, 5, 15, 0, 0, TimeSpan.Zero)
let private recentTime = now.AddSeconds(-30.0)

let private completedSubagentFiles =
    [ makeFileData Subagent recentTime executorLines
      makeFileData Subagent recentTime reviewerClaudeLines
      makeFileData Subagent recentTime reviewerGeminiLines ]


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type SessionReplayTests() =

    [<Test>]
    member _.``Parent actively working with completed subagents yields Working``() =
        let files = (makeFileData Parent recentTime parentActiveLines) :: completedSubagentFiles
        let result = getStatusFromFiles now files
        Assert.That(result, Is.EqualTo(Working))

    [<Test>]
    member _.``All files completed yields Done``() =
        let files = (makeFileData Parent recentTime parentCompletedLines) :: completedSubagentFiles
        let result = getStatusFromFiles now files
        Assert.That(result, Is.EqualTo(Done))

    [<Test>]
    member _.``All files stale yields Idle``() =
        let staleTime = now.AddHours(-3.0)
        let files =
            [ makeFileData Parent staleTime parentCompletedLines
              makeFileData Subagent staleTime executorLines
              makeFileData Subagent staleTime reviewerClaudeLines
              makeFileData Subagent staleTime reviewerGeminiLines ]
        let result = getStatusFromFiles now files
        Assert.That(result, Is.EqualTo(Idle))

    [<Test>]
    member _.``One active parent among completed subagents yields Working``() =
        let files =
            [ makeFileData Parent recentTime parentActiveLines
              makeFileData Subagent recentTime executorLines ]
        let result = getStatusFromFiles now files
        Assert.That(result, Is.EqualTo(Working))

    [<Test>]
    member _.``Single file with WaitingForUser yields WaitingForUser``() =
        let askUserEntry =
            """{"type":"assistant","timestamp":"2026-03-05T14:59:30.000Z","message":{"stop_reason":"tool_use","content":[{"type":"tool_use","name":"AskUserQuestion","id":"toolu_test","input":{"question":"Which approach?"}}]}}"""
        let files = [ makeFileData Parent recentTime [ askUserEntry ] ]
        let result = getStatusFromFiles now files
        Assert.That(result, Is.EqualTo(WaitingForUser))

    [<Test>]
    member _.``Recently completed file yields Working due to Done-to-Working conversion``() =
        let justNow = now.AddSeconds(-5.0)
        let files = [ makeFileData Subagent justNow executorLines ]
        let result = getStatusFromFiles now files
        // No parent files => parent status defaults to Idle, subagent Working upgrades to Working
        Assert.That(result, Is.EqualTo(Working))

    [<Test>]
    member _.``File older than 2 hours yields Idle regardless of content``() =
        let oldTime = now.AddHours(-2.5)
        let files = [ makeFileData Parent oldTime parentActiveLines ]
        let result = getStatusFromFiles now files
        Assert.That(result, Is.EqualTo(Idle))

    [<Test>]
    member _.``Empty file list yields Idle``() =
        let result = getStatusFromFiles now []
        Assert.That(result, Is.EqualTo(Idle))


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type ParentAuthoritativeResolutionTests() =

    let makeWorkingEntry timestamp =
        $"""{{"type":"user","timestamp":"{timestamp}","message":{{"content":[{{"type":"text","text":"do something"}}]}}}}"""

    let makeDoneEntry timestamp =
        $"""{{"type":"assistant","timestamp":"{timestamp}","message":{{"stop_reason":"end_turn","content":[{{"type":"text","text":"all done"}}]}}}}"""

    let makeWaitingEntry timestamp =
        $"""{{"type":"assistant","timestamp":"{timestamp}","message":{{"stop_reason":"tool_use","content":[{{"type":"tool_use","name":"AskUserQuestion","id":"toolu_test","input":{{"question":"Which approach?"}}}}]}}}}"""

    [<Test>]
    member _.``Parent Working + subagent Done = Working``() =
        let files =
            [ makeFileData Parent recentTime [ makeWorkingEntry "2026-03-05T14:59:30.000Z" ]
              makeFileData Subagent recentTime [ makeDoneEntry "2026-03-05T14:59:30.000Z" ] ]
        let result = getStatusFromFiles now files
        Assert.That(result, Is.EqualTo(Working))

    [<Test>]
    member _.``Parent WaitingForUser + subagent Working = WaitingForUser``() =
        let files =
            [ makeFileData Parent recentTime [ makeWaitingEntry "2026-03-05T14:59:30.000Z" ]
              makeFileData Subagent recentTime [ makeWorkingEntry "2026-03-05T14:59:30.000Z" ] ]
        let result = getStatusFromFiles now files
        Assert.That(result, Is.EqualTo(WaitingForUser))

    [<Test>]
    member _.``Parent Done + subagent Working = Working``() =
        let files =
            [ makeFileData Parent recentTime [ makeDoneEntry "2026-03-05T14:59:20.000Z" ]
              makeFileData Subagent recentTime [ makeWorkingEntry "2026-03-05T14:59:30.000Z" ] ]
        let result = getStatusFromFiles now files
        Assert.That(result, Is.EqualTo(Working))

    [<Test>]
    member _.``Parent Done + subagent Done = Done``() =
        let files =
            [ makeFileData Parent recentTime [ makeDoneEntry "2026-03-05T14:59:30.000Z" ]
              makeFileData Subagent recentTime [ makeDoneEntry "2026-03-05T14:59:30.000Z" ] ]
        let result = getStatusFromFiles now files
        Assert.That(result, Is.EqualTo(Done))

    [<Test>]
    member _.``Parent Idle + subagent Working = Working``() =
        let files =
            [ makeFileData Parent recentTime []
              makeFileData Subagent recentTime [ makeWorkingEntry "2026-03-05T14:59:30.000Z" ] ]
        let result = getStatusFromFiles now files
        Assert.That(result, Is.EqualTo(Working))

    [<Test>]
    member _.``No parent files with subagent Working = Working (parent defaults to Idle)``() =
        let files =
            [ makeFileData Subagent recentTime [ makeWorkingEntry "2026-03-05T14:59:30.000Z" ] ]
        let result = getStatusFromFiles now files
        Assert.That(result, Is.EqualTo(Working))

    [<Test>]
    member _.``No parent files with subagent Done = Done from subagent does not upgrade Idle``() =
        let files =
            [ makeFileData Subagent recentTime [ makeDoneEntry "2026-03-05T14:59:30.000Z" ] ]
        let result = getStatusFromFiles now files
        // Parent defaults to Idle, subagent Done does not upgrade
        Assert.That(result, Is.EqualTo(Idle))

    [<Test>]
    member _.``Parent WaitingForUser + multiple subagents Working = WaitingForUser``() =
        let files =
            [ makeFileData Parent recentTime [ makeWaitingEntry "2026-03-05T14:59:30.000Z" ]
              makeFileData Subagent recentTime [ makeWorkingEntry "2026-03-05T14:59:30.000Z" ]
              makeFileData Subagent recentTime [ makeWorkingEntry "2026-03-05T14:59:30.000Z" ] ]
        let result = getStatusFromFiles now files
        Assert.That(result, Is.EqualTo(WaitingForUser))

    [<Test>]
    member _.``Parent Done + one subagent Working among Done subagents = Working``() =
        let files =
            [ makeFileData Parent recentTime [ makeDoneEntry "2026-03-05T14:59:20.000Z" ]
              makeFileData Subagent recentTime [ makeDoneEntry "2026-03-05T14:59:20.000Z" ]
              makeFileData Subagent recentTime [ makeWorkingEntry "2026-03-05T14:59:30.000Z" ]
              makeFileData Subagent recentTime [ makeDoneEntry "2026-03-05T14:59:20.000Z" ] ]
        let result = getStatusFromFiles now files
        Assert.That(result, Is.EqualTo(Working))
