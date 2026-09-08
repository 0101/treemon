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
    "PreToolUse":       [{ "matcher": "Task|Skill|AskUserQuestion|ExitPlanMode", "hooks": [{ "type": "command", "command": "node /path/to/treemon/src/ClaudeHooks/report.mjs" }] }],
    "PostToolUse":      [{ "matcher": "Task|AskUserQuestion|ExitPlanMode", "hooks": [{ "type": "command", "command": "node /path/to/treemon/src/ClaudeHooks/report.mjs" }] }],
    "PostToolUseFailure": [{ "matcher": "Task|AskUserQuestion|ExitPlanMode", "hooks": [{ "type": "command", "command": "node /path/to/treemon/src/ClaudeHooks/report.mjs" }] }],
    "PermissionDenied": [{ "matcher": "Task|AskUserQuestion|ExitPlanMode", "hooks": [{ "type": "command", "command": "node /path/to/treemon/src/ClaudeHooks/report.mjs" }] }],
    "Notification":     [{ "hooks": [{ "type": "command", "command": "node /path/to/treemon/src/ClaudeHooks/report.mjs" }] }],
    "SubagentStop":     [{ "hooks": [{ "type": "command", "command": "node /path/to/treemon/src/ClaudeHooks/report.mjs" }] }],
    "Stop":             [{ "hooks": [{ "type": "command", "command": "node /path/to/treemon/src/ClaudeHooks/report.mjs" }] }],
    "SessionEnd":       [{ "hooks": [{ "type": "command", "command": "node /path/to/treemon/src/ClaudeHooks/report.mjs" }] }]
  }
}
```

The matchers on the tool hooks are deliberate. Claude runs a hook **synchronously and waits for it**,
so an unmatched tool hook would add a process launch and an HTTP round trip to every single tool call
— measured at ~160 ms each, which an agent making hundreds of calls pays in full. The listed tools
are the ones whose status cannot be recovered from any other event: `Task` opens and closes a
delegated agent, `Skill` names what the session is running, and `AskUserQuestion` / `ExitPlanMode`
are waits on the human that raise no notification of their own. Everything else costs nothing.

A turn can therefore run for minutes inside one unmatched tool call without emitting anything, which
is what the heartbeat below exists to cover.

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
| `SessionStart` | `went_idle`, `title_bootstrap` — registers the session as present but not working, and restores the title a resumed session already had. On `source: "compact"` a `heartbeat` instead, because compaction happens mid-turn |
| `UserPromptSubmit` | `user_input_completed`, `user_prompt`, `turn_started` |
| `PreToolUse` (`Task`) | `background_agent_started` |
| `PreToolUse` (`Skill`) | `skill_invoked`, carrying the skill's name |
| `PreToolUse` (`AskUserQuestion` / `ExitPlanMode`) | `awaiting_user_input`, carrying the question where there is one |
| `PostToolUse` / `PostToolUseFailure` / `PermissionDenied` (`Task`), `SubagentStop` | `background_agent_finished` |
| `PostToolUse` / `PostToolUseFailure` / `PermissionDenied` (`AskUserQuestion` / `ExitPlanMode`) | `user_input_completed` |
| `Notification` | `awaiting_user_input`, carrying the question |
| `Stop` | `user_input_completed`, `assistant_message`, `title_reported`, `turn_ended` |
| `SessionEnd` | `user_input_completed`, `went_idle` |
| anything else | `heartbeat` |

Two of those are less obvious than they look.

**`user_input_completed` leads three of the mappings** because an ask_user wait is a *durable* gate
on the server: it holds the session at WaitingForUser until something says the input arrived. Without
it a session stays visibly blocked on a question that was answered long ago.

**The title and the last reply come from the transcript, not the payload.** No hook carries either,
but Claude keeps both in the session's JSONL: it restamps its own generated session title as an
`ai-title` entry, and its replies are `assistant` entries. `transcript_path` is on every payload, so
the two hooks that need them read the tail of that file — the last 256 KB, because a transcript runs
to megabytes and this is on the critical path of a hook Claude is waiting for. A sidechain entry is a
delegated agent talking rather than the session, and is skipped. A session too young to have a title
simply reports none: the event is dropped rather than sent blank, which the server would reject.

**The delegated-agent pair matters** because Treemon folds `background_agent_started` /
`background_agent_finished` into per-tool clocks, so a root turn cannot settle Idle while its
sub-agents are still running. Reporting one without the other leaves a card showing work that
finished — which is why the failure and permission-denied hooks are registered too: a `Task` that
errors or is refused never reaches `PostToolUse`, and its clock would stay open for the rest of the
session. An event whose id is missing is dropped rather than sent unpaired, for the same reason.

## Heartbeat

Hooks fire only when Claude does something. A turn that spends twenty minutes inside a single tool
call emits nothing in between, and the server demotes any non-Idle session whose `last_seen` is older
than its five-minute staleness timeout — so the card would report a working agent as idle. The Copilot
extension covers this with an in-process timer; a hook has no process to keep, so `report.mjs`
detaches `heartbeat.mjs` at the start of a turn and retires it at the end.

The two coordinate through a claim file under `~/.treemon/claude-heartbeat` (or `TREEMON_CONFIG_DIR`),
named by a hash of the session id — Treemon's own directory rather than the shared system temp, where
another user could pre-create the path. Turn start writes a claim naming a new owner; the beater posts a `heartbeat` every 60 s
and refreshes the claim's timestamp; turn end clears the claim's owner, which stops the beater at its
next beat.

Turn end **clears the owner rather than deleting the file**, and only turn start ever creates one.
That is what keeps the two writers from racing: a delete could be undone by a beater that was already
mid-refresh, leaving it reporting a session as present long after its turn ended. A four-hour cap
bounds a beater that never hears about the end at all, which is what happens when Claude is killed
outright; the next `SessionStart` collects it in any case.

Nothing about this can fail a hook. A beater that cannot be spawned, or a claim that cannot be
written, leaves exactly the behaviour that existed before it.

## Tests

`npm run test:extension` covers the mapping (`src/Tests/Extension/claude-hooks/`). The script itself
swallows every error by design, so a mapping regression cannot fail a hook — which is exactly why the
mapping is a separate, tested module rather than living inside `report.mjs`.

## Behaviour under failure

The script always exits 0 and every post is bounded, so a Treemon that is down, slow or absent can
never fail a hook or stall a session. Reporting errors are swallowed rather than surfaced in the
user's session.
