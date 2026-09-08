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

import { spawn } from "node:child_process";
import { open } from "node:fs/promises";
import { randomUUID } from "node:crypto";
import { fileURLToPath } from "node:url";
import { dirname, join } from "node:path";

import {
  claimIsLive,
  claimPath,
  toReport,
  transcriptFacts,
  treemonEvents,
} from "./claude-hooks-core.mjs";
import { activityUrls, claimTurnStarted, postReport, readClaim, updateClaim } from "./hook-io.mjs";

// Claude runs this hook synchronously and waits for it, so this is latency added to the user's
// session. The budget is shared across every post one invocation makes rather than applied per
// request: a per-request timeout multiplies by the number of events, and a server that accepts a
// connection and then never answers would hold up a prompt for the sum of them.
const HOOK_BUDGET_MS = 1500;

// How much of the transcript's tail to read. Claude restamps its title and appends its replies, so
// what we want is always at the end - and a transcript runs to megabytes, which is not something to
// read whole on the critical path of a hook.
const TRANSCRIPT_TAIL_BYTES = 256 * 1024;

// The only hooks that need it. Everywhere else the read would be pure cost.
const TRANSCRIPT_HOOKS = new Set(["SessionStart", "Stop"]);

const heartbeatScript = join(dirname(fileURLToPath(import.meta.url)), "heartbeat.mjs");

const urls = activityUrls(process.env);

const readStdin = async () => {
  const chunks = [];
  for await (const chunk of process.stdin) chunks.push(chunk);
  return Buffer.concat(chunks).toString("utf8");
};

/** @param {string} path */
const readTranscriptTail = async (path) => {
  try {
    const handle = await open(path, "r");
    try {
      const { size } = await handle.stat();
      const start = Math.max(0, size - TRANSCRIPT_TAIL_BYTES);
      const buffer = Buffer.alloc(size - start);
      if (!buffer.length) return "";
      await handle.read(buffer, 0, buffer.length, start);
      return buffer.toString("utf8");
    } finally {
      await handle.close();
    }
  } catch {
    return "";
  }
};

/**
 * Detaches a beater for this turn, unless one is already running for the session. Failures are
 * swallowed: no heartbeat is the behaviour this whole mechanism improves on, not a reason to disturb
 * a session.
 *
 * @param {Record<string, unknown>} context
 */
const startHeartbeat = async (context) => {
  try {
    const file = claimPath(String(context.sessionId));
    if (claimIsLive(await readClaim(file), Date.now())) return;

    const owner = randomUUID();
    await claimTurnStarted(file, { ...context, owner, beatAt: new Date().toISOString() });

    spawn(process.execPath, [heartbeatScript, owner, file], {
      detached: true,
      stdio: "ignore",
    }).unref();
  } catch {
    /* no heartbeat is a degradation, never a failure */
  }
};

/**
 * Retires the beater by clearing the claim's owner. Deliberately not a delete: the beater rewrites
 * the claim in place and would recreate a deleted one, whereas an owner it does not recognise stops
 * it at its next beat.
 *
 * @param {string} sessionId
 */
const stopHeartbeat = async (sessionId) => {
  try {
    const file = claimPath(sessionId);
    const claim = await readClaim(file);
    if (claim?.owner) await updateClaim(file, { ...claim, owner: null });
  } catch {
    /* the beater's own backstop bounds it if this never lands */
  }
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

    const facts = TRANSCRIPT_HOOKS.has(hook.hook_event_name)
      ? transcriptFacts(await readTranscriptTail(hook.transcript_path))
      : {};

    const identity = {
      sessionId,
      worktreePath,
      provider: "claude_code",
      ...(process.env.TREEMON_TERMINAL_SESSION_ID
        ? { terminalSessionId: process.env.TREEMON_TERMINAL_SESSION_ID }
        : {}),
    };

    for (const event of treemonEvents(hook, facts)) {
      const context = {
        ...identity,
        // Treemon de-duplicates by event id, so each event needs its own.
        eventId: `${sessionId}:${occurredAt}:${event.kind}:${Math.random().toString(36).slice(2)}`,
        occurredAt,
      };

      const report = toReport(context, event);

      if (report) await postReport(urls, report, budget);
    }

    // A turn is the beater's whole lifetime. A session that is starting is not mid-turn, so that
    // retires one too - it is where a beater left over from a Claude killed outright gets collected.
    // Compaction is the exception, being a SessionStart raised in the middle of a turn.
    const retires =
      hook.hook_event_name === "Stop" ||
      hook.hook_event_name === "SessionEnd" ||
      (hook.hook_event_name === "SessionStart" && hook.source !== "compact");

    if (hook.hook_event_name === "UserPromptSubmit") await startHeartbeat(identity);
    else if (retires) await stopHeartbeat(sessionId);
  }
} catch {
  // Deliberately silent: a reporting failure must never surface inside the user's session.
}

process.exit(0);
