---
name: treemon-cli
description: Interacts with the Treemon dashboard via the `tm` CLI. Use when launching coding agents, creating worktrees, or checking worktree status across repos.
---

# Treemon CLI

`tm` is a command-line interface to the Treemon worktree dashboard. Use it to launch agents, create worktrees, and query status. The Treemon server must be running (`treemon.ps1 start`).

## Commands

### Launch a coding agent

```bash
tm launch --path <worktree-path> "your prompt here"
tm launch --path <worktree-path> --fix-pr <pr-url>
tm launch --path <worktree-path> --fix-build <build-url>
tm launch --path <worktree-path> --fix-tests
tm launch --path <worktree-path> --create-pr
```

Exactly one action must be specified per launch.

### Create a worktree

```bash
tm new --repo <repo-root> --branch <branch-name>              # forks from main
tm new --repo <repo-root> --branch <branch-name> --base dev   # forks from dev
```

### List worktrees

```bash
tm worktrees
```

Shows all tracked worktrees with branch, agent status, and PR info.

## Options

All commands accept `--port <number>` to override the server port (default: 5000, env: `TREEMON_PORT`).

## When to use

- You need to spin up a new worktree for parallel work
- You want to launch an agent in another worktree with a specific task
- You need to check the status of worktrees, agents, or PRs across repos
