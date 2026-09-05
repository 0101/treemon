/**
 * @param {import("node:http").IncomingMessage} req
 * @param {number} [maxBytes]
 * @returns {Promise<string>}
 */
export function readBody(req, maxBytes = 1024 * 1024) {
  return new Promise((resolve, reject) => {
    let body = "";
    let size = 0;

    req.on("error", reject);
    req.on("data", (chunk) => {
      size += chunk.length;
      if (size > maxBytes) {
        req.destroy();
        reject(new Error("body too large"));
        return;
      }
      body += chunk;
    });
    req.on("end", () => resolve(body));
  });
}
