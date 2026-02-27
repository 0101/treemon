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

### Arrow Key Navigation

All navigable elements form a flat visual list top-to-bottom:

```
[RepoHeader "AITestAgent"]
  [Card "AITestAgent/main"]
  [Card "AITestAgent/feature-1"]
[RepoHeader "OtherProject"]
  [Card "OtherProject/main"]
```

- **Up/Down arrows**: Move focus to the previous/next visible element in the flat list
- Collapsed repos hide their cards — arrow keys skip over them
- Navigation wraps: down from last element goes to first, up from first goes to last

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

### Navigation List

Build a flat list of visible `FocusTarget` elements from the current Model state (respecting collapsed repos and sort order). Arrow keys move an index within this list.

### Event Handling

- Single `onKeyDown` handler on the `.dashboard` div
- The div needs `tabIndex 0` to receive keyboard events
- Auto-focus the dashboard div on mount so keys work immediately after alt-tab

### CSS

- `.focused` class on the active card/header
- Visible outline style, e.g. `outline: 2px solid #4a9eff`

## Key Files

- `src/Client/App.fs` — Model, Msg, update, view (all client changes here)
- `src/Client/index.html` — CSS for focus ring
