# Canvas Pane — Toolbar Consolidation & Doc Archive

Quick UX fixes for the canvas pane: consolidate the toolbar, add doc archiving.

## Goals

- **Unified toolbar**: Position selector buttons (◧ ◨ ⬒ ⬓) share the same bar as doc tabs, right-aligned, at 0.4 opacity (full on hover) — they're rarely used day-to-day
- **Doc archive**: A trash button in the tab bar archives the selected doc to `.agents/canvas/archive/` for recovery; only clickable when a doc is selected (double-click to archive an unselected doc)

## Expected Behavior

### Unified Toolbar

- The `.canvas-toolbar` (position buttons) merges into `.canvas-tab-bar` — one bar with tabs on the left, position buttons on the right
- Position buttons render at `opacity: 0.4`, transitioning to `1.0` on hover
- When only one doc exists (no tab bar currently shown), the bar still renders with just the position buttons at low opacity
- Layout is flex with `justify-content: space-between` — tabs grow left, position buttons fixed right

### Doc Archive

- A 🗑️ button appears in the tab bar, next to the active tab (or next to position buttons if single-doc)
- The button is **only clickable when its doc is the currently selected tab** — clicking an unselected doc's tab first selects it, then a second click on trash archives it
- Server endpoint moves the file from `.agents/canvas/<filename>` to `.agents/canvas/archive/<filename>` (creating the `archive/` dir if needed)
- The scanner already ignores subdirectories (only scans `*.html` in the root of `.agents/canvas/`), so archived docs disappear from the doc list automatically
- After archiving, if other docs remain, switch to the first one; if none remain, show the overview

## Technical Approach

### Unified Toolbar (client + CSS)

- `CanvasPane.fs`: Remove the separate `toolbar` rendering. Merge position buttons into the `tabBar` function (or a new `headerBar` that always renders)
- `index.html`: Remove `.canvas-toolbar` styles. Add position buttons as a right-aligned group inside `.canvas-tab-bar`. Add `.canvas-pos-btn { opacity: 0.4; transition: opacity 0.15s; }` and `.canvas-pos-btn:hover { opacity: 1; }`
- The bar always renders (even for 0 or 1 docs) — it's the chrome for position + archive controls

### Doc Archive (server + client)

- `Types.fs`: Add `archiveCanvasDoc: ArchiveCanvasDocRequest -> Async<Result<unit, string>>` to `IWorktreeApi`. Request type: `{ WorktreePath: string; Filename: string }`
- `WorktreeApi.fs`: Implement by moving the file to `<worktreePath>/.agents/canvas/archive/<filename>`, creating the dir if needed. Validate path stays within `.agents/canvas/`.
- `App.fs`: Add `ArchiveCanvasDoc of scopedKey:string * filename:string` Msg. On success, remove from local state and select next doc.
- `CanvasPane.fs`: Render trash button in the header bar. Disabled/hidden unless the doc is the active tab. Use existing archive-btn styling pattern.

### Overview Doc Names

- In the empty canvas overview, replace "X docs" with the actual doc filenames shown inline: `branch-name  [doc1] [doc2] [doc3]`
- Each doc name is plain text (not a button), subtly underlined or color-shifted on hover to hint clickability
- Clicking a doc name focuses the worktree AND selects that specific doc (SetFocus + SelectCanvasDoc)

### Canvas Authoring Skill

- A Copilot CLI skill (`src/Extension/skill/SKILL.md`) that teaches agents how to create and update canvas docs
- Covers: file placement, dark theme styling, postMessage interactivity, multi-doc, updates via file overwrite
- Includes a minimal template example

## Key Files

- `src/Client/CanvasPane.fs` — toolbar + tab bar rendering (lines 16-29, 94-112)
- `src/Client/App.fs` — canvas Msgs, update handlers (lines 39-77, 501-535)
- `src/Client/index.html` — canvas CSS (lines 438-505)
- `src/Shared/Types.fs` — IWorktreeApi (lines 197-220)
- `src/Server/WorktreeApi.fs` — API implementation
