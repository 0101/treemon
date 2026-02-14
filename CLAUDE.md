# mait — Git Worktree Monitor Dashboard

## Goals

- At-a-glance visibility into all active worktrees — see what's happening across branches without switching context or running multiple commands
- Surface activity signals from multiple sources (git, beads, Claude Code, Azure DevOps) in one place so stalled or forgotten branches are obvious
- Lightweight and non-intrusive — polling-based, no hooks or agents that interfere with active work
- Zero configuration per worktree — just point at a root directory and it discovers everything

For detailed requirements, expected behavior, and design decisions see `docs/spec/worktree-monitor.md`.

## Quick Reference

```
.\start.ps1 -WorktreeRoot "Q:\code\AITestAgent"   # start both servers (dev)
dotnet run --project src/Server -- "Q:\code\AITestAgent"  # server only (port 5000)
npm start                                          # Fable + Vite dev server (port 5173)
npm run build                                      # production build → dist/
dotnet test src/Tests/Tests.fsproj                  # E2E tests (Playwright, needs both servers)
```

Open http://localhost:5173 in dev mode (Vite proxies API calls to server).

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
src/
  Shared/Types.fs          — domain types shared between client and server
  Server/
    Log.fs                 — diagnostic logging to logs/server.log
    GitWorktree.fs         — git worktree enumeration, commit data
    BeadsStatus.fs         — beads task counts via `bd` CLI
    ClaudeStatus.fs        — Claude Code activity from session file mtimes
    PrStatus.fs            — Azure DevOps PR, threads, build status via `az` CLI
    WorktreeApi.fs         — API implementation, caching, parallel data assembly
    Program.fs             — Saturn app entry point, CLI arg parsing
  Client/
    App.fs                 — entire Elmish app (Model, Msg, update, view)
    index.html             — entry point with inline CSS
    output/                — Fable compilation output (gitignored)
  Tests/
    ServerFixture.fs       — starts server + Vite for tests
    DashboardTests.fs      — Playwright E2E tests
docs/spec/
  worktree-monitor.md     — full specification
start.ps1                 — PowerShell script to start both servers
vite.config.js            — Vite config with API proxy
mait.slnx                 — .NET 9 solution file (.slnx format)
```

## Domain Types (src/Shared/Types.fs)

```fsharp
type WorktreeStatus =
    { Branch: string; Head: string; LastCommitMessage: string
      LastCommitTime: DateTimeOffset; UpstreamBranch: string option
      Beads: BeadsSummary; Claude: ClaudeCodeStatus
      Pr: PrStatus; IsStale: bool }

type IWorktreeApi = { getWorktrees: unit -> Async<WorktreeStatus list> }
```

Key DUs: `ClaudeCodeStatus` (Active|Recent|Idle|Unknown), `PrStatus` (NoPr|HasPr of PrInfo), `BuildStatus` (NoBuild|Building|Succeeded|Failed|PartiallySucceeded|Canceled).

## Architecture

- Client polls `IWorktreeApi.getWorktrees` every 15 seconds
- Server assembles data in parallel from 4 sources: git, beads CLI, Claude session files, AzDo CLI
- Server-side caching: worktree list 60s, git/beads/claude 15s, PRs 120s
- Client renders responsive card grid (1–4 columns by viewport width)
- Stale detection: all of (commit >24h, claude Idle/Unknown, no beads in_progress, no active PR)

## External Dependencies

- **git** — worktree enumeration and commit data
- **bd** (beads CLI) — `bd count --by-status --json --db <path>/.beads/beads.db`
- **az** (Azure CLI) — `az repos pr list`, `az devops invoke` (PR threads), `az pipelines runs list`
- **Claude session files** — reads `~/.claude/projects/<encoded-path>/*.jsonl` mtimes
- **.NET SDK 9.0.205** — pinned in global.json (Fable 4.28.0 has issues with .NET 10)
- **Node.js** — for Vite and npm

## E2E Verification

All features should include Playwright E2E verification tasks. The dashboard is read-only (polls git, az CLI, file mtimes) so tests run safely against live data.

**How to run:**
```
dotnet test src/Tests/Tests.fsproj    # starts server + Vite automatically via ServerFixture
```

- `ServerFixture.fs` starts the server against `Q:\code\AITestAgent` (port 5000) and Vite (port 5173)
- Tests use real AzDo data — PRs, threads, builds are all live
- Tests should assert on CSS classes and DOM structure, not specific data values (data changes over time)
- Every feature plan should include a `verify`-labeled beads task with Playwright tests

## Key Implementation Details

- Fable.Remoting routes use `/{TypeName}/{MethodName}` format — Vite proxy matches `/IWorktreeApi`
- Claude path encoding: replace `:`, `\`, `/` with `-` (e.g., `Q:\code\foo` → `Q--code-foo`)
- PR detection: parse org/project/repo from git remote URL, match upstream branch to PR sourceRefName
- Client is a single file (App.fs) with Elmish MVU pattern
- Server logs diagnostics (process invocations, exit codes, parse failures) to `logs/server.log` — truncated on startup
- Solution uses .slnx format (new in .NET 9)
