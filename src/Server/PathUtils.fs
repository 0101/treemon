module Server.PathUtils

open System
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

let private segmentsOf (path: string) =
    path.Split([| '/'; '\\' |], StringSplitOptions.RemoveEmptyEntries)

/// Display names for the watched roots. A card is labelled by its folder, which stops telling cards
/// apart the moment two roots end in the same name — an agent keeping its own clone of a shared
/// repository gives every agent a card called "Centro".
///
/// Each label is therefore the shortest trailing run of segments that is unique among the roots, so
/// what differs is what shows. A long run is elided in the middle rather than printed whole, because
/// the segments between the distinguishing one and the repository are exactly the ones carrying no
/// information: `blue/…/Centro`, not `blue/git/Centro`.
let displayNames (paths: string list) : Map<string, string> =
    let indexed = paths |> List.map (fun path -> path, segmentsOf path)

    let trailing depth (parts: string[]) =
        parts |> Array.skip (max 0 (parts.Length - depth))

    let render (parts: string[]) =
        match parts.Length with
        | 0 -> ""
        | 1 -> parts[0]
        | 2 -> String.Join("/", parts)
        | length -> $"{parts[0]}/…/{parts[length - 1]}"

    indexed
    |> List.map (fun (path, parts) ->
        let isUnique depth =
            let mine = trailing depth parts

            indexed
            |> List.forall (fun (other, otherParts) -> other = path || trailing depth otherParts <> mine)

        // Two roots at the same path share a label, which is the honest answer for the same folder
        // watched twice.
        let depth =
            [ 1 .. parts.Length ]
            |> List.tryFind isUnique
            |> Option.defaultValue (max 1 parts.Length)

        path, render (trailing depth parts))
    |> Map.ofList

/// Validates that a bare filename follows the shared canvas contract and resolves inside the
/// .agents/canvas/ directory for the given worktree.
/// Returns Ok(resolvedPath) or Error(reason).
let validateCanvasPath (worktreePath: string) (filename: string) =
    if not (CanvasFilename.isValid filename) then
        Error "Filename does not match the canvas filename contract"
    else
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
