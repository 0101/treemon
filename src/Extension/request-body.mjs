/**
 * @param {import("node:http").IncomingMessage} req
 * @param {number} [maxBytes]
 * @returns {Promise<string>}
 */
export function readBody(req, maxBytes = 1024 * 1024) {
  return new Promise((resolve, reject) => {
    /** @type {Buffer[]} */
    const chunks = [];
    let size = 0;

    req.on("error", reject);
    req.on("data", (chunk) => {
      const buffer = Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk);
      size += buffer.length;
      if (size > maxBytes) {
        req.destroy();
        reject(new Error("body too large"));
        return;
      }
      chunks.push(buffer);
    });
    req.on("end", () => resolve(Buffer.concat(chunks, size).toString("utf8")));
  });
}
