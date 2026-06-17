module Tests.CanvasDocServerTests

open System.IO
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

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type BuildInjectionTests() =

    // ── SystemView: stripped injection (no morph, no bridge) ──────────────────

    [<Test>]
    member _.``SystemView injection omits the idiomorph runtime and morph controller``() =
        let injection = buildInjection SystemView
        Assert.That(injection, Does.Not.Contain(Server.IdiomorphScript.idiomorphJs),
                    "A system view must never morph, so the idiomorph runtime is omitted")
        Assert.That(injection, Does.Not.Contain(Server.IdiomorphScript.morphController),
                    "A system view must never morph, so the morph controller is omitted")

    [<Test>]
    member _.``SystemView injection omits the message-bridge heartbeat``() =
        let injection = buildInjection SystemView
        Assert.That(injection, Does.Not.Contain(bridgeMarker),
                    "A system view has no owner session, so the bridge heartbeat is omitted")

    [<Test>]
    member _.``SystemView injection keeps the base style and link interceptor``() =
        let injection = buildInjection SystemView
        Assert.That(injection, Does.Contain(baseStyleMarker), "Both kinds keep the scrollbar base style")
        Assert.That(injection, Does.Contain(linkInterceptorMarker), "Both kinds keep the link interceptor")

    // ── Finding 11: query/hash-safe in-doc navigation ────────────────────────
    // A link like status.html?tab=errors must post the bare "status.html" a CanvasDoc.Filename can
    // match. The filename is taken from a.pathname (which excludes ?query/#hash), not the raw href.
    [<Test>]
    member _.``link interceptor derives the filename from a.pathname so ?query/#hash are stripped``() =
        let injection = buildInjection SystemView
        Assert.That(injection, Does.Contain("(a.pathname||h).split('/').pop()"),
                    "The clicked filename must come from a.pathname (no ?query/#hash), not the raw href")
        Assert.That(injection, Does.Not.Contain("var f=h.split('/').pop()"),
                    "The naive raw-href split keeps ?query/#hash and silently mis-tabs (Finding 11)")

    // ── AgentDoc: full injection (unchanged behaviour) ────────────────────────

    [<Test>]
    member _.``AgentDoc injection includes the full morph + bridge machinery``() =
        let injection = buildInjection AgentDoc
        Assert.That(injection, Does.Contain(Server.IdiomorphScript.idiomorphJs), "Agent docs keep the idiomorph runtime")
        Assert.That(injection, Does.Contain(Server.IdiomorphScript.morphController), "Agent docs keep the morph controller")
        Assert.That(injection, Does.Contain(bridgeMarker), "Agent docs keep the message-bridge heartbeat")
        Assert.That(injection, Does.Contain(baseStyleMarker))
        Assert.That(injection, Does.Contain(linkInterceptorMarker))

    // ── End-to-end of the server's decision: classify(filename) -> injection ──

    [<Test>]
    member _.``beads.html classifies as a SystemView and gets the stripped injection``() =
        let injection = buildInjection (CanvasDocKind.classify "beads.html")
        Assert.That(injection, Does.Not.Contain(Server.IdiomorphScript.idiomorphJs))
        Assert.That(injection, Does.Not.Contain(Server.IdiomorphScript.morphController))

    [<Test>]
    member _.``an agent .html classifies as an AgentDoc and gets the full injection``() =
        let injection = buildInjection (CanvasDocKind.classify "status.html")
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

    // attribute persists the whole ownership map to data/canvas-owners.json under the current
    // directory; run under a throwaway CWD so a test never touches the real data file. getOwner
    // (PostAndAsyncReply) is ordered after the attribute Post, so the persist flush always lands
    // in this temp dir before CWD is restored.
    let withTempCwd (action: unit -> unit) =
        let tempDir = Path.Combine(Path.GetTempPath(), $"canvasdocserver-test-{System.Guid.NewGuid()}")
        Directory.CreateDirectory(tempDir) |> ignore
        let original = System.Environment.CurrentDirectory
        System.Environment.CurrentDirectory <- tempDir

        try
            action ()
        finally
            System.Environment.CurrentDirectory <- original
            try Directory.Delete(tempDir, recursive = true) with _ -> ()

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
