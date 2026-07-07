# Beads Overview Band

A chrome-less, collapsible **Overview** band at the top of the dashboard that rolls up beads
task state and live agent activity across **all monitored worktrees**. Toggled and persisted like
the Canvas pane. Investigation: `.agents/beads-panel-investigation.md` (see its "Decisions locked").

## Goals

- One glance answers "what is the swarm of agents doing, and what needs *me*?" without scanning
  every worktree card.
- Surface the novel **started-vs-awaiting** split: work an agent is already executing (**Queued**)
  vs. work where planning is done and the agent waits for the user's go-ahead (**Planned**).
- Show **live agent activity** (which skill each active agent is running) as an aggregate.
- Reuse the existing single per-worktree beads collection point and the Canvas toggle/persistence
  patterns — no second collection, no duplicated plumbing.
- Degrade gracefully: empty categories are simply absent, never rendered as `0`.

## Expected Behavior

### The Overview band

- A new `ctrl-btn` labelled **"Overview"** in `header-controls` (mirrors the Canvas button) toggles
  the band. Open/closed state persists in global config and survives reload, exactly like
  `CanvasPaneOpen`.
- The band is **chrome-less**: no box, no title bar — the toggle *is* its header. When open it hangs
  directly under the app-header on the dashboard background.
- **Placement is dashboard-scoped**: rendered inside `.dashboard`, above `.repo-list`. It leaves the
  Canvas pane untouched and reflows via the existing dashboard container-query on narrow panes.
- **Aggregate-only**: no per-worktree cards or rows inside the band (the grid below already does
  that). All figures are cross-worktree roll-ups.
- Two sibling sections, styled with the same count+label rhythm (left-aligned, gaps):
  1. **Active agents** — **circles**, one per active agent, **grouped by the running skill** (no
     per-agent status dot on the circle).
  2. **Tasks** — solid **bars**, one per status (**Planned · Queued · In progress · Blocked ·
     Done**), width ∝ count on **one true shared linear scale** (no cap, no fade). Each column keeps
     its label width so a short bar still shows its full label.
- **Empty categories are omitted** — a status or activity with zero items renders no label and no
  bar/circle group (never a `0`).
- Category counts use the **same font size/weight** as their label, distinguished only by color.
- **v1 is static** — no hover, click, or greenlight interactions (deferred).

### Task buckets (definitions)

| Bucket | Definition |
|---|---|
| **Planned** | Open tasks under an **open** feature (planning done, awaiting go-ahead) **plus** loose open tasks (no/closed/blocked parent). |
| **Queued** | Open tasks under an **in_progress** feature (execution underway, next-up). |
| **In progress** | Tasks with status `in_progress` (`Beads.InProgress`). |
| **Blocked** | Tasks with status `blocked` (`Beads.Blocked`). |
| **Done** | Σ closed **issues** (any type) across **non-archived** worktrees (`Beads.Closed` where `not IsArchived`). Naturally bounded — a worktree's `.beads/beads.db` is not committed, so its closed issues drop out when the worktree is merged/deleted. Only filter is `not IsArchived`. |

The **Planned/Queued/Loose** split derives from the **parent-child dependency graph + feature
status**: for each open task, find its parent feature (parent-child edge) and read that feature's
status — `open` → Planned, `in_progress` → Queued, none/`closed`/`blocked` → Loose. Loose is a
distinct server-side bucket for fidelity but folds into **Planned** for display (decision #6).

### Live agent activity

- Each active worktree (has a live session / red dot) exposes the **skill currently running**,
  surfaced from the **same session scan** that drives the red dot — no new data source.
- A pure classifier maps skill/command name → an activity bucket:

  | Activity | Skills / commands |
  |---|---|
  | **Investigating** | `investigate` |
  | **Planning** | `bd-plan`, `bd-improve`, `bd-autoimprove`, `spec-management` |
  | **Executing** | `bd-execute`, `bd-phase`, `bd-autopilot`, `refactor` |
  | **Reviewing** | `pr`, `review-branch`, `reviewing-tests`, `comprehensive-review`, `code-review`, `bd-review`, `contribution` |
  | **Fixing** | `fix-build`, `conflict` |
  | **Working** (fallback) | active session, no recognized skill |

- The band groups active-agent circles by running skill. A per-card **color stripe** on `wt-card`,
  colored by activity, adds the *what* alongside the existing binary red dot.

## Technical Approach

Two parts: (1) enrich the per-worktree beads data + surface the running skill server-side;
(2) add the collapsible band + toggle client-side, aggregating client-side across worktrees.

### Data source — parse `.beads/issues.jsonl` (no SQLite dependency)

Beads maintains `.beads/issues.jsonl` (its canonical JSONL export, auto-flushed after CRUD). Each
record carries its **dependency edges inline**:

```json
{ "id": "...", "status": "open", "issue_type": "task",
  "dependencies": [ { "issue_id": "<child>", "depends_on_id": "<parent>", "type": "parent-child" } ] }
```

For a `parent-child` edge, **`issue_id` = child, `depends_on_id` = parent** (the child depends on its
feature). A single read of this file yields every issue's status/type **and** its parent-child parent
— everything the split needs, in one call, with:

- **No new package** (no SQLite — none is referenced anywhere today).
- **No binary-schema coupling** (JSONL is beads' stable interchange format).
- Consistency with the house rules (minimal moving parts, reuse what exists).

Derive **both** the status `BeadsSummary` and the planning split from this single parse, replacing
the current `bd count --by-status` spawn in `getBeadsSummary` — one enriched collection point, no
skew between summary and split. Missing file → zeros (fresh/empty worktree). **Freshness caveat:**
the JSONL lags the db only until the next auto-flush; if guaranteed freshness is needed, refresh via
`bd export` before reading (one spawn, same cost as today's `bd count`). Isolate all beads-schema
knowledge in `BeadsStatus`.

### Surface the running skill from the existing detector scan

The red dot comes from `CodingToolStatus.getRefreshData` scanning each worktree's session files. The
running skill rides the same scan:

- **Copilot CLI** (`CopilotDetector.fs`): prefer the dedicated **`skill.invoked`** event
  (`data.name`); fall back to the latest `skill` tool-call in `assistant.message` `toolRequests`
  (`arguments_json.skill`).
- **Claude Code** (`ClaudeDetector.fs`): `tryExtractSlashCommand` already extracts the slash command
  — the command *is* the skill. Surface it.
- **VS Code Copilot** (`VsCodeCopilotDetector.fs`): verify its tool-call encoding; surface the skill
  if present, else `None` (→ Working).

Carry `CurrentSkill: string option` on `CodingToolResult` → `WorktreeStatus`. Activity is **derived**
from the skill via the pure Shared classifier (no separate stored field), so client and card share
one source of truth.

### Domain changes (`src/Shared/Types.fs`)

- `BeadsPlanning { Planned; Queued; Loose }` (+ `zero`), new field
  `Planning: BeadsPlanning` on `WorktreeStatus`.
- `CurrentActivity` DU (`Investigating | Planning | Executing | Reviewing | Fixing | Working`) +
  `Activity.classify : string -> CurrentActivity`.
- `CurrentSkill: string option` on `WorktreeStatus` (and `CodingToolResult`).
- `OverviewPanelOpen: bool` on `DashboardResponse`; `saveOverviewPanelOpen: bool -> Async<unit>` on
  `IWorktreeApi`.

Adding record fields breaks every construction site (no default record values in F#) — each
type-growth task must update all sites (`DemoFixture.fs` ×8, `WorktreeApi.fs` mapping,
`RefreshScheduler.fs`, client/server `IWorktreeApi` impls, test fixtures) in the same change to keep
the solution compiling (no compat shims, per house rules).

### Client aggregation + band

- Aggregate **client-side** (the client already receives every worktree). A pure module folds
  `RepoWorktrees list` → task buckets (Planned = Σ Planned+Loose, Queued, InProgress, Blocked,
  Done = Σ Closed where `not IsArchived`) + activity groups (active worktrees by `CurrentSkill` /
  `Activity.classify`) + the true-scale max.
- The band is native **Feliz with CSS classes only** (no inline styles). Toggle mirrors Canvas:
  `ToggleOverviewPanel` message, `OverviewPanelOpen` model state, `saveOverviewPanelOpen` persistence.
- Per-card stripe: an activity modifier class on `wt-card` in `CardViews.fs`.

## Decisions

Authoritative list is "Decisions locked" in `.agents/beads-panel-investigation.md`. Key ones:
band is chrome-less and dashboard-scoped; aggregate-only; agent **circles** + task **true-scale
bars**; empty categories omitted; **Planned vs Queued** = open vs in_progress parent feature; Loose →
Planned; **Done** = Σ closed non-archived; v1 static; reuse the single `getBeadsSummary` call site;
running skill from the existing session scan; per-session context usage (Extension C) parked.

**Resolved during planning:**
- (a) `BeadsPlanning` is a **sibling record** — a new `Planning` field on `WorktreeStatus`, not a
  growth of `BeadsSummary`.
- (b) The status summary is **derived from the same JSONL parse**; the `bd count` spawn is removed
  (single source, no new spawn).
- (c) **No keyboard shortcut in v1** — the band is toggled by its `ctrl-btn` only (Canvas's `C` is
  deliberately not mirrored; deferred).
- (d) **`FeaturesOpen` / `FeaturesWip` are dropped** — the v1 band never displays feature counts, so
  `BeadsPlanning` carries only `{ Planned; Queued; Loose }` (no computed-but-dead fields). The
  classifier still reads each task's parent-feature status to bucket it accurately; it just emits no
  standalone feature counts. The **Planned-vs-Queued** count must be exact — it is the feature's
  core signal.
- (e) **Classifier subjects are OPEN, non-feature issues** (`Server.BeadsStatus.Planning.classify`
  over a lightweight `PlanningIssue { Id; IssueType; Status; ParentId option }`, both defined in
  `BeadsStatus.fs` to isolate beads-schema knowledge). A feature is a *container*, never a bucketed
  task: since display folds Loose into Planned and Planned is defined over open *tasks*, counting an
  open feature would over-count it — so features are excluded from the subjects. Non-open items
  (in_progress/blocked/closed) are left to the status `BeadsSummary`, so the split and the summary
  never overlap. A parent that is absent, dangling (id not in the set), non-feature, or a
  closed/blocked feature ⇒ Loose. Matching is one hop and case-insensitive against the raw beads
  strings (`"feature"`, `"open"`, `"in_progress"`).

## Key Files

| Concern | File |
|---|---|
| Domain types | `src/Shared/Types.fs` (`BeadsSummary`, `WorktreeStatus`, `DashboardResponse`, `IWorktreeApi`) |
| Beads collection | `src/Server/BeadsStatus.fs` (`getBeadsSummary`, `getBeadsIssueList`) |
| Session/skill scan | `src/Server/CopilotDetector.fs`, `ClaudeDetector.fs`, `VsCodeCopilotDetector.fs`, `CodingToolStatus.fs` |
| Refresh + assembly | `src/Server/RefreshScheduler.fs`, `src/Server/WorktreeApi.fs` |
| Toggle precedent | `src/Client/App.fs` (`ToggleCanvasPane`, `header-controls`), `saveCanvasPaneOpen` |
| Cards | `src/Client/CardViews.fs` (`beadsCounts`, `beadsProgressBar`, `wt-card`) |
| Fixtures | `src/Server/DemoFixture.fs` |

## Related Specs

- `docs/spec/canvas-pane.md` — the toggle/persistence pattern this band mirrors.
- `docs/spec/beadspace-canvas.md` — the per-worktree beads canvas doc (distinct from this
  cross-worktree roll-up; may share the data layer).
- `docs/spec/worktree-monitor.md` — dashboard architecture and domain types.

## Verification Strategy

- **Unit** (in impl tasks): the planning classifier (feature open/in_progress/closed; task with no
  parent; a `blocks` edge must **not** be treated as parent-child; loose bucket) and the
  skill→activity classifier (each known skill → its bucket; unknown/empty → Working); cross-worktree
  aggregation (sums match inputs; archived excluded from Done; empty categories omitted).
- **Data correctness E2E**: the enriched collection over a known worktree's `.beads/issues.jsonl`
  matches the manual issues+dependencies join. Choose a worktree that actually exercises the split
  (open tasks under **both** an open and an in_progress feature) so Planned **and** Queued are
  non-zero — an all-closed worktree proves nothing.
- **UI E2E** (Playwright): band renders, toggles, persists across reload; asserts on CSS classes /
  DOM structure (bars + circles present, empty status absent), not data values.
