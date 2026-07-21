module Tests.HttpSecurityTests

open NUnit.Framework
open Server.HttpSecurity

// Unit coverage for the CSRF guard's pure decision core (Server.HttpSecurity, internal via
// InternalsVisibleTo). The guard rejects a state-changing request only when it carries a
// non-loopback Origin/Referer; a missing header pair is allowed so the non-browser Cli (sends
// neither) and same-origin SPA keep working.

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type IsSameOriginRequestTests() =

    // ── Missing Origin/Referer: allowed (Cli sends neither; some browsers omit on same-origin) ──
    [<Test>]
    member _.``no Origin and no Referer is allowed``() =
        Assert.That(isSameOriginRequest None None, Is.True)

    // ── Present loopback Origin: allowed (the same-origin SPA) ──
    [<Test>]
    member _.``localhost Origin is allowed``() =
        Assert.That(isSameOriginRequest (Some "http://localhost:5000") None, Is.True)

    [<Test>]
    member _.``127.0.0.1 Origin is allowed``() =
        Assert.That(isSameOriginRequest (Some "http://127.0.0.1:5000") None, Is.True)

    [<Test>]
    member _.``IPv6 loopback Origin is allowed``() =
        Assert.That(isSameOriginRequest (Some "http://[::1]:5000") None, Is.True)

    [<Test>]
    member _.``https loopback Origin is allowed``() =
        Assert.That(isSameOriginRequest (Some "https://localhost") None, Is.True)

    [<Test>]
    member _.``loopback Origin on any port is allowed``() =
        Assert.That(isSameOriginRequest (Some "http://127.0.0.1:9999") None, Is.True)

    // ── Present non-loopback Origin: rejected (the cross-origin attack surface) ──
    [<Test>]
    member _.``cross-origin Origin is rejected``() =
        Assert.That(isSameOriginRequest (Some "http://evil.com") None, Is.False)

    [<Test>]
    member _.``LAN IP Origin is rejected``() =
        Assert.That(isSameOriginRequest (Some "http://192.168.1.10:5000") None, Is.False)

    [<Test>]
    member _.``opaque null Origin is rejected``() =
        Assert.That(isSameOriginRequest (Some "null") None, Is.False)

    [<Test>]
    member _.``unparseable Origin is rejected``() =
        Assert.That(isSameOriginRequest (Some "not a url") None, Is.False)

    // ── Referer is consulted only when Origin is absent ──
    [<Test>]
    member _.``loopback Referer with no Origin is allowed``() =
        Assert.That(isSameOriginRequest None (Some "http://localhost:5000/dashboard"), Is.True)

    [<Test>]
    member _.``cross-origin Referer with no Origin is rejected``() =
        Assert.That(isSameOriginRequest None (Some "http://evil.com/attack"), Is.False)

    [<Test>]
    member _.``Origin is authoritative over Referer - loopback Origin wins``() =
        // A loopback Origin passes even if the Referer looks cross-origin.
        Assert.That(isSameOriginRequest (Some "http://localhost:5000") (Some "http://evil.com"), Is.True)

    [<Test>]
    member _.``Origin is authoritative over Referer - cross-origin Origin still rejected``() =
        // A cross-origin Origin is rejected even if the Referer is loopback (Origin cannot be spoofed away).
        Assert.That(isSameOriginRequest (Some "http://evil.com") (Some "http://localhost:5000"), Is.False)


[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type IsRequestAllowedTests() =

    // ── Safe methods bypass the cross-origin check entirely ──
    [<Test>]
    member _.``GET is allowed even cross-origin``() =
        Assert.That(isRequestAllowed "GET" (Some "http://evil.com") None, Is.True)

    [<Test>]
    member _.``HEAD is allowed even cross-origin``() =
        Assert.That(isRequestAllowed "HEAD" (Some "http://evil.com") None, Is.True)

    [<Test>]
    member _.``OPTIONS is allowed even cross-origin``() =
        Assert.That(isRequestAllowed "OPTIONS" (Some "http://evil.com") None, Is.True)

    // ── State-changing methods enforce same-origin ──
    [<Test>]
    member _.``POST with no Origin is allowed (Cli)``() =
        Assert.That(isRequestAllowed "POST" None None, Is.True)

    [<Test>]
    member _.``POST with loopback Origin is allowed (SPA)``() =
        Assert.That(isRequestAllowed "POST" (Some "http://localhost:5000") None, Is.True)

    [<Test>]
    member _.``POST with cross-origin Origin is rejected``() =
        Assert.That(isRequestAllowed "POST" (Some "http://evil.com") None, Is.False)

    [<Test>]
    member _.``DELETE with cross-origin Origin is rejected``() =
        Assert.That(isRequestAllowed "DELETE" (Some "http://evil.com") None, Is.False)
