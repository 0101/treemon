# Treemon Dashboard

## Goals

- At-a-glance visibility into all active worktrees — see what's happening across branches without switching context or running multiple commands
- Monitor multiple git worktree roots from a single instance
- Surface activity signals from multiple sources (git, beads, Claude Code, Azure DevOps, GitHub) in one place so stalled or forgotten branches are obvious
- Lightweight and non-intrusive — polling-based, no hooks or agents that interfere with active work
- Zero configuration per worktree — just point at a root directory and it discovers everything

For detailed requirements, expected behavior, and design decisions see `docs/spec/worktree-monitor.md`.

## Setup

After cloning or creating a new worktree, install npm dependencies (not shared between worktrees):

```
npm install
```

## Quick Reference

```
.\treemon.ps1 start "Q:\code\AITestAgent"                            # single root
.\treemon.ps1 start "Q:\code\AITestAgent" "Q:\code\OtherProject"     # multiple roots
.\treemon.ps1 stop                          # stop production
.\treemon.ps1 restart                       # restart production
.\treemon.ps1 status                        # show PID, port, uptime
.\treemon.ps1 log                           # tail production log
.\treemon.ps1 dev "Q:\code\AITestAgent" "Q:\code\OtherProject"       # dev mode with multiple roots
.\treemon.ps1 deploy                        # build frontend → wwwroot/, restart prod if running
dotnet test src/Tests/Tests.fsproj                        # all tests
dotnet test src/Tests/Tests.fsproj --filter "Category=Fast"   # fast suite (unit + selected E2E, <60s)
dotnet test src/Tests/Tests.fsproj --filter "Category=Unit"   # unit tests only
dotnet test src/Tests/Tests.fsproj --filter "Category=E2E"    # E2E tests (Playwright, fixture data)
dotnet test src/Tests/Tests.fsproj --filter "Category=Smoke"  # smoke tests (real data, port 5002)
```

Open http://localhost:5174 in dev mode (Vite proxies API calls to server on :5001).
Open http://localhost:5000 for production (serves pre-built files from wwwroot/).

## Deployment

Only deploy after all Playwright E2E tests pass (`dotnet test src/Tests/Tests.fsproj`). Never deploy with failing tests.

```
dotnet test src/Tests/Tests.fsproj    # must pass first
.\treemon.ps1 deploy                    # builds frontend, copies to wwwroot/, restarts prod
```

`deploy` rebuilds the frontend (`npm run build`), copies `dist/*` to `wwwroot/`, and restarts the production server if it's running. The server uses `dotnet run` so backend changes are also picked up on restart.

## Stack

- **F# (.NET 9.0)** — both server and client, shared types
- **Saturn/Giraffe** — server framework (port 5000)
- **Fable 4.28.0** — F#-to-JS compiler for client
- **Elmish + Feliz** — MVU architecture with React 18
- **Fable.Remoting** — type-safe RPC, route: `/IWorktreeApi/getWorktrees`
- **Vite 6** — dev server (port 5173), proxies `/IWorktreeApi` to server
- **Playwright + NUnit** — E2E tests

## Project Structure

```
.github/
  workflows/
    ci.yml                 — CI workflow (push to main + PRs, excludes Category=Local)
src/
  Shared/
    Types.fs               — domain types shared between client and server
    EventUtils.fs          — event processing: branch extraction, pinning, deduplication
  Server/
    Log.fs                 — diagnostic logging to logs/server.log
    GitWorktree.fs         — git worktree enumeration, commit data, dirty detection, work metrics
    BeadsStatus.fs         — beads task counts via `bd` CLI
    ClaudeStatus.fs        — Claude Code activity + last assistant message parsing
    PrStatus.fs            — Azure DevOps PR, threads, build status, failure extraction
    GithubPrStatus.fs      — GitHub PR, Actions status via `gh` CLI
    ProcessRunner.fs       — shared process execution utilities
    RefreshScheduler.fs    — background refresh loop, MailboxProcessor state agent
    SyncEngine.fs          — branch sync pipeline orchestration
    WorktreeApi.fs         — API implementation, state reads, event merging
    Program.fs             — Saturn app entry point, CLI arg parsing, scheduler wiring
  Client/
    App.fs                 — entire Elmish app (Model, Msg, update, view)
    index.html             — entry point with inline CSS
    output/                — Fable compilation output (gitignored)
  Tests/
    fixtures/
      azdo/                — Azure DevOps fixture data for tests
      github/              — GitHub fixture data for tests
    ServerFixture.fs       — starts server + Vite for tests
    DashboardTests.fs      — Playwright E2E tests
    EventProcessingTests.fs — unit tests for event processing
    GitParsingTests.fs     — unit tests for git output parsing
    ServerParsingTests.fs  — server parsing unit tests
    GithubParsingTests.fs  — GitHub URL/JSON parsing tests
    GithubFixtureTests.fs  — GitHub fixture-based tests
    AzDoFixtureTests.fs    — Azure DevOps fixture-based tests
    MultiRepoApiTests.fs   — multi-repo API integration tests
    PrFilteringTests.fs    — PR filtering unit tests
    SchedulerTests.fs      — unit tests for scheduler logic
    SmokeTests.fs          — smoke tests against live data
docs/spec/
  worktree-monitor.md     — full specification
  future/
    llm-comment-resolution.md — future spec for LLM-based comment resolution
treemon.ps1                — production lifecycle + dev mode + deploy
vite.config.js            — Vite config with API proxy (ports via env vars)
treemon.slnx               — .NET 9 solution file (.slnx format)
```

## Domain Types (src/Shared/Types.fs)

```fsharp
type CommentSummary =
    | WithResolution of unresolved: int * total: int
    | CountOnly of total: int

type WorktreeStatus =
    { Path: string; Branch: string; LastCommitMessage: string
      LastCommitTime: DateTimeOffset; Beads: BeadsSummary
      Claude: ClaudeCodeStatus; Pr: PrStatus
      MainBehindCount: int; IsDirty: bool; WorkMetrics: WorkMetrics option }

type RepoWorktrees =
    { RepoId: string; RootFolderName: string
      Worktrees: WorktreeStatus list; IsReady: bool }

type DashboardResponse =
    { Repos: RepoWorktrees list; SchedulerEvents: CardEvent list
      LatestByCategory: Map<string, CardEvent>; AppVersion: string }

type IWorktreeApi =
    { getWorktrees: unit -> Async<DashboardResponse>
      openTerminal: string -> Async<unit>
      startSync: string -> Async<Result<unit, string>>
      cancelSync: string -> Async<unit>
      getSyncStatus: unit -> Async<Map<string, CardEvent list>>
      deleteWorktree: string -> Async<Result<unit, string>> }
```

Key DUs: `ClaudeCodeStatus` (Working|WaitingForUser|Done|Idle), `PrStatus` (NoPr|HasPr of PrInfo), `BuildStatus` (Building|Succeeded|Failed|PartiallySucceeded|Canceled), `StepStatus` (Pending|Running|Succeeded|Failed|Cancelled), `CommentSummary` (WithResolution|CountOnly). `PrInfo` includes `Comments: CommentSummary`, `IsMerged`, and `Builds: BuildInfo list`. `BuildInfo` includes optional `Failure: BuildFailure`.

## Architecture

- Background `MailboxProcessor` refresh scheduler runs one task at a time, caps concurrent processes at ~6
- Multi-repo state stored as `Map<string, PerRepoState>`, tasks scoped per repo
- API responses are instant — pure reads from in-memory state, no process spawning
- Client polls every 1s; data updates stream in as each worktree completes
- Refresh intervals: worktree list 60s, git/beads/claude 15s, PRs/fetch 120s
- Client renders responsive card grid (1-4 columns by viewport width)
- Merged PRs get dimmed cards (actionable: delete the worktree)

## External Dependencies

- **git** — worktree enumeration and commit data
- **bd** (beads CLI) — `bd count --by-status --json --db <path>/.beads/beads.db`
- **az** (Azure CLI) — `az repos pr list`, `az devops invoke` (PR threads), `az pipelines runs list`
- **gh** (GitHub CLI) — `gh api` for PRs, comments, Actions workflow runs
- **Claude session files** — reads `~/.claude/projects/<encoded-path>/*.jsonl` mtimes
- **.NET SDK 9.0.205** — pinned in global.json (Fable 4.28.0 has issues with .NET 10)
- **Node.js** — for Vite and npm

## E2E Verification

All features should include Playwright E2E verification tasks. The dashboard is read-only (polls git, az CLI, gh CLI, file mtimes) so tests run safely against live data.

**How to run:**
```
dotnet test src/Tests/Tests.fsproj    # starts server + Vite automatically via ServerFixture
```

- `ServerFixture.fs` starts the server against `Q:\code\AITestAgent` (port 5001) and Vite (port 5174)
- Tests use real AzDo and GitHub data — PRs, threads, builds are all live
- Tests should assert on CSS classes and DOM structure, not specific data values (data changes over time)
- Every feature plan should include a `verify`-labeled beads task with Playwright tests
- Tests needing a live server + CLIs use `Category=Local`; CI runs `Category!=Local`

## Key Implementation Details

- Fable.Remoting routes use `/{TypeName}/{MethodName}` format — Vite proxy matches `/IWorktreeApi`
- Claude path encoding: replace `:`, `\`, `/` with `-` (e.g., `Q:\code\foo` -> `Q--code-foo`)
- PR provider auto-detected from git remote URL — `RemoteInfo` DU routes to AzDo or GitHub fetcher
- Client is a single file (App.fs) with Elmish MVU pattern
- Server logs to `logs/server.log` — truncated on startup
- Solution uses .slnx format (new in .NET 9)
- Branch sync configured via `.treemon.json` in worktree root
- Pinned errors keyed by `(Source, Branch)` — only cleared when same combination succeeds
- CI runs on push to main and PRs, excludes `Category=Local` tests
