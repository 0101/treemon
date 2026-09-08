# Claude Code status reporting

Treemon shows a live status donut per coding session. The Copilot CLI reports its own status from a
session extension; Claude Code has no extension host to observe a session from the inside, so it
reports through hooks instead. `report.mjs` receives a hook payload on stdin and translates it into
the same `POST /api/session/activity` events the Copilot reporter sends.

## Install

Add the hooks to `~/.claude/settings.json` (all users of this machine) or to a project's
`.claude/settings.json`. Replace `/path/to/treemon` with your checkout.

```json
{
  "hooks": {
    "SessionStart":     [{ "hooks": [{ "type": "command", "command": "node /path/to/treemon/src/ClaudeHooks/report.mjs" }] }],
    "UserPromptSubmit": [{ "hooks": [{ "type": "command", "command": "node /path/to/treemon/src/ClaudeHooks/report.mjs" }] }],
    "PreToolUse":       [{ "matcher": "Task", "hooks": [{ "type": "command", "command": "node /path/to/treemon/src/ClaudeHooks/report.mjs" }] }],
    "PostToolUse":      [{ "matcher": "Task", "hooks": [{ "type": "command", "command": "node /path/to/treemon/src/ClaudeHooks/report.mjs" }] }],
    "Notification":     [{ "hooks": [{ "type": "command", "command": "node /path/to/treemon/src/ClaudeHooks/report.mjs" }] }],
    "SubagentStop":     [{ "hooks": [{ "type": "command", "command": "node /path/to/treemon/src/ClaudeHooks/report.mjs" }] }],
    "Stop":             [{ "hooks": [{ "type": "command", "command": "node /path/to/treemon/src/ClaudeHooks/report.mjs" }] }],
    "SessionEnd":       [{ "hooks": [{ "type": "command", "command": "node /path/to/treemon/src/ClaudeHooks/report.mjs" }] }]
  }
}
```

The `"matcher": "Task"` on the two tool hooks is deliberate. Claude runs a hook **synchronously and
waits for it**, so an unmatched tool hook would add a process launch and an HTTP round trip to every
single tool call — measured at ~160 ms each, which an agent making hundreds of calls pays in full.
Matching only `Task` keeps the delegated-agent tracking, which is the part that cannot be recovered
from other events, and costs nothing the rest of the time.

The trade is that a single tool call running longer than the server's staleness window (~5 minutes)
with no other event can decay the session to Idle until the turn ends. Drop the matchers if you would
rather pay the per-call cost for a status that never goes stale.

Then tell Treemon which tool drives the worktree, so it launches and resumes the right one — add
`.treemon.json` at the worktree root:

```json
{ "codingTool": "claude" }
```

Nothing else is required: the agent's working directory has to be a worktree Treemon is monitoring,
which is how a report finds its card. The endpoint answers `{"monitored":true,"recorded":true}` when
it matched one, so an unexpected `"monitored":false` means the path is not being watched.

## Ports

`TREEMON_PORTS` (comma-separated) reports to several instances at once; `TREEMON_PORT` names one;
otherwise 5000. Same convention as the Copilot reporter.

## What each hook becomes

| Hook | Reported as |
|---|---|
| `SessionStart` | `went_idle` — registers the session as present but not working |
| `UserPromptSubmit` | `user_input_completed`, `user_prompt`, `turn_started` |
| `PreToolUse` (`Task`) | `background_agent_started` |
| `PostToolUse` (`Task`) / `SubagentStop` | `background_agent_finished` |
| `PreToolUse` / `PostToolUse` (other tools) | `heartbeat` — keeps a long tool run from decaying to Idle |
| `Notification` | `awaiting_user_input`, carrying the question |
| `Stop` | `user_input_completed`, `turn_ended` |
| `SessionEnd` | `user_input_completed`, `went_idle` |

Two of those are less obvious than they look.

**`user_input_completed` leads three of the mappings** because an ask_user wait is a *durable* gate
on the server: it holds the session at WaitingForUser until something says the input arrived. Without
it a session stays visibly blocked on a question that was answered long ago.

**The delegated-agent pair matters** because Treemon folds `background_agent_started` /
`background_agent_finished` into per-tool clocks, so a root turn cannot settle Idle while its
sub-agents are still running. Reporting one without the other leaves a card showing work that
finished.

## Behaviour under failure

The script always exits 0 and every post is bounded, so a Treemon that is down, slow or absent can
never fail a hook or stall a session. Reporting errors are swallowed rather than surfaced in the
user's session.
