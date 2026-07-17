module Tests.CardViewsTests

open System
open NUnit.Framework
open Shared
open CardViews
open Tests.WorktreeFixtures

/// The card's intent line (footer line 1) combines the agent's current intent (SDK `assistant.intent`)
/// with the running skill as a pill. These tests exercise CardViews.cardIntentLine — the pure decision
/// behind intentLineView — so the intent/skill presence logic is verified without rendering React.
/// `Line` carries at least one of intent/skill (a blank/whitespace skill counts as none); `Empty` when
/// neither is present. The last user/assistant message lines are trivial Option renders (not decisions),
/// so they are not unit-tested here.
[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type CardIntentLineTests() =

    let ts = DateTimeOffset(2026, 7, 9, 12, 0, 0, TimeSpan.Zero)

    [<Test>]
    member _.``Intent and skill together surface both``() =
        let wt = { baseWt with AgentIntent = Some("investigating the fold", ts); CurrentSkill = Some "investigate" }
        Assert.That(cardIntentLine wt, Is.EqualTo(CardIntentLine.Line(Some("investigating the fold", ts), Some "investigate")))

    [<Test>]
    member _.``Intent with no skill surfaces the intent alone``() =
        let wt = { baseWt with AgentIntent = Some("running the tests", ts); CurrentSkill = None }
        Assert.That(cardIntentLine wt, Is.EqualTo(CardIntentLine.Line(Some("running the tests", ts), None)))

    [<Test>]
    member _.``A skill with no intent surfaces the skill alone``() =
        let wt = { baseWt with AgentIntent = None; CurrentSkill = Some "bd-execute" }
        Assert.That(cardIntentLine wt, Is.EqualTo(CardIntentLine.Line(None, Some "bd-execute")))

    [<Test>]
    member _.``Neither intent nor skill surfaces nothing``() =
        let wt = { baseWt with AgentIntent = None; CurrentSkill = None }
        Assert.That(cardIntentLine wt, Is.EqualTo(CardIntentLine.Empty))

    [<Test>]
    member _.``The skill name is trimmed``() =
        let wt = { baseWt with AgentIntent = None; CurrentSkill = Some "  refactor  " }
        Assert.That(cardIntentLine wt, Is.EqualTo(CardIntentLine.Line(None, Some "refactor")))

    // ----- A blank / whitespace skill is not a skill -----

    [<TestCase("")>]
    [<TestCase("   ")>]
    member _.``A blank or whitespace skill counts as no skill``(skill: string) =
        let wt = { baseWt with AgentIntent = Some("thinking", ts); CurrentSkill = Some skill }
        Assert.That(cardIntentLine wt, Is.EqualTo(CardIntentLine.Line(Some("thinking", ts), None)))

    [<TestCase("")>]
    [<TestCase("   ")>]
    member _.``A blank skill with no intent is Empty``(skill: string) =
        let wt = { baseWt with AgentIntent = None; CurrentSkill = Some skill }
        Assert.That(cardIntentLine wt, Is.EqualTo(CardIntentLine.Empty))

    [<Test>]
    member _.``The intent text is surfaced verbatim``() =
        let intent = "explain the caching approach"
        let wt = { baseWt with AgentIntent = Some(intent, ts); CurrentSkill = None }
        match cardIntentLine wt with
        | CardIntentLine.Line (Some (text, _), None) -> Assert.That(text, Is.EqualTo(intent))
        | other -> Assert.Fail($"Expected an intent-only line, got {other}")

/// isVisibleCardEvent decides which events reach a card. Post-fork setup is routine noise while it
/// runs or when it succeeds, so only its failures (a genuine failure or a timeout, both `Failed`)
/// stay on the card; events from every other source always show.
[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type VisibleCardEventTests() =

    let event source status : CardEvent =
        { Source = source
          Message = "setup"
          Timestamp = DateTimeOffset(2026, 7, 9, 12, 0, 0, TimeSpan.Zero)
          Status = status
          Duration = None }

    [<Test>]
    member _.``A running post-fork event is hidden``() =
        Assert.That(isVisibleCardEvent (event EventSource.PostFork (Some StepStatus.Running)), Is.False)

    [<Test>]
    member _.``A succeeded post-fork event is hidden``() =
        Assert.That(isVisibleCardEvent (event EventSource.PostFork (Some StepStatus.Succeeded)), Is.False)

    [<Test>]
    member _.``A failed post-fork event is kept``() =
        Assert.That(isVisibleCardEvent (event EventSource.PostFork (Some(StepStatus.Failed "boom"))), Is.True)

    [<Test>]
    member _.``A timed-out post-fork event is kept (timeout surfaces as a failure)``() =
        Assert.That(isVisibleCardEvent (event EventSource.PostFork (Some(StepStatus.Failed "Timed out after 300000ms"))), Is.True)

    [<Test>]
    member _.``A succeeded sync event is always kept``() =
        Assert.That(isVisibleCardEvent (event EventSource.Sync (Some StepStatus.Succeeded)), Is.True)
