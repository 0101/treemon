module Tests.CanvasExportTests

open NUnit.Framework
open Shared
open Server
open Server.CanvasExport

// The static export re-injects exactly TWO pieces into a published, standalone copy of a canvas doc
// — the shared base theme and a NO-OP window.canvasSend — and NONE of the pane-only machinery the
// live server adds. These markers assert presence of the two and absence of everything else; each is
// a stable, unique fragment of its script (mirroring CanvasDocServerTests, which markers the same
// live-injected scripts).

// ── present in the static export ──────────────────────────────────────────────
let private themeMarker = "scrollbar-color"          // unique to baseStyle (CSS)
let private themeTokenMarker = "--text-muted:#9399b2" // an app design token from baseStyle
let private canvasSendMarker = "window.canvasSend="   // the (inert) helper is still DEFINED

// ── must be ABSENT from the static export (pane-only, omitted) ────────────────
let private realPostMarker = "window.parent.postMessage" // the REAL canvasSend/overlay poster
let private sizeGuardMarker = "var MAX="                 // the real canvasSend's size guard
let private bridgeMarker = "/bridge/heartbeat"           // bridge heartbeat
let private errorOverlayMarker = "canvas-doc-error"      // JS error overlay
let private linkInterceptorMarker = "navigate-canvas-doc" // link interceptor (pane tab-switch)

// A minimal doc with a </head> slot; buildStaticHtml splices its injection in just before it.
let private sampleDoc = "<html><head><title>x</title></head><body>hi</body></html>"

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type StaticExportInjectionTests() =

    // ── the two pieces a standalone copy needs ARE injected ───────────────────

    [<Test>]
    member _.``buildStaticHtml re-injects the base theme``() =
        let html = buildStaticHtml sampleDoc
        Assert.That(html, Does.Contain(themeMarker),
                    "a published copy must carry the base theme so a doc that leaned on it renders on-theme")
        Assert.That(html, Does.Contain(themeTokenMarker),
                    "the theme's design tokens must be present so var(--…) references resolve standalone")

    [<Test>]
    member _.``buildStaticHtml re-injects a no-op window.canvasSend``() =
        let html = buildStaticHtml sampleDoc
        Assert.That(html, Does.Contain(canvasSendMarker),
                    "author buttons that call canvasSend(...) must not throw ReferenceError standalone")

    [<Test>]
    member _.``the injected canvasSend is inert (defined but posts nothing)``() =
        let html = buildStaticHtml sampleDoc
        // It is DEFINED (so calls don't throw) but is a no-op that returns false (nothing delivered),
        // NOT the live helper that posts to the pane and size-checks the payload.
        Assert.That(html, Does.Contain("window.canvasSend=function(){return false}"),
                    "the export's canvasSend must be the inert no-op form")
        Assert.That(html, Does.Not.Contain(realPostMarker),
                    "the export's canvasSend must not post to a parent — there is no pane")
        Assert.That(html, Does.Not.Contain(sizeGuardMarker),
                    "the inert helper carries none of the live helper's size-guard machinery")

    // ── the pane-only machinery is NOT injected ───────────────────────────────

    [<Test>]
    member _.``buildStaticHtml omits the bridge heartbeat``() =
        Assert.That(buildStaticHtml sampleDoc, Does.Not.Contain(bridgeMarker),
                    "a standalone doc has no bridge to heartbeat")

    [<Test>]
    member _.``buildStaticHtml omits the idiomorph runtime and morph controller``() =
        let html = buildStaticHtml sampleDoc
        Assert.That(html, Does.Not.Contain(Server.IdiomorphScript.idiomorphJs),
                    "a standalone doc never morphs, so the idiomorph runtime is omitted")
        Assert.That(html, Does.Not.Contain(Server.IdiomorphScript.morphController),
                    "a standalone doc never morphs, so the morph controller is omitted")

    [<Test>]
    member _.``buildStaticHtml omits the JS error overlay``() =
        Assert.That(buildStaticHtml sampleDoc, Does.Not.Contain(errorOverlayMarker),
                    "a standalone doc has no pane to surface errors in, so the overlay is omitted")

    [<Test>]
    member _.``buildStaticHtml omits the link interceptor (sibling .html links are inert)``() =
        Assert.That(buildStaticHtml sampleDoc, Does.Not.Contain(linkInterceptorMarker),
                    "the tab-switch interceptor is pane-coupling; sibling .html links are inert standalone")

    // ── placement mirrors the live server (right before </head>) ──────────────

    [<Test>]
    member _.``buildStaticHtml splices the injection immediately before </head> and preserves the doc``() =
        let html = buildStaticHtml sampleDoc
        // The injection ends with the inert canvasSend </script>, so </head> is right after it.
        Assert.That(html, Does.Contain("return false}</script></head>"),
                    "the injection must land immediately before the closing </head>")
        Assert.That(html, Does.Contain("<body>hi</body>"), "the original doc body must be preserved")
        Assert.That(html, Does.Contain("<title>x</title>"), "the original head content must be preserved")

    [<Test>]
    member _.``buildStaticHtml matches </head> case-insensitively``() =
        let html = buildStaticHtml "<head></HEAD>"
        Assert.That(html, Does.Contain(themeMarker),
                    "a </HEAD> in any case must still receive the injection")

    [<Test>]
    member _.``buildStaticHtml prepends when the doc has no </head>``() =
        let html = buildStaticHtml "<div>plain</div>"
        Assert.That(html, Does.StartWith("<style>"), "with no </head>, the theme is prepended to the whole doc")
        Assert.That(html, Does.EndWith("<div>plain</div>"), "the original doc follows the prepended injection")

    // ── the exported theme is the SAME shared string the live server injects ───

    [<Test>]
    member _.``the export and the live server inject the identical shared base theme``() =
        // baseStyle was relocated into CanvasExport and both paths reference it, so the theme a
        // recipient sees is byte-identical to what the pane shows — no second copy to drift.
        Assert.That(buildStaticHtml sampleDoc, Does.Contain(CanvasExport.baseStyle),
                    "the export must inject the shared CanvasExport.baseStyle")
        Assert.That(Server.CanvasDocServer.buildInjection SystemView "beads.html",
                    Does.Contain(CanvasExport.baseStyle),
                    "the live server must inject the very same shared baseStyle")


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type InjectAtHeadTests() =

    [<Test>]
    member _.``injectAtHead splices immediately before an existing </head>``() =
        Assert.That(injectAtHead "<INJ>" "<head></head>", Is.EqualTo("<head><INJ></head>"))

    [<Test>]
    member _.``injectAtHead prepends when there is no </head>``() =
        Assert.That(injectAtHead "<INJ>" "no head here", Is.EqualTo("<INJ>no head here"))

    [<Test>]
    member _.``injectAtHead is case-insensitive on the head close tag``() =
        // The matched close tag is rewritten to the injection + a lower-case </head>; content around
        // it is otherwise untouched.
        Assert.That(injectAtHead "<INJ>" "<head></HEAD>tail", Is.EqualTo("<head><INJ></head>tail"))


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type ExtractTitleTests() =

    let assertSome (expected: string) (actual: string option) =
        match actual with
        | Some v -> Assert.That(v, Is.EqualTo(expected))
        | None -> Assert.Fail($"expected Some \"{expected}\" but got None")

    [<Test>]
    member _.``extractTitle returns the title text``() =
        assertSome "Build status" (extractTitle "<html><head><title>Build status</title></head></html>")

    [<Test>]
    member _.``extractTitle returns None when there is no title``() =
        Assert.That(Option.isNone (extractTitle "<html><head></head><body>hi</body></html>"), Is.True)

    [<Test>]
    member _.``extractTitle returns None for a blank title``() =
        Assert.That(Option.isNone (extractTitle "<title>   </title>"), Is.True,
                    "a whitespace-only title is not a usable label")

    [<Test>]
    member _.``extractTitle matches <title> case-insensitively``() =
        assertSome "Foo" (extractTitle "<TITLE>Foo</TITLE>")

    [<Test>]
    member _.``extractTitle ignores attributes on the title tag``() =
        assertSome "Foo" (extractTitle "<title data-x=\"y\">Foo</title>")

    [<Test>]
    member _.``extractTitle HTML-decodes entities``() =
        assertSome "Build & Deploy" (extractTitle "<title>Build &amp; Deploy</title>")

    [<Test>]
    member _.``extractTitle collapses internal whitespace like a browser``() =
        assertSome "Build status" (extractTitle "<title>\n   Build   status\n</title>")

    [<Test>]
    member _.``extractTitle takes the first title when several are present``() =
        assertSome "First" (extractTitle "<title>First</title><title>Second</title>")


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type PrettifyFilenameTests() =

    [<Test>]
    member _.``prettifyFilename turns a kebab filename into sentence case``() =
        Assert.That(prettifyFilename "build-status.html", Is.EqualTo("Build status"))

    [<Test>]
    member _.``prettifyFilename handles a single word``() =
        Assert.That(prettifyFilename "ci.html", Is.EqualTo("Ci"))

    [<Test>]
    member _.``prettifyFilename treats underscores as spaces``() =
        Assert.That(prettifyFilename "my_report.html", Is.EqualTo("My report"))

    [<Test>]
    member _.``prettifyFilename handles mixed separators``() =
        Assert.That(prettifyFilename "weekly-sync_notes.html", Is.EqualTo("Weekly sync notes"))

    [<Test>]
    member _.``prettifyFilename strips the extension case-insensitively``() =
        Assert.That(prettifyFilename "Report.HTML", Is.EqualTo("Report"))

    [<Test>]
    member _.``prettifyFilename falls back to the raw name when stripping leaves nothing``() =
        Assert.That(prettifyFilename ".html", Is.EqualTo(".html"))


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type ResolveTitleTests() =

    [<Test>]
    member _.``resolveTitle prefers the doc title``() =
        Assert.That(resolveTitle "<title>Real Title</title>" "build-status.html",
                    Is.EqualTo("Real Title"))

    [<Test>]
    member _.``resolveTitle falls back to the prettified filename when there is no title``() =
        Assert.That(resolveTitle "<html><body>no title here</body></html>" "build-status.html",
                    Is.EqualTo("Build status"))

    [<Test>]
    member _.``resolveTitle falls back when the title is blank``() =
        Assert.That(resolveTitle "<title>   </title>" "ci.html", Is.EqualTo("Ci"))
