module Tests.FormattingTests

open NUnit.Framework
open Shared.Formatting

// The single, consolidated prettify-filename suite. `prettifyFilename` used to be duplicated in the
// client (`CanvasUpdate`) and the server (`CanvasExport`), each tested by its own `PrettifyFilenameTests`
// fixture that only ever re-asserted its own side's behavior on inputs where the two agreed. The
// function now lives once in `Shared.Formatting`, so this is the only fixture — it merges both former
// suites and adds the Unicode-whitespace case that pins the chosen behavior.

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
[<Category("Canvas")>]
type PrettifyFilenameTests() =

    [<Test>]
    member _.``Turns a kebab filename into sentence case``() =
        Assert.That(prettifyFilename "build-status.html", Is.EqualTo("Build status"))

    [<Test>]
    member _.``Treats underscores as spaces``() =
        Assert.That(prettifyFilename "release_notes.html", Is.EqualTo("Release notes"))

    [<Test>]
    member _.``A single-word filename is just capitalized``() =
        Assert.That(prettifyFilename "data.html", Is.EqualTo("Data"))

    [<Test>]
    member _.``Handles mixed separators``() =
        Assert.That(prettifyFilename "weekly-sync_notes.html", Is.EqualTo("Weekly sync notes"))

    [<Test>]
    member _.``Strips the extension case-insensitively``() =
        Assert.That(prettifyFilename "Report.HTML", Is.EqualTo("Report"))

    [<Test>]
    member _.``Takes the leaf from a nested path, normalizing both separators``() =
        Assert.That(prettifyFilename "sub\\dir/build-status.html", Is.EqualTo("Build status"))

    [<Test>]
    member _.``Falls back to the raw name when stripping leaves nothing``() =
        Assert.That(prettifyFilename ".html", Is.EqualTo(".html"))

    // Parity lock: the shared impl splits on an explicit ASCII-whitespace set (Fable-safe), NOT a `\s`
    // Regex, so a Unicode space such as U+00A0 (non-breaking space) is preserved verbatim rather than
    // collapsed to an ASCII space. This case fails if anyone reintroduces a `\s`-based collapse.
    [<Test>]
    member _.``Preserves a non-breaking space instead of collapsing it``() =
        Assert.That(prettifyFilename "weekly\u00A0sync.html", Is.EqualTo("Weekly\u00A0sync"))
