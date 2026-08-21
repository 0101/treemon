# Canvas Doc Sharing

## Goals

- One-click **Share** of a focused canvas doc to an **unguessable URL** any recipient can open in a
  plain browser — **no login** — that renders the doc's HTML+JS read-only with agent-interactivity
  neutralized.
- **Recipient-only secrecy:** the link is a per-doc capability (a leaked link exposes only that one
  doc) and **auto-expires** (7 days — Azure's maximum for a keyless user delegation SAS).
- **Hide the ugly URL:** copy a **rich titled hyperlink** to the clipboard so the raw URL length is
  cosmetically irrelevant when pasted into chat/mail.

## Expected Behavior

### Share action

- A **Share** button appears in the canvas tab bar next to Archive, for **`AgentDoc` docs only**
  (a `SystemView` like the beads dashboard is server-generated and not shareable).
- Clicking it: static-exports the focused doc → uploads it to Azure Blob Storage → mints a per-doc
  read-only SAS URL → writes a **rich link + plain URL** to the clipboard → shows a success banner
  (`Shared — link copied`). On failure it shows the existing dismissible error banner.
- **The button shows progress and refuses re-entry while a share is in flight.** Publishing is a
  multi-second round-trip (Entra token → user delegation key → upload → clipboard), so
  `CanvasState.ShareState` records the scoped worktree/doc and the `Publishing` or
  `WritingClipboard` phase. Every Share button is disabled while that state is non-idle, only the
  matching scoped doc shows the spinner, and the reducer rejects another launch. Results transition
  or clear only the matching operation, so navigation and stale async completions cannot unlock or
  overwrite a newer share (locked by `ShareCanvasDocResultTests`).
- Share and canvas-path copy are mutually exclusive clipboard workflows. The reducer rejects either
  action while the other owns the clipboard, and both controls are disabled until that workflow
  settles, so their async results cannot overwrite the pane-global error or clipboard notice state.
- The action operates on a **single, self-contained doc**. Docs that link to sibling `.html` tabs
  are shared as just the focused file; sibling links are inert in the export. Multi-doc bundles are
  out of scope.

### Static export (what gets published)

The on-disk `.agents/canvas/<file>.html` already contains **none** of the serve-time injected
scripts (bridge heartbeat, `canvasSend`, idiomorph/morph, error overlay) — those are added only by
`CanvasDocServer` at `:5002`. So the export does the **opposite of stripping**: it re-injects the
two pieces a standalone copy needs, and nothing else.

- Inject the **base theme `<style>`** (dark theme + the `--bg-*`/`--text-*`/`--accent`/`--status-*`
  design tokens + base typography) so a doc that leaned on the injected theme renders on-theme
  standalone.
- Inject a **no-op `window.canvasSend`** so author buttons that call it do nothing (instead of
  throwing `ReferenceError`). Raw `window.parent.postMessage` already degrades to a harmless
  self-post in a top-level window.
- Inject **none** of: bridge heartbeat, idiomorph runtime, morph controller, error overlay — nor the
  **link interceptor**. The interceptor turns same-origin `.html` link clicks into
  `navigate-canvas-doc` tab-switch messages to the pane; a standalone published copy has no pane, so
  it is pane-coupling machinery like the others. Omitting it means sibling-`.html` links no longer
  switch tabs — they fall back to the browser's default navigation, which resolves to a sibling blob
  URL *without* the SAS token and so fails (`403`); they are **non-functional**, consistent with
  sharing a *single self-contained doc* (Decision #8). External links keep their default behavior.
- Injection lands at `</head>` (case-insensitive), mirroring `CanvasDocServer.handleCanvasRequest`;
  if there is no `</head>`, prepend. The two share one helper (`CanvasExport.injectAtHead`, which
  `CanvasDocServer.handleCanvasRequest` now calls) so live-served and published placement cannot
  drift.

### Publishing & secrecy (Azure Blob)

- The exported HTML is uploaded to a **private** container under an **unguessable prefix + the doc's
  real filename** (`<random-id>/<filename>`), with `Content-Type: text/html; charset=utf-8` (the
  charset is declared explicitly — the export injects no `<meta charset>`, so a standalone non-ASCII
  doc would otherwise risk mojibake; a `Content-Type` check should therefore match the `text/html`
  prefix, not exact-equal). The real filename is kept so the recipient sees a meaningful page/tab
  title.
- The storage account has **anonymous blob access disabled**, so a bare blob URL is denied
  (`409 PublicAccessNotPermitted`) — the **only** way in is the signed link.
- The link is a **per-doc, blob-scoped, read-only SAS** (`sr=b`, `sp=r`, `spr=https`) with an
  expiry, signed with an Entra **user delegation key** rather than an account key (Decision #3).
  Because it is blob-scoped, a recipient of doc A's link **cannot** read doc B even if they
  guess B's name (least privilege; verified by isolation test — the signature covers the full blob
  path, so a crossed token fails `AuthenticationFailed`/"Signature did not match").
- The SAS is additionally bound to the **signing identity**: it carries `skoid` (the signer's object
  id) and `sktid` (the tenant). Azure caches role assignments and user delegation keys, so removing
  the role or revoking delegation keys invalidates outstanding links only after cache propagation.
- **Revocation** is per-doc: delete the blob → the link returns `404`. The strongest bulk operation
  is `az storage account revoke-delegation-keys`; it invalidates all user delegation SAS grants for
  the account after Azure's cache propagation, not instantly.
- **Lifecycle cleanup:** an Azure storage **lifecycle policy** deletes shared blobs older than the
  expiry window (8 days, just behind the 7-day link cap), so a doc's content does not linger at rest
  after its link is dead (privacy) and storage does not accumulate (cost). The policy runs daily
  (≈1-day granularity); immediate per-doc revoke is still a blob delete.

### Clipboard (rich link)

On success the client writes **two clipboard formats at once** via the async Clipboard API:

- `text/html` = `<a href="<sas-url>"><title></a>` — rich targets (Teams, Slack, Google Chat,
  Outlook, Gmail, Word) render a **titled hyperlink**.
- `text/plain` = the raw SAS URL — plain targets (VS Code editor, terminal, Notepad) get the URL.

The title is the doc's `<title>`, falling back to a prettified filename
(`build-status.html` → `Build status`). Because the URL is hidden behind the title, its length does
not matter.

Authoring note: the prettified fallback only sentence-cases a lowercase kebab filename
(`mtp-testexplorer-hang.html` → "Mtp testexplorer hang") and can't recover acronyms or camelCase, so
canvas docs should set a well-cased `<title>` (e.g. `MTP TestExplorer Hang`) to control the shared
link text. The canvas skill's minimal template and authoring guidance cover this
(`src/Extension/skill/SKILL.md`).

The clipboard write is async and its outcome is **routed back into the update** (`ClipboardWriteResult`)
rather than being fire-and-forget: the success banner confirms `Shared — link copied` only once the
write actually lands, and a rejected write (transient activation lost across the share round-trip, a
revoked permission, or an unsupported API — the last throws synchronously and is caught) is corrected
to `Shared — link ready, copy it manually: <url>` with the raw SAS URL shown as selectable text. The
banner never claims a copy that did not happen (Decision #10).

### Configuration

- The share backend is configured in the machine-level Treemon config (`~/.treemon/config.json`,
  read via `GlobalConfig`): a `canvasShare` section with `accountName`, `container` and
  `defaultExpiryDays` (default `7`; **bounded to `1–7` days** — the ceiling is Azure's user
  delegation key limit, not a preference, and a value outside the range falls back to the default so
  a typo can't produce links Azure refuses to sign).
- **There is no application credential to configure.** Links are signed with an Entra user
  delegation key obtained through `AzureCliCredential`, which uses the operator's existing
  `az login` and its persisted MSAL token cache. Treemon stores no account key, connection string,
  or credential env var; `accountName` is ordinary non-secret config. The CLI cache remains a host
  credential and must be protected and revoked normally.
- The one operator prerequisite is an RBAC grant: **`Storage Blob Data Contributor` on the storage
  account** for the identity running the server. It covers both the blob write and the
  `generateUserDelegationKey` action (which acts at account scope), so no second role is needed.
- If the backend is unconfigured (no `accountName`), the Share action returns a clear `Result.Error`
  ("Canvas sharing is not configured — set `canvasShare.accountName` …") **before** acquiring a
  credential or touching the network. If the host identity has expired, it returns a distinct
  "run `az login` on this host" error rather than surfacing an SDK exception type. Nothing logged
  contains the full SAS.
- The demo-mode API stub returns `Error "… not available in demo mode"`, matching `archiveCanvasDoc`.

## Technical Approach

- **API contract** (`src/Shared/Types.fs`): add `ShareCanvasDocRequest { WorktreePath; Filename }`,
  a `CanvasShareResult { Url: string; Title: string }`, and
  `IWorktreeApi.shareCanvasDoc : ShareCanvasDocRequest -> Async<Result<CanvasShareResult, string>>`.
  The server returns the title (it extracts it from the HTML) so the client can build the rich
  clipboard link without re-parsing.
- **Static export** (new `src/Server/CanvasExport.fs`, chosen over a third `buildInjection` arm so
  the downstream `shareCanvasDocImpl` in `WorktreeApi.fs` — which compiles *before*
  `CanvasDocServer.fs` — can call it): `buildStaticHtml : string -> string` re-injects the base theme
  + a no-op `canvasSend` (and nothing else) at `</head>`. The shared `baseStyle` is **relocated**
  from `CanvasDocServer.fs` into this dependency-free module (single source of truth; `buildInjection`
  now references `CanvasExport.baseStyle`), and the `</head>` placement is a shared
  `injectAtHead` both call. Also exposes `extractTitle (html) : string option` and
  `resolveTitle html filename` (the `<title>`→prettified-filename fallback the server returns as
  `CanvasShareResult.Title`), which delegates the filename fallback to the shared
  `Shared.Formatting.prettifyFilename` (Decision #11). Every function is a pure
  `string→string`/`string option` for unit testing.
- **Publish backend** (`src/Server/CanvasShare.fs`): cached
  `BlobServiceClient(Uri($"https://{accountName}.blob.core.windows.net"), AzureCliCredential())` →
  `GetUserDelegationKeyAsync(startsOn, expiresOn)` →
  `CreateIfNotExistsAsync(PublicAccessType.None)` → `UploadAsync(randomPrefix/filename, html)` with
  `BlobHttpHeaders.ContentType = "text/html; charset=utf-8"` →
  `BlobSasBuilder(...).ToSasQueryParameters(delegationKey, accountName)`, returning
  `$"{blobClient.Uri}?{sasParameters}"`. Random prefix is a high-entropy base62 id. Both `startsOn`
  and `expiresOn` derive from **one** start instant backdated 5 minutes: that absorbs clock skew and
  keeps the window strictly inside Azure's 7-day key limit, which is rejected outright at the
  boundary. The container is created on demand so a fresh account works on first publish without a
  manual container-create step (F13), inside the existing `try` so a create failure reuses the same
  `RequestFailedException` handler. Requires `Azure.Storage.Blobs` **and `Azure.Identity`** in
  `Server.fsproj`.

  Two F# interop notes worth keeping: `GetUserDelegationKeyAsync` takes `Nullable<DateTimeOffset>`
  for `startsOn`, and the `CancellationToken` must be passed **explicitly** — with two arguments F#
  binds the same-arity `(BlobGetUserDelegationKeyOptions, CancellationToken)` overload instead and
  fails to compile.

  **The credential and one `BlobServiceClient` per account are built once (`lazy`) and reused — this
  is a requirement, not a micro-optimization.** `AzureCliCredential` invokes the `az` CLI, while the
  reusable bearer-token cache belongs to the service client's authentication pipeline. Constructing
  a fresh client per publish therefore paid the measured 3–5 s CLI cost every time: shares took
  9–11 s, which exceeds the browser's ~5 s transient-activation window, so
  `navigator.clipboard.write` was rejected and the user got the "copy it manually" correction
  instead of a copied link. Reusing the client brings a warm share to 3–4 s and the clipboard write
  back inside the window. The first share after a server restart is still slow (cold JIT + CLI);
  the spinner covers it, and the banner degrades honestly if the write is rejected.
- **Server wiring** (`src/Server/WorktreeApi.fs`): `shareCanvasDocImpl` =
  `validateCanvasPath → read file → CanvasExport.buildStaticHtml → CanvasShare.publish → Result`,
  wired into the live `IWorktreeApi` record via `withValidatedPath` (mirroring `archiveCanvasDoc`),
  plus the demo-mode stub. `withValidatedPath` was generalized from
  `(unit -> Async<Result<unit,string>>)` to `(unit -> Async<Result<'a,string>>)` so `shareCanvasDoc`
  (which returns `CanvasShareResult`, not `unit`) reuses the same path-validation guard as every
  other write method; existing `unit`-returning callers unify unchanged. `Title` is assembled with
  `CanvasExport.resolveTitle html filename` (not bare `extractTitle`, which is `string option`) so the
  non-optional `CanvasShareResult.Title` gets the `<title>`→prettified-filename fallback; the title is
  read from the original on-disk HTML since `buildStaticHtml` only injects at `</head>` and never
  alters `<title>`.
- **Client** (`src/Client/CanvasPane.fs`, `CanvasUpdate.fs`, `index.html`): a Share button in
  `headerBar` (AgentDoc-only, beside Archive) raising a new `ShareDoc` callback in
  `CanvasPaneCallbacks`; `ShareCanvasDoc` / `ShareCanvasDocResult` / `ClipboardWriteResult` update arms
  in `CanvasUpdate.fs`; on `Ok { Url; Title }`, write the two clipboard formats with
  `navigator.clipboard.write([new ClipboardItem({ "text/html": …, "text/plain": … })])` and dispatch
  `ClipboardWriteResult` from its `then`/`catch` (and a synchronous `try/catch` for an unavailable API)
  so the success banner reflects the write's real outcome — copied vs. "copy it manually: `<url>`"
  (F6) — instead of unconditionally claiming a copy; on `Error`, reuse the error banner. Button styling
  in `index.html`. Share and path-copy controls also gate each other while either clipboard workflow
  is pending.

## Storage Account Setup

One-time, **local-only** provisioning, run by an operator with `az login` to the dev subscription
(never from CI). Everything below uses the logged-in account; Treemon never receives or uses an
account key.

```bash
# Pass --subscription explicitly on every command so creation, RBAC, policy, and verification all
# target the intended subscription even when the CLI default points elsewhere.
SUB=<personal-dev-subscription-id>

az group create -n rg-treemon-canvas-share -l westeurope --subscription $SUB

# Decision #3 — Treemon never uses Shared Key; Decision #4 — bare blob URL denied
az storage account create -n <account> -g rg-treemon-canvas-share -l westeurope --subscription $SUB \
  --sku Standard_LRS --kind StorageV2 \
  --allow-shared-key-access false \
  --allow-blob-public-access false \
  --https-only true --min-tls-version TLS1_2

# The one RBAC grant. Scope it at the ACCOUNT: generateUserDelegationKey acts at account level, so a
# container-scoped data role alone cannot sign links (it would need Storage Blob Delegator as well).
az role assignment create --assignee <operator-object-id> \
  --role "Storage Blob Data Contributor" \
  --scope /subscriptions/$SUB/resourceGroups/rg-treemon-canvas-share/providers/Microsoft.Storage/storageAccounts/<account> \
  --subscription $SUB
```

The strongest account-wide emergency revocation invalidates all user delegation keys after Azure's
cache propagation, not immediately. Run it only when revocation is required. This management-plane
action requires `Microsoft.Storage/storageAccounts/revokeUserDelegationKeys/action`; the app's
`Storage Blob Data Contributor` role is not enough.

```bash
az storage account revoke-delegation-keys \
  --name <account> --resource-group rg-treemon-canvas-share \
  --subscription $SUB
```

- **Lifecycle cleanup** (Decision #9): the management policy below deletes shared blobs after the
  expiry window so expired-link content does not linger at rest (privacy) and storage does not
  accumulate (cost).

> **The container itself needs no manual step.** The app creates the private
> `canvasShare.container` (default `canvas-shared`) on demand on first publish, via
> `CreateIfNotExists(PublicAccessType.None)` in `CanvasShare.publish` — a *data-plane* operation
> authorized by the same Entra identity that signs the SAS, so a fresh account/subscription works
> without a manual `az storage container create`. The call is idempotent (a no-op once the container
> exists) and keeps anonymous access off at the container level, complementing the account-level
> setting above.

The lifecycle rule is committed at `scripts/canvas-share-lifecycle-policy.json`:

```json
{
  "rules": [
    {
      "name": "expire-shared-canvas-docs",
      "enabled": true,
      "type": "Lifecycle",
      "definition": {
        "filters": { "blobTypes": [ "blockBlob" ], "prefixMatch": [ "canvas-shared/" ] },
        "actions": { "baseBlob": { "delete": { "daysAfterModificationGreaterThan": 8 } } }
      }
    }
  ]
}
```

Two invariants keep it correct:

- **Container-scoped:** a lifecycle `prefixMatch` string must begin with the container name, so
  `"canvas-shared/"` targets **only** the canvas-share container (`canvasShare.container`, default
  `canvas-shared`) — no other blob in the account is ever deleted. If the container is renamed in
  config, change the prefix to match.
- **Window sits just past expiry:** `daysAfterModificationGreaterThan` (`8`) is one day beyond the
  7-day maximum link lifetime. Published blobs are write-once, so *modification* time equals *share*
  time; the daily lifecycle run (≈1-day granularity) therefore deletes a blob shortly *after* its SAS
  link has already expired — never while the link is live. The 7-day ceiling is fixed by Azure, so
  this number does not track a configurable value.

Apply the policy, then confirm the rule is present on the account:

```bash
az storage account management-policy create \
  --account-name <account> --resource-group <rg> \
  --policy @scripts/canvas-share-lifecycle-policy.json \
  --subscription $SUB

az storage account management-policy show \
  --account-name <account> --resource-group <rg> \
  --subscription $SUB
```

### Operator setup

The concrete Azure subscription, resource group, storage-account name, and operator identity are
deployment-specific and must not be recorded in this public spec. The provisioned account must keep
Shared Key and anonymous blob access disabled, use a private `canvas-shared` container created on
demand, and grant the operator `Storage Blob Data Contributor` at account scope.

**Subscription choice is load-bearing, not incidental.** Every provisioning, RBAC, lifecycle, and
verification command passes `--subscription $SUB` explicitly so the storage account and its policy
cannot follow an unrelated CLI default.

**Per-host setup is one line:** `az login`. `AzureCliCredential` uses the Azure CLI's persisted MSAL
token cache, so Treemon needs no credential env var or restart to install a credential.
`treemon.ps1` has no credential plumbing. Add `canvasShare.accountName` to
`~/.treemon/config.json` and sharing works:

```json
{ "canvasShare": { "accountName": "<account>" } }
```

If the host's `az login` lapses, publishing fails loudly at share time with a "run `az login`"
message — links already issued keep working, because a signed SAS does not depend on the signer's
ongoing session. Role removal or delegation-key revocation invalidates them only after Azure's cache
propagation; otherwise they live until the 7-day key expiry.

## Decisions

| # | Decision | Choice & rationale |
|---|----------|--------------------|
| 1 | Hosting backend | **Azure Blob Storage** (user's dev subscription). Rejected: private-repo GitHub Pages (anonymously **un**viewable — recipients must log in), public-repo Pages (obscurity only), gists (served `text/plain`, JS won't run), Netlify/Vercel/Cloudflare (3rd-party data egress — against policy), nginx-on-a-VM (ops overhead Blob avoids). |
| 2 | URL secrecy model | **Per-doc, blob-scoped, read-only SAS** over a reusable container token. Least privilege: a leaked link exposes exactly one doc (proven — doc A's token can't open doc B, which fails "Signature did not match"). It is **not heavy** — one delegation-key fetch + one signing call at share time, server stores nothing. |
| 3 | Credential & expiry | **User delegation SAS signed via `AzureCliCredential`; 7-day expiry (Azure's maximum).** The account rejects Shared Key authorization, so Treemon stores no account key, connection string, or credential env var; the operator's Azure CLI MSAL cache remains the host credential. Per-doc revoke = delete the blob; strongest bulk revoke = `az storage account revoke-delegation-keys`, subject to Azure cache propagation. |
| 4 | Anonymous access | **Disabled** at the account (`allow-blob-public-access=false`). Bare URL → `409`; SAS is the only entry. |
| 5 | Blob naming | **Unguessable prefix + real filename** (`<random>/<file>.html`). Random prefix gives uniqueness/obscurity; the real filename gives the recipient a meaningful name. The SAS signature is the actual gate. |
| 6 | Strip vs inject | The on-disk file is already script-free; the export **re-injects** base theme + no-op `canvasSend` and nothing else — a **third `buildInjection` mode**, not a stripping pass. |
| 7 | Clipboard | Write **`text/html` titled `<a>` + `text/plain` URL**; every app self-selects. URL length is cosmetic. Title from doc `<title>`, fallback prettified filename. |
| 8 | Scope | **Single self-contained doc** in v1. Multi-doc link bundles, custom-domain short URLs, and redirect-indirection (stable link → freshly-minted short-lived SAS) are deferred. |
| 9 | Blob lifecycle | **Auto-delete via an Azure storage lifecycle policy** — blobs older than 8 days (one day past the 7-day link ceiling) are removed, so a doc's content does not linger at rest after its link is dead (privacy) and storage does not accumulate (cost). Runs daily (≈1-day granularity); immediate per-doc revoke is still a blob delete. Rule JSON + apply/verify commands live in **Storage Account Setup** (`scripts/canvas-share-lifecycle-policy.json`). |
| 10 | Client banner state | Two **mutually-exclusive** banners: share **failure reuses** the existing dismissible error banner (`CanvasSendState.Failed`), success uses the shared dismissible `ClipboardNotice`. The success banner reflects the **actual clipboard-write outcome**, not merely that the share succeeded: because `navigator.clipboard.write` is async and can be rejected (transient user activation / an active document — both can be lost across the share network round-trip — a revoked permission, or an unavailable API), the `Ok` share arm does **not** pre-claim a copy. It clears the stale channels and fires the write, then a `ClipboardWriteResult` arm raises `Shared — link copied` on a landed write or `Shared — link ready, copy it manually: <url>` on a rejected one (the raw SAS URL is surfaced as selectable text so a failed copy is still recoverable). Each result arm clears the other channel — the `Ok` arm clears a stale `Failed`, the `Error` arm clears a stale `ClipboardNotice` — so a red + green stack can never render (a fail→retry→succeed flow is common). A live `Waiting` banner is independent and is preserved. Invariant locked by `ShareCanvasDocResultTests`. |
| 11 | Shared `prettifyFilename` | The filename→title helper is a **single source of truth in `src/Shared/Formatting.fs`**, not duplicated per side. It uses the client's `Split`-on-explicit-ASCII-whitespace body (proven Fable-safe; no `\s` Regex), so it compiles under Fable and behaves identically everywhere — a Unicode space such as U+00A0 is preserved, not collapsed (pinned by `FormattingTests`). Home is a new `Formatting.fs`, not `PathUtils.fs`, to keep `PathUtils` scoped to path comparison (module cohesion). The client's `buildClipboardPayload` **dead fallback was removed**: `WorktreeApi.shareCanvasDocImpl` always resolves a non-blank `CanvasShareResult.Title` via `resolveTitle`, so the client uses `result.Title` directly and no longer takes a `filename` arg. Fixes focused-review F4/F5. |
| 12 | Remoting CSRF exposure (F16) | **Now covered by the central pipeline guard** (`docs/spec/remoting-csrf-hardening.md`). No per-endpoint guard was added on `shareCanvasDoc`: it rides the same Remoting surface as every `IWorktreeApi` method (behind the same `withValidatedPath` worktree-membership guard as `archiveCanvasDoc`), and the single `HttpSecurity.csrfGuard` fronting that surface rejects cross-origin forged calls for all of them at once. `shareCanvasDoc` — that surface's first **data-egress** endpoint (forged call → local file published to an internet-reachable blob) — is what raised the guard's priority. See **Security Posture**. |
| 13 | Published-doc active HTML (F17) | **Accepted risk — no CSP/security headers and no sanitization in v1.** A published copy is served as active, non-sandboxed HTML/JS with only `Content-Type`. Not fixed: sanitizing/stripping `<script>` + inline handlers would defeat the interactivity goal (Decision #6), and a per-blob `Content-Security-Policy` would need a CDN/redirect-proxy — both disproportionate to a *Low*, two-stage-trigger risk (malicious JS must land verbatim in a doc **and** a human must click Share). Revisit if the audience broadens beyond the current local/dev, trusted-recipient scope. See **Security Posture**. |
| 14 | Section-divider comments (F3/F9/F10/F12) | **Removed the four `// ── … ──` dividers because they did not explain non-obvious algorithms or critical edge cases.** The three `CanvasShare.fs` banners (`pure: blob naming` / `pure: SAS grant` / `impure: publish`) only restated each function's own `///` purity doc, and the `CanvasUpdate.fs` banner was dropped in favor of the explanatory paragraph it introduced (kept as the section lead-in). This follows AGENTS.md's governing rule that production comments are limited to non-obvious algorithms and critical edge cases; the banners carried no information the per-function `///` docs didn't, and the convention isn't broadly established in production code (only `DemoFixture.fs`, a long timed-frame data fixture that fits the rule's long-fixture carve-out). Comment-only change, zero behavioral effect. |

## Security Posture

Two properties surfaced by focused-review (F16/F17, both *Low*). F16 is now **closed** by the central
Origin/Referer guard; F17 remains **explicitly accepted** for v1 rather than mitigated in code.
Recording both here so each is a documented decision, not a blind spot:

- **Remoting CSRF exposure (F16) — now closed.** `shareCanvasDoc` is dispatched over the same
  Fable.Remoting surface as every other `IWorktreeApi` method, so a page open in the operator's browser
  could in principle forge a call. Rather than a per-endpoint guard, the fix landed **once, at the
  pipeline** — the `HttpSecurity.csrfGuard` Origin/Referer allowlist (`docs/spec/remoting-csrf-hardening.md`),
  which covers the whole surface (including the more dangerous process-launching endpoints).
  `shareCanvasDoc` — the first member whose forged invocation causes **data egress** (a local canvas
  file published to an internet-reachable blob) — is what raised that fix's priority. Residual risk was
  already low even before the guard: the forger can't read the response (CORS-blocked), can't enumerate
  the machine-specific worktree path, and the feature is opt-in (no `canvasShare.accountName` ⇒
  the call fails closed before any I/O).
- **Published docs run untrusted-derived JS, non-sandboxed (F17).** A published copy is author-authored
  canvas HTML/JS served **as active content** from the storage-account origin with only
  `Content-Type: text/html` — **no CSP, no `X-Content-Type-Options`, no sanitization** — and the
  recipient opens it **top-level**, not in the sandboxed iframe the live pane uses at `127.0.0.1:5002`
  (`CanvasDocServer`'s `frame-ancestors` CSP). So for the life of the link, whatever JS the doc
  contains executes in the recipient's browser. This is **accepted, not fixed** (Decision #13):
  stripping scripts/handlers would defeat the feature's interactivity goal (Decision #6), and attaching
  response headers like CSP would require a CDN/redirect-proxy in front of the blob (Azure Blob can't
  set a per-blob CSP). The trigger is two-stage — malicious JS must first land verbatim in a doc (a
  successful prompt injection), *then* a human must click **Share** on that doc — the SAS is per-doc,
  blob-scoped and read-only (a leaked link exposes only that one doc), and the doc runs on the
  storage-account origin, **not** on any Treemon/localhost origin, so it cannot reach the local pane or
  the Remoting API. Revisit (CDN/proxy for CSP + `X-Content-Type-Options`, or a `<script>`/handler
  sanitizer that accepts the interactivity loss) if the audience broadens — e.g. docs routinely
  embedding raw external text, or recipients outside a trusted circle.

## Key Files

| File | Purpose |
|------|---------|
| `src/Shared/Types.fs` | `ShareCanvasDocRequest`, `CanvasShareResult`, `IWorktreeApi.shareCanvasDoc` |
| `src/Server/CanvasExport.fs` | `StaticExport` transform: base theme + no-op `canvasSend`; `extractTitle` / `resolveTitle` |
| `src/Shared/Formatting.fs` | `prettifyFilename` (filename → sentence-case title) — the single Fable-safe source shared by the server's `resolveTitle` and any client caller (Decision #11) |
| `src/Server/CanvasShare.fs` | Azure Blob upload + per-doc read-only user delegation SAS; reads config, uses `AzureCliCredential`, and caches one service client per account |
| `src/Server/Server.fsproj` | `Azure.Storage.Blobs` + `Azure.Identity` package references |
| `scripts/canvas-share-lifecycle-policy.json` | Storage lifecycle rule — deletes canvas-share blobs after 8 days (see **Storage Account Setup**) |
| `src/Server/WorktreeApi.fs` | `shareCanvasDocImpl` + live wiring (`withValidatedPath`) + demo-mode stub |
| `src/Server/GlobalConfig.fs` | Reads the `canvasShare` config section (`accountName`, `container`, `defaultExpiryDays`) |
| `src/Client/CanvasPane.fs` | Share button (AgentDoc-only, spinner + disabled while in flight) + `ShareDoc` callback + success banner |
| `src/Client/CanvasState.fs` | Scoped `ShareState` phase machine that drives the global share lock and matching spinner |
| `src/Client/CanvasUpdate.fs` | `ShareCanvasDoc` / `ShareCanvasDocResult` / `ClipboardWriteResult` arms + dual-format clipboard write (outcome-routed banner) |
| `src/Client/index.html` | Share button styling + `.canvas-share-btn.sharing` spinner |
| `src/Tests/*` | Static-export transform tests, publish-backend unit tests (naming, delegation signing, SAS grant, client reuse, config, unconfigured gate), share-state/clipboard tests, Share-button AgentDoc gating |

## Verification

- **Backend unit tests** cover the pure and deterministic parts: blob naming and prefix entropy, the
  exact SAS grant (`sr=b`/`sp=r`/`spr=https`), per-account service-client reuse, the config reader
  (including the 7-day ceiling), and the unconfigured gate — which must fail *before* acquiring a
  credential or touching the network.
- **No Azurite round-trip.** The emulator does not implement `GetUserDelegationKey` at all, so it
  cannot emulate this design.
- **Backend round-trip is verified against the real account** by running the actual
  `CanvasShare.publish` path: publish → `GET` the returned link renders `200 text/html; charset=utf-8`
  with the doc intact; the bare blob URL is denied `409 PublicAccessNotPermitted`; the same token
  applied to a different blob path fails `AuthenticationFailed` / "Signature did not match" (the
  per-doc isolation property); and a >7-day expiry is refused when the delegation key is minted.
- **Client** is covered by unit tests for the scoped share-state transitions, stale-result guards,
  global button lock/matching spinner, and clipboard-payload builder (both formats), plus a
  view-level test that the Share button is AgentDoc-only (mirroring the archive-button SystemView
  gating test in `CanvasPaneTests`).
- The **end-to-end UI clipboard write + paste** (button click → `navigator.clipboard.write` of both
  formats → paste a titled link into a rich app) is confirmed **manually**: browser clipboard
  automation is permission-gated and flaky, so it is not automated. Its constituent parts (payload
  builder, button gating, published-doc render) are covered above.
- The **lifecycle cleanup policy** is confirmed at setup level — its rule is present on the account
  via `az storage account management-policy show` (see **Storage Account Setup**); the actual
  age-based deletion is not run-time testable and is not automated.

## Related Specs

- `docs/spec/canvas-pane.md` — the canvas pane this Share button lives in (tab bar, AgentDoc vs SystemView, archive precedent)
- `docs/spec/canvas-interaction-routing.md` — per-doc ownership/routing (the Share button is AgentDoc-scoped like liveness/archive)
- `.agents/canvas-sharing-investigation.md` — the investigation this spec derives from (options analysis, manual Azure prototype)
