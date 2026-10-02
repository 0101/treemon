import { randomBytes } from "node:crypto";
import { isValidCanvasFilename } from "./canvas-filename.mjs";

const capabilityPattern = /^[A-Za-z0-9_-]{43}$/;
const routePattern = /^\/canvas\/([A-Za-z0-9_-]{43})\/([^/]+)(?:\/(hash|message))?$/;

const createCapability = () => randomBytes(32).toString("base64url");

/**
 * @typedef {"document" | "hash" | "message"} BrowserCanvasRouteKind
 * @typedef {{
 *   filename: string,
 *   kind: BrowserCanvasRouteKind,
 *   documentPath: string,
 *   hashPath: string,
 *   messagePath: string
 * }} BrowserCanvasRoute
 */

/**
 * Gives each served document an unguessable route capability bound to that filename. Authored
 * scripts can use their own route but cannot select another same-origin document as the source.
 *
 * @param {() => string} [newCapability]
 */
export function createBrowserCanvasRoutes(newCapability = createCapability) {
  /** @type {Map<string, string>} */
  const capabilities = new Map();

  /** @param {string} filename */
  const capabilityFor = (filename) => {
    if (!isValidCanvasFilename(filename)) {
      throw new Error("invalid canvas filename");
    }

    const existing = capabilities.get(filename);
    if (existing) return existing;

    const capability = newCapability();
    if (!capabilityPattern.test(capability)) {
      throw new Error("invalid browser canvas capability");
    }
    capabilities.set(filename, capability);
    return capability;
  };

  /** @param {string} filename */
  const pathsFor = (filename) => {
    const capability = capabilityFor(filename);
    const documentPath = `/canvas/${capability}/${encodeURIComponent(filename)}`;
    return {
      documentPath,
      hashPath: `${documentPath}/hash`,
      messagePath: `${documentPath}/message`,
    };
  };

  /**
   * @param {number} port
   * @param {string} filename
   */
  const documentUrl = (port, filename) =>
    `http://127.0.0.1:${port}${pathsFor(filename).documentPath}`;

  /**
   * @param {string | undefined} url
   * @returns {BrowserCanvasRoute | null}
   */
  const parse = (url) => {
    if (typeof url !== "string") return null;
    const match = routePattern.exec(url);
    if (!match) return null;

    let filename;
    try {
      filename = decodeURIComponent(match[2]);
    } catch {
      return null;
    }

    if (!isValidCanvasFilename(filename) ||
        capabilities.get(filename) !== match[1]) {
      return null;
    }

    const paths = pathsFor(filename);
    const kind =
      match[3] === "hash"
        ? "hash"
        : match[3] === "message"
          ? "message"
          : "document";

    return { filename, kind, ...paths };
  };

  return Object.freeze({ documentUrl, parse });
}
