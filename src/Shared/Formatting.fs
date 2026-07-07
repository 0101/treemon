module Shared.Formatting

open System

/// A human title derived from a canvas filename — `build-status.html` → `Build status`: take the
/// path leaf, drop a trailing `.html`, turn `-`/`_` into spaces, collapse ASCII whitespace, and
/// capitalize only the first letter (sentence case, not Title Case). Falls back to the raw filename
/// if stripping leaves nothing (e.g. a bare ".html").
///
/// Single source of truth shared by the server (`CanvasExport.resolveTitle`, which returns the share
/// title to the client) and any client caller, so both resolve a filename to the identical title.
/// Lives in `Shared` — not `Server` — because it must compile under Fable; it splits on an explicit
/// ASCII-whitespace set rather than a `\s` Regex both to stay Fable-safe (this exact `Split`-based
/// body already ships in the production client bundle) and to keep behavior identical on every
/// runtime — a Unicode space such as U+00A0 is therefore left intact, not collapsed (see
/// `FormattingTests`).
let prettifyFilename (filename: string) : string =
    let leaf = filename.Replace('\\', '/').Split('/') |> Array.last
    let stem =
        if leaf.EndsWith(".html", StringComparison.OrdinalIgnoreCase)
        then leaf.Substring(0, leaf.Length - ".html".Length)
        else leaf
    let spaced =
        stem.Replace('-', ' ').Replace('_', ' ').Split([| ' '; '\t'; '\n'; '\r'; '\f'; '\v' |], StringSplitOptions.RemoveEmptyEntries)
        |> String.concat " "
    if spaced = "" then filename
    else string (Char.ToUpperInvariant spaced[0]) + spaced.Substring(1)
