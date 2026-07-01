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
  real filename** (`<random-id>/<filename>`), with `Content-Type: text/html`. The real filename is
  kept so the recipient sees a meaningful page/tab title.
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

### Configuration

- The share backend is configured in the machine-level Treemon config (`~/.treemon/config.json`,
  read via `GlobalConfig`): a `canvasShare` section with `container` and `defaultExpiryDays`
  (default `90`).
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
  `injectAtHead` both call. Also exposes `extractTitle (html) : string option`, `prettifyFilename`,
  and `resolveTitle html filename` (the `<title>`→prettified-filename fallback the server returns as
  `CanvasShareResult.Title`). Every function is a pure `string→string`/`string option` for unit
  testing.
- **Publish backend** (`src/Server/CanvasShare.fs`, new): use `Azure.Storage.Blobs` —
  `BlobContainerClient` (private), `UploadBlobAsync(randomPrefix/filename, html)` with
  `BlobHttpHeaders.ContentType = "text/html"`, then `BlobClient.GenerateSasUri(BlobSasBuilder with
  BlobSasPermissions.Read, ExpiresOn = now + expiry, Protocol = Https)`. Backend reads config +
  `AZURE_STORAGE_CONNECTION_STRING`. Random prefix is a high-entropy base62 id. Add the
  `Azure.Storage.Blobs` package to `Server.fsproj`.
- **Server wiring** (`src/Server/WorktreeApi.fs`): `shareCanvasDocImpl` =
  `validateCanvasPath → read file → CanvasExport.buildStaticHtml → CanvasShare.publish → Result`,
  wired into the live `IWorktreeApi` record via `withValidatedPath` (mirroring `archiveCanvasDoc`),
  plus the demo-mode stub.
- **Client** (`src/Client/CanvasPane.fs`, `CanvasUpdate.fs`, `index.html`): a Share button in
  `headerBar` (AgentDoc-only, beside Archive) raising a new `ShareDoc` callback in
  `CanvasPaneCallbacks`; `ShareCanvasDoc` / `ShareCanvasDocResult` update arms in `CanvasUpdate.fs`;
  on `Ok { Url; Title }`, write the two clipboard formats with
  `navigator.clipboard.write([new ClipboardItem({ "text/html": …, "text/plain": … })])` and show a
  success banner; on `Error`, reuse the error banner. Button styling in `index.html`.

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
| 9 | Blob lifecycle | **Auto-delete via an Azure storage lifecycle policy** — blobs older than the expiry window (default 90 days) are removed, so a doc's content does not linger at rest after its link is dead (privacy) and storage does not accumulate (cost). Runs daily (≈1-day granularity); immediate per-doc revoke is still a blob delete. |

## Key Files

| File | Purpose |
|------|---------|
| `src/Shared/Types.fs` | `ShareCanvasDocRequest`, `CanvasShareResult`, `IWorktreeApi.shareCanvasDoc` |
| `src/Server/CanvasDocServer.fs` or new `src/Server/CanvasExport.fs` | `StaticExport` transform: base theme + no-op `canvasSend`; `extractTitle` / `prettifyFilename` |
| `src/Server/CanvasShare.fs` (new) | Azure Blob upload + per-doc read-only SAS; reads config + `AZURE_STORAGE_CONNECTION_STRING` |
| `src/Server/Server.fsproj` | Add `Azure.Storage.Blobs` package reference |
| `src/Server/WorktreeApi.fs` | `shareCanvasDocImpl` + live wiring (`withValidatedPath`) + demo-mode stub |
| `src/Server/GlobalConfig.fs` | Reads the `canvasShare` config section (`container`, `defaultExpiryDays`) |
| `src/Client/CanvasPane.fs` | Share button (AgentDoc-only) + `ShareDoc` callback + success banner |
| `src/Client/CanvasUpdate.fs` | `ShareCanvasDoc` / `ShareCanvasDocResult` arms + dual-format clipboard write |
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
- The **lifecycle cleanup policy** is confirmed at setup level (its rule is present on the account);
  the actual age-based deletion (≥ expiry window) is not run-time testable and is not automated.

## Related Specs

- `docs/spec/canvas-pane.md` — the canvas pane this Share button lives in (tab bar, AgentDoc vs SystemView, archive precedent)
- `docs/spec/canvas-doc-ownership.md` — per-doc ownership/routing (the Share button is AgentDoc-scoped like liveness/archive)
- `.agents/canvas-sharing-investigation.md` — the investigation this spec derives from (options analysis, manual Azure prototype)
