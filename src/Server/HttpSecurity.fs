module Server.HttpSecurity

open Giraffe
open Microsoft.AspNetCore.Http

/// True when a URL host is a loopback address (IPv4 127.0.0.0/8 or IPv6 ::1) or the literal
/// "localhost". Mirrors the host test in CanvasDocServer.isLoopbackInjectUrl; scheme and port are
/// intentionally ignored — any local port may serve the SPA and both http/https localhost are the
/// same machine.
let private isLoopbackHost (host: string) : bool =
    // Uri.Host wraps an IPv6 literal in brackets (e.g. "[::1]"); strip them before IPAddress.TryParse.
    let h = host.Trim('[', ']')
    System.String.Equals(h, "localhost", System.StringComparison.OrdinalIgnoreCase)
    || (match System.Net.IPAddress.TryParse h with
        | true, ip -> System.Net.IPAddress.IsLoopback ip
        | false, _ -> false)

/// Decide whether a request whose Origin/Referer headers hold these values originates from the
/// same machine. Origin is authoritative when present; the Referer is only consulted when Origin
/// is absent. A MISSING pair is treated as same-origin (returns true) so the non-browser Cli client
/// (which sends neither header) and same-origin SPA requests keep working. A present value that is
/// not an absolute loopback http(s) URL — including the literal "null" browsers emit for opaque
/// origins — returns false (rejected).
let internal isSameOriginRequest (origin: string option) (referer: string option) : bool =
    let hostOf (value: string) =
        match System.Uri.TryCreate(value, System.UriKind.Absolute) with
        | true, uri -> Some uri.Host
        | false, _ -> None

    match origin |> Option.orElse referer with
    | None -> true
    | Some value ->
        match hostOf value with
        | Some host -> isLoopbackHost host
        | None -> false

/// GET/HEAD/OPTIONS are safe (non-state-changing) and exempt from the cross-origin check.
let private isSafeMethod (method: string) : bool =
    HttpMethods.IsGet method || HttpMethods.IsHead method || HttpMethods.IsOptions method

/// Full CSRF decision for a request: safe methods always pass; any other (state-changing) method
/// must be same-origin (loopback) per isSameOriginRequest. Pure, so the whole policy is
/// unit-testable without HTTP plumbing.
let internal isRequestAllowed (method: string) (origin: string option) (referer: string option) : bool =
    isSafeMethod method || isSameOriginRequest origin referer

/// Reads a request header, collapsing an absent-or-blank value to None.
let private headerValue (ctx: HttpContext) (name: string) : string option =
    match ctx.Request.Headers.TryGetValue name with
    | true, values ->
        let v = values.ToString()
        if System.String.IsNullOrWhiteSpace v then None else Some v
    | false, _ -> None

/// CSRF guard for the Fable.Remoting surface. The remoting API carries no auth or CSRF token, and
/// Fable.Remoting.Server does not enforce a request content type, so a cross-origin page could POST
/// (even a preflight-free text/plain body) to reach state-changing methods such as createWorktree —
/// which fire-and-forget launches a coding agent in the new worktree. Compose this BEFORE the
/// remoting handler: it rejects any state-changing request carrying a non-loopback Origin/Referer
/// while allowing a missing one (the Cli sends none; the same-origin SPA is loopback). Kestrel only
/// binds to localhost, so this closes the browser cross-origin vector without blocking either client.
let csrfGuard: HttpHandler =
    fun next ctx ->
        if isRequestAllowed ctx.Request.Method (headerValue ctx "Origin") (headerValue ctx "Referer") then
            next ctx
        else
            let origin = headerValue ctx "Origin" |> Option.defaultValue "<none>"
            Log.log "API" $"Rejected cross-origin {ctx.Request.Method} {ctx.Request.Path} (Origin={origin})"
            RequestErrors.FORBIDDEN "Cross-origin request rejected" next ctx
