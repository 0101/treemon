module Tests.UserMessageFormattingTests

open NUnit.Framework
open Server.UserMessageFormatting

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type UserMessageFormattingTests() =

    [<TestCase("fix the retry tests", null, "fix the retry tests")>]
    [<TestCase("<system_remindered> is user text", null, "<system_remindered> is user text")>]
    [<TestCase("[canvas] {\"action\":\"comment\",\"text\":\"Why is retry not jittered?\"}", "Canvas", "Why is retry not jittered?")>]
    [<TestCase("[canvas] {\"topic\":\"recommendation\",\"text\":\"Use the simpler parser\"}", "Canvas", "Use the simpler parser")>]
    [<TestCase("[canvas] {\"action\":\"decision\",\"topic\":\"cli-parity\",\"choice\":\"dashboard-only\"}", "Canvas", "CLI parity: Dashboard only")>]
    [<TestCase("[canvas] {\"action\":\"expand-section\",\"section\":\"data-flow\",\"doc\":\"investigation.html\"}", "Canvas", "Expand data flow")>]
    [<TestCase("[canvas] {\"action\":\"custom-action\",\"text\":\"Use SQLite\"}", "Canvas", "Use SQLite")>]
    [<TestCase("[canvas] {\"action\":\"custom-action\",\"payload\":{\"value\":42}}", "Canvas", "action: custom-action, payload: value: 42")>]
    [<TestCase("[canvas] {\"topic\":\"recommendation\"}", "Canvas", "topic: recommendation")>]
    [<TestCase("[canvas] {\"action\":\"comment\",\"text\":\"   \"}", "Canvas", "action: comment, text: ")>]
    [<TestCase("[canvas] [\"unexpected\",\"array\"]", "Canvas", "unexpected, array")>]
    [<TestCase("[canvas] {not valid JSON", "Canvas", "not valid JSON")>]
    [<TestCase("[canvas] {\"action\":\"open-link\",\"url\":\"https://example.test/a,b\"}", "Canvas", "action: open-link, url: https://example.test/a,b")>]
    [<TestCase("[canvas] {\"intent\":\"explain\",\"doc\":\"selection.html\",\"contextBefore\":\"Before \",\"selectedText\":\"Selected phrase\",\"contextAfter\":\" after\",\"section\":\"alpha\",\"request\":\"User asked to explain/expand this\",\"action\":\"canvas-selection\"}", "Canvas", "User asked to explain/expand this")>]
    member _.``User messages are formatted for the dashboard``(input: string, expectedGlyph: string, expectedText: string) =
        match classify input with
        | UserMessageClassification.SystemReminder ->
            Assert.Fail "expected a displayable user message"
        | UserMessageClassification.Display(glyph, text) ->
            Assert.Multiple(fun () ->
                Assert.That(glyph |> Option.map string |> Option.toObj, Is.EqualTo(expectedGlyph))
                Assert.That(text, Is.EqualTo(expectedText)))

    [<TestCase("<system_reminder>internal runtime guidance")>]
    [<TestCase("  <system_reminder>internal runtime guidance")>]
    member _.``System reminders are classified as synthetic``(input: string) =
        Assert.That(classify input, Is.EqualTo UserMessageClassification.SystemReminder)
