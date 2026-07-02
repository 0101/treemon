module Tests.SessionManagerBuildScriptTests

open NUnit.Framework
open Server.SessionManager

// Durable, headless cover for the deterministic core of the manual worktree-launch smoke step
// (verification tm-quicklaunch-nvb, step 10): a worktree parent path containing an apostrophe must
// still launch — the emitted `Set-Location '<path>'` must double the single quote so the path
// cannot break out of the single-quoted PowerShell literal (parse error) or inject. buildScript is
// internal (InternalsVisibleTo "Tests"); these assert its literal output with no GUI.
[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type BuildScriptTests() =

    [<Test>]
    member _.``command branch doubles a single quote in the path``() =
        // Core of step 10: C:\wt\o'brien -> Set-Location 'C:\wt\o''brien'; <cmd>.
        // The command is appended verbatim: its own single quotes are NOT doubled
        // (only the path is escaped here — the command is pre-escaped by CodingToolCli).
        let result = buildScript @"C:\wt\o'brien" (Some "copilot --yolo -i 'go'")
        Assert.That(result, Is.EqualTo(@"Set-Location 'C:\wt\o''brien'; copilot --yolo -i 'go'"))

    [<Test>]
    member _.``no-command branch doubles a single quote in the path``() =
        // The None branch (spawnTerminal): escapes identically but emits no trailing "; <cmd>".
        let result = buildScript @"C:\wt\o'brien" None
        Assert.That(result, Is.EqualTo(@"Set-Location 'C:\wt\o''brien'"))
        Assert.That(result, Does.Not.Contain(";"), "no-command branch must not append a command separator")

    [<Test>]
    member _.``every single quote in the path is doubled, not just the first``() =
        // Guards against a naive first-occurrence escape: both apostrophes must be doubled.
        let result = buildScript @"C:\o'br'ien" (Some "run")
        Assert.That(result, Is.EqualTo(@"Set-Location 'C:\o''br''ien'; run"))

    [<Test>]
    member _.``a path with no single quote is emitted verbatim``() =
        // Reference/common case: the escaping is a no-op for a normal path — no over-escaping,
        // and the Set-Location '<path>'; <cmd> wrapper is well-formed.
        let result = buildScript @"C:\wt\feature-x" (Some "copilot --yolo -i 'go'")
        Assert.That(result, Is.EqualTo(@"Set-Location 'C:\wt\feature-x'; copilot --yolo -i 'go'"))
