module Tests.CanvasPromptTests

open System.IO
open System.Text.RegularExpressions
open NUnit.Framework
open Shared

// `forLaunch` builds the FIRST message a freshly started session sees, so these assert the facts a
// cold agent needs rather than pinning whole paragraphs: which file to open, whether it may write
// to it, whether it must claim it, and that a real interaction follows. Wording is free to change;
// these break only when the meaning does.
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

    // Treemon regenerates a SystemView from live data, so an edit is silently discarded. The
    // previous prompt told these sessions to "continue working on" the file and that their edits
    // would be live-reloaded — inviting exactly the work that gets thrown away.
    [<Test>]
    member _.``a SystemView session is told not to edit the generated file``() =
        Assert.That(systemViewPrompt, Does.Contain("Do not edit"))
        Assert.That(systemViewPrompt, Does.Contain("regenerates"),
            "The session needs the reason, or it reads as an arbitrary restriction")

    // The doc is the surface the user is watching; a cold agent's default is to reply in chat.
    [<Test>]
    member _.``an AgentDoc session is told to respond by editing the doc, not the terminal``() =
        Assert.That(agentDocPrompt, Does.Contain("editing the doc"))
        Assert.That(agentDocPrompt, Does.Contain("terminal"))

    // Both launches are triggered by a queued interaction that drains into the new session, so the
    // agent should expect it instead of inventing work.
    [<Test>]
    member _.``both kinds warn that the user's interaction arrives next``() =
        Assert.That(agentDocPrompt, Does.Contain("next message"))
        Assert.That(systemViewPrompt, Does.Contain("next message"))

    // "served at localhost:5002" was an infrastructure fact with no action attached, and it invited
    // a cold agent to curl the port, open a browser, or start a server of its own.
    [<Test>]
    member _.``neither prompt leaks the canvas port or invites serving the file``() =
        Assert.That(agentDocPrompt, Does.Not.Contain("5002"))
        Assert.That(systemViewPrompt, Does.Not.Contain("5002"))
        Assert.That(agentDocPrompt, Does.Contain("Do not try to serve"))
        Assert.That(systemViewPrompt, Does.Contain("Do not try to serve"))

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
