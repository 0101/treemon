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
  Auth handles unauthenticated identities before application code runs and never reveals whether
  the requested share exists.
- A `BlobNotFound` 404 is the only dependency outcome treated as a missing share. Container or
  account failures, storage throttling, authorization, service, and managed-credential failures
  return one fixed, empty 503 with restrictive response headers in every runtime environment, so
  an outage is retryable without exposing framework diagnostics.
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
  `https://treemon.azurewebsites.net`. The URL must be an HTTPS origin whose parsed path is exactly
  `/`, with no user info, query, or fragment; path-based viewer URLs are rejected because the
  deployed viewer serves `/c/...` only at the origin root. An unconfigured Share action still
  fails with a clear `Result.Error` before any network call.
- `defaultExpiryDays` remains 7 and `maxCanvasShareExpiryDays` is 30. The share container's Blob
  lifecycle policy deletes only after 31 days or more, so cleanup never removes a document the
  viewer would still have served.
- The same section also carries `approvedSubscription`, read only by the deployment script and
  never by the running server. It is machine-private: it has no repository default, no
  placeholder value, and its absence means deployment is unconfigured rather than unrestricted.
  Its value may be a subscription name or ID; the script resolves it to a subscription ID and
  compares IDs, so the two forms are interchangeable.
- The deployment script and the server both resolve the config through `TREEMON_CONFIG_DIR` when
  it is set. Migration reconciliation and live verification use that isolation deliberately: they
  run against a throwaway config seeded with `accountName`, `container`, and
  `approvedSubscription` copied from the private machine config, so a run cannot add
  `viewerBaseUrl` to -- or otherwise alter -- the configuration the production instance reads.
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
- `WorktreeApi.shareCanvasDocImpl` applies `CanvasShare.validateFilename` before path validation or
  file access, then keeps the existing read, export, and publish pipeline behind the same
  `withValidatedPath` guard that every other write method uses (mirroring `archiveCanvasDoc`).
  `CanvasShare.publish` repeats that same validation at the upload boundary before configuration or
  Azure work, and the demo-mode stub keeps returning `Error "... not available in demo mode"`.
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
  application code. Assignment is not required on the enterprise application: every identity the
  tenant authenticates, including B2B guests, passes the gate. The registration authenticates to
  Easy Auth via a managed-identity federated credential rather than a long-lived client secret.
  The registration enables ID-token issuance because Easy Auth's browser callback requests
  `response_type=code id_token` with `response_mode=form_post`; browser access-token issuance
  remains disabled.
- Two routes divide responsibility: a shell route (`/c/<opaque-prefix>/<filename>`) validates the
  request and expiry through an exact Blob properties lookup and renders a minimal HTML page
  without downloading the document body; a content route
  (`/c/<opaque-prefix>/<filename>/content`) performs the only body-bearing lookup and is the only
  thing the shell's iframe loads. Keeping them separate lets the content response carry a much
  stricter policy than the shell needs.
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
  the exception type and available Azure status/error code, clears the route response, and emits
  the fixed dependency-failure response; it never converts an outage to not-found.
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
- Easy Auth's token store is disabled and no downstream Graph scopes are requested; the viewer
  origin exposes no upload/delete/admin API -- it is read-only, with only the shell and content
  routes.

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

### Provisioning

- Provisioning targets the personal development Azure subscription and an isolated,
  non-production resource group. Subscription, tenant, resource-group, plan, identity, and
  registration names are operator inputs; the App Service name is `treemon`, producing
  `https://treemon.azurewebsites.net`.
- Machine-private configuration identifies the one approved subscription. Before any
  resource-provider or Entra application operation, provisioning requires the operator input and
  selected Azure CLI account to match that configured target exactly and fails with no changes when
  the value is absent, ambiguous, disabled, or mismatched. Exact subscription and tenant
  identifiers never appear in tracked repository content.
  The match is by resolved subscription ID -- the configured value, the `-Subscription` input, and
  the selected CLI account must all resolve to the same enabled subscription -- so a name and an ID
  naming the same subscription agree, while a raw-string near-miss cannot pass. Failure diagnostics
  name the configuration key and the mismatch, never the values.
  The guard is mandatory and independent of whatever restrictions the operator's environment
  places on direct Azure CLI use. An environment-level control can only see the commands issued to
  it, not the `az` child processes this script spawns, so the script proves its own target on every
  run; the two are layers, and neither is removed or relaxed because the other exists. Every
  resource-plane `az` invocation the script makes names its subscription explicitly rather than
  inheriting the CLI's selected account.
- Before first creation, provisioning checks global App Service name availability and fails
  clearly if `treemon` is no longer available. It never silently appends a random suffix because
  that would change the durable shared-link origin and its browser SSO session.
- The publisher keeps its existing delegated Entra/Azure CLI identity and
  `Storage Blob Data Contributor` grant. The viewer uses a managed identity with the new read-only
  grant scoped to the share container.
- When the requested viewer identity already exists, provisioning audits its Blob-read RBAC before
  any Azure mutation and repeats the audit as part of deployed-state verification. The audit
  enumerates direct and group-derived assignments throughout the subscription plus assignments
  inherited from parent scopes, resolves each role definition's effective
  `dataActions`/`notDataActions`, and fails on a Blob-read grant unless its assignment scope is the
  configured share container or a descendant. It reports but never deletes an offending
  assignment, because that grant may belong to another workload.
- Provisioning ensures the configured share container exists with anonymous access disabled before
  either container-scoped grant is applied; the publisher intentionally does not create containers
  at share time.
- `scripts/deploy-canvas-share-viewer.ps1` is the idempotent operator entry point. Its only
  deployment-name inputs are subscription, tenant, resource group, plan, identity, and app
  registration; it reads account/container from the machine-level `canvasShare` config and resolves
  the delegated publisher as the current Azure CLI user. The fixed B1 Linux plan and any new
  resource group use the storage account's Azure location.
- Azure CLI resource reads accept both flattened command output and fields nested under
  `properties`, including the current `appServicePlanId` web-app field. The pipe-bearing Linux
  runtime value is supplied through the CLI's JSON-file configuration path rather than as an
  `az.cmd` argument. If Entra alone rejects an app-registration create or update because
  `serviceManagementReference` is required, provisioning retries with the one distinct non-empty
  reference on applications owned by the delegated publisher; zero or multiple values fail closed
  rather than inventing or ambiguously selecting an organizational service reference.
- `-ValidateOnly` performs subscription/tenant, configuration, existing-resource, viewer-identity
  RBAC, global-name, and local-publish checks without changing Azure or machine configuration. An
  apply run reconciles resources, merges the canvas rule into the account's complete lifecycle
  policy without removing unrelated rules, deploys with `az webapp deploy` after SCM/FTP basic
  authentication is disabled, verifies the resulting control-plane state, and writes the exact
  canonical `viewerBaseUrl` while preserving every other machine setting.
- Deployment sets and deployed-state validation asserts both `DOTNET_ENVIRONMENT=Production` and
  `ASPNETCORE_ENVIRONMENT=Production`; the application-level dependency exception boundary remains
  active even if either platform setting later drifts.
- Easy Auth's `clientSecretSettingName` is the
  `OVERRIDE_USE_MI_FIC_ASSERTION_CLIENTID` sentinel; the slot-sticky app setting with that name
  contains the user-assigned identity's client ID. The registration's federated credential trusts
  that identity's principal ID with the tenant v2 issuer and `api://AzureADTokenExchange`
  audience. Provisioning and deployed-state validation require ID-token issuance for Easy Auth's
  hybrid callback while keeping browser access-token issuance disabled. No client secret or extra
  login scope is created.
- The canonical App Service, identity, Entra configuration, RBAC grants, and lifecycle policy
  remain deployed after verification. Verification removes only its document fixtures and any
  auxiliary resources created solely to prove the permission boundary.
- A cross-subscription correction preserves the canonical hostname by moving the App Service and
  its plan together rather than deleting and recreating the globally named app. The correction is
  split by who can reach which subscription. `-PrepareCrossSubscriptionMove` confirms the
  configured private storage/container and reconciles only the approved destination resource
  group, replacement user-assigned identity, and its container-scoped reader grant; it neither
  checks the global app name nor reads or creates an App Service or plan.
  `-ReconcileCrossSubscriptionMove` requires `-ConfirmPortalMoveCompleted` before any tool or Azure
  call, resolves the moved app and plan only in the approved destination, replaces the app's
  identity attachment with the prepared identity, and reconciles the retained tenant-scoped app
  registration, federated credential, container RBAC, settings, package, and deployed state.
  Between those modes the operator performs the move in the Azure portal using the redacted
  checklist. Automation never queries, validates, or mutates anything in the source subscription
  and never carries a resource ID, subscription, or tenant identifier belonging to it; the
  checklist identifies what to move by the canonical App Service and plan names.
  Source-side leftovers -- the obsolete identity, its role assignments, any stand-in verification
  storage, and the former resource groups -- stay in place until the move is independently
  verified, so the operator can move the app and plan back while automation restores the previous
  identity reference, credential subject, and settings. Removing them is a separate, explicitly
  approved operator step, not part of the move.
  The source app still holds the global name until it moves, so a pre-move `-ValidateOnly` run
  against the destination subscription is expected to stop at the name-availability check. That
  check is not relaxed for the correction: the operator's move is what places the app in the
  destination, and reconciliation runs afterwards, when the app is already there and the check no
  longer applies.
- Decommissioning the source-side leftovers is a manual operator activity in the portal. Automation
  prepares an ordered checklist describing each target by resource type and role -- never by
  resource ID, subscription, or tenant identifier -- and reviews whatever redacted evidence the
  operator supplies afterwards. It deletes nothing, at any scope, in either subscription.
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
| Enable registration ID-token issuance but not browser access-token issuance | App Service Easy Auth uses an OIDC hybrid `code id_token` form-post callback and rejects sign-in when the registration cannot issue that ID token; it redeems the code server-side through managed-identity federation, so browser access-token issuance remains unnecessary. |
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
| Derive storage and publisher deployment inputs from machine/Azure CLI state | The existing `canvasShare` account/container and current delegated publisher are already the publisher's source of truth. Requiring them again as script arguments would permit a viewer and publisher to be provisioned against different containers or identities. |
| Require an exact machine-private subscription allowlist match | Azure CLI's ambient default proves only what is selected, not whether that subscription is approved for this workload. A private source of truth keeps identifiers out of the repository and makes a mistaken shared-subscription deployment fail before any resource operation. |
| Keep the in-script guard even when the environment already restricts direct CLI use | A control that filters issued commands cannot observe the `az` child processes a deployment script starts, so it stops covering exactly the operations this feature automates. The script's own check is the only one present inside a run, and an outer restriction is never accepted as a reason to remove or weaken it. |
| Move the App Service and plan together when correcting subscription placement | An ARM move preserves the globally unique `treemon.azurewebsites.net` name; deleting and recreating the app would briefly release that name and make rollback dependent on reacquiring it. |
| Have the operator perform the move in the portal rather than automating it | Automation is confined to the approved subscription and cannot query or validate the source one, so it cannot issue the move at all. Splitting the correction into prepare / operator move / reconcile keeps the one irreversible step under direct human control and leaves automation with only operations it is allowed to perform. |
| Expose preparation and reconciliation as mutually exclusive deployment-script modes | Preparation must bypass the ordinary global-name guard without weakening it, while reconciliation must not begin from an unconfirmed portal handoff. Separate parameter sets and an explicit post-move confirmation make those safety boundaries visible at invocation time. |
| Decommission by exact resource allowlist rather than broad scope | Shared subscriptions can contain unrelated workloads. Fresh inventory, drift checks, and individual resource-ID deletion make collateral changes falsifiable and leave ambiguous resources untouched. |
| Reuse one unambiguous publisher-owned `serviceManagementReference` only when Entra requires it | Restricted tenants reject registration mutations without their organizational service reference, while an arbitrary GUID can be invalid or misrepresent ownership. Conditional discovery keeps the normal path unchanged, adds no secret or deployment-name input, and fails closed when publisher-owned state cannot identify one value. |
| Pass the Linux runtime through Azure CLI's JSON-file configuration input | The runtime contains `|`, which the Windows `az.cmd` launcher can reinterpret as a command pipe even when PowerShell supplied it as one argument. A file preserves the exact value without platform-specific quoting or reliance on Azure CLI installation internals. |
| Treat only a container-scoped RBAC assignment (or a descendant scope) as proof of viewer containment | Fully interpreting arbitrary Azure RBAC conditions would reproduce the authorization engine and could silently accept a broader grant. A conditioned assignment at an account, resource-group, subscription, or parent scope therefore fails closed; the operator must remove it or use a dedicated identity. |
| Merge the lifecycle rule instead of replacing the account policy | Azure lifecycle policies are whole-document resources. Preserving unrelated rules avoids destructive drift when the storage account has other lifecycle-managed data. |
| Share with the whole tenant instead of requiring enterprise-application assignment | Sharing is link-driven and ad hoc; a maintained assignment list would lock out the colleagues and guests a link is handed to, while the unguessable path, tenant sign-in, and expiry already bound exposure. |
| Guarantee an identical not-found response but not identical timing | Status, headers, body, and emission order are what an authenticated recipient can compare reliably; equalizing elapsed time would need a threat model, a maximum blob size, and padding, which is disproportionate to the leaked fact that a path once existed. |
| Return a fixed 503 rather than 404 for storage or credential failures | Dependency outages are operational and retryable, not evidence that a share is missing; preserving that distinction avoids hiding failures while an environment-independent boundary prevents diagnostic disclosure. |
| Let the deployment script write `~/.treemon/config.json` directly instead of through the running server | Provisioning must work with no Treemon instance running, and a server RPC or shared cross-process lock would add permanent coupling for a rare operator step; the script re-reads, replaces atomically, and preserves every other setting, and the operator runs it while Treemon is not writing config. |
| Keep the publisher/viewer wire contract as pinned constants on both sides | The protocol is a handful of literals across two independently deployed apps, where a shared module would add coupling without preventing version skew; fixed-fixture compatibility tests catch drift at build time instead. |

## Key Files

| File | Purpose |
|---|---|
| `src/Shared/Types.fs` | `ShareCanvasDocRequest`, `CanvasShareResult`, `IWorktreeApi.shareCanvasDoc` (shape unchanged) |
| `src/Server/CanvasExport.fs` | Static export transform: base theme + no-op `canvasSend`; `extractTitle` / `resolveTitle` (unchanged) |
| `src/Server/CanvasShare.fs` | Publisher filename validation, Blob upload, expiry metadata, and clean viewer-URL construction; no SAS |
| `src/Server/WorktreeApi.fs` | Pre-I/O share filename/path gates, `shareCanvasDocImpl`, `withValidatedPath` wiring, and demo-mode stub |
| `src/Server/GlobalConfig.fs` | `canvasShare` config: `accountName`, `container`, `defaultExpiryDays`, `viewerBaseUrl` |
| `src/Server/HttpSecurity.fs` | Shared Remoting CSRF guard covering `shareCanvasDoc` (unchanged) |
| `src/Client/CanvasPane.fs`, `CanvasState.fs`, `CanvasUpdate.fs`, `index.html` | Share button, `ShareState` phase machine, clipboard write and banner routing (unchanged) |
| `src/CanvasShareViewer/` | New App Service viewer: shell route, content route, expiry check, sandbox/CSP, Easy Auth configuration |
| `scripts/deploy-canvas-share-viewer.ps1` | Idempotent non-production Azure provisioning, secret-free Easy Auth, Entra-authenticated ZIP deployment, validation, and machine-config update |
| `scripts/canvas-share-viewer-deployment/CrossSubscriptionCorrection.ps1` | Approved-destination preparation, redacted portal handoff, post-move discovery, and replacement-identity attachment |
| `scripts/canvas-share-viewer-deployment/Deployment.Tests.ps1` | Windows/Azure CLI shape, packaging-output, reconciliation, restricted-tenant registration, and Easy Auth callback regressions |
| `scripts/canvas-share-viewer-deployment/ViewerBlobAccess.ps1` | Fail-closed audit of the viewer identity's effective Blob-read data-plane assignments |
| `scripts/canvas-share-lifecycle-policy.json` | Container-filtered deletion rule starting after 31 days |
| `docs/canvas-share-viewer-deployment.md` | Local operator prerequisites, dry run, apply, and durable-resource guidance |

## Verification

- The copied URL is clean: no SAS, signature, or other query-string token of any kind.
- The deployed viewer and every returned share URL use
  `https://treemon.azurewebsites.net`; provisioning never substitutes a suffixed hostname.
- Entra redirect/allow/deny: an anonymous request redirects to sign-in; any identity the tenant
  authenticates -- member or B2B guest -- views the document; an identity outside the tenant is
  denied.
- The control-plane RBAC audit finds no effective Blob data-plane read assignment outside the share
  container, and the live second-container probe under the deployed viewer identity still returns
  403 as defense in depth.
- A document is denied immediately once its metadata expiry has passed, before any lifecycle
  deletion runs.
- The content route enforces the same checks as the shell: an expired or malformed share is denied
  on the content route too. A document opened directly at the content URL receives the normal
  shell, runs only in its `sandbox="allow-scripts"` iframe, cannot navigate the top-level page, and
  sends no request to an external probe.
- A normal shell-plus-content page load performs one properties-only exact Blob lookup and one
  body-bearing exact read; the shell never downloads or buffers the document body.
- Throwing storage and credential readers produce the same empty policy-headered 503 on shell and
  content routes in both Production and Development, with no framework diagnostic response.
- Deleting or clearing a document's backing blob denies its link immediately (revocation).
- A hostile fixture attempting cookie/storage access, same-origin fetches, form submission,
  popups, parent/top navigation, frame self-navigation (`location`, `location.replace`, and
  `_self`), and network exfiltration all fail inside the sandboxed iframe and CSP, while intended
  self-contained document scripting still works.
- Existing share UI/clipboard behavior -- AgentDoc-only button gating, `ShareState` lock and
  spinner, and clipboard-outcome banner routing -- continues to pass unchanged.
- The actual secret detector is run against a clean viewer URL and does not flag it.
- Deployment against a subscription other than the machine-private approved one exits non-zero
  before any resource-provider or Entra application call, and its output contains no exact
  subscription name or ID; the approved context proceeds normally.
- After the cross-subscription correction the App Service and its plan are in the approved
  subscription, still answer on `https://treemon.azurewebsites.net`, and the authenticated share
  lifecycle -- publish, view, expiry, revocation, containment, fixed 503 -- still passes end to
  end when driven through an isolated `TREEMON_CONFIG_DIR` on a port other than 5000, leaving the
  production configuration and instance untouched.
- Nothing in the approved subscription outside the prepared destination and the reconciled app
  changed, and the correction's own command log shows no operation against the source
  subscription and no resource ID belonging to it. Source-side state is attested by the operator,
  not queried by automation.

## Related Specs

- `docs/spec/canvas-pane.md` -- the canvas pane the Share button lives in (tab bar, AgentDoc vs
  SystemView, archive precedent)
- `docs/spec/canvas-interaction-routing.md` -- per-doc ownership/routing (the Share button is
  AgentDoc-scoped like liveness/archive)
- `docs/spec/remoting-csrf-hardening.md` -- the pipeline-level Origin/Referer guard that fronts
  `shareCanvasDoc` and the rest of the Remoting surface
