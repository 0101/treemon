# Canvas Doc Sharing

## Goals

- One-click Share of a focused canvas doc to a clean, unguessable URL that a recipient opens in a
  plain browser after signing in with Microsoft Entra. No SAS, account key, or other bearer
  credential is ever generated or returned to the recipient.
- Real authorization, not just secrecy: the URL's opaque segment narrows which document a
  signed-in identity can reach, but Entra sign-in is what actually gates access. A link leaked
  outside the tenant is not sufficient to view the document; inside the tenant, the audience is
  every authenticated member and B2B guest holding it.
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
- Before reading the source file or contacting Azure, the publisher requires a non-empty filename
  segment with no path separator and an `.html` suffix matched case-insensitively. The original
  filename casing is preserved for the exact Blob name and recipient URL; harmless consecutive
  dots within the segment are accepted.
- The button shows progress and refuses re-entry while a share is in flight: `CanvasState.ShareState`
  records the scoped worktree/doc and the `Publishing` or `WritingClipboard` phase, every Share
  button is disabled while that state is non-idle, only the matching scoped doc shows the spinner,
  and results only transition or clear the matching operation -- so navigation or a stale async
  completion cannot unlock or overwrite a newer share (locked by `ShareCanvasDocResultTests`).
- Share and canvas-path copy are mutually exclusive clipboard workflows. The reducer rejects either
  action while the other owns the clipboard, and both controls are disabled until that workflow
  settles, so their async results cannot overwrite the pane-global error or clipboard notice state.
- Sharing operates on a single, self-contained doc. Docs that link to sibling `.html` tabs are
  shared as just the focused file; those links are inert in the exported copy.

### Static export

- The static export (`CanvasExport.buildStaticHtml`) starts from the on-disk
  `.agents/canvas/<file>.html`, which is free of the serve-time injected scripts (bridge heartbeat,
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
  Auth handles unauthenticated identities before application code runs and never reveals whether
  the requested share exists.
- A `BlobNotFound` 404 is the only dependency outcome treated as a missing share. Container or
  account failures, storage throttling, authorization, service, and managed-credential failures
  before a response starts return one fixed, empty 503 with restrictive response headers in every
  runtime environment. A failure after document streaming starts aborts the response because its
  status and partial body can no longer be replaced.
- The audience is tenant-wide: any identity the tenant issuer authenticates -- a current-tenant
  user or an invited B2B guest -- may view a share whose link it holds. Enterprise-application
  assignment is deliberately not required, so possession of the link plus a tenant sign-in is the
  access boundary; identities outside the tenant are denied.
- The rendered page is a minimal shell that embeds the document itself from a separate content
  route inside a sandboxed iframe (see Technical Approach); the shell carries no document content
  and no privileged API of its own.
- The content route returns active HTML only for a fail-closed browser navigation whose Fetch
  Metadata identifies a same-origin iframe (`Sec-Fetch-Site: same-origin`,
  `Sec-Fetch-Mode: navigate`, and `Sec-Fetch-Dest: iframe`, each as one header value). A direct or
  top-level navigation, a cross-site iframe, or a request with partial, duplicated, or missing
  metadata receives the normal non-executable shell after its own path/expiry check. That shell's
  sandboxed iframe then supplies the expected tuple in a Fetch-Metadata-capable browser, so a
  recipient who opens `/content` directly still sees the document without ever executing it in the
  top-level response. A browser or intermediary that omits Fetch Metadata from the iframe request
  fails closed and does not render the active document; there is no headerless compatibility
  bypass.

### Clipboard

- On success the client writes both `text/html` (a titled
  `<a href>` using the doc's `<title>`, falling back to a prettified filename) and `text/plain` (the
  raw URL) via the async Clipboard API, and the outcome is routed back through
  `ClipboardWriteResult` rather than assumed -- the banner reads "copied" only once the write
  lands, and otherwise falls back to `Shared -- link ready, copy it manually: <url>` with the URL
  shown as selectable text. The copied URL is a clean `/c/...` viewer path with no SAS query.

### Configuration

- The `canvasShare` section of the machine-level Treemon config (`~/.treemon/config.json`) contains
  `accountName`, `container`, `defaultExpiryDays`, and `viewerBaseUrl` -- the viewer App Service's
  HTTPS base URL. All four are ordinary non-secret settings; none of them, and no Entra
  tenant/client/resource identifier or secret, ships as a value in the repository's defaults.
  `accountName` and `viewerBaseUrl` have no default, so their absence means the feature is
  unconfigured; the canonical deployed value of `viewerBaseUrl` is
  `https://treemon.azurewebsites.net`. The URL must be an HTTPS origin whose parsed path is exactly
  `/`, with no user info, query, or fragment; path-based viewer URLs are rejected because the
  deployed viewer serves `/c/...` only at the origin root. An unconfigured Share action still
  fails with a clear `Result.Error` before any network call.
- `defaultExpiryDays` is 7 and `maxCanvasShareExpiryDays` is 30. The share container's Blob
  lifecycle policy deletes only after 31 days or more, so cleanup never removes a document the
  viewer would still have served.
- The viewer reads its own required, non-secret ASP.NET Core settings from
  `CanvasShareViewer:StorageAccountName` and `CanvasShareViewer:ShareContainer` (App Service
  environment names use `CanvasShareViewer__StorageAccountName` and
  `CanvasShareViewer__ShareContainer`). The repository carries only blank placeholders.

## Technical Approach

### Publisher (`src/Server`)

- `CanvasShare` uploads the exported HTML directly to the pre-provisioned private Blob container
  using the same cached, delegated `AzureCliCredential`-backed identity as today. It writes the
  share's expiry as blob metadata and returns a `CanvasShareResult` built from `viewerBaseUrl` plus
  the blob's existing unguessable-prefix-plus-filename naming (`<opaque-prefix>/<filename>`); it
  never mints or returns a SAS.
- `WorktreeApi.shareCanvasDocImpl` applies `CanvasShare.validateFilename` before path validation or
  file access, then runs the read, export, and publish pipeline behind the same
  `withValidatedPath` guard that every other write method uses (mirroring `archiveCanvasDoc`).
  `CanvasShare.publish` repeats that validation at the upload boundary before configuration or
  Azure work, and the demo-mode stub returns `Error "... not available in demo mode"`.
- `ShareCanvasDocRequest` carries `WorktreePath` and `Filename`; `CanvasShareResult` carries `Url`
  and `Title`.
- The Treemon server stays bound to loopback and is never exposed to the internet. Its shared
  `HttpSecurity.csrfGuard` gates `shareCanvasDoc` with every other state-changing endpoint.

### Viewer (`src/CanvasShareViewer/`)

- A small ASP.NET Core F# application on its own Azure App Service is the feature's only
  internet-facing component.
- Easy Auth is configured for the workforce, current-tenant, single-tenant Entra registration with
  authentication required at the platform level, so an unauthenticated request never reaches
  application code. Assignment is not required on the enterprise application: every identity the
  tenant authenticates, including B2B guests, passes the gate. The registration authenticates to
  Easy Auth via a managed-identity federated credential rather than a long-lived client secret.
  The registration enables ID-token issuance because Easy Auth's browser callback requests
  `response_type=code id_token` with `response_mode=form_post`; browser access-token issuance
  remains disabled. Easy Auth explicitly requests only `openid`, the minimum scope needed for the
  ID token and stable subject identifier; the viewer requests neither profile nor email claims.
  The registration is named **Treemon Canvas Viewer**.
- Two routes divide responsibility: a shell route (`/c/<opaque-prefix>/<filename>`) validates the
  request and expiry through an exact Blob properties lookup and renders a minimal HTML page
  without downloading the document body; a content route
  (`/c/<opaque-prefix>/<filename>/content`) performs the only body-bearing lookup and streams that
  Blob response directly to the HTTP response. Keeping them separate lets the content response
  carry a much stricter policy than the shell needs while bounding application memory independently
  of document size.
- Each route re-validates the segments and re-checks expiry against blob metadata on its own; the
  content route never trusts that the shell already checked. Otherwise the content route would be
  an unguarded bypass for an expired or malformed share whose URL the recipient still holds.
- A matched route collapses path validity, Blob existence, and expiry into the single not-found
  outcome. A valid shell path and a content request rejected by the Fetch Metadata gate each
  perform one exact `GetPropertiesAsync` call; an accepted same-origin iframe content request
  performs one exact body read of `<prefix>/<filename>`. Malformed segments resolve to not-found
  before any storage access, so untrusted dot segments never reach Blob URI construction. Every
  not-found path within its selected shell/content response policy emits the identical response
  through the same application-level ordering; only elapsed time may differ.
- An exception boundary registered before routing handles non-404 Azure Storage failures and
  `DefaultAzureCredential` failures independently of the ASP.NET Core environment. It logs only
  the exception type and available Azure status/error code. Before headers are committed it clears
  the route response and emits the fixed dependency-failure response; after streaming begins it
  aborts the connection instead of attempting to replace a partial response.
- The shell's iframe uses `sandbox="allow-scripts"` only -- it omits `allow-same-origin`,
  `allow-forms`, `allow-popups`, and `allow-top-navigation`, so the embedded document's script can
  run but cannot read the viewer's cookies or storage, submit forms, open popups, or navigate the
  parent frame.
- The shell's `frame-src 'self'` is also part of containment: the sandbox permits the child to
  navigate its own browsing context, while the ancestor's CSP blocks that self-navigation from
  reaching an external origin. Chromium may replace the child with its local CSP error document
  after such an attempt; no external request is sent.
- The content endpoint checks Fetch Metadata before choosing a storage lookup or response policy.
  Only a request with the single-value same-origin/navigate/iframe tuple reaches the body-bearing
  lookup and active-content response. Every other request fails closed to the shell policy and
  properties-only lookup. A normal shell load therefore remains one properties lookup followed by
  one iframe body read in supported browsers, while a direct `/content` load becomes that same
  contained two-request sequence instead of executing the document at top level.
- The content route's response carries a restrictive Content-Security-Policy that blocks outbound
  network/fetch/form targets, plus `X-Content-Type-Options: nosniff` and a strict
  `Referrer-Policy`, so script that does run in the iframe cannot exfiltrate over those channels or
  leak the referrer. Its CSP also includes `sandbox allow-scripts` as defense in depth for the
  iframe's opaque-origin sandbox; top-level containment relies on the fail-closed Fetch Metadata
  gate because a sandboxed top-level document can still navigate its own browsing context.
- The viewer's managed identity is granted read-only (`Storage Blob Data Reader`) access scoped to
  the share container only -- it can read published blobs and their metadata, and nothing else.
- Expiry is enforced synchronously on every request against the metadata the publisher wrote. The
  account's Blob lifecycle policy still runs, but only as eventual cleanup after the access
  deadline, not as the authorization boundary.
- Easy Auth's token store is disabled and no downstream Graph or other API permissions are
  declared. Microsoft Entra may still show its generic "Maintain access" consent text because
  `offline_access` is implicit in delegated consent, but the authorization request omits that scope
  and the viewer cannot retain provider refresh tokens. The viewer origin exposes no
  upload/delete/admin API -- it is read-only, with only the shell and content routes.

### Wire contract (publisher <-> viewer)

The publisher and the viewer are separate projects that never call each other, so these are the
only things they must agree on. They are pinned here rather than discovered per side.

| Item | Value |
|---|---|
| Expiry metadata | Blob metadata key `expiresOn`, matched case-insensitively per Azure Blob metadata semantics, with an ISO-8601 UTC round-trip (`DateTimeOffset` `"o"`) value. A value that is absent or unparseable is malformed, not "never expires". |
| Opaque prefix segment | Exactly `CanvasShare.PrefixLength` (22) base62 characters (`[0-9A-Za-z]`). |
| Filename segment | One non-empty path segment ending `.html`, compared with `StringComparison.OrdinalIgnoreCase`; no `/` or `\`. Internal consecutive dots are allowed because they cannot traverse without a separator. The publisher preserves the original filename casing, URL composers percent-encode it as one segment, and Blob lookup uses that decoded casing exactly. |
| Not-found response | HTTP 404 with one fixed, content-free body, byte-identical for malformed, missing, and expired shares: identical status, headers, body, and response-emitting order. Elapsed time is not equalized -- a malformed path may be rejected before any storage access. |

Response headers, by route:

| Route | Headers |
|---|---|
| Shell, including a rejected `/content` navigation | `Content-Security-Policy: default-src 'none'; style-src 'unsafe-inline'; frame-src 'self'; form-action 'none'; base-uri 'none'; frame-ancestors 'none'`, `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`, `Cache-Control: no-store` |
| Active content (accepted same-origin iframe navigation only) | `Content-Security-Policy: default-src 'none'; script-src 'unsafe-inline' 'unsafe-eval'; style-src 'unsafe-inline'; img-src data:; font-src data:; media-src data:; connect-src 'none'; form-action 'none'; frame-src 'none'; object-src 'none'; base-uri 'none'; frame-ancestors 'self'; sandbox allow-scripts`, `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`, `Cache-Control: no-store` |
| Dependency failure (either route) | HTTP 503 with an empty body and `Content-Security-Policy: default-src 'none'; frame-ancestors 'none'; form-action 'none'; base-uri 'none'`, `X-Content-Type-Options: nosniff`, `Referrer-Policy: no-referrer`, `Cache-Control: no-store` |

`script-src`/`style-src` allow inline because a self-contained canvas doc *is* inline script and
style. `unsafe-eval` preserves existing support for documents that use `eval` or `new Function`;
the Fetch Metadata gate, opaque-origin iframe sandbox, and network-denying directives remain the
security boundary.
`img-src data:`/`font-src data:`/`media-src data:` keep embedded assets working while denying the
remote-URL fetch that would otherwise be a working exfiltration channel.

### Provisioning Contract

Provisioning is an attended, clean-slate agent operation rather than checked-in deployment
automation. The agent confirms the private subscription and tenant immediately before mutation and
fails if the requested resource group, B1 Linux plan, user-assigned managed identity, fixed-name
`treemon` App Service, or **Treemon Canvas Viewer** app registration already exists. The configured
publisher storage account is the sole pre-existing Azure prerequisite.

The resulting Azure state must satisfy all of these invariants:

- Blob public access is disabled. The configured container is private, the viewer identity has only
  `Storage Blob Data Reader` at that container, and the current publisher has
  `Storage Blob Data Contributor` at the same scope.
- The storage account lifecycle policy preserves unrelated rules and contains one container-filtered
  deletion rule that starts only after more than 31 days.
- The App Service uses the fixed `https://treemon.azurewebsites.net` origin, .NET 10 on Linux,
  HTTPS-only transport, disabled FTP/SCM basic publishing credentials, and the
  `CanvasShareViewer__StorageAccountName`, `CanvasShareViewer__ShareContainer`, and
  `AZURE_CLIENT_ID` settings. `AZURE_CLIENT_ID` is the created user-assigned identity's client ID,
  which selects that identity for `DefaultAzureCredential`.
- Easy Auth requires the current single tenant before requests reach the application, requires no
  enterprise-application assignment, requests only `openid`, disables its token store, and uses the
  exact `https://treemon.azurewebsites.net/.auth/login/aad/callback` redirect.
- The app registration declares no API permissions, enables ID-token issuance but not browser
  access-token issuance, and authenticates Easy Auth through a managed-identity federated
  credential rather than a client secret. A tenant-mandated `serviceManagementReference` is reused
  only when one unambiguous publisher-owned value exists.
- After deployment, the agent verifies the control-plane state, including that `AZURE_CLIENT_ID`
  equals the created user-assigned identity's client ID, and sets `canvasShare.viewerBaseUrl`
  separately while Treemon is not writing machine configuration.

## Security Posture

- No bearer credential ever reaches the recipient's browser. The opaque path segment is an
  identifier, not an authorization grant; Entra sign-in plus the viewer's own expiry check are what
  authorize a view.
- The embedded document is contained by iframe sandboxing (script execution allowed; same-origin,
  forms, popups, and top-navigation denied) and a restrictive content CSP.
- The active-content CSP and security headers apply only after the content route accepts a
  same-origin iframe navigation. Direct, top-level, cross-site, and metadata-missing requests get
  the unframeable shell instead, preventing active script from using top-level self-navigation as a
  network channel. Script that executes in the accepted sandbox still cannot read the viewer
  origin, fetch over the network, or leak referrer information.
- Missing, malformed, and expired share paths return an indistinguishable response -- same status,
  headers, and body -- to an authenticated caller; only response latency may differ, which is an
  accepted residual signal rather than a guarantee. Easy Auth rejects identities the tenant does
  not authenticate without revealing path existence.
- Storage and credential outages are intentionally distinguishable from a missing share by their
  fixed empty 503, but are not distinguishable by route or runtime environment and expose no
  exception message, stack, request path, or document content.
- The Remoting CSRF guard protects the publish call itself: a forged cross-origin
  `shareCanvasDoc` request from the operator's browser is rejected before any Azure I/O, the same as
  every other `IWorktreeApi` state-changing endpoint.

## Decisions

| Decision | Rationale |
|---|---|
| Use the publisher's private Blob container rather than App Service storage | One backing store keeps expiry metadata, lifecycle cleanup, and publisher RBAC attached to the shared artifact. |
| Split the viewer into a shell route and a separate content route | Lets the content response carry a much stricter CSP than the shell page needs, and gives the iframe a distinct `src` resource. |
| Sandbox the content iframe without `allow-same-origin` | Granting it would hand the embedded document the viewer's authenticated origin (cookies, Easy Auth session) even though it also has script execution. |
| Enforce expiry in the viewer at request time rather than relying on Blob lifecycle deletion | Lifecycle deletion runs on a daily-ish schedule and is a backstop; relying on it alone would leave documents readable past their promised expiry. |
| Prefer a managed-identity federated credential over an Easy Auth client secret | Avoids minting, storing, or rotating a long-lived secret for the viewer's app registration. |
| Enable registration ID-token issuance but not browser access-token issuance | App Service Easy Auth uses an OIDC hybrid `code id_token` form-post callback and rejects sign-in when the registration cannot issue that ID token; it redeems the code server-side through managed-identity federation, so browser access-token issuance remains unnecessary. |
| Request only the `openid` login scope | The viewer needs an ID token to authenticate a tenant subject but reads no profile/email claims and calls no downstream API. Explicit `scope=openid` prevents App Service's broader `openid profile email` defaults from adding an unused basic-profile consent ask. |
| Store expiry as blob metadata rather than a separate data store | Keeps the expiry attached to the artifact it governs, with no second store to keep in sync; it travels and disappears with the blob. |
| Re-check segments and expiry on the content route instead of trusting the shell | The recipient holds the URL, so the content route is directly reachable; a shell-only check would leave an expired share readable by editing the path. |
| Use a properties-only Blob lookup for the shell and reserve the body read for the content route | The shell needs only existence and expiry metadata, so downloading and discarding the complete document there would double the transferred document bytes without strengthening validation. |
| Accept case-insensitive `.html` suffixes and consecutive dots in one filename segment | Windows can surface documents such as `Status.HTML`, and `release..notes.html` is not traversal once `/` and `\` are forbidden. Preserving the original casing keeps the generated URL and exact Blob lookup aligned, while rejecting invalid names before publish prevents successful uploads with dead viewer links. |
| Gate active content on exact same-origin iframe Fetch Metadata and serve the shell on mismatch | A CSP-sandboxed top-level document has an opaque origin but can still navigate its own browsing context. Browser-controlled Fetch Metadata distinguishes the intended sandboxed iframe navigation; failing closed to the normal shell keeps direct and metadata-missing loads contained without adding a second document-body read. |
| Keep `sandbox allow-scripts` in the active-content CSP as well as on the iframe | The response policy reinforces the iframe's opaque-origin, script-enabled boundary. It remains defense in depth rather than the top-level navigation boundary, which is enforced by the Fetch Metadata gate. |
| Keep `frame-src 'self'` on the shell | A sandboxed child cannot navigate its parent without permission but can navigate itself. The ancestor policy blocks an external self-navigation before the probe receives a request. |
| Look the blob up by exact composed name, never by listing or prefix search | A share URL then reveals only its own document; no reachable code path can turn one link into an inventory of the container. |
| Allow `unsafe-eval` only inside the contained document response | Shared canvases already support arbitrary inline JavaScript; preserving `eval`/`new Function` compatibility does not grant viewer-origin or network access because the sandbox and remaining CSP directives still deny both. |
| Use `treemon.azurewebsites.net` rather than a custom domain or generated suffix | The Azure-provided hostname is short, TLS-enabled, requires no DNS ownership, and gives every shared document one stable origin for browser SSO. |
| Provision through an attended agent instead of checked-in deployment automation | The topology is small and infrequently created; preserving desired state is cheaper and clearer than maintaining migration, reconciliation, and mocked Azure CLI machinery. The agent confirms the private target, fails on existing named resources, and derives storage and publisher identity from the same sources the running publisher uses. |
| Reuse one unambiguous publisher-owned `serviceManagementReference` only when Entra requires it | Restricted tenants reject registration creation without their organizational service reference, while an arbitrary GUID can be invalid or misrepresent ownership. |
| Pass the Linux runtime through Azure CLI's JSON-file configuration input | The runtime contains `|`, which the Windows `az.cmd` launcher can reinterpret as a command pipe even when PowerShell supplied it as one argument. A file preserves the exact value without platform-specific quoting or reliance on Azure CLI installation internals. |
| Treat only a container-scoped RBAC assignment (or a descendant scope) as proof of viewer containment | Fully interpreting arbitrary Azure RBAC conditions would reproduce the authorization engine and could silently accept a broader grant. A conditioned assignment at an account, resource-group, subscription, or parent scope therefore fails closed; the operator must remove it or use a dedicated identity. |
| Merge the lifecycle rule instead of replacing the account policy | Azure lifecycle policies are whole-document resources. Preserving unrelated rules avoids destructive drift when the storage account has other lifecycle-managed data. |
| Share with the whole tenant instead of requiring enterprise-application assignment | Sharing is link-driven and ad hoc; a maintained assignment list would lock out the colleagues and guests a link is handed to, while the unguessable path, tenant sign-in, and expiry already bound exposure. |
| Guarantee an identical not-found response but not identical timing | Status, headers, body, and emission order are what an authenticated recipient can compare reliably; equalizing elapsed time would need a threat model, a maximum blob size, and padding, which is disproportionate to the leaked fact that a path once existed. |
| Return a fixed 503 rather than 404 for storage or credential failures | Dependency outages are operational and retryable, not evidence that a share is missing; preserving that distinction avoids hiding failures while an environment-independent boundary prevents diagnostic disclosure. |
| Update `viewerBaseUrl` separately from Azure provisioning | Keeping machine configuration outside the Azure operation avoids a second config writer. Any out-of-band edit happens only while Treemon is not writing the file. |
| Keep the publisher/viewer wire contract as pinned constants on both sides | The protocol is a handful of literals across two independently deployed apps, where a shared module would add coupling without preventing version skew; fixed-fixture compatibility tests catch drift at build time instead. |

## Key Files

| File | Purpose |
|---|---|
| `src/Shared/Types.fs` | `ShareCanvasDocRequest`, `CanvasShareResult`, `IWorktreeApi.shareCanvasDoc` |
| `src/Server/CanvasExport.fs` | Static export transform: base theme + no-op `canvasSend`; `extractTitle` / `resolveTitle` |
| `src/Server/CanvasShare.fs` | Publisher filename validation, Blob upload, expiry metadata, and clean viewer-URL construction; no SAS |
| `src/Server/WorktreeApi.fs` | Pre-I/O share filename/path gates, `shareCanvasDocImpl`, `withValidatedPath` wiring, and demo-mode stub |
| `src/Server/GlobalConfig.fs` | `canvasShare` config: `accountName`, `container`, `defaultExpiryDays`, `viewerBaseUrl` |
| `src/Server/HttpSecurity.fs` | Shared Remoting CSRF guard covering `shareCanvasDoc` |
| `src/Client/CanvasPane.fs`, `CanvasState.fs`, `CanvasUpdate.fs`, `index.html` | Share button, `ShareState` phase machine, clipboard write and banner routing |
| `src/CanvasShareViewer/` | New App Service viewer: shell route, content route, expiry check, sandbox/CSP, Easy Auth configuration |

## Verification

- The copied URL is clean: no SAS, signature, or other query-string token of any kind.
- The deployed viewer and every returned share URL use
  `https://treemon.azurewebsites.net`; provisioning never substitutes a suffixed hostname.
- Entra redirect/allow/deny: an anonymous browser navigation redirects to sign-in; the probe sends
  browser navigation headers (`Accept: text/html`), since App Service Easy Auth may return an empty
  401 instead of a redirect to an API-style client. Any identity the tenant authenticates -- member
  or B2B guest -- views the document; an identity outside the tenant is denied.
- The authorization redirect requests exactly `scope=openid` with `response_type=code id_token`;
  the app registration declares no Graph or other API permissions and the Enterprise Application
  does not require assignment.
- The control-plane RBAC audit finds no effective Blob data-plane read assignment outside the share
  container, and the live second-container probe under the deployed viewer identity still returns
  403 as defense in depth.
- The deployed App Service selects the created user-assigned identity through an exact
  `AZURE_CLIENT_ID` match, and a live share-container read succeeds under that identity.
- A document is denied immediately once its metadata expiry has passed, before any lifecycle
  deletion runs.
- The content route enforces the same checks as the shell: an expired or malformed share is denied
  on the content route too. A document opened directly at the content URL receives the normal
  shell, runs only in its `sandbox="allow-scripts"` iframe, cannot navigate the top-level page, and
  sends no request to an external probe.
- A normal shell-plus-content page load performs one properties-only exact Blob lookup and one
  streamed body read; neither route buffers the complete document in application memory.
- Throwing storage and credential readers produce the same empty policy-headered 503 on shell and
  content routes in both Production and Development, with no framework diagnostic response.
- Deleting or clearing a document's backing blob denies its link immediately (revocation).
- A hostile fixture attempting cookie/storage access, same-origin fetches, form submission,
  popups, parent/top navigation, frame self-navigation (`location`, `location.replace`, and
  `_self`), and network exfiltration all fail inside the sandboxed iframe and CSP, while intended
  self-contained document scripting still works.
- Share UI/clipboard behavior covers AgentDoc-only button gating, the `ShareState` lock and
  spinner, and clipboard-outcome banner routing.
- The actual secret detector is run against a clean viewer URL and does not flag it.
- Before provisioning, the selected subscription and tenant are confirmed explicitly; the operation
  fails rather than reusing or reconciling any named viewer resource.

## Related Specs

- `docs/spec/canvas-pane.md` -- the canvas pane the Share button lives in (tab bar, AgentDoc vs
  SystemView, archive precedent)
- `docs/spec/canvas-interaction-routing.md` -- per-doc ownership/routing (the Share button is
  AgentDoc-scoped like liveness/archive)
- `docs/spec/worktree-monitor.md` -- the loopback Origin/Referer boundary that fronts
  `shareCanvasDoc` and the rest of the Remoting surface
