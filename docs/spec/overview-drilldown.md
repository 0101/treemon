# Overview Drill-Down

Make the Overview band's agent groups and task groups **clickable**. Selecting a group opens a
detail panel — directly below that group's row — listing the worktrees that make up the group.
Each listed worktree is clickable and, when clicked, selects it in the dashboard exactly as
arrow-key navigation would (focus its card, uncollapse its repo, scroll it into view).

Builds on the read-only band from `docs/spec/beads-overview-band.md` (which explicitly deferred
"hover, click, or greenlight interactions"). Investigation: `.agents/overview-drilldown-investigation.md`.
Static styling prototypes: `.agents/canvas/overview-drilldown-investigation.html`.

## Goals

- From the band alone, answer "**which worktrees** make up this activity/status?" without scanning
  cards.
- Jump from an aggregate straight to the relevant worktree card with one click — the same
  focus/expand/scroll behavior as arrow-key navigation.
- Keep the roll-up counts and the drill-down member lists derived from **one set of predicates** so
  they can never diverge.
- Stay client-only: no `Shared`/server/API changes for the core feature.

## Expected Behavior

### Selecting a group

- Each agent-group column (Active agents section) and task-bucket column (Tasks section) is
  clickable. Clicking a column selects that group; clicking the selected group again deselects it.
- **Single-select**: at most one group is selected at a time (across both sections).
- The selected column renders as a **black "tab"** with rounded top corners, sitting flush against a
  black **breakdown panel** rendered directly beneath the group's row.
- The breakdown panel appears **inside its own section**:
  - Agent-group breakdown renders **between the Active agents row and the Tasks section**.
  - Task-bucket breakdown renders **directly below the Tasks row**.
- Within each section, the group columns **wrap** to the next line when the pane is too narrow (a
  row-gap keeps wrapped lines legible), instead of a horizontal scrollbar.
- The panel closes when: the group is re-clicked, the panel's **✕** button is clicked, or **Esc** is
  pressed while a group is selected.
- Selection is **ephemeral** session state — it does not persist across reloads (unlike the band's
  open/closed state).

### Breakdown panel content

- A header shows the group label in the group's accent color plus a muted summary
  (e.g. `Executing · 3 agents · 2 repos`; `In progress · 12 tasks · 4 worktrees`) and the ✕ close
  button.
- Member worktrees are **grouped by repo**, each repo introduced by a small uppercase muted repo
  name (matching the band's section-header style).
- **Agent-group breakdown**: per repo, borderless inline **chips**, each `[● branch-name]` with the
  dot in the group's activity color. One chip per member worktree (agent groups are one agent per
  worktree, so chip count == the group count).
- **Task-bucket breakdown**: per repo, one row per member worktree: the branch name followed by a
  **bar** in the bucket's color, sized on the **same linear scale as the overview bars**. One task is
  the same pixel width here as in the band above (`barMaxPx / Overview.Scale` px per task), so a
  worktree's row bar reflects its own task count in that bucket and the rows sum to the bucket's
  overview bar width.

### Clicking a worktree

Clicking a member worktree selects it in the dashboard, identical to arrow-key navigation:

1. Uncollapse the owning repo if it is collapsed (`expandRepoOwning`), persisting collapsed-repo
   state if that changed it.
2. Focus the worktree card (`applyFocus` with retarget, the single focus chokepoint).
3. Scroll the dashboard so the card is in the viewport (`scrollFocusedIntoView Normal`).

It does **not** open the Canvas pane (this is the deliberate difference from the existing
`FocusOverviewCard`, which force-opens the pane on a doc).

`SelectOverviewWorktree` guards against archived breakdown rows: non-`Done` buckets keep archived
worktrees as members, but archived worktrees have no focusable card (`visibleFocusTargets` scans only
`repo.Worktrees`). A `scopedKey` that does not resolve to a focusable card via
`Navigation.resolvesToFocusableCard` is a **no-op** — it never sets an invalid `FocusedElement`
(which would produce no visible focus/scroll and be reset by `adjustFocusForVisibility`).

### Edge cases

- Empty groups are already omitted upstream (a group with count 0 never renders), so a selected
  group always has ≥1 member.
- After a data refresh, if the selected group is no longer present in the fresh roll-up (its count
  dropped to 0), the selection is cleared and the panel closes.
- Toggling the band closed clears any selection.

## Technical Approach

Three client layers, plus tests.

### 1. Data — expose group membership (`src/Client/OverviewData.fs`)

Extend the roll-up so each group carries its member worktrees, built from the **same predicates**
that already compute the counts (single source of truth):

```fsharp
type GroupMember =
    { ScopedKey: string      // WorktreePath.value — the focus/selection key
      Branch: string
      RepoId: RepoId         // stable repo identity — keeps two same-named repos separable
      RepoName: string
      Contribution: int }    // agent group: 1; task bucket: this worktree's task count in the bucket

type TaskBucket = { Kind: TaskBucketKind; Count: int; Members: GroupMember list }
type AgentGroup = { Kind: AgentGroupKind; Count: int; Members: GroupMember list }
```

- `aggregate` currently flattens `repos |> List.collect _.Worktrees`, discarding repo identity.
  Build membership **before flattening** (or thread `RepoWorktrees.RootFolderName` onto each worktree)
  so each `GroupMember` carries its `RepoName`.
- Invariants (assert in tests): agent group `Count = Members.Length`; task bucket
  `Count = Members |> List.sumBy _.Contribution`. Members preserve the repo order and the canonical
  group order.
- Task-bucket membership follows the existing per-bucket predicates exactly (e.g. `InProgress` counts
  `Beads.InProgress` only where `isActive`; `Done` excludes archived; `Planned` folds `Loose`;
  `Unattended` = inactive worktrees' `InProgress + Queued`). A worktree is a member iff its
  contribution to that bucket is > 0.

### 2. State + messages (`src/Client/AppTypes.fs`, `src/Client/App.fs`)

```fsharp
[<RequireQualifiedAccess>]
type OverviewSelection =
    | Agents of AgentGroupKind
    | Tasks of TaskBucketKind
```

- `Model` gains `SelectedOverviewGroup: OverviewSelection option` (initialized `None`).
- New messages:
  - `SelectOverviewGroup of OverviewSelection` — **toggles**: selecting the already-selected group
    sets it back to `None`.
  - `SelectOverviewWorktree of scopedKey: string` — the arrow-nav-parity handler:
    `expandRepoOwning` → `applyFocus true (Some (Card scopedKey))` → `scrollFocusedIntoView Normal`,
    persisting collapsed repos when a repo was expanded. Must **not** open the Canvas pane.
- `ToggleOverviewPanel` clears `SelectedOverviewGroup` when it closes the band.
- On `DataLoaded`, drop `SelectedOverviewGroup` if the fresh roll-up no longer contains that group.
- `Esc` closes the panel when a group is selected (extend the dashboard key handling / `KeyPressed`).

### 3. View + CSS (`src/Client/OverviewBand.fs`, `src/Client/index.html`)

- `OverviewBand.view` gains the current `OverviewSelection option` and callbacks
  (`onSelectGroup`, `onSelectWorktree`) — wired from `App.fs`'s view (which currently calls
  `OverviewBand.view model.Repos`).
- Each `agentColumn` / `taskColumn` becomes clickable (raises `onSelectGroup`) and gets an
  `overview-item-selected`/tab class when it is the selected group.
- Render the breakdown panel inside the relevant section when a matching group is selected: header +
  ✕, repo-grouped members, agent chips vs. task bars (task bar width = `member.Contribution * barMaxPx / overview.Scale`, floored at the existing visible minimum).
- CSS additions near the existing `.overview-*` rules (`index.html`): the selected black tab
  (rounded top corners), the `.overview-breakdown` panel, `.overview-chips`/chip, the task
  `name + bar` rows, the close button, and `.overview-items` wrapping (`flex-wrap: wrap`, no
  horizontal scrollbar). Follow
  the existing Catppuccin palette and the CSS-classes-only rule (the proportional bar width is the
  already-documented inline-width exception).

### 4. Tests

- **Unit** (`src/Tests/OverviewDataTests.fs`): membership correctness per bucket/group — the right
  worktrees, correct `Contribution`, `Count` == list length / Σ contributions, respects
  `isActive`/`IsArchived`, and repo names populated.
- **E2E**: **no new E2E tests are added** for this feature (project decision). Verification instead
  proves the existing E2E suite (`Category=E2E`, incl. the read-only `OverviewBandE2ETests`) still
  passes with zero regressions, alongside the `OverviewData` membership unit tests above.

## Decisions

Locked during prototyping (see the canvas prototype doc):

- **Worktree click = arrow-nav parity, no Canvas pane.** Distinguishes it from `FocusOverviewCard`.
- **Panel placement: inside the band**, directly under the selected group's section row.
- **Single-select**; re-click / ✕ / Esc closes.
- **Ephemeral** selection (not persisted).
- **Groups wrap** to the next line per section on narrow panes (no horizontal scrollbar).
- **Agent breakdown** = borderless activity-colored `[● name]` chips; **task breakdown** = `name + bar`
  rows on the band's shared task scale.
- **No new E2E tests.** Verification = existing E2E suite stays green + `OverviewData` membership unit
  tests; new UI behavior is covered by unit tests at the data layer, not new Playwright tests.

## Related Specs

- `docs/spec/beads-overview-band.md` — the read-only band this extends.
