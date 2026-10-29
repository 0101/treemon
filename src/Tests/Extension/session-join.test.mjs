import test from "node:test";
import assert from "node:assert/strict";
import { joinCanvasSession } from "../../Extension/session-join.mjs";

const config = {
  tools: [{ name: "canvas_take_ownership" }],
  hooks: { onPostToolUse: () => undefined },
};

test("keeps ownership tools and readability hooks when the first join succeeds", async () => {
  const calls = [];
  const logs = [];
  const session = {};
  const joined = await joinCanvasSession(async (options) => {
    calls.push(options);
    return session;
  }, config, (message) => logs.push(message));

  assert.equal(joined, session);
  assert.deepEqual(calls, [config]);
  assert.deepEqual(logs, []);
});

test("preserves the ownership tool when the host rejects optional readability hooks", async () => {
  const calls = [];
  const logs = [];
  const session = {};
  const joined = await joinCanvasSession(async (options) => {
    calls.push(options);
    if (options?.hooks) throw new Error("hooks unsupported");
    assert.deepEqual(options.tools, config.tools);
    return session;
  }, config, (message) => logs.push(message));

  assert.equal(joined, session);
  assert.deepEqual(calls, [config, { tools: config.tools }]);
  assert.match(logs[0], /hooks unsupported/);
});

test("falls back to a plain join only after tool registration also fails", async () => {
  const calls = [];
  const logs = [];
  const session = {};
  const joined = await joinCanvasSession(async (options) => {
    calls.push(options);
    if (options?.hooks) throw new Error("hooks unsupported");
    if (options?.tools) throw new Error("tools unsupported");
    return session;
  }, config, (message) => logs.push(message));

  assert.equal(joined, session);
  assert.deepEqual(calls, [config, { tools: config.tools }, undefined]);
  assert.equal(logs.length, 2);
  assert.match(logs[1], /tools unsupported/);
});

test("reports a final plain-join failure rather than returning an unusable session", async () => {
  const calls = [];
  const failure = new Error("session unavailable");
  await assert.rejects(joinCanvasSession(async (options) => {
    calls.push(options);
    throw failure;
  }, config, () => {}), (error) => error === failure);
  assert.deepEqual(calls, [config, { tools: config.tools }, undefined]);
});
