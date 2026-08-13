module Tests.CanvasPromptTests

open System.IO
open System.Text.RegularExpressions
open NUnit.Framework
open Shared

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type CanvasPromptTests() =

    let prompt =
        CanvasSessionPrompt.forAgentDoc "Q:/code/demo" "report.html"

    [<Test>]
    member _.``prompt names the doc's real on-disk path``() =
        Assert.That(prompt, Does.Contain("Q:/code/demo/.agents/canvas/report.html"))

    [<Test>]
    member _.``prompt names the registered ownership tool and its filename``() =
        let repoRoot =
            Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))

        let extensionSource =
            File.ReadAllText(Path.Combine(repoRoot, "src", "Extension", "extension.mjs"))

        let registeredName =
            Regex.Match(extensionSource, @"name:\s*""(canvas_[A-Za-z0-9_]+)""")

        Assert.That(registeredName.Success, Is.True,
            "Could not find the canvas tool registration in src/Extension/extension.mjs")
        Assert.That(prompt, Does.Contain(registeredName.Groups[1].Value))
        Assert.That(prompt, Does.Contain("\"report.html\""))

    [<Test>]
    member _.``prompt tells the replacement session to load the canvas skill``() =
        Assert.That(prompt, Does.Contain("canvas skill"))

    [<Test>]
    member _.``SystemView prompt never asks the session to edit or claim the generated file``() =
        let systemViewPrompt =
            CanvasPrompt.continueWorking "Q:/code/demo" "diff.html"

        Assert.That(systemViewPrompt, Does.Contain("Q:/code/demo/.agents/canvas/diff.html"))
        Assert.That(systemViewPrompt, Does.Not.Contain("Continue working"))
        Assert.That(systemViewPrompt, Does.Not.Contain("live-reload"))
        Assert.That(systemViewPrompt, Does.Not.Contain("canvas_take_ownership"))
        Assert.That(systemViewPrompt, Does.Not.Contain("5002"))
