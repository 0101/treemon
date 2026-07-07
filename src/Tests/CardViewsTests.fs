module Tests.CardViewsTests

open System
open NUnit.Framework
open Shared
open CardViews

/// Tests for the per-card activity stripe (CardViews.activityStripe / cardClassName), the left
/// color bar that adds the *what* alongside the existing binary red dot. The stripe is gated on an
/// active session (so an idle card never shows a stale skill) and derives its color from the running
/// skill via the Shared Activity classifier. Working / no recognized skill => no stripe.
[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type CardViewsStripeTests() =

    let baseWt: WorktreeStatus =
        { Path = WorktreePath "/wt"
          Branch = "b"
          LastCommitMessage = "m"
          LastCommitTime = DateTimeOffset.UnixEpoch
          Beads = BeadsSummary.zero
          Planning = BeadsPlanning.zero
          CodingTool = CodingToolStatus.Idle
          CodingToolProvider = None
          CurrentSkill = None
          LastUserMessage = None
          Pr = PrStatus.NoPr
          MainBehindCount = 0
          IsDirty = false
          WorkMetrics = None
          HasActiveSession = false
          HasTestFailureLog = false
          IsMainWorktree = false
          IsArchived = false
          CanvasDocs = [] }

    /// An active-session worktree running (or not) a given skill — the only inputs the stripe reads.
    let activeWith skill = { baseWt with HasActiveSession = true; CurrentSkill = skill }

    // ----- Recognized activity => matching stripe class -----

    [<TestCase("investigate", " act-investigating")>]
    [<TestCase("bd-plan", " act-planning")>]
    [<TestCase("spec-management", " act-planning")>]
    [<TestCase("bd-execute", " act-executing")>]
    [<TestCase("refactor", " act-executing")>]
    [<TestCase("pr", " act-reviewing")>]
    [<TestCase("code-review", " act-reviewing")>]
    [<TestCase("fix-build", " act-fixing")>]
    [<TestCase("conflict", " act-fixing")>]
    member _.``Active worktree gets the stripe class for its skill's activity``(skill: string, expected: string) =
        // Leading space is significant — the class is concatenated straight onto the wt-card list.
        Assert.That(activeWith (Some skill) |> activityStripe, Is.EqualTo(expected))

    [<Test>]
    member _.``A raw slash command with args still classifies via the shared normalizer``() =
        // ClaudeDetector may surface "<cmd> <args>"; the stripe must normalize like the band does.
        Assert.That(activeWith (Some "/pr https://example.com/pull/1") |> activityStripe, Is.EqualTo(" act-reviewing"))

    // ----- Working / no skill => no stripe -----

    [<TestCase("")>]
    [<TestCase("   ")>]
    [<TestCase("totally-unknown-skill")>]
    member _.``Active worktree with an unrecognized or empty skill gets no stripe``(skill: string) =
        Assert.That(activeWith (Some skill) |> activityStripe, Is.EqualTo(""))

    [<Test>]
    member _.``Active worktree with no skill gets no stripe (Working)``() =
        Assert.That(activeWith None |> activityStripe, Is.EqualTo(""))

    // ----- Idle gate: no stripe when the session is not live, even with a real skill -----

    [<Test>]
    member _.``Idle worktree gets no stripe even when it still carries a recognized skill``() =
        // Mirrors the band's active-only filter: CurrentSkill is not staleness-gated, so display
        // consumers gate on the live session instead (a32 implementation note).
        let idleButSkilled = { baseWt with HasActiveSession = false; CurrentSkill = Some "investigate" }
        Assert.That(idleButSkilled |> activityStripe, Is.EqualTo(""))

    // ----- cardClassName integration: stripe is appended, red-dot/base classes unchanged -----

    [<Test>]
    member _.``cardClassName appends the activity stripe onto the base wt-card classes``() =
        let cls = cardClassName (activeWith (Some "investigate"))
        Assert.That(cls, Does.Contain("wt-card"))
        Assert.That(cls, Does.Contain("has-session")) // red-dot / active-session marker preserved
        Assert.That(cls, Does.Contain("act-investigating"))

    [<Test>]
    member _.``cardClassName carries no act- stripe for an idle card``() =
        Assert.That(cardClassName baseWt, Does.Not.Contain("act-"))
