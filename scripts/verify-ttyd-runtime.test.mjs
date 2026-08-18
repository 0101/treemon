import assert from "node:assert/strict";
import { EventEmitter } from "node:events";
import { test } from "node:test";
import {
  cleanupOwnedTree,
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

  const child = {
    pid: 101,
    exitCode: null,
    signalCode: null,
  };
  const actual = await waitForIdentity(child, processController, {
    timeoutMs: 10,
    now: () => now,
    wait: async () => {
      now += 1;
    },
  });

  assert.equal(actual, expected);
  assert.equal(attempts, 2);
});

test("exit and PID reuse during capture never become an owned verifier identity", async () => {
  const child = new EventEmitter();
  child.pid = 151;
  child.exitCode = null;
  child.signalCode = null;
  const replacement = {
    pid: 151,
    parentPid: 900,
    startIdentity: "windows:replacement",
  };
  let pidTerminationCalls = 0;
  let retainedCleanupChild;
  const processController = {
    inspect: async () => {
      child.exitCode = 0;
      return replacement;
    },
    terminate: async () => {
      pidTerminationCalls += 1;
    },
  };

  await assert.rejects(
    waitForIdentity(child, processController),
    /exited during identity capture/,
  );
  await cleanupRuntimeResources(
    { child, childIdentity: null, browser: null },
    processController,
    async (actualChild) => {
      retainedCleanupChild = actualChild;
    },
  );

  assert.equal(retainedCleanupChild, child);
  assert.equal(pidTerminationCalls, 0);
});

test("spawn errors raised during identity inspection reject attribution", async () => {
  const child = new EventEmitter();
  child.pid = 161;
  child.exitCode = null;
  child.signalCode = null;
  const spawnError = new Error("spawn failed");

  await assert.rejects(
    waitForIdentity(child, {
      inspect: async () => {
        child.emit("error", spawnError);
        return {
          pid: 161,
          parentPid: 0,
          startIdentity: "windows:161",
        };
      },
    }),
    spawnError,
  );
});

test("verifier discovery rejects replacement descendants of a reused parent", async () => {
  const root = {
    pid: 171,
    parentPid: 0,
    startIdentity: "test:171:root",
  };
  const originalParent = {
    pid: 172,
    parentPid: 171,
    startIdentity: "test:172:original",
  };
  const replacementParent = {
    pid: 172,
    parentPid: 900,
    startIdentity: "test:172:replacement",
  };
  const replacementChild = {
    pid: 173,
    parentPid: 172,
    startIdentity: "test:173:replacement",
  };
  const processes = new Map([
    [root.pid, root],
    [originalParent.pid, originalParent],
  ]);
  const terminated = [];
  let replaced = false;
  const sameIdentity = (left, right) =>
    left?.pid === right?.pid &&
    left?.startIdentity === right?.startIdentity;
  const processController = {
    inspect: async (pid) => processes.get(pid) ?? null,
    children: async (parent) => {
      const actual = processes.get(parent.pid);
      if (!sameIdentity(actual, parent)) return null;
      const children = [...processes.values()].filter(
        (identity) => identity.parentPid === parent.pid,
      );
      if (!replaced && sameIdentity(parent, root)) {
        replaced = true;
        processes.set(replacementParent.pid, replacementParent);
        processes.set(replacementChild.pid, replacementChild);
      }
      return children;
    },
    terminate: async (identity) => {
      const actual = processes.get(identity.pid);
      if (!sameIdentity(actual, identity)) return false;
      terminated.push(identity.pid);
      processes.delete(identity.pid);
      return true;
    },
  };

  await cleanupOwnedTree(root, processController);

  assert.deepEqual(terminated, [171]);
  assert.equal(processes.get(172), replacementParent);
  assert.equal(processes.get(173), replacementChild);
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
