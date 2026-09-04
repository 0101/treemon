# Keyboard Navigation

## Goals

- Enable full keyboard-driven workflow: alt-tab to treemon, arrow-key to a card, press a key to act
- Navigation covers both repo headers (collapsible) and worktree cards
- Find and reveal any active worktree across repositories without first navigating the grid
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
| Card | s | Toggle auto-sync |
| Card | r | Resume last session (when resumable) |
| Card | + | Open new tab (when session active) |
| Card | e | Open editor |
| Card | a | Archive worktree |
| Card | Delete | Delete worktree (non-main only) |
| Repo header | Enter | Toggle collapse/expand |
| Repo header | + | Create new worktree |
| Global | Ctrl+P | Open fuzzy worktree search |
| Global | Escape | Reclaim keyboard focus to the worktree navigation (also closes an open modal) |

Focused card/header bindings live in `keyBinding`; global bindings live in the document keyboard
subscription and the canvas iframe bridge.

### Edge Cases

- Collapsing a repo while a child card is focused: focus moves to the repo header
- Modifier keys (Ctrl/Alt/Cmd) suppress focused letter bindings; Ctrl+P is the explicit global exception
- `onKeyDown` on `.dashboard` div with `tabIndex 0`, auto-focused on mount

### Worktree Search

Ctrl+P opens a command palette from anywhere in the top-level app, including editable fields. It is
suppressed while a create-worktree or confirmation modal is active so overlays never stack.

The palette searches every non-archived worktree by repository name, branch, and full path.
Characters match in order without requiring adjacency. Space-separated terms can match different
fields, while a compact term can continue from repository into branch in display order
(`tremokb` matches `treemon` / `kb-navigation`). Results show repository above a compact
branch-and-path row and highlight the matched characters.

Up/Down wraps through results; hover also changes selection. Enter or click closes the palette,
expands a collapsed owning repository, focuses the card through the normal focus chokepoint, and
scrolls it into view without launching a terminal. Escape closes without changing card focus.
Selection is stored by `WorktreePath`, not list index, so polling-driven reorder does not move the
user to a different worktree.

### Global Shortcut Reach

Navigation only works while DOM focus is on (or inside) the `.dashboard` div, since that element
owns the `onKeyDown` handler. The document-level `globalKeyboard` subscription catches Ctrl+P from
the top-level app and catches Escape when focus has left the dashboard. Escape refocuses the
dashboard through `Navigation.reclaimFocusTarget`; editable fields retain their own Escape.

Canvas docs run in a cross-origin iframe, so the server injects `CanvasDocServer.globalKeyboardScript`.
It posts `open-worktree-search` for Ctrl+P (including from editable fields) and `reclaim-focus` for
Escape outside an editable. `CanvasPane.messageListener` accepts either action only from the active
canvas iframe before routing it into the same Elmish messages as top-level shortcuts.

## Technical Approach

`Navigation.FocusTarget` stores stable repository or worktree identity rather than a rendered index.
`navigateSpatial` is a pure transition over the visible repository/card layout and the measured grid
column count. `App.keyBinding` maps context-sensitive keys to Elmish messages, while the dashboard
event handler owns only keyboard plumbing and dispatch.

The client reads `.card-grid` computed columns when navigation runs, then focuses and scrolls the
resolved element after React has rendered the new model state. A document-level Escape subscription
reclaims focus from sibling dashboard UI, and the Canvas doc server injects the cross-origin iframe
bridge needed for the same action inside a document.

## Key Files

- `src/Client/WorktreeSearch.fs` — fuzzy matching, identity-stable selection state, keyboard update, and palette view
- `src/Client/App.fs` — focused bindings, `globalKeyboard`, modal gating, and shared focus/reveal orchestration
- `src/Client/Navigation.fs` — `FocusTarget` DU, `navigateSpatial`, `reclaimFocusTarget` (focus target to restore on Escape)
- `src/Server/CanvasDocServer.fs` — global keyboard bridge injected into every canvas doc
- `src/Client/CanvasPane.fs` — validates and routes active-canvas global shortcut messages
- `src/Client/index.html` — focused-card, command-palette, and keyboard-hint styling
