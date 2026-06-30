# Canvas Doc Ownership & Auto-Resume

## Goals

- Route canvas-doc → agent messages to the doc's **owning session**, even when multiple sessions share one worktree (fixes cross-session misrouting)
- Track per-doc authoring session ID via **explicit declaration from the authoring session** (not last-registered inference)
- Persist ownership across server restarts so session affinity survives reboots
- Auto-resume a dead owner session when the user interacts with its doc (postMessage from iframe)
- Fall back to starting a new session if owner is unknown or resume fails
- Show per-doc liveness in the UI (owner session alive vs dead)

## Expected Behavior

### Ownership Attribution

When an agent creates or updates a canvas doc, the **authoring session declares ownership explicitly**: the extension — which holds that session's own `sessionId` — POSTs `{worktreePath, filename, sessionId}` to Treemon's `/api/canvas/attribute` endpoint. This is authoritative because the declaration comes from the one process that actually wrote the file.

This **replaces** the previous `FileSystemWatcher` inference, which credited whichever session was *registered last* for the worktree and therefore misattributed (and misrouted) docs whenever two sessions shared a worktree. The watcher path is kept only as a best-effort fallback for docs with **no** declared owner: if exactly one session is registered for the worktree it may be attributed; otherwise the doc is left unowned.

Ownership is stored as `Map<worktreePath, Map<filename, string>>` (worktree → filename → sessionId), persisted to `data/canvas-owners.json` on every change. Loaded on server startup.

`CanvasDoc.OwnerSessionId` is populated from this ownership map during canvas scans, so the client always receives the current owner.

### Auto-Resume on Interaction

When a user interacts with a canvas doc (postMessage) and no live bridge can deliver the message, Treemon looks up the doc's owner and resumes *that specific* session (a targeted resume for the worktree's coding tool), queues the message for delivery when the resumed bridge re-registers, and returns `Queued` (the client shows a "Waiting for session…" banner).

### Fallback: New Session

If the owner session ID is unknown (doc was created without a registered bridge) or the resume attempt fails:

- Start a new session with the doc as context — identical to the existing "▶ Start session" button behavior (`LaunchCanvasSession` in App.fs)
- Queue the message as before

The "▶ Start session" button itself is unchanged — it always starts a fresh session.

### Per-Doc Liveness

The liveness dot in the tab bar and overview is per-doc rather than per-worktree: each doc maps its `OwnerSessionId` against the bridge liveness data — 🟢 if the owner's session is alive, ⚪ if there is no owner or the owner's session is dead.

### Bridge Registry (per session)

The bridge registry is keyed by **`sessionId`**, not worktree path, so multiple sessions in one worktree coexist instead of overwriting each other's slot. Each entry carries `{ WorktreePath; InjectUrl; SessionId; RegisteredAt }`. `registerSession` upserts by `sessionId`. A helper `sessionsForWorktree: worktreePath -> SessionEntry list` backs worktree-level fallbacks and liveness.

### Message Routing (by owner)

`CanvasBridge.sendMessage` routes a doc's message to that doc's **owner**:

1. Resolve `owner = CanvasDocOwnership.getOwner worktree filename`.
2. Owner has a live registry entry → POST to that session's `InjectUrl` (on HTTP failure, fall through to queue).
3. Owner offline/gone → enqueue (`Queued`); never fall back to a non-owner.
4. No declared owner → enqueue (`Queued`). The former "exactly one live session" single-session
   fallback is **removed**: now that authoring sessions declare ownership explicitly, an unowned doc
   has no identifiable recipient, so handing the message to whatever co-located session happens to be
   live misroutes it (e.g. a focused-review reply into an unrelated session). The send/resume flow
   (`WorktreeApi.sendCanvasMessage`) then starts or continues a session to collect the queued message.

A doc's message is **never** delivered to a non-owner session in the same worktree.

### Queue & Drain (owner-aware)

A queued message carries its doc's resolved owner sessionId (captured at enqueue time; `None`
when the doc has no declared owner). Both drain paths honor it, so a message queued while its
owner is offline is never cross-routed to a co-located non-owner that re-registers or polls first:

- **Drain on register** (`drainQueue`, fired by `registerSession`): forwards only messages whose
  owner is unknown **or** equals the registering session's sessionId; the rest are re-queued
  (original `EnqueuedAt`/TTL preserved) for the rightful owner to drain when it (re-)registers.
- **Drain on heartbeat poll** (`drainPending`): the poll carries no sessionId, so it is an
  anonymous drainer and may collect only owner-unknown messages; owner-bound messages are
  re-queued and wait for the owner's push-bridge re-registration.

This closes the end-to-end hole — owner-aware `sendMessage` no longer leaks through an
ownership-blind drain. (Owner is captured at enqueue; ownership changing between enqueue and
drain is out of scope.)

### What Doesn't Change

- The "▶ Start session" button always starts a fresh session, not a resume.
- The message queue (cap 10, 5-min TTL) keeps the same cap and TTL, but each queued message now carries its doc's resolved owner so both drain paths are owner-aware (see "Queue & Drain (owner-aware)") — they drain to the owner-resumed session, never to a co-located non-owner.
- Auto-resume on interaction is unchanged in spirit — it now resumes the *correct* owner because attribution is explicit.

## Technical Approach

- **Authorship declaration** — the agent pings the local bridge with just the filename; the extension stamps its own `sessionId` and POSTs `{worktreePath, filename, sessionId}` to `/api/canvas/attribute`. The handler validates body + worktree like `canvasRegisterHandler`: malformed/blank → `400`; well-formed but **unmonitored** worktree → `200` with nothing recorded (benign no-op — the extension still serves the doc in-browser); **monitored** → records ownership.
- **Ownership store** — `CanvasDocOwnership.fs` is a `MailboxProcessor` serializing an immutable `Map<worktreePath, Map<filename, sessionId>>`, persisted to `data/canvas-owners.json` on every change and loaded at startup; reads are async.
- **Scanner attribution is fallback-only** — `RefreshScheduler` populates `CanvasDoc.OwnerSessionId` from the ownership map on each scan and auto-attributes a no-owner changed doc only when exactly one session is registered for the worktree. Explicit declarations are primary and are never overwritten.
- **Send / resume flow** — `WorktreeApi.sendCanvasMessage` calls `CanvasBridge.sendMessage`; on `Queued` it resumes the doc's owner via `SessionManager.spawnSession` (or starts a fresh session when the owner is unknown or resume fails) and leaves the message queued for delivery when the bridge re-registers.

## Decisions

| # | Decision | Choice |
|---|----------|--------|
| 1 | Ownership attribution trigger | **Explicit declaration** from the authoring session's extension (`POST /api/canvas/attribute`); file-watcher inference kept only as a single-session fallback |
| 1b | Bridge registry keying | Keyed by **`sessionId`** (was worktree path) — multiple sessions per worktree coexist |
| 1b-i | `None`/blank sessionId | A blank/whitespace sessionId is normalized to `None`, so it can't become a sticky, unroutable owner; `None`-sessionId registrations share one per-worktree fallback slot and never clobber an identified session. |
| 1c | Delivery routing | Route by doc **owner** sessionId; with no declared owner, queue (the single-session fallback is removed — never deliver to a co-located non-author); never cross-route to a non-owner |
| 1c-i | Queue/drain ownership | Each `QueuedMessage` carries its resolved owner; `drainQueue` (on register) and `drainPending` (anonymous poll) deliver only when the owner is unknown or matches the drainer, re-queuing the rest (TTL preserved) |
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
| `src/Server/RefreshScheduler.fs` | Fallback-only scanner attribution: credits a no-owner changed doc to the worktree's bridge session **only when exactly one is registered** (`CanvasWatchers.fallbackOwner`/`attributeChangedDocs`); never overwrites a declared owner |
| `src/Server/CanvasBridge.fs` | sessionId-keyed registry; owner-based delivery routing; liveness |
| `src/Extension/extension.mjs` | Declares doc ownership — stamps its `sessionId`, forwards to `/api/canvas/attribute` |
| `src/Extension/skill/SKILL.md` | Instructs the agent to declare ownership when writing a canvas doc |
| `src/Server/WorktreeApi.fs` | Queues canvas messages, resumes owner sessions, and falls back to new sessions |
| `src/Server/Program.fs` | Calls `CanvasDocOwnership.load()` during startup |
| `src/Client/CanvasPane.fs` | Shows per-doc liveness based on the owning session |
| `src/Client/App.fs` | Sends the focused doc filename in `CanvasMessageRequest` |
| `src/Tests/fixtures/worktrees.json` | Provides fixture `CanvasDoc` entries with `OwnerSessionId` |

## Related Specs

- `docs/spec/canvas-pane.md` — Generic canvas pane architecture; defers to this spec for doc ownership, owner-based routing, and per-doc liveness
- `docs/spec/resume-last-session.md` — Resume session infrastructure (reused here)
