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

Fork `cameronsjo/beadspace:index.html` (MIT, vanilla JS) and apply:
- **Strip the dashboard view and top nav bar** (`tm-canvas48-dsh`) — only the "All Issues" table view remains, always visible.
- **Dark-only theme** — remove Google Fonts and the light-theme media query; remap the CSS variables to Catppuccin Mocha (the canvas pane is always dark), keeping the semantic status/priority/type colors.
- **Poll a same-origin endpoint** instead of `issues.json` — fetch the `beads-data` URL (derived from `window.location.pathname`) every 30s, and reload on a `refresh-beads` postMessage.
- **Add an issue detail panel** — clicking a row expands full description, labels, priority/type badges, and dependency counts.

### Same-Origin Data Endpoint

`GET /{encodedWorktreePath}/beads-data` on the canvas doc server (port 5002) runs `bd list --json` against the worktree's `.beads/beads.db` and returns the issue array as `application/json` (`Cache-Control: no-cache`). It validates the worktree is known (the same `isKnownWorktree` check as HTML serving; no path validation is needed for this virtual endpoint). The template consumes the `bd` issue shape, not the GitHub Issues format.

### Auto-Provisioning

In the refresh scheduler, when scanning worktrees:
- If `.beads/beads.db` exists AND has at least one issue → ensure `.agents/canvas/beads.html` matches the current template: write it when absent, and rewrite it when the on-disk content differs from `BeadspaceTemplate.html` (so template fixes reach existing worktrees on the next refresh after a deploy)
- If `.beads/beads.db` has zero issues AND `.agents/canvas/beads.html` exists → delete `beads.html`
- Template is the `BeadspaceTemplate.html` file, embedded into the Server assembly as a resource and read once at startup by the `BeadspaceTemplate.fs` module (exposed as the `BeadspaceTemplate.html` value) — one definition shared by the runtime and the E2E tests
- Creates `.agents/canvas/` directory if needed
- `beads.html` is a generated view kept in sync with the template — it is not meant to be user-customized

### Incremental Data Refresh

The template distinguishes the initial render from subsequent polls:
- **Initial render**: full DOM build (search bar, filter chips, sortable table head, empty body) plus event binding; the default filter is the most actionable non-empty status (WIP → open → blocked → closed).
- **Subsequent polls**: skipped entirely when the payload is byte-identical to the previous fetch; otherwise only the table body and the filter-chip counts re-render in place. Scroll position, filter/sort selections, search input, and any expanded detail panel are preserved across the refresh.

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
