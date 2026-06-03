# Beadspace Canvas Dashboard

## Goals

Add a beads issue dashboard to the canvas pane by integrating Beadspace (`cameronsjo/beadspace`) — a single-file vanilla HTML/CSS/JS dashboard. The dashboard shows per-worktree beads stats, a filterable issues table, triage suggestions, and a click-to-expand detail panel for viewing descriptions and labels.

## Expected Behavior

- Any worktree with `.beads/beads.db` gets a `beads.html` canvas page auto-provisioned
- The page renders a **dashboard view** (status donut, priority/label/type charts, completion %, active issues, triage suggestions) and a **sortable/filterable issues table**
- Clicking an issue row expands a **detail panel** showing: full description, all labels, priority badge, type badge, dependency count, dependent count, age
- Data refreshes every 30 seconds by polling a same-origin JSON endpoint on port 5002
- Theme matches Catppuccin Mocha (canvas pane dark theme)
- No external dependencies (CDN, fonts, frameworks) — fully self-contained HTML
- A postMessage `{ action: 'refresh-beads' }` triggers immediate data reload

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
- If `.beads/beads.db` exists AND `.agents/canvas/beads.html` doesn't exist → write the template
- Template is stored as a string constant in `BeadspaceTemplate.fs` module in the Server project
- Creates `.agents/canvas/` directory if needed
- Leaves existing `beads.html` alone (user may have customized)

### Key Files

| File | Change |
|------|--------|
| `src/Server/BeadsStatus.fs` | Add `getBeadsIssueList` function |
| `src/Server/Program.fs` | Add `beads-data` route to CanvasDocServer |
| `src/Server/BeadspaceTemplate.html` | Customized Beadspace template |
| `src/Server/BeadspaceTemplate.fs` | String constant exposing the template HTML |
| `src/Server/RefreshScheduler.fs` | Auto-provision logic on worktree scan |
