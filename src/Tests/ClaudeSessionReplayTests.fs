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

let private executorLines = readLastLinesReversed "subagent-executor.jsonl" 20
let private reviewerClaudeLines = readLastLinesReversed "subagent-reviewer-claude.jsonl" 20
let private reviewerGeminiLines = readLastLinesReversed "subagent-reviewer-gemini.jsonl" 20

let private now = DateTimeOffset(2026, 3, 5, 15, 0, 0, TimeSpan.Zero)
let private recentTime = now.AddSeconds(-30.0)

let private activeToolUseEntry =
    """{"type":"assistant","timestamp":"2026-03-05T14:59:30.000Z","message":{"id":"msg_test","type":"message","role":"assistant","content":[{"type":"tool_use","id":"toolu_test","name":"Bash","input":{"command":"dotnet build"}}],"model":"claude-opus-4.6-1m","stop_reason":"tool_use","stop_sequence":null,"usage":{"input_tokens":100,"output_tokens":50}}}"""

let private activeParentLines = [ activeToolUseEntry ]

let private completedSubagentFiles =
    [ (recentTime, executorLines)
      (recentTime, reviewerClaudeLines)
      (recentTime, reviewerGeminiLines) ]


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type SessionReplayTests() =

    [<Test>]
    member _.``Parent actively working with completed subagents yields Working``() =
        let files = (recentTime, activeParentLines) :: completedSubagentFiles
        let result = getStatusFromFiles now files
        Assert.That(result, Is.EqualTo(Working))

    [<Test>]
    member _.``All files completed yields Done``() =
        let result = getStatusFromFiles now completedSubagentFiles
        Assert.That(result, Is.EqualTo(Done))

    [<Test>]
    member _.``All files stale yields Idle``() =
        let staleTime = now.AddHours(-3.0)
        let files =
            [ (staleTime, activeParentLines)
              (staleTime, executorLines)
              (staleTime, reviewerClaudeLines)
              (staleTime, reviewerGeminiLines) ]
        let result = getStatusFromFiles now files
        Assert.That(result, Is.EqualTo(Idle))

    [<Test>]
    member _.``One active file among completed files yields Working``() =
        let files =
            [ (recentTime, activeParentLines)
              (recentTime, executorLines) ]
        let result = getStatusFromFiles now files
        Assert.That(result, Is.EqualTo(Working))

    [<Test>]
    member _.``Single file with WaitingForUser yields WaitingForUser``() =
        let askUserEntry =
            """{"type":"assistant","timestamp":"2026-03-05T14:59:30.000Z","message":{"stop_reason":"tool_use","content":[{"type":"tool_use","name":"AskUserQuestion","id":"toolu_test","input":{"question":"Which approach?"}}]}}"""
        let files = [ (recentTime, [ askUserEntry ]) ]
        let result = getStatusFromFiles now files
        Assert.That(result, Is.EqualTo(WaitingForUser))

    [<Test>]
    member _.``Recently completed file yields Working due to Done-to-Working conversion``() =
        let justNow = now.AddSeconds(-5.0)
        let files = [ (justNow, executorLines) ]
        let result = getStatusFromFiles now files
        Assert.That(result, Is.EqualTo(Working))

    [<Test>]
    member _.``File older than 2 hours yields Idle regardless of content``() =
        let oldTime = now.AddHours(-2.5)
        let files = [ (oldTime, activeParentLines) ]
        let result = getStatusFromFiles now files
        Assert.That(result, Is.EqualTo(Idle))

    [<Test>]
    member _.``Empty file list yields Idle``() =
        let result = getStatusFromFiles now []
        Assert.That(result, Is.EqualTo(Idle))
