module Tests.ClaudeSessionReplayTests

open System
open System.IO
open System.Text.Json
open NUnit.Framework
open Server.ClaudeDetector
open Shared

let private fixtureDir =
    Path.Combine(Path.GetDirectoryName(System.Reflection.Assembly.GetExecutingAssembly().Location), "fixtures", "claude", "multi-session")
    |> Path.GetFullPath

let private readLinesReversedUpTo (fileName: string) (maxLineIndex: int) (maxLines: int) =
    let path = Path.Combine(fixtureDir, fileName)
    File.ReadAllLines(path)
    |> Array.truncate (maxLineIndex + 1)
    |> Array.map _.Trim()
    |> Array.filter (fun s -> s.Length > 0)
    |> Array.rev
    |> Array.truncate maxLines
    |> Array.toList

let private readLastLinesReversed (fileName: string) (maxLines: int) =
    readLinesReversedUpTo fileName (Int32.MaxValue - 1) maxLines

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
    member _.``Parent Done + subagent Working = Done (parent Done is authoritative)``() =
        let files =
            [ makeFileData Parent recentTime [ makeDoneEntry "2026-03-05T14:59:20.000Z" ]
              makeFileData Subagent recentTime [ makeWorkingEntry "2026-03-05T14:59:30.000Z" ] ]
        let result = getStatusFromFiles now files
        Assert.That(result, Is.EqualTo(Done))

    [<Test>]
    member _.``Parent Done + subagent WaitingForUser = Done (subagent WaitingForUser is internal)``() =
        let files =
            [ makeFileData Parent recentTime [ makeDoneEntry "2026-03-05T14:59:20.000Z" ]
              makeFileData Subagent recentTime [ makeWaitingEntry "2026-03-05T14:59:30.000Z" ] ]
        let result = getStatusFromFiles now files
        Assert.That(result, Is.EqualTo(Done))

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
    member _.``Parent Idle + subagent WaitingForUser = Idle (subagent WaitingForUser is internal)``() =
        let files =
            [ makeFileData Parent recentTime []
              makeFileData Subagent recentTime [ makeWaitingEntry "2026-03-05T14:59:30.000Z" ] ]
        let result = getStatusFromFiles now files
        Assert.That(result, Is.EqualTo(Idle))

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
    member _.``Parent Done + subagent with tool_result-only user entry = Done (not false Working)``() =
        let warmupAbortEntry =
            """{"type":"user","timestamp":"2026-03-05T14:59:30.000Z","message":{"role":"user","content":[{"type":"tool_result","content":"Warmup","is_error":true,"tool_use_id":"toolu_test"}]}}"""
        let files =
            [ makeFileData Parent recentTime [ makeDoneEntry "2026-03-05T14:59:20.000Z" ]
              makeFileData Subagent recentTime [ warmupAbortEntry ] ]
        let result = getStatusFromFiles now files
        Assert.That(result, Is.EqualTo(Done))

    [<Test>]
    member _.``Parent Done + subagent with Warmup user entry = Done (not false Working)``() =
        let warmupEntry =
            """{"type":"user","timestamp":"2026-03-05T14:59:30.000Z","message":{"role":"user","content":"Warmup"},"isSidechain":true,"userType":"external"}"""
        let files =
            [ makeFileData Parent recentTime [ makeDoneEntry "2026-03-05T14:59:20.000Z" ]
              makeFileData Subagent recentTime [ warmupEntry ] ]
        let result = getStatusFromFiles now files
        Assert.That(result, Is.EqualTo(Done))

    [<Test>]
    member _.``Parent Done + one subagent Working among Done subagents = Done (parent Done is authoritative)``() =
        let files =
            [ makeFileData Parent recentTime [ makeDoneEntry "2026-03-05T14:59:20.000Z" ]
              makeFileData Subagent recentTime [ makeDoneEntry "2026-03-05T14:59:20.000Z" ]
              makeFileData Subagent recentTime [ makeWorkingEntry "2026-03-05T14:59:30.000Z" ]
              makeFileData Subagent recentTime [ makeDoneEntry "2026-03-05T14:59:20.000Z" ] ]
        let result = getStatusFromFiles now files
        Assert.That(result, Is.EqualTo(Done))


type private TimelineEntry =
    { Timestamp: DateTimeOffset
      FileName: string
      Kind: SessionFileKind
      Line: string
      LineIndex: int }

type private StatusTransition =
    { Timestamp: string
      Status: string
      Trigger: string }

let private fixtureFiles =
    [ "parent-session.jsonl"
      "subagent-executor.jsonl"
      "subagent-reviewer-claude.jsonl"
      "subagent-reviewer-gemini.jsonl" ]

let private parseTimestampFromLine (line: string) =
    try
        use doc = JsonDocument.Parse(line)
        match doc.RootElement.TryGetProperty("timestamp") with
        | true, ts ->
            match DateTimeOffset.TryParse(ts.GetString()) with
            | true, dto -> Some dto
            | _ -> None
        | _ -> None
    with _ -> None

let private statusToString = function
    | Working -> "Working"
    | WaitingForUser -> "WaitingForUser"
    | Done -> "Done"
    | Idle -> "Idle"

let private maxLinesForReplay = 20

let private buildTimeline () =
    fixtureFiles
    |> List.collect (fun fileName ->
        let path = Path.Combine(fixtureDir, fileName)
        let kind = kindFromFileName fileName
        File.ReadAllLines(path)
        |> Array.mapi (fun i line -> i, line.Trim())
        |> Array.filter (fun (_, line) -> line.Length > 0)
        |> Array.choose (fun (i, line) ->
            parseTimestampFromLine line
            |> Option.map (fun ts ->
                { Timestamp = ts
                  FileName = fileName
                  Kind = kind
                  Line = line
                  LineIndex = i }))
        |> Array.toList)
    |> List.sortBy _.Timestamp

let private replayTimeline (timeline: TimelineEntry list) =
    let folder (accumulatedLines: Map<string, string list>, lastStatus: CodingToolStatus option, transitions: StatusTransition list) (entry: TimelineEntry) =
        let currentLines =
            accumulatedLines
            |> Map.tryFind entry.FileName
            |> Option.defaultValue []
        let updatedLines = entry.Line :: currentLines
        let newAccumulated = accumulatedLines |> Map.add entry.FileName updatedLines

        let files =
            newAccumulated
            |> Map.toList
            |> List.map (fun (fileName, lines) ->
                let kind = kindFromFileName fileName
                let latestTimestamp =
                    lines
                    |> List.tryPick parseTimestampFromLine
                    |> Option.defaultValue entry.Timestamp
                let lastLinesReversed = lines |> List.truncate maxLinesForReplay
                makeFileData kind latestTimestamp lastLinesReversed)

        let status = getStatusFromFiles entry.Timestamp files

        let newTransitions =
            match lastStatus with
            | Some prev when prev = status -> transitions
            | _ ->
                let transition =
                    { Timestamp = entry.Timestamp.ToString("o")
                      Status = statusToString status
                      Trigger = $"{entry.FileName}:{entry.LineIndex}" }
                transition :: transitions

        (newAccumulated, Some status, newTransitions)

    let (_, _, transitions) =
        timeline
        |> List.fold folder (Map.empty, None, [])

    transitions |> List.rev

let private parseTransition (line: string) =
    use doc = JsonDocument.Parse(line)
    let root = doc.RootElement
    { Timestamp = root.GetProperty("timestamp").GetString()
      Status = root.GetProperty("status").GetString()
      Trigger = root.GetProperty("trigger").GetString() }

let private expectedStatusesPath =
    Path.Combine(fixtureDir, "expected-statuses.jsonl")


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type TimelineReplayTests() =

    [<Test>]
    member _.``Timeline replay matches expected status transitions``() =
        let timeline = buildTimeline ()
        let actual = replayTimeline timeline

        Assert.That(
            File.Exists(expectedStatusesPath),
            Is.True,
            $"Expected statuses fixture not found at {expectedStatusesPath}. Run the generator first.")

        let expected =
            File.ReadAllLines(expectedStatusesPath)
            |> Array.map _.Trim()
            |> Array.filter (fun s -> s.Length > 0)
            |> Array.map parseTransition
            |> Array.toList

        Assert.That(actual.Length, Is.EqualTo(expected.Length),
            $"Number of status transitions differs. Actual: {actual.Length}, Expected: {expected.Length}")

        List.zip actual expected
        |> List.iteri (fun i (a, e) ->
            Assert.That(a.Timestamp, Is.EqualTo(e.Timestamp),
                $"Transition {i}: timestamp mismatch")
            Assert.That(a.Status, Is.EqualTo(e.Status),
                $"Transition {i}: status mismatch at {a.Timestamp} (trigger: {a.Trigger})")
            Assert.That(a.Trigger, Is.EqualTo(e.Trigger),
                $"Transition {i}: trigger mismatch at {a.Timestamp}"))

    [<Test>]
    member _.``Timeline has entries from all fixture files``() =
        let timeline = buildTimeline ()
        let filesWithEntries =
            timeline
            |> List.map _.FileName
            |> List.distinct
        Assert.That(filesWithEntries.Length, Is.EqualTo(4),
            $"Expected entries from all 4 fixture files, got: {filesWithEntries}")
