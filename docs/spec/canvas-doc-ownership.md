# Canvas Doc Ownership & Auto-Resume

## Goals

- Track per-doc authoring session ID — the bridge session that last modified each canvas doc
- Persist ownership across server restarts so session affinity survives reboots
- Auto-resume a dead owner session when the user interacts with its doc (postMessage from iframe)
- Fall back to starting a new session if owner is unknown or resume fails
- Show per-doc liveness in the UI (owner session alive vs dead)

## Expected Behavior

### Ownership Attribution

When a canvas doc file changes (via `FileSystemWatcher`) and a bridge session is registered for that worktree, the doc's owner is set to that session's `SessionId`. If no session is registered at the time of the file change, the existing owner (if any) is preserved.

Ownership is stored as `Map<worktreePath, Map<filename, string>>` (worktree → filename → sessionId), persisted to `data/canvas-owners.json` on every change. Loaded on server startup.

`CanvasDoc.OwnerSessionId` is populated from this ownership map during canvas scans, so the client always receives the current owner.

### Auto-Resume on Interaction

When a user interacts with a canvas doc (sends a postMessage) and no active bridge can deliver the message:

1. Look up the owner session ID for the doc being interacted with
2. Resolve the coding tool provider for the worktree
3. Build a resume command: `CodingToolCli.build provider (Resume (Some ownerSessionId))`
4. Launch via `SessionManager.launchAction` — opens a new tab in the tracked terminal window (or spawns a new window if none exists)
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

### What Doesn't Change

- Bridge registry remains per-worktree (keyed by normalized path) — the multi-session routing problem from the investigation is a separate concern
- The "▶ Start session" button always starts a fresh session, not a resume
- `CanvasMessageRequest` routing still goes through the worktree's registered bridge when one is alive

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

Add `Filename` to `CanvasMessageRequest` so the server knows which doc the message originates from, enabling owner-specific resume. Update the single caller in `App.fs`.

### Ownership Module (Server)

New module `CanvasDocOwnership.fs`:

- `attribute: worktreePath -> filename -> sessionId -> unit` — updates ownership, persists
- `getOwner: worktreePath -> filename -> string option` — looks up owner
- `getAll: worktreePath -> Map<string, string>` — all owners for a worktree
- `load: unit -> unit` — loads from `data/canvas-owners.json` on startup
- Internal state: `ConcurrentDictionary` matching the pattern in `CanvasBridge.fs`

### Scanner Integration

In `RefreshScheduler.fs`, the `CanvasScanner` file watcher callback:
1. After re-scanning docs, diff against previous docs to find changed/new files
2. For each changed doc, call `CanvasDocOwnership.attribute` with the current bridge session
3. Populate `CanvasDoc.OwnerSessionId` from the ownership map during scan

### Message Send Enhancement

In `WorktreeApi.fs`, replace the direct passthrough `sendCanvasMessage = CanvasBridge.sendMessage` with a wrapper:

1. Call `CanvasBridge.sendMessage` as before
2. If result is `Queued`:
   a. Look up doc owner via `CanvasDocOwnership.getOwner`
   b. If owner found: build resume command, call `SessionManager.launchAction`
   c. If no owner or resume fails: build a new session command (same as `LaunchCanvasSession` handler)
   d. Either way, message stays queued for delivery on bridge re-registration
3. Return the original `Queued` result to client

### Client Changes

In `CanvasPane.fs`, change `isWorktreeAlive` to a per-doc check:
- Use `CanvasDoc.OwnerSessionId` and `BridgeLiveness` map (which already has `SessionId`)
- Match doc owner → any bridge entry whose `SessionId` matches → use its `IsAlive`

In `App.fs`, pass the focused doc's filename in `CanvasMessageRequest`.

### Fixture Data

Update `src/Tests/fixtures/worktrees.json` to include `OwnerSessionId` in `CanvasDoc` entries (can be `null`).

## Decisions

| # | Decision | Choice |
|---|----------|--------|
| 1 | Ownership attribution trigger | File watcher change event — attribute to currently registered bridge session |
| 2 | Persistence format | JSON file `data/canvas-owners.json` — matches `data/sessions.json` pattern |
| 3 | Resume mechanism | `SessionManager.launchAction` (new tab in tracked window, or new window) |
| 4 | Resume command | `CodingToolCli.build provider (Resume (Some ownerSessionId))` — same as `resumeSession` but targeted |
| 5 | Client message change | Add `Filename` to `CanvasMessageRequest` — enables per-doc owner lookup |
| 6 | Liveness granularity | Per-doc (owner session alive) replaces per-worktree |
| 7 | "Start session" button | Unchanged — always starts fresh, auto-resume is for postMessage interactions only |

## Key Files

| File | Changes |
|------|---------|
| `src/Shared/Types.fs` | Add `OwnerSessionId` to `CanvasDoc`, `Filename` to `CanvasMessageRequest` |
| `src/Server/CanvasDocOwnership.fs` | New module: ownership tracking + persistence |
| `src/Server/RefreshScheduler.fs` | Wire ownership attribution into canvas scanner |
| `src/Server/CanvasBridge.fs` | Expose session lookup for ownership attribution |
| `src/Server/WorktreeApi.fs` | Enhance `sendCanvasMessage` with resume logic |
| `src/Client/CanvasPane.fs` | Per-doc liveness check |
| `src/Client/App.fs` | Pass `Filename` in `CanvasMessageRequest` |
| `src/Tests/fixtures/worktrees.json` | Add `OwnerSessionId` to fixture data |

## Related Specs

- `docs/spec/canvas-pane.md:290-313` — Original per-doc ownership design (this spec implements it)
- `docs/spec/resume-last-session.md` — Resume session infrastructure (reused here)
- `.agents/canvas-bridge-handover.md` — Investigation that motivated this work
