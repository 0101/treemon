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

`Tick` handler dispatches only `fetchWorktrees()` — sync status data is already included in the dashboard response. `fetchSyncStatus()` remains in `init` (startup hydration) and `SyncStarted Ok` handler.

### Error Toast

- `Model` gains `LastError: string option`
- Failed operations (`DataFailed`, `DeleteCompleted Error`, `SessionResult Error`, `LaunchActionResult Error`) set `LastError`
- A fixed-position toast renders when `LastError` is `Some`, with a dismiss button
- Auto-dismiss via subscription (keyed by message content to reset timer on new errors)
- `DismissError` msg clears `LastError`

### Tests

Unit tests verify:
- `DataFailed` sets `LastError`
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
