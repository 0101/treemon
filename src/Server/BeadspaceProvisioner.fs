module Server.BeadspaceProvisioner

open System.IO
open Shared

/// Keep beads.html in sync with the current BeadspaceTemplate when the worktree has
/// issues, and remove it when it has none. Writing only happens when the on-disk content
/// differs from the template, so template fixes reach existing worktrees on the next refresh.
/// Returns a description of the action taken, or None if nothing changed.
let provisionDashboard (worktreePath: string) (summary: BeadsSummary) =
    let beadsHtml = Path.Combine(worktreePath, ".agents", "canvas", "beads.html")
    let hasIssues = summary.Open + summary.InProgress + summary.Blocked + summary.Closed > 0
    let name = Path.GetFileName(worktreePath)

    if hasIssues then
        let existing = if File.Exists(beadsHtml) then Some(File.ReadAllText(beadsHtml)) else None
        if existing = Some BeadspaceTemplate.html then
            None
        else
            Directory.CreateDirectory(Path.GetDirectoryName(beadsHtml)) |> ignore
            File.WriteAllText(beadsHtml, BeadspaceTemplate.html)
            match existing with
            | None -> Some $"Wrote beads.html for {name}"
            | Some _ -> Some $"Updated beads.html for {name} (template changed)"
    elif File.Exists(beadsHtml) then
        File.Delete(beadsHtml)
        Some $"Removed beads.html for {name} (no issues)"
    else
        None
