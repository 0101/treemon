# Canvas Doc Ownership & Auto-Resume

## Goals

- Route canvas-doc → agent messages to the doc's **owning session**, even when multiple sessions share one worktree (fixes cross-session misrouting)
- Track per-doc authoring session ID via **explicit declaration from the authoring session** (not last-registered inference)
- Persist ownership across server restarts so session affinity survives reboots
- Auto-resume a dead owner session when the user interacts with its doc (postMessage from iframe)
- Fall back to starting a new session if owner is unknown or resume fails
- Show per-doc liveness in the UI (owner session alive vs dead)

## Scope of This Change

This branch **already implements** most of the end-state below; do **not** re-implement it. Already present and working:

- `CanvasDocOwnership.fs` — the ownership module (`attribute`/`getOwner`/`getAll`/`load`) with JSON persistence to `data/canvas-owners.json`, loaded at startup by `Program.fs`.
- `Types.fs` — `CanvasDoc.OwnerSessionId` and `CanvasMessageRequest.Filename`.
- `WorktreeApi.fs` — auto-resume-on-queue + new-session fallback in `sendCanvasMessage`.
- `CanvasPane.fs` — per-doc liveness (`isDocAlive` via `OwnerSessionId`).
- `App.fs` — passes the focused doc's `Filename` in `CanvasMessageRequest`.

What this feature **delivers** (the actual bug fix — attribution and routing today are buggy and feed the otherwise-correct flows above):

- `CanvasBridge.fs` — re-key the registry from worktree path to **`sessionId`** (it currently clobbers a single worktree slot, so two sessions in one worktree overwrite each other) and route `sendMessage` by **doc owner** (never cross-route to a non-owner).
- `RefreshScheduler.fs` — make scanner attribution **fallback-only** (it currently credits the last-registered session for every changed doc — the misattribution bug).
- `/api/canvas/attribute` endpoint + extension/SKILL **explicit ownership declaration**.
- App.fs canvas extraction (`AppTypes.fs` + `CanvasUpdate.fs`) — see `docs/spec/canvas-pane.md`.

The sections below describe the **target end-state**, not a green-field build.

## Expected Behavior

### Ownership Attribution

When an agent creates or updates a canvas doc, the **authoring session declares ownership explicitly**: the extension — which holds that session's own `sessionId` — POSTs `{worktreePath, filename, sessionId}` to Treemon's `/api/canvas/attribute` endpoint. This is authoritative because the declaration comes from the one process that actually wrote the file.

This **replaces** the previous `FileSystemWatcher` inference, which credited whichever session was *registered last* for the worktree and therefore misattributed (and misrouted) docs whenever two sessions shared a worktree. The watcher path is kept only as a best-effort fallback for docs with **no** declared owner: if exactly one session is registered for the worktree it may be attributed; otherwise the doc is left unowned.

Ownership is stored as `Map<worktreePath, Map<filename, string>>` (worktree → filename → sessionId), persisted to `data/canvas-owners.json` on every change. Loaded on server startup.

`CanvasDoc.OwnerSessionId` is populated from this ownership map during canvas scans, so the client always receives the current owner.

### Auto-Resume on Interaction

When a user interacts with a canvas doc (sends a postMessage) and no active bridge can deliver the message:

1. Look up the owner session ID for the doc being interacted with
2. Resolve the coding tool provider for the worktree
3. Build a targeted resume command: `CodingToolCli.build provider (Resume (Some ownerSessionId))`
4. Resume via `SessionManager.spawnSession`, which starts the targeted session for the worktree
5. Queue the message for delivery when the resumed session re-registers its bridge
6. Return `Queued` to the client (shows "Waiting for session…" banner)

### Fallback: New Session

If the owner session ID is unknown (doc was created without a registered bridge) or the resume attempt fails:

- Start a new session with the doc as context — identical to the existing "▶ Start session" button behavior (`LaunchCanvasSession` in App.fs)
- Queue the message as before

The "▶ Start session" button itself is unchanged — it always starts a fresh session.

### Per-Doc Liveness

The liveness dot in `CanvasPane.fs` becomes per-doc rather than per-worktree:

- For each doc, map `CanvasDoc.OwnerSessionId` → `BridgeLiveness` via the existing `getBridgeLiveness` data
- 🟢 if the owner's bridge session is alive
- ⚪ if no owner or owner's session is dead

This replaces the current per-worktree `isWorktreeAlive` check in the tab bar and overview.

### Bridge Registry (per session)

The bridge registry is keyed by **`sessionId`**, not worktree path, so multiple sessions in one worktree coexist instead of overwriting each other's slot. Each entry carries `{ WorktreePath; InjectUrl; SessionId; RegisteredAt }`. `registerSession` upserts by `sessionId`. A helper `sessionsForWorktree: worktreePath -> SessionEntry list` backs worktree-level fallbacks and liveness.

### Message Routing (by owner)

`CanvasBridge.sendMessage` routes a doc's message to that doc's **owner**:

1. Resolve `owner = CanvasDocOwnership.getOwner worktree filename`.
2. Owner has a live registry entry → POST to that session's `InjectUrl` (on HTTP failure, fall through to queue).
3. Owner offline/gone, **or** no owner and not exactly one live session → enqueue (`Queued`).
4. No owner **and** exactly one live session for the worktree → deliver to it (single-session back-compat).

A doc's message is **never** delivered to a non-owner session in the same worktree.

### What Doesn't Change

- The "▶ Start session" button always starts a fresh session, not a resume.
- The message queue (cap 10, 5-min TTL) and drain-on-register are unchanged; they now drain to the owner-resumed session.
- Auto-resume on interaction is unchanged in spirit — it now resumes the *correct* owner because attribution is explicit.

## Technical Approach

### Types (Shared)

```fsharp
type CanvasDoc =
    { Filename: string
      ContentHash: string
      LastModified: DateTimeOffset
      OwnerSessionId: string option }

type CanvasMessageRequest =
    { WorktreePath: WorktreePath
      Filename: string
      Payload: string }
```

`CanvasMessageRequest` includes `Filename` so the server knows which doc the message originates from, enabling owner-specific routing and resume. `App.fs` passes the focused doc's filename.

### Authorship Declaration (Extension + Skill)

- `src/Extension/extension.mjs` already holds the session's `sessionId` (sent at registration). It gains a tiny local hook: when the agent writes a canvas doc, the agent pings the local bridge with just the `filename`; the extension stamps in its `sessionId` and POSTs `{worktreePath, filename, sessionId}` to Treemon `/api/canvas/attribute`.
- `src/Extension/skill/SKILL.md` instructs the agent to declare ownership whenever it creates or updates a canvas doc. The agent never needs to know its own sessionId — the extension owns that.
- Treemon's `/api/canvas/attribute` handler validates the worktree and calls `CanvasDocOwnership.attribute`.

### Bridge Registry (Server)

`CanvasBridge.fs` re-keys `sessionRegistry` from `worktreePath` to `sessionId`:

- Entry: `{ WorktreePath; InjectUrl; SessionId; RegisteredAt }`.
- `registerSession` upserts by `sessionId` (no longer clobbers a worktree's single slot).
- `sessionsForWorktree` and the liveness helpers iterate sessions for a worktree.
- `sendMessage` resolves the owner and routes per "Message Routing (by owner)" above.

### Ownership Module (Server)

`CanvasDocOwnership.fs` handles ownership tracking and persistence:

- `attribute: worktreePath -> filename -> sessionId -> unit` — records ownership and persists it
- `getOwner: worktreePath -> filename -> string option` — looks up owner
- `getAll: worktreePath -> Map<string, string>` — returns all owners for a worktree
- `load: unit -> unit` — loads `data/canvas-owners.json` on startup
- Internal state: `ConcurrentDictionary` matching the pattern in `CanvasBridge.fs`

### Scanner Integration

In `RefreshScheduler.fs`, the `CanvasScanner` file watcher callback is now **fallback-only**:
1. After re-scanning docs, populate `CanvasDoc.OwnerSessionId` from the ownership map during scan
2. For changed docs with **no** declared owner, attribute to the worktree's bridge session **only when exactly one is registered** (avoids the last-registered misattribution); otherwise leave unowned
3. Explicit `/api/canvas/attribute` declarations are the primary attribution path

### Message Send Flow

In `WorktreeApi.fs`, `sendCanvasMessage` wraps `CanvasBridge.sendMessage` (which now routes by owner — see "Message Routing (by owner)"):

1. Call `CanvasBridge.sendMessage` (delivers to the owner session when alive)
2. If result is `Queued`:
   a. Look up doc owner via `CanvasDocOwnership.getOwner`
   b. If owner is found: build `CodingToolCli.Resume (Some ownerSessionId)` and call `SessionManager.spawnSession`
   c. If no owner exists or resume fails: build a new interactive session command (matching the `LaunchCanvasSession` prompt shape) and call `SessionManager.spawnSession`
   d. Either way, the message stays queued for delivery on bridge re-registration
3. Return the original `Queued` result to the client

### Client Behavior

In `CanvasPane.fs`, liveness is computed per doc:
- Use `CanvasDoc.OwnerSessionId` and the `BridgeLiveness` map (which already includes `SessionId`)
- Match doc owner → any bridge entry whose `SessionId` matches → use its `IsAlive`

In `App.fs`, the focused doc's filename is passed in `CanvasMessageRequest`.

### Fixture Data

`src/Tests/fixtures/worktrees.json` includes `OwnerSessionId` in `CanvasDoc` entries (can be `null`).

## Decisions

| # | Decision | Choice |
|---|----------|--------|
| 1 | Ownership attribution trigger | **Explicit declaration** from the authoring session's extension (`POST /api/canvas/attribute`); file-watcher inference kept only as a single-session fallback |
| 1b | Bridge registry keying | Keyed by **`sessionId`** (was worktree path) — multiple sessions per worktree coexist |
| 1b-i | `sessionId=None` registry key | Namespaced fallback key `wt:<normalizedWorktree>` (vs `sid:<sessionId>` for identified sessions). Two `None` registrations for one worktree collapse to this single slot; distinct sessionIds (and `None` + an id) coexist. Never clobbers an identified session's slot. |
| 1b-ii | Freshest-session determinism | A monotonic registration clock issues strictly-increasing `RegisteredAt`, so single-status views (`getStatus`, `getSessionForWorktree`, `getAllLiveness`) deterministically report the **last-registered** session for a worktree even under same-tick registrations. |
| 1c | Delivery routing | Route by doc **owner** sessionId; fall back to the single live session, else queue; never cross-route to a non-owner |
| 1d | sessionId source | Extension **stamps its own** sessionId; the agent only sends the filename |
| 2 | Persistence format | JSON file `data/canvas-owners.json` — matches `data/sessions.json` pattern |
| 3 | Resume mechanism | `SessionManager.spawnSession` using a targeted resume command |
| 4 | Resume command | `CodingToolCli.build provider (Resume (Some ownerSessionId))` — same as `resumeSession` but targeted |
| 5 | Client message structure | `CanvasMessageRequest` includes `Filename` — enables per-doc owner lookup |
| 6 | Liveness granularity | Per-doc (owner session alive) replaces per-worktree |
| 7 | "Start session" button | Unchanged — always starts fresh, auto-resume is for postMessage interactions only |

## Key Files

| File | Purpose |
|------|---------|
| `src/Shared/Types.fs` | Defines `CanvasDoc.OwnerSessionId` and `CanvasMessageRequest.Filename` |
| `src/Server/CanvasDocOwnership.fs` | Stores per-doc ownership and persists it to `data/canvas-owners.json` |
| `src/Server/RefreshScheduler.fs` | Attributes changed canvas docs to the current bridge session during scans |
| `src/Server/CanvasBridge.fs` | sessionId-keyed registry; owner-based delivery routing; liveness |
| `src/Extension/extension.mjs` | Declares doc ownership — stamps its `sessionId`, forwards to `/api/canvas/attribute` |
| `src/Extension/skill/SKILL.md` | Instructs the agent to declare ownership when writing a canvas doc |
| `src/Server/WorktreeApi.fs` | Queues canvas messages, resumes owner sessions, and falls back to new sessions |
| `src/Server/Program.fs` | Calls `CanvasDocOwnership.load()` during startup |
| `src/Client/CanvasPane.fs` | Shows per-doc liveness based on the owning session |
| `src/Client/App.fs` | Sends the focused doc filename in `CanvasMessageRequest` |
| `src/Tests/fixtures/worktrees.json` | Provides fixture `CanvasDoc` entries with `OwnerSessionId` |

## Related Specs

- `docs/spec/canvas-pane.md:290-313` — Original per-doc ownership design reflected here
- `docs/spec/resume-last-session.md` — Resume session infrastructure (reused here)
- `.agents/canvas-bridge-handover.md` — Investigation that motivated this work
