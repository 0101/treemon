# Error Handling & Typed Browser APIs

## Goals

- Replace dynamic (`?`) browser API interop in `Navigation.fs` with typed equivalents, catching type errors at compile time instead of runtime
- Remove redundant `fetchSyncStatus` call from `Tick` handler to reduce unnecessary API traffic
- Surface errors to the user via a dismissable toast instead of silently swallowing them
- Add unit tests for error handling behavior

## Expected Behavior

### Typed Browser APIs (Navigation.fs)

`getColumnCount()` and `scrollFocusedIntoView()` use typed Fable.Browser APIs instead of dynamic member access (`?`). `Dom.window?scrollTo(createObj [...])` remains dynamic because the typed overload only accepts `(x, y)`, not the options object form.

Requires adding `Fable.Browser.Css` package reference to `Client.fsproj`.

### Remove Redundant fetchSyncStatus from Tick

`Tick` handler dispatches only `fetchWorktrees()` — `fetchSyncStatus()` is removed since sync events are polled via `SyncTick` (2s interval, active only when sync is running). Trade-off: sync status no longer refreshes on every tick, but the dedicated `SyncTick` subscription provides timely updates when a sync is actually in progress.

### Error Toast

- `Model` has `LastError: string option` (no `HasError` field -- eye state derives from `LastError.IsSome`)
- `DataFailed` sets `LastError` (data fetch failures)
- `ActionFailed` sets `LastError` only (action failures -- does not touch `IsLoading`)
- Action commands (OpenTerminal, OpenEditor, StartSync, CancelSync, DeleteWorktree, FocusSession, OpenNewTab, LaunchAction) route to `ActionFailed`
- Failed result messages (`DeleteCompleted Error`, `SessionResult Error`, `LaunchActionResult Error`) set `LastError` with prefix
- A fixed-position toast renders when `LastError` is `Some`, with a dismiss button
- Auto-dismiss via subscription (keyed by message content to reset timer on new errors)
- `DismissError` msg clears `LastError`

### Tests

Unit tests verify:
- `DataFailed` sets `LastError`
- `ActionFailed` sets `LastError` without changing `IsLoading`
- `DeleteCompleted Error` sets `LastError` with "Delete failed:" prefix
- `SessionResult Error` sets `LastError` with prefix
- `DismissError` clears `LastError`
- Unrelated messages (e.g. `ToggleCompact`) preserve `LastError`

## Technical Approach

1. **Typed APIs**: Add `Fable.Browser.Css` NuGet package. Replace `?` member access with typed equivalents in `Navigation.fs`. Keep `scrollTo` dynamic.
2. **Remove redundant fetch**: Delete `fetchSyncStatus()` from `Tick` handler's `Cmd.batch` in `App.fs`.
3. **Error handling**: Add `LastError` field to `Model`, `DismissError` to `Msg`, `.either` pattern for Cmd calls, error toast view, CSS styles in `index.html`. Wire auto-dismiss subscription.
4. **Tests**: Extract `defaultModel`/`tryUpdateModel` from `CreateWorktreeTests.fs` into `TestUtils.fs`. Create `ErrorToastTests.fs` with error handling unit tests.

## Key Files

- `src/Client/Navigation.fs` — typed browser API migration
- `src/Client/App.fs` — error handling, redundant fetch removal
- `src/Client/index.html` — toast CSS
- `src/Client/Client.fsproj` — package reference
- `src/Tests/ErrorToastTests.fs` — new test file
- `src/Tests/TestUtils.fs` — shared test helpers
- `src/Tests/Tests.fsproj` — test project reference
