# Overview Drill-Down

Make the Overview band's agent groups and task groups **clickable**. Selecting a group opens a
detail panel — directly below that group's row — listing the worktrees that make up the group.
Each listed worktree is clickable and, when clicked, selects it in the dashboard exactly as
arrow-key navigation would (focus its card, uncollapse its repo, scroll it into view).

Builds on the Overview band from `docs/spec/beads-overview-band.md`.

## Goals

- From the band alone, answer "**which worktrees** make up this activity/status?" without scanning
  cards.
- Jump from an aggregate straight to the relevant worktree card with one click — the same
  focus/expand/scroll behavior as arrow-key navigation.
- Keep the roll-up counts and the drill-down member lists derived from **one set of predicates** so
  they can never diverge.
- Keep membership in the shared Overview aggregate so the live band, drill-down, and history use the
  same task and agent predicates.

## Expected Behavior

### Selecting a group

- Each agent-group column (Agents section) and task-bucket column (Tasks section) is
  clickable. Clicking a column selects that group; clicking the selected group again deselects it.
- Selecting an agent group from the sticky Agents strip scrolls the dashboard all the way to the top
  after the selection renders, placing the newly-opened agent breakdown in view. Closing the selected
  group does not force a scroll.
- **Single-select**: at most one group is selected at a time (across both sections).
- The selected column renders as a **black "tab"** with rounded top corners, sitting flush against a
  black **breakdown panel** rendered directly beneath the group's row.
- The breakdown panel appears **inside its own section**:
  - Agent-group breakdown renders **between the Agents row and the Tasks section**.
  - Task-bucket breakdown renders **directly below the Tasks row**.
- In the expanded Overview, group columns **wrap** to the next line when the pane is too narrow (a
  row-gap keeps wrapped lines legible). The compact pinned Agents row stays on one line and uses
  horizontal overflow when needed, so no agent group is clipped.
- The panel closes when: the group is re-clicked, the panel's **✕** button is clicked, or **Esc** is
  pressed while a group is selected. An agent-group panel also closes when scrolling transitions
  the Agents strip into its compact pinned state.
- Selection is **ephemeral** session state — it does not persist across reloads (unlike the band's
  open/closed state).

### Breakdown panel content

- The panel's only chrome is the ✕ close button, positioned in the top-right corner so it takes no
  vertical space. (An earlier design placed a header above the members showing the group label in the
  group's accent color plus a muted summary — e.g. `Executing · 3 agents · 2 repos`;
  `Underway · 12 tasks · 4 worktrees` — but that only repeated the selected column tab sitting
  flush above the panel, so it was removed.)
- Member worktrees are **grouped by repo**, each repo introduced by a small uppercase muted repo
  name (matching the band's section-header style).
- **Agent-group breakdown**: per repo, borderless inline **chips**, each `[● branch-name]` with the
  dot in the group's activity color. One chip is rendered per member worktree; a chip can contribute
  multiple matching sessions, so the group count is the sum of member contributions rather than the
  number of chips.
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

Archived worktrees are excluded from the shared aggregate and never appear in a breakdown. A stale
`scopedKey` that no longer resolves to a focusable card via
`Navigation.resolvesToFocusableCard` is a **no-op** — it never sets an invalid `FocusedElement`
(which would produce no visible focus/scroll and be reset by `adjustFocusForVisibility`).

### Edge cases

- Empty groups are already omitted upstream (a group with count 0 never renders), so a selected
  group always has ≥1 member.
- After a data refresh, if the selected group is no longer present in the fresh roll-up (its count
  dropped to 0), the selection is cleared and the panel closes.
- Toggling the band closed clears any selection.

## Technical Approach

One shared data layer, two client layers, plus tests.

### 1. Data — expose group membership (`src/Shared/OverviewData.fs`)

Extend the roll-up so each group carries its member worktrees, built from the **same predicates**
that already compute the counts (single source of truth):

```fsharp
type GroupMember =
    { ScopedKey: string      // WorktreePath.value — the focus/selection key
      Branch: string
      RepoId: RepoId         // stable repo identity — keeps two same-named repos separable
      RepoName: string
      Sessions: SessionDot list
      Contribution: int }    // agent group: matching sessions; task bucket: task count in the bucket

type TaskBucket = { Kind: TaskBucketKind; Count: int; Members: GroupMember list }
type AgentGroup = { Kind: AgentGroupKind; Count: int; Members: GroupMember list }
```

- Build membership before flattening so each `GroupMember` carries its stable repo identity and
  display name.
- Invariants (assert in tests): both agent-group and task-bucket counts equal
  `Members |> List.sumBy _.Contribution`. For agents, one worktree can appear in several groups and
  contributes the number of its sessions matching that group. Members preserve repo and group order.
- Archived worktrees are removed before any grouping. Task-bucket membership then follows the
  per-bucket predicates exactly (for example, `Underway` counts `Beads.InProgress + Planning.Queued`
  only where `isActive`; `Planned` folds `Loose`; `Unattended` is inactive worktrees' Underway work;
  `Done` and `ToLand` split `Beads.Closed` on whether the worktree still has work left).
  A worktree is a member iff its contribution to that bucket is greater than zero.

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
    sets it back to `None`. Opening an Agents selection while the Agents strip is pinned schedules a
    dashboard scroll to `top = 0` through `Navigation` on the next animation frame so the rendered
    breakdown is visible; normal-row selections do not move the dashboard.
  - `SelectOverviewWorktree of scopedKey: string` — the arrow-nav-parity handler:
    `expandRepoOwning` → `applyFocus true (Some (Card scopedKey))` → `scrollFocusedIntoView Normal`,
    persisting collapsed repos when a repo was expanded. Must **not** open the Canvas pane.
- `ToggleOverviewPanel` clears `SelectedOverviewGroup` when it closes the band.
- `SetOverviewAgentsStuck` records the dashboard's normal-vs-pinned transition. Entering the pinned
  state clears an Agents selection so a detached selected tab cannot remain after its panel scrolls
  away.
- On `DataLoaded`, drop `SelectedOverviewGroup` if the fresh roll-up no longer contains that group.
- `Esc` closes the panel when a group is selected (extend the dashboard key handling / `KeyPressed`).

### 3. View + CSS (`src/Client/OverviewBand.fs`, `src/Client/index.html`)

- `OverviewBand.view` gains the current `OverviewSelection option` and callbacks
  (`onSelectGroup`, `onSelectWorktree`) — wired from `App.fs`'s view (which currently calls
  `OverviewBand.view model.Repos`).
- Each `agentColumn` / `taskColumn` becomes clickable (raises `onSelectGroup`) and gets an
  `overview-item-selected`/tab class when it is the selected group.
- Render one sticky Agents section plus a 1px zero-net-flow sentinel and the normal-flow
  breakdown/Tasks remainder. A dashboard CSS Scroll Timeline morphs the same Agents DOM: heading and
  metadata fade, the existing circle groups translate upward, and the band clips to compact
  Canvas-header height. There is no second compact circle tree to overlap during transition. Circle
  translation is derived from rendered geometry and refreshed by `ResizeObserver`, so platform font
  metrics land on the same center. An `IntersectionObserver` is active only while agent groups
  exist, watches only the sentinel, closes an agent selection after it passes strictly above the
  dashboard boundary, and enables the pinned `nowrap` layout with horizontal overflow.
- Render the breakdown panel below the relevant row when a matching group is selected: the ✕ close
  button (top-right corner, absolutely positioned so it adds no vertical space), repo-grouped members,
  agent chips vs. task bars.
- CSS additions near the existing `.overview-*` rules (`index.html`): the selected black tab
  (rounded top corners), the `.overview-breakdown` panel, `.overview-chips`/chip, the task
  `name + bar` rows, the close button, expanded `.overview-items` wrapping, and pinned-agent
  single-row overflow. Follow
  the existing Catppuccin palette and the CSS-classes-only rule (the proportional bar width is the
  already-documented inline-width exception).

### 4. Tests

- **Unit** (`src/Tests/OverviewDataTests.fs`): membership correctness per bucket/group — the right
  worktrees, correct `Contribution`, `Count` equals the sum of member contributions, respects
  `isActive`/`IsArchived`, and repo names populated.
- **E2E** (`OverviewBandE2ETests`): verifies exactly one Agents band/circle tree exists, heading and
  metadata have intermediate opacity midway through the morph, entering the pinned state closes an
  agent drill-down, final circles share one centered row at Canvas-header height on Windows/Linux,
  a narrow dashboard keeps one horizontally scrollable row with the last group reachable, and
  clicking a circle scrolls back to its drill-down.

## Decisions

- **Worktree click = arrow-nav parity, no Canvas pane.** Distinguishes it from `FocusOverviewCard`.
- **Panel placement: inside the band**, directly under the selected group's section row.
- **Single-select**; re-click / ✕ / Esc closes.
- **Ephemeral** selection (not persisted).
- **Expanded groups wrap** on narrow panes; the compact pinned Agents row stays single-line and
  horizontally scrolls only when required.
- **Agent breakdown** = borderless activity-colored `[● name]` chips; **task breakdown** = `name + bar`
  rows on the band's shared task scale.
- **Sticky strip stays separate from the breakdown.** Only the agent summary remains pinned; the
  detail panel and Tasks content stay in normal flow so they cannot cover the dashboard while
  scrolling.

## Related Specs

- `docs/spec/beads-overview-band.md` — the canonical live Overview band this extends.
