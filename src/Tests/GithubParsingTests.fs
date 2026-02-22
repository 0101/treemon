module Tests.GithubParsingTests

open NUnit.Framework
open Server.GithubPrStatus
open Server.PrStatus
open Shared

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type ParseGithubUrlTests() =

    [<Test>]
    member _.``HTTPS github.com URL parses owner and repo``() =
        let result = parseGithubUrl "https://github.com/octocat/hello-world"
        Assert.That(result.IsSome, Is.True)
        Assert.That(result.Value.Owner, Is.EqualTo("octocat"))
        Assert.That(result.Value.Repo, Is.EqualTo("hello-world"))

    [<Test>]
    member _.``HTTPS github.com URL with .git suffix strips suffix``() =
        let result = parseGithubUrl "https://github.com/octocat/hello-world.git"
        Assert.That(result.IsSome, Is.True)
        Assert.That(result.Value.Owner, Is.EqualTo("octocat"))
        Assert.That(result.Value.Repo, Is.EqualTo("hello-world"))

    [<Test>]
    member _.``HTTPS github.com URL with trailing slash``() =
        let result = parseGithubUrl "https://github.com/octocat/hello-world/"
        Assert.That(result.IsSome, Is.True)
        Assert.That(result.Value.Owner, Is.EqualTo("octocat"))
        Assert.That(result.Value.Repo, Is.EqualTo("hello-world"))

    [<Test>]
    member _.``SSH github.com URL parses owner and repo``() =
        let result = parseGithubUrl "git@github.com:octocat/hello-world.git"
        Assert.That(result.IsSome, Is.True)
        Assert.That(result.Value.Owner, Is.EqualTo("octocat"))
        Assert.That(result.Value.Repo, Is.EqualTo("hello-world"))

    [<Test>]
    member _.``SSH github.com URL without .git suffix``() =
        let result = parseGithubUrl "git@github.com:octocat/hello-world"
        Assert.That(result.IsSome, Is.True)
        Assert.That(result.Value.Owner, Is.EqualTo("octocat"))
        Assert.That(result.Value.Repo, Is.EqualTo("hello-world"))

    [<Test>]
    member _.``AzDo URL returns None``() =
        let result = parseGithubUrl "https://dev.azure.com/myorg/myproject/_git/myrepo"
        Assert.That(result, Is.EqualTo(None))

    [<Test>]
    member _.``Bitbucket URL returns None``() =
        let result = parseGithubUrl "https://bitbucket.org/user/repo"
        Assert.That(result, Is.EqualTo(None))

    [<Test>]
    member _.``Empty string returns None``() =
        let result = parseGithubUrl ""
        Assert.That(result, Is.EqualTo(None))

    [<Test>]
    member _.``Random string returns None``() =
        let result = parseGithubUrl "not-a-url"
        Assert.That(result, Is.EqualTo(None))

    [<Test>]
    member _.``HTTP (non-HTTPS) github.com URL also parses``() =
        let result = parseGithubUrl "http://github.com/octocat/hello-world"
        Assert.That(result.IsSome, Is.True)
        Assert.That(result.Value.Owner, Is.EqualTo("octocat"))
        Assert.That(result.Value.Repo, Is.EqualTo("hello-world"))


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type DetectProviderTests() =

    [<Test>]
    member _.``AzDo HTTPS URL returns AzureDevOps``() =
        let result = detectProvider "https://dev.azure.com/myorg/myproject/_git/myrepo"
        match result with
        | Some(AzureDevOps remote) ->
            Assert.That(remote.Org, Is.EqualTo("myorg"))
            Assert.That(remote.Project, Is.EqualTo("myproject"))
            Assert.That(remote.Repo, Is.EqualTo("myrepo"))
        | _ -> Assert.Fail("Expected AzureDevOps provider")

    [<Test>]
    member _.``GitHub HTTPS URL returns GitHub``() =
        let result = detectProvider "https://github.com/octocat/hello-world"
        match result with
        | Some(GitHub remote) ->
            Assert.That(remote.Owner, Is.EqualTo("octocat"))
            Assert.That(remote.Repo, Is.EqualTo("hello-world"))
        | _ -> Assert.Fail("Expected GitHub provider")

    [<Test>]
    member _.``GitHub SSH URL returns GitHub``() =
        let result = detectProvider "git@github.com:octocat/hello-world.git"
        match result with
        | Some(GitHub remote) ->
            Assert.That(remote.Owner, Is.EqualTo("octocat"))
            Assert.That(remote.Repo, Is.EqualTo("hello-world"))
        | _ -> Assert.Fail("Expected GitHub provider")

    [<Test>]
    member _.``AzDo visualstudio.com URL returns AzureDevOps``() =
        let result = detectProvider "https://myorg.visualstudio.com/myproject/_git/myrepo"
        match result with
        | Some(AzureDevOps remote) ->
            Assert.That(remote.Org, Is.EqualTo("myorg"))
        | _ -> Assert.Fail("Expected AzureDevOps provider")

    [<Test>]
    member _.``Unknown URL returns None``() =
        let result = detectProvider "https://gitlab.com/user/repo"
        Assert.That(result, Is.EqualTo(None))

    [<Test>]
    member _.``Empty string returns None``() =
        let result = detectProvider ""
        Assert.That(result, Is.EqualTo(None))

    [<Test>]
    member _.``AzDo is preferred over GitHub when URL matches AzDo``() =
        let result = detectProvider "https://dev.azure.com/myorg/myproject/_git/myrepo"
        match result with
        | Some(AzureDevOps _) -> Assert.Pass()
        | _ -> Assert.Fail("AzDo should be tried first and matched")


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type ParseAzureDevOpsUrlRegressionTests() =

    [<Test>]
    member _.``HTTPS dev.azure.com URL still parses after GitHub addition``() =
        let result = parseAzureDevOpsUrl "https://dev.azure.com/myorg/myproject/_git/myrepo"
        Assert.That(result.IsSome, Is.True)
        let r = result.Value
        Assert.That(r.Org, Is.EqualTo("myorg"))
        Assert.That(r.Project, Is.EqualTo("myproject"))
        Assert.That(r.Repo, Is.EqualTo("myrepo"))

    [<Test>]
    member _.``SSH dev.azure.com URL still parses after GitHub addition``() =
        let result = parseAzureDevOpsUrl "git@ssh.dev.azure.com:v3/myorg/myproject/myrepo"
        Assert.That(result.IsSome, Is.True)
        let r = result.Value
        Assert.That(r.Org, Is.EqualTo("myorg"))
        Assert.That(r.Project, Is.EqualTo("myproject"))
        Assert.That(r.Repo, Is.EqualTo("myrepo"))

    [<Test>]
    member _.``visualstudio.com URL still parses after GitHub addition``() =
        let result = parseAzureDevOpsUrl "https://myorg.visualstudio.com/myproject/_git/myrepo"
        Assert.That(result.IsSome, Is.True)
        Assert.That(result.Value.Org, Is.EqualTo("myorg"))

    [<Test>]
    member _.``GitHub URL returns None from parseAzureDevOpsUrl``() =
        let result = parseAzureDevOpsUrl "https://github.com/octocat/hello-world"
        Assert.That(result, Is.EqualTo(None))


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type CommentSummaryRenderingTests() =

    let formatComments (comments: CommentSummary) =
        match comments with
        | WithResolution(u, t) when t > 0 ->
            let text = $"{u}/{t} threads"
            let dimmed = (u = 0)
            Some(text, dimmed)
        | CountOnly c ->
            let text = $"{c} comments"
            let dimmed = (c = 0)
            Some(text, dimmed)
        | _ -> None

    [<Test>]
    member _.``WithResolution shows threads format``() =
        match formatComments (WithResolution(2, 7)) with
        | Some(text, isDimmed) ->
            Assert.That(text, Is.EqualTo("2/7 threads"))
            Assert.That(isDimmed, Is.False)
        | None -> Assert.Fail("Expected Some result")

    [<Test>]
    member _.``WithResolution all resolved is dimmed``() =
        match formatComments (WithResolution(0, 5)) with
        | Some(text, isDimmed) ->
            Assert.That(text, Is.EqualTo("0/5 threads"))
            Assert.That(isDimmed, Is.True)
        | None -> Assert.Fail("Expected Some result")

    [<Test>]
    member _.``WithResolution zero total returns None``() =
        let result = formatComments (WithResolution(0, 0))
        Assert.That(result, Is.EqualTo(None))

    [<Test>]
    member _.``CountOnly shows comments format``() =
        match formatComments (CountOnly 5) with
        | Some(text, isDimmed) ->
            Assert.That(text, Is.EqualTo("5 comments"))
            Assert.That(isDimmed, Is.False)
        | None -> Assert.Fail("Expected Some result")

    [<Test>]
    member _.``CountOnly zero is dimmed``() =
        match formatComments (CountOnly 0) with
        | Some(text, isDimmed) ->
            Assert.That(text, Is.EqualTo("0 comments"))
            Assert.That(isDimmed, Is.True)
        | None -> Assert.Fail("Expected Some result")
