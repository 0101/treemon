import test from "node:test";
import assert from "node:assert/strict";
import { createBrowserCanvasRoutes } from "../../Extension/browser-canvas-routes.mjs";

const capability = (character) => character.repeat(43);

test("browser canvas routes bind an unguessable capability to one filename", () => {
  const tokens = [capability("A"), capability("B")];
  const routes = createBrowserCanvasRoutes(() => tokens.shift());

  const reviewUrl = routes.documentUrl(4321, "review.html");
  const diffUrl = routes.documentUrl(4321, "diff.html");
  const review = routes.parse(new URL(reviewUrl).pathname);
  const diff = routes.parse(new URL(diffUrl).pathname);

  assert.equal(routes.documentUrl(4321, "review.html"), reviewUrl);
  assert.equal(review?.filename, "review.html");
  assert.equal(review?.kind, "document");
  assert.equal(routes.parse(review?.hashPath)?.kind, "hash");
  assert.equal(routes.parse(review?.messagePath)?.kind, "message");
  assert.equal(diff?.filename, "diff.html");
  assert.notEqual(review?.documentPath, diff?.documentPath);
});

test("one document capability cannot claim another document as its source", () => {
  const tokens = [capability("A"), capability("B")];
  const routes = createBrowserCanvasRoutes(() => tokens.shift());
  const review = routes.parse(new URL(routes.documentUrl(4321, "review.html")).pathname);
  routes.documentUrl(4321, "other.html");

  const forged = review?.messagePath.replace("review.html", "other.html");

  assert.equal(routes.parse(forged), null);
  assert.equal(routes.parse("/_message/other.html"), null);
  assert.equal(routes.parse("/canvas/not-a-capability/review.html"), null);
});
