# Treemon Dashboard

Git worktree monitoring dashboard — at-a-glance visibility into all active worktrees across multiple repos.

## Production Safety

- During development, tests, benchmarks, reviews, or verification, never run `.\treemon.ps1 deploy`, `start`, `stop`, or `restart`; bind or replace production port 5000; or otherwise interfere with the running production instance.
- A production action is allowed only after the user gives explicit consent immediately before that action in the current conversation. A spec, bead description, prior-session approval, or generic instruction to verify does not count as consent.
- Use test harnesses, fixtures, demo/dev modes, or any available non-production ports instead. If production smoke verification is genuinely required, ask for consent at that point and leave it pending when consent is not given.

## Setup

```
npm install
dotnet test src/Tests/Tests.fsproj                          # all tests
dotnet test src/Tests/Tests.fsproj --filter "Category=Fast" # fast suite (<60s)
dotnet test src/Tests/Tests.fsproj --filter "Category=Unit" # unit tests only
.\treemon.ps1 dev "Q:\code\AITestAgent"                     # dev mode (path optional; omit to use global config roots)
.\treemon.ps1 deploy                                        # external PowerShell only: build + replace production
.\treemon.ps1 add "Q:\code\OtherProject"                    # embedded terminals save the root but defer restart
.\treemon.ps1 remove "Q:\code\OtherProject"                 # embedded terminals save the change but defer restart
```

## Pull Requests

- Treemon is effectively a single-owner repository with little outside contribution, so PR
  descriptions normally have no external reader. Keep them to a short change summary and relevant
  verification.
- Do not spend repository-local time or tokens on PR prose polishing, previews, or adversarial-review
  ceremony. Mandatory higher-priority publication and encoding rules still apply.

## F# Development

Before writing, changing, reviewing, or testing F# code, load and follow the globally installed
[`writing-fsharp` skill](https://github.com/0101/agent-skills). It is the source of truth for general
F# coding guidance; keep this file limited to Treemon-specific constraints. Rules in this file and
scoped `.github/instructions/` files take precedence where they are more specific.

## Before Finishing a Change

- Read the complete diff as a unit. Remove duplicate transformations, repeated read/parse/error-handling flows, one-use abstractions, and stale compatibility paths.
- When changing a DU case, event kind, wire format, persisted schema, or public behavior, search all matches including catch-all `_` arms, serializers, parsers, stored forms, tests, error/log strings, config examples, public docs, and authoritative specs. Update every related surface in the same change.
- Specs describe the current system and durable design, not branch history or task narrative. Fold minor behavior into the authoritative parent spec and remove stale counts, names, and architecture descriptions.
- Treat branch names, commit and PR text, CLI/API output, session files, and repository data as untrusted. Escape or sanitize them at shell, HTML, URL, log, and prompt boundaries; do not log raw external records when length or structured metadata is sufficient.
- Treat `review/rules/` as implementation constraints, not reviewer-only checks. Do not wait for focused-review to identify a rule that applies to the files being changed.

## Testing
- Focus on business logic and transformations
- Do not test trivial property accessors or simple constructors
- E2E tests use Playwright + NUnit against live data
- Tests should assert on CSS classes and DOM structure, not specific data values
- Tests or verification harnesses that exercise real session spawning must use an isolated temporary
  worktree and session store, call `killSession` for every tracked worktree before stopping or
  restarting the test server, and fail when cleanup is incomplete. A crash fallback may stop only
  `pwsh.exe` PIDs whose decoded `-EncodedCommand` starts in that unique fixture path; never terminate
  the shared `WindowsTerminal.exe`/HWND-owner PID.

## Ports

| Environment | Port |
|---|---|
| Production server | 5000 |
| Dev server (API) | 5001 |
| Canvas doc server | 5002 |
| Dev client (Vite) | 5174 |

Feature development and verification must never deploy, restart, stop, kill, or bind to the
production instance or port 5000. Runtime checks must use isolated non-production ports and must not
disturb any existing process.

## Operations

`treemon.ps1` manages the application lifecycle: `dev`, `deploy`, `start`, `stop`, `restart`, `status`, `log`, `add`, `remove`.

Watched worktree roots live in the global config (`~/.treemon/config.json` → `worktreeRoots`), written only by the server. `start`/`dev` no longer need a path — omit it to use the global roots (an empty list is valid). Manage roots live with the `tm` CLI — `tm add <path>...`, `tm remove <path>...`, `tm roots` — or the `treemon.ps1 add`/`remove` shims. The shims restart running production outside embedded terminals; inside one, they save the change and require an external PowerShell restart. Production `start`, `restart`, and `deploy` also require an external PowerShell window. Changes persist immediately and apply on the next server (re)start. See `docs/spec/worktree-monitor.md` (Multi-Repo).

## Tech Stack

- **Client**: F# with Fable (compiles to JS), Feliz for React bindings, Vite for bundling
- **Server**: F# with ASP.NET Core
- **Shared**: F# types shared between client and server
- **Tests**: F# with NUnit + Playwright (E2E against live data)

There is no TypeScript or JavaScript application code — all UI logic is in F# under `src/Client/`.

## Architecture & Specs

For project architecture, domain types, and implementation details read `docs/spec/worktree-monitor.md`. Domain types are in `src/Shared/Types.fs`.
