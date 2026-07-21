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
| Card | r | Resume last session (when resumable) |
| Card | + | Open new tab (when session active) |
| Card | e | Open editor |
| Card | a | Archive worktree |
| Card | Delete | Delete worktree (non-main only) |
| Repo header | Enter | Toggle collapse/expand |
| Repo header | + | Create new worktree |
| Global | Escape | Reclaim keyboard focus to the worktree navigation (also closes an open modal) |

Adding new bindings = adding a match arm in `keyBinding`.

### Edge Cases

- Collapsing a repo while a child card is focused: focus moves to the repo header
- Modifier keys (Ctrl/Alt/Cmd) suppress letter bindings
- `onKeyDown` on `.dashboard` div with `tabIndex 0`, auto-focused on mount

### Reclaiming Focus

Navigation only works while DOM focus is on (or inside) the `.dashboard` div, since that element
owns the `onKeyDown` handler. When focus escapes to a sibling (canvas pane, header, mascot) or
`<body>`, arrow keys go dead. A global document-level `keydown` subscription catches **Escape** from
anywhere outside the dashboard and refocuses it, restoring the focus target
(`Navigation.reclaimFocusTarget`). It is skipped when focus is already inside the dashboard (its own
handler applies) or in an editable field. Escape while the caret is inside the cross-origin canvas
doc iframe (`127.0.0.1:5002`) can't reach this listener directly (the keystroke does not cross the
origin boundary), so the doc server injects a keydown bridge (`CanvasDocServer.reclaimFocusScript`)
that posts `{action:'reclaim-focus'}` to the pane on Escape; `CanvasPane.messageListener` routes it to
the same reclaim (honored only from the active doc). The bridge is likewise skipped when the caret is
in an editable field inside the doc.

## Key Files

- `src/Client/App.fs` — `keyBinding`, `KeyPressed` handler (incl. Escape reclaim), `focusReclaim` global subscription, view focus rendering
- `src/Client/Navigation.fs` — `FocusTarget` DU, `navigateSpatial`, `reclaimFocusTarget` (focus target to restore on Escape)
- `src/Server/CanvasDocServer.fs` — `reclaimFocusScript` (Escape→`reclaim-focus` bridge injected into every canvas doc)
- `src/Client/CanvasPane.fs` — routes the `reclaim-focus` doc message to the Escape reclaim
- `src/Client/index.html` — `.focused` CSS class (outline: 2px solid #4a9eff), `.nav-hint` footer hint

