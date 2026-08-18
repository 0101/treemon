module Tests.CanvasShareViewerContainmentTests

open System
open System.Collections.Concurrent
open System.IO
open System.Net
open System.Net.Http
open System.Text.Json
open System.Xml.Linq
open Microsoft.Playwright
open Microsoft.Playwright.NUnit
open NUnit.Framework
open Tests.CanvasShareViewerContainmentTestHelpers

let private shellContentSecurityPolicy =
    "default-src 'none'; style-src 'unsafe-inline'; frame-src 'self'; form-action 'none'; base-uri 'none'; frame-ancestors 'none'"

let private contentContentSecurityPolicy =
    "default-src 'none'; script-src 'unsafe-inline' 'unsafe-eval'; style-src 'unsafe-inline'; img-src data:; font-src data:; media-src data:; connect-src 'none'; form-action 'none'; frame-src 'none'; object-src 'none'; base-uri 'none'; frame-ancestors 'self'; sandbox allow-scripts"

let private getIframeContent
    (client: HttpClient)
    (url: string)
    =
    task {
        use request =
            new HttpRequestMessage(HttpMethod.Get, url)

        [
            "Sec-Fetch-Site", "same-origin"
            "Sec-Fetch-Mode", "navigate"
            "Sec-Fetch-Dest", "iframe"
        ]
        |> List.iter (fun (name: string, value: string) ->
            request.Headers.TryAddWithoutValidation(
                name,
                value
            )
            |> ignore)

        return! client.SendAsync(request)
    }

let private singleHeader
    name
    (response: HttpResponseMessage)
    =
    response.Headers.GetValues(name)
    |> Seq.exactlyOne

let private assertPolicy
    expectedContentSecurityPolicy
    (response: HttpResponseMessage)
    =
    Assert.Multiple(fun () ->
        Assert.That(
            singleHeader
                "Content-Security-Policy"
                response,
            Is.EqualTo(expectedContentSecurityPolicy)
        )

        Assert.That(
            singleHeader
                "X-Content-Type-Options"
                response,
            Is.EqualTo("nosniff")
        )

        Assert.That(
            singleHeader "Referrer-Policy" response,
            Is.EqualTo("no-referrer")
        )

        Assert.That(
            singleHeader "Cache-Control" response,
            Is.EqualTo("no-store")
        ))

let private parseShell (html: string) =
    html.Replace(
        "<!doctype html>",
        "",
        StringComparison.OrdinalIgnoreCase
    )
    |> XDocument.Parse

let private artifactDirectory () =
    Environment.GetEnvironmentVariable(
        "CANVAS_VIEWER_VERIFICATION_ARTIFACT_DIR"
    )
    |> Option.ofObj
    |> Option.map _.Trim()
    |> Option.filter (not << String.IsNullOrWhiteSpace)
    |> Option.map (fun path ->
        Directory.CreateDirectory(path) |> ignore
        path)

let private writeArtifact
    (filename: string)
    (content: string)
    =
    artifactDirectory ()
    |> Option.iter (fun directory ->
        File.WriteAllText(
            Path.Combine(directory, filename),
            content
        ))

let private captureScreenshot
    filename
    (page: IPage)
    =
    task {
        match artifactDirectory () with
        | Some directory ->
            let! _ =
                page.ScreenshotAsync(
                    PageScreenshotOptions(
                        Path = Path.Combine(
                            directory,
                            filename
                        ),
                        FullPage = true
                    )
                )
            ()
        | None ->
            ()
    }

let private seedViewerOrigin
    (page: IPage)
    (harness: ContainmentHarness)
    =
    task {
        let url =
            $"{harness.ViewerBaseUrl}/c/{validPrefix}/self-contained.html"

        let! response = page.GotoAsync(url)

        Assert.That(
            response.Status,
            Is.EqualTo(200),
            "the live viewer shell must be available before storage is seeded"
        )

        return!
            page.EvaluateAsync<string>(
                """() => {
                    document.cookie = 'viewer-auth=viewer-cookie-secret; path=/';
                    localStorage.setItem('viewer-auth', 'viewer-storage-secret');
                    return JSON.stringify({
                        cookie: document.cookie,
                        storage: localStorage.getItem('viewer-auth')
                    });
                }"""
            )
    }

let private observePage
    (page: IPage)
    =
    let requests = ConcurrentQueue<string>()
    let responses = ConcurrentQueue<string>()
    let requestFailures = ConcurrentQueue<string>()
    let popups = ConcurrentQueue<string>()
    let consoleMessages = ConcurrentQueue<string>()
    let pageErrors = ConcurrentQueue<string>()

    page.Request.Add(fun request ->
        requests.Enqueue(
            $"{request.Method} {request.Url}"
        ))

    page.Response.Add(fun response ->
        responses.Enqueue(
            $"{response.Status} {response.Url}"
        ))

    page.RequestFailed.Add(fun request ->
        requestFailures.Enqueue(
            $"{request.Method} {request.Url} :: {request.Failure}"
        ))

    page.Popup.Add(fun popup ->
        popups.Enqueue(popup.Url))

    page.Console.Add(fun message ->
        consoleMessages.Enqueue(
            $"{message.Type}: {message.Text}"
        ))

    page.PageError.Add(fun error ->
        pageErrors.Enqueue(string error))

    requests,
    responses,
    requestFailures,
    popups,
    consoleMessages,
    pageErrors

let private escapeRequests
    (harness: ContainmentHarness)
    (requests: string array)
    =
    requests
    |> Array.filter (fun request ->
        request.Contains(
            "/__viewer_escape__/",
            StringComparison.Ordinal
        )
        || request.Contains(
            harness.ProbeBaseUrl,
            StringComparison.Ordinal
        ))

let private assertHostileOutcomes
    (resultJson: string)
    =
    use document = JsonDocument.Parse(resultJson)
    let root = document.RootElement
    let stringProperty (name: string) =
        root.GetProperty(name).GetString()

    Assert.Multiple(fun () ->
        Assert.That(
            root.GetProperty("completed").GetBoolean(),
            Is.True
        )

        Assert.That(
            stringProperty "cookie",
            Does.Not.Contain("viewer-cookie-secret"),
            "the sandboxed document must not read viewer-origin cookies"
        )

        Assert.That(
            stringProperty "localStorage",
            Does.Not.Contain("viewer-storage-secret"),
            "the sandboxed document must not read viewer-origin local storage"
        )

        Assert.That(
            stringProperty "viewerFetch",
            Does.StartWith("blocked:"),
            "viewer-origin fetch must be blocked before a response is obtained"
        )

        Assert.That(
            stringProperty "externalFetch",
            Does.StartWith("blocked:"),
            "external fetch must be blocked before a response is obtained"
        )

        Assert.That(
            stringProperty "image",
            Does.StartWith("blocked"),
            "remote image exfiltration must be blocked"
        )

        Assert.That(
            stringProperty "form",
            Is.EqualTo("attempted")
        )

        Assert.That(
            stringProperty "popup",
            Is.EqualTo("blocked")
        ))

let private assertNoEscape
    (harness: ContainmentHarness)
    expectedPageUrl
    expectedFrameUrl
    (page: IPage)
    (requests: ConcurrentQueue<string>)
    (responses: ConcurrentQueue<string>)
    (popups: ConcurrentQueue<string>)
    =
    let observedRequests = requests.ToArray()
    let attemptedEscapeRequests =
        escapeRequests harness observedRequests
    let escapeResponses =
        responses.ToArray()
        |> escapeRequests harness

    let childFrameUrls =
        page.Frames
        |> Seq.filter (fun frame ->
            not (
                Object.ReferenceEquals(
                    frame,
                    page.MainFrame
                )
            ))
        |> Seq.map _.Url
        |> Array.ofSeq

    Assert.Multiple(fun () ->
        Assert.That(
            escapeResponses,
            Is.Empty,
            "no hostile network attempt may obtain a response"
        )

        Assert.That(
            harness.ProbeRequests.ToArray(),
            Is.Empty,
            "the external probe server must receive no request"
        )

        Assert.That(
            popups.ToArray(),
            Is.Empty,
            "the hostile fixture must not open a page"
        )

        Assert.That(
            page.Context.Pages.Count,
            Is.EqualTo(1),
            "the browser context must still contain only the original page"
        )

        Assert.That(
            page.Url,
            Is.EqualTo(expectedPageUrl),
            "the hostile fixture must not navigate the parent or top-level page"
        )

        Assert.That(
            childFrameUrls,
            Is.EqualTo([| expectedFrameUrl |])
                .Or.EqualTo(
                    [| "chrome-error://chromewebdata/" |]
                ),
            "self-navigation must remain on the document or Chromium's local CSP-block page"
        )

        Assert.That(
            observedRequests,
            Has.None.Contains(":5000"),
            "verification must never touch the production port"
        )

        if attemptedEscapeRequests.Length > 0 then
            TestContext.Out.WriteLine(
                $"Browser-recorded escape attempts (response and probe assertions determine transport):{Environment.NewLine}{String.Join(Environment.NewLine, attemptedEscapeRequests)}"
            ))

type BrowserEvidence =
    { Mode: string
      ViewerUrl: string
      ViewerPort: int
      ProbePort: int
      Seed: string
      Result: string
      Requests: string array
      Responses: string array
      RequestFailures: string array
      ProbeRequests: string array
      Popups: string array
      ConsoleMessages: string array
      PageErrors: string array
      BlobRequests: string array }

let private writeBrowserEvidence
    filename
    evidence
    =
    let json =
        JsonSerializer.Serialize(
            evidence,
            JsonSerializerOptions(WriteIndented = true)
        )

    TestContext.Out.WriteLine(json)
    writeArtifact filename json

[<TestFixture>]
[<Category("E2E")>]
[<Category("ViewerContainment")>]
[<NonParallelizable>]
type CanvasShareViewerContainmentTests() =
    inherit PageTest()

    [<Test>]
    member _.``live viewer routes enforce the exact wire contract``() =
        withContainmentHarness (fun harness ->
            task {
                use client = new HttpClient()

                use! hostileShell =
                    client.GetAsync(
                        $"{harness.ViewerBaseUrl}/c/{validPrefix}/hostile.html"
                    )

                use! hostileContent =
                    getIframeContent
                        client
                        $"{harness.ViewerBaseUrl}/c/{validPrefix}/hostile.html/content"

                use! benignShell =
                    client.GetAsync(
                        $"{harness.ViewerBaseUrl}/c/{validPrefix}/self-contained.html"
                    )

                use! benignContent =
                    getIframeContent
                        client
                        $"{harness.ViewerBaseUrl}/c/{validPrefix}/self-contained.html/content"

                let! shellHtml =
                    hostileShell.Content.ReadAsStringAsync()

                let shellDom = parseShell shellHtml

                let iframe =
                    shellDom.Descendants(
                        XName.Get("iframe")
                    )
                    |> Seq.exactlyOne

                let sandbox =
                    iframe
                        .Attribute(XName.Get("sandbox"))
                        .Value

                [
                    hostileShell
                    hostileContent
                    benignShell
                    benignContent
                ]
                |> List.iter (fun response ->
                    Assert.That(
                        response.StatusCode,
                        Is.EqualTo(HttpStatusCode.OK)
                    ))

                assertPolicy
                    shellContentSecurityPolicy
                    hostileShell
                assertPolicy
                    contentContentSecurityPolicy
                    hostileContent
                assertPolicy
                    shellContentSecurityPolicy
                    benignShell
                assertPolicy
                    contentContentSecurityPolicy
                    benignContent

                Assert.Multiple(fun () ->
                    Assert.That(
                        sandbox,
                        Is.EqualTo("allow-scripts"),
                        "the iframe sandbox token set must be exact"
                    )

                    Assert.That(
                        harness.ViewerPort,
                        Is.Not.EqualTo(5000)
                    )

                    Assert.That(
                        harness.ProbePort,
                        Is.Not.EqualTo(5000)
                    ))

                let evidence =
                    JsonSerializer.Serialize(
                        {| viewerBaseUrl =
                            harness.ViewerBaseUrl
                           viewerPort = harness.ViewerPort
                           probePort = harness.ProbePort
                           statuses =
                            [|
                                int hostileShell.StatusCode
                                int hostileContent.StatusCode
                                int benignShell.StatusCode
                                int benignContent.StatusCode
                            |]
                           iframeSandbox = sandbox
                           shellCsp =
                            singleHeader
                                "Content-Security-Policy"
                                hostileShell
                           contentCsp =
                            singleHeader
                                "Content-Security-Policy"
                                hostileContent
                           xContentTypeOptions =
                            singleHeader
                                "X-Content-Type-Options"
                                hostileContent
                           referrerPolicy =
                            singleHeader
                                "Referrer-Policy"
                                hostileContent
                           cacheControl =
                            singleHeader
                                "Cache-Control"
                                hostileContent
                           blobRequests =
                            harness.BlobRequests.ToArray() |},
                        JsonSerializerOptions(
                            WriteIndented = true
                        )
                    )

                TestContext.Out.WriteLine(evidence)
                writeArtifact "wire-contract.json" evidence
            })

    [<Test>]
    member this.``hostile fixture cannot escape the shell iframe``() =
        withContainmentHarness (fun harness ->
            task {
                let! seed =
                    seedViewerOrigin this.Page harness

                let
                    (requests,
                     responses,
                     requestFailures,
                     popups,
                     consoleMessages,
                     pageErrors) =
                    observePage this.Page

                let url =
                    $"{harness.ViewerBaseUrl}/c/{validPrefix}/hostile.html"

                let! response = this.Page.GotoAsync(url)

                Assert.That(
                    response.Status,
                    Is.EqualTo(200)
                )

                let results =
                    this.Page
                        .FrameLocator("iframe")
                        .Locator("#hostile-results")

                do!
                    Assertions
                        .Expect(results)
                        .ToHaveAttributeAsync(
                            "data-complete",
                            "true",
                            LocatorAssertionsToHaveAttributeOptions(
                                Timeout = 10000.0f
                            )
                        )

                let! resultJson = results.TextContentAsync()
                do! this.Page.WaitForTimeoutAsync(700.0f)

                do!
                    captureScreenshot
                        "hostile-iframe.png"
                        this.Page

                writeBrowserEvidence
                    "hostile-iframe.json"
                    { Mode = "shell iframe"
                      ViewerUrl = this.Page.Url
                      ViewerPort = harness.ViewerPort
                      ProbePort = harness.ProbePort
                      Seed = seed
                      Result = resultJson
                      Requests = requests.ToArray()
                      Responses = responses.ToArray()
                      RequestFailures =
                        requestFailures.ToArray()
                      ProbeRequests =
                        harness.ProbeRequests.ToArray()
                      Popups = popups.ToArray()
                      ConsoleMessages =
                        consoleMessages.ToArray()
                      PageErrors = pageErrors.ToArray()
                      BlobRequests =
                        harness.BlobRequests.ToArray() }

                assertHostileOutcomes resultJson

                assertNoEscape
                    harness
                    url
                    $"{url}/content"
                    this.Page
                    requests
                    responses
                    popups
            })

    [<Test>]
    member this.``direct content navigation falls back to sandboxed shell containment``() =
        withContainmentHarness (fun harness ->
            task {
                let! seed =
                    seedViewerOrigin this.Page harness

                let
                    (requests,
                     responses,
                     requestFailures,
                     popups,
                     consoleMessages,
                     pageErrors) =
                    observePage this.Page

                let url =
                    $"{harness.ViewerBaseUrl}/c/{validPrefix}/hostile.html/content"

                let! response = this.Page.GotoAsync(url)

                Assert.That(
                    response.Status,
                    Is.EqualTo(200)
                )

                let iframe = this.Page.Locator("iframe")
                let! sandbox =
                    iframe.GetAttributeAsync("sandbox")

                let! topLevelHostileResults =
                    this.Page
                        .Locator("#hostile-results")
                        .CountAsync()

                let results =
                    this.Page
                        .FrameLocator("iframe")
                        .Locator("#hostile-results")

                do!
                    Assertions
                        .Expect(results)
                        .ToHaveAttributeAsync(
                            "data-complete",
                            "true",
                            LocatorAssertionsToHaveAttributeOptions(
                                Timeout = 10000.0f
                            )
                        )

                let! resultJson = results.TextContentAsync()
                do! this.Page.WaitForTimeoutAsync(700.0f)

                Assert.Multiple(fun () ->
                    Assert.That(
                        sandbox,
                        Is.EqualTo("allow-scripts"),
                        "a top-level content URL must render the normal sandboxed shell"
                    )

                    Assert.That(
                        topLevelHostileResults,
                        Is.Zero,
                        "the active document must not be emitted into the top-level response"
                    )

                    Assert.That(
                        harness.BlobRequests.ToArray(),
                        Is.EqualTo(
                            [|
                                $"PROPERTIES {validPrefix}/self-contained.html"
                                $"CONTENT {validPrefix}/self-contained.html"
                                $"PROPERTIES {validPrefix}/hostile.html"
                                $"CONTENT {validPrefix}/hostile.html"
                            |]
                        ),
                        "each shell load must perform one properties lookup and one iframe body read"
                    ))

                do!
                    captureScreenshot
                        "hostile-direct.png"
                        this.Page

                writeBrowserEvidence
                    "hostile-direct.json"
                    { Mode =
                        "direct content route shell fallback"
                      ViewerUrl = this.Page.Url
                      ViewerPort = harness.ViewerPort
                      ProbePort = harness.ProbePort
                      Seed = seed
                      Result = resultJson
                      Requests = requests.ToArray()
                      Responses = responses.ToArray()
                      RequestFailures =
                        requestFailures.ToArray()
                      ProbeRequests =
                        harness.ProbeRequests.ToArray()
                      Popups = popups.ToArray()
                      ConsoleMessages =
                        consoleMessages.ToArray()
                      PageErrors = pageErrors.ToArray()
                      BlobRequests =
                        harness.BlobRequests.ToArray() }

                assertHostileOutcomes resultJson

                assertNoEscape
                    harness
                    url
                    url
                    this.Page
                    requests
                    responses
                    popups
            })

    [<Test>]
    member this.``benign self-contained scripting remains interactive``() =
        withContainmentHarness (fun harness ->
            task {
                let requests = ConcurrentQueue<string>()

                this.Page.Request.Add(fun request ->
                    requests.Enqueue(
                        $"{request.Method} {request.Url}"
                    ))

                let url =
                    $"{harness.ViewerBaseUrl}/c/{validPrefix}/self-contained.html"

                let! response = this.Page.GotoAsync(url)

                Assert.That(
                    response.Status,
                    Is.EqualTo(200)
                )

                let frame =
                    this.Page.FrameLocator("iframe")

                let status =
                    frame.Locator("#execution-status")

                do!
                    Assertions
                        .Expect(status)
                        .ToHaveAttributeAsync(
                            "data-inline",
                            "ran"
                        )

                do!
                    Assertions
                        .Expect(status)
                        .ToHaveAttributeAsync(
                            "data-eval",
                            "ran"
                        )

                do!
                    Assertions
                        .Expect(status)
                        .ToHaveAttributeAsync(
                            "data-dynamic-function",
                            "ran"
                        )

                do!
                    Assertions
                        .Expect(status)
                        .ToHaveAttributeAsync(
                            "data-canvas-send",
                            "inert"
                        )

                let! color =
                    status.EvaluateAsync<string>(
                        "element => getComputedStyle(element).color"
                    )

                let image =
                    frame.Locator("#embedded-image")

                let! imageLoaded =
                    image.EvaluateAsync<bool>(
                        "element => element.complete && element.naturalWidth === 1 && element.naturalHeight === 1"
                    )

                let details =
                    frame.Locator("#native-disclosure")

                Assert.That(
                    details.GetAttributeAsync("open")
                        .GetAwaiter()
                        .GetResult(),
                    Is.Null
                )

                do!
                    frame
                        .Locator(
                            "#native-disclosure summary"
                        )
                        .ClickAsync()

                do!
                    Assertions
                        .Expect(details)
                        .ToHaveAttributeAsync("open", "")

                let observedRequests = requests.ToArray()
                let! inlineExecution =
                    status.GetAttributeAsync("data-inline")
                let! evalExecution =
                    status.GetAttributeAsync("data-eval")
                let! newFunctionExecution =
                    status.GetAttributeAsync(
                        "data-dynamic-function"
                    )
                let! canvasSendOutcome =
                    status.GetAttributeAsync(
                        "data-canvas-send"
                    )
                let! disclosureOpen =
                    details.GetAttributeAsync("open")

                Assert.Multiple(fun () ->
                    Assert.That(
                        color,
                        Is.EqualTo("rgb(20, 90, 160)"),
                        "inline style must apply"
                    )

                    Assert.That(
                        imageLoaded,
                        Is.True,
                        "the data image must load"
                    )

                    Assert.That(
                        observedRequests,
                        Has.None.Contains(":5000")
                    )

                    Assert.That(
                        escapeRequests
                            harness
                            observedRequests,
                        Is.Empty
                    ))

                let evidence =
                    JsonSerializer.Serialize(
                        {| viewerUrl = this.Page.Url
                           viewerPort = harness.ViewerPort
                           probePort = harness.ProbePort
                           inlineExecution = inlineExecution
                           evalExecution = evalExecution
                           newFunctionExecution =
                            newFunctionExecution
                           canvasSendOutcome =
                            canvasSendOutcome
                           inlineColor = color
                           dataImageLoaded = imageLoaded
                           disclosureOpen = disclosureOpen
                           requests = observedRequests
                           blobRequests =
                            harness.BlobRequests.ToArray() |},
                        JsonSerializerOptions(
                            WriteIndented = true
                        )
                    )

                TestContext.Out.WriteLine(evidence)
                writeArtifact "benign.json" evidence

                do!
                    captureScreenshot
                        "benign.png"
                        this.Page
            })
