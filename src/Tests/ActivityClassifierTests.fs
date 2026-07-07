module Tests.ActivityClassifierTests

open NUnit.Framework
open Shared

/// Tests for the pure skill->activity classifier (Shared.Activity.classify).
/// It maps a running skill/command name to a CurrentActivity bucket per the beads-overview-band
/// spec table; unknown/empty/whitespace input falls back to Working. The name is normalized first
/// — first whitespace-delimited token, leading '/' stripped, case-insensitive — so a CLI event
/// name, a Claude slash command (possibly with args) and a VS Code tool-call name all classify
/// identically.
[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type ActivityClassifierTests() =

    let assertAll (expected: CurrentActivity) (skills: string list) =
        skills
        |> List.iter (fun skill ->
            Assert.That(Activity.classify skill, Is.EqualTo(expected), $"skill: '{skill}'"))

    [<Test>]
    member _.``investigate maps to Investigating``() =
        assertAll CurrentActivity.Investigating [ "investigate" ]

    [<Test>]
    member _.``planning skills map to Planning``() =
        assertAll CurrentActivity.Planning [ "bd-plan"; "bd-improve"; "bd-autoimprove"; "spec-management" ]

    [<Test>]
    member _.``executing skills map to Executing``() =
        assertAll CurrentActivity.Executing [ "bd-execute"; "bd-phase"; "bd-autopilot"; "refactor" ]

    [<Test>]
    member _.``reviewing skills map to Reviewing``() =
        assertAll
            CurrentActivity.Reviewing
            [ "pr"; "review-branch"; "reviewing-tests"; "comprehensive-review"; "code-review"; "bd-review"; "contribution" ]

    [<Test>]
    member _.``fixing skills map to Fixing``() =
        assertAll CurrentActivity.Fixing [ "fix-build"; "conflict" ]

    [<Test>]
    member _.``an unrecognized skill falls back to Working``() =
        assertAll CurrentActivity.Working [ "some-unknown-skill"; "canvas"; "later"; "bd" ]

    [<Test>]
    member _.``empty and whitespace-only input falls back to Working``() =
        assertAll CurrentActivity.Working [ ""; "   "; "\t" ]

    [<Test>]
    member _.``null input falls back to Working``() =
        Assert.That(Activity.classify null, Is.EqualTo(CurrentActivity.Working))

    [<Test>]
    member _.``matching is case-insensitive``() =
        Assert.That(Activity.classify "PR", Is.EqualTo(CurrentActivity.Reviewing))
        Assert.That(Activity.classify "Investigate", Is.EqualTo(CurrentActivity.Investigating))
        Assert.That(Activity.classify "BD-Plan", Is.EqualTo(CurrentActivity.Planning))

    [<Test>]
    member _.``surrounding whitespace is trimmed before matching``() =
        Assert.That(Activity.classify "  investigate  ", Is.EqualTo(CurrentActivity.Investigating))

    [<Test>]
    member _.``a leading slash from a Claude slash command is ignored``() =
        Assert.That(Activity.classify "/pr", Is.EqualTo(CurrentActivity.Reviewing))
        Assert.That(Activity.classify "/fix-build", Is.EqualTo(CurrentActivity.Fixing))

    [<Test>]
    member _.``only the first token is significant when a command carries args``() =
        // ClaudeDetector surfaces "<cmd> <args>" (e.g. a /pr command with a URL argument), so
        // everything after the command name must be ignored for classification.
        Assert.That(Activity.classify "pr https://github.com/org/repo/pull/42", Is.EqualTo(CurrentActivity.Reviewing))
        Assert.That(Activity.classify "/pr https://github.com/org/repo/pull/42", Is.EqualTo(CurrentActivity.Reviewing))
        Assert.That(Activity.classify "bd-plan my-feature", Is.EqualTo(CurrentActivity.Planning))
