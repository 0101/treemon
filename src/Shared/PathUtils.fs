module Shared.PathUtils

open System

let pathEquals (a: string) (b: string) =
    String.Equals(a, b, StringComparison.OrdinalIgnoreCase)

let siblingWorktreePath (repoRoot: string) (branchName: string) =
    let separatorIndex =
        max (repoRoot.LastIndexOf('/')) (repoRoot.LastIndexOf('\\'))

    let parent = repoRoot.Substring(0, separatorIndex + 1)
    $"{parent}tm-{branchName.Replace('/', '-')}"
