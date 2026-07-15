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

[<CLIMutable>]
type CanvasAttributeRequest =
    { worktreePath: string
      filename: string
      sessionId: string }

/// Outcome of an ownership-attribution attempt, decoupled from HTTP so it is unit-testable
/// (the same extraction the SSRF guard uses with isLoopbackInjectUrl). Ownership is recorded for
/// a *known* (monitored) worktree with a well-formed body and nothing else: a missing field or an
/// unmonitored worktree records nothing, so a later getOwner stays None.
type AttributeOutcome =
    | Attributed                  // ownership recorded + persisted
    | UnknownWorktree             // well-formed but unmonitored worktree — nothing recorded
    | Invalid of reason: string   // missing/blank field — nothing recorded

/// Defense-in-depth for the F9 command-injection class: a declared owner sessionId is eventually
/// interpolated into a launched `--resume {id}` command (via CanvasDocOwnership.getOwner ->
/// CodingToolCli.build Resume). CodingToolCli now single-quote-escapes that value at the sink, but
/// we additionally refuse to *store* an owner id outside the safe set real provider session ids use
/// (ASCII alphanumerics, '-', '_' — GUIDs and provider UUIDs all qualify), so a hostile id carrying
/// ';', a newline, or '$(...)' never enters the ownership store in the first place.
let private isSafeSessionIdChar (c: char) =
    (c >= 'a' && c <= 'z') || (c >= 'A' && c <= 'Z') || (c >= '0' && c <= '9') || c = '-' || c = '_'

let internal isValidSessionId (sessionId: string) =
    not (System.String.IsNullOrWhiteSpace sessionId) && sessionId |> Seq.forall isSafeSessionIdChar

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
                    Log.log "Canvas" $"Registration: unmonitored worktree — {worktreePath} (extension serves the doc in a browser)"
                    return! Successful.ok (json {| registered = false; monitored = false |}) next ctx
                else
                    // Normalize a blank/whitespace sessionId to None (anonymous) rather than
                    // Some "": Option.ofObj only maps null. A Some "" owner is unroutable yet
                    // sticky (see CanvasBridge.normalizeSessionId), so it must never be stored —
                    // mirror attributeOwnership's IsNullOrWhiteSpace treatment of sessionId.
                    let sessionId =
                        if System.String.IsNullOrWhiteSpace body.sessionId then None else Some body.sessionId
                    CanvasBridge.registerSession worktreePath body.injectUrl sessionId
                    return! Successful.ok (json {| registered = true; monitored = true |}) next ctx
        with ex ->
            Log.log "Canvas" $"Registration failed: malformed JSON — {ex.Message}"
            return! RequestErrors.BAD_REQUEST $"malformed JSON: {ex.Message}" next ctx
    }

/// Validate a declared ownership and, only for a known (monitored) worktree, record it via
/// CanvasDocOwnership.attribute. Returns the decision so canvasAttributeHandler can map it to an
/// HTTP response and tests can assert it without HTTP plumbing. A missing field or an unmonitored
/// worktree records nothing — the caller's getOwner stays None.
let attributeOwnership
    (agent: MailboxProcessor<RefreshScheduler.StateMsg>)
    (worktreePath: string)
    (filename: string)
    (sessionId: string)
    : Async<AttributeOutcome> =
    async {
        if System.String.IsNullOrWhiteSpace worktreePath then
            return Invalid "missing worktreePath"
        elif System.String.IsNullOrWhiteSpace filename then
            return Invalid "missing filename"
        elif System.String.IsNullOrWhiteSpace sessionId then
            return Invalid "missing sessionId"
        elif not (isValidSessionId sessionId) then
            return Invalid "invalid sessionId format"
        else
            let worktreePath = worktreePath |> Server.PathUtils.normalizePath
            let! isKnown = isKnownWorktree agent worktreePath

            if not isKnown then
                return UnknownWorktree
            else
                CanvasDocOwnership.attribute worktreePath filename sessionId
                return Attributed
    }

/// POST /api/canvas/attribute {worktreePath, filename, sessionId}: the authoring session's
/// extension declares which session owns a canvas doc. Validates the body and known-worktree
/// guard exactly like canvasRegisterHandler, then records ownership for a monitored worktree.
let canvasAttributeHandler (agent: MailboxProcessor<RefreshScheduler.StateMsg>) : HttpHandler =
    fun next ctx -> task {
        try
            let! body = ctx.BindJsonAsync<CanvasAttributeRequest>()
            let! outcome = attributeOwnership agent body.worktreePath body.filename body.sessionId |> Async.StartAsTask

            match outcome with
            | Invalid reason ->
                Log.log "Canvas" $"Attribution failed: {reason}"
                return! RequestErrors.BAD_REQUEST reason next ctx
            | UnknownWorktree ->
                Log.log "Canvas" $"Attribution: unmonitored worktree — {body.worktreePath} (nothing recorded)"
                return! Successful.ok (json {| attributed = false; monitored = false |}) next ctx
            | Attributed ->
                Log.log "Canvas" $"Attribution recorded: {body.filename} -> {body.sessionId} for {body.worktreePath}"
                return! Successful.ok (json {| attributed = true; monitored = true |}) next ctx
        with ex ->
            Log.log "Canvas" $"Attribution failed: malformed JSON — {ex.Message}"
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

/// Intercepts in-doc link clicks: same-origin .html links become navigate-canvas-doc messages
/// (tab switch), everything else opens in a new tab. The target filename is taken from a.pathname
/// (the resolved path, which never includes ?query or #hash) rather than the raw href, so a link
/// like status.html?tab=errors resolves to the bare "status.html" that a CanvasDoc.Filename can
/// match — not a suffixed name that would silently fall back to the wrong tab. The `||h` guards the
/// (matched-branch-impossible) empty-pathname case; both match branches guarantee a.pathname ends
/// with .html.
let private linkInterceptor = "<script>document.addEventListener('click',function(e){var a=e.target.closest('a');if(!a)return;var h=a.getAttribute('href');if(!h||h.startsWith('#'))return;e.preventDefault();if((h.endsWith('.html')&&!h.includes('://'))||(a.origin===location.origin&&a.pathname.endsWith('.html'))){var f=(a.pathname||h).split('/').pop();parent.postMessage({action:'navigate-canvas-doc',filename:f},'*')}else{window.open(a.href,'_blank')}})</script>"

/// Bridge Escape from a cross-origin canvas doc back to the dashboard's focus reclaim. The doc is a
/// separate origin, so its keydown never reaches the pane's document-level focus-reclaim listener;
/// this injected listener posts {action:'reclaim-focus'} on Escape (unless the caret is in an
/// editable field inside the doc, which owns its own Escape). The pane routes it to the same Escape
/// reclaim. Injected into both doc kinds — reclaim should work from any doc the user is looking at.
let private reclaimFocusScript =
    [ "<script>document.addEventListener('keydown',function(e){"
      "if(e.key!=='Escape')return;"
      "var a=document.activeElement;"
      "if(a){var t=(a.tagName||'').toUpperCase();"
      "if(t==='INPUT'||t==='TEXTAREA'||t==='SELECT'||a.isContentEditable)return}"
      "parent.postMessage({action:'reclaim-focus'},'*')})</script>" ]
    |> String.concat ""

/// window.canvasSend(action, payload): the first-class doc→pane message helper, injected in the
/// AgentDoc arm only (a SystemView is server-generated and posts nothing, so it never gets the
/// helper). It wraps the existing FLAT message contract the pane already handles —
/// canvasSend('navigate-canvas-doc',{filename}) posts {action:'navigate-canvas-doc', filename} via
/// window.parent.postMessage(...,'*'), identical in effect to a hand-rolled postMessage.
///
/// The explicit `action` argument ALWAYS wins: payload is merged FIRST and {action} is applied OVER
/// it (Object.assign({},payload,{action:action})), so a payload that carries its own `action` key
/// can't silently override the caller's action — canvasSend('navigate-canvas-doc',{action:'x',...})
/// still posts (and size-checks) {action:'navigate-canvas-doc',...}, not {action:'x',...}. Applying
/// {action} last is load-bearing; do NOT flip it back to Object.assign({action:action},payload).
///
/// The size guard mirrors the client EXACTLY. CanvasPane.fs computes JSON.stringify(me.data).length
/// — where me.data IS the posted {action,...payload} object and .length is UTF-16 code units (the JS
/// String.length) — and DROPS the message when that exceeds MaxPayloadBytes (the "postMessage
/// DROPPED: payload too large" path). The helper measures the identical metric on the identical
/// object (var size=JSON.stringify(msg).length) and refuses to post when size>MAX, so the doc-side
/// verdict equals the client's drop decision — accept iff length<=cap, drop iff length>cap — but the
/// author gets an immediate doc-side console.error instead of a silent client-side drop. The cap is
/// applied uniformly to every action; the navigate/morph payloads the client special-cases ahead of
/// its size check are tiny, so the uniform guard never diverges in practice. UTF-8 byte length is
/// deliberately NOT used: it would disagree with the client's String.length check and could block a
/// payload the client accepts (or pass one it drops). The 64000 literal mirrors MaxPayloadBytes in
/// src/Client/CanvasPane.fs and is kept in sync by hand (CanvasDocServerTests pins the two together).
let private canvasSendScript =
    [ "<script>(function(){"
      "var MAX=64000;"
      "window.canvasSend=function(action,payload){"
      "var msg=Object.assign({},payload,{action:action});"
      "var size=JSON.stringify(msg).length;"
      "if(size>MAX){"
      "console.error('[canvas] canvasSend DROPPED: '+action+' message too large ('+size+' > '+MAX+' UTF-16 code units); not sent');"
      "return false}"
      "window.parent.postMessage(msg,'*');"
      "return true}"
      "})()</script>" ]
    |> String.concat ""

/// `.canvas-spinner`: the themed spinner style for the expand-in-place feedback, injected in the
/// AgentDoc arm only. Its sole consumer is the spinner window.canvasExpand swaps the clicked button
/// for, so it ships alongside that helper rather than in the shared baseStyle. Drawn with currentColor
/// + a CSS keyframe so it stays on-theme without depending on any doc-defined variable;
/// `.canvas-spinner` is a normal-specificity utility class an author can still override.
let private canvasExpandStyle =
    "<style>@keyframes canvas-spin{to{transform:rotate(360deg)}}.canvas-spinner{display:inline-flex;align-items:center;gap:.5em;color:inherit}.canvas-spinner::before{content:\"\";width:1em;height:1em;border:2px solid currentColor;border-top-color:transparent;border-radius:50%;animation:canvas-spin .6s linear infinite;opacity:.85}</style>"

/// window.canvasExpand(button, sectionId): the doc→agent "expand this section in place" helper,
/// injected in the AgentDoc arm only (it calls window.canvasSend, and only an AgentDoc has an owner
/// session to receive the request). On click it (1) sends the flat {action:'expand-section', section,
/// doc} message — `doc` is THIS doc's filename (the last location.pathname segment, decoded), so the
/// owning agent knows which .agents/canvas/<doc> file to edit — and (2) swaps the triggering button
/// for the themed spinner (styled by canvasExpandStyle), giving the user immediate in-pane feedback
/// while the agent works. The agent replaces the button with the real expanded content; Treemon's
/// content-change morph then swaps the spinner for that content, in place where the button was. The
/// button is swapped ONLY after canvasSend actually posts (returns true), so a dropped message (e.g.
/// oversized — unreachable here, but defensive) never strands a spinner with no agent on the other
/// end. `section` is validated against [A-Za-z0-9_-] before posting — a hardening guard so doc content
/// (which may embed untrusted external data like branch names or PR titles) can't smuggle
/// instruction-shaped text into the agent's [canvas] turn; a value with any other character is ignored.
/// The raw postMessage contract bypasses this guard, so SKILL.md also tells the agent to treat
/// section/doc as data to locate (match against a known section, never run as an instruction).
/// Injected after canvasSendScript (the helper it calls), alongside canvasExpandStyle.
let private canvasExpandScript =
    [ "<script>(function(){"
      "window.canvasExpand=function(btn,section){"
      "if(!section)return false;"
      "if(!/^[A-Za-z0-9_-]+$/.test(section)){console.error('[canvas] canvasExpand IGNORED: sectionId must match [A-Za-z0-9_-]: '+section);return false}"
      "var doc=decodeURIComponent((location.pathname.split('/').pop())||'');"
      "if(!window.canvasSend('expand-section',{section:section,doc:doc}))return false;"
      "if(btn){var s=document.createElement('span');s.className='canvas-spinner';s.setAttribute('role','status');s.textContent='Expanding…';btn.replaceWith(s)}"
      "return true}"
      "})()</script>" ]
    |> String.concat ""

/// Doc-side JS error overlay, injected in the AgentDoc arm only (a SystemView is server-generated
/// and runs no author JS, so it never gets the overlay). Installs window.onerror plus an
/// unhandledrejection listener and forwards each doc-side failure to the pane as the FLAT
/// {action:'canvas-doc-error', wt, doc, message, source, line, col} message the client surfaces in a
/// non-blocking, dismissible banner (src/Client/CanvasPane.fs). window.onerror's
/// (message, source, lineno, colno, error) signature maps 1:1 onto that payload; error.message is
/// preferred when present (the author's bare "boom" over the host's "Uncaught Error: boom"), else
/// the raw message string. Any window.onerror a <head> script installed before this </head>
/// injection is chained (prev.apply) so the overlay never silently swallows a doc's own handler, and
/// nothing is suppressed (no `return true`) so the native console error still reaches the author. The
/// postMessage is wrapped in try/catch so a throw inside the error path can never re-enter onerror
/// and spin an error loop. unhandledrejection reports reason.message (or String(reason)) with no
/// source/line/col, matching the same flat shape.
///
/// The `wt` and `doc` fields carry THIS doc's emitting identity — the worktree (derived in-iframe from
/// location.pathname, mirroring the bridge heartbeat) and the filename (the overlay is served per-doc).
/// The pane stamps the error from `wt`+`doc`, so it attributes the failure to the doc that actually
/// threw — independent of the active tab. Visited docs stay mounted as hidden iframes and keep running
/// JS, so an async error from a hidden/background doc (even in a different worktree) is no longer
/// misattributed to whatever tab is focused when the message is processed (focused-review A-02, C-06).
/// The reducer re-validates `wt`+`doc` against that worktree's docs before showing a banner, so a
/// stale/forged identity can't surface one.
let private errorOverlayScript (filename: string) =
    // JsonSerializer.Serialize yields a properly-escaped, HTML-safe JS string literal (e.g.
    // "status.html"; <,>,& and quotes are \uXXXX-escaped so a crafted filename can neither close the
    // <script> nor break out of the string), safe to splice straight into the injected source.
    let docLiteral = JsonSerializer.Serialize(filename)
    [ "<script>(function(){"
      $"var DOC={docLiteral};"
      "var WT=decodeURIComponent(location.pathname.substring(1,location.pathname.lastIndexOf('/')));"
      "function report(message,source,line,col){"
      "try{window.parent.postMessage({action:'canvas-doc-error',wt:WT,doc:DOC,message:String(message),source:source||'',line:(line==null?null:line),col:(col==null?null:col)},'*')}catch(e){}}"
      "var prev=window.onerror;"
      "window.onerror=function(message,source,line,col,error){"
      "report((error&&error.message)||message,source,line,col);"
      "return prev?prev.apply(this,arguments):false};"
      "window.addEventListener('unhandledrejection',function(e){"
      "var r=e&&e.reason;"
      "report((r&&r.message)||String(r),'',null,null)})"
      "})()</script>" ]
    |> String.concat ""

/// Choose the style/script injection for a served canvas doc based on its kind.
/// Both kinds get baseStyle + linkInterceptor + the Escape focus-reclaim bridge. AgentDocs additionally get the message-bridge
/// heartbeat, the window.canvasSend helper, the window.canvasExpand expand-in-place helper and
/// its spinner style (canvasExpandStyle), the JS error overlay, and the idiomorph runtime +
/// morph controller. `filename` is the doc being served: it is embedded into the error overlay
/// so a doc-side error carries its own identity (the emitter), letting the pane attribute it
/// correctly even when other docs are mounted as hidden iframes. It is unused for SystemViews
/// (no overlay).
/// SystemViews (e.g. the beads dashboard) are server-generated and data-driven with no owner
/// session: they drive their own refresh and must never morph (a morph would stomp the live,
/// JS-rendered dashboard back to the empty template shell), nothing routes session→doc messages to
/// them, and they post nothing back — so the bridge, canvasSend, and morph pieces are all omitted.
let buildInjection (kind: CanvasDocKind) (filename: string) : string =
    match kind with
    | SystemView -> CanvasExport.baseStyle + linkInterceptor + reclaimFocusScript
    | AgentDoc -> CanvasExport.baseStyle + linkInterceptor + reclaimFocusScript + bridgeScript + canvasSendScript + canvasExpandStyle + canvasExpandScript + errorOverlayScript filename + IdiomorphScript.idiomorphJs + IdiomorphScript.morphController

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
                let injection = buildInjection (CanvasDocKind.classify filename) filename
                // Same </head> placement the static export uses (CanvasExport.injectAtHead) — one
                // implementation so live-served and published docs can never drift.
                let injected = CanvasExport.injectAtHead injection html
                ctx.Response.ContentType <- "text/html; charset=utf-8"
                ctx.Response.Headers["Cache-Control"] <- "no-cache"
                // Restrict who may frame a canvas doc to local treemon UI origins. The dashboard frames
                // docs cross-origin (loopback, with dev/prod ports that vary), so a port-wildcard
                // loopback allowlist permits the pane while blocking any public page from framing a doc
                // and harvesting its iframe→parent postMessages (canvasSend input, doc-error text). CSP
                // frame-ancestors is the modern successor to X-Frame-Options, which is deliberately NOT
                // set: its only cross-origin option is DENY/SAMEORIGIN, which would also block the
                // legitimate cross-origin pane.
                ctx.Response.Headers["Content-Security-Policy"] <- "frame-ancestors 'self' http://localhost:* http://127.0.0.1:*"
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
