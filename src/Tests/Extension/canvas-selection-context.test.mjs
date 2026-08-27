import test from "node:test";
import assert from "node:assert/strict";

import "../../Extension/canvas-selection-context.js";

const selection = globalThis.canvasSelectionContextInternals;
const { validateSelectionMetadata } = selection;

test("selection metadata accepts and isolates plain JSON objects", () => {
  const metadata = {
    kind: "diff",
    lines: [2, 4],
    nested: { oldLine: null, enabled: true },
  };

  const result = validateSelectionMetadata(metadata);

  assert.equal(result.status, "valid");
  assert.deepEqual(JSON.parse(JSON.stringify(result.value)), metadata);
  assert.notEqual(result.value, metadata);
  assert.equal(Object.getPrototypeOf(result.value), null);
  metadata.nested.enabled = false;
  assert.equal(result.value.nested.enabled, true);
});

test("selection metadata rejects non-object and non-JSON values", () => {
  const sparse = [];
  sparse[1] = "value";

  assert.deepEqual(validateSelectionMetadata(null), { status: "invalid" });
  assert.deepEqual(validateSelectionMetadata([]), { status: "invalid" });
  assert.deepEqual(validateSelectionMetadata(new Date()), { status: "invalid" });
  assert.deepEqual(
    validateSelectionMetadata({ callback: () => "not JSON" }),
    { status: "invalid" },
  );
  assert.deepEqual(
    validateSelectionMetadata({ value: Number.POSITIVE_INFINITY }),
    { status: "invalid" },
  );
  assert.deepEqual(validateSelectionMetadata({ sparse }), { status: "invalid" });
});

test("selection metadata rejects cycles and symbol keys", () => {
  const cyclic = {};
  cyclic.self = cyclic;
  const symbolKeyed = { kind: "diff" };
  symbolKeyed[Symbol("hidden")] = "value";

  assert.deepEqual(validateSelectionMetadata(cyclic), { status: "invalid" });
  assert.deepEqual(validateSelectionMetadata(symbolKeyed), { status: "invalid" });
});

test("selection metadata rejects named array properties without silently dropping them", () => {
  const rows = [1];
  rows.side = "old";

  assert.deepEqual(validateSelectionMetadata({ rows }), { status: "invalid" });
});

test("selection metadata rejects oversized, deeply nested, and huge sparse values early", () => {
  const hugeSparse = [];
  hugeSparse.length = 10_000_000;
  hugeSparse[9_999_999] = "value";
  const deeplyNested = Array.from({ length: 70 })
    .reduce((value) => ({ value }), "leaf");

  assert.deepEqual(
    validateSelectionMetadata({ text: "x".repeat(64001) }),
    { status: "too-large" },
  );
  assert.deepEqual(validateSelectionMetadata({ hugeSparse }), { status: "too-large" });
  assert.deepEqual(validateSelectionMetadata(deeplyNested), { status: "too-large" });
});
