// The side-effecting half both entry points share: posting to Treemon, and the claim file that keeps
// exactly one heartbeat beater alive per session.
//
// report.mjs runs inside a hook Claude is waiting for; heartbeat.mjs runs detached. They agree on the
// wire and on the claim, so those live here rather than being written twice.

import { open, mkdir, writeFile } from "node:fs/promises";
import { dirname } from "node:path";

/**
 * Fan-out targets. Same convention as the Copilot reporter: TREEMON_PORTS fans out to several
 * instances, TREEMON_PORT names one, and 5000 is production.
 *
 * @param {Record<string, string|undefined>} env
 */
export function activityUrls(env) {
  return (env.TREEMON_PORTS || env.TREEMON_PORT || "5000")
    .split(",")
    .map((port) => port.trim())
    .filter(Boolean)
    .map((port) => `http://127.0.0.1:${port}/api/session/activity`);
}

/**
 * Best-effort by design: an unreachable, slow or absent Treemon must never surface in the session.
 *
 * @param {string[]} urls
 * @param {Record<string, unknown>} body
 * @param {AbortSignal} signal
 */
export async function postReport(urls, body, signal) {
  await Promise.all(
    urls.map((url) =>
      fetch(url, {
        method: "POST",
        headers: { "Content-Type": "application/json" },
        body: JSON.stringify(body),
        signal,
      }).catch(() => {}),
    ),
  );
}

/** @param {string} file */
export async function readClaim(file) {
  try {
    const handle = await open(file, "r");
    try {
      return JSON.parse(await handle.readFile("utf8"));
    } finally {
      await handle.close();
    }
  } catch {
    return null;
  }
}

/**
 * Rewrites a claim that already exists, and fails rather than creating one.
 *
 * That distinction is the whole reason this is not a plain writeFile. Turn end retires the claim and
 * the beater refreshes it, and both would otherwise race to recreate a file the other had just
 * finished with - leaving a beater reporting a session as present long after its turn ended. Only
 * `claimTurnStarted` creates, so neither writer can resurrect what it did not open.
 *
 * @param {string} file
 * @param {Record<string, unknown>} claim
 */
export async function updateClaim(file, claim) {
  const handle = await open(file, "r+");
  try {
    const buffer = Buffer.from(JSON.stringify(claim), "utf8");
    await handle.truncate(buffer.length);
    await handle.write(buffer, 0, buffer.length, 0);
  } finally {
    await handle.close();
  }
}

/**
 * @param {string} file
 * @param {Record<string, unknown>} claim
 */
export async function claimTurnStarted(file, claim) {
  await mkdir(dirname(file), { recursive: true });
  await writeFile(file, JSON.stringify(claim), "utf8");
}
