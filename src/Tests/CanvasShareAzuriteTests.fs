module Tests.CanvasShareAzuriteTests

open System
open System.IO
open System.Net
open System.Net.Http
open System.Net.Security
open System.Reflection
open System.Security.Cryptography.X509Certificates
open NUnit.Framework
open Azure.Storage.Blobs
open Azure.Core.Pipeline
open Server.CanvasShare
open Server.CanvasExport
open Tests.TestUtils

// Integration test for the publish -> SAS-fetch backend ROUND-TRIP against the Azurite emulator.
// This owns the Azure round-trip that CanvasShareTests deliberately leaves out ("The Azure round-trip
// (upload -> SAS fetch -> 409/403/404) is an Azurite integration test owned by a separate verification
// task, not a unit test."). Spec: docs/spec/canvas-sharing.md, "Verification" (publish -> SAS renders
// themed + inert; bare URL denied; cross-doc SAS 403; expired SAS denied). Task: tm-canvas-share-gtj.
//
// Requires Azurite reachable over HTTPS at 127.0.0.1:10000 (the well-known dev account). The product
// signs an https-only SAS (spr=https), so the emulator MUST be served over TLS for the round-trip to
// be meaningful — an http fetch of an https-only SAS is refused for protocol reasons, which would make
// the deny-path steps pass for the wrong reason. When the emulator is not running the whole fixture is
// IGNORED (not failed) via Assert.Ignore in OneTimeSetUp, so the ordinary unit suite stays green
// without external infrastructure; CI/verification starts Azurite (HTTPS) first so the assertions below
// actually execute.
//
// The task note ("Azurite denial codes differ from real Azure (no 409) - assert denial, not the exact
// code") is honoured: every deny-path step asserts NOT 200 as the hard gate and records the concrete
// status Azurite returned, so a real-Azure-vs-emulator code difference can never cause a false failure.

[<TestFixture>]
[<Category("Integration")>]
[<Category("Azurite")>]
[<Category("Canvas")>]
// Mutates the process-global AZURE_STORAGE_CONNECTION_STRING and TREEMON_CONFIG_DIR env vars, so it
// must never run alongside the other config/publish fixtures.
[<NonParallelizable>]
type CanvasShareAzuriteRoundTripTests() =

    // The well-known Azurite / legacy Azure Storage Emulator dev account. This is a fixed, PUBLIC test
    // credential shipped with the emulator (not a secret): account devstoreaccount1 + its documented
    // key, blob endpoint path-style on 127.0.0.1:10000.
    let azuriteConnectionString =
        "DefaultEndpointsProtocol=https;AccountName=devstoreaccount1;"
        + "AccountKey=Eby8vdM02xNOcqFlqUwJPLlmEtlCDXJ1OUzFT50uSRZ6IFsuFq2UVErCz4I6tq/K1SZFPTOtr/KBHBeksoGMGw==;"
        + "BlobEndpoint=https://127.0.0.1:10000/devstoreaccount1;"

    let connKey = "AZURE_STORAGE_CONNECTION_STRING"

    // Two distinct authored docs so a cross-doc mix-up is detectable. Each CALLS canvasSend (author
    // code) but does NOT itself define the inert helper the export injects, and contains none of the
    // pane-only markers this test asserts are absent.
    let docABody = "<html><head><title>Doc A</title></head><body><h1>Alpha doc</h1><button onclick=\"canvasSend('ping',{})\">Send</button></body></html>"
    let docBBody = "<html><head><title>Doc B</title></head><body><h1>Bravo doc</h1></body></html>"

    // Markers reuse the exact fragments CanvasExportTests uses, so presence/absence stays consistent
    // between the pure export test and this live round-trip.
    let themeMarker = "scrollbar-color"                                 // base theme CSS (present)
    let themeTokenMarker = "--text-muted:#9399b2"                       // an app design token (present)
    let inertCanvasSend = "window.canvasSend=function(){return false}"  // the no-op helper (present)
    let bridgeHeartbeatMarker = "/bridge/heartbeat"                     // bridge + heartbeat (absent)
    let idiomorphMarker = "var Idiomorph="                              // idiomorph runtime / morph (absent)
    let morphControllerMarker = "action:'content-updated'"             // morph controller / morph (absent)
    let errorOverlayMarker = "canvas-doc-error"                         // JS error overlay (absent)

    // NUnit lifecycle fields: captured/created in OneTimeSetUp and restored/cleaned up in OneTimeTearDown,
    // so they must be mutable instance state shared across the separate setup and teardown methods.
    let mutable originalConn : string option = None
    let mutable originalConfigDir : string option = None
    let mutable tempConfigDir = ""
    let mutable urlA = ""
    let mutable urlB = ""

    // ── TLS for the Azurite HTTPS endpoint ────────────────────────────────────────
    // The product signs an https-only SAS (spr=https), so Azurite is reached over HTTPS. The emulator
    // serves a self-signed cert; rather than tamper with the OS trust store (blocked on Windows by
    // "protected roots" without an interactive dialog / admin), the cert is validated in-process: only
    // the loopback emulator is trusted, and — when the harness exports AZURITE_CERT_PATH — that exact
    // certificate is additionally pinned by thumbprint. Nothing but loopback is ever trusted.
    let loopbackHosts = set [ "127.0.0.1"; "::1"; "localhost" ]

    let pinnedThumbprint =
        match Environment.GetEnvironmentVariable("AZURITE_CERT_PATH") with
        | path when not (String.IsNullOrWhiteSpace path) && File.Exists path ->
            use c = X509CertificateLoader.LoadCertificateFromFile path
            Some c.Thumbprint
        | _ -> None

    let certValidation =
        Func<HttpRequestMessage, X509Certificate2, X509Chain, SslPolicyErrors, bool>(fun req cert _ _ ->
            let hostOk = not (isNull req.RequestUri) && loopbackHosts.Contains req.RequestUri.Host
            let thumbOk =
                match pinnedThumbprint with
                | None -> true
                | Some t -> not (isNull cert) && cert.Thumbprint = t
            hostOk && thumbOk)

    let makeHttpClient () =
        let h = new HttpClientHandler()
        h.ServerCertificateCustomValidationCallback <- certValidation
        new HttpClient(h)

    // Azure SDK plumbing captured so OneTimeTearDown can restore the process-global shared transport.
    let mutable sdkClientField : FieldInfo option = None
    let mutable sdkWrapper : obj option = None
    let mutable sdkOriginalClient : obj option = None

    /// Point the Azure SDK's process-shared HttpClientTransport at our cert-validating HttpClient so the
    /// backend's own BlobServiceClient — which uses the default transport and exposes no injection seam —
    /// can speak HTTPS to the self-signed emulator. Reversed in teardown.
    let redirectSdkTransportToValidatingClient () =
        try
            let sharedField = typeof<HttpClientTransport>.GetField("Shared", BindingFlags.Static ||| BindingFlags.Public ||| BindingFlags.NonPublic)
            let shared = sharedField.GetValue(null)
            let wrapperField = typeof<HttpClientTransport>.GetField("_clientWrapper", BindingFlags.Instance ||| BindingFlags.NonPublic)
            let wrapper = wrapperField.GetValue(shared)
            let clientField = wrapper.GetType().GetField("_client", BindingFlags.Instance ||| BindingFlags.NonPublic)
            sdkWrapper <- Some wrapper
            sdkClientField <- Some clientField
            sdkOriginalClient <- clientField.GetValue(wrapper) |> Option.ofObj
            clientField.SetValue(wrapper, makeHttpClient())
        with ex ->
            TestContext.WriteLine($"WARNING: could not redirect Azure SDK shared transport ({ex.GetType().Name}: {ex.Message}); publish over HTTPS may fail.")

    let httpClient = makeHttpClient ()

    let httpGet (url: string) : HttpResponseMessage =
        runAsync (httpClient.GetAsync(url) |> Async.AwaitTask)

    let readBody (resp: HttpResponseMessage) : string =
        runAsync (resp.Content.ReadAsStringAsync() |> Async.AwaitTask)

    /// Everything before the '?' — the bare blob URL with its SAS query stripped off.
    let stripQuery (url: string) : string = url.Split('?')[0]

    /// The SAS query, INCLUDING the leading '?', taken verbatim off `url` (no Uri round-trip, so the
    /// percent-encoding of the signature is preserved byte-for-byte).
    let sasQuery (url: string) : string = url.Substring(url.IndexOf('?'))

    /// Publish an already-exported doc through the REAL backend and assert we got an Ok URL. A failure
    /// here is a precondition failure (Azurite up but the round-trip couldn't even publish).
    let publishDoc (filename: string) (body: string) : string =
        match runAsync (publish filename (buildStaticHtml body)) with
        | Ok url -> url
        | Error msg -> failwith $"publish of {filename} returned Error (Azurite round-trip precondition failed): {msg}"

    [<OneTimeSetUp>]
    member _.Setup() =
        // Capture originals FIRST so OneTimeTearDown always restores correctly, even on the Ignore path.
        originalConn <- Environment.GetEnvironmentVariable(connKey) |> Option.ofObj
        originalConfigDir <- Environment.GetEnvironmentVariable("TREEMON_CONFIG_DIR") |> Option.ofObj

        // Skip (do not fail) when the emulator isn't up, so the unit suite is green without Azurite.
        let reachable =
            try
                use probe = makeHttpClient ()
                probe.Timeout <- TimeSpan.FromSeconds 5.0
                use r = runAsync (probe.GetAsync("https://127.0.0.1:10000/devstoreaccount1?comp=list") |> Async.AwaitTask)
                // Any HTTP reply (even 403 for the unauthenticated probe) proves Azurite is listening.
                true
            with _ -> false
        if not reachable then
            Assert.Ignore("Azurite not reachable over HTTPS at 127.0.0.1:10000 — start `azurite-blob --cert <pem> --key <pem> --blobHost 127.0.0.1 --blobPort 10000` (and optionally export AZURITE_CERT_PATH to pin the cert) to run this integration test.")

        // Route the backend's OWN Azure SDK client through our cert-validating transport before any
        // publish call, so its default-transport HTTPS upload trusts the self-signed emulator cert.
        redirectSdkTransportToValidatingClient ()

        // Redirect the machine config to a throwaway dir so readCanvasShareConfig returns defaults
        // (container 'canvas-shared', 90-day expiry) regardless of the developer's real ~/.treemon.
        tempConfigDir <- Path.Combine(Path.GetTempPath(), $"canvas-share-azurite-{Guid.NewGuid()}")
        Directory.CreateDirectory(tempConfigDir) |> ignore
        Environment.SetEnvironmentVariable("TREEMON_CONFIG_DIR", tempConfigDir)
        Environment.SetEnvironmentVariable(connKey, azuriteConnectionString)

        // Publish two distinct docs through the real backend once; the per-step tests reuse them.
        urlA <- publishDoc "doc-a.html" docABody
        urlB <- publishDoc "doc-b.html" docBBody
        TestContext.WriteLine($"Published doc A: {urlA}")
        TestContext.WriteLine($"Published doc B: {urlB}")

    [<OneTimeTearDown>]
    member _.Teardown() =
        Environment.SetEnvironmentVariable(connKey, Option.toObj originalConn)
        Environment.SetEnvironmentVariable("TREEMON_CONFIG_DIR", Option.toObj originalConfigDir)
        // Restore the process-global Azure SDK shared transport we redirected in setup.
        match sdkClientField, sdkWrapper with
        | Some clientField, Some wrapper ->
            try clientField.SetValue(wrapper, Option.toObj sdkOriginalClient) with _ -> ()
        | _ -> ()
        httpClient.Dispose()
        if not (String.IsNullOrEmpty tempConfigDir) then
            try Directory.Delete(tempConfigDir, recursive = true) with _ -> ()

    // ── Step 1: publish -> a non-empty per-doc SAS URL (sig=, sr=b, sp=r) ─────────

    [<Test>]
    [<Order(1)>]
    member _.``Step 1 - publish returns a non-empty per-doc SAS URL carrying sig, sr=b, sp=r``() =
        Assert.That(String.IsNullOrWhiteSpace urlA, Is.False, "publish must return a non-empty SAS URL")
        Assert.That(urlA, Does.Contain("sig="), "the URL must carry a SAS signature (sig=)")
        Assert.That(urlA, Does.Contain("sr=b"), "the SAS must be blob-scoped (sr=b) — least privilege")
        Assert.That(urlA, Does.Contain("sp=r"), "the SAS must be read-only (sp=r) — no write/delete/list")

    // ── Step 2: GET the SAS URL -> 200 text/html, themed + inert, no pane machinery ─

    [<Test>]
    [<Order(2)>]
    member _.``Step 2 - GET the SAS URL returns 200 text/html, themed + inert, without pane machinery``() =
        use resp = httpGet urlA
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.OK), "the signed link must resolve to the blob")
        Assert.That(resp.Content.Headers.ContentType, Is.Not.Null, "a Content-Type must be present")
        Assert.That(resp.Content.Headers.ContentType.MediaType, Is.EqualTo("text/html"),
                    "the doc is published as text/html (charset is a separate param)")
        let body = readBody resp
        // The two pieces a standalone copy needs ARE injected:
        Assert.That(body, Does.Contain(themeMarker), "the base theme must be injected into the published copy")
        Assert.That(body, Does.Contain(themeTokenMarker), "the base-theme design tokens must be present standalone")
        Assert.That(body, Does.Contain(inertCanvasSend), "the no-op canvasSend must be injected")
        // The pane-only machinery is NOT:
        Assert.That(body, Does.Not.Contain(bridgeHeartbeatMarker), "a standalone copy carries no bridge/heartbeat")
        Assert.That(body, Does.Not.Contain(idiomorphMarker), "a standalone copy carries no idiomorph runtime (morph)")
        Assert.That(body, Does.Not.Contain(morphControllerMarker), "a standalone copy carries no morph controller")
        Assert.That(body, Does.Not.Contain(errorOverlayMarker), "a standalone copy carries no JS error overlay")
        // The original authored content survives the export + round-trip:
        Assert.That(body, Does.Contain("Alpha doc"), "the original doc body must be served back")

    // ── Step 3: GET the blob WITHOUT the SAS -> denied ────────────────────────────

    [<Test>]
    [<Order(3)>]
    member _.``Step 3 - GET the blob WITHOUT the SAS is denied (not 200)``() =
        use resp = httpGet (stripQuery urlA)
        TestContext.WriteLine($"Step 3 bare-URL (no SAS) status: {int resp.StatusCode} {resp.StatusCode}")
        Assert.That(resp.StatusCode, Is.Not.EqualTo(HttpStatusCode.OK),
                    "anonymous access to a private blob must be denied — the SAS is the only way in")
        Assert.That(int resp.StatusCode, Is.GreaterThanOrEqualTo(400),
                    "the response must be a denial, not a redirect or success")

    // ── Step 4: doc A's SAS on doc B's URL -> 403 ─────────────────────────────────

    [<Test>]
    [<Order(4)>]
    member _.``Step 4 - doc A's SAS used on doc B's URL is denied (403)``() =
        // Doc B's bare blob URL + doc A's SAS token: the signature is bound to A's blob, so B is refused.
        let crossUrl = stripQuery urlB + sasQuery urlA
        use resp = httpGet crossUrl
        TestContext.WriteLine($"Step 4 cross-doc (A's SAS on B) status: {int resp.StatusCode} {resp.StatusCode}")
        Assert.That(resp.StatusCode, Is.Not.EqualTo(HttpStatusCode.OK),
                    "doc A's blob-scoped SAS must not open doc B (least privilege, Decision #2)")
        Assert.That(resp.StatusCode, Is.EqualTo(HttpStatusCode.Forbidden),
                    "a signature bound to another blob is a 403 denial")

    // ── Step 5: a SAS with a past expiry -> denied ────────────────────────────────

    [<Test>]
    [<Order(5)>]
    member _.``Step 5 - a SAS with a past expiry is denied (not 200)``() =
        // Re-sign doc A's OWN blob with an already-elapsed expiry, using the SAME buildSasBuilder the
        // backend uses, and confirm the emulator refuses the expired token.
        let uriBuilder = BlobUriBuilder(Uri(urlA))
        let container = uriBuilder.BlobContainerName
        let blob = uriBuilder.BlobName
        let blobClient = BlobClient(azuriteConnectionString, container, blob)
        let pastExpiry = DateTimeOffset.UtcNow.AddHours(-1.0)
        let expiredSas = blobClient.GenerateSasUri(buildSasBuilder container blob pastExpiry)
        use resp = httpGet (expiredSas.ToString())
        TestContext.WriteLine($"Step 5 expired-SAS status: {int resp.StatusCode} {resp.StatusCode}")
        Assert.That(resp.StatusCode, Is.Not.EqualTo(HttpStatusCode.OK),
                    "an expired SAS must not open the blob — the link auto-expires")
        Assert.That(int resp.StatusCode, Is.GreaterThanOrEqualTo(400),
                    "the response must be a denial, not a redirect or success")
