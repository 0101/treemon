namespace TerminalHost

open System
open System.Net
open System.Net.Http
open System.Net.WebSockets
open System.Text
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Primitives

type private SocketReceive =
    | Frame of byte array
    | PeerClosed
    | MessageTooLarge
    | ReceiveFailed

[<RequireQualifiedAccess>]
module internal TerminalProxy =
    [<Literal>]
    let private AttachmentPathRoot = "/_treemon/"

    [<Literal>]
    let private TtySubprotocol = "tty"

    [<Literal>]
    let private HiddenViewportScrollbarStyle =
        "<style>.xterm-viewport{scrollbar-width:none}.xterm-viewport::-webkit-scrollbar{display:none}</style>"

    let private proxyShutdownTimeout = TimeSpan.FromSeconds 5.0

    let internal hideViewportScrollbar (html: string) =
        html.Replace("</head>", HiddenViewportScrollbarStyle + "</head>", StringComparison.OrdinalIgnoreCase)

    let private receiveMessage maximumBytes (socket: WebSocket) =
        async {
            let buffer = Array.zeroCreate<byte> 8_192

            let rec receive chunks total messageType =
                async {
                    try
                        let! result =
                            socket.ReceiveAsync(
                                ArraySegment<byte>(buffer),
                                CancellationToken.None
                            )
                            |> Async.AwaitTask

                        if result.MessageType = WebSocketMessageType.Close then
                            return PeerClosed
                        elif
                            messageType
                            |> Option.exists ((<>) result.MessageType)
                        then
                            return ReceiveFailed
                        elif total + result.Count > maximumBytes then
                            return MessageTooLarge
                        else
                            let chunk = buffer.AsSpan(0, result.Count).ToArray()
                            let updated = chunk :: chunks

                            if result.EndOfMessage then
                                return updated |> List.rev |> Array.concat |> Frame
                            else
                                return!
                                    receive
                                        updated
                                        (total + result.Count)
                                        (Some result.MessageType)
                    with _ ->
                        return ReceiveFailed
                }

            return! receive [] 0 None
        }

    let private startUpstreamPump plane (upstream: WebSocket) =
        let rec pump () =
            async {
                match! receiveMessage Protocol.MaximumReplayBytes upstream with
                | Frame frame ->
                    do! plane.AcceptUpstreamFrame frame
                    return! pump ()
                | PeerClosed
                | MessageTooLarge
                | ReceiveFailed ->
                    do! plane.UpstreamEnded()
            }

        Async.Start(pump ())

    let private runBrowser plane attachmentId (socket: WebSocket) =
        let rec receive () =
            async {
                match!
                    receiveMessage Protocol.MaximumAttachmentMessageBytes socket
                with
                | Frame frame ->
                    match! plane.AcceptBrowserFrame attachmentId frame with
                    | Ok() -> return! receive ()
                    | Error _ ->
                        do! TerminalDataPlane.closeSocket WebSocketCloseStatus.PolicyViolation "Invalid terminal protocol frame" socket
                | MessageTooLarge ->
                    do! TerminalDataPlane.closeSocket WebSocketCloseStatus.MessageTooBig "Terminal frame too large" socket
                | PeerClosed
                | ReceiveFailed -> ()
            }

        async {
            do! receive ()
            do! plane.DetachSocket attachmentId
        }

    let private authorization attachmentPathPrefix (context: HttpContext) =
        let headers = context.Request.Headers.Authorization |> Seq.toList
        let path =
            context.Request.Path.Value
            |> Option.ofObj
            |> Option.defaultValue "/"

        let pathHeaders, targetPath =
            if not (path.StartsWith(attachmentPathPrefix, StringComparison.Ordinal)) then
                [], path
            else
                let afterPrefix = path.Substring(attachmentPathPrefix.Length)
                let separator = afterPrefix.IndexOf('/')

                if separator < 0 then
                    [ $"Bearer {afterPrefix}" ], "/"
                else
                    let token = afterPrefix.Substring(0, separator)
                    [ $"Bearer {token}" ], afterPrefix.Substring(separator)

        if List.isEmpty headers then pathHeaders, targetPath else headers, targetPath

    let private reject rejection (context: HttpContext) =
        if rejection = RequestRejection.Unauthorized then
            context.Response.Headers.WWWAuthenticate <- "Bearer"

        context.Response.StatusCode <- RequestSecurity.statusCode rejection

    let private copyRequestHeaders (context: HttpContext) (request: HttpRequestMessage) =
        [ "Accept"; "Accept-Language"; "If-Modified-Since"; "If-None-Match"; "Range" ]
        |> List.iter (fun name ->
            let values = context.Request.Headers[name] |> Seq.toArray

            if values.Length > 0 then
                request.Headers.TryAddWithoutValidation(name, values) |> ignore)

    let private hopByHopHeaders =
        set
            [ "connection"; "cache-control"; "content-security-policy"; "keep-alive"
              "pragma"; "proxy-authenticate"; "proxy-authorization"
              "referrer-policy"; "server"; "set-cookie"; "te"; "trailer"
              "transfer-encoding"; "upgrade" ]

    let private copyResponseHeaders
        (response: HttpResponseMessage)
        (context: HttpContext)
        =
        Seq.append response.Headers response.Content.Headers
        |> Seq.filter (fun pair ->
            hopByHopHeaders
            |> Set.contains (pair.Key.ToLowerInvariant())
            |> not)
        |> Seq.iter (fun pair ->
            context.Response.Headers[pair.Key] <-
                pair.Value |> Seq.toArray |> StringValues)

    let private isTerminalPage targetPath (response: HttpResponseMessage) =
        targetPath = "/"
        && response.StatusCode = HttpStatusCode.OK
        && String.Equals(
            response.Content.Headers.ContentType
            |> Option.ofObj
            |> Option.bind (_.MediaType >> Option.ofObj)
            |> Option.defaultValue "",
            "text/html",
            StringComparison.OrdinalIgnoreCase)
        && Seq.isEmpty response.Content.Headers.ContentEncoding

    let private protectAttachmentResponse allowedOrigins (context: HttpContext) =
        let frameAncestors =
            match allowedOrigins with
            | [] -> "frame-ancestors 'none'"
            | origins -> "frame-ancestors " + String.concat " " origins

        context.Response.Headers["Content-Security-Policy"] <- StringValues frameAncestors
        context.Response.Headers["Referrer-Policy"] <- "no-referrer"
        context.Response.Headers.CacheControl <- "no-store"
        context.Response.Headers.Pragma <- "no-cache"

    let private proxyHttp
        ttydPort
        targetPath
        (client: HttpClient)
        (context: HttpContext)
        =
        task {
            match context.Request.Method with
            | "GET"
            | "HEAD" ->
                let target = Uri($"http://127.0.0.1:{ttydPort}{targetPath}{context.Request.QueryString}")

                use request = new HttpRequestMessage(HttpMethod(context.Request.Method), target)
                copyRequestHeaders context request

                try
                    use! response =
                        client.SendAsync(
                            request,
                            HttpCompletionOption.ResponseHeadersRead,
                            context.RequestAborted
                        )

                    context.Response.StatusCode <- int response.StatusCode
                    copyResponseHeaders response context

                    if
                        context.Request.Method = "GET"
                        && isTerminalPage targetPath response
                    then
                        [ "Accept-Ranges"; "Content-Encoding"; "Content-Length"; "Content-MD5"
                          "Content-Range"; "ETag" ]
                        |> List.iter (context.Response.Headers.Remove >> ignore)

                        let! html = response.Content.ReadAsStringAsync(context.RequestAborted)

                        let bytes =
                            html
                            |> hideViewportScrollbar
                            |> Encoding.UTF8.GetBytes

                        context.Response.ContentLength <- int64 bytes.Length
                        do! context.Response.Body.WriteAsync(bytes, context.RequestAborted)
                    elif context.Request.Method <> "HEAD" then
                        do!
                            response.Content.CopyToAsync(context.Response.Body, context.RequestAborted)
                with
                | :? OperationCanceledException when context.RequestAborted.IsCancellationRequested ->
                    ()
                | _ when not context.Response.HasStarted ->
                    context.Response.StatusCode <- StatusCodes.Status502BadGateway
            | _ ->
                context.Response.StatusCode <- StatusCodes.Status405MethodNotAllowed
                context.Response.Headers.Allow <- "GET, HEAD"
        }

    let private handleAttachment
        allowedOrigins
        bearerToken
        attachmentPathPrefix
        ttydPort
        client
        plane
        (context: HttpContext)
        =
        task {
            protectAttachmentResponse allowedOrigins context

            let authorizationHeaders, targetPath =
                authorization attachmentPathPrefix context

            match
                RequestSecurity.validate
                    allowedOrigins bearerToken
                    (RequestSecurity.metadata authorizationHeaders context)
            with
            | Error rejection -> reject rejection context
            | Ok() ->
                if targetPath = "/ws" then
                    let supportsTty =
                        context.WebSockets.WebSocketRequestedProtocols
                        |> Seq.exists (fun protocol ->
                            String.Equals(protocol, TtySubprotocol, StringComparison.Ordinal))

                    if not context.WebSockets.IsWebSocketRequest || not supportsTty then
                        context.Response.StatusCode <- StatusCodes.Status400BadRequest
                    else
                        use! socket =
                            context.WebSockets.AcceptWebSocketAsync(TtySubprotocol)

                        match! plane.AttachSocket socket |> Async.StartAsTask with
                        | None -> context.Abort()
                        | Some attachmentId ->
                            do! runBrowser plane attachmentId socket |> Async.StartAsTask
                else
                    return! proxyHttp ttydPort targetPath client context
        }

    let private ignoreTaskFailure (operation: unit -> Task) =
        task {
            try
                do! operation ()
            with _ ->
                ()
        }

    let private stopProxy plane (application: WebApplication) (client: HttpClient) =
        task {
            do! plane.Stop() |> Async.StartAsTask
            use cancellation = new CancellationTokenSource(proxyShutdownTimeout)

            do!
                ignoreTaskFailure (fun () ->
                    application.StopAsync(cancellation.Token))

            do!
                ignoreTaskFailure (fun () ->
                    application.DisposeAsync().AsTask().WaitAsync(proxyShutdownTimeout))

            client.Dispose()
        }

    let private startProxy
        allowedOrigins
        bearerToken
        sessionId
        ttydPort
        plane
        =
        task {
            let handler = new SocketsHttpHandler(UseProxy = false, AllowAutoRedirect = false)
            let client = new HttpClient(handler, true)
            let attachmentPathPrefix = $"{AttachmentPathRoot}{sessionId}/"

            let buildPipeline (application: WebApplication) =
                application.UseWebSockets() |> ignore

                RequestDelegate(fun context ->
                    handleAttachment allowedOrigins bearerToken attachmentPathPrefix ttydPort client plane context
                    :> Task)

            try
                let! application, boundPort = LoopbackHost.start 0 buildPipeline

                let endpoint =
                    $"http://127.0.0.1:{boundPort}{attachmentPathPrefix}{Uri.EscapeDataString bearerToken}/"

                return Ok(application, client, endpoint)
            with _ ->
                client.Dispose()
                return Error "Could not start the terminal attachment endpoint"
        }

    let private openUpstream ttydPort =
        async {
            let socket = new ClientWebSocket()
            socket.Options.AddSubProtocol TtySubprotocol

            socket.Options.SetRequestHeader("Origin", $"http://127.0.0.1:{ttydPort}")

            use cancellation =
                new CancellationTokenSource(TimeSpan.FromSeconds 5.0)

            try
                do! socket.ConnectAsync(Uri($"ws://127.0.0.1:{ttydPort}/ws"), cancellation.Token) |> Async.AwaitTask

                return Ok(socket :> WebSocket)
            with _ ->
                socket.Abort()
                socket.Dispose()
                return Error "Could not connect to the ttyd WebSocket"
        }

    let internal startWithConnector
        connector
        allowedOrigins
        bearerToken
        sessionId
        ttydPort
        onUpstreamEnded
        =
        async {
            match! connector ttydPort with
            | Error error -> return Error error
            | Ok upstream ->
                let! initialized =
                    TerminalProtocol.defaultSize
                    |> TerminalProtocol.initialHandshake
                    |> TerminalDataPlane.sendFrame upstream

                if not initialized then
                    do!
                        TerminalDataPlane.closeSocket
                            WebSocketCloseStatus.EndpointUnavailable
                            "Terminal startup failed"
                            upstream

                    return Error "Could not initialize the ttyd WebSocket"
                else
                    let core =
                        TerminalDataPlane.createCore
                            Protocol.MaximumReplayBytes
                            upstream
                            onUpstreamEnded

                    startUpstreamPump core upstream

                    match!
                        startProxy allowedOrigins bearerToken sessionId ttydPort core
                        |> Async.AwaitTask
                    with
                    | Error error ->
                        do! core.Stop()
                        return Error error
                    | Ok(application, client, endpoint) ->
                        let stopWorkflow = lazy (stopProxy core application client)

                        return
                            Ok
                                { core with
                                    AttachmentEndpoint = endpoint
                                    Stop =
                                        fun () ->
                                            stopWorkflow.Value
                                            |> Async.AwaitTask }
        }

    let start
        allowedOrigins
        bearerToken
        sessionId
        ttydPort
        onUpstreamEnded
        =
        startWithConnector openUpstream allowedOrigins bearerToken sessionId ttydPort onUpstreamEnded
