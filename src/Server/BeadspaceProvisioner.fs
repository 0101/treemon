module Server.BeadspaceProvisioner

open System.IO
open Shared

/// Provision or remove beads.html based on whether the worktree has any beads issues.
/// Returns a description of the action taken, or None if no action was needed.
let provisionDashboard (worktreePath: string) (summary: BeadsSummary) =
    let beadsHtml = Path.Combine(worktreePath, ".agents", "canvas", "beads.html")
    let hasIssues = summary.Open + summary.InProgress + summary.Blocked + summary.Closed > 0

    if hasIssues && not (File.Exists(beadsHtml)) then
        let dir = Path.GetDirectoryName(beadsHtml)
        Directory.CreateDirectory(dir) |> ignore
        File.WriteAllText(beadsHtml, BeadspaceTemplate.html)
        Some $"Wrote beads.html for {Path.GetFileName(worktreePath)}"
    elif not hasIssues && File.Exists(beadsHtml) then
        File.Delete(beadsHtml)
        Some $"Removed beads.html for {Path.GetFileName(worktreePath)} (no issues)"
    else
        None
