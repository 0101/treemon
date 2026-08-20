namespace TerminalHost

open System
open System.Net
open System.Net.Http
open System.Net.WebSockets
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Hosting
open Microsoft.AspNetCore.Hosting.Server
open Microsoft.AspNetCore.Hosting.Server.Features
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open Microsoft.Extensions.Hosting
open Microsoft.Extensions.Logging
open Microsoft.Extensions.Primitives

type private BrowserAttachment =
    { Id: Guid
      Socket: WebSocket
      Initialized: bool
      Paused: bool
      NextSequence: int64 }

type private DataPlaneState =
    { Replay: ReplayBuffer
      Attachment: BrowserAttachment option
      TerminalSize: TerminalSize
      TitleFrame: byte array option
      PreferencesFrame: byte array option
      Stopped: bool }

type private DataPlaneMessage =
    | Attach of WebSocket * AsyncReplyChannel<Guid option>
    | BrowserFrame of
        Guid *
        byte array *
        AsyncReplyChannel<Result<unit, string>>
    | Detach of Guid * AsyncReplyChannel<unit>
    | UpstreamFrame of byte array * AsyncReplyChannel<unit>
    | UpstreamClosed of AsyncReplyChannel<unit>
    | Stop of AsyncReplyChannel<unit>

type private SocketReceive =
    | Frame of byte array
    | PeerClosed
    | MessageTooLarge
    | ReceiveFailed

type TerminalDataPlane =
    internal
        { AttachmentEndpoint: string
          AttachSocket: WebSocket -> Async<Guid option>
          AcceptBrowserFrame: Guid -> byte array -> Async<Result<unit, string>>
          DetachSocket: Guid -> Async<unit>
          AcceptUpstreamFrame: byte array -> Async<unit>
          UpstreamEnded: unit -> Async<unit>
          Stop: unit -> Async<unit> }

[<RequireQualifiedAccess>]
module TerminalDataPlane =
    [<Literal>]
    let private AttachmentPathRoot = "/_treemon/"

    [<Literal>]
    let private TtySubprotocol = "tty"

    let private socketOperationTimeout = TimeSpan.FromSeconds 2.0
    let private proxyShutdownTimeout = TimeSpan.FromSeconds 5.0

    let private socketIsOpen (socket: WebSocket) =
        try
            socket.State = WebSocketState.Open
        with _ ->
            false

    let private sendFrame (socket: WebSocket) (data: byte array) =
        async {
            if not (socketIsOpen socket) then
                return false
            else
                use cancellation =
                    new CancellationTokenSource(socketOperationTimeout)

                try
                    do!
                        socket.SendAsync(
                            ArraySegment<byte>(data),
                            WebSocketMessageType.Binary,
                            true,
                            cancellation.Token
                        )
                        |> Async.AwaitTask

                    return true
                with _ ->
                    return false
        }

    let private sendFrames socket frames =
        let rec send remaining =
            async {
                match remaining with
                | [] -> return true
                | frame :: tail ->
                    match! sendFrame socket frame with
                    | false -> return false
                    | true -> return! send tail
            }

        send frames

    let private closeSocket status reason (socket: WebSocket) =
        async {
            use cancellation =
                new CancellationTokenSource(socketOperationTimeout)

            try
                match socket.State with
                | WebSocketState.Open
                | WebSocketState.CloseReceived ->
                    do!
                        socket.CloseOutputAsync(status, reason, cancellation.Token)
                        |> Async.AwaitTask
                | WebSocketState.None
                | WebSocketState.Connecting
                | WebSocketState.CloseSent
                | WebSocketState.Closed
                | WebSocketState.Aborted -> ()
                | _ -> ()
            with _ ->
                try
                    socket.Abort()
                with _ ->
                    ()

            try
                socket.Dispose()
            with _ ->
                ()
        }

    let private initialBrowserFrames state =
        let controlFrames =
            [ state.TitleFrame; state.PreferencesFrame ]
            |> List.choose id

        let replayFrames =
            state.Replay
            |> ReplayBuffer.frames
            |> List.map _.Data

        controlFrames @ replayFrames

    let private initializeAttachment upstream state attachment frame =
        async {
            match TerminalProtocol.parseHandshakeSize frame with
            | Error error -> return state, Error error
            | Ok terminalSize ->
                let! replaySent =
                    initialBrowserFrames state
                    |> sendFrames attachment.Socket

                if not replaySent then
                    return
                        { state with Attachment = None },
                        Error "Browser attachment closed during replay"
                else
                    let! resized =
                        TerminalProtocol.resizeFrame terminalSize
                        |> sendFrame upstream

                    if not resized then
                        return state, Error "ttyd WebSocket is not open"
                    else
                        let initialized =
                            { attachment with
                                Initialized = true
                                NextSequence = ReplayBuffer.nextSequence state.Replay }

                        return
                            { state with
                                Attachment = Some initialized
                                TerminalSize = terminalSize },
                            Ok()
        }

    let private resumeAttachment upstream state attachment =
        async {
            let frames =
                state.Replay
                |> ReplayBuffer.framesFrom attachment.NextSequence
                |> List.map _.Data

            let! replaySent = sendFrames attachment.Socket frames

            if not replaySent then
                return
                    { state with Attachment = None },
                    Error "Browser attachment closed during replay"
            else
                let! resized =
                    state.TerminalSize
                    |> TerminalProtocol.resizeFrame
                    |> sendFrame upstream

                if not resized then
                    return state, Error "ttyd WebSocket is not open"
                else
                    let resumed =
                        { attachment with
                            Paused = false
                            NextSequence = ReplayBuffer.nextSequence state.Replay }

                    return { state with Attachment = Some resumed }, Ok()
        }

    let private handleInitializedBrowserFrame upstream state attachment (frame: byte array) =
        async {
            if frame.Length = 0 then
                return state, Error "Terminal browser frame is empty"
            else
                match char frame[0] with
                | '0' ->
                    match! sendFrame upstream frame with
                    | true -> return state, Ok()
                    | false -> return state, Error "ttyd WebSocket is not open"
                | '1' ->
                    match TerminalProtocol.parseResizeFrame frame with
                    | Error error -> return state, Error error
                    | Ok terminalSize ->
                        match!
                            terminalSize
                            |> TerminalProtocol.resizeFrame
                            |> sendFrame upstream
                        with
                        | false -> return state, Error "ttyd WebSocket is not open"
                        | true ->
                            return { state with TerminalSize = terminalSize }, Ok()
                | '2' ->
                    let paused = { attachment with Paused = true }
                    return { state with Attachment = Some paused }, Ok()
                | '3' -> return! resumeAttachment upstream state attachment
                | _ -> return state, Error "Unknown ttyd browser command"
        }

    let private handleBrowserFrame upstream state attachment frame =
        if attachment.Initialized then
            handleInitializedBrowserFrame upstream state attachment frame
        else
            initializeAttachment upstream state attachment frame

    let private updatedControlFrame state (frame: byte array) =
        if frame.Length = 0 then
            state
        else
            match char frame[0] with
            | '1' -> { state with TitleFrame = Some(Array.copy frame) }
            | '2' -> { state with PreferencesFrame = Some(Array.copy frame) }
            | _ -> state

    let private sendLiveFrame state sequence (frame: byte array) =
        async {
            match state.Attachment with
            | Some attachment when attachment.Initialized && not attachment.Paused ->
                match! sendFrame attachment.Socket frame with
                | false ->
                    do!
                        closeSocket
                            WebSocketCloseStatus.EndpointUnavailable
                            "Terminal attachment closed"
                            attachment.Socket

                    return { state with Attachment = None }
                | true ->
                    let delivered =
                        match sequence with
                        | Some nextSequence ->
                            { attachment with NextSequence = nextSequence }
                        | None -> attachment

                    return { state with Attachment = Some delivered }
            | Some _
            | None -> return state
        }

    let private handleUpstreamFrame replayCapacity state (frame: byte array) =
        async {
            if frame.Length = 0 then
                return state
            elif frame[0] = byte '0' then
                let replay =
                    state.Replay
                    |> ReplayBuffer.append replayCapacity frame

                let updated = { state with Replay = replay }

                return!
                    sendLiveFrame
                        updated
                        (Some(ReplayBuffer.nextSequence replay))
                        frame
            else
                return!
                    sendLiveFrame
                        (updatedControlFrame state frame)
                        None
                        frame
        }

    let private notifyUpstreamEnded onUpstreamEnded =
        try
            onUpstreamEnded ()
        with _ ->
            ()

    let internal createCore replayCapacity upstream onUpstreamEnded =
        let mailbox =
            MailboxProcessor.Start(fun inbox ->
                let rec loop state =
                    async {
                        let! message = inbox.Receive()

                        match message with
                        | Attach(socket, reply) when state.Stopped ->
                            do!
                                closeSocket
                                    WebSocketCloseStatus.EndpointUnavailable
                                    "Terminal session closed"
                                    socket

                            reply.Reply None
                            return! loop state
                        | Attach(socket, reply) ->
                            match state.Attachment with
                            | Some previous ->
                                do!
                                    closeSocket
                                        WebSocketCloseStatus.NormalClosure
                                        "Replaced by a new attachment"
                                        previous.Socket
                            | None -> ()

                            let attachment =
                                { Id = Guid.NewGuid()
                                  Socket = socket
                                  Initialized = false
                                  Paused = false
                                  NextSequence = ReplayBuffer.nextSequence state.Replay }

                            reply.Reply(Some attachment.Id)

                            return!
                                loop
                                    { state with
                                        Attachment = Some attachment }
                        | BrowserFrame(attachmentId, frame, reply) ->
                            match state.Attachment with
                            | Some attachment when attachment.Id = attachmentId ->
                                let! updated, result =
                                    handleBrowserFrame upstream state attachment frame

                                reply.Reply result
                                return! loop updated
                            | Some _
                            | None ->
                                reply.Reply(Error "Browser attachment was replaced")
                                return! loop state
                        | Detach(attachmentId, reply) ->
                            let updated =
                                match state.Attachment with
                                | Some attachment when attachment.Id = attachmentId ->
                                    { state with Attachment = None }
                                | Some _
                                | None -> state

                            reply.Reply()
                            return! loop updated
                        | UpstreamFrame(frame, reply) when state.Stopped ->
                            reply.Reply()
                            return! loop state
                        | UpstreamFrame(frame, reply) ->
                            let! updated =
                                handleUpstreamFrame replayCapacity state frame

                            reply.Reply()
                            return! loop updated
                        | UpstreamClosed reply when state.Stopped ->
                            reply.Reply()
                            return! loop state
                        | UpstreamClosed reply ->
                            match state.Attachment with
                            | Some attachment ->
                                do!
                                    closeSocket
                                        WebSocketCloseStatus.EndpointUnavailable
                                        "Terminal session interrupted"
                                        attachment.Socket
                            | None -> ()

                            do!
                                closeSocket
                                    WebSocketCloseStatus.NormalClosure
                                    "Terminal upstream closed"
                                    upstream

                            notifyUpstreamEnded onUpstreamEnded
                            reply.Reply()

                            return!
                                loop
                                    { state with
                                        Attachment = None
                                        Stopped = true }
                        | Stop reply when state.Stopped ->
                            reply.Reply()
                            return! loop state
                        | Stop reply ->
                            match state.Attachment with
                            | Some attachment ->
                                do!
                                    closeSocket
                                        WebSocketCloseStatus.NormalClosure
                                        "Terminal session closed"
                                        attachment.Socket
                            | None -> ()

                            do!
                                closeSocket
                                    WebSocketCloseStatus.NormalClosure
                                    "Terminal session closed"
                                    upstream

                            reply.Reply()

                            return!
                                loop
                                    { state with
                                        Attachment = None
                                        Stopped = true }
                    }

                loop
                    { Replay = ReplayBuffer.empty
                      Attachment = None
                      TerminalSize = TerminalProtocol.defaultSize
                      TitleFrame = None
                      PreferencesFrame = None
                      Stopped = false })

        { AttachmentEndpoint = ""
          AttachSocket = fun socket -> mailbox.PostAndAsyncReply(fun reply -> Attach(socket, reply))
          AcceptBrowserFrame =
            fun attachmentId frame ->
                mailbox.PostAndAsyncReply(fun reply ->
                    BrowserFrame(attachmentId, Array.copy frame, reply))
          DetachSocket =
            fun attachmentId ->
                mailbox.PostAndAsyncReply(fun reply -> Detach(attachmentId, reply))
          AcceptUpstreamFrame =
            fun frame ->
                mailbox.PostAndAsyncReply(fun reply ->
                    UpstreamFrame(Array.copy frame, reply))
          UpstreamEnded =
            fun () -> mailbox.PostAndAsyncReply UpstreamClosed
          Stop = fun () -> mailbox.PostAndAsyncReply Stop }

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
                            let chunk =
                                if result.Count = 0 then
                                    Array.empty
                                else
                                    buffer[0 .. result.Count - 1]

                            let updated = chunk :: chunks

                            if result.EndOfMessage then
                                return
                                    updated
                                    |> List.rev
                                    |> Array.concat
                                    |> Frame
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
            task {
                match!
                    receiveMessage Protocol.MaximumAttachmentMessageBytes socket
                    |> Async.StartAsTask
                with
                | Frame frame ->
                    match!
                        plane.AcceptBrowserFrame attachmentId frame
                        |> Async.StartAsTask
                    with
                    | Ok() -> return! receive ()
                    | Error _ ->
                        do!
                            closeSocket
                                WebSocketCloseStatus.PolicyViolation
                                "Invalid terminal protocol frame"
                                socket
                            |> Async.StartAsTask
                | MessageTooLarge ->
                    do!
                        closeSocket
                            WebSocketCloseStatus.MessageTooBig
                            "Terminal frame too large"
                            socket
                        |> Async.StartAsTask
                | PeerClosed
                | ReceiveFailed -> ()
            }

        task {
            do! receive ()
            do! plane.DetachSocket attachmentId |> Async.StartAsTask
        }

    let private pathAuthorization attachmentPathPrefix (context: HttpContext) =
        let path =
            context.Request.Path.Value
            |> Option.ofObj
            |> Option.defaultValue "/"

        if not (path.StartsWith(attachmentPathPrefix, StringComparison.Ordinal)) then
            [], path
        else
            let afterPrefix = path.Substring(attachmentPathPrefix.Length)
            let separator = afterPrefix.IndexOf('/')

            if separator < 0 then
                [ $"Bearer {afterPrefix}" ], "/"
            else
                let token = afterPrefix.Substring(0, separator)
                let targetPath = afterPrefix.Substring(separator)
                [ $"Bearer {token}" ], targetPath

    let private authorization attachmentPathPrefix (context: HttpContext) =
        let headerValues =
            context.Request.Headers.Authorization
            |> Seq.toList

        let pathValues, targetPath =
            pathAuthorization attachmentPathPrefix context

        if not (List.isEmpty headerValues) then
            headerValues, targetPath
        else
            pathValues, targetPath

    let private reject rejection (context: HttpContext) =
        task {
            match rejection with
            | RequestRejection.Forbidden ->
                context.Response.StatusCode <- StatusCodes.Status403Forbidden
            | RequestRejection.Unauthorized ->
                context.Response.Headers.WWWAuthenticate <- "Bearer"
                context.Response.StatusCode <- StatusCodes.Status401Unauthorized
            | RequestRejection.TooLarge ->
                context.Response.StatusCode <- StatusCodes.Status413PayloadTooLarge
        }

    let private copyRequestHeaders (context: HttpContext) (request: HttpRequestMessage) =
        [ "Accept"; "Accept-Language"; "If-Modified-Since"; "If-None-Match"; "Range" ]
        |> List.iter (fun name ->
            let values = context.Request.Headers[name] |> Seq.toArray

            if values.Length > 0 then
                request.Headers.TryAddWithoutValidation(name, values) |> ignore)

    let private hopByHopHeaders =
        set
            [ "connection"; "cache-control"; "keep-alive"; "pragma"
              "proxy-authenticate"; "proxy-authorization"; "referrer-policy"
              "server"; "set-cookie"; "te"; "trailer"; "transfer-encoding"; "upgrade" ]

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
                pair.Value
                |> Seq.toArray
                |> StringValues)

    let private protectAttachmentResponse (context: HttpContext) =
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
                let target =
                    Uri(
                        $"http://127.0.0.1:{ttydPort}{targetPath}{context.Request.QueryString}"
                    )

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

                    if context.Request.Method <> "HEAD" then
                        do!
                            response.Content.CopyToAsync(
                                context.Response.Body,
                                context.RequestAborted
                            )
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
            protectAttachmentResponse context

            let authorizationHeaders, targetPath =
                authorization attachmentPathPrefix context

            match
                RequestSecurity.validate
                    allowedOrigins
                    bearerToken
                    (RequestSecurity.metadata authorizationHeaders context)
            with
            | Error rejection -> return! reject rejection context
            | Ok() ->
                if targetPath = "/ws" then
                    let supportsTty =
                        context.WebSockets.WebSocketRequestedProtocols
                        |> Seq.exists (fun protocol ->
                            String.Equals(
                                protocol,
                                TtySubprotocol,
                                StringComparison.Ordinal
                            ))

                    if not context.WebSockets.IsWebSocketRequest || not supportsTty then
                        context.Response.StatusCode <- StatusCodes.Status400BadRequest
                    else
                        use! socket =
                            context.WebSockets.AcceptWebSocketAsync(TtySubprotocol)

                        match!
                            plane.AttachSocket socket
                            |> Async.StartAsTask
                        with
                        | None ->
                            context.Abort()
                        | Some attachmentId ->
                            do! runBrowser plane attachmentId socket
                else
                    return! proxyHttp ttydPort targetPath client context
        }

    let private startProxy
        allowedOrigins
        bearerToken
        sessionId
        ttydPort
        plane
        =
        task {
            let handler =
                new SocketsHttpHandler(
                    UseProxy = false,
                    AllowAutoRedirect = false
                )

            let client = new HttpClient(handler, disposeHandler = true)
            let builder = WebApplication.CreateSlimBuilder()
            builder.Logging.ClearProviders() |> ignore

            builder.WebHost.ConfigureKestrel(fun options ->
                options.Limits.MaxRequestBodySize <- Protocol.MaximumRequestBodyBytes
                options.AddServerHeader <- false
                options.Listen(IPAddress.Loopback, 0))
            |> ignore

            let application = builder.Build()
            application.UseWebSockets() |> ignore
            let attachmentPathPrefix = $"{AttachmentPathRoot}{sessionId}/"

            application.Run(
                RequestDelegate(fun context ->
                    handleAttachment
                        allowedOrigins
                        bearerToken
                        attachmentPathPrefix
                        ttydPort
                        client
                        plane
                        context
                    :> Task)
            )

            try
                do! application.StartAsync()
                let server = application.Services.GetRequiredService<IServer>()
                let addresses = server.Features.Get<IServerAddressesFeature>().Addresses
                let bound = addresses |> Seq.exactlyOne |> Uri

                let endpoint =
                    $"http://127.0.0.1:{bound.Port}{attachmentPathPrefix}{Uri.EscapeDataString bearerToken}/"

                return Ok(application, client, endpoint)
            with _ ->
                client.Dispose()
                do! application.DisposeAsync().AsTask()
                return Error "Could not start the terminal attachment endpoint"
        }

    let private openUpstream ttydPort =
        async {
            let socket = new ClientWebSocket()
            socket.Options.AddSubProtocol TtySubprotocol

            socket.Options.SetRequestHeader(
                "Origin",
                $"http://127.0.0.1:{ttydPort}"
            )

            use cancellation =
                new CancellationTokenSource(TimeSpan.FromSeconds 5.0)

            try
                do!
                    socket.ConnectAsync(
                        Uri($"ws://127.0.0.1:{ttydPort}/ws"),
                        cancellation.Token
                    )
                    |> Async.AwaitTask

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
        (terminalProcess: TerminalProcess)
        =
        async {
            match! connector terminalProcess.TtydPort with
            | Error error -> return Error error
            | Ok upstream ->
                let! initialized =
                    TerminalProtocol.defaultSize
                    |> TerminalProtocol.initialHandshake
                    |> sendFrame upstream

                if not initialized then
                    do!
                        closeSocket
                            WebSocketCloseStatus.EndpointUnavailable
                            "Terminal startup failed"
                            upstream

                    return Error "Could not initialize the ttyd WebSocket"
                else
                    let core =
                        createCore
                            Protocol.MaximumReplayBytes
                            upstream
                            terminalProcess.Close

                    startUpstreamPump core upstream

                    match!
                        startProxy
                            allowedOrigins
                            bearerToken
                            sessionId
                            terminalProcess.TtydPort
                            core
                        |> Async.AwaitTask
                    with
                    | Error error ->
                        do! core.Stop()
                        return Error error
                    | Ok(application, client, endpoint) ->
                        let stopWorkflow =
                            lazy
                                (task {
                                    do! core.Stop() |> Async.StartAsTask
                                    use stopCancellation =
                                        new CancellationTokenSource(proxyShutdownTimeout)

                                    try
                                        do! application.StopAsync(stopCancellation.Token)
                                    with _ ->
                                        ()

                                    try
                                        do!
                                            application.DisposeAsync().AsTask().WaitAsync(
                                                proxyShutdownTimeout
                                            )
                                    with _ ->
                                        ()

                                    client.Dispose()
                                })

                        return
                            Ok
                                { core with
                                    AttachmentEndpoint = endpoint
                                    Stop =
                                        fun () ->
                                            stopWorkflow.Value
                                            |> Async.AwaitTask }
        }

    let start allowedOrigins bearerToken sessionId terminalProcess =
        startWithConnector
            openUpstream
            allowedOrigins
            bearerToken
            sessionId
            terminalProcess
