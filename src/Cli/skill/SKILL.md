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

#### Prompts — ALWAYS use a file

**NEVER pass multi-line prompts as CLI arguments** — they get truncated to the first line. Instead, write the prompt to a file inside the worktree and tell the agent to read it:

```powershell
# 1. Write the full prompt to a file INSIDE the worktree
$promptPath = Join-Path "<worktree-path>" ".agents\prompt.md"
New-Item -ItemType Directory -Path (Split-Path $promptPath) -Force | Out-Null
@"
Your detailed multi-line prompt here.

## Context
- File locations, root cause analysis, etc.

## Required Changes
1. First change...
2. Second change...
"@ | Set-Content $promptPath -Encoding UTF8

# 2. Launch with a single-line instruction to read the file
tm launch --path <worktree-path> "Read .agents/prompt.md and follow the instructions there."
```

This works because:
- The file lives inside the worktree, so the agent can always find it
- The single-line prompt is just a pointer — all detail goes in the file
- No truncation, no encoding issues, no cleanup needed

### Pre-launch checklist

Before launching an agent in a worktree, ensure any context files it needs are present. Files in `.agents/` or other gitignored directories **do not exist in new worktrees**. Copy them explicitly:

```powershell
# Copy investigation/context docs to the worktree before launching
New-Item -ItemType Directory -Path "<worktree>\.agents" -Force | Out-Null
Copy-Item ".\path\to\context.md" "<worktree>\.agents\"
```

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
