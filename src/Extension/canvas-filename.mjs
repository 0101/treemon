import { readFileSync } from "node:fs";

const contract = JSON.parse(
  readFileSync(new URL("./canvas-filename-contract.json", import.meta.url), "utf8"),
);

if (typeof contract?.pattern !== "string" || contract.pattern.length === 0) {
  throw new Error("canvas-filename-contract.json must define a non-empty pattern");
}

export const CANVAS_FILENAME_PATTERN = contract.pattern;

const canvasFilenameRegex = new RegExp(CANVAS_FILENAME_PATTERN);

export function isValidCanvasFilename(filename) {
  if (typeof filename !== "string") return false;
  const match = canvasFilenameRegex.exec(filename);
  return match !== null && match.index === 0 && match[0].length === filename.length;
}
