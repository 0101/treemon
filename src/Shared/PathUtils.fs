module Shared.PathUtils

open System
open System.IO

let normalizePath (path: string) =
    Path.GetFullPath(path)
        .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)

let pathEquals (a: string) (b: string) =
    String.Equals(a, b, StringComparison.OrdinalIgnoreCase)
