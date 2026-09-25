namespace TerminalHost

open System
open System.Net
open System.Net.Http
open System.Net.WebSockets
open System.Text
open System.Text.Json
open System.Threading
open System.Threading.Tasks
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.Primitives

type private SocketReceive = Frame of byte array | PeerClosed | MessageTooLarge | ReceiveFailed
type private SocketReceiveMode = Buffered of int | Streaming of (byte array -> Async<unit>)

[<RequireQualifiedAccess>]
module internal TerminalProxy =
    let [<Literal>] private AttachmentPathRoot = "/_treemon/"
    let [<Literal>] private TtySubprotocol = "tty"
    let [<Literal>] private CommandSubprotocol = "treemon-command"

    let private terminalPageHeadInjection =
        $"<style>.xterm-viewport{{scrollbar-width:none}}.xterm-viewport::-webkit-scrollbar{{display:none}}</style><script>(function(){{function focusTerminal(){{var input=document.querySelector('.xterm-helper-textarea');if(input)input.focus()}}function isTerminalInput(e){{return e.target&&e.target.classList&&e.target.classList.contains('xterm-helper-textarea')}}function hasTerminalMethod(name){{return window.term&&typeof window.term[name]==='function'}}window.addEventListener('message',function(e){{if(e.source!==parent||!e.data||e.data.action!=='{Shared.TerminalPageMessage.FocusTerminal}')return;focusTerminal()}});document.addEventListener('keydown',function(e){{var key=(e.key||'').toLowerCase();var exactCtrl=e.ctrlKey&&!e.metaKey&&!e.altKey&&!e.shiftKey;if(isTerminalInput(e)&&exactCtrl&&key==='enter'&&hasTerminalMethod('input')){{e.preventDefault();e.stopImmediatePropagation();window.term.input('\\n',true);return}}if(isTerminalInput(e)&&exactCtrl&&key==='v'){{e.stopImmediatePropagation();return}}if(!(e.ctrlKey||e.metaKey)||e.altKey)return;var action=key==='p'?'{Shared.TerminalPageMessage.OpenWorktreeSearch}':key==='tab'?'{Shared.TerminalPageMessage.CycleTerminal}':key==='w'&&!e.shiftKey?'{Shared.TerminalPageMessage.CloseTerminal}':key==='n'&&!e.shiftKey?'{Shared.TerminalPageMessage.StartTerminal}':'';if(!action)return;e.preventDefault();e.stopImmediatePropagation();if(action==='{Shared.TerminalPageMessage.CycleTerminal}')parent.postMessage({{action:action,direction:e.shiftKey?'{Shared.TerminalPageMessage.PreviousDirection}':'{Shared.TerminalPageMessage.NextDirection}'}},'*');else parent.postMessage({{action:action}},'*')}},true)}})()</script>"

    let private terminalClipboardInjection =
        """<style>
            .treemon-clipboard-error {
                position: fixed; z-index: 10; left: 12px; right: 12px; bottom: 12px;
                display: flex; align-items: center; gap: 8px; padding: 8px 12px;
                border: 1px solid #f38ba8; border-radius: 4px;
                background: #1e1e2e; color: #f38ba8; font: 13px system-ui, sans-serif;
            }
            .treemon-clipboard-error button {
                margin-left: auto; border: 1px solid #45475a; border-radius: 4px;
                background: #313244; color: #cdd6f4; font: inherit; cursor: pointer;
            }
        </style><script>(function() {
            const replayStart = """
        + JsonSerializer.Serialize TerminalProtocol.ClipboardReplayStart
        + """, replayEnd = """
        + JsonSerializer.Serialize TerminalProtocol.ClipboardReplayEnd
        + """;
            let replaying = true, copyIntentUntil = 0, copyAttempt = 0;
            const maxEncodedLength = 262144;
            function clearError() {
                document.querySelector('.treemon-clipboard-error')?.remove();
            }
            function showError(message, error) {
                if (error) console.error('[treemon] terminal clipboard copy failed', error);
                clearError();
                const notice = document.createElement('div');
                notice.className = 'treemon-clipboard-error';
                notice.setAttribute('role', 'alert');
                const text = document.createElement('span');
                text.textContent = message;
                const dismiss = document.createElement('button');
                dismiss.type = 'button';
                dismiss.textContent = 'Dismiss';
                dismiss.addEventListener('click', clearError);
                notice.append(text, dismiss);
                document.body.appendChild(notice);
            }
            function inTerminal(event) {
                return event.target instanceof Element && !!event.target.closest('.xterm');
            }
            document.addEventListener('contextmenu', event => {
                if (inTerminal(event)) event.preventDefault();
            }, true);
            function armCopy(event) {
                if (!event.isTrusted || !inTerminal(event)) return;
                const rightClick = event.type === 'pointerdown' && event.button === 2;
                const copyKey = event.type === 'keydown' &&
                    (event.ctrlKey || event.metaKey) && !event.altKey && !event.shiftKey &&
                    event.key.toLowerCase() === 'c';
                if (rightClick || copyKey) copyIntentUntil = performance.now() + 5000;
            }
            document.addEventListener('pointerdown', armCopy, true);
            document.addEventListener('keydown', armCopy, true);
            function install() {
                if (!window.term) return false;
                if (!window.term.parser?.registerOscHandler) {
                    showError('This terminal cannot copy text to the browser clipboard.');
                    return true;
                }
                const parser = window.term.parser;
                parser.registerOscHandler("""
        + string TerminalProtocol.ClipboardReplayOsc
        + """, function(data) {
                    if (data === replayStart) replaying = true;
                    else if (data === replayEnd) replaying = false;
                    return true;
                });
                parser.registerOscHandler(52, function(data) {
                    if (replaying || !data.startsWith('c;') ||
                        copyIntentUntil < performance.now() || !document.hasFocus()) return true;
                    copyIntentUntil = 0;
                    const attempt = ++copyAttempt;
                    const encoded = data.slice(2);
                    if (!encoded || encoded.length > maxEncodedLength ||
                        encoded.length % 4 !== 0 || !/^[A-Za-z0-9+/]*={0,2}$/.test(encoded)) {
                        showError('Could not copy: invalid or oversized terminal clipboard data.');
                        return true;
                    }
                    let text;
                    try {
                        const bytes = Uint8Array.from(atob(encoded), char => char.charCodeAt(0));
                        text = new TextDecoder('utf-8', { fatal: true }).decode(bytes);
                    } catch (error) {
                        showError('Could not decode terminal clipboard data.', error);
                        return true;
                    }
                    const reportWriteFailure = error => {
                        if (attempt === copyAttempt) {
                            showError('Could not copy to the browser clipboard. Check clipboard permissions and try again.', error);
                        } else {
                            console.error('[treemon] superseded terminal clipboard write failed', error);
                        }
                    };
                    try {
                        navigator.clipboard.writeText(text)
                            .then(() => { if (attempt === copyAttempt) clearError(); })
                            .catch(reportWriteFailure);
                    } catch (error) {
                        reportWriteFailure(error);
                    }
                    return true;
                });
                return true;
            }
            if (!install()) {
                const observer = new MutationObserver(() => {
                    if (install()) observer.disconnect();
                });
                observer.observe(document, { childList: true, subtree: true });
                setTimeout(() => {
                    observer.disconnect();
                    if (!window.term) console.error('[treemon] terminal clipboard handler was not installed');
                }, 10000);
            }
        })()</script>"""

    let private proxyShutdownTimeout = TimeSpan.FromSeconds 5.0

    type internal ProxyStopOperations =
        { StopDataPlane: unit -> Task
          StopApplication: CancellationToken -> Task
          DisposeApplication: unit -> Task
          DisposeClient: unit -> unit }

    let internal customizeTerminalPage (allowedOrigins: string list) (html: string) =
        let reconnectScript =
            $"<script>(function(){{var allowedOrigins={JsonSerializer.Serialize allowedOrigins},action={JsonSerializer.Serialize Shared.TerminalPageMessage.TerminalVisible},reconnectPrompt=\"Press \\u23CE to Reconnect\",poll=null,deadline=null,reloading=false,reloadMarker='treemon-terminal-reconnect-load',suppressNextLoadedActivation=(function(){{try{{var marked=sessionStorage.getItem(reloadMarker)==='1';sessionStorage.removeItem(reloadMarker);return marked}}catch(_){{return true}}}})();function clearPending(){{if(poll!==null){{clearInterval(poll);poll=null}}if(deadline!==null){{clearTimeout(deadline);deadline=null}}}}function isWaitingForReconnect(){{var terminal=document.querySelector('.xterm');return !!terminal&&Array.prototype.some.call(terminal.children,function(child){{return child.tagName==='DIV'&&child.style.position==='absolute'&&child.textContent===reconnectPrompt}})}}function reconnectIfWaiting(){{if(reloading||document.visibilityState!=='visible'||!isWaitingForReconnect())return false;reloading=true;clearPending();try{{sessionStorage.setItem(reloadMarker,'1')}}catch(_){{}}window.location.reload();return true}}function activate(loaded){{if(document.visibilityState!=='visible'){{clearPending();return}}if(loaded&&suppressNextLoadedActivation){{suppressNextLoadedActivation=false;return}}if(reconnectIfWaiting())return;if(poll===null)poll=setInterval(reconnectIfWaiting,100);if(deadline!==null)clearTimeout(deadline);deadline=setTimeout(clearPending,10000)}}window.addEventListener('message',function(event){{if(event.source!==window.parent||allowedOrigins.indexOf(event.origin)<0||!event.data||event.data.action!==action)return;if(event.data.active===false){{clearPending();return}}if(event.data.active===true)activate(event.data.loaded===true)}});document.addEventListener('visibilitychange',function(){{if(document.visibilityState!=='visible')clearPending()}})}})();</script>"

        html.Replace("</head>", terminalPageHeadInjection + terminalClipboardInjection + reconnectScript + "</head>", StringComparison.OrdinalIgnoreCase)

    let private receiveMessage mode (socket: WebSocket) =
        let buffer = Array.zeroCreate<byte> 8_192

        let rec receive chunks total messageType frameKind =
            async {
                try
                    let! result =
                        socket.ReceiveAsync(ArraySegment<byte>(buffer), CancellationToken.None)
                        |> Async.AwaitTask

                    if result.MessageType = WebSocketMessageType.Close then return PeerClosed
                    elif messageType |> Option.exists ((<>) result.MessageType) then return ReceiveFailed
                    else
                        let chunk = buffer.AsSpan(0, result.Count).ToArray()

                        match mode with
                        | Buffered maximumBytes when total + chunk.Length > maximumBytes ->
                            return MessageTooLarge
                        | Buffered _ ->
                            let updated = chunk :: chunks
                            if result.EndOfMessage then return Frame(updated |> List.rev |> Array.concat)
                            else return! receive updated (total + chunk.Length) (Some result.MessageType) None
                        | Streaming forward ->
                            // WebSocket continuations omit ttyd's leading message kind; restore it on each forwarded chunk.
                            let frame =
                                match frameKind with
                                | Some kind when chunk.Length > 0 -> Array.append [| kind |] chunk
                                | Some _ -> Array.empty
                                | None -> chunk

                            if frame.Length > 0 then do! forward frame
                            if result.EndOfMessage then return Frame Array.empty
                            else
                                return!
                                    receive [] 0 (Some result.MessageType)
                                        (frameKind |> Option.orElseWith (fun () -> Array.tryHead chunk))
                with _ -> return ReceiveFailed
            }

        receive [] 0 None None

    let private startUpstreamPumpUntilReady (startupTimeout: TimeSpan) plane upstream =
        let ready = TaskCompletionSource<Result<unit, string>>(TaskCreationOptions.RunContinuationsAsynchronously)

        let forward frame =
            async {
                do! plane.AcceptUpstreamFrame frame
                if frame[0] = byte '0' then ready.TrySetResult(Ok()) |> ignore
            }

        let rec pump () =
            async {
                match! receiveMessage (Streaming forward) upstream with
                | Frame _ -> return! pump ()
                | _ ->
                    ready.TrySetResult(Error "Terminal upstream closed before the shell became ready")
                    |> ignore
                    do! plane.UpstreamEnded()
            }

        Async.Start(pump ())

        async {
            let! completed =
                Task.WhenAny(ready.Task :> Task, Task.Delay startupTimeout)
                |> Async.AwaitTask

            if Object.ReferenceEquals(completed, ready.Task) then
                return! ready.Task |> Async.AwaitTask
            else
                return Error "Timed out waiting for the terminal shell to become ready"
        }

    let private runBrowser plane attachmentId (socket: WebSocket) =
        let rec receive () =
            async {
                match! receiveMessage (Buffered Protocol.MaximumAttachmentMessageBytes) socket with
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

    let private copyResponseHeaders (response: HttpResponseMessage) (context: HttpContext) =
        Seq.append response.Headers response.Content.Headers
        |> Seq.filter (fun pair ->
            hopByHopHeaders |> Set.contains (pair.Key.ToLowerInvariant()) |> not)
        |> Seq.iter (fun pair ->
            context.Response.Headers[pair.Key] <- pair.Value |> Seq.toArray |> StringValues)

    let private isTerminalPage targetPath (response: HttpResponseMessage) =
        let contentType =
            response.Content.Headers.ContentType
            |> Option.ofObj
            |> Option.bind (_.MediaType >> Option.ofObj)
            |> Option.defaultValue ""

        targetPath = "/"
        && response.StatusCode = HttpStatusCode.OK
        && String.Equals(contentType, "text/html", StringComparison.OrdinalIgnoreCase)
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
        allowedOrigins
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
                        client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, context.RequestAborted)

                    context.Response.StatusCode <- int response.StatusCode
                    copyResponseHeaders response context

                    if context.Request.Method = "GET" && isTerminalPage targetPath response then
                        [ "Accept-Ranges"; "Content-Encoding"; "Content-Length"; "Content-MD5"
                          "Content-Range"; "ETag" ]
                        |> List.iter (context.Response.Headers.Remove >> ignore)

                        let! html = response.Content.ReadAsStringAsync(context.RequestAborted)

                        let bytes =
                            html
                            |> customizeTerminalPage allowedOrigins
                            |> Encoding.UTF8.GetBytes

                        context.Response.ContentLength <- int64 bytes.Length
                        do! context.Response.Body.WriteAsync(bytes, context.RequestAborted)
                    elif context.Request.Method <> "HEAD" then
                        do! response.Content.CopyToAsync(context.Response.Body, context.RequestAborted)
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

            match RequestSecurity.validate allowedOrigins bearerToken (RequestSecurity.metadata authorizationHeaders context) with
            | Error rejection -> reject rejection context
            | Ok() ->
                if targetPath = "/ws" then
                    let protocols = context.WebSockets.WebSocketRequestedProtocols
                    let supports protocol =
                        protocols |> Seq.exists (fun value -> String.Equals(value, protocol, StringComparison.Ordinal))

                    let attachment =
                        if supports CommandSubprotocol then
                            Some(CommandSubprotocol, TerminalAttachmentMode.Command)
                        elif supports TtySubprotocol then
                            Some(TtySubprotocol, TerminalAttachmentMode.Browser)
                        else
                            None

                    if not context.WebSockets.IsWebSocketRequest || Option.isNone attachment then
                        context.Response.StatusCode <- StatusCodes.Status400BadRequest
                    else
                        let protocol, mode = Option.get attachment
                        use! socket = context.WebSockets.AcceptWebSocketAsync(protocol)

                        match! plane.AttachSocket mode socket |> Async.StartAsTask with
                        | None -> context.Abort()
                        | Some attachmentId ->
                            do! runBrowser plane attachmentId socket |> Async.StartAsTask
                else
                    return!
                        proxyHttp
                            allowedOrigins
                            ttydPort
                            targetPath
                            client
                            context
        }

    let private ignoreTaskFailure (operation: unit -> Task) =
        task {
            try
                do! operation ()
            with _ ->
                ()
        }

    let internal stopProxy operations () =
        task {
            let! dataPlaneFailure =
                task {
                    try
                        do! operations.StopDataPlane()
                        return None
                    with error ->
                        return Some error
                }

            try
                use cancellation = new CancellationTokenSource(proxyShutdownTimeout)

                do! ignoreTaskFailure (fun () -> operations.StopApplication cancellation.Token)
                do! ignoreTaskFailure operations.DisposeApplication
            finally
                operations.DisposeClient()

            match dataPlaneFailure with
            | None -> ()
            | Some error -> raise error
        }
        |> Async.AwaitTask

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

                let endpoint = $"http://127.0.0.1:{boundPort}{attachmentPathPrefix}{Uri.EscapeDataString bearerToken}/"

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
        connector startupTimeout allowedOrigins bearerToken
        sessionId ttydPort onUpstreamEnded =
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
                        TerminalDataPlane.closeSocket WebSocketCloseStatus.EndpointUnavailable "Terminal startup failed" upstream

                    return Error "Could not initialize the ttyd WebSocket"
                else
                    let core = TerminalDataPlane.createCore Protocol.MaximumReplayBytes upstream onUpstreamEnded

                    match! startUpstreamPumpUntilReady startupTimeout core upstream with
                    | Error error ->
                        do! core.Stop()
                        return Error error
                    | Ok() ->
                        match!
                            startProxy allowedOrigins bearerToken sessionId ttydPort core |> Async.AwaitTask
                        with
                        | Error error ->
                            do! core.Stop()
                            return Error error
                        | Ok(application, client, endpoint) ->
                            let stopOperations =
                                { StopDataPlane =
                                    fun () ->
                                        (core.Stop() |> Async.StartAsTask) :> Task
                                  StopApplication =
                                    fun cancellation ->
                                        application.StopAsync(cancellation)
                                  DisposeApplication =
                                    fun () ->
                                        application
                                            .DisposeAsync()
                                            .AsTask()
                                            .WaitAsync(proxyShutdownTimeout)
                                  DisposeClient = fun () -> client.Dispose() }

                            return
                                Ok
                                    { core with
                                        AttachmentEndpoint = endpoint
                                        Stop = stopProxy stopOperations }
        }

    let start startupTimeout allowedOrigins bearerToken
        sessionId ttydPort onUpstreamEnded =
        startWithConnector openUpstream startupTimeout allowedOrigins bearerToken
            sessionId ttydPort onUpstreamEnded
