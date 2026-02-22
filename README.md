<p align="center">
  <img src="src/Client/public/icon-512.png" width="128" />
</p>

<h1 align="center">Treemon</h1>

<p align="center">
  A dashboard for monitoring git worktrees across branches — git status, Claude Code activity, beads tasks, and Azure DevOps PRs in one place.
</p>

---

Point Treemon at a directory that contains git worktrees and it discovers all active branches, polling them in the background. Each worktree gets a card showing:

- **Last commit** and relative time
- **Dirty / behind-main** indicators with one-click branch sync
- **Claude Code** session status (Working / Waiting / Done / Idle)
- **Beads** task counts with progress bar
- **Azure DevOps** PR status, thread resolution, and build results
- **Work metrics** — commit grid and diff stats

No hooks, no agents running inside your worktrees — just a lightweight polling loop that reads git, CLI tools, and file mtimes.

## Getting started

Prerequisites: [.NET SDK 9](https://dotnet.microsoft.com/download), [Node.js](https://nodejs.org), git. Optional: `az` CLI (for PR/build data), `bd` CLI (for beads counts).

```
npm install
```

### Production

```powershell
.\treemon.ps1 start "C:\code\my-project"   # start on port 5000
.\treemon.ps1 stop                          # stop
.\treemon.ps1 status                        # show PID, port, uptime
.\treemon.ps1 log                           # tail server log
```

Open http://localhost:5000 — install as a PWA from the browser for a native app experience.

### Development

```powershell
.\treemon.ps1 dev "C:\code\my-project"      # server :5001 + Vite :5174
```

Open http://localhost:5174 (Vite proxies API calls to the server).

### Deploy

```powershell
.\treemon.ps1 deploy                         # build frontend → wwwroot/, restart prod
```

## Stack

F# on both sides — [Saturn](https://saturnframework.org) server, [Fable](https://fable.io) + [Elmish](https://elmish.github.io) client, [Fable.Remoting](https://github.com/Zaid-Ajaj/Fable.Remoting) for type-safe RPC, [Vite](https://vitejs.dev) for dev tooling.
