/// Pure HTML→HTML transforms that turn an on-disk canvas doc into a standalone, shareable page,
/// plus the shared base theme both the live server and the export inject. Everything here is a pure
/// string transform with no I/O and no project dependencies, so the whole export path is
/// unit-testable without a server, a filesystem, or a network.
///
/// The on-disk `.agents/canvas/<file>.html` contains NONE of the serve-time injected scripts (bridge
/// heartbeat, canvasSend, idiomorph/morph runtime, error overlay): `CanvasDocServer` adds those only
/// while serving at :5002. A *published* copy is opened in a plain browser with no pane and no
/// bridge, so the export does the OPPOSITE of stripping — it re-injects exactly the two pieces a
/// standalone copy needs and nothing else:
///   1. the base theme `<style>` (so a doc that leaned on the injected theme still renders on-theme), and
///   2. a NO-OP `window.canvasSend` (so author buttons that call `canvasSend(...)` do nothing instead
///      of throwing `ReferenceError`; a raw `window.parent.postMessage` already degrades to a harmless
///      self-post in a top-level window).
/// It injects NONE of the pane-only machinery (bridge heartbeat, idiomorph runtime, morph controller,
/// error overlay): those only make sense inside the live pane and would be dead weight — or throw —
/// standalone.
module Server.CanvasExport

open System
open System.Text.RegularExpressions

/// Scrollbar styling + an opinionated dark-theme typography base — the SHARED base theme.
/// `CanvasDocServer.buildInjection` injects it into every LIVE-served doc (both kinds) at the
/// `</head>` slot, and `buildStaticHtml` re-injects the identical string into a published copy so a
/// doc that leaned on the injected theme renders on-theme standalone. This is what lets a plain doc
/// render on-theme and *readable* (type scale + whitespace, not boxes) with zero authored CSS; see
/// src/Extension/skill/SKILL.md.
///
/// Cascade safety: the injection lands AFTER any <head> styles the doc/template already declares, so
/// an equal-specificity element rule here would win the source-order tiebreak and stomp the doc.
/// Every selector is therefore wrapped in :where(...) — carrying ZERO specificity like the universal
/// `*` scrollbar rule — and no rule uses !important. Then any real doc rule (even a bare body{}) and
/// the SystemView template's own element-SELECTOR rules (e.g. BeadspaceTemplate.html's
/// body{background:var(--bg-deep);margin:0;padding:0}, specificity 0,0,1) override the reset
/// (specificity 0,0,0) regardless of source order. The override is per-property and only via a rule on
/// the element selector: a box property the template zeroes through the universal `*{margin:0;padding:0}`
/// reset (also 0,0,0) would otherwise LOSE the source-order tiebreak to the later-injected
/// :where(body){padding:2rem}, so the template resets margin/padding on `body` directly to keep its
/// padding:0. The design tokens are likewise wrapped in :where(:root) so a doc's own :root{--x} wins.
///
/// Values are grounded, not invented: 15px / line-height 1.55 (WCAG 1.4.12 requires surviving 1.5);
/// a hand-tuned serif type scale (h1 1.85rem / h2 1.35rem / h3 1.12rem / h4 1rem); a ~800px single-column
/// page (--page-max) where text and figures share one width. Tokens mirror the app palette (BeadspaceTemplate.html) — names
/// and values — except --text-muted is nudged lighter (#9399b2 vs the dashboard's denser #6c7086) to
/// clear WCAG AA contrast on long-form reading — so docs reference var(--text-muted)/var(--accent)/…
/// instead of reinventing a palette. Tables default to a quiet bottom-border style and callouts to a
/// semantic blockquote — steering away from heavy boxes. Form controls (button/textarea/input/select)
/// are themed too, so the common "collect input" doc needs no control CSS.
let baseStyle = "<style>*{scrollbar-width:thin;scrollbar-color:rgba(88,91,112,.5) transparent}::-webkit-scrollbar{width:8px;height:8px}::-webkit-scrollbar-track{background:transparent}::-webkit-scrollbar-thumb{background:rgba(88,91,112,.5);border-radius:4px}::-webkit-scrollbar-thumb:hover{background:rgba(88,91,112,.8)}:where(:root){--bg-deep:#1e1e2e;--bg-surface:#181825;--bg-elevated:#313244;--border:#45475a;--border-bright:#585b70;--text-primary:#cdd6f4;--text-secondary:#bac2de;--text-muted:#9399b2;--accent:#cba6f7;--accent-bright:#b4befe;--status-wip:#f59e0b;--status-blocked:#ef4444;--status-closed:#22c55e;--serif:ui-serif,Georgia,'Times New Roman',Times,serif;--diagram-max:900px;--page-max:800px}:where(body){background:var(--bg-deep);color:var(--text-primary);font-family:system-ui,-apple-system,'Segoe UI',sans-serif;font-size:15px;line-height:1.55;margin:0 auto;padding:2rem 2.25rem;max-width:var(--page-max)}:where(p,ul,ol,pre,table,blockquote){margin:0 0 1.2rem}:where(li){margin:0 0 .3rem}:where(h1,h2,h3,h4,h5,h6){font-family:var(--serif);font-weight:500;line-height:1.15;letter-spacing:-.012em;margin:2.4rem 0 .5rem}:where(h1){font-size:1.85rem;line-height:1.1;letter-spacing:-.018em;margin-top:0}:where(h2){font-size:1.35rem}:where(h3){font-size:1.12rem}:where(h4){font-size:1rem}:where(body>:first-child){margin-top:0}:where(h1+*,h2+*,h3+*,h4+*){margin-top:0}:where(a){color:var(--accent);text-decoration:none}:where(a:hover){text-decoration:underline}:where(button){font:inherit;color:var(--text-primary);background:var(--bg-elevated);border:1px solid var(--border-bright);border-radius:6px;padding:.5em 1em;cursor:pointer}:where(button:hover){background:var(--border)}:where(textarea,input,select){font:inherit;color:var(--text-primary);background:var(--bg-elevated);border:1px solid var(--border);border-radius:6px;padding:.5em .6em}:where(textarea){display:block;width:100%;box-sizing:border-box;resize:vertical}:where(small){color:var(--text-muted)}:where(blockquote){margin:1.2em 0;padding:.2em 0 .2em 1em;border-left:3px solid var(--border);color:var(--text-secondary)}:where(hr){border:none;border-top:1px solid var(--border);margin:1.6em 0}:where(code,kbd,samp,pre){font-family:'Consolas','Courier New',monospace}:where(code){background:var(--bg-elevated);padding:.1em .3em;border-radius:4px;font-size:.9em}:where(pre){background:var(--bg-surface);padding:1em;border-radius:6px;overflow:auto}:where(pre code){background:none;padding:0;font-size:inherit}:where(table){border-collapse:collapse}:where(th,td){border-bottom:1px solid var(--border);padding:.5em .7em;text-align:left;vertical-align:top}:where(th){color:var(--text-secondary);font-size:.8em;text-transform:uppercase;letter-spacing:.04em}:where(svg){max-width:100%;height:auto}:where(figure){max-width:min(100%,var(--diagram-max));margin:0 auto 1.2rem}:where(figure svg,figure img){display:block;max-width:100%;height:auto;margin-inline:auto}:where(figcaption){color:var(--text-muted);font-size:.9em;text-align:center;margin-top:.5em}</style>"

/// The static export's inert `window.canvasSend`. The live server injects a REAL helper
/// (`CanvasSendScript.script`) that posts the doc→pane message and size-checks it; a
/// published doc has no pane to post to, so the export injects this no-op stand-in instead. Its only
/// job is to keep author buttons that call `canvasSend(...)` from throwing `ReferenceError`; it
/// ignores its arguments and returns `false` (nothing was delivered), mirroring the real helper's
/// "dropped" return so author code that branches on the result sees a not-delivered, not a crash.
let private noopCanvasSendScript = "<script>window.canvasSend=function(){return false}</script>"

/// The complete static-export injection: the shared base theme + the inert `canvasSend`, and nothing
/// else. Deliberately omits every pane-only script (bridge heartbeat, idiomorph runtime, morph
/// controller, error overlay).
let private staticInjection = baseStyle + noopCanvasSendScript

/// Inject `injection` at the doc's `</head>` (case-insensitive), mirroring
/// `CanvasDocServer.handleCanvasRequest`: splice it in immediately before the closing head tag, or —
/// when the doc has no `</head>` at all — prepend it to the whole document. `CanvasDocServer` calls
/// this same function for its live injection, so the live and exported placement can never drift.
let injectAtHead (injection: string) (html: string) : string =
    if html.Contains("</head>", StringComparison.OrdinalIgnoreCase) then
        html.Replace("</head>", injection + "</head>", StringComparison.OrdinalIgnoreCase)
    else
        injection + html

/// Turn an on-disk canvas doc into a standalone, shareable page: re-inject the shared base theme +
/// the inert `canvasSend` at `</head>` (or prepend when there is no `</head>`), and nothing else.
/// Pure `string -> string` so the export is unit-testable without a server.
let buildStaticHtml (html: string) : string = injectAtHead staticInjection html

/// The doc's `<title>` text — the first `<title>…</title>` (case-insensitive), HTML-entity-decoded
/// and whitespace-collapsed the way a browser renders a title — or `None` when the doc has no title
/// or the title is blank. Used to label the rich clipboard link; the caller re-encodes per clipboard
/// format, so this returns the logical (decoded) text rather than the raw markup.
let extractTitle (html: string) : string option =
    let m = Regex.Match(html, @"<title\b[^>]*>(.*?)</title>", RegexOptions.IgnoreCase ||| RegexOptions.Singleline)
    if not m.Success then None
    else
        let text = Regex.Replace(System.Net.WebUtility.HtmlDecode m.Groups[1].Value, @"\s+", " ").Trim()
        if text = "" then None else Some text

/// The share title the spec's clipboard link uses: the doc's `<title>`, falling back to a prettified
/// filename (`Shared.Formatting.prettifyFilename`) when the doc has none. Single source of truth for
/// the `extractTitle`→prettified-filename fallback so the server (which returns the title to the
/// client) and any other caller resolve it identically.
let resolveTitle (html: string) (filename: string) : string =
    extractTitle html |> Option.defaultWith (fun () -> Shared.Formatting.prettifyFilename filename)
