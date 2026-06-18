module Server.PathUtils

open System.IO
open System.Runtime.InteropServices
open Shared

let normalizePath (path: string) =
    let p =
        Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)

    if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then
        p.ToLowerInvariant()
    else
        p

/// Validates that a filename resolves inside the .agents/canvas/ directory for the given worktree.
/// Returns Ok(resolvedPath) or Error(reason).
let validateCanvasPath (worktreePath: string) (filename: string) =
    let canvasDir = Path.Combine(worktreePath, ".agents", "canvas")
    let resolvedPath = Path.Combine(canvasDir, filename)
    let normalizedResolved = normalizePath resolvedPath
    let normalizedCanvasDir = normalizePath canvasDir + string Path.DirectorySeparatorChar

    if not (normalizedResolved.StartsWith(normalizedCanvasDir)) then
        Error "Path traversal rejected"
    else
        Ok resolvedPath

let toRepoId (path: string) = path |> normalizePath |> RepoId

let toWorktreePath (path: string) = path |> normalizePath |> WorktreePath
