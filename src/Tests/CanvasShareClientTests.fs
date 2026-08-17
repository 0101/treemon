module Tests.CanvasShareClientTests

open NUnit.Framework
open Shared
open CanvasUpdate

// Unit tests for the client-side share helper in CanvasUpdate: the rich-link clipboard payload
// builder (both formats). Pure functions — no browser, no clipboard, no server.

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
[<Category("Canvas")>]
type BuildClipboardPayloadTests() =

    let viewerUrl =
        "https://viewer.test/c/0123456789AbCdEfGhIjKl/build-status.html"

    [<Test>]
    member _.``Writes BOTH formats: titled text/html anchor + plain-text URL``() =
        let result = { Url = viewerUrl; Title = "Build Status Report" }
        let payload = buildClipboardPayload result

        // text/plain is the raw URL, verbatim (plain targets get the link itself).
        Assert.That(payload.Text, Is.EqualTo(viewerUrl), "text/plain must be the raw viewer URL")

        // text/html is a titled anchor: the visible text is the doc title, the href is the URL.
        Assert.That(payload.Html, Does.StartWith("<a href=\""), "text/html must be an anchor")
        Assert.That(payload.Html, Does.EndWith("</a>"))
        Assert.That(payload.Html, Does.Contain(">Build Status Report</a>"), "anchor text must be the title")
        Assert.That(payload.Html, Does.Contain($"href=\"{viewerUrl}\""),
                    "href must carry the full clean viewer URL")

    [<Test>]
    member _.``HTML-special characters in the title are escaped in the anchor text``() =
        let result = { Url = viewerUrl; Title = "A & B <tag> \"q\"" }
        let payload = buildClipboardPayload result

        Assert.That(payload.Html, Does.Contain(">A &amp; B &lt;tag&gt; &quot;q&quot;</a>"),
                    "title must be HTML-escaped so it cannot inject markup")
