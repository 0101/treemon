module Tests.SessionManagerBuildScriptTests

open NUnit.Framework
open Server.SessionManager

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type BuildScriptTests() =

    [<Test>]
    member _.``native terminal script doubles a single quote in the path``() =
        let result = buildScript @"C:\wt\o'brien"
        Assert.That(result, Is.EqualTo(@"Set-Location 'C:\wt\o''brien'"))
        Assert.That(result, Does.Not.Contain(";"), "native terminal startup must not append a command")
