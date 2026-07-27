module Server.DiffProvisioner

open System.IO

let filename = "diff.html"

/// Present in every generated viewer. Provisioning replaces only files it recognises as generated,
/// so a document a user authored at this path before it became a generated SystemView is preserved
/// rather than destroyed on the first refresh after upgrading.
let internal generatedMarker = "treemon:generated worktree-diff-viewer"

/// Viewers generated before the marker existed still carry the pinned renderer route, which nothing
/// but this template emits — so an already-deployed viewer keeps receiving template updates.
let private generatedAssetRoute = "/assets/diff2html/"

let internal isGeneratedViewer (content: string) =
    content.Contains generatedMarker || content.Contains generatedAssetRoute

/// Keep diff.html synchronized with the embedded viewer template for every known worktree.
/// Returns a description of the action taken, or None when the file is already current.
let provisionViewer (worktreePath: string) =
    let diffHtml = Path.Combine(worktreePath, ".agents", "canvas", filename)
    let existing =
        if File.Exists(diffHtml) then
            Some(File.ReadAllText(diffHtml))
        else
            None

    let name = Path.GetFileName(worktreePath)

    match existing with
    | Some current when current = DiffTemplate.html -> None
    | Some current when not (isGeneratedViewer current) ->
        Some $"Left diff.html for {name} untouched (not a Treemon-generated viewer)"
    | _ ->
        Directory.CreateDirectory(Path.GetDirectoryName(diffHtml)) |> ignore
        File.WriteAllText(diffHtml, DiffTemplate.html)

        match existing with
        | None -> Some $"Wrote diff.html for {name}"
        | Some _ -> Some $"Updated diff.html for {name} (template changed)"
