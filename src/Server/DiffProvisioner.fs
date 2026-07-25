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
/// comparison content, and remove the generated viewer from a worktree that is confirmed clean, so a
/// clean worktree offers no diff page instead of an empty one. Removal is irreversible, so it is
/// skipped whenever the worktree could merely not be evaluated or Git tracks the viewer — leaving a
/// viewer in place is always recoverable, deleting repository content is not.
/// Returns a description of the action taken, or None when the file is already in its intended state.
let provisionViewer (worktreePath: string) (comparison: GitWorktree.ComparisonContent) =
    async {
        let diffHtml = Path.Combine(worktreePath, ".agents", "canvas", filename)
        let existing =
            if File.Exists(diffHtml) then
                Some(File.ReadAllText(diffHtml))
            else
                None

        let name = Path.GetFileName(worktreePath)

        match comparison, existing with
        | _, Some current when not (isGeneratedViewer current) ->
            return Some $"Left diff.html for {name} untouched (not a Treemon-generated viewer)"
        | GitWorktree.Clean, None -> return None
        | GitWorktree.Clean, Some _ ->
            let! tracked = GitWorktree.tracksGeneratedDiffViewer worktreePath

            if tracked then
                return Some $"Left diff.html for {name} in place (tracked by Git)"
            else
                File.Delete(diffHtml)
                return Some $"Removed diff.html for {name} (no diff)"
        | _, Some current when current = DiffTemplate.html -> return None
        | _, _ ->
            Directory.CreateDirectory(Path.GetDirectoryName(diffHtml)) |> ignore
            File.WriteAllText(diffHtml, DiffTemplate.html)

            match existing with
            | None -> return Some $"Wrote diff.html for {name}"
            | Some _ -> return Some $"Updated diff.html for {name} (template changed)"
    }
