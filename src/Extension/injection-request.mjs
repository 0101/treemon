/** @param {string} origin */
export function isLoopbackOrigin(origin) {
  return /^https?:\/\/(127(?:\.\d{1,3}){3}|localhost|\[::1\])(?::\d+)?$/i.test(origin);
}

/** @param {string | string[] | undefined} value */
function singleHeaderValue(value) {
  return typeof value === "string" ? value : undefined;
}

/**
 * @param {import("node:http").IncomingHttpHeaders} headers
 */
export function isTrustedInjectionHeaders(headers) {
  const contentType = (singleHeaderValue(headers["content-type"]) ?? "")
    .split(";")[0]
    .trim()
    .toLowerCase();
  if (contentType !== "application/json") return false;

  const origin = singleHeaderValue(headers.origin);
  if (headers.origin !== undefined && origin === undefined) return false;
  return !origin || isLoopbackOrigin(origin);
}
