# Canvas Doc Sharing

## Goals

- One-click Share of a focused canvas doc to a clean, unguessable URL that a recipient opens in a
  plain browser after signing in with Microsoft Entra. No SAS, account key, or other bearer
  credential is ever generated or returned to the recipient.
- Real authorization, not just secrecy: the URL's opaque segment narrows which document a
  signed-in identity can reach, but Entra sign-in (and any enterprise-app assignment) is what
  actually gates access. A leaked link alone is not sufficient to view the document.
- A bounded, view-time-enforced lifetime: a document stops being viewable at its configured expiry
  even if the underlying blob has not yet been swept up by storage lifecycle cleanup.
- Contained interactivity: the recipient still sees the document's live HTML/JS rather than a
  static/PDF downgrade, but that script runs in a sandbox that cannot reach the viewer's
  authenticated origin, read its cookies, or exfiltrate over the network.
- A rich, titled hyperlink on the clipboard so paste targets (chat, mail, docs) show a meaningful
  link regardless of the URL's actual length or shape.

## Expected Behavior

### Share action

- A Share button appears in the canvas tab bar next to Archive, for `AgentDoc` docs only (a
  `SystemView` such as the beads dashboard is server-generated and not shareable).
- Clicking it static-exports the focused doc, uploads it to the private Blob container, records an
  expiry, and returns a clean viewer URL; the client then writes the rich clipboard payload and
  shows a success banner (`Shared -- link copied`). A failure at any stage reuses the existing
  dismissible error banner.
- The button shows progress and refuses re-entry while a share is in flight: `CanvasState.ShareState`
  records the scoped worktree/doc and the `Publishing` or `WritingClipboard` phase, every Share
  button is disabled while that state is non-idle, only the matching scoped doc shows the spinner,
  and results only transition or clear the matching operation -- so navigation or a stale async
  completion cannot unlock or overwrite a newer share (locked by `ShareCanvasDocResultTests`).
- Sharing operates on a single, self-contained doc. Docs that link to sibling `.html` tabs are
  shared as just the focused file; those links remain inert in the exported copy, unchanged from
  today.

### Static export

- The static export (`CanvasExport.buildStaticHtml`) is unchanged: the on-disk
  `.agents/canvas/<file>.html` is already free of the serve-time injected scripts (bridge heartbeat,
  `canvasSend`, idiomorph/morph, error overlay), so the export re-injects only the shared base theme
  `<style>` and a no-op `window.canvasSend` at `</head>` (or prepends when there is no `</head>`),
  via the same `injectAtHead` helper `CanvasDocServer` uses for live-served docs. It injects nothing
  else: no bridge, no idiomorph/morph runtime, no error overlay, and no same-origin link
  interceptor, since a published copy has no pane to route to.

### Recipient viewing

- The link opens the dedicated App Service viewer, never Treemon and never Azure Blob Storage
  directly. Its canonical base URL is `https://treemon.azurewebsites.net`, using the TLS-enabled
  hostname supplied by Azure App Service; no custom domain or DNS setup is required. A complete
  link has the shape
  `https://treemon.azurewebsites.net/c/<opaque-prefix>/<filename>.html`.
  App Service Easy Auth requires Microsoft Entra sign-in before any application code runs; an
  unauthenticated visitor is redirected to the tenant's sign-in page.
- Once signed in, the viewer validates both URL segments, composes the blob name from them
  (`<opaque-prefix>/<filename>`, an exact lookup -- it never lists or prefix-searches the
  container, so the URL cannot be used to enumerate other shares), checks the expiry the publisher
  wrote as blob metadata, and renders the document only when the blob exists, the metadata is
  well-formed, and the expiry has not passed.
- Missing, malformed, and expired share paths return the same generic not-found response. Easy
  Auth handles unauthenticated or unassigned identities before application code runs and never
  reveals whether the requested share exists.
- The rendered page is a minimal shell that embeds the document itself from a separate content
  route inside a sandboxed iframe (see Technical Approach); the shell carries no document content
  and no privileged API of its own.

### Clipboard

- Clipboard behavior is unchanged: on success the client writes both `text/html` (a titled
  `<a href>` using the doc's `<title>`, falling back to a prettified filename) and `text/plain` (the
  raw URL) via the async Clipboard API, and the outcome is routed back through
  `ClipboardWriteResult` rather than assumed -- the banner reads "copied" only once the write
  lands, and otherwise falls back to `Shared -- link ready, copy it manually: <url>` with the URL
  shown as selectable text. What changed is only the shape of the URL itself: a clean `/c/...`
  viewer path instead of a blob URL with a SAS query string.

### Configuration

- The `canvasShare` section of the machine-level Treemon config (`~/.treemon/config.json`) keeps
  `accountName`, `container`, and `defaultExpiryDays`, and adds `viewerBaseUrl` -- the viewer App
  Service's HTTPS base URL. All four are ordinary non-secret settings; none of them, and no Entra
  tenant/client/resource identifier or secret, ships as a value in the repository's defaults.
  `accountName` and `viewerBaseUrl` have no default, so their absence means the feature is
  unconfigured; the canonical deployed value of `viewerBaseUrl` is
  `https://treemon.azurewebsites.net`. An unconfigured Share action still fails with a clear
  `Result.Error` before any network call.
- `defaultExpiryDays` remains 7 and `maxCanvasShareExpiryDays` is 30. The share container's Blob
  lifecycle policy deletes only after 31 days or more, so cleanup never removes a document the
  viewer would still have served.
- The viewer reads its own required, non-secret ASP.NET Core settings from
  `CanvasShareViewer:StorageAccountName` and `CanvasShareViewer:ShareContainer` (App Service
  environment names use `CanvasShareViewer__StorageAccountName` and
  `CanvasShareViewer__ShareContainer`). The repository carries only blank placeholders.

## Technical Approach

### Publisher (`src/Server`, unchanged project)

- `CanvasShare` uploads the exported HTML directly to the pre-provisioned private Blob container
  using the same cached, delegated `AzureCliCredential`-backed identity as today. It writes the
  share's expiry as blob metadata and returns a `CanvasShareResult` built from `viewerBaseUrl` plus
  the blob's existing unguessable-prefix-plus-filename naming (`<opaque-prefix>/<filename>`); it
  never mints or returns a SAS.
- `WorktreeApi.shareCanvasDocImpl` keeps its existing pipeline (validate path, read file, export,
  publish) behind the same `withValidatedPath` guard that every other write method uses (mirroring
  `archiveCanvasDoc`), and the demo-mode stub keeps returning `Error "... not available in demo
  mode"`.
- `ShareCanvasDocRequest` and `CanvasShareResult` keep their existing shape (`WorktreePath`/
  `Filename` in, `Url`/`Title` out); only the value and format of `Url` changes.
- The Treemon server itself is unchanged: it stays bound to loopback and is never exposed to the
  internet. Its shared Remoting `HttpSecurity.csrfGuard` (Origin/Referer allowlist, one
  pipeline-level guard over every `IWorktreeApi` method) continues to gate `shareCanvasDoc` exactly
  as it gates every other state-changing endpoint.

### Viewer (new project, `src/CanvasShareViewer/`)

- A small ASP.NET Core F# application, deployed to its own Azure App Service, is the only
  internet-facing component this feature adds.
- Easy Auth is configured for the workforce, current-tenant, single-tenant Entra registration with
  authentication required at the platform level, so an unauthenticated request never reaches
  application code. A narrower enterprise-application assignment (specific users or groups) can
  constrain the audience below "whole tenant" where needed. The registration authenticates to Easy
  Auth via a managed-identity federated credential rather than a long-lived client secret.
- Two routes divide responsibility: a shell route (`/c/<opaque-prefix>/<filename>`) validates the
  request and expiry and renders a minimal HTML page; a content route
  (`/c/<opaque-prefix>/<filename>/content`) streams the document bytes and is the only thing the
  shell's iframe loads. Keeping them separate lets the content response carry a much stricter
  policy than the shell needs.
- Each route re-validates the segments and re-checks expiry against blob metadata on its own; the
  content route never trusts that the shell already checked. Otherwise the content route would be
  an unguarded bypass for an expired or malformed share whose URL the recipient still holds.
- A matched route performs one exact read before it collapses path validity, Blob existence, and
  expiry into the single not-found outcome: a valid path reads its exact `<prefix>/<filename>`,
  while malformed segments read one fixed, non-servable probe name so untrusted dot segments never
  reach Blob URI construction. This keeps malformed, missing, and expired requests on the same
  application-level ordering.
- The shell's iframe uses `sandbox="allow-scripts"` only -- it omits `allow-same-origin`,
  `allow-forms`, `allow-popups`, and `allow-top-navigation`, so the embedded document's script can
  run but cannot read the viewer's cookies or storage, submit forms, open popups, or navigate the
  parent frame.
- The content route's response carries a restrictive Content-Security-Policy that blocks outbound
  network/fetch/form targets, plus `X-Content-Type-Options: nosniff` and a strict
  `Referrer-Policy`, so script that does run cannot exfiltrate over the network or leak the
  referrer. Its CSP includes a `sandbox allow-scripts` directive, so the document stays sandboxed
  even when an authenticated recipient opens the content URL directly at top level rather than
  through the shell's iframe.
- The viewer's managed identity is granted read-only (`Storage Blob Data Reader`) access scoped to
  the share container only -- it can read published blobs and their metadata, and nothing else.
- Expiry is enforced synchronously on every request against the metadata the publisher wrote. The
  account's Blob lifecycle policy still runs, but only as eventual cleanup after the access
  deadline, not as the authorization boundary.
- Easy Auth's token store is disabled and no downstream Graph scopes are requested; the viewer
  origin exposes no upload/delete/admin API -- it is read-only, with only the shell and content
  routes.

### Wire contract (publisher <-> viewer)

The publisher and the viewer are separate projects that never call each other, so these are the
only things they must agree on. They are pinned here rather than discovered per side.

| Item | Value |
|---|---|
| Expiry metadata | Blob metadata key `expiresOn`, value ISO-8601 UTC round-trip (`DateTimeOffset` `"o"`). A value that is absent or unparseable is malformed, not "never expires". |
| Opaque prefix segment | Exactly `CanvasShare.PrefixLength` (22) base62 characters (`[0-9A-Za-z]`). |
| Filename segment | One path segment ending `.html`; no `/`, `\`, or `..`. This is the publisher's `leafName` output. |
| Not-found response | HTTP 404 with one fixed, content-free body, byte-identical for malformed, missing, and expired shares -- no header, status, or timing distinguishes them. |

Response headers, by route:

| Route | Headers |
|---|---|
| Shell | `Content-Security-Policy: default-src 'none'; style-src 'unsafe-inline'; frame-src 'self'; form-action 'none'; base-uri 'none'`, `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`, `Cache-Control: no-store` |
| Content | `Content-Security-Policy: default-src 'none'; script-src 'unsafe-inline' 'unsafe-eval'; style-src 'unsafe-inline'; img-src data:; font-src data:; media-src data:; connect-src 'none'; form-action 'none'; frame-src 'none'; object-src 'none'; base-uri 'none'; frame-ancestors 'self'; sandbox allow-scripts`, `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`, `Cache-Control: no-store` |

`script-src`/`style-src` allow inline because a self-contained canvas doc *is* inline script and
style. `unsafe-eval` preserves existing support for documents that use `eval` or `new Function`;
the opaque-origin sandbox and network-denying directives remain the security boundary.
`img-src data:`/`font-src data:`/`media-src data:` keep embedded assets working while denying the
remote-URL fetch that would otherwise be a working exfiltration channel.

### Provisioning

- Provisioning targets the personal development Azure subscription and an isolated,
  non-production resource group. Subscription, tenant, resource-group, plan, identity, and
  registration names are operator inputs; the App Service name is `treemon`, producing
  `https://treemon.azurewebsites.net`.
- Before first creation, provisioning checks global App Service name availability and fails
  clearly if `treemon` is no longer available. It never silently appends a random suffix because
  that would change the durable shared-link origin and its browser SSO session.
- The publisher keeps its existing delegated Entra/Azure CLI identity and
  `Storage Blob Data Contributor` grant. The viewer uses a managed identity with the new read-only
  grant scoped to the share container.
- The canonical App Service, identity, Entra configuration, RBAC grants, and lifecycle policy
  remain deployed after verification. Verification removes only its document fixtures and any
  auxiliary resources created solely to prove the permission boundary.
- Feature development, deployment, and verification never run a production lifecycle command
  (`treemon.ps1 deploy`/`start`/`stop`/`restart`) and never bind to or otherwise disturb the
  production instance on port 5000.

## Security Posture

- No bearer credential ever reaches the recipient's browser. The opaque path segment is an
  identifier, not an authorization grant; Entra sign-in plus the viewer's own expiry check are what
  authorize a view.
- The embedded document is contained by iframe sandboxing (script execution allowed; same-origin,
  forms, popups, and top-navigation denied) and a restrictive content CSP. This replaces the
  previous posture of serving canvas exports as unsandboxed, top-level active HTML.
- The content route's CSP and security headers apply regardless of authentication state, so even
  script that does execute inside the sandbox cannot reach the network or leak referrer
  information.
- Missing, malformed, and expired share paths are indistinguishable to an authenticated caller.
  Easy Auth rejects unauthenticated or unassigned identities without revealing path existence.
- The Remoting CSRF guard continues to protect the publish call itself: a forged cross-origin
  `shareCanvasDoc` request from the operator's browser is rejected before any Azure I/O, the same as
  every other `IWorktreeApi` state-changing endpoint.

## Decisions

| Decision | Rationale |
|---|---|
| Keep the existing private Blob container as backing storage rather than App Service's own storage | Minimizes change to the proven publish path and keeps the existing lifecycle policy and publisher RBAC grant intact. |
| Split the viewer into a shell route and a separate content route | Lets the content response carry a much stricter CSP than the shell page needs, and gives the iframe a distinct `src` resource. |
| Sandbox the content iframe without `allow-same-origin` | Granting it would hand the embedded document the viewer's authenticated origin (cookies, Easy Auth session) even though it also has script execution. |
| Enforce expiry in the viewer at request time rather than relying on Blob lifecycle deletion | Lifecycle deletion runs on a daily-ish schedule and is a backstop; relying on it alone would leave documents readable past their promised expiry. |
| Prefer a managed-identity federated credential over an Easy Auth client secret | Avoids minting, storing, or rotating a long-lived secret for the viewer's app registration. |
| Store expiry as blob metadata rather than a separate data store | Keeps the expiry attached to the artifact it governs, with no second store to keep in sync; it travels and disappears with the blob. |
| Re-check segments and expiry on the content route instead of trusting the shell | The recipient holds the URL, so the content route is directly reachable; a shell-only check would leave an expired share readable by editing the path. |
| Put `sandbox allow-scripts` in the content route's CSP as well as on the iframe | The iframe attribute only covers the embedded case; the CSP directive also covers a signed-in recipient opening the content URL at top level, where the document would otherwise run on the viewer's authenticated origin. |
| Look the blob up by exact composed name, never by listing or prefix search | A share URL then reveals only its own document; no reachable code path can turn one link into an inventory of the container. |
| Allow `unsafe-eval` only inside the contained document response | Shared canvases already support arbitrary inline JavaScript; preserving `eval`/`new Function` compatibility does not grant viewer-origin or network access because the sandbox and remaining CSP directives still deny both. |
| Use `treemon.azurewebsites.net` rather than a custom domain or generated suffix | The Azure-provided hostname is short, TLS-enabled, requires no DNS ownership, and gives every shared document one stable origin for browser SSO. |

## Key Files

| File | Purpose |
|---|---|
| `src/Shared/Types.fs` | `ShareCanvasDocRequest`, `CanvasShareResult`, `IWorktreeApi.shareCanvasDoc` (shape unchanged) |
| `src/Server/CanvasExport.fs` | Static export transform: base theme + no-op `canvasSend`; `extractTitle` / `resolveTitle` (unchanged) |
| `src/Server/CanvasShare.fs` | Blob upload, expiry metadata, and clean viewer-URL construction; no SAS |
| `src/Server/WorktreeApi.fs` | `shareCanvasDocImpl` + `withValidatedPath` wiring + demo-mode stub (unchanged) |
| `src/Server/GlobalConfig.fs` | `canvasShare` config: `accountName`, `container`, `defaultExpiryDays`, `viewerBaseUrl` |
| `src/Server/HttpSecurity.fs` | Shared Remoting CSRF guard covering `shareCanvasDoc` (unchanged) |
| `src/Client/CanvasPane.fs`, `CanvasState.fs`, `CanvasUpdate.fs`, `index.html` | Share button, `ShareState` phase machine, clipboard write and banner routing (unchanged) |
| `src/CanvasShareViewer/` | New App Service viewer: shell route, content route, expiry check, sandbox/CSP, Easy Auth configuration |

## Verification

- The copied URL is clean: no SAS, signature, or other query-string token of any kind.
- The deployed viewer and every returned share URL use
  `https://treemon.azurewebsites.net`; provisioning never substitutes a suffixed hostname.
- Entra redirect/allow/deny: an anonymous request redirects to sign-in; an allowed tenant identity
  views the document; an unassigned or external identity is denied.
- The viewer's managed identity can read the share container via Blob storage and nothing beyond
  it.
- A document is denied immediately once its metadata expiry has passed, before any lifecycle
  deletion runs.
- The content route enforces the same checks as the shell: an expired or malformed share is denied
  on the content route too, and a document opened directly at the content URL is still sandboxed.
- Deleting or clearing a document's backing blob denies its link immediately (revocation).
- A hostile fixture attempting cookie/storage access, same-origin fetches, form submission,
  popups, top navigation, and network exfiltration all fail inside the sandboxed iframe and CSP,
  while intended self-contained document scripting still works.
- Existing share UI/clipboard behavior -- AgentDoc-only button gating, `ShareState` lock and
  spinner, and clipboard-outcome banner routing -- continues to pass unchanged.
- The actual secret detector is run against a clean viewer URL and does not flag it.

## Related Specs

- `docs/spec/canvas-pane.md` -- the canvas pane the Share button lives in (tab bar, AgentDoc vs
  SystemView, archive precedent)
- `docs/spec/canvas-interaction-routing.md` -- per-doc ownership/routing (the Share button is
  AgentDoc-scoped like liveness/archive)
- `docs/spec/remoting-csrf-hardening.md` -- the pipeline-level Origin/Referer guard that fronts
  `shareCanvasDoc` and the rest of the Remoting surface
