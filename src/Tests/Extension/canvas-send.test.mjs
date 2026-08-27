import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";
import vm from "node:vm";

const source =
  readFileSync(new URL("../../Extension/canvas-send.js", import.meta.url), "utf8");

function installedCanvasSend({ topLevel = false } = {}) {
  const errors = [];
  const messages = [];
  const parent = {
    postMessage(message, origin) {
      messages.push({ message: structuredClone(message), origin });
    },
  };
  const window = { parent };
  if (topLevel) window.parent = window;

  vm.runInNewContext(source, {
    console: {
      error(...parts) {
        errors.push(parts.map(String).join(" "));
      },
    },
    window,
  });

  return { errors, messages, send: window.canvasSend };
}

test("canvasSend posts a flat action message", () => {
  const { messages, send } = installedCanvasSend();

  assert.equal(send("comment", { text: "keep this" }), true);
  assert.equal(
    JSON.stringify(messages),
    JSON.stringify([{
      message: { text: "keep this", action: "comment" },
      origin: "*",
    }]),
  );
});

test("canvasSend rejects unavailable, invalid, oversized, and cyclic messages", () => {
  const topLevel = installedCanvasSend({ topLevel: true });
  assert.equal(topLevel.send("comment", { text: "blocked" }), false);
  assert.deepEqual(topLevel.messages, []);

  const installed = installedCanvasSend();
  assert.equal(installed.send("   ", {}), false);
  assert.equal(installed.send("comment", { text: "x".repeat(64000) }), false);

  const cyclic = {};
  cyclic.self = cyclic;
  assert.equal(installed.send("comment", cyclic), false);
  assert.deepEqual(installed.messages, []);
  assert.equal(installed.errors.length, 3);
});

test("canvasSend catches construction, serialization-result, and structured-clone failures", () => {
  const installed = installedCanvasSend();
  const throwingGetter = {};
  Object.defineProperty(throwingGetter, "value", {
    enumerable: true,
    get() {
      throw new Error("getter failed");
    },
  });

  assert.equal(installed.send("comment", { callback() {} }), false);
  assert.equal(installed.send("comment", { toJSON: () => undefined }), false);
  assert.equal(installed.send("comment", throwingGetter), false);
  assert.deepEqual(installed.messages, []);
  assert.equal(installed.errors.length, 3);
});
