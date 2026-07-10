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
