module Tests.CanvasDocServerTests

open System.IO
open System.Text.RegularExpressions
open NUnit.Framework
open Shared
open Server
open Server.CanvasDocServer
open Tests.TestUtils

// baseStyle, linkInterceptor and bridgeScript are private to the server module, so we assert on
// stable, unique fragments of each. The idiomorph runtime and morph controller are public, so we
// assert on the exact strings the spec/verify task reference (IdiomorphScript.idiomorphJs etc.).
let private baseStyleMarker = "scrollbar-color"          // unique to baseStyle (CSS)
let private linkInterceptorMarker = "navigate-canvas-doc" // unique to linkInterceptor
let private bridgeMarker = "/bridge/heartbeat"            // unique to bridgeScript

// ── Item 1: dark-theme base reset markers ─────────────────────────────────────
let private resetWrapMarker = ":where(body)"  // reset selectors are :where()-wrapped (zero specificity)
let private resetDarkBgMarker = "#1e1e2e"      // the dark background the reset paints on a plain doc
// The base also steers plain docs toward typography over boxes (grounded, not invented):
let private resetTokenMarker = "--text-muted:#9399b2"          // app design tokens, via :where(:root)
let private resetTypeScaleMarker = ":where(h1){font-size:2rem"  // 1.25 "Major Third" heading scale
let private resetMeasureMarker = "max-width:70ch"               // Bringhurst ~45–75ch measure

// Element-name selectors that, if they appeared *bare* (name directly followed by `{`), would carry
// non-zero specificity and could beat a doc's own rule via the source-order tiebreak (the reset is
// injected AFTER the doc's <head> styles). Every reset selector must instead be :where()-wrapped, so
// none of these may appear in bare `name{` form. Inside :where(...) each name is followed by `,`/`)`
// (never `{`), so this only fires on a genuinely unwrapped selector.
let private bareElementSelector =
    Regex(@"(?<![\w-])(body|h1|h2|h3|h4|h5|h6|a|code|pre|kbd|samp|table|th|td)\s*\{")

/// Extract the <style> block content from an injection so specificity assertions never see the
/// injected <script> bodies (idiomorph/bridge/link-interceptor JS can legitimately contain `x{`).
let private styleBlock (injection: string) =
    let m = Regex.Match(injection, "<style>(.*?)</style>", RegexOptions.Singleline)
    Assert.That(m.Success, Is.True, "injection must contain a <style> block")
    m.Groups[1].Value

// ── Item 2: injected window.canvasSend helper markers ─────────────────────────
let private canvasSendMarker = "window.canvasSend="   // unique to canvasSendScript

// ── Item 3: injected JS error overlay marker ──────────────────────────────────
let private errorOverlayMarker = "canvas-doc-error"   // the action the overlay posts (unique to errorOverlayScript)

// The cap the client enforces (MaxPayloadBytes in src/Client/CanvasPane.fs, which is `private` so it
// can't be referenced directly). The injected helper must carry the identical literal so the
// doc-side drop decision matches the client's.
let [<Literal>] private ClientMaxPayloadBytes = 64000

/// The size cap the injected canvasSend helper actually enforces, parsed straight out of the
/// injected JS (`var MAX=<n>`). Reading the REAL literal — rather than hard-coding a second copy of
/// the helper's value — means a change to the helper's own cap is caught here. (The client's
/// MaxPayloadBytes is `private`/unreferenceable, so drift on the *client* side is instead guarded by
/// a reciprocal sync comment at MaxPayloadBytes in src/Client/CanvasPane.fs.)
let private helperCap (injection: string) =
    let m = Regex.Match(injection, @"var MAX=(\d+)")
    Assert.That(m.Success, Is.True, "canvasSend must define its size cap as `var MAX=<n>`")
    int m.Groups[1].Value

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type BuildInjectionTests() =

    // ── SystemView: stripped injection (no morph, no bridge) ──────────────────

    [<Test>]
    member _.``SystemView injection omits the idiomorph runtime and morph controller``() =
        let injection = buildInjection SystemView "beads.html"
        Assert.That(injection, Does.Not.Contain(Server.IdiomorphScript.idiomorphJs),
                    "A system view must never morph, so the idiomorph runtime is omitted")
        Assert.That(injection, Does.Not.Contain(Server.IdiomorphScript.morphController),
                    "A system view must never morph, so the morph controller is omitted")

    [<Test>]
    member _.``SystemView injection omits the message-bridge heartbeat``() =
        let injection = buildInjection SystemView "beads.html"
        Assert.That(injection, Does.Not.Contain(bridgeMarker),
                    "A system view has no owner session, so the bridge heartbeat is omitted")

    [<Test>]
    member _.``SystemView injection keeps the base style and link interceptor``() =
        let injection = buildInjection SystemView "beads.html"
        Assert.That(injection, Does.Contain(baseStyleMarker), "Both kinds keep the scrollbar base style")
        Assert.That(injection, Does.Contain(linkInterceptorMarker), "Both kinds keep the link interceptor")

    // ── Item 1: dark-theme base reset, injected for BOTH kinds, zero specificity ──

    [<Test>]
    member _.``both doc kinds inject the dark-theme base reset``() =
        [ SystemView; AgentDoc ]
        |> List.iter (fun kind ->
            let injection = buildInjection kind "status.html"
            Assert.That(injection, Does.Contain(resetWrapMarker),
                        $"{kind}: the base reset (:where(body)) must be injected for both kinds")
            Assert.That(injection, Does.Contain(resetDarkBgMarker),
                        $"{kind}: the reset must set the dark theme background so a plain doc renders dark"))

    [<Test>]
    member _.``the base reset bakes in design tokens, a type scale, and a readable measure``() =
        // Beyond dark colours the base steers plain docs toward typography over boxes: a :where(:root)
        // token palette (so docs stop reinventing one), a 1.25 Major Third heading scale (hierarchy
        // from size, not borders), and a ~70ch measure on text elements (readable line length).
        [ SystemView; AgentDoc ]
        |> List.iter (fun kind ->
            let injection = buildInjection kind "status.html"
            Assert.That(injection, Does.Contain(resetTokenMarker),
                        $"{kind}: the base must expose the app design tokens so docs reuse the palette")
            Assert.That(injection, Does.Contain(resetTypeScaleMarker),
                        $"{kind}: the base must bake in the heading type scale")
            Assert.That(injection, Does.Contain(resetMeasureMarker),
                        $"{kind}: the base must cap the text measure (~70ch) for readability"))

    [<Test>]
    member _.``the base reset carries zero specificity (no bare element selectors, no !important)``() =
        // The reset lands right before </head>, AFTER any doc/template <head> styles. At equal
        // specificity it would win the source-order tiebreak and stomp the doc; wrapping every
        // selector in :where(...) drops it to zero specificity so even a bare body{} doc rule — and
        // the beads SystemView template's own body{background:var(--bg-deep)} — overrides it.
        let style = styleBlock (buildInjection SystemView "beads.html")
        Assert.That(style, Does.Not.Contain("!important"),
                    "no reset rule may use !important — that would defeat doc/template overrides")
        let bare = bareElementSelector.Match(style)
        Assert.That(bare.Success, Is.False,
                    $"reset selectors must be :where()-wrapped (zero specificity); found bare selector '{bare.Value}'")

    // ── Finding 11: query/hash-safe in-doc navigation ────────────────────────
    // A link like status.html?tab=errors must post the bare "status.html" a CanvasDoc.Filename can
    // match. The filename is taken from a.pathname (which excludes ?query/#hash), not the raw href.
    [<Test>]
    member _.``link interceptor derives the filename from a.pathname so ?query/#hash are stripped``() =
        let injection = buildInjection SystemView "beads.html"
        Assert.That(injection, Does.Contain("(a.pathname||h).split('/').pop()"),
                    "The clicked filename must come from a.pathname (no ?query/#hash), not the raw href")
        Assert.That(injection, Does.Not.Contain("var f=h.split('/').pop()"),
                    "The naive raw-href split keeps ?query/#hash and silently mis-tabs (Finding 11)")

    // ── AgentDoc: full injection (unchanged behaviour) ────────────────────────

    [<Test>]
    member _.``AgentDoc injection includes the full morph + bridge machinery``() =
        let injection = buildInjection AgentDoc "status.html"
        Assert.That(injection, Does.Contain(Server.IdiomorphScript.idiomorphJs), "Agent docs keep the idiomorph runtime")
        Assert.That(injection, Does.Contain(Server.IdiomorphScript.morphController), "Agent docs keep the morph controller")
        Assert.That(injection, Does.Contain(bridgeMarker), "Agent docs keep the message-bridge heartbeat")
        Assert.That(injection, Does.Contain(baseStyleMarker))
        Assert.That(injection, Does.Contain(linkInterceptorMarker))

    // ── Item 2: injected window.canvasSend(action, payload) helper ────────────
    // canvasSend wraps the flat postMessage contract and enforces the SAME size cap the client
    // applies (CanvasPane.fs drops when JSON.stringify(me.data).length > MaxPayloadBytes). It is
    // injected for AgentDocs only — a SystemView is server-generated and posts nothing.

    [<Test>]
    member _.``AgentDoc injection includes the canvasSend helper``() =
        let injection = buildInjection AgentDoc "status.html"
        Assert.That(injection, Does.Contain(canvasSendMarker),
                    "Agent docs get the first-class window.canvasSend(action,payload) helper")

    [<Test>]
    member _.``SystemView injection omits the canvasSend helper``() =
        let injection = buildInjection SystemView "beads.html"
        Assert.That(injection, Does.Not.Contain(canvasSendMarker),
                    "A system view is server-generated and posts nothing, so canvasSend is omitted")

    [<Test>]
    member _.``the canvasSend helper measures the same metric as the client (JSON.stringify length)``() =
        // The client drops on JSON.stringify(me.data).length (UTF-16 code units / JS String.length).
        // The helper must measure the identical metric on the object it posts — NOT a UTF-8 byte
        // length, which would diverge from the client's String.length check.
        let injection = buildInjection AgentDoc "status.html"
        Assert.That(injection, Does.Contain("JSON.stringify(msg).length"),
                    "helper must size-check JSON.stringify(...).length — the same metric the client enforces")

    [<Test>]
    member _.``the canvasSend size cap matches the client's MaxPayloadBytes``() =
        // Drift guard: the helper's literal must equal the cap the client enforces in CanvasPane.fs,
        // otherwise the helper could block a payload the client accepts (or pass one it drops).
        Assert.That(helperCap (buildInjection AgentDoc "status.html"), Is.EqualTo(ClientMaxPayloadBytes),
                    "canvasSend's cap must equal MaxPayloadBytes (src/Client/CanvasPane.fs)")

    [<Test>]
    member _.``the canvasSend helper drops STRICTLY above the cap, so exactly-cap is still delivered``() =
        // The client accepts iff JSON.stringify(me.data).length <= MaxPayloadBytes. For the helper's
        // verdict to match the client's at EVERY size, three things must line up — each pinned by a
        // test here so none can drift silently:
        //   1. the same metric   — `JSON.stringify(msg).length`          (test above)
        //   2. the same cap      — `var MAX=64000` == MaxPayloadBytes    (test above)
        //   3. a strict `>` drop — so accept is `<=`, boundary inclusive (this test)
        // With an identical metric and cap, a strict `size>MAX` drop is the exact complement of the
        // client's `<=` accept: a message whose serialized length is EXACTLY the cap is delivered by
        // both, and cap+1 is dropped by both. (A non-strict `>=` would drop at exactly-cap and
        // diverge.) Asserting the operator in the EMITTED JS is the real guard; re-deriving the
        // verdict in F# would only restate the trichotomy identity `not (x > c) = (x <= c)`.
        Assert.That(buildInjection AgentDoc "status.html", Does.Contain("if(size>MAX)"),
                    "helper must drop only when size STRICTLY exceeds the cap, so exactly-cap is delivered (matches the client's <=)")

    [<Test>]
    member _.``the canvasSend action argument overrides an action key in the payload``() =
        // Regression guard (focused-review A-01). The helper originally built
        //   var msg=Object.assign({action:action},payload);
        // which applies `payload` LAST, so a payload carrying its OWN `action` key silently overrode
        // the caller's action argument: canvasSend('navigate-canvas-doc',{action:'x',filename:'y'})
        // posted {action:'x',...}, the pane never recognised it as navigate-canvas-doc, and the size
        // check ran against the wrong object — yet canvasSend still returned true, so the failure was
        // silent. The explicit action argument must ALWAYS win, so it has to be merged AFTER payload
        // (Object.assign({},payload,{action:action})). There is no JS engine in this test project, so
        // the behaviour is pinned via the merge ORDER in the emitted JS the doc actually runs. (If the
        // helper is ever refactored to `msg.action=action` after the assign, update the second regex.)
        let injection = buildInjection AgentDoc "status.html"
        // (a) the original action-FIRST order — which let payload.action win — must be gone
        Assert.That(Regex.IsMatch(injection, @"Object\.assign\(\s*\{\s*action\s*:\s*action\s*\}\s*,\s*payload"),
                    Is.False,
                    "action must not be merged BEFORE payload, or a payload `action` key would override the caller's action argument")
        // (b) the explicit action must be merged AFTER payload so it overrides any payload.action
        Assert.That(Regex.IsMatch(injection, @"Object\.assign\([^)]*payload[^)]*action\s*:\s*action"),
                    Is.True,
                    "canvasSend's explicit action argument must be merged last so it overrides any `action` key in payload")

    // ── Item 3: injected JS error overlay (window.onerror + unhandledrejection) ──
    // The overlay (AgentDoc only) forwards doc-side JS failures to the pane as the flat
    // {action:'canvas-doc-error', wt, doc, message, source, line, col} message the client surfaces in a
    // dismissible banner. A SystemView runs no author JS, so it never gets the overlay.

    [<Test>]
    member _.``AgentDoc injection includes the JS error overlay``() =
        let injection = buildInjection AgentDoc "status.html"
        Assert.That(injection, Does.Contain(errorOverlayMarker),
                    "Agent docs get the JS error overlay that forwards doc-side errors to the pane")
        Assert.That(injection, Does.Contain("window.onerror="),
                    "the overlay installs window.onerror to catch uncaught errors")
        Assert.That(injection, Does.Contain("unhandledrejection"),
                    "the overlay also catches unhandled promise rejections")

    [<Test>]
    member _.``SystemView injection omits the JS error overlay``() =
        let injection = buildInjection SystemView "beads.html"
        Assert.That(injection, Does.Not.Contain(errorOverlayMarker),
                    "A system view runs no author JS, so the error overlay is omitted")

    [<Test>]
    member _.``the error overlay wraps its postMessage in try/catch so the error path can't loop``() =
        // A throw inside the onerror handler would re-enter window.onerror and spin an error loop;
        // the post is guarded so a serialization failure in the error path is swallowed, not rethrown.
        let injection = buildInjection AgentDoc "status.html"
        Assert.That(injection, Does.Contain("catch(e){}"),
                    "the overlay's postMessage must be wrapped so the error handler can never re-throw")

    [<Test>]
    member _.``the error overlay embeds the emitting doc's filename and posts it as the doc field``() =
        // Doc identity (focused-review A-02, C-06): the overlay is served per-doc and derives its own
        // worktree from location.pathname, so it stamps THIS doc's worktree + filename into the posted
        // payload (wt:WT, doc:DOC) — letting the pane attribute the error to the emitter, not the active
        // tab, even when other docs (in any worktree) are mounted as hidden iframes. The filename literal
        // is JSON-serialized at injection time, so it is a quoted, HTML-safe JS string.
        let injection = buildInjection AgentDoc "status.html"
        Assert.That(injection, Does.Contain("var DOC=\"status.html\""),
                    "the overlay must embed the served doc's filename as a JS string constant")
        Assert.That(injection, Does.Contain("doc:DOC"),
                    "the canvas-doc-error payload must carry the emitting doc's identity in the doc field")
        Assert.That(injection, Does.Contain("var WT=decodeURIComponent(location.pathname"),
                    "the overlay must derive the emitting worktree from its own URL (mirrors the bridge heartbeat)")
        Assert.That(injection, Does.Contain("wt:WT"),
                    "the canvas-doc-error payload must carry the emitting worktree so attribution doesn't depend on focus")

    [<Test>]
    member _.``the error overlay's embedded filename is escaped so a crafted name can't break out``() =
        // The filename is spliced into injected <script> source. JSON-serializing it escapes quotes
        // and HTML-significant chars (<,>,&) to \uXXXX, so a name carrying e.g. </script> or a quote
        // can neither close the script element nor terminate the JS string literal early.
        let injection = buildInjection AgentDoc "a\"</script>.html"
        Assert.That(injection, Does.Not.Contain("var DOC=\"a\"</script>"),
                    "a raw quote/script tag must not appear unescaped in the embedded doc literal")
        Assert.That(injection, Does.Not.Contain("</script>.html"),
                    "the embedded filename must be HTML-escaped so it cannot close the injected script")

    // ── End-to-end of the server's decision: classify(filename) -> injection ──

    [<Test>]
    member _.``beads.html classifies as a SystemView and gets the stripped injection``() =
        let injection = buildInjection (CanvasDocKind.classify "beads.html") "beads.html"
        Assert.That(injection, Does.Not.Contain(Server.IdiomorphScript.idiomorphJs))
        Assert.That(injection, Does.Not.Contain(Server.IdiomorphScript.morphController))

    [<Test>]
    member _.``an agent .html classifies as an AgentDoc and gets the full injection``() =
        let injection = buildInjection (CanvasDocKind.classify "status.html") "status.html"
        Assert.That(injection, Does.Contain(Server.IdiomorphScript.idiomorphJs))
        Assert.That(injection, Does.Contain(Server.IdiomorphScript.morphController))

// ── injectUrl loopback guard (Finding 10 / SSRF) ──────────────────────────────
// injectUrl is registered then used as a POST target by CanvasBridge, so a non-loopback value
// would turn /api/canvas/register into an SSRF primitive. Only loopback hosts are accepted.
[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type LoopbackInjectUrlTests() =

    [<TestCase("http://127.0.0.1:8765/inject")>]   // IPv4 loopback (the real extension's value)
    [<TestCase("http://127.5.6.7/inject")>]        // all of 127.0.0.0/8 is loopback
    [<TestCase("http://localhost:8765/inject")>]   // literal localhost
    [<TestCase("http://LOCALHOST/inject")>]        // literal localhost is case-insensitive
    [<TestCase("https://localhost/inject")>]       // scheme-agnostic
    [<TestCase("http://[::1]:8765/inject")>]       // IPv6 loopback
    member _.``accepts loopback inject URLs``(url: string) =
        Assert.That(isLoopbackInjectUrl url, Is.True, $"{url} resolves to a loopback host")

    [<TestCase("http://evil.com/inject")>]                   // arbitrary remote host
    [<TestCase("http://169.254.169.254/latest/meta-data")>]  // cloud metadata SSRF target
    [<TestCase("http://127.0.0.1.evil.com/inject")>]         // loopback-looking label, remote host
    [<TestCase("http://10.0.0.5/inject")>]                   // private but non-loopback
    [<TestCase("file://localhost/etc/passwd")>]              // loopback host but non-http scheme
    [<TestCase("/inject")>]                                  // relative — no absolute host
    [<TestCase("not a url")>]                                // unparseable
    [<TestCase("")>]                                         // empty
    member _.``rejects non-loopback or malformed inject URLs``(url: string) =
        Assert.That(isLoopbackInjectUrl url, Is.False, $"{url} must be rejected")

// ── /api/canvas/attribute ownership declaration ───────────────────────────────
// attributeOwnership is the HTTP-free core of canvasAttributeHandler (the same seam extraction
// isLoopbackInjectUrl uses for canvasRegisterHandler). It records ownership only for a known
// (monitored) worktree with a well-formed body; an unmonitored/blank worktree records nothing.
// CanvasDocOwnership.attribute persists data/canvas-owners.json relative to CWD, so the fixture
// runs under a throwaway CWD; it is NonParallelizable because that CWD swap and the ownership
// agent are process-global.
[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
[<NonParallelizable>]
type AttributeOwnershipTests() =

    // Unique per test so the process-global ownership agent never leaks state between tests.
    let uniquePath prefix =
        let id = System.Guid.NewGuid().ToString("N")[..7]
        $"/test/{prefix}/{id}"

    let uniqueSid prefix =
        let id = System.Guid.NewGuid().ToString("N")[..7]
        $"{prefix}-{id}"

    // A scheduler agent that knows exactly one worktree. KnownPaths stores WorktreeInfo.Path
    // verbatim and isKnownWorktree compares the normalized request path, so the path is stored
    // normalized just like the real populate path does.
    let agentKnowing (worktreePath: string) =
        let agent = RefreshScheduler.createAgent ()

        let info: GitWorktree.WorktreeInfo =
            { Path = PathUtils.normalizePath worktreePath; Head = ""; Branch = Some "test" }

        agent.Post(RefreshScheduler.UpdateWorktreeList(RepoId "attr-test-repo", [ info ]))
        agent

    [<Test>]
    member _.``a valid declaration for a known worktree records the posted owner``() =
        withTempCwd (fun () ->
            let worktree = uniquePath "attr-known"
            let sessionId = uniqueSid "owner"
            let agent = agentKnowing worktree

            let outcome = runAsync (attributeOwnership agent worktree "a.html" sessionId)
            Assert.That(outcome, Is.EqualTo(Attributed),
                        "A well-formed body for a monitored worktree must record ownership")

            let owner = runAsync (CanvasDocOwnership.getOwner worktree "a.html")
            Assert.That(owner, Is.EqualTo(Some sessionId),
                        "getOwner must return the sessionId the authoring session declared"))

    [<Test>]
    member _.``an unknown worktree is rejected and records no ownership``() =
        withTempCwd (fun () ->
            let unknown = uniquePath "attr-unknown"
            // The agent knows a *different* worktree, so `unknown` is unmonitored.
            let agent = agentKnowing (uniquePath "attr-known")

            let outcome = runAsync (attributeOwnership agent unknown "a.html" (uniqueSid "owner"))
            Assert.That(outcome, Is.EqualTo(UnknownWorktree), "An unmonitored worktree must be rejected")

            let owner = runAsync (CanvasDocOwnership.getOwner unknown "a.html")
            Assert.That(owner, Is.EqualTo(None: string option),
                        "No ownership may be recorded for an unknown worktree"))

    [<Test>]
    member _.``a blank worktree path is rejected as Invalid``() =
        withTempCwd (fun () ->
            let agent = agentKnowing (uniquePath "attr-known")

            let outcome = runAsync (attributeOwnership agent "   " "a.html" (uniqueSid "owner"))

            match outcome with
            | Invalid _ -> ()
            | other -> Assert.Fail($"A blank worktree path must be Invalid, got {other}"))

    [<Test>]
    member _.``a malformed body (blank sessionId) is rejected and records no ownership``() =
        withTempCwd (fun () ->
            // The worktree IS known, so only the blank sessionId can reject — proving the
            // rejection short-circuits before CanvasDocOwnership.attribute is ever called.
            let worktree = uniquePath "attr-malformed"
            let agent = agentKnowing worktree

            let outcome = runAsync (attributeOwnership agent worktree "a.html" "   ")

            match outcome with
            | Invalid _ -> ()
            | other -> Assert.Fail($"A blank sessionId must be Invalid, got {other}")

            let owner = runAsync (CanvasDocOwnership.getOwner worktree "a.html")
            Assert.That(owner, Is.EqualTo(None: string option),
                        "A rejected declaration must record no ownership"))
