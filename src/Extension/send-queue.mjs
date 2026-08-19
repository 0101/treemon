// Serialized session.send queue that coalesces exact duplicates.
//
// A payload counts as pending only while it waits in the chain: its key is dropped the instant
// session.send is invoked (before awaiting, so a rejected send leaves nothing stale behind). An
// identical payload that arrives after delivery started is therefore queued and delivered — only
// the copies that would repeat an undelivered message are skipped.
/**
 * @param {{ log?: (message: string) => void }} [options]
 */
export function createSendQueue({ log = () => {} } = {}) {
  const pending = new Set();
  let chain = Promise.resolve();

  /**
   * @param {{ send: (message: { prompt: string }) => unknown }} session
   * @param {string} kind
   * @param {string} prompt
   */
  const enqueue = (session, kind, prompt) => {
    const key = JSON.stringify({ kind, prompt });
    if (pending.has(key)) {
      log(`duplicate ${kind} prompt already queued, skipping (${prompt.length} chars)`);
      return false;
    }
    pending.add(key);
    chain = chain
      .then(() => {
        pending.delete(key);
        return session.send({ prompt });
      })
      .then(() => log(`session.send succeeded (${prompt.length} chars)`))
      .catch((err) => log(`session.send FAILED: ${err?.message ?? err}`));
    return true;
  };

  return { enqueue };
}
