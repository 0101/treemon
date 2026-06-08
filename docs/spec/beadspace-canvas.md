# Beadspace Canvas Dashboard

## Goals

Add a beads issue dashboard to the canvas pane by integrating Beadspace (`cameronsjo/beadspace`) — a single-file vanilla HTML/CSS/JS dashboard. The dashboard shows per-worktree beads stats, a filterable issues table, triage suggestions, and a click-to-expand detail panel for viewing descriptions and labels.

## Expected Behavior

- Any worktree with `.beads/beads.db` **and at least one beads issue** gets a `beads.html` canvas page auto-provisioned. Worktrees with an empty database (zero issues) do not get a dashboard.
- If a previously-provisioned `beads.html` exists but the database now has zero issues, the dashboard is removed (file deleted) so it doesn't show an empty page.
- The page renders a **dashboard view** (status donut, priority/label/type charts, completion %, active issues, triage suggestions) and a **sortable/filterable issues table**
- Clicking an issue row expands a **detail panel** showing: full description, all labels, priority badge, type badge, dependency count, dependent count, age
- Data refreshes every 30 seconds by polling a same-origin JSON endpoint on port 5002
- Data refresh is **incremental** — only changed elements update in-place. Scroll position, expanded detail panels, active nav tab, filter/sort selections, and search input are preserved across polls.
- Theme matches Catppuccin Mocha (canvas pane dark theme)
- No external dependencies (CDN, fonts, frameworks) — fully self-contained HTML
- A postMessage `{ action: 'refresh-beads' }` triggers immediate data reload (also incremental)

## Technical Approach

### Beadspace Template Customization

Fork `cameronsjo/beadspace:index.html` (40KB, MIT, vanilla JS) and apply:
- **Remove Google Fonts** — 3 `<link>` tags; system font fallbacks already defined
- **Delete light theme** media query — canvas pane is always dark
- **Remap CSS variables** to Catppuccin Mocha:
  - `--bg-deep: #1e1e2e`, `--bg-surface: #181825`, `--bg-elevated: #313244`
  - `--text-primary: #cdd6f4`, `--text-secondary: #bac2de`, `--accent: #cba6f7`
  - Keep semantic status/priority/type colors as-is (they work on dark backgrounds)
- **Replace `fetch('issues.json')`** with polling from same-origin endpoint: `fetch(dataUrl)` every 30s, where `dataUrl` is derived from `window.location.pathname`
- **Add postMessage listener** for `refresh-beads` action
- **Add issue detail panel** — click a table row to expand a panel showing full description (rendered as text), labels as colored pills, priority/type badges, dependency/dependent counts

### Same-Origin Data Endpoint

Add to `CanvasDocServer` (port 5002):
- Route: `GET /{encodedWorktreePath}/beads-data`
- Runs `bd list --json --db {path}/.beads/beads.db`
- Returns `application/json` with `Cache-Control: no-cache`
- Validates worktree is known (same `isKnownWorktree` check as HTML serving; no filesystem path validation needed since this is a virtual endpoint)

**Data format** — `bd list --json` returns an array of objects with fields: `id`, `title`, `description`, `status` (open/in_progress/blocked/closed), `priority` (int), `issue_type` (task/feature/bug/etc.), `labels` (string array), `created_at`, `updated_at`, `dependency_count`, `dependent_count`. The template JS must be adapted to consume this format (not GitHub Issues format).

### Auto-Provisioning

In the refresh scheduler, when scanning worktrees:
- If `.beads/beads.db` exists AND has at least one issue → ensure `.agents/canvas/beads.html` matches the current template: write it when absent, and rewrite it when the on-disk content differs from `BeadspaceTemplate.html` (so template fixes reach existing worktrees on the next refresh after a deploy)
- If `.beads/beads.db` has zero issues AND `.agents/canvas/beads.html` exists → delete `beads.html`
- Template is stored as a string constant in `BeadspaceTemplate.fs` module in the Server project
- Creates `.agents/canvas/` directory if needed
- `beads.html` is a generated view kept in sync with the template — it is not meant to be user-customized

### Incremental Data Refresh

The template's `loadAndRender()` must distinguish initial render from subsequent polls:
- **Initial render**: full DOM build (dashboard + issues view + event binding)
- **Subsequent polls**: update only what changed:
  - Dashboard stat numbers update via `textContent` on existing elements
  - Issues table replaces only `<tbody>`, not the entire table or container
  - Scroll position saved/restored around table body replacement
  - Active nav tab, filter chip state, and search input preserved from `tableState`
  - Expanded detail panel re-expanded if `tableState.expandedId` is still present in new data

### Key Files

| File | Purpose |
|------|---------|
| `src/Server/BeadsStatus.fs` | `getBeadsIssueList` (full issue JSON) and `getBeadsSummary` (status counts) |
| `src/Server/Program.fs` | `beads-data` route on CanvasDocServer (port 5002) |
| `src/Server/BeadspaceTemplate.html` | Customized Beadspace template source |
| `src/Server/BeadspaceTemplate.fs` | String constant exposing the template HTML |
| `src/Server/RefreshScheduler.fs` | Auto-provision logic on worktree scan |
