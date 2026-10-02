// Serialized session.send queue that coalesces exact duplicates.
//
// A payload counts as pending only while it waits in the chain: its key is dropped the instant
// session.send is invoked (before awaiting, so a rejected send leaves nothing stale behind). An
// identical payload that arrives after delivery started is therefore queued and delivered — only
// the copies that would repeat an undelivered message are skipped.
/**
 * @param {{ log?: (message: string) => void }} [options]
 * @returns {{
 *   enqueue: (session: { send: (message: { prompt: string }) => unknown }, kind: string, prompt: string) => boolean,
 *   enqueueAndWait: (session: { send: (message: { prompt: string }) => unknown }, kind: string, prompt: string) => Promise<void>
 * }}
 */
export function createSendQueue({ log = () => {} } = {}) {
  /** @type {Map<string, Promise<void>>} */
  const pending = new Map();
  let chain = Promise.resolve();

  /**
   * @param {{ send: (message: { prompt: string }) => unknown }} session
   * @param {string} kind
   * @param {string} prompt
   */
  const schedule = (session, kind, prompt) => {
    const key = JSON.stringify({ kind, prompt });
    const existing = pending.get(key);
    if (existing) {
      log(`duplicate ${kind} prompt already queued, skipping (${prompt.length} chars)`);
      return { scheduled: false, completion: existing };
    }
    const completion = chain
      .then(() => {
        pending.delete(key);
        return session.send({ prompt });
      })
      .then(() => log(`session.send succeeded (${prompt.length} chars)`));
    pending.set(key, completion);
    chain = completion.catch(() => log(`session.send FAILED (${kind}, ${prompt.length} chars)`));
    return { scheduled: true, completion };
  };

  return {
    enqueue: (session, kind, prompt) => schedule(session, kind, prompt).scheduled,
    enqueueAndWait: (session, kind, prompt) => schedule(session, kind, prompt).completion,
  };
}
