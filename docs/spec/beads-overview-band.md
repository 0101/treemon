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
- Two stacked sections are separated by a **1px dashed** rule and headed by small uppercase muted
  labels: `AGENTS` and `TASKS`. The per-group/per-bucket counts live in the columns below, so the
  headers stay bare (no count suffix, no "across all worktrees" caption).
- Each category is a column with its **count-first label above the visual**: the count uses the
  category accent, the label stays neutral, and both share the same font size/weight.
  1. **Agents** — **circles** (~15px), grouped by the running skill (working agents),
     the waiting-for-user state, or the **idle** state (blue-dot idle agents that finished a
     turn with the CLI still open). One circle **per live session**, clustered per agent (a worktree
     with several open sessions shows several adjacent circles), so all of an agent's sessions are
     visible rather than a single collapsed mark. Each circle is a **context-usage donut**: a ring
     filled to the fraction of context-window still **remaining** (`1 − currentTokens / tokenLimit`,
     from the SDK `session.usage_info` event) — a healthy low-usage session is a nearly full ring and
     one near its limit thins to a sliver. A session that has not reported usage yet (or after a
     restart — the value is not persisted) falls back to the solid circle. Idle is a second track
     sharing the Agents row.
  2. **Tasks** — one solid **bar** per status (**Planned · Queued · In progress · Blocked · Done ·
     Unattended**), width ∝ count on **one true shared linear scale** (no cap, no fade). Each column
     keeps its label width so a short bar still shows its full label.
- Task bar width is computed from `Overview.Scale`: the largest bucket fills a fixed max width and
  smaller buckets render at `count / Scale` of it, with a small minimum width so a count of `1`
  remains visible. This dynamic width is the documented inline `width`/CSS-variable exception to the
  CSS-classes-only rule.
- **Palette (Catppuccin Mocha, exact):**
  - Tasks — Planned `#fab387` · Queued `#89dceb` · In progress `#a6e3a1` · Blocked `#f38ba8` · Done `#cba6f7` · Unattended `#7f849c`.
  - Activities — Investigating `#89dceb` · Planning `#cba6f7` · Executing `#a6e3a1` · Reviewing `#f5c2e7` · PR `#fab387` · Working `#ff0000`.
  - Waiting — `#f9e2af`, matching the card's `WaitingForUser` dot.
  - Idle — `#89b4fa`, matching the card's `Idle` dot. No two co-occurring agent
    groups share a hue: Investigating teal, Planning mauve, Executing green, Reviewing pink, PR
    peach, Working red, Waiting yellow, Idle blue.
- **Empty categories are omitted** — a status or activity with zero items renders no label and no
  bar/circle group (never a `0`).
- An all-empty band renders nothing.
- The band has no per-card activity stripe; the band alone conveys activity, and the card red dot is
  unchanged.
- The band is static — no hover, click, or greenlight interactions (deferred).

### Task buckets (definitions)

| Bucket | Definition |
|---|---|
| **Planned** | Open tasks under an **open** feature (planning done, awaiting go-ahead) **plus** loose open tasks (no/closed/blocked parent). |
| **Queued** | Open tasks under an **in_progress** feature (execution underway, next-up) **on a worktree with an active agent** (`CodingTool` = `Working` or `WaitingForUser`). On an inactive worktree they fold into **Unattended**. |
| **In progress** | Tasks with status `in_progress` (`Beads.InProgress`) **on a worktree with an active agent** (`CodingTool` = `Working` or `WaitingForUser`). On an inactive worktree they fold into **Unattended**. |
| **Blocked** | Tasks with status `blocked` (`Beads.Blocked`). |
| **Done** | Σ closed **issues** (any type) across **non-archived** worktrees (`Beads.Closed` where `not IsArchived`). Naturally bounded — a worktree's `.beads/beads.db` is not committed, so its closed issues drop out when the worktree is merged/deleted. Only filter is `not IsArchived`. |
| **Unattended** | `In progress` + `Queued` tasks whose worktree has **no active agent** (`CodingTool` = `Idle` or `NoSession`) — likely stale beads status nobody is working. A single muted catch-all, trailing Done. |

The **Planned/Queued/Loose** split derives from the **parent-child dependency graph + feature
status**: for each open task, find its parent feature (parent-child edge) and read that feature's
status — `open` → Planned, `in_progress` → Queued, none/`closed`/`blocked` → Loose. Loose is a
distinct server-side bucket for fidelity but folds into **Planned** for display (decision #6).

### Live agent activity

- A working agent is a red-dot worktree: `CodingTool = Working`. Working agents expose the **skill
  currently running**, surfaced from the **same session scan** that drives the red dot — no new data
  source.
- Worktrees with `CodingTool = WaitingForUser` form a separate **Waiting** group, distinct from
  skill-derived activities.
- A pure classifier maps skill/command name → an activity bucket:

  | Activity | Skills / commands |
  |---|---|
  | **Investigating** | `investigate`, `research` |
  | **Planning** | `bd-plan`, `bd-improve`, `bd-autoimprove` |
  | **Executing** | `execute`, `bd-execute`, `bd-phase`, `bd-autopilot`, `refactor` |
  | **Reviewing** | `review-branch`, `reviewing-tests`, `comprehensive-review`, `code-review`, `bd-review`, `contribution`, `review` (focused-review plugin — the actual `skill.invoked` `data.name`; `focused-review:review` also mapped) |
  | **PR** | `babysit-pr`, `pr`, `github`, `fix-build` |
  | **Working** (fallback) | working agent, no recognized skill |

- `Shared.Activity.classify : string -> CurrentActivity` implements the table and **normalizes its
  input** first — trims, takes the first whitespace-delimited token, strips a leading `/`, and
  lower-cases — so detectors may surface the **raw** skill/command (a Claude slash command such as
  `/pr https://…`, a CLI event name, or a VS Code tool-call name) without pre-cleaning; unknown or
  empty input ⇒ Working. `CurrentActivity` is `[<RequireQualifiedAccess>]` because its `Working`
  case would otherwise collide with `CodingToolStatus.Working`.
- The band groups red-dot working-agent circles by the currently running skill; red-dot agents with
  no recognized skill fall into the generic **Working** group. Waiting agents are shown in the
  separate Waiting group.
- There is no per-card activity stripe; the band alone conveys activity.

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

Derive **both** the status `BeadsSummary` and the planning split from this single parse; `getBeadsData`
has one enriched collection point and no `bd count --by-status` spawn, so there is no skew between
summary and split. Missing file → zeros (fresh/empty worktree). **Freshness caveat:**
the JSONL lags the db only until the next auto-flush; if guaranteed freshness is needed, refresh via
`bd export` before reading (one spawn, same cost as a `bd count`). Isolate all beads-schema
knowledge in `BeadsStatus`.

### Surface the running skill from the existing detector scan

The red dot comes from `CodingToolStatus.getRefreshData` scanning each worktree's session files. The
Copilot CLI running skill rides the same scan:

- **Copilot CLI** (`CopilotDetector.fs`): prefer the dedicated **`skill.invoked`** event
  (`data.name`); fall back to the latest `skill` tool-call in `assistant.message` `toolRequests`
  (`arguments_json.skill`).
- **Claude Code** (`ClaudeDetector.fs`): may surface a slash command when available; `None` is an
  acceptable degradation.
- **VS Code Copilot** (`VsCodeCopilotDetector.fs`): may surface `request.slashCommand.name` when
  present; `None` is an acceptable degradation.

Carry `CurrentSkill: string option` on `CodingToolResult` → `WorktreeStatus`. For working agents,
activity is **derived** from the skill via the pure Shared classifier (no separate stored field);
`WaitingForUser` is a separate group.

**Implementation notes (a32):**
- **Recency = preference.** Copilot's skill parser treats the newest decisive event as current:
  `skill.invoked` wins when present and falls back to a `skill` tool-call for a skill that is still
  starting. Non-skill `assistant.message`s between the signal and EOF parse to `None`, so the scan
  steps past them.
- **Copilot tool-call arg encoding.** Real `events.jsonl` encodes tool-call args as a nested
  `arguments` *object* (`{"skill":"fix-build"}`), whereas the session-store schema names it
  `arguments_json` as a JSON *string*. Both are handled (object read directly; string re-parsed).
- **VS Code skill = `request.slashCommand.name`** (e.g. `@binlog /summary` → `summary`) when that
  surface provides it; absent ⇒ `None`.
- **Provider selection for Copilot.** `getRefreshData` carries `CurrentSkill` from the same
  `target` provider as the last-message surfacing; when `target = Copilot` it resolves the running
  skill via `pickActiveSkill` over the CLI and VS Code surfaces. `pickActiveSkill` shares the
  `mostRecentActive` rule with `pickActiveProvider` (drop Idle surfaces, then newest mtime wins) and
  reuses the *same* `ProviderResult`s that drove status resolution, so the surfaced skill always
  comes from the surface that won the status — never from an idle surface that merely has a newer
  session file. Only the winning surface is scanned (lazy getter); both surfaces Idle ⇒ `None`
  (display consumers gate on an active session anyway). A raw-mtime comparison here (the original
  bug, focused-review F5) could attach an idle CLI skill onto an active VS Code session.
- **Bounded horizon (accepted degradation).** Like all detector reads, the skill scan reaches ~1 MB
  back from EOF. A skill whose start-of-run signal has scrolled past that window degrades to `None`
  → Working — consistent with the spec's graceful-degradation goal.
- **Copilot CLI skill freshness.** Copilot CLI has no explicit skill-finished event, and
  `assistant.turn_end` interleaves mid-skill, so it is not a boundary. The bounded backward events
  scan treats the first decisive signal as current: a `skill.invoked` / `skill` tool-call ⇒ that
  skill runs now; a **genuine `user.message` ⇒ `None`** because a new top-level or scheduled request
  means the prior skill's run is over. A skill context-injection `user.message` is transparent only
  when both markers are present (`source: "skill-<name>"` and a `<skill-context …>` content
  preamble), so the scan steps past it to the `skill.invoked` it belongs to. A `user.message` that is
  an `ask_user` reply is also not a boundary: the fold keeps the current skill across a reply (the
  reply follows the outstanding `ask_user` request rather than superseding the skill). This
  determination is a **forward fold over the whole session** (oldest→newest) through the append-aware
  per-session cache — see `worktree-monitor.md` (Copilot CLI Status Detection) — not a bounded tail
  scan, so a skill invoked megabytes back is still detected. Claude Code / VS Code Copilot may report `None`; display
  consumers still gate on an active session, so an idle card never shows a skill.

### Domain changes (`src/Shared/Types.fs`)

- `BeadsPlanning { Planned; Queued; Loose }` (+ `zero`), new field
  `Planning: BeadsPlanning` on `WorktreeStatus`.
- `CurrentActivity` DU (`Investigating | Planning | Executing | Reviewing | PR | Working`) +
  `Activity.classify : string -> CurrentActivity`; Waiting is an overview activity group derived
  from `CodingToolStatus.WaitingForUser`, not from skill classification.
- `CurrentSkill: string option` on `WorktreeStatus` (and `CodingToolResult`).
- `OverviewPanelOpen: bool` on `DashboardResponse`; `saveOverviewPanelOpen: bool -> Async<unit>` on
  `IWorktreeApi`.

Adding record fields breaks every construction site (no default record values in F#) — each
type-growth task must update all sites (`DemoFixture.fs` ×8, `WorktreeApi.fs` mapping,
`RefreshScheduler.fs`, client/server `IWorktreeApi` impls, test fixtures) in the same change to keep
the solution compiling (no compat shims, per house rules).

### Client aggregation + band

- Aggregate **client-side** (the client already receives every worktree). `Client/OverviewData.fs`
  (`OverviewData.aggregate : RepoWorktrees list -> Overview`) folds every **non-archived** worktree →
  task buckets (Planned = Σ Planned+Loose, Queued and InProgress only when the worktree has
  `CodingTool = Working` or `WaitingForUser`, inactive Queued/InProgress folded into Unattended,
  Blocked, Done = Σ Closed) + agent groups (`CodingTool = Working` grouped by
  `Activity.classify` of `CurrentSkill`, absent skill ⇒ Working; `CodingTool = WaitingForUser` ⇒
  Waiting; `CodingTool = Idle` ⇒ Idle; `NoSession` excluded) + `Scale` (the largest bucket count — the
  one true shared linear denominator). **Archived
  worktrees are excluded from the entire roll-up** (every task bucket and every agent group), so
  archiving a worktree drops all of its contributions at once. Empty buckets/groups are omitted
  (never a `0`); both lists come back in canonical order, with Unattended trailing Done. The result
  `Overview` carries `Tasks: TaskBucket list` / `Agents: AgentGroup list` / `Scale: int`
  (`TaskBucketKind` is `[<RequireQualifiedAccess>]` to keep its case names — `Done`, `Blocked`,
  `InProgress` — from colliding with `BeadsSummary` field labels and other DU cases). **Input contract:** pass the un-split `RepoWorktrees` shape (see decision (f))
  — not the client `RepoModel`.
- The band is native **Feliz with CSS classes**, with the documented exception that each task bar
  uses a computed inline width or CSS variable for its proportional scale. Toggle mirrors Canvas:
  `ToggleOverviewPanel` message, `OverviewPanelOpen` model state, `saveOverviewPanelOpen`
  persistence.
- There is no per-card activity stripe; `CardViews.cardClassName` keeps the existing coding-tool
  status classes and red dot semantics.

**Implementation notes (c8k — the band view, `src/Client/OverviewBand.fs`):**
- **Bars are one proportional mark per status.** Each task bucket renders one solid bar whose width
  is computed from `Overview.Scale`, with the largest bucket filling the fixed max width and all
  others at `count / Scale` of that width. The computed inline width / CSS variable is the accepted
  exception to static CSS classes because proportional width is inherently data-driven. Agent groups
  render one `.overview-circle` **per live session** (clustered per agent inside an `.overview-member`
  wrapper). A session with a known context-window occupancy also gets `.overview-donut` and an inline
  `--ctx-remaining` (0–1); the donut's conic-gradient fills that fraction of *remaining* context over
  a muted track and a radial mask cuts the centre hole — the same accepted inline-custom-property
  exception the task bars use with `--bar-fill`. No usage reported ⇒ the plain solid circle (donut
  class not applied). The drill-down agent chips reuse the same per-session donuts next to the branch
  name; collapsed repo headers show per-session **plain dots** (no donut — too dense for arcs).
- **Accent colour drives both mark and count via `currentColor`.** One class per category
  (`.task-*` / `.activity-*`) sets `color`; the count text takes it directly and each mark paints
  `background: currentColor`. Label stays neutral, the same inherited `12.5px`/weight `400` as the
  count — so count and label differ only by colour, per spec.
- **Section chrome.** The band renders bare `AGENTS` and `TASKS` headings (no count suffix, no
  "across all worktrees" caption — the columns carry the counts), separated by a 1px dashed rule,
  and has no top/bottom border hairlines or task footer caption.
- **RepoModel → RepoWorktrees recombination lives in the band** (`toRepoWorktrees`, the single
  `aggregate` call site) so decision (f)'s `Worktrees @ ArchivedWorktrees` merge can't be forgotten.
- **Empty-state collapse.** `view` drops an all-empty lens by pattern-matching (each section is built
  by the `section` helper) and returns `Html.none` when both lenses are empty, so an opened-but-empty
  band adds no chrome (not even margin).
- **Placement:** rendered in `App.fs` as the first child inside `.dashboard` (above `.repo-list`),
  gated on `model.OverviewPanelOpen`. The two sections stay **stacked** at every width; the reflow is
  the category columns wrapping onto new rows via `.overview-items { flex-wrap: wrap }` as the pane
  narrows — there is no container-query flip to a side-by-side layout.

## Decisions

Authoritative list is "Decisions locked" in `.agents/beads-panel-investigation.md`. Key ones:
band is chrome-less and dashboard-scoped; aggregate-only; agent **circles** + task **true-scale
bars**; empty categories omitted; **Planned vs Queued** = open vs in_progress parent feature; Loose →
Planned; **Done** = Σ closed; **archived worktrees excluded from the whole roll-up** (every task
bucket and every agent group); static interactions; reuse the single `getBeadsData`
call site; running skill from the existing session scan; per-session context usage (Extension C)
parked.

**Resolved during planning:**
- (a) `BeadsPlanning` is a **sibling record** — a new `Planning` field on `WorktreeStatus`, not a
  growth of `BeadsSummary`.
- (b) The status summary is **derived from the same JSONL parse**; there is no `bd count` spawn
  (single source, no new spawn).
- (c) **No keyboard shortcut** — the band is toggled by its `ctrl-btn` only (Canvas's `C` is
  deliberately not mirrored; deferred).
- (d) **`FeaturesOpen` / `FeaturesWip` are dropped** — the band never displays feature counts, so
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
- (f) **Archived worktrees are excluded from the entire roll-up; the aggregation folds the un-split
  `RepoWorktrees list`.** `OverviewData.aggregate` drops `IsArchived` worktrees up front (when
  building `taggedWorktrees`), so an archived worktree contributes to **no** task bucket and **no**
  agent group — archiving a worktree removes all of its Overview contributions at once. (This
  reverses the original decision, which scoped the `not IsArchived` filter to `Done` alone and let
  archived worktrees keep inflating Planned/Queued/In-progress/Blocked; users reported the residual
  counts as a bug — archived means "put away", so it should leave the band entirely.) Consequence for
  wiring: the aggregation receives the server-shaped `RepoWorktrees list` (every worktree present,
  archived ones flagged via `IsArchived`), **not** the client `RepoModel`, which pre-splits archived
  worktrees into a separate `ArchivedWorktrees` field. A `RepoModel`-based caller recombines
  `Worktrees @ ArchivedWorktrees` before calling `aggregate` and lets `aggregate` — the single owner
  of the archived policy — drop the archived ones.

**Additional locked decisions:**
- The visual contract is the count-first, label-above-mark layout with section headers, dashed
  separator, exact Catppuccin palette, no hairline borders, and no footer caption.
- Working agents are red-dot worktrees (`CodingTool = Working`); `WaitingForUser` is a separate
  Waiting group; idle sessions form a distinct blue Idle group and do not inflate activity counts.
- Copilot CLI skill freshness is bounded to the current request; Claude Code and VS Code Copilot may
  report `None`.
- `review` and `focused-review:review` both classify as Reviewing.
- There is no per-card `act-*` stripe; card activity remains the existing coding-tool status dot.
- In-progress and Queued task counts require an active agent and otherwise fold into the muted
  Unattended bucket; Planned, Blocked, and Done are unaffected.

## Key Files

| Concern | File |
|---|---|
| Domain types | `src/Shared/Types.fs` (`BeadsSummary`, `WorktreeStatus`, `DashboardResponse`, `IWorktreeApi`) |
| Beads collection | `src/Server/BeadsStatus.fs` (`getBeadsData`, `getBeadsIssueList`) |
| Cross-worktree aggregation | `src/Client/OverviewData.fs` (`aggregate`, `Overview`, `TaskBucket`, `AgentGroup`) |
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
