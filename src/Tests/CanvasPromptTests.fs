module Tests.CanvasPromptTests

open System.IO
open System.Text.RegularExpressions
open NUnit.Framework
open Shared

// `forLaunch` builds the FIRST message a freshly started session sees. Its real correctness is
// semantic — no string assertion can verify that a cold agent understands it — so these cover only
// the mechanical couplings, where a break has a consequence no other test would catch: the tool name
// crossing into JavaScript, the kind gate, the doc path, and the specific defects already shipped
// once. Wording is deliberately NOT pinned; a prompt that cannot be reworded without red tests is a
// prompt nobody improves.
[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type CanvasPromptTests() =

    let worktree = "Q:/code/demo"
    let agentDocPrompt = CanvasPrompt.forLaunch AgentDoc worktree "report.html"
    let systemViewPrompt = CanvasPrompt.forLaunch SystemView worktree "diff.html"

    [<Test>]
    member _.``both kinds name the doc's real on-disk path``() =
        Assert.That(agentDocPrompt, Does.Contain("Q:/code/demo/.agents/canvas/report.html"))
        Assert.That(systemViewPrompt, Does.Contain("Q:/code/demo/.agents/canvas/diff.html"))

    // Without the claim the started session is not the owner, so the doc's interactions keep
    // routing to the author that is gone and the pane keeps showing "Waiting for session…".
    [<Test>]
    member _.``an AgentDoc session is told to claim the doc by filename``() =
        Assert.That(agentDocPrompt, Does.Contain("canvas_take_ownership"))
        Assert.That(agentDocPrompt, Does.Contain("\"report.html\""),
            "The claim needs the filename to pass to the tool")

    // A SystemView has no author, and `canvas_take_ownership` refuses one — instructing the
    // auto-spawned session to claim it would hand it a guaranteed tool error.
    [<Test>]
    member _.``a SystemView session is never told to claim``() =
        Assert.That(systemViewPrompt, Does.Not.Contain("canvas_take_ownership"))

    // Regression guard for a defect this branch shipped once: the SystemView prompt told an
    // auto-spawned session to "continue working on" diff.html and that its edits would be
    // live-reloaded, but Treemon regenerates that file and discards the work. Asserted as absences
    // of the broken promises rather than presence of today's phrasing, so rewording cannot break it.
    [<Test>]
    member _.``a SystemView session is never promised its edits survive``() =
        Assert.That(systemViewPrompt, Does.Not.Contain("live-reload"))
        Assert.That(systemViewPrompt, Does.Not.Contain("Continue working"))

    // The skill carries what a launch prompt cannot: canvasSend, canvasExpand, and how to read the
    // canvas-selection / expand-section payload that is very often the session's next message.
    [<Test>]
    member _.``an AgentDoc session is pointed at the canvas skill``() =
        Assert.That(agentDocPrompt, Does.Contain("canvas skill"))

    // The canvas skill is an authoring contract, and a SystemView session must not author — sending
    // it there would invite the edit the same prompt forbids two sentences later.
    [<Test>]
    member _.``a SystemView session is not pointed at the authoring skill``() =
        Assert.That(systemViewPrompt, Does.Not.Contain("canvas skill"))

    // "served at localhost:5002" was an infrastructure fact with no action attached, and it invited
    // a cold agent to curl the port, open a browser, or start a server of its own.
    [<Test>]
    member _.``neither prompt leaks the canvas port``() =
        Assert.That(agentDocPrompt, Does.Not.Contain("5002"))
        Assert.That(systemViewPrompt, Does.Not.Contain("5002"))

// The AgentDoc prompt names a tool that is defined in JavaScript, in a process Treemon does not
// compile — so nothing but this test connects the two spellings. If the extension renames the tool,
// every launched session is told to call something that no longer exists and silently fails to
// claim its doc; the F# side would still compile and every other test would still pass.
[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type ClaimToolNameSyncTests() =

    // __SOURCE_DIRECTORY__ resolves at compile time, so this is correct from any test working
    // directory and in a linked worktree (where `.git` is a file, not a directory).
    let repoRoot =
        Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))

    [<Test>]
    member _.``the launch prompt names the tool the extension actually registers``() =
        let extensionSource =
            File.ReadAllText(Path.Combine(repoRoot, "src", "Extension", "extension.mjs"))

        let registeredName =
            Regex.Match(extensionSource, @"name:\s*""(canvas_[A-Za-z0-9_]+)""")

        Assert.That(registeredName.Success, Is.True,
            "Could not find the canvas tool registration in src/Extension/extension.mjs — if the shape changed, update this guard")

        Assert.That(
            CanvasPrompt.forLaunch AgentDoc "Q:/code/demo" "report.html",
            Does.Contain(registeredName.Groups[1].Value),
            "The AgentDoc launch prompt must name the tool the extension registers, or the claim instruction is a no-op")
