# Keyboard Navigation

## Goals

- Enable full keyboard-driven workflow: alt-tab to treemon, arrow-key to a card, press a key to act
- Navigation covers both repo headers (collapsible) and worktree cards
- Extensible key binding system so new shortcuts are trivial to add

## Expected Behavior

### Focus Model

A single focused element tracked in the Model as `FocusedElement: FocusTarget option` where `FocusTarget = RepoHeader of RepoId | Card of scopedKey`.

- Focus persists across poll updates (keyed by identity, not index)
- If the focused card disappears, focus moves to the nearest visible element
- On first keypress, focus lands on the first visible element

### Arrow Key Navigation — Spatial

Cards are in a CSS Grid (1-4 columns by viewport width). Arrow keys navigate spatially.

**On cards:**
- **Left/Right**: Adjacent card in the same visual row. Left from first column goes to repo header or previous repo's last card. Right from end of row goes to next row or next repo header.
- **Up/Down**: Same column, one row above/below. Crossing a repo boundary always lands on the repo header (not directly on cards in the other repo).

**On repo headers:**
- **Up/Down**: Previous/next navigable element
- **Left**: Collapse (no-op if already collapsed)
- **Right**: Expand (no-op if already expanded)

**Column count detection**: Read computed `grid-template-columns` from `.card-grid` DOM element. Navigation wraps at boundaries. Focus changes trigger `scrollIntoView` with `block: "nearest"`.

### Key Bindings

| Context | Key | Action |
|---------|-----|--------|
| Card | Enter | Open terminal / focus active session |
| Card | s | Start sync |
| Repo header | Enter | Toggle collapse/expand |

Adding new bindings = adding a match arm in `keyBinding`.

### Edge Cases

- Collapsing a repo while a child card is focused: focus moves to the repo header
- Modifier keys (Ctrl/Alt/Cmd) suppress letter bindings
- `onKeyDown` on `.dashboard` div with `tabIndex 0`, auto-focused on mount

## Key Files

- `src/Client/App.fs` — `FocusTarget` DU, `navigateSpatial`, `keyBinding`, `KeyPressed` handler, view focus rendering
- `src/Client/index.html` — `.focused` CSS class (outline: 2px solid #4a9eff)
