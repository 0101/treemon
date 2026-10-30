# Beadspace Canvas Dashboard

## Goals

- Expose each worktree's beads issues in a sortable, filterable canvas table with expandable details.
- Keep generated content current without discarding the user's browsing state.

## Expected Behavior

- Each successful scheduled Beads refresh provisions `beads.html` when the
  worktree's status summary contains issues, and removes an existing page when
  that summary is empty. Failed storage reads leave the previous page intact.
- The page renders one always-visible sortable and filterable issues table. It has no separate
  dashboard view or top navigation bar.
- Each status **filter chip shows a count badge** of issues in that status (hidden when the count is zero); the `All` chip shows no count.
- The **default filter on load** is the most actionable non-empty status, chosen in priority order **WIP → open → blocked → closed** (falling back to open when empty). After load, the user's filter choice is preserved across polls.
- Clicking an issue row expands a **detail panel** showing: full description, all labels, priority badge, type badge, dependency count, dependent count, age
- Data refreshes every 30 seconds by polling a same-origin JSON endpoint on port 5002
- Data refresh is **incremental** — only the issues table body re-renders. Scroll position, expanded detail panels, filter/sort selections, and search input are preserved across polls.
- Theme matches Catppuccin Mocha (canvas pane dark theme)
- No external dependencies (CDN, fonts, frameworks) — fully self-contained HTML
- A postMessage `{ action: 'refresh-beads' }` triggers immediate data reload (also incremental)

## Canvas Doc Kind: SystemView

`beads.html` is classified as a **`SystemView`**, not an `AgentDoc` (`CanvasDocKinds.classify "beads.html" → SystemView`). The shared filename list lives in `src/Extension/canvas-doc-kinds.json` and is consumed by both the server and browser fallback. It is server-generated and data-driven with no owner session, so it deliberately opts out of the agent-doc machinery the canvas pane applies to authored docs:

- **No liveness dot and no `▶ Start session` button** — there is no author session to be alive or to launch.
- **No author heartbeat bridge** — authored heartbeat and morph machinery are omitted, while
  `canvasSend` and selected-text actions remain. Interactions use the worktree activity, bridge-gap,
  and exact fallback policy in `docs/spec/canvas-interaction-routing.md`, never stored ownership.
- **No DOM morph** — the idiomorph runtime and morph controller are also omitted. A morph would stomp the live, JS-rendered table back down to the empty template shell; the dashboard instead refreshes itself via its 30s `/beads-data` poll and the `refresh-beads` postMessage.
- **Excluded from authored-document inventory and awareness** — `CanvasAwareness.agentDocs` filters
  `SystemView` docs out, so the beads file never contributes to the card's canvas strip, unviewed
  badges, seeded viewed-hashes, or idle auto-display. The file hash is stable while the data changes;
  beads state is surfaced on the worktree card as `BeadsSummary` instead.
- **Distinct tab affordance, no archive** — it renders as a far-left `.canvas-system-tab` entry (a "BD" glyph + total-issue-count badge) rather than a normal doc tab, and the archive button is hidden (the file is regenerated from the template, not user-owned).

See `docs/spec/canvas-pane.md` for the generic pane behavior and the full `AgentDoc` vs `SystemView` rationale the two kinds share.

## Technical Approach

### Generated Template

The self-contained template adapts the MIT-licensed `cameronsjo/beadspace` issues table to
Catppuccin Mocha, without external fonts, a light theme, or a separate dashboard/navigation view.
It derives its same-origin `beads-data` URL from the document location. Selections within an issue
row add the Beads task ID as structured `sourceContext`, separate from the user request and text.

### Same-Origin Data Endpoint

`GET /{encodedWorktreePath}/beads-data` on the canvas doc server (port 5002) runs `bd list --json`
against the worktree's `.beads/beads.db` and returns the issue array as `application/json`
(`Cache-Control: no-cache`). The route uses the same normalized known-worktree lookup as document
serving. The template consumes the `bd` issue shape, not the GitHub Issues format.

### Auto-Provisioning

`RefreshScheduler` passes the summary from `BeadsStatus.getBeadsData` to
`BeadspaceProvisioner` after each successful Beads refresh. The summary and
Overview planning counts share one read-only snapshot of `.beads/beads.db` when
present, regardless of export presence or age. JSONL-only worktrees without a
database use `.beads/issues.jsonl`. See
[Beads Overview Band](beads-overview-band.md#technical-approach) for source
selection, status counting, parent-edge semantics, and failure handling.

Provisioning does not depend on an export or a CLI invocation. A database error
fails the refresh without clearing counts, deleting the page, or using stale
export data. An empty authoritative store (or no store) produces zero counts.
The page's data endpoint continues to read the live database independently;
JSONL-only support here covers summary, planning, and provisioning, not that
database-backed detail endpoint.

A nonzero summary ensures `.agents/canvas/beads.html` matches the embedded template, creating the
directory and writing only when content differs. Zero counts remove an existing page.
`BeadspaceTemplate.fs` loads the embedded HTML once; browser tests read the same source file.
The generated page is not user-customizable because subsequent refreshes restore the template.

### Incremental Data Refresh

The template distinguishes the initial render from subsequent polls:
- **Initial render**: full DOM build (search bar, filter chips, sortable table head, empty body) plus event binding; the default filter is the most actionable non-empty status (WIP → open → blocked → closed).
- **Subsequent polls**: skipped entirely when the payload is byte-identical to the previous fetch; otherwise only the table body and the filter-chip counts re-render in place. Scroll position, filter/sort selections, search input, and any expanded detail panel are preserved across the refresh.

### Key Files

| File | Purpose |
|------|---------|
| `src/Server/BeadsStatus.fs` | Live issue JSON and database-first summary/planning snapshots |
| `src/Server/CanvasDocServer.fs` | Known-worktree `beads-data` route |
| `src/Server/BeadspaceTemplate.html` | Beadspace template — single source of truth (embedded resource; also read from disk by the E2E tests) |
| `src/Server/BeadspaceTemplate.fs` | Reads the embedded `BeadspaceTemplate.html` resource at startup, exposing it as the `html` value |
| `src/Server/BeadspaceProvisioner.fs` | Template synchronization and empty-summary removal |
| `src/Server/RefreshScheduler.fs` | Beads refresh and provisioning orchestration |

## Related Specs

- `docs/spec/canvas-pane.md` — generic canvas pane architecture and the `AgentDoc` vs `SystemView` behavior matrix
- `docs/spec/canvas-interaction-routing.md` — generic selected-text routing and ownership
