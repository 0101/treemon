module Shared.PathUtils

open System
open System.IO
open System.Runtime.InteropServices

let normalizePath (path: string) =
    let p =
        Path.GetFullPath(path)
            .TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar)

    if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then
        p.ToLowerInvariant()
    else
        p

let pathEquals (a: string) (b: string) =
    String.Equals(a, b, StringComparison.OrdinalIgnoreCase)
