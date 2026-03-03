module Tests.ServerParsingTests

open NUnit.Framework
open Server.PrStatus
open Server.ClaudeDetector

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type ParseAzureDevOpsUrlTests() =

    [<Test>]
    member _.``HTTPS dev.azure.com URL parses org, project, and repo``() =
        let result = parseAzureDevOpsUrl "https://dev.azure.com/myorg/myproject/_git/myrepo"
        Assert.That(result.IsSome, Is.True)
        let r = result.Value
        Assert.That(r.Org, Is.EqualTo("myorg"))
        Assert.That(r.Project, Is.EqualTo("myproject"))
        Assert.That(r.Repo, Is.EqualTo("myrepo"))

    [<Test>]
    member _.``HTTPS dev.azure.com URL strips .git suffix``() =
        let result = parseAzureDevOpsUrl "https://dev.azure.com/myorg/myproject/_git/myrepo.git"
        Assert.That(result.IsSome, Is.True)
        Assert.That(result.Value.Repo, Is.EqualTo("myrepo"))

    [<Test>]
    member _.``HTTPS dev.azure.com URL with trailing slash``() =
        let result = parseAzureDevOpsUrl "https://dev.azure.com/myorg/myproject/_git/myrepo/"
        Assert.That(result.IsSome, Is.True)
        Assert.That(result.Value.Repo, Is.EqualTo("myrepo"))

    [<Test>]
    member _.``SSH dev.azure.com URL parses correctly``() =
        let result = parseAzureDevOpsUrl "git@ssh.dev.azure.com:v3/myorg/myproject/myrepo"
        Assert.That(result.IsSome, Is.True)
        let r = result.Value
        Assert.That(r.Org, Is.EqualTo("myorg"))
        Assert.That(r.Project, Is.EqualTo("myproject"))
        Assert.That(r.Repo, Is.EqualTo("myrepo"))

    [<Test>]
    member _.``visualstudio.com URL parses org from subdomain``() =
        let result = parseAzureDevOpsUrl "https://myorg.visualstudio.com/myproject/_git/myrepo"
        Assert.That(result.IsSome, Is.True)
        let r = result.Value
        Assert.That(r.Org, Is.EqualTo("myorg"))
        Assert.That(r.Project, Is.EqualTo("myproject"))
        Assert.That(r.Repo, Is.EqualTo("myrepo"))

    [<Test>]
    member _.``visualstudio.com URL strips .git suffix``() =
        let result = parseAzureDevOpsUrl "https://myorg.visualstudio.com/myproject/_git/myrepo.git"
        Assert.That(result.IsSome, Is.True)
        Assert.That(result.Value.Repo, Is.EqualTo("myrepo"))

    [<Test>]
    member _.``Unrecognized URL returns None``() =
        let result = parseAzureDevOpsUrl "https://github.com/user/repo"
        Assert.That(result, Is.EqualTo(None))

    [<Test>]
    member _.``Empty string returns None``() =
        let result = parseAzureDevOpsUrl ""
        Assert.That(result, Is.EqualTo(None))


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type EncodeWorktreePathTests() =

    [<Test>]
    member _.``Colon is replaced with hyphen``() =
        let result = encodeWorktreePath "Q:"
        Assert.That(result, Is.EqualTo("Q-"))

    [<Test>]
    member _.``Backslash is replaced with hyphen``() =
        let result = encodeWorktreePath @"code\foo"
        Assert.That(result, Is.EqualTo("code-foo"))

    [<Test>]
    member _.``Forward slash is replaced with hyphen``() =
        let result = encodeWorktreePath "code/foo"
        Assert.That(result, Is.EqualTo("code-foo"))

    [<Test>]
    member _.``Full Windows path encodes all separators``() =
        let result = encodeWorktreePath @"Q:\code\AITestAgent"
        Assert.That(result, Is.EqualTo("Q--code-AITestAgent"))

    [<Test>]
    member _.``Path without special characters is unchanged``() =
        let result = encodeWorktreePath "simple"
        Assert.That(result, Is.EqualTo("simple"))

    [<Test>]
    member _.``Empty string returns empty``() =
        let result = encodeWorktreePath ""
        Assert.That(result, Is.EqualTo(""))


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type DetectProviderTests() =

    [<Test>]
    member _.``detectProvider with GitHub HTTPS URL returns GitHub remote``() =
        let result = detectProvider "https://github.com/octocat/hello-world"
        Assert.That(result.IsSome, Is.True)
        Assert.That(toRepoProvider result.Value, Is.EqualTo(Shared.GitHubProvider))

    [<Test>]
    member _.``detectProvider with GitHub SSH URL returns GitHub remote``() =
        let result = detectProvider "git@github.com:octocat/hello-world.git"
        Assert.That(result.IsSome, Is.True)
        Assert.That(toRepoProvider result.Value, Is.EqualTo(Shared.GitHubProvider))

    [<Test>]
    member _.``detectProvider with AzDo HTTPS URL returns AzDo remote``() =
        let result = detectProvider "https://dev.azure.com/myorg/myproject/_git/myrepo"
        Assert.That(result.IsSome, Is.True)
        Assert.That(toRepoProvider result.Value, Is.EqualTo(Shared.AzDoProvider))

    [<Test>]
    member _.``detectProvider with AzDo SSH URL returns AzDo remote``() =
        let result = detectProvider "git@ssh.dev.azure.com:v3/myorg/myproject/myrepo"
        Assert.That(result.IsSome, Is.True)
        Assert.That(toRepoProvider result.Value, Is.EqualTo(Shared.AzDoProvider))

    [<Test>]
    member _.``detectProvider with unknown URL returns None``() =
        let result = detectProvider "https://gitlab.com/user/repo"
        Assert.That(result, Is.EqualTo(None))

    [<Test>]
    member _.``detectProvider with empty string returns None``() =
        let result = detectProvider ""
        Assert.That(result, Is.EqualTo(None))
