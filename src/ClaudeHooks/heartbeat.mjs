#!/usr/bin/env node
// Keeps a Claude session visibly alive through a turn that emits no hooks.
//
// Claude fires hooks when it does something. A turn that spends twenty minutes inside a single tool
// call does nothing observable in between, and the server demotes any non-Idle session whose
// last_seen is older than its five-minute staleness timeout - so the card would report a working
// agent as idle. The Copilot extension solves this with an in-process timer; a hook has no process to
// keep, so report.mjs detaches this one at the start of a turn and retires it at the end.
//
// Run as: node heartbeat.mjs <owner> <claim file>. Nothing else starts it, and it exits on its own if
// the claim is retired, taken over, or missing.

import { setTimeout as delay } from "node:timers/promises";

import {
  HEARTBEAT_INTERVAL_MS,
  HEARTBEAT_MAX_MS,
  toReport,
} from "./claude-hooks-core.mjs";
import { activityUrls, postReport, readClaim, updateClaim } from "./hook-io.mjs";

const [owner, claimFile] = process.argv.slice(2);

// Detached, so a slow server delays nothing a human is waiting on - but a beat must still not outlive
// the interval that follows it.
const POST_TIMEOUT_MS = 5000;

const urls = activityUrls(process.env);

/** One beat. Returns false when this beater should stop. */
const beat = async () => {
  const claim = await readClaim(claimFile);

  // A different owner means a newer turn took over; a cleared one means the turn ended. Either way
  // this beater is finished, and must not write - the claim is no longer describing it.
  if (claim?.owner !== owner) return false;

  const occurredAt = new Date().toISOString();
  const report = toReport(
    {
      sessionId: claim.sessionId,
      worktreePath: claim.worktreePath,
      provider: claim.provider,
      eventId: `${claim.sessionId}:${occurredAt}:heartbeat:${owner}`,
      occurredAt,
      ...(claim.terminalSessionId ? { terminalSessionId: claim.terminalSessionId } : {}),
    },
    { kind: "heartbeat" },
  );

  await postReport(urls, report, AbortSignal.timeout(POST_TIMEOUT_MS));

  try {
    await updateClaim(claimFile, { ...claim, beatAt: occurredAt });
  } catch {
    // The claim went away underneath us, which is the retirement signal arriving a moment early.
    return false;
  }

  return true;
};

if (owner && claimFile) {
  const deadline = Date.now() + HEARTBEAT_MAX_MS;

  // Sleep first: the hook that spawned this has just posted the events opening the turn, so there is
  // nothing to say yet.
  while (Date.now() < deadline) {
    await delay(HEARTBEAT_INTERVAL_MS);

    try {
      if (!(await beat())) break;
    } catch {
      break;
    }
  }
}

process.exit(0);
