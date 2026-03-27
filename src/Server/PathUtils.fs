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

let toRepoId (path: string) = path |> normalizePath |> RepoId

let toWorktreePath (path: string) = path |> normalizePath |> WorktreePath
