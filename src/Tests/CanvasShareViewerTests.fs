module Tests.CanvasShareViewerTests

open System
open System.Collections.Generic
open System.Globalization
open System.IO
open System.Net
open System.Net.Http
open System.Net.Http.Headers
open System.Text
open System.Threading
open System.Threading.Tasks
open System.Xml.Linq
open CanvasShareViewer
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Routing
open Microsoft.Extensions.Configuration
open NUnit.Framework
open Tests.TestUtils

let private validPrefix = "0123456789ABCDEFGHIJKL"

let private shellContentSecurityPolicy =
    "default-src 'none'; style-src 'unsafe-inline'; frame-src 'self'; form-action 'none'; base-uri 'none'"

let private contentContentSecurityPolicy =
    "default-src 'none'; script-src 'unsafe-inline' 'unsafe-eval'; style-src 'unsafe-inline'; img-src data:; font-src data:; media-src data:; connect-src 'none'; form-action 'none'; frame-src 'none'; object-src 'none'; base-uri 'none'; frame-ancestors 'self'; sandbox allow-scripts"

let private formatExpiry (value: DateTimeOffset) =
    value.ToString("o", CultureInfo.InvariantCulture)

let private document
    (content: string)
    (metadata: Map<string, string>)
    =
    { Content =
        content
        |> Encoding.UTF8.GetBytes
        |> ReadOnlyMemory<byte>
      Metadata = metadata }

type private FakeBlobReader =
    { Reader: BlobReader
      Requests: unit -> string list }

let private fakeBlobReader documents =
    // Mutation is confined to this fake's request observer; production storage state is immutable.
    let mutable requestsRev = []

    { Reader =
        { ReadExact =
            fun blobName _ ->
                requestsRev <- blobName :: requestsRev
                documents
                |> Map.tryFind blobName
                |> Task.FromResult }
      Requests = fun () -> List.rev requestsRev }

let private withViewer
    documents
    now
    (action: FakeBlobReader -> HttpClient -> string -> unit)
    =
    let fake = fakeBlobReader documents
    let port = getFreeTcpPort ()
    let builder =
        WebApplication.CreateEmptyBuilder(
            WebApplicationOptions()
        )
    builder.WebHost.UseKestrel(fun options ->
        options.Listen(IPAddress.Loopback, port))
    |> ignore

    use app =
        ViewerApplication.create
            builder
            fake.Reader
            (fun () -> now)

    app.StartAsync(CancellationToken.None)
        .GetAwaiter()
        .GetResult()

    try
        use client = new HttpClient()
        action fake client $"http://127.0.0.1:{port}"
    finally
        app.StopAsync(CancellationToken.None)
            .GetAwaiter()
            .GetResult()

let private await (work: Task<'value>) =
    work.GetAwaiter().GetResult()

let private headerPairs (headers: HttpHeaders) =
    headers
    |> Seq.filter (fun header ->
        not (
            String.Equals(
                header.Key,
                "Date",
                StringComparison.OrdinalIgnoreCase
            )
        ))
    |> Seq.map (fun header ->
        header.Key,
        (header.Value |> Seq.sort |> List.ofSeq))

let private responseHeadersWithoutDate
    (response: HttpResponseMessage)
    =
    response.Headers
    |> headerPairs
    |> Seq.sortBy fst
    |> List.ofSeq

let private expectedPolicyHeaders contentSecurityPolicy =
    [
        "Cache-Control", [ "no-store" ]
        "Content-Security-Policy",
        [ contentSecurityPolicy ]
        "Referrer-Policy", [ "no-referrer" ]
        "X-Content-Type-Options", [ "nosniff" ]
    ]

let private parseHtmlDom (html: string) =
    html.Replace(
        "<!doctype html>",
        "",
        StringComparison.OrdinalIgnoreCase
    )
    |> XDocument.Parse

let private requiredAttribute
    name
    (element: XElement)
    =
    match element.Attribute(XName.Get(name)) |> Option.ofObj with
    | Some attribute -> attribute.Value
    | None ->
        Assert.Fail(
            $"Expected <{element.Name.LocalName}> to have a {name} attribute."
        )
        ""

let private selfContainedFixtureBytes () =
    Path.Combine(
        __SOURCE_DIRECTORY__,
        "fixtures",
        "canvas-share-viewer",
        "self-contained.html"
    )
    |> File.ReadAllBytes

type private ResponseSnapshot =
    { StatusCode: HttpStatusCode
      Headers: (string * string list) list
      Body: byte array }

let private responseSnapshot
    (client: HttpClient)
    (url: string)
    : ResponseSnapshot
    =
    use response: HttpResponseMessage =
        client.GetAsync(url) |> await

    let headers =
        Seq.append
            (headerPairs response.Headers)
            (headerPairs response.Content.Headers)
        |> Seq.sortBy fst
        |> List.ofSeq

    { StatusCode = response.StatusCode
      Headers = headers
      Body = response.Content.ReadAsByteArrayAsync() |> await }

let private configuration values =
    values
    |> Seq.map (fun (key, value) ->
        KeyValuePair<string, string>(key, value))
    |> ConfigurationBuilder()
        .AddInMemoryCollection
    |> _.Build()

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type SharePathValidationTests() =

    [<Test>]
    member _.``valid segments compose the exact blob name``() =
        let path =
            SharePath.tryCreate
                validPrefix
                "build-status.html"

        Assert.That(path |> Option.isSome, Is.True)

        Assert.That(
            path |> Option.map SharePath.blobName,
            Is.EqualTo(
                Some
                    $"{validPrefix}/build-status.html"
            )
        )

    [<Test>]
    member _.``prefix must be exactly 22 characters``() =
        Assert.That(
            SharePath.tryCreate
                "0123456789ABCDEFGHIJK"
                "report.html"
            |> Option.isNone,
            Is.True
        )

        Assert.That(
            SharePath.tryCreate
                "0123456789ABCDEFGHIJKLM"
                "report.html"
            |> Option.isNone,
            Is.True
        )

    [<Test>]
    member _.``prefix accepts only ASCII base62``() =
        Assert.That(
            SharePath.tryCreate
                "0123456789ABCDEFGHIJK_"
                "report.html"
            |> Option.isNone,
            Is.True
        )

        Assert.That(
            SharePath.tryCreate
                "0123456789ABCDEFGHIJKé"
                "report.html"
            |> Option.isNone,
            Is.True
        )

    [<TestCase("../report.html")>]
    [<TestCase("folder/report.html")>]
    [<TestCase(@"folder\report.html")>]
    [<TestCase("report..html")>]
    member _.``filename rejects traversal``(filename: string) =
        Assert.That(
            SharePath.tryCreate validPrefix filename
            |> Option.isNone,
            Is.True
        )

    [<Test>]
    member _.``filename must end with lowercase html``() =
        Assert.That(
            SharePath.tryCreate validPrefix "report.txt"
            |> Option.isNone,
            Is.True
        )

        Assert.That(
            SharePath.tryCreate validPrefix "report.HTML"
            |> Option.isNone,
            Is.True
        )

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type ShareExpiryTests() =

    let expiresOn =
        DateTimeOffset(
            2030,
            1,
            1,
            0,
            0,
            0,
            TimeSpan.Zero
        )

    let metadata =
        Map [
            ShareExpiry.MetadataKey,
            formatExpiry expiresOn
        ]

    [<Test>]
    member _.``share is live only before its expiry``() =
        Assert.Multiple(fun () ->
            Assert.That(
                ShareExpiry.isLive
                    (expiresOn.AddTicks(-1L))
                    metadata,
                Is.True,
                "before expiry"
            )

            Assert.That(
                ShareExpiry.isLive expiresOn metadata,
                Is.False,
                "at expiry"
            )

            Assert.That(
                ShareExpiry.isLive
                    (expiresOn.AddTicks(1L))
                    metadata,
                Is.False,
                "after expiry"
            ))

    [<Test>]
    member _.``missing expiry metadata is malformed``() =
        Assert.That(
            ShareExpiry.isLive
                (expiresOn.AddDays(-1.0))
                Map.empty,
            Is.False
        )

    [<Test>]
    member _.``unparseable expiry metadata is malformed``() =
        Assert.That(
            ShareExpiry.isLive
                (expiresOn.AddDays(-1.0))
                (Map [
                    ShareExpiry.MetadataKey, "tomorrow"
                 ]),
            Is.False
        )

    [<Test>]
    member _.``expiry must be canonical round-trip UTC``() =
        let nonUtc =
            expiresOn
                .ToOffset(TimeSpan.FromHours(2.0))
                .ToString("o", CultureInfo.InvariantCulture)

        let nonCanonical =
            expiresOn.ToString(
                "yyyy-MM-dd'T'HH:mm:ssK",
                CultureInfo.InvariantCulture
            )

        Assert.Multiple(fun () ->
            Assert.That(
                ShareExpiry.isLive
                    (expiresOn.AddDays(-1.0))
                    (Map [
                        ShareExpiry.MetadataKey, nonUtc
                     ]),
                Is.False
            )

            Assert.That(
                ShareExpiry.isLive
                    (expiresOn.AddDays(-1.0))
                    (Map [
                        ShareExpiry.MetadataKey,
                        nonCanonical
                     ]),
                Is.False
            ))

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type ViewerConfigurationTests() =

    [<Test>]
    member _.``storage account and share container bind from viewer configuration``() =
        let values =
            [
                "CanvasShareViewer:StorageAccountName",
                "storageacct"
                "CanvasShareViewer:ShareContainer",
                "canvas-shares"
            ]

        match values |> configuration |> ViewerConfiguration.read with
        | Ok loaded ->
            Assert.Multiple(fun () ->
                Assert.That(
                    loaded.StorageAccountName,
                    Is.EqualTo("storageacct")
                )

                Assert.That(
                    loaded.ShareContainer,
                    Is.EqualTo("canvas-shares")
                ))
        | Error error ->
            Assert.Fail(error)

    [<TestCase("CanvasShareViewer:StorageAccountName")>]
    [<TestCase("CanvasShareViewer:ShareContainer")>]
    member _.``blank required viewer configuration is rejected``(blankKey: string) =
        let values =
            [
                "CanvasShareViewer:StorageAccountName",
                "storageacct"
                "CanvasShareViewer:ShareContainer",
                "canvas-shares"
            ]
            |> List.map (fun (key, value) ->
                key,
                (if key = blankKey then " " else value))

        Assert.That(
            values
            |> configuration
            |> ViewerConfiguration.read
            |> Result.isError,
            Is.True
        )

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
[<NonParallelizable>]
type ViewerRouteTests() =

    let now =
        DateTimeOffset(
            2030,
            1,
            1,
            0,
            0,
            0,
            TimeSpan.Zero
        )

    let liveMetadata =
        Map [
            ShareExpiry.MetadataKey,
            now.AddHours(1.0) |> formatExpiry
        ]

    [<Test>]
    member _.``viewer maps exactly the two GET routes``() =
        let fake = fakeBlobReader Map.empty
        let builder =
            WebApplication.CreateEmptyBuilder(
                WebApplicationOptions()
            )

        builder.WebHost.UseKestrel() |> ignore

        use app =
            ViewerApplication.create
                builder
                fake.Reader
                (fun () -> now)

        let routes =
            (app :> IEndpointRouteBuilder).DataSources
            |> Seq.collect _.Endpoints
            |> Seq.map (fun endpoint ->
                let route = endpoint :?> RouteEndpoint

                let methods =
                    endpoint.Metadata
                        .GetMetadata<HttpMethodMetadata>()
                        .HttpMethods
                    |> List.ofSeq

                route.RoutePattern.RawText, methods)
            |> List.ofSeq

        Assert.That(
            routes,
            Is.EquivalentTo(
                [
                    ViewerApplication.ShellRoute,
                    [ "GET" ]
                    ViewerApplication.ContentRoute,
                    [ "GET" ]
                ]
            )
        )

    [<Test>]
    member _.``shell routes only to sandboxed content and both routes re-read the blob``() =
        let filename = "report & \"notes\".html"
        let encodedFilename = Uri.EscapeDataString(filename)
        let blobName = $"{validPrefix}/{filename}"
        let secretMarker = "document-body-secret-marker"

        let documents =
            Map [
                blobName,
                document
                    $"<html><body>{secretMarker}</body></html>"
                    liveMetadata
            ]

        withViewer documents now (fun fake client baseUrl ->
            use shell =
                client.GetAsync(
                    $"{baseUrl}/c/{validPrefix}/{encodedFilename}"
                )
                |> await

            let shellBody =
                shell.Content.ReadAsStringAsync() |> await

            let shellDom = parseHtmlDom shellBody

            let iframe =
                shellDom.Descendants(XName.Get("iframe"))
                |> Seq.exactlyOne

            let sandboxTokens =
                requiredAttribute "sandbox" iframe
                |> _.Split(
                        ' ',
                        StringSplitOptions.RemoveEmptyEntries
                    )
                |> Set.ofArray

            use content =
                client.GetAsync(
                    $"{baseUrl}/c/{validPrefix}/{encodedFilename}/content"
                )
                |> await

            let contentBody =
                content.Content.ReadAsStringAsync() |> await

            Assert.Multiple(fun () ->
                Assert.That(
                    shell.StatusCode,
                    Is.EqualTo(HttpStatusCode.OK)
                )

                Assert.That(
                    shellBody,
                    Does.Not.Contain(secretMarker),
                    "the shell must not carry document content"
                )

                Assert.That(
                    shellDom.Descendants(XName.Get("script")),
                    Is.Empty,
                    "the shell must expose no script API"
                )

                Assert.That(
                    requiredAttribute "src" iframe,
                    Is.EqualTo(
                        $"/c/{validPrefix}/{encodedFilename}/content"
                    )
                )

                Assert.That(
                    sandboxTokens,
                    Is.EqualTo(Set.singleton "allow-scripts"),
                    "the iframe must omit same-origin, forms, popups, downloads, and top-navigation"
                )

                Assert.That(
                    iframe.Attribute(XName.Get("srcdoc"))
                    |> Option.ofObj
                    |> Option.isNone,
                    Is.True,
                    "the document must be loaded only from the content route"
                )

                Assert.That(
                    content.StatusCode,
                    Is.EqualTo(HttpStatusCode.OK)
                )

                Assert.That(
                    contentBody,
                    Does.Contain(secretMarker)
                )

                Assert.That(
                    fake.Requests(),
                    Is.EqualTo([ blobName; blobName ]),
                    "each route must perform its own exact read"
                )))

    [<Test>]
    member _.``shell and content emit their exact response policies``() =
        let blobName = $"{validPrefix}/report.html"

        let documents =
            Map [
                blobName,
                document
                    "<html><body>content</body></html>"
                    liveMetadata
            ]

        withViewer documents now (fun _ client baseUrl ->
            use shell =
                client.GetAsync(
                    $"{baseUrl}/c/{validPrefix}/report.html"
                )
                |> await

            use content =
                client.GetAsync(
                    $"{baseUrl}/c/{validPrefix}/report.html/content"
                )
                |> await

            Assert.Multiple(fun () ->
                Assert.That(
                    responseHeadersWithoutDate shell,
                    Is.EqualTo(
                        expectedPolicyHeaders
                            shellContentSecurityPolicy
                    )
                )

                Assert.That(
                    responseHeadersWithoutDate content,
                    Is.EqualTo(
                        expectedPolicyHeaders
                            contentContentSecurityPolicy
                    )
                )

                Assert.That(
                    shell.Content.Headers.ContentType
                    |> Option.ofObj
                    |> Option.map string,
                    Is.EqualTo(
                        Some "text/html; charset=utf-8"
                    )
                )

                Assert.That(
                    content.Content.Headers.ContentType
                    |> Option.ofObj
                    |> Option.map string,
                    Is.EqualTo(
                        Some "text/html; charset=utf-8"
                    )
                )))

    [<Test>]
    member _.``content preserves a self-contained active document``() =
        let blobName = $"{validPrefix}/self-contained.html"
        let fixture = selfContainedFixtureBytes ()

        let documents =
            Map [
                blobName,
                { Content = ReadOnlyMemory<byte>(fixture)
                  Metadata = liveMetadata }
            ]

        withViewer documents now (fun _ client baseUrl ->
            use response =
                client.GetAsync(
                    $"{baseUrl}/c/{validPrefix}/self-contained.html/content"
                )
                |> await

            let actual =
                response.Content.ReadAsByteArrayAsync()
                |> await

            let dom =
                actual
                |> Encoding.UTF8.GetString
                |> parseHtmlDom

            let style =
                dom.Descendants(XName.Get("style"))
                |> Seq.exactlyOne

            let script =
                dom.Descendants(XName.Get("script"))
                |> Seq.exactlyOne

            let image =
                dom.Descendants(XName.Get("img"))
                |> Seq.exactlyOne

            Assert.Multiple(fun () ->
                Assert.That(
                    response.StatusCode,
                    Is.EqualTo(HttpStatusCode.OK)
                )

                Assert.That(
                    actual,
                    Is.EqualTo(fixture),
                    "the exported HTML must be streamed unchanged"
                )

                Assert.That(
                    style.Value,
                    Does.Contain("#execution-status")
                )

                Assert.That(
                    script.Value,
                    Does.Contain("window.eval(")
                )

                Assert.That(
                    script.Value,
                    Does.Contain("new Function(")
                )

                Assert.That(
                    requiredAttribute "src" image,
                    Does.StartWith("data:image/")
                )))

    [<Test>]
    member _.``all not-found outcomes are indistinguishable on both routes``() =
        let expiredName = $"{validPrefix}/expired.html"
        let missingExpiryName =
            $"{validPrefix}/missing-expiry.html"
        let badExpiryName =
            $"{validPrefix}/bad-expiry.html"

        let documents =
            Map [
                ShareLookup.InvalidPathProbeBlobName,
                document "reserved probe" liveMetadata
                expiredName,
                document
                    "expired"
                    (Map [
                        ShareExpiry.MetadataKey,
                        now |> formatExpiry
                     ])
                missingExpiryName,
                document "missing expiry" Map.empty
                badExpiryName,
                document
                    "bad expiry"
                    (Map [
                        ShareExpiry.MetadataKey,
                        "not-a-timestamp"
                     ])
            ]

        let cases =
            [
                "short/report.html",
                ShareLookup.InvalidPathProbeBlobName
                $"{validPrefix}/report..html",
                ShareLookup.InvalidPathProbeBlobName
                $"{validPrefix}/missing.html",
                $"{validPrefix}/missing.html"
                $"{validPrefix}/expired.html",
                expiredName
                $"{validPrefix}/missing-expiry.html",
                missingExpiryName
                $"{validPrefix}/bad-expiry.html",
                badExpiryName
            ]

        withViewer documents now (fun fake client baseUrl ->
            let snapshots =
                cases
                |> List.map (fun (path, _) ->
                    responseSnapshot
                        client
                        $"{baseUrl}/c/{path}",
                    responseSnapshot
                        client
                        $"{baseUrl}/c/{path}/content")

            let expectedShell, expectedContent =
                snapshots |> List.head

            Assert.Multiple(fun () ->
                snapshots
                |> List.iter (fun (shell, content) ->
                    Assert.That(
                        shell,
                        Is.EqualTo(expectedShell)
                    )

                    Assert.That(
                        content,
                        Is.EqualTo(expectedContent)
                    )

                    Assert.That(
                        shell.StatusCode,
                        Is.EqualTo(HttpStatusCode.NotFound)
                    )

                    Assert.That(
                        content.StatusCode,
                        Is.EqualTo(HttpStatusCode.NotFound)
                    )

                    Assert.That(
                        shell.Body,
                        Is.Empty
                    )

                    Assert.That(
                        content.Body,
                        Is.Empty
                    ))

                Assert.That(
                    fake.Requests(),
                    Is.EqualTo(
                        cases
                        |> List.collect (fun (_, blobName) ->
                            [ blobName; blobName ])
                    ),
                    "malformed, missing, and expired requests must follow the same exact-read ordering on each route"
                )))

    [<Test>]
    member _.``viewer exposes no write or admin surface``() =
        let blobName = $"{validPrefix}/report.html"

        let documents =
            Map [
                blobName,
                document "content" liveMetadata
            ]

        withViewer documents now (fun _ client baseUrl ->
            let writeRequests =
                [
                    HttpMethod.Post,
                    $"/c/{validPrefix}/report.html"
                    HttpMethod.Put,
                    $"/c/{validPrefix}/report.html/content"
                    HttpMethod.Delete,
                    $"/c/{validPrefix}/report.html"
                    HttpMethod.Post, "/upload"
                    HttpMethod.Delete, "/admin"
                ]

            Assert.Multiple(fun () ->
                writeRequests
                |> List.iter (fun (method, path) ->
                    use request =
                        new HttpRequestMessage(
                            method,
                            $"{baseUrl}{path}"
                        )

                    use response =
                        client.SendAsync(request) |> await

                    Assert.That(
                        int response.StatusCode,
                        Is.GreaterThanOrEqualTo(400),
                        $"{method} {path}"
                    ))))
