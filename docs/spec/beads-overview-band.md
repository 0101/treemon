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

## Corrections (v1.1 — prototype fidelity + activity accuracy)

The deployed v1 diverged from the agreed prototype (`.agents/canvas/beads-panel-prototypes.html`,
the chosen reference design) on **both** visual style and data accuracy. v1.1 makes the band match
that prototype exactly and makes the agent-activity counts truthful. **This section supersedes any
conflicting implementation note below.**

### What v1 got wrong

- **Visual** (`OverviewBand.fs` + `index.html`): marks rendered *above* the label (prototype: label
  above the marks); count *after* the label (prototype: **count first**, e.g. `4 Investigating`);
  **no section headers** (`ACTIVE AGENTS · N WORKING` / `TASKS · ACROSS ALL WORKTREES`); no dashed
  separator; no footer caption; task bars drawn as **N fixed 8px unit cells** (so `Done = 477`
  overflowed to full width while small counts were tiny squares) instead of one proportionally-scaled
  bar on the true shared scale; and accent colours **shuffled off-palette** (Done rendered green not
  mauve, Planning yellow not mauve, In progress blue not green …).
- **Data** (`OverviewData.fs`): counted every worktree with `HasActiveSession` — a valid *terminal
  window*, which includes Done/Idle/WaitingForUser (blue/grey/yellow dots) — **not** the red dot. So
  ~21 idle/finished agents inflated **Working** and one lingering `CurrentSkill` showed **Planning**
  when nothing was planning. `CurrentSkill` is also last-seen, not the skill running *now*.

### Corrected visualization — match the prototype exactly

Reference: the `.band` block in `.agents/canvas/beads-panel-prototypes.html`. Two stacked `.psec`
sections separated by a **1px dashed** rule:

1. Each section opens with a small **uppercase muted header** (`.plabel`): `ACTIVE AGENTS · N WORKING`
   (extend with waiting when present) and `TASKS · ACROSS ALL WORKTREES`. `N` = count of red-dot
   working agents.
2. Each category is a **column**: a **count + label** meta line **above** its visual — **count
   first**, coloured to the accent; label neutral; both the same font size/weight, differing only by
   colour (e.g. `4 Investigating` over four circles, `34 Planned` over its bar).
3. **Agents = circles** (~15px), one per agent, grouped by activity, coloured per activity.
4. **Tasks = one solid bar** per status, **width ∝ count on one true shared scale** — the widest
   bucket fills a fixed max width and every other bar is `count / Scale` of it (min ~5px so a `1` is
   still visible). Use `Overview.Scale` as the denominator with a **computed width**. *An inline
   `width` (or a CSS variable) is an accepted, documented exception to the CSS-classes-only rule — a
   proportional width is inherently dynamic and cannot be a static class; the unit-cell workaround is
   removed.* Because the label sits on its own line above, a short bar always keeps its full label.
5. **Palette (Catppuccin Mocha, exact):**
   - Tasks — Planned `#fab387` · Queued `#89dceb` · In progress `#a6e3a1` · Blocked `#f38ba8` · Done `#cba6f7` · Unattended `#7f849c` (muted grey).
   - Activities — Investigating `#89dceb` · Planning `#cba6f7` · Executing `#a6e3a1` · Reviewing `#f5c2e7` · Fixing `#fab387` · Working (fallback) `#ff0000` (matches the card's `Working` red dot).
   - Waiting — `#f9e2af` (reuse the card's `WaitingForUser` dot colour). This frees `#f9e2af` from
     Reviewing, which moves to Pink `#f5c2e7` so no two co-occurring agent groups share a hue
     (Investigating teal, Planning mauve, Executing green, **Reviewing pink**, Fixing peach, Working
     red, **Waiting yellow** — all distinct).
6. Empty categories stay omitted (never a `0`); an all-empty band renders nothing.
7. **Chrome-less:** no border/hairlines around the band and **no footer caption** — the toggle
   button is the only header.

### Corrected agent-activity semantics

- **Working agent = red dot = `CodingTool = Working`.** The agent groups count only red-dot
  worktrees — replacing the `HasActiveSession` filter, which counted mere terminal presence.
- **Waiting is its own group.** Worktrees with `CodingTool = WaitingForUser` (yellow dot) form a
  separate "Waiting for user" group, distinct from the skill-derived activities.
- **Group working agents by the skill running *now*.** Activity is the currently-executing skill's
  bucket. `focused-review:review` is added to the classifier → **Reviewing**. A red-dot agent with no
  recognized skill falls to the generic **Working** group — an honest count, no longer inflated by
  idle agents.
- **Skill freshness is Copilot-CLI-only.** `CopilotDetector` must report `CurrentSkill` only while the
  skill is *actively executing* — a skill invoked earlier in the same still-running session that has
  since finished must not linger. **Claude Code and VS Code Copilot may report `None`/empty** (the
  user runs Copilot CLI; making the others exact is out of scope — do only if trivial). This replaces
  the "not staleness-gated" note.

### Remove the per-card activity stripe

Drop the `act-*` left stripe entirely: remove `activityStripe` from `CardViews.cardClassName`, delete
the `.wt-card.act-*` CSS in `index.html` (and the stripe-only `position: relative` add if unused
elsewhere), and remove its tests. The red dot is unchanged; the band alone now conveys the *what*.

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
     Done · Unattended**), width ∝ count on **one true shared linear scale** (no cap, no fade). Each
     column keeps its label width so a short bar still shows its full label.
- **Empty categories are omitted** — a status or activity with zero items renders no label and no
  bar/circle group (never a `0`).
- Category counts use the **same font size/weight** as their label, distinguished only by color.
- **v1 is static** — no hover, click, or greenlight interactions (deferred).

### Task buckets (definitions)

| Bucket | Definition |
|---|---|
| **Planned** | Open tasks under an **open** feature (planning done, awaiting go-ahead) **plus** loose open tasks (no/closed/blocked parent). |
| **Queued** | Open tasks under an **in_progress** feature (execution underway, next-up) **on a worktree with an active agent** (`CodingTool` = `Working` or `WaitingForUser`). On an inactive worktree they fold into **Unattended**. |
| **In progress** | Tasks with status `in_progress` (`Beads.InProgress`) **on a worktree with an active agent** (`CodingTool` = `Working` or `WaitingForUser`). On an inactive worktree they fold into **Unattended**. |
| **Blocked** | Tasks with status `blocked` (`Beads.Blocked`). |
| **Done** | Σ closed **issues** (any type) across **non-archived** worktrees (`Beads.Closed` where `not IsArchived`). Naturally bounded — a worktree's `.beads/beads.db` is not committed, so its closed issues drop out when the worktree is merged/deleted. Only filter is `not IsArchived`. |
| **Unattended** | `In progress` + `Queued` tasks whose worktree has **no active agent** (`CodingTool` = `Done` or `Idle`) — likely stale beads status nobody is working. A single muted catch-all, trailing Done. |

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
  | **Reviewing** | `pr`, `review-branch`, `reviewing-tests`, `comprehensive-review`, `code-review`, `bd-review`, `contribution`, `review` (focused-review plugin — the actual `skill.invoked` `data.name`; `focused-review:review` also mapped) |
  | **Fixing** | `fix-build`, `conflict` |
  | **Working** (fallback) | active session, no recognized skill |

- `Shared.Activity.classify : string -> CurrentActivity` implements the table and **normalizes its
  input** first — trims, takes the first whitespace-delimited token, strips a leading `/`, and
  lower-cases — so detectors may surface the **raw** skill/command (a Claude slash command such as
  `/pr https://…`, a CLI event name, or a VS Code tool-call name) without pre-cleaning; unknown or
  empty input ⇒ Working. `CurrentActivity` is `[<RequireQualifiedAccess>]` because its `Working`
  case would otherwise collide with `CodingToolStatus.Working`.
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

**Implementation notes (a32):**
- **Single backward scan, recency = preference.** Each detector reuses the existing bounded
  `FileUtils.scanBackward` (most-recent line wins). Copilot's parser matches *either* a
  `skill.invoked` event or a `skill` tool-call in one pass; because `skill.invoked` is written
  *after* its tool-call, recency naturally yields `skill.invoked` and falls back to the tool-call
  for a skill that is still starting (no second scan needed). Non-skill `assistant.message`s between
  the signal and EOF parse to `None`, so the scan steps past them.
- **Copilot tool-call arg encoding.** Real `events.jsonl` encodes tool-call args as a nested
  `arguments` *object* (`{"skill":"fix-build"}`), whereas the session-store schema names it
  `arguments_json` as a JSON *string*. Both are handled (object read directly; string re-parsed).
- **VS Code skill = `request.slashCommand.name`** (e.g. `@binlog /summary` → `summary`), captured
  onto `ReqState.SlashCommand` during request reconstruction; absent ⇒ `None`.
- **Provider selection for Copilot.** `getRefreshData` carries `CurrentSkill` from the same
  `target` provider as the last-message surfacing; when `target = Copilot` it resolves the running
  skill via `pickActiveSkill` over the CLI and VS Code surfaces. `pickActiveSkill` shares the
  `mostRecentActive` rule with `pickActiveProvider` (drop Idle surfaces, then newest mtime wins) and
  reuses the *same* `ProviderResult`s that drove status resolution, so the surfaced skill always
  comes from the surface that won the status — never from an idle surface that merely has a newer
  session file. Only the winning surface is scanned (lazy getter); both surfaces Idle ⇒ `None`
  (display consumers gate on an active session anyway). A raw-mtime comparison here (the original
  bug, focused-review F5) could attach an idle CLI skill onto an active VS Code session.
- **Bounded horizon (accepted degradation).** Like all detector reads, the scan reaches ~1 MB back
  from EOF. A skill whose start-of-run signal has scrolled past that window degrades to `None` →
  Working — consistent with the spec's graceful-degradation goal; not gated further.
- **Skill freshness (supersedes the old "not staleness-gated" note; v1.1 (i), yh5).** The Copilot
  scan now bounds the skill to the *current request*: scanning backward, a `skill.invoked` / `skill`
  tool-call ⇒ that skill runs now, but a **genuine `user.message` first ⇒ `None`** (a new request
  means the prior skill finished — no more lingering). The skill's own context-injection
  `user.message` (`source: "skill-<name>"` / `<skill-context …>` preamble) is transparent, so the
  scan steps past it to the `skill.invoked` it belongs to. `assistant.turn_end` is **not** a boundary
  (it interleaves mid-skill). Claude Code / VS Code surfaces are left as-is (out of scope). Display
  consumers still gate on an active session, so an idle card never shows a skill.

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

- Aggregate **client-side** (the client already receives every worktree). `Client/OverviewData.fs`
  (`OverviewData.aggregate : RepoWorktrees list -> Overview`) folds every worktree → task buckets
  (Planned = Σ Planned+Loose, Queued, InProgress, Blocked, Done = Σ Closed where `not IsArchived`)
  + activity groups (active worktrees grouped by `Activity.classify` of `CurrentSkill`; absent skill
  ⇒ Working) + `Scale` (the largest bucket count — the one true shared linear denominator). Empty
  buckets/groups are omitted (never a `0`); both lists come back in canonical order. The result
  `Overview` carries `Tasks: TaskBucket list` / `Activities: ActivityGroup list` / `Scale: int`
  (`TaskBucketKind` is `[<RequireQualifiedAccess>]` to avoid the `Done`/`Working` collisions with
  `CodingToolStatus`). **Input contract:** pass the un-split `RepoWorktrees` shape (see decision
  (f)) — not the client `RepoModel`.
- The band is native **Feliz with CSS classes only** (no inline styles). Toggle mirrors Canvas:
  `ToggleOverviewPanel` message, `OverviewPanelOpen` model state, `saveOverviewPanelOpen` persistence.
- Per-card stripe: an activity modifier class on `wt-card` in `CardViews.fs`.

**Implementation notes (c8k — the band view, `src/Client/OverviewBand.fs`):**
- **Bars are a run of unit cells, not an inline-width bar.** *(SUPERSEDED by correction (g): render exactly ONE proportional bar per status whose width is computed from `Overview.Scale` via an inline width / CSS variable; the unit-cell design in this bullet — including the "does not read `Overview.Scale`" claim — is historical.)* Each task bar renders `count` identical
  fixed-size (`8×12px`) `.overview-cell` spans that touch (container `gap: 0`), so a count-N bar is
  exactly N cells wide. This puts every bar on one true shared linear scale *structurally* — no cap,
  no fade — while honouring **CSS-classes-only / no inline styles** (the natural `width: count/Scale%`
  would need an inline style or CSS var, both disallowed). Consequently the view **does not read
  `Overview.Scale`**: the scale is implicit in the cell geometry, and the widest bucket = `Scale`
  cells by construction. `Scale` is retained on `Overview` as the documented denominator / for a
  future non-cell renderer. Agents reuse the same rhythm as `.overview-circle` spans (one per active
  agent) with a normal gap.
- **Accent colour drives both mark and count via `currentColor`.** One class per category
  (`.task-*` / `.activity-*`) sets `color`; the count text takes it directly and each mark paints
  `background: currentColor`. Label stays neutral, same `0.82em`/`600` as the count — so count and
  label differ only by colour, per spec.
- **RepoModel → RepoWorktrees recombination lives in the band** (`toRepoWorktrees`, the single
  `aggregate` call site) so decision (f)'s `Worktrees @ ArchivedWorktrees` merge can't be forgotten.
- **Empty-state collapse.** `renderSection` drops an all-empty lens and `view` returns `Html.none`
  when both lenses are empty, so an opened-but-empty band adds no chrome (not even margin).
- **Placement:** rendered in `App.fs` as the first child inside `.dashboard` (above `.repo-list`),
  gated on `model.OverviewPanelOpen`; reflow via a `@container dashboard (min-width: 1200px)` rule
  that flips the two sections from stacked (narrow) to side-by-side (wide).

**Implementation notes (49w — the per-card stripe, `CardViews.fs` + `index.html`):** *(SUPERSEDED by correction (k): the per-card stripe is REMOVED entirely — this subsection is historical context for the removal task only.)*
- **`CardViews.activityStripe` appends a card-scoped `act-*` modifier**, gated on `HasActiveSession`
  (mirrors the band's active-only filter, since `CurrentSkill` is not staleness-gated) and derived
  from `Activity.classify (CurrentSkill |> Option.defaultValue "")`. `Working` / no skill ⇒ `""` (no
  stripe). `cardClassName` concatenates it alongside `ct-*` / `has-session`, so the red dot is
  untouched.
- **Distinct `act-*` classes, not the band's `activity-*`.** The band's `.activity-*` set `color`
  (for `currentColor`); reusing them on the whole `wt-card` would tint every child text node, so the
  stripe uses its own `.wt-card.act-*` classes that only paint the stripe. Colors still match the
  band accents exactly (one source of truth for the *what*).
- **Stripe is a `::before` pseudo-element, not `border-left`.** A prior decision removed the
  has-session left border (guarded by a test asserting `borderLeftWidth == 0px`); the stripe adds
  `position: relative` to `.wt-card` and a 3px left `::before`, clipped to the rounded corners by the
  card's existing `overflow: hidden` — no layout shift and the border test still holds.

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
- (f) **Only `Done` filters archived; the aggregation folds the un-split `RepoWorktrees list`.**
  `OverviewData.aggregate` scopes the `not IsArchived` filter to `Done` alone — Planned/Queued/
  In-progress/Blocked sum across *all* worktrees, archived included. Rationale: `Done` accumulates
  closed work, so a stale/parked (archived) worktree would inflate it, whereas the other buckets are
  current work and naturally bounded. Consequence for wiring: the aggregation must receive the
  server-shaped `RepoWorktrees list` (every worktree present, archived ones flagged via
  `IsArchived`), **not** the client `RepoModel`, which pre-splits archived worktrees into a separate
  `ArchivedWorktrees` field. A `RepoModel`-based caller must recombine `Worktrees @ ArchivedWorktrees`
  before calling `aggregate`, or archived worktrees vanish entirely (silently zeroing their
  contribution to every bucket, not just `Done`).

**Corrections v1.1 (supersede where they conflict):**
- (g) **Prototype fidelity.** The band matches `.agents/canvas/beads-panel-prototypes.html` exactly
  (section headers, count-first labels *above* the visual, dashed separator, exact Catppuccin
  palette). The unit-cell bar is replaced by a **true-scale proportional bar** whose width
  is computed from `Overview.Scale` — a **computed inline width / CSS variable is an accepted
  exception** to the CSS-classes-only rule (a proportional width cannot be a static class).
- (h) **Working agent = red dot (`CodingTool = Working`)**, not `HasActiveSession`. `WaitingForUser`
  (yellow) is a **separate "Waiting" group**. A red-dot agent with no recognized skill → generic
  **Working** group (honest count).
- (i) **Skill freshness is Copilot-CLI-only.** `CopilotDetector` reports `CurrentSkill` only while the
  skill is actively executing now; a finished skill in a still-active session must not linger. Claude
  Code / VS Code Copilot may report `None` (out of scope — the user runs Copilot CLI).
  *Implemented (yh5):* Copilot CLI has **no explicit skill-finished event**, and `assistant.turn_end`
  interleaves constantly mid-skill (even between a skill tool-call and its `skill.invoked`), so it is
  **not** a boundary. The backward events scan instead treats the first of these as decisive: a
  `skill.invoked` / `skill` tool-call ⇒ that skill runs now; a **genuine `user.message` ⇒ None** (a
  new top-level or scheduled request means the prior skill's run is over). A skill's own
  **context-injection `user.message`** — Copilot tags it `source: "skill-<name>"` with a
  `<skill-context …>` content preamble, written right after `skill.invoked` — is part of the skill
  *starting*, so the scan steps past it (otherwise long orchestrators like `bd-execute`, whose only
  later user.message is that injection, would never report their skill).
  *Hardened (hsg, focused-review F4/F5):* a context injection is recognized only when **both**
  markers are present (`source: "skill-<name>"` **and** the `<skill-context …>` content) — source
  alone is system-controlled, but requiring only the content let a normal user message that merely
  *begins* "<skill-context" masquerade as one and resurrect a finished skill. And a **`user.message`
  that is an `ask_user` reply is not a boundary**: mid-skill the agent may ask the user a question
  (an `assistant.message` requesting the `ask_user` tool → WaitingForUser) and the same skill resumes
  after the answer. Since that reply is a plain `source:""` user.message indistinguishable per-line
  from a new request, the scan is **stateful** (newest→oldest): a candidate boundary is discarded
  only if the first assistant.message older than it was that outstanding `ask_user` request. This
  reads the whole bounded (~1 MB) tail (`FileUtils.readTailLines`) rather than `scanBackward`'s
  overlapping chunks, which can re-emit a boundary line and so cannot carry the scan state.
- (j) **`focused-review:review` → Reviewing** added to `Activity.classify` (verify the exact skill
  identifier Copilot CLI emits for the plugin skill).
  *Verified (yh5):* the plugin skill emits `skill.invoked` `data.name = "review"` (`source: "plugin"`)
  and its `skill` tool-call arg is `{"skill":"review"}` — **not** `focused-review:review`. Both
  `"review"` and `"focused-review:review"` are mapped to Reviewing (the former matches reality; the
  latter kept for robustness / any fully-qualified surface). Note `review` is distinct from the
  already-mapped `review-branch`, `bd-review`, `code-review`.
- (k) **Per-card `act-*` stripe removed** (`CardViews` + `index.html` + tests); the red dot is
  unchanged.

**Corrections v1.2:**
- (l) **Chrome trimmed.** The band drops its top/bottom border hairlines and the task footer caption
  — it is fully chrome-less (the toggle button is the only header).
- (m) **Generic Working agent = red.** The fallback **Working** activity circle uses `#ff0000`, the
  same red as the card's `Working` dot (was blue `#89b4fa`).
- (n) **In-progress / Queued gated on an active agent → new Unattended bucket.** `In progress`
  (`Beads.InProgress`) and `Queued` count toward their live bars only when their worktree has an
  **active agent** (`CodingTool` = `Working` or `WaitingForUser`). On an inactive worktree
  (`Done`/`Idle`) they are likely stale beads status nobody is working, so they fold into a single
  muted **Unattended** catch-all bucket (accent `#7f849c`) that trails Done in canonical order.
  Planned/Blocked/Done are unaffected (they count across all worktrees).

## Key Files

| Concern | File |
|---|---|
| Domain types | `src/Shared/Types.fs` (`BeadsSummary`, `WorktreeStatus`, `DashboardResponse`, `IWorktreeApi`) |
| Beads collection | `src/Server/BeadsStatus.fs` (`getBeadsData`, `getBeadsIssueList`) |
| Cross-worktree aggregation | `src/Client/OverviewData.fs` (`aggregate`, `Overview`, `TaskBucket`, `ActivityGroup`) |
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
