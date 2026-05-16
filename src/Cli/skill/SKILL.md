---
name: treemon-cli
description: Interacts with the Treemon dashboard via the `tm` CLI. Use when launching coding agents, creating worktrees, or checking worktree status across repos.
---

# Treemon CLI

`tm` is a command-line interface to the Treemon worktree dashboard. Use it to launch agents, create worktrees, and query status. The Treemon server must be running (`treemon.ps1 start`).

## Default workflow when this skill is explicitly invoked

If the user explicitly invokes this skill (e.g. "use the treemon-cli skill" or similar), they are telling you that the work should happen **in a new worktree, performed by a coding agent launched there** — not in the current working directory.

When the user follows the invocation with a task description (rather than an explicit `tm` command), do the following without asking for further confirmation:

1. **Pick a short, descriptive kebab-case branch name** based on the task (e.g. `ignore-by-folder`, `fix-pr-comments`, `add-export-button`). If the task clearly maps to an existing branch naming convention in the repo, follow it.
2. **Write a prompt file** capturing the full task in `.agents/<branch-name>.md` (create `.agents/` if missing — it should already be gitignored). Include:
   - The user's request, verbatim or lightly tidied.
   - Any context you already have that the agent will need (relevant files, prior decisions from this session, gotchas).
   - Acceptance criteria if implied by the request (e.g. "build passes, fast tests pass").
3. **Create the worktree:** `tm new --repo <repo-root> --branch <branch-name>`. The repo root is the current repo unless the user named a different one.
4. **Launch a coding agent** in the new worktree with the prompt file:
   `tm launch --path <worktree-path> --prompt-file <absolute-path-to-prompt.md>`
   The worktree path follows the repo's convention — use `tm worktrees` to confirm if unsure.
5. **Report back** to the user with: the branch name, worktree path, and that the agent was launched. Do not start doing the task yourself in the current directory.

Only deviate from this flow if the user explicitly asks for something else (e.g. "just create the worktree, don't launch an agent", or "do it here instead").

### When the user did NOT invoke this skill explicitly

The skill may have been auto-loaded because the user mentioned `tm` or worktrees. In that case, do not assume they want a new worktree — just use the commands below as a reference for whatever they actually asked for.

## Commands

### Launch a coding agent

```bash
tm launch --path <worktree-path> --prompt-file <file.md>
tm launch --path <worktree-path> --fix-pr <pr-url>
tm launch --path <worktree-path> --fix-build <build-url>
tm launch --path <worktree-path> --fix-tests
tm launch --path <worktree-path> --create-pr
```

Exactly one action must be specified per launch. The `--prompt-file` option accepts a path to a markdown file containing instructions. The file is copied into the worktree at `.agents/prompt.md` and the agent is directed to read it.

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

Shows all tracked worktrees with branch, agent status, and PR info. Use this to look up the path of a worktree you just created.

## Options

All commands accept `--port <number>` to override the server port (default: 5000, env: `TREEMON_PORT`).

## When to use

- The user explicitly invoked this skill — default to creating a worktree and launching an agent there (see "Default workflow" above).
- You need to spin up a new worktree for parallel work.
- You want to launch an agent in another worktree with a specific task.
- You need to check the status of worktrees, agents, or PRs across repos.
