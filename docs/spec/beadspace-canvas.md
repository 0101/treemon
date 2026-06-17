# Beadspace Canvas Dashboard

## Goals

Add a beads issue dashboard to the canvas pane by integrating Beadspace (`cameronsjo/beadspace`) — a single-file vanilla HTML/CSS/JS dashboard. The dashboard shows a per-worktree sortable/filterable issues table with a click-to-expand detail panel for viewing descriptions, labels, and dependency counts.

## Expected Behavior

- Any worktree with `.beads/beads.db` **and at least one beads issue** gets a `beads.html` canvas page auto-provisioned. Worktrees with an empty database (zero issues) do not get a dashboard.
- If a previously-provisioned `beads.html` exists but the database now has zero issues, the dashboard is removed (file deleted) so it doesn't show an empty page.
- The page renders a single **sortable/filterable issues table** (the "All Issues" view), always visible. There is no separate dashboard view (no status donut, priority/label/type charts, completion %, active-issues or triage panels) and no top navigation bar — these were removed in `tm-canvas48-dsh`.
- Each status **filter chip shows a count badge** of issues in that status (hidden when the count is zero); the `All` chip shows no count.
- The **default filter on load** is the most actionable non-empty status, chosen in priority order **WIP → open → blocked → closed** (falling back to open when empty). After load, the user's filter choice is preserved across polls.
- Clicking an issue row expands a **detail panel** showing: full description, all labels, priority badge, type badge, dependency count, dependent count, age
- Data refreshes every 30 seconds by polling a same-origin JSON endpoint on port 5002
- Data refresh is **incremental** — only the issues table body re-renders. Scroll position, expanded detail panels, filter/sort selections, and search input are preserved across polls.
- Theme matches Catppuccin Mocha (canvas pane dark theme)
- No external dependencies (CDN, fonts, frameworks) — fully self-contained HTML
- A postMessage `{ action: 'refresh-beads' }` triggers immediate data reload (also incremental)

## Canvas Doc Kind: SystemView

`beads.html` is classified as a **`SystemView`**, not an `AgentDoc` (`CanvasDocKind.classify "beads.html" → SystemView`, in `src/Shared/Types.fs`). It is server-generated and data-driven with no owner session, so it deliberately opts out of the agent-doc machinery the canvas pane applies to authored docs:

- **No liveness dot and no `▶ Start session` button** — there is no author session to be alive or to launch.
- **No message bridge** — `CanvasDocServer.buildInjection` omits the bridge heartbeat script for a `SystemView` (it injects only the scrollbar CSS and link interceptor). The dashboard has no session to route postMessage payloads to.
- **No DOM morph** — the idiomorph runtime and morph controller are also omitted. A morph would stomp the live, JS-rendered table back down to the empty template shell; the dashboard instead refreshes itself via its 30s `/beads-data` poll and the `refresh-beads` postMessage.
- **Excluded from content-hash awareness** — `CanvasAwareness.awarenessDocs` filters `SystemView` docs out, so the beads file never contributes to unviewed badges, card notifications, seeded viewed-hashes, or idle auto-display. The file hash is stable while the data changes; beads "newness" is surfaced on the worktree card as `BeadsSummary` instead.
- **Distinct tab affordance, no archive** — it renders as a far-left `.canvas-system-tab` entry (a "BD" glyph + total-issue-count badge) rather than a normal doc tab, and the archive button is hidden (the file is regenerated from the template, not user-owned).

See `docs/spec/canvas-pane.md` for the generic pane behavior and the full `AgentDoc` vs `SystemView` rationale the two kinds share.

## Technical Approach

### Beadspace Template Customization

Fork `cameronsjo/beadspace:index.html` (40KB, MIT, vanilla JS) and apply:
- **Strip the dashboard view and top navigation bar** (`tm-canvas48-dsh`) — the upstream template's second "Dashboard" view (stat cards, status donut, priority/label/type charts, completion %, active-issues and triage panels) and the nav bar that toggled between views are removed. Only the "All Issues" table view remains, always visible via `.view { display: block }` (no nav/`.active` toggle).
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
- Template is the `BeadspaceTemplate.html` file, embedded into the Server assembly as a resource and read once at startup by the `BeadspaceTemplate.fs` module (exposed as the `BeadspaceTemplate.html` value) — one definition shared by the runtime and the E2E tests
- Creates `.agents/canvas/` directory if needed
- `beads.html` is a generated view kept in sync with the template — it is not meant to be user-customized

### Incremental Data Refresh

The template distinguishes the initial render from subsequent polls:
- **Initial render** (`initialRender`): full DOM build — the issues view (search bar, filter chips, sortable table head, empty `<tbody>`) plus event binding. The default filter chip is set to the most actionable non-empty status via `chooseDefaultFilter`, and chip count badges are populated via `updateFilterCounts`.
- **Subsequent polls** (`refreshData`): skip entirely when the `/beads-data` payload is byte-identical to the previous fetch; otherwise update only what changed:
  - Issues table replaces only `<tbody>` (via `renderIssuesTable`), not the entire table or container — the search bar, filter chips, and table head DOM are left intact
  - Filter chip count badges are recomputed in place via `updateFilterCounts` (the chip DOM is reused, only badge text changes)
  - Scroll position of the `.view` container saved/restored around the `<tbody>` replacement
  - Filter chip state, sort selection, and search input preserved (driven by `tableState`)
  - Expanded detail panel re-expanded if `tableState.expandedId` is still present in the new data

### Key Files

| File | Purpose |
|------|---------|
| `src/Server/BeadsStatus.fs` | `getBeadsIssueList` (full issue JSON) and `getBeadsSummary` (status counts) |
| `src/Server/Program.fs` | `beads-data` route on CanvasDocServer (port 5002) |
| `src/Server/BeadspaceTemplate.html` | Beadspace template — single source of truth (embedded resource; also read from disk by the E2E tests) |
| `src/Server/BeadspaceTemplate.fs` | Reads the embedded `BeadspaceTemplate.html` resource at startup, exposing it as the `html` value |
| `src/Server/RefreshScheduler.fs` | Auto-provision logic on worktree scan |

## Related Specs

- `docs/spec/canvas-pane.md` — generic canvas pane architecture and the `AgentDoc` vs `SystemView` behavior matrix
