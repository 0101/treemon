# Sync Button Instant Feedback

## Goals

- Sync button reacts visually on the same render frame as the click — no perceived delay
- Button shows a distinct "starting" state while the server call is in-flight (cancel is not yet available)
- Button transitions to "Cancel" only after the server confirms the sync has started
- If the server returns an error, the button reverts to "Sync" cleanly
- Sync polling subscription activates immediately on click, not after server round-trip

## Expected Behavior

**Before (current):**
Click "Sync" → 100-400ms of no change → button switches to "Cancel"

**After:**
Click "Sync" → instantly shows "Sync starting" (disabled, no cancel) → server confirms → switches to "Cancel" (functional)

### State transitions

1. **Idle** — button shows "Sync", enabled
2. **Starting** (new) — button shows "Sync starting" (or spinner), disabled. Entered immediately on click via optimistic model update. Cancel is not offered because the server hasn't registered the sync yet.
3. **Running** — button shows "Cancel", enabled. Entered when `SyncStarted (Ok _)` arrives and `SyncStatusUpdate` populates real events.
4. **Error** — reverts to Idle. Entered when `SyncStarted (Error _)` arrives; synthetic event removed from `BranchEvents`.

### Key files

- `src/Client/App.fs` — Elmish Model, Msg, update, syncButton view

## Technical Approach

### Model change

Add `SyncPending: Set<string>` to the `Model` record. This tracks branches where the `startSync` call is in-flight but not yet confirmed. The scoped key format (`{repoId}/{branch}`) matches `renderCard`.

### Update function changes

**`StartSync branch`:** Compute the scoped key from `model.Repos`. Add the key to `SyncPending`. Inject a synthetic `CardEvent` with `Status = Some Running` and `Message = "Sync starting"` into `BranchEvents` so that `hasSyncRunning` returns true (activating sync polling). Issue the async `startSync` call.

**`SyncStarted (Ok _)`:** Remove the branch from `SyncPending`. Issue `fetchSyncStatus()` as before — real events will overwrite the synthetic one.

**`SyncStarted (Error _)`:** Remove the branch from `SyncPending`. Remove the synthetic event from `BranchEvents` for that key.

### View change

In `syncButton`: check `SyncPending` (passed through or checked via a helper). When the scoped key is in `SyncPending`, render a disabled button with text "Sync starting" instead of the "Cancel" button. Once `SyncPending` is cleared (server confirmed), `isBranchSyncing` takes over and renders the normal "Cancel" button.

### CSS

Add a `.sync-starting-btn` class (or reuse `.sync-btn.disabled` with different text). Minimal styling — just needs to look distinct from both "Sync" and "Cancel".

## Decisions

- **No `PendingCancel` mechanism** — the "starting" state doesn't offer cancel, so the race condition from the investigation is sidestepped entirely. Users can only cancel once the server has confirmed.
- **`SyncPending` is a `Set<string>`** not a `Map` — we only need to know if a branch is pending, no extra data.
- **Synthetic event injected into `BranchEvents`** — this activates the sync polling subscription immediately, which is important for fast transition to the real "Running" state.
