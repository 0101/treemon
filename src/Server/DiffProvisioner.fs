module Server.DiffProvisioner

open System.IO

/// Keep diff.html synchronized with the embedded viewer template for every known worktree.
/// Returns a description of the action taken, or None when the file is already current.
let provisionViewer (worktreePath: string) =
    let diffHtml = Path.Combine(worktreePath, ".agents", "canvas", "diff.html")
    let existing =
        if File.Exists(diffHtml) then
            Some(File.ReadAllText(diffHtml))
        else
            None

    if existing = Some DiffTemplate.html then
        None
    else
        Directory.CreateDirectory(Path.GetDirectoryName(diffHtml)) |> ignore
        File.WriteAllText(diffHtml, DiffTemplate.html)

        match existing with
        | None ->
            Some $"Wrote diff.html for {Path.GetFileName(worktreePath)}"
        | Some _ ->
            Some $"Updated diff.html for {Path.GetFileName(worktreePath)} (template changed)"
