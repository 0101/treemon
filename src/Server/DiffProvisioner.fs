module Server.DiffProvisioner

open System.IO

let filename = "diff.html"

/// Present in every generated viewer. Provisioning writes and removes only files it recognises as
/// generated, so a document a user authored at this path before it became a generated SystemView is
/// preserved rather than destroyed on the first refresh after upgrading.
let internal generatedMarker = "treemon:generated worktree-diff-viewer"

/// Viewers generated before the marker existed still carry the pinned renderer route, which nothing
/// but this template emits — so an already-deployed viewer keeps receiving template updates.
let private generatedAssetRoute = "/assets/diff2html/"

let internal isGeneratedViewer (content: string) =
    content.Contains generatedMarker || content.Contains generatedAssetRoute

/// Keep diff.html synchronized with the embedded viewer template for every worktree that has
/// comparison content, and remove the generated viewer from a worktree that has none, so a clean
/// worktree offers no diff page instead of an empty one.
/// Returns a description of the action taken, or None when the file is already in its intended state.
let provisionViewer (worktreePath: string) (hasDiff: bool) =
    let diffHtml = Path.Combine(worktreePath, ".agents", "canvas", filename)
    let existing =
        if File.Exists(diffHtml) then
            Some(File.ReadAllText(diffHtml))
        else
            None

    let name = Path.GetFileName(worktreePath)

    match hasDiff, existing with
    | _, Some current when not (isGeneratedViewer current) ->
        Some $"Left diff.html for {name} untouched (not a Treemon-generated viewer)"
    | true, Some current when current = DiffTemplate.html -> None
    | true, _ ->
        Directory.CreateDirectory(Path.GetDirectoryName(diffHtml)) |> ignore
        File.WriteAllText(diffHtml, DiffTemplate.html)

        match existing with
        | None -> Some $"Wrote diff.html for {name}"
        | Some _ -> Some $"Updated diff.html for {name} (template changed)"
    | false, Some _ ->
        File.Delete(diffHtml)
        Some $"Removed diff.html for {name} (no diff)"
    | false, None -> None
