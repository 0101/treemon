module Tests.CardViewsTests

open System
open NUnit.Framework
open Shared
open CardViews
open Tests.WorktreeFixtures

/// The card's "user line" chooses between the running skill and the last user message.
/// These tests exercise CardViews.cardUserLine — the pure render decision behind userLineView — so
/// the skill-vs-message choice is verified without having to render React. A running skill surfaces
/// a `▶ <skill>` label; otherwise the genuine last user message shows. The server now yields a real
/// LastUserMessage (never a `<skill-context>` injection), so there is no injection path to filter on
/// the client.
[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type CardUserLineTests() =

    let ts = DateTimeOffset(2026, 7, 9, 12, 0, 0, TimeSpan.Zero)

    // ----- CurrentSkill = Some => skill label is surfaced (takes precedence over any message) -----

    [<Test>]
    member _.``A running skill surfaces the skill label``() =
        let wt = { baseWt with CurrentSkill = Some "investigate" }
        Assert.That(cardUserLine wt, Is.EqualTo(CardUserLine.Skill "investigate"))

    [<Test>]
    member _.``A running skill takes precedence over the last user message``() =
        let wt = { baseWt with CurrentSkill = Some "bd-execute"; LastUserMessage = Some("do the thing", ts) }
        Assert.That(cardUserLine wt, Is.EqualTo(CardUserLine.Skill "bd-execute"))

    [<Test>]
    member _.``A skill name is trimmed``() =
        let wt = { baseWt with CurrentSkill = Some "  refactor  " }
        Assert.That(cardUserLine wt, Is.EqualTo(CardUserLine.Skill "refactor"))

    // ----- CurrentSkill = None => the genuine last user message is surfaced -----

    [<Test>]
    member _.``No skill surfaces the last user message``() =
        let wt = { baseWt with CurrentSkill = None; LastUserMessage = Some("please review the PR", ts) }
        Assert.That(cardUserLine wt, Is.EqualTo(CardUserLine.Message("please review the PR", ts)))

    [<Test>]
    member _.``No skill and no message surfaces nothing``() =
        let wt = { baseWt with CurrentSkill = None; LastUserMessage = None }
        Assert.That(cardUserLine wt, Is.EqualTo(CardUserLine.Empty))

    // ----- An empty / whitespace skill is not a skill: fall through to the message -----

    [<TestCase("")>]
    [<TestCase("   ")>]
    member _.``An empty or whitespace skill falls through to the last user message``(skill: string) =
        let wt = { baseWt with CurrentSkill = Some skill; LastUserMessage = Some("real prompt", ts) }
        Assert.That(cardUserLine wt, Is.EqualTo(CardUserLine.Message("real prompt", ts)))

    // ----- No <skill-context> text path: the message is surfaced verbatim (server strips injections) -----

    [<Test>]
    member _.``The last user message is surfaced verbatim with no skill-context filtering``() =
        // The server guarantees LastUserMessage is a genuine prompt (never a <skill-context>
        // injection), so the client passes it straight through — there is no injection-suppression
        // branch, and thus no `<skill-context>` text path, on the display side.
        let genuine = "explain the caching approach"
        let wt = { baseWt with CurrentSkill = None; LastUserMessage = Some(genuine, ts) }
        match cardUserLine wt with
        | CardUserLine.Message (prompt, _) ->
            Assert.That(prompt, Is.EqualTo(genuine))
            Assert.That(prompt, Does.Not.Contain("skill-context"))
        | other -> Assert.Fail($"Expected a message line, got {other}")
