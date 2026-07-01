module Tests.CanvasShareClientTests

open NUnit.Framework
open Shared
open CanvasUpdate

// Unit tests for the client-side share helpers in CanvasUpdate: the rich-link clipboard payload
// builder (both formats) and the filename-prettify title fallback it uses. Pure functions — no
// browser, no clipboard, no server.

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
[<Category("Canvas")>]
type BuildClipboardPayloadTests() =

    // A representative per-doc SAS URL: long, and carrying `&`-separated query params so the test
    // also exercises HTML-escaping of the href.
    let sasUrl = "https://acct.blob.core.windows.net/canvas/9fA2/build-status.html?sv=2023-11-03&sr=b&sp=r&sig=aB%2Bc%3D"

    [<Test>]
    member _.``Writes BOTH formats: titled text/html anchor + plain-text URL``() =
        let result = { Url = sasUrl; Title = "Build Status Report" }
        let payload = buildClipboardPayload result "build-status.html"

        // text/plain is the raw URL, verbatim (plain targets get the link itself).
        Assert.That(payload.Text, Is.EqualTo(sasUrl), "text/plain must be the raw SAS URL")

        // text/html is a titled anchor: the visible text is the doc title, the href is the URL.
        Assert.That(payload.Html, Does.StartWith("<a href=\""), "text/html must be an anchor")
        Assert.That(payload.Html, Does.EndWith("</a>"))
        Assert.That(payload.Html, Does.Contain(">Build Status Report</a>"), "anchor text must be the title")
        // The href is HTML-escaped (the URL's `&` becomes `&amp;`) so the anchor can't be truncated.
        Assert.That(payload.Html, Does.Contain("href=\"https://acct.blob.core.windows.net/canvas/9fA2/build-status.html?sv=2023-11-03&amp;sr=b&amp;sp=r&amp;sig=aB%2Bc%3D\""),
                    "href must carry the full, HTML-escaped SAS URL")

    [<Test>]
    member _.``Title falls back to the prettified filename when the server title is blank``() =
        let result = { Url = sasUrl; Title = "" }
        let payload = buildClipboardPayload result "build-status.html"

        // build-status.html → "Build status" (drop .html, '-'→space, sentence-case).
        Assert.That(payload.Html, Does.Contain(">Build status</a>"),
                    "a blank server title must fall back to the prettified filename")
        Assert.That(payload.Text, Is.EqualTo(sasUrl), "text/plain is still the raw URL on the fallback path")

    [<Test>]
    member _.``Whitespace-only title also falls back to the prettified filename``() =
        let result = { Url = sasUrl; Title = "   " }
        let payload = buildClipboardPayload result "release_notes.html"

        // release_notes.html → "Release notes" ('_'→space).
        Assert.That(payload.Html, Does.Contain(">Release notes</a>"))

    [<Test>]
    member _.``HTML-special characters in the title are escaped in the anchor text``() =
        let result = { Url = "https://acct.blob.core.windows.net/canvas/x/a.html?sig=x"; Title = "A & B <tag> \"q\"" }
        let payload = buildClipboardPayload result "a.html"

        Assert.That(payload.Html, Does.Contain(">A &amp; B &lt;tag&gt; &quot;q&quot;</a>"),
                    "title must be HTML-escaped so it cannot inject markup")


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
[<Category("Canvas")>]
type PrettifyFilenameTests() =

    [<Test>]
    member _.``Drops .html, turns hyphens into spaces, sentence-cases``() =
        Assert.That(prettifyFilename "build-status.html", Is.EqualTo("Build status"))

    [<Test>]
    member _.``Turns underscores into spaces``() =
        Assert.That(prettifyFilename "release_notes.html", Is.EqualTo("Release notes"))

    [<Test>]
    member _.``Single-word filename is just capitalized``() =
        Assert.That(prettifyFilename "data.html", Is.EqualTo("Data"))

    [<Test>]
    member _.``.html match is case-insensitive``() =
        Assert.That(prettifyFilename "Report.HTML", Is.EqualTo("Report"))
