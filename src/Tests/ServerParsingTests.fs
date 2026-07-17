module Tests.ServerParsingTests

open NUnit.Framework
open Server.PrStatus

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
type DetectProviderTests() =

    [<Test>]
    member _.``detectProvider with GitHub HTTPS URL returns GitHub remote``() =
        let result = detectProvider "https://github.com/octocat/hello-world"
        Assert.That(result.IsSome, Is.True)
        Assert.That(toRepoProvider result.Value, Is.EqualTo(Shared.GitHubProvider "https://github.com/octocat/hello-world"))

    [<Test>]
    member _.``detectProvider with GitHub SSH URL returns GitHub remote``() =
        let result = detectProvider "git@github.com:octocat/hello-world.git"
        Assert.That(result.IsSome, Is.True)
        Assert.That(toRepoProvider result.Value, Is.EqualTo(Shared.GitHubProvider "https://github.com/octocat/hello-world"))

    [<Test>]
    member _.``detectProvider with AzDo HTTPS URL returns AzDo remote``() =
        let result = detectProvider "https://dev.azure.com/myorg/myproject/_git/myrepo"
        Assert.That(result.IsSome, Is.True)
        Assert.That(toRepoProvider result.Value, Is.EqualTo(Shared.AzDoProvider "https://dev.azure.com/myorg/myproject/_git/myrepo"))

    [<Test>]
    member _.``detectProvider with AzDo SSH URL returns AzDo remote``() =
        let result = detectProvider "git@ssh.dev.azure.com:v3/myorg/myproject/myrepo"
        Assert.That(result.IsSome, Is.True)
        Assert.That(toRepoProvider result.Value, Is.EqualTo(Shared.AzDoProvider "https://dev.azure.com/myorg/myproject/_git/myrepo"))

    [<Test>]
    member _.``detectProvider with unknown URL returns None``() =
        let result = detectProvider "https://gitlab.com/user/repo"
        Assert.That(result, Is.EqualTo(None))

    [<Test>]
    member _.``detectProvider with empty string returns None``() =
        let result = detectProvider ""
        Assert.That(result, Is.EqualTo(None))
