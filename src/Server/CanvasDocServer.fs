module Server.CanvasDocServer

open Giraffe
open Microsoft.AspNetCore.Builder
open Microsoft.AspNetCore.Http
open Microsoft.Extensions.DependencyInjection
open System.IO
open System.Text.Json
open global.Microsoft.AspNetCore.Hosting
open Shared

[<CLIMutable>]
type CanvasRegisterRequest =
    { worktreePath: string
      injectUrl: string
      sessionId: string }

let private allKnownPaths (agent: MailboxProcessor<RefreshScheduler.StateMsg>) = async {
    let! state = agent.PostAndAsyncReply RefreshScheduler.GetState
    return
        state.Repos
        |> Map.values
        |> Seq.collect _.KnownPaths
        |> Set.ofSeq
}

let private isKnownWorktree agent path = async {
    let! paths = allKnownPaths agent
    return paths |> Set.contains path
}

/// injectUrl is stored and later used as an HTTP POST target by CanvasBridge (sendMessage /
/// drainQueue), so a non-local value would let a registrant make the server POST to arbitrary
/// hosts (SSRF). Accept only well-formed absolute http(s) URLs whose host is a loopback IP
/// (IPAddress.IsLoopback — IPv4 127.0.0.0/8 or IPv6 ::1) or the literal "localhost".
let isLoopbackInjectUrl (injectUrl: string) : bool =
    match System.Uri.TryCreate(injectUrl, System.UriKind.Absolute) with
    | true, uri ->
        (uri.Scheme = System.Uri.UriSchemeHttp || uri.Scheme = System.Uri.UriSchemeHttps)
        && (System.String.Equals(uri.Host, "localhost", System.StringComparison.OrdinalIgnoreCase)
            || (match System.Net.IPAddress.TryParse uri.Host with
                | true, ip -> System.Net.IPAddress.IsLoopback ip
                | false, _ -> false))
    | false, _ -> false

let canvasRegisterHandler (agent: MailboxProcessor<RefreshScheduler.StateMsg>) : HttpHandler =
    fun next ctx -> task {
        try
            let! body = ctx.BindJsonAsync<CanvasRegisterRequest>()

            if System.String.IsNullOrWhiteSpace body.worktreePath then
                Log.log "Canvas" "Registration failed: missing worktreePath"
                return! RequestErrors.BAD_REQUEST "missing worktreePath" next ctx
            elif System.String.IsNullOrWhiteSpace body.injectUrl then
                Log.log "Canvas" $"Registration failed: missing injectUrl for {body.worktreePath}"
                return! RequestErrors.BAD_REQUEST "missing injectUrl" next ctx
            elif not (isLoopbackInjectUrl body.injectUrl) then
                Log.log "Canvas" $"Registration failed: non-loopback injectUrl ({body.injectUrl}) for {body.worktreePath}"
                return! RequestErrors.BAD_REQUEST "injectUrl must resolve to a loopback host" next ctx
            else
                let worktreePath = body.worktreePath |> Server.PathUtils.normalizePath
                let! isKnown = isKnownWorktree agent worktreePath |> Async.StartAsTask

                if not isKnown then
                    Log.log "Canvas" $"Registration failed: unknown worktree — {worktreePath}"
                    return! RequestErrors.NOT_FOUND "unknown worktree" next ctx
                else
                    CanvasBridge.registerSession worktreePath body.injectUrl (Option.ofObj body.sessionId)
                    return! Successful.OK "registered" next ctx
        with ex ->
            Log.log "Canvas" $"Registration failed: malformed JSON — {ex.Message}"
            return! RequestErrors.BAD_REQUEST $"malformed JSON: {ex.Message}" next ctx
    }

let bridgeStatusHandler : HttpHandler =
    fun next ctx ->
        let worktreePath = ctx.GetQueryStringValue "worktreePath"

        match worktreePath with
        | Ok path when not (System.String.IsNullOrWhiteSpace path) ->
            let status = CanvasBridge.getStatus path
            Successful.ok
                (json {| registered = status.Registered; lastHeartbeatAge = status.LastHeartbeatAge; isAlive = status.IsAlive; sessionId = status.SessionId |})
                next ctx
        | _ ->
            RequestErrors.BAD_REQUEST "missing worktreePath query parameter" next ctx

let private handleHeartbeat (agent: MailboxProcessor<RefreshScheduler.StateMsg>) (ctx: HttpContext) : System.Threading.Tasks.Task = task {
    try
        let! body = ctx.Request.ReadFromJsonAsync<JsonElement>()

        match body.TryGetProperty("worktreePath") with
        | true, prop when prop.ValueKind = JsonValueKind.String ->
            let raw = prop.GetString()
            if System.String.IsNullOrWhiteSpace raw then
                ctx.Response.StatusCode <- 400
                do! ctx.Response.WriteAsync("missing worktreePath")
            else
                let worktreePath = raw |> Server.PathUtils.normalizePath
                let! isKnown = isKnownWorktree agent worktreePath |> Async.StartAsTask

                if not isKnown then
                    ctx.Response.StatusCode <- 404
                    do! ctx.Response.WriteAsync("Unknown worktree")
                else
                    CanvasBridge.registerPoll worktreePath
                    let messages = CanvasBridge.drainPending worktreePath
                    ctx.Response.ContentType <- "application/json"
                    do! ctx.Response.WriteAsJsonAsync(messages)
        | _ ->
            ctx.Response.StatusCode <- 400
            do! ctx.Response.WriteAsync("missing worktreePath")
    with ex ->
        Log.log "CanvasBridge" $"Heartbeat error: {ex.Message}"
        ctx.Response.StatusCode <- 400
        do! ctx.Response.WriteAsync("malformed request")
}

let private bridgeScript =
    [ "<script>(function(){"
      "var p=decodeURIComponent(location.pathname.substring(1,location.pathname.lastIndexOf('/')));"
      "var o=location.origin;"
      "function hb(){fetch(o+'/bridge/heartbeat',{method:'POST',"
      "headers:{'Content-Type':'application/json'},"
      "body:JSON.stringify({worktreePath:p})})"
      ".then(function(r){return r.json()})"
      ".then(function(msgs){if(msgs&&msgs.length)msgs.forEach(function(m){"
      "try{var d=typeof m==='string'?JSON.parse(m):m;"
      "window.dispatchEvent(new CustomEvent('canvasBridgeMessage',{detail:d}))"
      "}catch(e){}})})"
      ".catch(function(){})}"
      "hb();setInterval(hb,30000)"
      "})()</script>" ]
    |> String.concat ""

let private baseStyle = "<style>*{scrollbar-width:thin;scrollbar-color:rgba(88,91,112,.5) transparent}::-webkit-scrollbar{width:8px;height:8px}::-webkit-scrollbar-track{background:transparent}::-webkit-scrollbar-thumb{background:rgba(88,91,112,.5);border-radius:4px}::-webkit-scrollbar-thumb:hover{background:rgba(88,91,112,.8)}</style>"

/// Intercepts in-doc link clicks: same-origin .html links become navigate-canvas-doc messages
/// (tab switch), everything else opens in a new tab. The target filename is taken from a.pathname
/// (the resolved path, which never includes ?query or #hash) rather than the raw href, so a link
/// like status.html?tab=errors resolves to the bare "status.html" that a CanvasDoc.Filename can
/// match — not a suffixed name that would silently fall back to the wrong tab. The `||h` guards the
/// (matched-branch-impossible) empty-pathname case; both match branches guarantee a.pathname ends
/// with .html.
let private linkInterceptor = "<script>document.addEventListener('click',function(e){var a=e.target.closest('a');if(!a)return;var h=a.getAttribute('href');if(!h||h.startsWith('#'))return;e.preventDefault();if((h.endsWith('.html')&&!h.includes('://'))||(a.origin===location.origin&&a.pathname.endsWith('.html'))){var f=(a.pathname||h).split('/').pop();parent.postMessage({action:'navigate-canvas-doc',filename:f},'*')}else{window.open(a.href,'_blank')}})</script>"

/// Choose the style/script injection for a served canvas doc based on its kind.
/// Both kinds get baseStyle + linkInterceptor. AgentDocs additionally get the message-bridge
/// heartbeat and the idiomorph runtime + morph controller. SystemViews (e.g. the beads dashboard)
/// are server-generated and data-driven with no owner session: they drive their own refresh and
/// must never morph (a morph would stomp the live, JS-rendered dashboard back to the empty
/// template shell), and nothing routes session→doc messages to them, so those three are omitted.
let buildInjection (kind: CanvasDocKind) : string =
    match kind with
    | SystemView -> baseStyle + linkInterceptor
    | AgentDoc -> baseStyle + linkInterceptor + bridgeScript + IdiomorphScript.idiomorphJs + IdiomorphScript.morphController

let private handleCanvasRequest (agent: MailboxProcessor<RefreshScheduler.StateMsg>) (ctx: HttpContext) : System.Threading.Tasks.Task = task {
    let catchAll = ctx.Request.RouteValues["path"] :?> string
    let lastSlash = catchAll.LastIndexOf('/')
    if lastSlash < 1 then
        ctx.Response.StatusCode <- 400
        do! ctx.Response.WriteAsync("Invalid path format")
        Log.log "Canvas" $"Doc request 400: invalid path format — {catchAll}"
    else
        let worktreePathEncoded = catchAll.Substring(0, lastSlash)
        let filename = catchAll.Substring(lastSlash + 1)
        let worktreePath = System.Net.WebUtility.UrlDecode worktreePathEncoded |> Server.PathUtils.normalizePath

        let! isKnown = (isKnownWorktree agent worktreePath) |> Async.StartAsTask

        if filename = "beads-data" then
            if not isKnown then
                ctx.Response.StatusCode <- 404
                do! ctx.Response.WriteAsync("Unknown worktree")
                Log.log "Canvas" $"Doc request 404: unknown worktree — {worktreePath}"
            else
                let dbPath = Path.Combine(worktreePath, ".beads", "beads.db")
                let! json = BeadsStatus.getBeadsIssueList dbPath |> Async.StartAsTask
                ctx.Response.ContentType <- "application/json"
                ctx.Response.Headers["Cache-Control"] <- "no-cache"
                do! ctx.Response.WriteAsync(json)
                Log.log "Canvas" $"Doc request 200: {Path.GetFileName(worktreePath)}/beads-data"
        elif not (filename.EndsWith(".html")) then
            ctx.Response.StatusCode <- 400
            do! ctx.Response.WriteAsync("Only .html files are served")
            Log.log "Canvas" $"Doc request 400: non-html file — {filename}"
        elif not isKnown then
            ctx.Response.StatusCode <- 404
            do! ctx.Response.WriteAsync("Unknown worktree")
            Log.log "Canvas" $"Doc request 404: unknown worktree — {worktreePath}"
        else
            match Server.PathUtils.validateCanvasPath worktreePath filename with
            | Error reason ->
                ctx.Response.StatusCode <- 400
                do! ctx.Response.WriteAsync(reason)
                Log.log "Canvas" $"Doc request 400: path traversal — {filename}"
            | Ok resolvedPath when not (File.Exists resolvedPath) ->
                ctx.Response.StatusCode <- 404
                do! ctx.Response.WriteAsync("File not found")
                Log.log "Canvas" $"Doc request 404: file not found — {resolvedPath}"
            | Ok resolvedPath ->
                let! rawBytes = File.ReadAllBytesAsync(resolvedPath)
                let html = System.Text.Encoding.UTF8.GetString(rawBytes)
                let injection = buildInjection (CanvasDocKind.classify filename)
                let injected =
                    if html.Contains("</head>", System.StringComparison.OrdinalIgnoreCase)
                    then html.Replace("</head>", injection + "</head>", System.StringComparison.OrdinalIgnoreCase)
                    else injection + html
                ctx.Response.ContentType <- "text/html; charset=utf-8"
                ctx.Response.Headers["Cache-Control"] <- "no-cache"
                do! ctx.Response.WriteAsync(injected)
                Log.log "Canvas" $"Doc request 200: {Path.GetFileName(worktreePath)}/{filename}"
}

let start (agent: MailboxProcessor<RefreshScheduler.StateMsg>) (canvasPort: int) (cts: System.Threading.CancellationToken) =
    let host =
        Microsoft.AspNetCore.Hosting.WebHostBuilder()
            .UseKestrel(fun opts ->
                opts.Listen(System.Net.IPAddress.Loopback, canvasPort))
            .ConfigureServices(fun services ->
                services.AddRouting() |> ignore)
            .Configure(fun (app: IApplicationBuilder) ->
                app.UseRouting() |> ignore
                app.UseEndpoints(fun endpoints ->
                    endpoints.MapPost("/bridge/heartbeat", RequestDelegate(handleHeartbeat agent)) |> ignore
                    endpoints.MapGet("/{**path}", RequestDelegate(handleCanvasRequest agent)) |> ignore) |> ignore)
            .Build()
    Log.log "Startup" $"Canvas doc server starting on http://127.0.0.1:{canvasPort}"
    host.StartAsync(cts).ContinueWith(fun (t: System.Threading.Tasks.Task) ->
        if t.IsFaulted then
            Log.log "Canvas" $"Canvas doc server failed to start: {t.Exception.InnerException.Message}")
    |> ignore
