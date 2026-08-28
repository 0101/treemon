import test from "node:test";
import assert from "node:assert/strict";
import { isValidCanvasFilename } from "../../Extension/canvas-filename.mjs";

test("accepts contract canvas filenames", () => {
  [
    "review.html",
    "Review-1_2.v3.html",
    "0.html",
    "a..b.html",
  ].forEach((filename) => assert.equal(isValidCanvasFilename(filename), true, filename));
});

test("rejects filenames outside the canvas contract", () => {
  [
    "",
    ".review.html",
    "review.htm",
    "review.HTML",
    "review page.html",
    "review\"quote.html",
    "review'quote.html",
    "review.html\n",
    "review\t.html",
    "review\u0001.html",
    "../review.html",
    "..\\review.html",
    "nested/review.html",
    "nested\\review.html",
  ].forEach((filename) => assert.equal(isValidCanvasFilename(filename), false, JSON.stringify(filename)));

  [null, undefined, 42, {}]
    .forEach((value) => assert.equal(isValidCanvasFilename(value), false, String(value)));
});
