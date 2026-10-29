/**
 * @param {typeof import("@github/copilot-sdk/extension").joinSession} joinSession
 * @param {import("@github/copilot-sdk/extension").JoinSessionConfig} config
 * @param {(message: string) => void} log
 */
export async function joinCanvasSession(joinSession, config, log) {
  try {
    return await joinSession(config);
  } catch (error) {
    log(`joinSession with tools/hooks failed (${error?.message ?? error}); retrying without readability hooks`);
  }

  try {
    return await joinSession({ tools: config.tools });
  } catch (error) {
    log(`joinSession with tools failed (${error?.message ?? error}); retrying without tools`);
    return joinSession();
  }
}
