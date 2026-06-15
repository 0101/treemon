module Tests.CanvasDocServerTests

open NUnit.Framework
open Shared
open Server.CanvasDocServer

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
