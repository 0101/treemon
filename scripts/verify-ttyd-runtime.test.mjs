import assert from "node:assert/strict";
import { test } from "node:test";
import {
  cleanupRuntimeResources,
  waitForIdentity,
} from "./verify-ttyd-runtime.mjs";

test("verifier helpers are import-safe and visible at module scope", async () => {
  let attempts = 0;
  let now = 0;
  const expected = {
    pid: 101,
    parentPid: 0,
    startIdentity: "windows:100",
  };
  const processController = {
    inspect: async () => (++attempts === 2 ? expected : null),
  };

  const actual = await waitForIdentity(101, processController, {
    timeoutMs: 10,
    now: () => now,
    wait: async () => {
      now += 1;
    },
  });

  assert.equal(actual, expected);
  assert.equal(attempts, 2);
});

test("identity-capture failure cleanup uses only the retained child handle", async () => {
  const child = { pid: 202 };
  let closed = false;
  let retainedChild;
  const browser = {
    close: async () => {
      closed = true;
    },
  };
  const processController = {
    terminate: async () => {
      throw new Error("PID-based controller cleanup must not be used");
    },
  };

  await cleanupRuntimeResources(
    { child, childIdentity: null, browser },
    processController,
    async (actualChild) => {
      retainedChild = actualChild;
    },
  );

  assert.equal(closed, true);
  assert.equal(retainedChild, child);
});

test("browser cleanup failure cannot skip retained-child cleanup", async () => {
  const child = { pid: 303 };
  let retainedChild;

  await assert.rejects(
    cleanupRuntimeResources(
      {
        child,
        childIdentity: null,
        browser: {
          close: async () => {
            throw new Error("browser close failed");
          },
        },
      },
      {},
      async (actualChild) => {
        retainedChild = actualChild;
      },
    ),
    /browser close failed/,
  );

  assert.equal(retainedChild, child);
});
