module Tests.CanvasShareViewerContainmentTestHelpers

open System
open System.Collections.Concurrent
open System.Globalization
open System.IO
open System.Net
open System.Text
open System.Threading
open System.Threading.Tasks
open CanvasShareViewer
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Http
open Tests.TestUtils

let validPrefix = "0123456789ABCDEFGHIJKL"

let private fixedNow =
    DateTimeOffset(
        2030,
        1,
        1,
        0,
        0,
        0,
        TimeSpan.Zero
    )

let private liveMetadata =
    Map [
        ShareExpiry.MetadataKey,
        fixedNow
            .AddHours(1.0)
            .ToString("o", CultureInfo.InvariantCulture)
    ]

let private fixturePath (filename: string) =
    Path.Combine(
        __SOURCE_DIRECTORY__,
        "fixtures",
        "canvas-share-viewer",
        filename
    )

let private exportedFixture
    (filename: string)
    (replacements: (string * string) list)
    : string
    =
    replacements
    |> List.fold
        (fun (content: string) (placeholder, value) ->
            content.Replace(
                placeholder,
                value,
                StringComparison.Ordinal
            ))
        (File.ReadAllText(fixturePath filename))
    |> Server.CanvasExport.buildStaticHtml

type private StoredBlobDocument =
    { Content: byte array
      Metadata: Map<string, string> }

let private blobDocument (content: string) : StoredBlobDocument =
    { Content =
        content
        |> Encoding.UTF8.GetBytes
      Metadata = liveMetadata }

let private openBlobDocument
    (stored: StoredBlobDocument)
    : BlobDocument
    =
    { Content =
        new MemoryStream(stored.Content, false)
        :> Stream
      ContentLength = int64 stored.Content.LongLength
      Metadata = stored.Metadata }

let private isolatedPorts () =
    let rec reserve () =
        let ports = getFreeTcpPorts 2

        if ports |> List.contains 5000 then
            reserve ()
        else
            ports

    match reserve () with
    | [ viewerPort; probePort ] ->
        viewerPort, probePort
    | _ ->
        failwith "Expected exactly two isolated ports."

type ContainmentHarness =
    { ViewerBaseUrl: string
      ProbeBaseUrl: string
      ViewerPort: int
      ProbePort: int
      BlobRequests: ConcurrentQueue<string>
      ProbeRequests: ConcurrentQueue<string> }

let private startProbe
    port
    (requests: ConcurrentQueue<string>)
    =
    let builder =
        WebApplication.CreateEmptyBuilder(
            WebApplicationOptions()
        )

    builder.WebHost.UseKestrel(fun options ->
        options.Listen(IPAddress.Loopback, port))
    |> ignore

    let app = builder.Build()

    let gif =
        Convert.FromBase64String(
            "R0lGODlhAQABAIAAAAAAAP///ywAAAAAAQABAAACAUwAOw=="
        )

    app.Run(
        RequestDelegate(fun context ->
            task {
                requests.Enqueue(
                    $"{context.Request.Method} http://127.0.0.1:{port}{context.Request.Path}{context.Request.QueryString}"
                )

                context.Response.Headers["Access-Control-Allow-Origin"] <-
                    "*"

                if
                    context.Request.Path.Value.Contains(
                        "image",
                        StringComparison.OrdinalIgnoreCase
                    )
                then
                    context.Response.ContentType <- "image/gif"
                    do!
                        context.Response.Body.WriteAsync(
                            gif,
                            context.RequestAborted
                        )
                else
                    context.Response.ContentType <- "text/plain"
                    do! context.Response.WriteAsync("probe-response")
            })
    )

    app

let withContainmentHarness
    (action: ContainmentHarness -> Task)
    =
    task {
        let viewerPort, probePort = isolatedPorts ()
        let viewerBaseUrl =
            $"http://127.0.0.1:{viewerPort}"
        let probeBaseUrl =
            $"http://127.0.0.1:{probePort}"
        let probeRequests =
            ConcurrentQueue<string>()
        use probe =
            startProbe probePort probeRequests
        do! probe.StartAsync(CancellationToken.None)

        let hostile =
            exportedFixture
                "hostile.html"
                [
                    "{{PROBE_BASE_URL}}", probeBaseUrl
                ]

        let benign =
            exportedFixture "self-contained.html" []

        let documents =
            Map [
                $"{validPrefix}/hostile.html",
                blobDocument hostile
                $"{validPrefix}/self-contained.html",
                blobDocument benign
            ]

        let blobRequests =
            ConcurrentQueue<string>()

        let reader =
            { ReadPropertiesExact =
                fun blobName _ ->
                    blobRequests.Enqueue(
                        $"PROPERTIES {blobName}"
                    )

                    documents
                    |> Map.tryFind blobName
                    |> Option.map _.Metadata
                    |> Task.FromResult
              ReadExact =
                fun blobName _ ->
                    blobRequests.Enqueue(
                        $"CONTENT {blobName}"
                    )

                    documents
                    |> Map.tryFind blobName
                    |> Option.map openBlobDocument
                    |> Task.FromResult }

        let builder =
            WebApplication.CreateEmptyBuilder(
                WebApplicationOptions()
            )

        builder.WebHost.UseKestrel(fun options ->
            options.Listen(
                IPAddress.Loopback,
                viewerPort
            ))
        |> ignore

        use viewer =
            ViewerApplication.create
                builder
                reader
                (fun () -> fixedNow)

        do! viewer.StartAsync(CancellationToken.None)

        try
            do!
                action
                    { ViewerBaseUrl = viewerBaseUrl
                      ProbeBaseUrl = probeBaseUrl
                      ViewerPort = viewerPort
                      ProbePort = probePort
                      BlobRequests = blobRequests
                      ProbeRequests = probeRequests }
        finally
            viewer
                .StopAsync(CancellationToken.None)
                .GetAwaiter()
                .GetResult()

            probe
                .StopAsync(CancellationToken.None)
                .GetAwaiter()
                .GetResult()
    }
