#!/usr/bin/env node
// Reports Claude Code session activity to Treemon.
//
// Claude Code has no extension host to observe a session from the inside, the way the Copilot CLI
// reporter does, so status comes from hooks instead: Claude invokes this script with the hook payload
// on stdin, and it posts the same /api/session/activity events the Copilot reporter sends. The
// mapping itself lives in claude-hooks-core.mjs, where it can be tested.
//
// Install: see README.md in this directory.
//
// Two rules this script must never break, because Claude waits for it:
//   - it always exits 0, so a Treemon that is down or slow can never fail a hook;
//   - the whole invocation is bounded, so an unresponsive server cannot stall the session.

import { treemonEvents, toReport } from "./claude-hooks-core.mjs";

// Claude runs this hook synchronously and waits for it, so this is latency added to the user's
// session. The budget is shared across every post one invocation makes rather than applied per
// request: a per-request timeout multiplies by the number of events, and a server that accepts a
// connection and then never answers would hold up a prompt for the sum of them.
const HOOK_BUDGET_MS = 1500;

// Same convention as the Copilot reporter: TREEMON_PORTS fans out to several instances, TREEMON_PORT
// names one, and 5000 is production.
const ports = (process.env.TREEMON_PORTS || process.env.TREEMON_PORT || "5000")
  .split(",")
  .map((p) => p.trim())
  .filter(Boolean);

const activityUrls = ports.map((p) => `http://127.0.0.1:${p}/api/session/activity`);

const readStdin = async () => {
  const chunks = [];
  for await (const chunk of process.stdin) chunks.push(chunk);
  return Buffer.concat(chunks).toString("utf8");
};

const post = async (body, signal) => {
  await Promise.all(
    activityUrls.map((url) =>
      fetch(url, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body),
        signal,
      }).catch(() => {}),
    ),
  );
};

try {
  const raw = await readStdin();
  const hook = raw ? JSON.parse(raw) : {};

  const sessionId = hook.session_id;
  const worktreePath = hook.cwd;

  // Without both, Treemon has nothing to attach the status to.
  if (sessionId && worktreePath) {
    const occurredAt = new Date().toISOString();
    // One deadline for the whole invocation. Events are posted in order because the server's state
    // machine reads them that way - clearing an ask_user wait before the turn it belongs to, for
    // instance - so they cannot simply be fired in parallel to save time.
    const budget = AbortSignal.timeout(HOOK_BUDGET_MS);

    for (const event of treemonEvents(hook)) {
      const context = {
        sessionId,
        worktreePath,
        provider: "claude_code",
        // Treemon de-duplicates by event id, so each event needs its own.
        eventId: `${sessionId}:${occurredAt}:${event.kind}:${Math.random().toString(36).slice(2)}`,
        occurredAt,
        ...(process.env.TREEMON_TERMINAL_SESSION_ID
          ? { terminalSessionId: process.env.TREEMON_TERMINAL_SESSION_ID }
          : {}),
      };

      const report = toReport(context, event);

      if (report) await post(report, budget);
    }
  }
} catch {
  // Deliberately silent: a reporting failure must never surface inside the user's session.
}

process.exit(0);
