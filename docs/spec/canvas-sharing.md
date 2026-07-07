# Canvas Doc Sharing

## Goals

- One-click **Share** of a focused canvas doc to an **unguessable URL** any recipient can open in a
  plain browser — **no login** — that renders the doc's HTML+JS read-only with agent-interactivity
  neutralized.
- **Recipient-only secrecy:** the link is a per-doc capability (a leaked link exposes only that one
  doc) and **auto-expires** (default 3 months).
- **Hide the ugly URL:** copy a **rich titled hyperlink** to the clipboard so the raw URL length is
  cosmetically irrelevant when pasted into chat/mail.

## Expected Behavior

### Share action

- A **Share** button appears in the canvas tab bar next to Archive, for **`AgentDoc` docs only**
  (a `SystemView` like the beads dashboard is server-generated and not shareable).
- Clicking it: static-exports the focused doc → uploads it to Azure Blob Storage → mints a per-doc
  read-only SAS URL → writes a **rich link + plain URL** to the clipboard → shows a success banner
  (`Shared — link copied`). On failure it shows the existing dismissible error banner.
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
  expiry. Because it is blob-scoped, a recipient of doc A's link **cannot** read doc B even if they
  guess B's name (least privilege; verified by isolation test).
- **Revocation** is per-doc: delete the blob → the link returns `404`. (No central revoke; rotating
  the account key would invalidate all links — the nuclear option.)
- **Lifecycle cleanup:** an Azure storage **lifecycle policy** deletes shared blobs older than the
  expiry window (default 90 days), so a doc's content does not linger at rest after its link is dead
  (privacy) and storage does not accumulate (cost). The policy runs daily (≈1-day granularity);
  immediate per-doc revoke is still a blob delete.

### Clipboard (rich link)

On success the client writes **two clipboard formats at once** via the async Clipboard API:

- `text/html` = `<a href="<sas-url>"><title></a>` — rich targets (Teams, Slack, Google Chat,
  Outlook, Gmail, Word) render a **titled hyperlink**.
- `text/plain` = the raw SAS URL — plain targets (VS Code editor, terminal, Notepad) get the URL.

The title is the doc's `<title>`, falling back to a prettified filename
(`build-status.html` → `Build status`). Because the URL is hidden behind the title, its length does
not matter.

The clipboard write is async and its outcome is **routed back into the update** (`ClipboardWriteResult`)
rather than being fire-and-forget: the success banner confirms `Shared — link copied` only once the
write actually lands, and a rejected write (transient activation lost across the share round-trip, a
revoked permission, or an unsupported API — the last throws synchronously and is caught) is corrected
to `Shared — link ready, copy it manually: <url>` with the raw SAS URL shown as selectable text. The
banner never claims a copy that did not happen (Decision #10).

### Configuration

- The share backend is configured in the machine-level Treemon config (`~/.treemon/config.json`,
  read via `GlobalConfig`): a `canvasShare` section with `container` and `defaultExpiryDays`
  (default `90`; **bounded to `1–3650` days** — a value outside that range falls back to the default,
  keeping the SAS expiry bounded per Decision #3 and preventing a `DateTimeOffset` overflow at publish).
- The Azure **credential is a secret** and is read from the `AZURE_STORAGE_CONNECTION_STRING`
  environment variable (preferred), not the JSON file. Config may also name the account; the secret
  stays in the env var.
- If the backend is unconfigured (no connection string), the Share action returns a clear
  `Result.Error` ("Canvas sharing is not configured — set AZURE_STORAGE_CONNECTION_STRING …") that
  the client surfaces in the error banner. Nothing is logged that contains the key or the full SAS.
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
- **Publish backend** (`src/Server/CanvasShare.fs`, new): use `Azure.Storage.Blobs` —
  `BlobContainerClient` (private), `UploadBlobAsync(randomPrefix/filename, html)` with
  `BlobHttpHeaders.ContentType = "text/html"`, then `BlobClient.GenerateSasUri(BlobSasBuilder with
  BlobSasPermissions.Read, ExpiresOn = now + expiry, Protocol = Https)`. Backend reads config +
  `AZURE_STORAGE_CONNECTION_STRING`. Random prefix is a high-entropy base62 id. The container is
  created on demand — `CreateIfNotExists(PublicAccessType.None)`, placed *after* the
  `CanGenerateSasUri` gate (so a SAS-only credential is still refused offline before any I/O) and
  inside the existing `try` (so a create failure reuses the same `RequestFailedException` handler) —
  so a fresh account works on first publish without a manual container-create step (F13). Add the
  `Azure.Storage.Blobs` package to `Server.fsproj`.
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
  in `index.html`.

## Storage Account Setup

One-time, **local-only** provisioning of the private account, run by an operator with `az login` to
the dev subscription (never from CI). Both settings below are **control-plane / ARM** operations, so
they use the logged-in account — **not** the `AZURE_STORAGE_CONNECTION_STRING` data-plane key, which
cannot set account policy:

- **Anonymous access disabled** (Decision #4):
  `az storage account update -n <account> -g <rg> --allow-blob-public-access false` — a bare blob URL
  is then denied, so the per-doc SAS is the only way in.
- **Lifecycle cleanup** (Decision #9): the management policy below deletes shared blobs after the
  expiry window so expired-link content does not linger at rest (privacy) and storage does not
  accumulate (cost).

> **The container itself needs no manual step.** The app creates the private
> `canvasShare.container` (default `canvas-shared`) on demand on first publish, via
> `CreateIfNotExists(PublicAccessType.None)` in `CanvasShare.publish` — a *data-plane* operation
> covered by the `AZURE_STORAGE_CONNECTION_STRING` account key (the same key that signs the SAS), so
> a fresh account/subscription works without a manual `az storage container create`. The call is
> idempotent (a no-op once the container exists) and keeps anonymous access off at the container
> level, complementing the account-level toggle above. Only the two ARM settings above require the
> `az login` account.

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
        "actions": { "baseBlob": { "delete": { "daysAfterModificationGreaterThan": 90 } } }
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
- **Window matches expiry:** `daysAfterModificationGreaterThan` (`90`) mirrors `defaultExpiryDays`.
  Published blobs are write-once, so *modification* time equals *share* time; the daily lifecycle run
  (≈1-day granularity) therefore deletes a blob ~0–1 day *after* its SAS link has already expired —
  never while the link is live. If an operator raises `defaultExpiryDays`, raise this to match (or to
  the largest expiry in use).

Apply the policy, then confirm the rule is present on the account:

```bash
az storage account management-policy create \
  --account-name <account> --resource-group <rg> \
  --policy @scripts/canvas-share-lifecycle-policy.json

az storage account management-policy show \
  --account-name <account> --resource-group <rg>
```

### Provisioned account & operator credential

The concrete account backing this deployment, plus the one manual step an operator must perform on
each host that runs the production server.

**Provisioned account** (user's dev subscription, `CodeTestingAgentDev`):

| Setting | Value |
|---|---|
| Storage account | `tmcanvas92r3du` |
| Resource group | `rg-treemon-canvas-share` |
| Location | `eastus` |
| Container | `canvas-shared` (private; created on demand on first publish) |

**Operator credential (per host).** The account key is never committed and never written to
`config.json`; each host supplies it through the `AZURE_STORAGE_CONNECTION_STRING` env var (read only
from the environment by `CanvasShare.connectionString`). On Windows, set it once at **User scope** so
it survives reboots and every `treemon.ps1` (re)start, then restart the server so the freshly-launched
process inherits it:

```powershell
$conn = az storage account show-connection-string `
  -n tmcanvas92r3du -g rg-treemon-canvas-share --query connectionString -o tsv
[Environment]::SetEnvironmentVariable('AZURE_STORAGE_CONNECTION_STRING', $conn, 'User')
.\treemon.ps1 restart   # new server process inherits the env var
```

`treemon.ps1` reads the persisted `AZURE_STORAGE_CONNECTION_STRING` (User scope, then Machine) straight
from the registry-backed store and injects it into the server process at launch (`Set-CanvasShareEnv`),
so `start`/`restart`/`deploy` pick up the credential from **any** shell — even one opened before the
variable was set. Setting the User-scope variable once is therefore all an operator needs; no terminal
restart or reboot is required. (The secret is still only ever read from the env var — never a file or
`config.json`.)

> The connection string embeds the account key — treat it as a secret. It is never logged and never
> persisted to `config.json`; only `AZURE_STORAGE_CONNECTION_STRING` carries it into the server.

## Decisions

| # | Decision | Choice & rationale |
|---|----------|--------------------|
| 1 | Hosting backend | **Azure Blob Storage** (user's dev subscription). Rejected: private-repo GitHub Pages (anonymously **un**viewable — recipients must log in), public-repo Pages (obscurity only), gists (served `text/plain`, JS won't run), Netlify/Vercel/Cloudflare (3rd-party data egress — against policy), nginx-on-a-VM (ops overhead Blob avoids). |
| 2 | URL secrecy model | **Per-doc, blob-scoped, read-only SAS** over a reusable container token. Least privilege: a leaked link exposes exactly one doc (proven — doc A's token can't open doc B). It is **not heavy** — one `GenerateSasUri` call at share time, server stores nothing. |
| 3 | Expiry | **3 months (90 days), bounded** — configurable default. *Consequence:* >7-day expiry **forces account-key SAS**; a user-delegation SAS (the no-stored-key hardening) is hard-capped at 7 days and is therefore **not** used in v1. The account key is supplied via `AZURE_STORAGE_CONNECTION_STRING`; per-doc revoke = delete the blob. |
| 4 | Anonymous access | **Disabled** at the account (`allow-blob-public-access=false`). Bare URL → `409`; SAS is the only entry. |
| 5 | Blob naming | **Unguessable prefix + real filename** (`<random>/<file>.html`). Random prefix gives uniqueness/obscurity; the real filename gives the recipient a meaningful name. The SAS signature is the actual gate. |
| 6 | Strip vs inject | The on-disk file is already script-free; the export **re-injects** base theme + no-op `canvasSend` and nothing else — a **third `buildInjection` mode**, not a stripping pass. |
| 7 | Clipboard | Write **`text/html` titled `<a>` + `text/plain` URL**; every app self-selects. URL length is cosmetic. Title from doc `<title>`, fallback prettified filename. |
| 8 | Scope | **Single self-contained doc** in v1. Multi-doc link bundles, custom-domain short URLs, and redirect-indirection (stable link → freshly-minted short-lived SAS) are deferred. |
| 9 | Blob lifecycle | **Auto-delete via an Azure storage lifecycle policy** — blobs older than the expiry window (default 90 days) are removed, so a doc's content does not linger at rest after its link is dead (privacy) and storage does not accumulate (cost). Runs daily (≈1-day granularity); immediate per-doc revoke is still a blob delete. Rule JSON + apply/verify commands live in **Storage Account Setup** (`scripts/canvas-share-lifecycle-policy.json`). |
| 10 | Client banner state | Two **mutually-exclusive** banners: share **failure reuses** the existing dismissible error banner (`CanvasSendState.Failed`), success uses a **new** dismissible `ShareNotice`. The success banner reflects the **actual clipboard-write outcome**, not merely that the share succeeded: because `navigator.clipboard.write` is async and can be rejected (transient user activation / an active document — both can be lost across the share network round-trip — a revoked permission, or an unavailable API), the `Ok` share arm does **not** pre-claim a copy. It clears the stale channels and fires the write, then a `ClipboardWriteResult` arm raises `Shared — link copied` on a landed write or `Shared — link ready, copy it manually: <url>` on a rejected one (the raw SAS URL is surfaced as selectable text so a failed copy is still recoverable). Each result arm clears the other channel — the `Ok` arm clears a stale `Failed`, the `Error` arm clears a stale `ShareNotice` — so a red + green stack can never render (a fail→retry→succeed flow is common). A live `Waiting` banner is independent and is preserved. Invariant locked by `ShareCanvasDocResultTests`. |
| 11 | Shared `prettifyFilename` | The filename→title helper is a **single source of truth in `src/Shared/Formatting.fs`**, not duplicated per side. It uses the client's `Split`-on-explicit-ASCII-whitespace body (proven Fable-safe; no `\s` Regex), so it compiles under Fable and behaves identically everywhere — a Unicode space such as U+00A0 is preserved, not collapsed (pinned by `FormattingTests`). Home is a new `Formatting.fs`, not `PathUtils.fs`, to keep `PathUtils` scoped to path comparison (module cohesion). The client's `buildClipboardPayload` **dead fallback was removed**: `WorktreeApi.shareCanvasDocImpl` always resolves a non-blank `CanvasShareResult.Title` via `resolveTitle`, so the client uses `result.Title` directly and no longer takes a `filename` arg. Fixes focused-review F4/F5. |
| 12 | Remoting CSRF exposure (F16) | **No per-endpoint guard on `shareCanvasDoc`; deferred to the central pipeline fix.** It rides the same unauthenticated, Origin-unvalidated Remoting surface as every `IWorktreeApi` method and is *consistent* with it — the same `withValidatedPath` worktree-membership guard as `archiveCanvasDoc` — so no per-endpoint auth/CSRF/path restriction is added. The correct fix is the single central Origin/Referer allowlist middleware already designed in `docs/spec/future/remoting-csrf-hardening.md`. `shareCanvasDoc` is that surface's first **data-egress** endpoint (forged call → local file published to an internet-reachable blob), which *raises the priority* of that hardening but does not change its design. See **Security Posture**. |
| 13 | Published-doc active HTML (F17) | **Accepted risk — no CSP/security headers and no sanitization in v1.** A published copy is served as active, non-sandboxed HTML/JS with only `Content-Type`. Not fixed: sanitizing/stripping `<script>` + inline handlers would defeat the interactivity goal (Decision #6), and a per-blob `Content-Security-Policy` would need a CDN/redirect-proxy — both disproportionate to a *Low*, two-stage-trigger risk (malicious JS must land verbatim in a doc **and** a human must click Share). Revisit if the audience broadens beyond the current local/dev, trusted-recipient scope. See **Security Posture**. |
| 14 | Section-divider comments (F3/F9/F10/F12) | **Removed the four `// ── … ──` dividers rather than loosening the `no-unnecessary-comments` rule.** The three `CanvasShare.fs` banners (`pure: blob naming` / `pure: SAS grant` / `impure: publish`) only restated each function's own `///` purity doc, and the `CanvasUpdate.fs` banner was dropped in favor of the explanatory paragraph it introduced (kept as the section lead-in). Chose deletion over the review's rule-refinement option because AGENTS.md's *"no unnecessary comments — code should be self-documenting"* is the governing value, the banners carried no information the per-function `///` docs didn't, and the convention isn't broadly established in production code (only `DemoFixture.fs`, a long timed-frame data fixture that fits the rule's long-fixture carve-out). Comment-only change, zero behavioral effect. |

## Security Posture

Two properties surfaced by focused-review (F16/F17, both *Low*) are **explicitly accepted** for v1
rather than mitigated in code. Recording them here so each is a documented trade-off, not a blind spot:

- **Remoting CSRF exposure (F16).** `shareCanvasDoc` is dispatched over the same unauthenticated,
  Origin/CSRF-unvalidated Fable.Remoting surface as every other `IWorktreeApi` method, so a page open
  in the operator's browser could in principle forge a call. It is handled **exactly like its peers**
  (the same `withValidatedPath` worktree-membership guard as `archiveCanvasDoc`), so it gets **no
  per-endpoint guard**; the fix belongs once, at the pipeline — the Origin/Referer allowlist middleware
  already designed in `docs/spec/future/remoting-csrf-hardening.md`, which covers the whole surface
  (including the more dangerous process-launching endpoints). `shareCanvasDoc` is the first member
  whose forged invocation causes **data egress** (a local canvas file published to an
  internet-reachable blob), which raises that fix's priority without changing its design. Residual risk
  stays low: the forger can't read the response (CORS-blocked), can't enumerate the machine-specific
  worktree path, and the feature is opt-in (no `AZURE_STORAGE_CONNECTION_STRING` ⇒ the call fails
  closed before any I/O).
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
| `src/Server/CanvasShare.fs` (new) | Azure Blob upload + per-doc read-only SAS; reads config + `AZURE_STORAGE_CONNECTION_STRING` |
| `src/Server/Server.fsproj` | Add `Azure.Storage.Blobs` package reference |
| `scripts/canvas-share-lifecycle-policy.json` (new) | Storage lifecycle rule — deletes canvas-share blobs older than the expiry window (see **Storage Account Setup**) |
| `src/Server/WorktreeApi.fs` | `shareCanvasDocImpl` + live wiring (`withValidatedPath`) + demo-mode stub |
| `src/Server/GlobalConfig.fs` | Reads the `canvasShare` config section (`container`, `defaultExpiryDays`) |
| `src/Client/CanvasPane.fs` | Share button (AgentDoc-only) + `ShareDoc` callback + success banner |
| `src/Client/CanvasUpdate.fs` | `ShareCanvasDoc` / `ShareCanvasDocResult` / `ClipboardWriteResult` arms + dual-format clipboard write (outcome-routed banner) |
| `src/Client/index.html` | Share button styling |
| `src/Tests/*` | Static-export transform tests, publish-backend tests (Azurite), clipboard payload test, Share-button AgentDoc-gating unit test (mirrors the archive-button SystemView-gating test) |

## Verification

- **Backend round-trip** is verified automatically against Azurite (CI) and faithfully against the
  real dev account (publish → SAS fetch renders themed + inert; bare URL `409`; cross-doc SAS `403`;
  expired SAS `403`; delete → `404`).
- **Client** is covered by a unit test of the clipboard-payload builder (both formats) and a
  view-level unit test that the Share button is AgentDoc-only (mirroring the archive-button
  SystemView-gating test in `CanvasPaneTests`).
- The **end-to-end UI clipboard write + paste** (button click → `navigator.clipboard.write` of both
  formats → paste a titled link into a rich app) is confirmed **manually**: browser clipboard
  automation is permission-gated and flaky, so it is not automated. Its constituent parts (payload
  builder, button gating, published-doc render) are covered above.
- The **lifecycle cleanup policy** is confirmed at setup level — its rule is present on the account
  via `az storage account management-policy show` (see **Storage Account Setup**); the actual
  age-based deletion (≥ expiry window) is not run-time testable and is not automated.

## Related Specs

- `docs/spec/canvas-pane.md` — the canvas pane this Share button lives in (tab bar, AgentDoc vs SystemView, archive precedent)
- `docs/spec/canvas-doc-ownership.md` — per-doc ownership/routing (the Share button is AgentDoc-scoped like liveness/archive)
- `.agents/canvas-sharing-investigation.md` — the investigation this spec derives from (options analysis, manual Azure prototype)
