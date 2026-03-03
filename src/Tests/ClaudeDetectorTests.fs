module Tests.ClaudeDetectorTests

open System
open System.IO
open NUnit.Framework
open Server.ClaudeDetector

let private makeUserEntry (text: string) (timestamp: string) =
    $"""{{"type":"user","timestamp":"{timestamp}","message":{{"content":[{{"type":"text","text":"{text}"}}]}}}}"""

let private makeAssistantEntry (text: string) (timestamp: string) =
    $"""{{"type":"assistant","timestamp":"{timestamp}","message":{{"stop_reason":"end_turn","content":[{{"type":"text","text":"{text}"}}]}}}}"""

let private makePadding (byteCount: int) =
    let entry = makeAssistantEntry "padding response" "2025-01-01T00:00:00Z"
    let entryBytes = System.Text.Encoding.UTF8.GetByteCount(entry + Environment.NewLine)
    let repetitions = (byteCount / entryBytes) + 1
    Seq.init repetitions (fun _ -> entry)
    |> String.concat Environment.NewLine

let private withTempJsonl (content: string) (action: string -> 'a) =
    let tempFile = Path.Combine(Path.GetTempPath(), $"claude-test-{Guid.NewGuid()}.jsonl")
    try
        File.WriteAllText(tempFile, content)
        action tempFile
    finally
        if File.Exists(tempFile) then File.Delete(tempFile)


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type ScanForUserMessageTests() =

    [<Test>]
    member _.``Finds user message in first 64KB chunk``() =
        let content =
            [ makeAssistantEntry "some response" "2025-01-01T00:00:01Z"
              makeUserEntry "hello world" "2025-01-01T00:00:02Z"
              makeAssistantEntry "reply" "2025-01-01T00:00:03Z" ]
            |> String.concat Environment.NewLine

        withTempJsonl content (fun path ->
            let result = scanForUserMessage path
            Assert.That(result.IsSome, Is.True)
            let text, _ = result.Value
            Assert.That(text, Is.EqualTo("hello world")))

    [<Test>]
    member _.``Finds user message requiring multiple chunks``() =
        let userEntry = makeUserEntry "deep message" "2025-01-01T00:00:00Z"
        let padding = makePadding (80 * 1024)
        let content = userEntry + Environment.NewLine + padding

        withTempJsonl content (fun path ->
            let fileSize = FileInfo(path).Length
            Assert.That(fileSize, Is.GreaterThan(64L * 1024L), "File must exceed 64KB to test multi-chunk")
            let result = scanForUserMessage path
            Assert.That(result.IsSome, Is.True)
            let text, _ = result.Value
            Assert.That(text, Is.EqualTo("deep message")))

    [<Test>]
    member _.``Returns None when no user message within 1MB``() =
        let padding = makePadding (1100 * 1024)
        let content = padding

        withTempJsonl content (fun path ->
            let result = scanForUserMessage path
            Assert.That(result, Is.EqualTo(None)))

    [<Test>]
    member _.``Handles line boundary across chunks``() =
        let targetSize = 64 * 1024
        let userEntry = makeUserEntry "boundary message" "2025-01-01T00:00:00Z"
        let userEntryBytes = System.Text.Encoding.UTF8.GetByteCount(userEntry + Environment.NewLine)
        let paddingNeeded = targetSize - userEntryBytes + 100
        let padding = makePadding paddingNeeded

        let content = userEntry + Environment.NewLine + padding

        withTempJsonl content (fun path ->
            let result = scanForUserMessage path
            Assert.That(result.IsSome, Is.True)
            let text, _ = result.Value
            Assert.That(text, Is.EqualTo("boundary message")))

    [<Test>]
    member _.``Returns None for empty file``() =
        withTempJsonl "" (fun path ->
            let result = scanForUserMessage path
            Assert.That(result, Is.EqualTo(None)))

    [<Test>]
    member _.``Skips skill prompt noise and returns real user message behind it``() =
        let longSkillPrompt = "# " + String.replicate 250 "x"
        let content =
            [ makeUserEntry "real user question" "2025-01-01T00:00:01Z"
              makeAssistantEntry "assistant reply" "2025-01-01T00:00:02Z"
              makeUserEntry longSkillPrompt "2025-01-01T00:00:03Z"
              makeAssistantEntry "working on it" "2025-01-01T00:00:04Z" ]
            |> String.concat Environment.NewLine

        withTempJsonl content (fun path ->
            let result = scanForUserMessage path
            Assert.That(result.IsSome, Is.True)
            let text, _ = result.Value
            Assert.That(text, Is.EqualTo("real user question")))


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type IsSystemNoiseTests() =

    [<Test>]
    member _.``Catches skill prompt starting with hash over 200 chars``() =
        let longSkillPrompt = "# " + String.replicate 250 "x"
        Assert.That(isSystemNoise longSkillPrompt, Is.True)

    [<Test>]
    member _.``Catches skill prompt starting with bold over 200 chars``() =
        let longBoldPrompt = "**" + String.replicate 250 "x"
        Assert.That(isSystemNoise longBoldPrompt, Is.True)

    [<Test>]
    member _.``Does not filter short message starting with hash``() =
        let shortMessage = "# Fix the bug"
        Assert.That(isSystemNoise shortMessage, Is.False)

    [<Test>]
    member _.``Does not filter short message starting with bold``() =
        let shortMessage = "**Important** fix this"
        Assert.That(isSystemNoise shortMessage, Is.False)

    [<Test>]
    member _.``Catches PRESERVE ON CONTEXT COMPACTION``() =
        Assert.That(isSystemNoise "PRESERVE ON CONTEXT COMPACTION", Is.True)

    [<Test>]
    member _.``Catches command-name tag``() =
        Assert.That(isSystemNoise "Some text with <command-name>/review</command-name>", Is.True)

    [<Test>]
    member _.``Catches Request interrupted by user``() =
        Assert.That(isSystemNoise "[Request interrupted by user]", Is.True)

    [<Test>]
    member _.``Does not filter normal user message``() =
        Assert.That(isSystemNoise "Please help me with this code", Is.False)
