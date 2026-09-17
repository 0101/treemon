import { readFileSync } from "node:fs";

/** @type {string[]} */
const systemViewFilenames =
  JSON.parse(readFileSync(new URL("./canvas-doc-kinds.json", import.meta.url), "utf8"));

/** @param {string} filename */
export function isSystemViewFilename(filename) {
  return systemViewFilenames.some((name) => name.toLowerCase() === filename.toLowerCase());
}
