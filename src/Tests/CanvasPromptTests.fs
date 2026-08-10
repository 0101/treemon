module Tests.CanvasPromptTests

open System.IO
open System.Text.RegularExpressions
open NUnit.Framework
open Shared

// The canvas-session launch prompt is shared by the client's `▶ Start session` button and the
// server's SystemView auto-spawn, so the claim instruction must be gated on document kind rather
// than appended unconditionally: `canvas_take_ownership` FAILS for a SystemView (it has no author),
// so an ungated instruction would hand every auto-spawned session a guaranteed tool error.
[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type CanvasPromptTests() =

    let worktree = "Q:/code/demo"

    // Without this, starting a session leaves the dead author as owner: the new session is not a
    // valid recipient, so the doc's interactions stay queued and the pane keeps waiting.
    [<Test>]
    member _.``AgentDoc prompt tells the session to claim the doc by filename``() =
        let result = CanvasPrompt.continueWorking AgentDoc worktree "report.html"

        Assert.That(
            result,
            Is.EqualTo(
                "First call the canvas_take_ownership tool with filename \"report.html\", "
                + "so this document's replies reach this session.\n"
                + "Continue working on canvas doc: Q:/code/demo/.agents/canvas/report.html\n"
                + "This is an HTML file served at localhost:5002. Edits are live-reloaded in the canvas pane."))

    [<Test>]
    member _.``SystemView prompt never mentions the claim tool``() =
        let result = CanvasPrompt.continueWorking SystemView worktree "diff.html"

        Assert.That(
            result,
            Does.Not.Contain("canvas_take_ownership"),
            "A SystemView has no author to claim, so instructing the auto-spawned session to claim it would always fail")

        Assert.That(
            result,
            Is.EqualTo(
                "Continue working on canvas doc: Q:/code/demo/.agents/canvas/diff.html\n"
                + "This is an HTML file served at localhost:5002. Edits are live-reloaded in the canvas pane."),
            "The SystemView prompt must stay exactly what it was before the claim instruction existed")

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
            CanvasPrompt.continueWorking AgentDoc "Q:/code/demo" "report.html",
            Does.Contain(registeredName.Groups[1].Value),
            "The AgentDoc launch prompt must name the tool the extension registers, or the claim instruction is a no-op")
