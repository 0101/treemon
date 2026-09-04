import { timingSafeEqual } from "node:crypto";
import { isTrustedInjectionHeaders } from "./injection-request.mjs";

const MAX_SHUTDOWN_BODY_BYTES = 4096;

/** @param {string | undefined} remoteAddress */
export function isLoopbackRemoteAddress(remoteAddress) {
  if (typeof remoteAddress !== "string" || !remoteAddress) return false;
  if (remoteAddress === "::1") return true;

  const ipv4 =
    remoteAddress.startsWith("::ffff:")
      ? remoteAddress.slice("::ffff:".length)
      : remoteAddress;
  const parts = ipv4.split(".");

  return (
    parts.length === 4
    && parts.every((part) => /^\d{1,3}$/.test(part) && Number(part) <= 255)
    && Number(parts[0]) === 127
  );
}

/** @param {string} expected @param {unknown} actual */
function capabilityMatches(expected, actual) {
  if (typeof actual !== "string") return false;
  const expectedBytes = Buffer.from(expected, "utf8");
  const actualBytes = Buffer.from(actual, "utf8");
  return (
    expectedBytes.length === actualBytes.length
    && timingSafeEqual(expectedBytes, actualBytes)
  );
}

/** @param {import("node:http").IncomingMessage} req */
function readShutdownBody(req) {
  return new Promise((resolve, reject) => {
    let body = "";
    let size = 0;

    req.on("data", (chunk) => {
      size += chunk.length;
      if (size > MAX_SHUTDOWN_BODY_BYTES) {
        req.destroy();
        reject(new Error("body too large"));
        return;
      }
      body += chunk;
    });
    req.on("end", () => resolve(body));
    req.on("error", reject);
  });
}

/**
 * @param {import("node:http").ServerResponse} res
 * @param {number} status
 * @param {boolean} accepted
 * @param {(() => void) | undefined} [onFinished]
 */
function respond(res, status, accepted, onFinished) {
  res.writeHead(status, { "Content-Type": "application/json" });
  res.end(JSON.stringify({ accepted }), onFinished);
}

/**
 * @param {{
 *   capability: string,
 *   shutdown?: (request: { type: "routine" }) => Promise<unknown>,
 *   schedule?: (callback: () => void) => void,
 *   log?: (message: string) => void,
 * }} options
 */
export function createShutdownHandler(options) {
  const schedule = options.schedule ?? queueMicrotask;
  const log = options.log ?? (() => {});

  /**
   * @param {import("node:http").IncomingMessage} req
   * @param {import("node:http").ServerResponse} res
   */
  return async function handleShutdown(req, res) {
    if (req.url !== "/shutdown") return false;

    if (req.method !== "POST") {
      respond(res, 405, false);
      return true;
    }
    if (!isLoopbackRemoteAddress(req.socket.remoteAddress)) {
      respond(res, 403, false);
      return true;
    }
    if (!isTrustedInjectionHeaders(req.headers)) {
      respond(res, 400, false);
      return true;
    }

    let body;
    try {
      body = JSON.parse(await readShutdownBody(req));
    } catch {
      respond(res, 400, false);
      return true;
    }

    if (!capabilityMatches(options.capability, body?.capability)) {
      respond(res, 401, false);
      return true;
    }
    const shutdown = options.shutdown;
    if (typeof shutdown !== "function") {
      respond(res, 409, false);
      return true;
    }

    // Wait for Node to finish the acknowledgement response before the SDK call can close the
    // session and this endpoint.
    respond(res, 202, true, () => {
      schedule(() => {
        Promise.resolve(shutdown({ type: "routine" }))
          .catch(() => log("routine shutdown invocation failed"));
      });
    });
    return true;
  };
}
