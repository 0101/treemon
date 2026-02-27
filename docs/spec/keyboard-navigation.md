# Keyboard Navigation

## Goals

- Enable full keyboard-driven workflow: alt-tab to treemon, arrow-key to a card, press a key to act
- Navigation covers both repo headers (collapsible) and worktree cards
- Extensible key binding system so new shortcuts are trivial to add

## Expected Behavior

### Focus Model

A single focused element tracked in the Elmish Model by a discriminated union:

```
FocusTarget = RepoHeader of repoId | Card of scopedKey
```

- `scopedKey` = `repoId/branch` (already used in the codebase)
- Focus persists across poll updates (keyed by identity, not index)
- If the focused card disappears (worktree deleted), focus moves to the nearest visible element
- On first keypress (arrow key), focus lands on the first visible element

### Arrow Key Navigation — Spatial

Cards are displayed in a CSS Grid (1-4 columns depending on viewport width). Arrow keys navigate spatially to match the visual layout.

**On cards (grid navigation):**
- **Left/Right**: Move to the adjacent card in the same visual row
- **Up/Down**: Move to the card in the same column, one row above/below
- At grid edges, Up/Down cross repo boundaries to find the nearest card in the same column
- Left from the first card in a row → repo header above (or last card of previous repo's last row)
- Right from the last card in a row → first card of next row (or next repo header)

**On repo headers:**
- **Up/Down**: Move to previous/next navigable element (previous repo's last card, or first card of this repo)
- **Left**: Collapse the repo (no-op if already collapsed)
- **Right**: Expand the repo (no-op if already expanded)

**Column count detection**: Read the computed `grid-template-columns` CSS property from the `.card-grid` element to determine the current column count. This avoids duplicating breakpoint logic.

**Wrapping**: Navigation wraps — down from the last element goes to first, up from first goes to last.

**Scroll into view**: When focus moves to an element outside the viewport, scroll it into view (`scrollIntoView` with `block: "nearest"`).

### Key Bindings on Focused Element

When a **card** is focused:
- **Enter** → open terminal (or focus session if active)
- **s** → start sync

When a **repo header** is focused:
- **Enter** → toggle collapse/expand

### Visual Indicator

- Focused element gets a visible focus ring (CSS outline or border)
- Focus ring is distinct from hover state

### Edge Cases

- Collapsing a repo while a child card is focused: move focus to the repo header
- No elements visible: focus is `None`
- Letters only trigger when no modifier keys are held (avoid conflicts with browser shortcuts)

## Technical Approach

### Model Changes

Add to Model:
- `FocusedElement: FocusTarget option` — currently focused item

### Msg Changes

Add messages:
- `KeyPressed of key: string` — raw keypress handler
- `SetFocus of FocusTarget option` — explicit focus changes

### Key Binding Dispatch

A lookup table maps `(elementType, key)` → `Msg option`. This is a pure function:

```fsharp
let keyBindings (focused: FocusTarget) (key: string) (model: Model) : Msg option =
    match focused with
    | Card scopedKey ->
        match key with
        | "Enter" ->
            // Look up WorktreeStatus to get wt.Path and wt.HasActiveSession
            // Dispatch FocusSession wt.Path or OpenTerminal wt.Path
            findWorktree scopedKey model |> Option.map terminalAction
        | "s" ->
            // scopedKey is "repoId/branch" — extract branch for StartSync
            findWorktree scopedKey model |> Option.map (fun wt -> StartSync (wt.Branch, scopedKey))
        | _ -> None
    | RepoHeader repoId ->
        match key with
        | "Enter" -> Some (ToggleCollapse repoId)
        | _ -> None
```

Adding new key bindings = adding a new match arm. The `findWorktree` helper looks up `WorktreeStatus` by `scopedKey` from `model.Repos`.

### Spatial Navigation

Replace the current flat-list `navigateFocus` with spatial-aware navigation:

1. **Column count**: Query the DOM for `.card-grid` computed `grid-template-columns`, parse the number of values to get column count. Fall back to 1 if no element found.
2. **Grid position**: For a card at index `i` within its repo's card list, `row = i / cols`, `col = i % cols`.
3. **Arrow dispatch**:
   - `Left/Right` on card: move within the same repo's cards by ±1 index, or cross to header/next repo
   - `Up/Down` on card: move by ±cols index within same repo, or cross repo boundary matching column
   - `Left/Right` on header: collapse/expand (dispatch `ToggleCollapse`)
   - `Up/Down` on header: linear move to previous/next element
4. **Scroll into view**: After each focus change, call `scrollIntoView { block = "nearest" }` on the newly focused DOM element via `Cmd.ofSub` or inline JS interop.

### Event Handling

- Single `onKeyDown` handler on the `.dashboard` div
- The div needs `tabIndex 0` to receive keyboard events
- Auto-focus the dashboard div on mount so keys work immediately after alt-tab

### CSS

- `.focused` class on the active card/header
- Visible outline style, e.g. `outline: 2px solid #4a9eff`

## Implementation Status

Already implemented (feature tm-wt-e5q, closed):
- Focus model (FocusTarget DU, FocusedElement in Model)
- KeyPressed/SetFocus messages
- Key binding dispatch (Enter, s on cards; Enter on headers)
- Flat Up/Down navigation via `navigateFocus` (linear traversal, wrapping)
- Visual focus ring (.focused class, outline CSS)
- Edge cases: collapse adjusts focus, modifier key filtering, visibility adjustment
- E2E tests for all of the above

Remaining (feature tm-wt-a7c):
- Replace flat `navigateFocus` with spatial grid-aware navigation (Left/Right/Up/Down)
- Left/Right on repo headers for collapse/expand
- Column count detection from DOM
- scrollIntoView on focus change
- Add ArrowLeft/ArrowRight to preventDefault

## Key Files

- `src/Client/App.fs` — Model, Msg, update, view (all client changes here)
- `src/Client/index.html` — CSS for focus ring
