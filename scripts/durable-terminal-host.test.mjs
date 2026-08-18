import assert from "node:assert/strict";
import { randomUUID } from "node:crypto";
import { EventEmitter } from "node:events";
import {
  existsSync,
  mkdirSync,
  readFileSync,
  rmSync,
  writeFileSync,
} from "node:fs";
import { request as httpRequest } from "node:http";
import { resolve } from "node:path";
import { test } from "node:test";
import {
  appendReplayFrame,
  captureSpawnedProcessIdentity,
  cleanupOwnedProcessTree,
  DurableTerminalHost,
  emptyReplayBuffer,
  manifestOwnership,
  parseInitialHandshake,
  parseResizeFrame,
  publicDiagnosticSession,
  removeManifestIfOwned,
  replayFramesFrom,
  resizeFrame,
  sameManifestOwner,
  sameProcessIdentity,
  sanitizeMetadataText,
  sessionCookieName,
  terminalSize,
} from "./durable-terminal-host.mjs";

test("browser handshake becomes a bounded resize frame", () => {
  const handshake = Buffer.from(
    JSON.stringify({ AuthToken: "ignored", columns: 220, rows: 70 }),
  );
  const size = parseInitialHandshake(handshake);

  assert.deepEqual(size, { columns: 220, rows: 70 });
  assert.deepEqual(parseResizeFrame(resizeFrame(size)), size);
  assert.deepEqual(
    terminalSize({ columns: 0, rows: 50_000 }),
    { columns: 120, rows: 30 },
  );
});

test("invalid initial handshake is rejected", () => {
  assert.throws(
    () => parseInitialHandshake(Buffer.from("[]")),
    /JSON object/,
  );
  assert.throws(() => parseInitialHandshake(Buffer.from("not-json")));
});

test("replay retains whole newest frames within its byte bound", () => {
  const first = Buffer.from("0first");
  const second = Buffer.from("0second");
  const third = Buffer.from("0third");
  const replay = [first, second, third].reduce(
    (state, frame) => appendReplayFrame(state, frame, 14),
    emptyReplayBuffer(),
  );

  assert.deepEqual(
    replay.frames.map((frame) => frame.data.toString()),
    ["0second", "0third"],
  );
  assert.equal(replay.bytes, 13);
  assert.equal(replay.nextSequence, 3);
  assert.equal(replay.droppedBytes, first.length);
  assert.deepEqual(
    replayFramesFrom(replay, 2).map((frame) => frame.data.toString()),
    ["0third"],
  );
});

test("oversized replay frame keeps a command-prefixed bounded suffix", () => {
  const replay = appendReplayFrame(
    emptyReplayBuffer(),
    Buffer.from(`0${"x".repeat(100)}`),
    16,
  );

  assert.equal(replay.bytes, 16);
  assert.equal(replay.frames[0].data[0], "0".charCodeAt(0));
  assert.equal(replay.droppedBytes, 85);
});

test("diagnostic session excludes worktree path, capability, and endpoint", () => {
  const diagnostic = publicDiagnosticSession({
    id: "terminal-id",
    state: "running",
    order: 3,
    worktreePath: "Q:\\secret\\worktree",
    capability: "secret-capability",
    endpoint: "http://127.0.0.1:12345/?cap=secret-capability",
    ttydPid: 10,
    shellPid: 11,
    publicPort: 12345,
    ttydPort: 12346,
    attachment: {},
    upstreamOpenedAt: "2026-08-18T12:00:00.000Z",
    replay: { bytes: 9, droppedBytes: 4 },
  });

  const serialized = JSON.stringify(diagnostic);
  assert.equal(serialized.includes("secret"), false);
  assert.equal(serialized.includes("worktree"), false);
  assert.equal(diagnostic.browserAttachments, 1);
});

test("diagnostic text is single-line and bounded", () => {
  assert.equal(
    sanitizeMetadataText(`reason\r\n${"x".repeat(200)}`, 20),
    "reason  xxxxxxxxxxxx",
  );
});

test("each terminal uses a distinct loopback cookie name", () => {
  assert.equal(sessionCookieName("first"), "treemon-terminal-first");
  assert.notEqual(sessionCookieName("first"), sessionCookieName("second"));
});

const testStateDirectory = () =>
  resolve(".agents", "durable-terminal-host-tests", randomUUID());

const withTestHost = async (processController, action) => {
  const stateDirectory = testStateDirectory();
  mkdirSync(stateDirectory, { recursive: true });
  const exits = [];
  let now = 0;
  const host = new DurableTerminalHost({
    stateDirectory,
    ttydPath: resolve("unused-ttyd.exe"),
    shellCommand: "pwsh",
    replayBytes: 1024,
    diagnosticBytes: 1024,
    processController,
    cleanupTimeouts: { graceful: 0, forced: 1 },
    wait: async (milliseconds) => {
      now += milliseconds;
    },
    now: () => now,
    exitProcess: (code) => exits.push(code),
  });
  host.persistStatus = () => {};
  host.record = () => {};

  try {
    await action(host, exits);
  } finally {
    host.stopTimers();
    if (host.controlServer.listening) {
      await new Promise((resolveClose) =>
        host.controlServer.close(() => resolveClose()),
      );
    }
    rmSync(stateDirectory, { recursive: true, force: true });
  }
};

const processIdentity = (pid, parentPid = 0, generation = "original") => ({
  pid,
  parentPid,
  startIdentity: `test:${pid}:${generation}`,
});

const fakeProcessController = (
  initialProcesses,
  terminateProcess,
  beforeTerminate,
) => {
  const processes = new Map(
    initialProcesses.map((identity) => [identity.pid, identity]),
  );
  const terminated = [];
  return {
    processes,
    terminated,
    controller: {
      inspect: async (pid) => processes.get(pid) ?? null,
      children: async (parent) => {
        const actual = processes.get(parent.pid);
        return sameProcessIdentity(actual, parent)
          ? [...processes.values()].filter(
              (identity) => identity.parentPid === parent.pid,
            )
          : null;
      },
      terminate: async (identity) => {
        if (beforeTerminate) beforeTerminate(identity, processes);
        const actual = processes.get(identity.pid);
        if (!sameProcessIdentity(actual, identity)) return false;
        terminated.push(identity.pid);
        if (terminateProcess) terminateProcess(identity.pid, processes);
        else processes.delete(identity.pid);
        return true;
      },
    },
  };
};

const markStartupReady = (session) => {
  session.ttydProcess = {
    pid: 1,
    exitCode: null,
    signalCode: null,
  };
  session.ttydPid = 1;
  session.shellPid = 2;
  session.publicServer = { listening: true };
  session.upstream = { readyState: 1 };
};

class FakeRetainedChild extends EventEmitter {
  constructor(pid) {
    super();
    this.pid = pid;
    this.exitCode = null;
    this.signalCode = null;
    this.killed = false;
  }

  kill() {
    this.killed = true;
    this.signalCode = "SIGKILL";
    queueMicrotask(() => {
      this.exitCode = 1;
      this.emit("exit", 1, "SIGKILL");
    });
    return true;
  }
}

class FakeStartupUpstream extends EventEmitter {
  constructor() {
    super();
    this.readyState = 1;
  }
}

const ownedSession = (
  id,
  ttydPid,
  shellPid,
  tracked = [
    ...(ttydPid ? [processIdentity(ttydPid)] : []),
    ...(shellPid ? [processIdentity(shellPid, ttydPid ?? 0)] : []),
  ],
) => ({
  id,
  capability: `cap-${id}`,
  worktreePath: resolve(".agents", id),
  key: id,
  state: "running",
  error: null,
  order: 0,
  replay: emptyReplayBuffer(),
  attachment: null,
  closing: false,
  ownedProcesses: new Map(
    tracked.map((identity, depth) => [
      `${identity.pid}:${identity.startIdentity}`,
      { identity, depth },
    ]),
  ),
  ttydPid,
  shellPid,
});

test("graceful close removes the registry entry without forcing a process", async () => {
  const { controller, terminated } = fakeProcessController([]);

  await withTestHost(controller, async (host) => {
    const session = ownedSession("graceful", 101, 102);
    host.sessions.set(session.id, session);

    await host.closeSession(session, "test");

    assert.equal(host.sessions.has(session.id), false);
    assert.deepEqual(terminated, []);
  });
});

test("close discovers and force-cleans only exact owned descendants", async () => {
  const root = processIdentity(201);
  const child = processIdentity(202, 201);
  const unrelated = processIdentity(999);
  const { controller, processes, terminated } = fakeProcessController([
    root,
    child,
    unrelated,
  ]);

  await withTestHost(controller, async (host) => {
    const session = ownedSession("forced", 201, 202, [root]);
    host.sessions.set(session.id, session);

    await host.closeSession(session, "test");

    assert.deepEqual(terminated, [201, 202]);
    assert.equal(processes.has(999), true);
    assert.equal(host.sessions.has(session.id), false);
  });
});

test("cleanup timeout reports failure while owned processes remain", async () => {
  const root = processIdentity(301);
  const { controller } = fakeProcessController([root], () => {
    throw new Error("Timed out in fake identity-bound termination");
  });

  await withTestHost(controller, async (host) => {
    const session = ownedSession("timeout", 301, null, [root]);
    host.sessions.set(session.id, session);

    await assert.rejects(
      host.closeSession(session, "test"),
      /forced cleanup timed out/,
    );
    assert.equal(session.state, "failed");
    assert.match(session.error, /cleanup did not complete/i);
  });
});

test("failed cleanup retains a retryable registry entry", async () => {
  const root = processIdentity(401);
  let canForce = false;
  const { controller } = fakeProcessController(
    [root],
    (pid, processes) => {
      if (canForce) processes.delete(pid);
      else throw new Error("Timed out in fake identity-bound termination");
    },
  );

  await withTestHost(controller, async (host) => {
    const session = ownedSession("retry", 401, null, [root]);
    host.sessions.set(session.id, session);

    await assert.rejects(host.closeSession(session, "first"));
    assert.equal(host.sessions.get(session.id), session);

    canForce = true;
    await host.closeSession(session, "retry");
    assert.equal(host.sessions.has(session.id), false);
  });
});

test("closing one session never signals another session's tracked PIDs", async () => {
  const closingRoot = processIdentity(501);
  const closingChild = processIdentity(502, 501);
  const retainedRoot = processIdentity(601);
  const retainedChild = processIdentity(602, 601);
  const { controller, processes, terminated } = fakeProcessController([
    closingRoot,
    closingChild,
    retainedRoot,
    retainedChild,
  ]);

  await withTestHost(controller, async (host) => {
    const closing = ownedSession("closing", 501, 502, [closingRoot]);
    const retained = ownedSession("retained", 601, 602, [retainedRoot]);
    host.sessions.set(closing.id, closing);
    host.sessions.set(retained.id, retained);

    await host.closeSession(closing, "test");

    assert.deepEqual(terminated, [501, 502]);
    assert.equal(host.sessions.has(retained.id), true);
    assert.equal(processes.has(601), true);
    assert.equal(processes.has(602), true);
  });
});

test("PID reuse is treated as owned-process exit and never terminated", async () => {
  const original = processIdentity(701);
  const replacement = processIdentity(701, 0, "replacement");
  const { controller, processes, terminated } = fakeProcessController([
    replacement,
  ]);

  await withTestHost(controller, async (host) => {
    const session = ownedSession("pid-reuse", 701, null, [original]);
    host.sessions.set(session.id, session);

    await host.closeSession(session, "test");

    assert.deepEqual(terminated, []);
    assert.equal(processes.get(701), replacement);
    assert.equal(host.sessions.has(session.id), false);
  });
});

test("an unverified spawned root is stopped only through its retained child handle", async () => {
  const unverified = processIdentity(705);
  const { controller, processes, terminated } = fakeProcessController([unverified]);

  await withTestHost(controller, async (host) => {
    const retainedChild = new FakeRetainedChild(705);
    const session = {
      ...ownedSession("unverified", 705, null, []),
      unverifiedSpawnedPids: [705],
      ttydProcess: retainedChild,
    };
    host.sessions.set(session.id, session);

    await host.closeSession(session, "test");

    assert.deepEqual(terminated, []);
    assert.equal(retainedChild.killed, true);
    assert.equal(processes.get(705), unverified);
    assert.equal(host.sessions.has(session.id), false);
  });
});

test("identity replacement between observation and atomic termination is never signaled", async () => {
  const original = processIdentity(706);
  const replacement = processIdentity(706, 0, "replacement");
  const { controller, processes, terminated } = fakeProcessController(
    [original],
    undefined,
    (_, current) => current.set(replacement.pid, replacement),
  );

  await withTestHost(controller, async (host) => {
    const session = ownedSession("identity-race", 706, null, [original]);
    host.sessions.set(session.id, session);

    await host.closeSession(session, "test");

    assert.deepEqual(terminated, []);
    assert.equal(processes.get(706), replacement);
    assert.equal(host.sessions.has(session.id), false);
  });
});

test("spawn identity capture rejects a root that exits during inspection", async () => {
  const child = {
    pid: 707,
    exitCode: null,
    signalCode: null,
  };
  const identity = processIdentity(707);

  await assert.rejects(
    captureSpawnedProcessIdentity(
      child,
      async () => {
        child.exitCode = 1;
        return identity;
      },
      async () => {},
    ),
    /exited during identity capture/,
  );
});

test("a captured descendant remains owned after reparenting", async () => {
  const root = processIdentity(711);
  const child = processIdentity(712, 711);
  const { controller, processes, terminated } = fakeProcessController([
    root,
    child,
  ]);
  let firstChildQuery = true;
  const discoverChildren = controller.children;
  controller.children = async (parent, timeoutMs) => {
    const children = await discoverChildren(parent, timeoutMs);
    if (parent.pid === root.pid && firstChildQuery) {
      firstChildQuery = false;
      processes.delete(root.pid);
      processes.set(child.pid, { ...child, parentPid: 0 });
    }
    return children;
  };

  await withTestHost(controller, async (host) => {
    const session = ownedSession("reparented", 711, 712, [root]);
    host.sessions.set(session.id, session);

    await host.closeSession(session, "test");

    assert.deepEqual(terminated, [712]);
    assert.equal(host.sessions.has(session.id), false);
  });
});

test("replacement children are never claimed after a parent identity changes", async () => {
  const root = processIdentity(715);
  const originalParent = processIdentity(716, 715);
  const replacementParent = processIdentity(716, 900, "replacement");
  const replacementChild = processIdentity(717, 716, "replacement");
  const { controller, processes, terminated } = fakeProcessController([
    root,
    originalParent,
  ]);
  const discoverChildren = controller.children;
  let replaced = false;
  controller.children = async (parent, timeoutMs) => {
    const children = await discoverChildren(parent, timeoutMs);
    if (!replaced && sameProcessIdentity(parent, root)) {
      replaced = true;
      processes.set(replacementParent.pid, replacementParent);
      processes.set(replacementChild.pid, replacementChild);
    }
    return children;
  };

  await withTestHost(controller, async (host) => {
    const session = ownedSession("parent-reuse", 715, null, [root]);
    host.sessions.set(session.id, session);

    await host.closeSession(session, "test");

    assert.deepEqual(terminated, [715]);
    assert.equal(processes.get(716), replacementParent);
    assert.equal(processes.get(717), replacementChild);
    assert.equal(host.sessions.has(session.id), false);
  });
});

test("forced cleanup gives every slow operation only the remaining deadline", async () => {
  const root = processIdentity(730);
  const firstChild = processIdentity(731, 730);
  const secondChild = processIdentity(732, 730);
  const processes = new Map(
    [root, firstChild, secondChild].map((identity) => [
      identity.pid,
      identity,
    ]),
  );
  const budgets = [];
  const terminated = [];
  let now = 0;
  const consume = (timeoutMs, durationMs) => {
    budgets.push(timeoutMs);
    now += Math.min(timeoutMs, durationMs);
  };
  const controller = {
    children: async (parent, timeoutMs) => {
      consume(timeoutMs, 2);
      const actual = processes.get(parent.pid);
      return sameProcessIdentity(actual, parent)
        ? [...processes.values()].filter(
            (identity) => identity.parentPid === parent.pid,
          )
        : null;
    },
    inspect: async (pid, timeoutMs) => {
      consume(timeoutMs, 4);
      return processes.get(pid) ?? null;
    },
    terminate: async (identity, timeoutMs) => {
      consume(timeoutMs, 6);
      terminated.push(identity.pid);
      processes.delete(identity.pid);
      return true;
    },
  };

  await assert.rejects(
    cleanupOwnedProcessTree(root, controller, {
      timeoutMs: 20,
      now: () => now,
      wait: async (milliseconds) => {
        now += milliseconds;
      },
    }),
    /forced cleanup timed out/,
  );

  assert.equal(now, 20);
  assert.deepEqual(terminated, [730]);
  assert.deepEqual(budgets, [20, 18, 16, 14, 8, 4]);
  assert.ok(budgets.every((budget) => budget <= 20));
});

test("a stalled cleanup cannot block another key or unbound shutdown", async () => {
  const root = processIdentity(740);
  const controller = {
    children: async (parent) =>
      parent.pid === root.pid
        ? new Promise(() => {})
        : [],
    inspect: async () => null,
    terminate: async () => true,
  };

  await withTestHost(controller, async (host) => {
    const stalled = ownedSession("stalled-cleanup", 740, null, [root]);
    const unrelated = ownedSession("unrelated-cleanup", null, null, []);
    host.sessions.set(stalled.id, stalled);
    host.sessions.set(unrelated.id, unrelated);

    const stalledClose = host.closeSession(stalled, "test");
    await host.closeSession(unrelated, "test");
    await assert.rejects(stalledClose, /forced cleanup timed out/);

    assert.equal(host.sessions.has(unrelated.id), false);
    assert.equal(host.sessions.get(stalled.id), stalled);

    const startedAt = Date.now();
    await assert.rejects(
      host.beginShutdown("bounded-timeout"),
      /could not close 1 session/,
    );
    assert.ok(Date.now() - startedAt < 250);
    assert.equal(host.sessions.get(stalled.id), stalled);
    host.resumeAfterFailedShutdown();
  });
});

test("cleanup retains the session when a captured descendant survives force", async () => {
  const root = processIdentity(721);
  const child = processIdentity(722, 721);
  const unrelated = processIdentity(799);
  const { controller, processes, terminated } = fakeProcessController(
    [root, child, unrelated],
    (pid, current) => {
      if (pid === child.pid) {
        throw new Error("Timed out in fake identity-bound termination");
      }
      current.delete(pid);
    },
  );

  await withTestHost(controller, async (host) => {
    const session = ownedSession("surviving-child", 721, 722, [root]);
    host.sessions.set(session.id, session);

    await assert.rejects(
      host.closeSession(session, "test"),
      /forced cleanup timed out/,
    );

    assert.deepEqual(terminated, [721, 722]);
    assert.equal(processes.has(799), true);
    assert.equal(host.sessions.get(session.id), session);
    assert.equal(session.state, "failed");
  });
});

test("manifest deletion requires the exact generation and process identity", () => {
  const stateDirectory = testStateDirectory();
  const statePath = resolve(stateDirectory, "host.json");
  const first = {
    generation: "first",
    pid: 701,
    processStartTicks: "100",
  };
  const replacement = {
    generation: "replacement",
    pid: 701,
    processStartTicks: "200",
  };
  mkdirSync(stateDirectory, { recursive: true });
  writeFileSync(statePath, JSON.stringify(replacement));

  try {
    assert.equal(sameManifestOwner(first, replacement), false);
    assert.equal(removeManifestIfOwned(statePath, first), false);
    assert.deepEqual(
      manifestOwnership(JSON.parse(readFileSync(statePath, "utf8"))),
      replacement,
    );
    assert.equal(removeManifestIfOwned(statePath, replacement), true);
    assert.equal(existsSync(statePath), false);
  } finally {
    rmSync(stateDirectory, { recursive: true, force: true });
  }
});

test("a host cannot overwrite another generation's manifest", async () => {
  const stateDirectory = testStateDirectory();
  const statePath = resolve(stateDirectory, "host.json");
  const existing = {
    version: 2,
    generation: "existing",
    pid: 801,
    processStartTicks: "100",
    controlPort: 12345,
    controlToken: "token",
    startedAt: new Date(0).toISOString(),
  };
  mkdirSync(stateDirectory, { recursive: true });
  writeFileSync(statePath, JSON.stringify(existing));
  const host = new DurableTerminalHost({
    stateDirectory,
    ttydPath: resolve("unused-ttyd.exe"),
    shellCommand: "pwsh",
    replayBytes: 1024,
    diagnosticBytes: 1024,
    exitProcess: () => {},
  });

  try {
    await assert.rejects(host.start(), /already owned by another generation/);
    assert.deepEqual(JSON.parse(readFileSync(statePath, "utf8")), existing);
  } finally {
    if (host.controlServer.listening) {
      await new Promise((resolveClose) =>
        host.controlServer.close(() => resolveClose()),
      );
    }
    rmSync(stateDirectory, { recursive: true, force: true });
  }
});

class FakeBrowserSocket extends EventEmitter {
  constructor(session) {
    super();
    this.session = session;
    this.readyState = 1;
    this.sent = [];
    this.attachmentAtClose = undefined;
  }

  send(data) {
    this.sent.push(Buffer.from(data));
  }

  close() {
    this.attachmentAtClose = this.session.attachment;
    this.readyState = 3;
  }
}

test("browser takeover revokes old handlers before closing the old socket", async () => {
  const { controller } = fakeProcessController([]);

  await withTestHost(controller, async (host) => {
    const upstreamFrames = [];
    const session = {
      ...ownedSession("attachment", null, null),
      replay: emptyReplayBuffer(),
      titleFrame: null,
      preferencesFrame: null,
      terminalSize: { columns: 120, rows: 30 },
      upstream: {
        readyState: 1,
        send: (frame) => upstreamFrames.push(Buffer.from(frame)),
      },
    };
    const oldSocket = new FakeBrowserSocket(session);
    const replacementSocket = new FakeBrowserSocket(session);

    host.attachBrowser(session, oldSocket);
    const oldAttachment = session.attachment;
    host.attachBrowser(session, replacementSocket);
    const replacement = session.attachment;

    assert.equal(oldSocket.attachmentAtClose, null);
    oldSocket.emit("message", Buffer.from("0stale-input"));
    oldSocket.emit("close", 1000, Buffer.from(""));
    oldSocket.emit("error", new Error("stale"));
    assert.equal(session.attachment, replacement);
    assert.equal(upstreamFrames.length, 0);

    host.handleBrowserFrame(
      session,
      oldAttachment,
      Buffer.from(JSON.stringify({ columns: 80, rows: 20 })),
    );
    assert.equal(upstreamFrames.length, 0);

    replacementSocket.emit(
      "message",
      Buffer.from(JSON.stringify({ columns: 80, rows: 20 })),
    );
    replacementSocket.emit("message", Buffer.from("0current-input"));
    assert.equal(upstreamFrames.length, 2);
    assert.equal(upstreamFrames[1].toString(), "0current-input");
  });
});

const waitForCondition = async (predicate) => {
  if (predicate()) return;
  await new Promise((resolveImmediate) => setImmediate(resolveImmediate));
  return waitForCondition(predicate);
};

test("upstream close and error at every startup stage revoke running publication", async () => {
  const stages = [
    "public-listen",
    "child-spawn",
    "identity-capture",
    "ttyd-ready",
    "upstream-open",
    "shell-identity",
  ];

  for (const eventName of ["close", "error"]) {
    for (const stage of stages) {
      const { controller } = fakeProcessController([]);
      await withTestHost(controller, async (host) => {
        const records = [];
        let cleanups = 0;
        host.record = (kind) => records.push(kind);
        host.stopSessionResources = async () => {
          cleanups += 1;
        };
        host.startSessionProxy = async (session) => {
          markStartupReady(session);
          session.upstream = new FakeStartupUpstream();
          host.configureUpstream(session);
          for (const currentStage of stages) {
            if (currentStage === stage) {
              if (eventName === "close") {
                session.upstream.readyState = 3;
                session.upstream.emit("close", 1006, Buffer.from(stage));
              } else {
                session.upstream.emit("error", new Error(stage));
              }
            }
            await Promise.resolve();
          }
        };

        const session = await host.startSession(resolve(".agents"));
        await waitForCondition(() => host.keyOperations.size === 0);

        assert.equal(session.state, "failed");
        assert.equal(session.failureRecorded, true);
        assert.equal(session.resourcesStopped, true);
        assert.equal(cleanups, 1);
        assert.equal(records.includes("session-running"), false);
      });
    }
  }
});

test("start racing synchronous interruption remains failed without deadlock", async () => {
  const { controller } = fakeProcessController([]);

  await withTestHost(controller, async (host) => {
    let releaseStart;
    const startGate = new Promise((resolveStart) => {
      releaseStart = resolveStart;
    });
    let cleanups = 0;
    host.stopSessionResources = async () => {
      cleanups += 1;
    };
    host.startSessionProxy = async (session) => {
      markStartupReady(session);
      await startGate;
    };

    const starting = host.startSession(resolve(".agents"));
    await waitForCondition(() => host.sessions.size === 1);
    const session = [...host.sessions.values()][0];
    const interrupted = host.interruptSession(session, "startup was interrupted");

    assert.equal(session.state, "failed");
    assert.equal(session.failureRecorded, true);
    releaseStart();

    const result = await starting;
    await interrupted;
    assert.equal(result, session);
    assert.equal(result.state, "failed");
    assert.equal(result.resourcesStopped, true);
    assert.equal(cleanups, 1);
  });
});

test("startup readiness failures clean resources and never publish running", async () => {
  const cases = [
    ["failed child", (session) => (session.ttydProcess.exitCode = 1)],
    ["closed upstream", (session) => (session.upstream.readyState = 3)],
    ["closed public server", (session) => (session.publicServer.listening = false)],
    ["missing shell identity", (session) => (session.shellPid = null)],
    [
      "changed key owner",
      (session, host) => {
        host.sessions.delete(session.id);
        host.sessions.set("replacement", {
          ...session,
          id: "replacement",
        });
      },
    ],
  ];

  for (const [name, invalidate] of cases) {
    const { controller } = fakeProcessController([]);
    await withTestHost(controller, async (host) => {
      const records = [];
      let cleanups = 0;
      host.record = (kind) => records.push(kind);
      host.stopSessionResources = async () => {
        cleanups += 1;
      };
      host.startSessionProxy = async (session) => {
        markStartupReady(session);
        invalidate(session, host);
      };

      const session = await host.startSession(resolve(".agents"));

      assert.equal(session.state, "failed");
      assert.equal(session.failureRecorded, true);
      assert.equal(session.resourcesStopped, true);
      assert.equal(cleanups, 1);
      assert.equal(records.includes("session-running"), false);
    });
  }
});

test("diagnostic heartbeat omits a running session whose upstream closed", async () => {
  const { controller } = fakeProcessController([]);

  await withTestHost(controller, async (host) => {
    const kinds = [];
    host.record = (kind) => kinds.push(kind);
    const closed = ownedSession("closed-upstream", null, null);
    closed.upstream = { readyState: 3 };
    const open = ownedSession("open-upstream", null, null);
    open.upstream = { readyState: 1 };
    host.sessions.set(closed.id, closed);
    host.sessions.set(open.id, open);

    host.recordHeartbeats();

    assert.deepEqual(kinds, ["heartbeat"]);
  });
});

test("close waits for the serialized start before removing its session", async () => {
  const { controller } = fakeProcessController([]);

  await withTestHost(controller, async (host) => {
    let releaseStart;
    const startGate = new Promise((resolveStart) => {
      releaseStart = resolveStart;
    });
    host.startSessionProxy = async (session) => {
      markStartupReady(session);
      await startGate;
    };
    host.stopSessionResources = async () => {};
    const path = resolve(".agents");

    const starting = host.startSession(path);
    await waitForCondition(() => host.sessions.size === 1);
    const session = [...host.sessions.values()][0];
    const closing = host.closeSession(session, "race-test");
    await new Promise((resolveImmediate) => setImmediate(resolveImmediate));

    assert.equal(host.sessions.get(session.id), session);
    assert.equal(session.state, "starting");

    releaseStart();
    await starting;
    await closing;
    assert.equal(host.sessions.size, 0);
  });
});

test("concurrent failed-session retries create one replacement", async () => {
  const { controller } = fakeProcessController([]);

  await withTestHost(controller, async (host) => {
    const path = resolve(".agents");
    const failed = {
      ...ownedSession("failed-retry", null, null),
      worktreePath: path,
      key: process.platform === "win32" ? path.toLowerCase() : path,
      state: "failed",
    };
    host.sessions.set(failed.id, failed);
    let releaseCleanup;
    const cleanupGate = new Promise((resolveCleanup) => {
      releaseCleanup = resolveCleanup;
    });
    host.stopSessionResources = async (session) => {
      if (session === failed) await cleanupGate;
    };
    let starts = 0;
    host.startSessionProxy = async (session) => {
      starts += 1;
      markStartupReady(session);
    };

    const first = host.startSession(path);
    const second = host.startSession(path);
    await waitForCondition(() => failed.state === "closing");
    releaseCleanup();
    const [firstResult, secondResult] = await Promise.all([first, second]);

    assert.equal(starts, 1);
    assert.equal(firstResult, secondResult);
    assert.equal(host.sessions.size, 1);
    assert.equal(firstResult.state, "running");
  });
});

test("reservation rejects same-key starts until explicit release", async () => {
  const { controller } = fakeProcessController([]);

  await withTestHost(controller, async (host) => {
    const path = resolve(".agents");
    host.startSessionProxy = async (session) => markStartupReady(session);
    const reservation = await host.reserveWorktree(path);

    await assert.rejects(
      host.startSession(path),
      /blocked while the worktree is being deleted or archived/,
    );
    assert.equal(await host.releaseReservation(reservation.id), true);
    const session = await host.startSession(path);
    assert.equal(session.state, "running");
  });
});

test("caller reservation identity is validated and retained", async () => {
  const { controller } = fakeProcessController([]);

  await withTestHost(controller, async (host) => {
    await assert.rejects(
      host.reserveWorktree(resolve(".agents"), "short"),
      /reservation ID is invalid/,
    );

    const reservation = await host.reserveWorktree(
      resolve(".agents"),
      "known-reservation-id",
    );
    assert.equal(reservation.id, "known-reservation-id");
    await host.releaseReservation(reservation.id);
  });
});

test("failed reservation cleanup releases the key for retry", async () => {
  const { controller } = fakeProcessController([]);

  await withTestHost(controller, async (host) => {
    const path = resolve(".agents");
    const failed = {
      ...ownedSession("reservation-failure", null, null),
      worktreePath: path,
      key: process.platform === "win32" ? path.toLowerCase() : path,
      state: "failed",
    };
    host.sessions.set(failed.id, failed);
    let failCleanup = true;
    host.stopSessionResources = async () => {
      if (failCleanup) throw new Error("cleanup failed");
    };

    await assert.rejects(host.reserveWorktree(path), /cleanup failed/);
    assert.equal(host.reservations.size, 0);

    failCleanup = false;
    const reservation = await host.reserveWorktree(path);
    assert.equal(host.reservations.size, 1);
    assert.equal(await host.releaseReservation(reservation.id), true);
  });
});

test("a reserved key never blocks an unrelated key", async () => {
  const { controller } = fakeProcessController([]);

  await withTestHost(controller, async (host) => {
    const firstPath = resolve(".agents");
    const secondPath = resolve("scripts");
    host.startSessionProxy = async (session) => markStartupReady(session);
    const reservation = await host.reserveWorktree(firstPath);

    const second = await host.startSession(secondPath);

    assert.equal(second.worktreePath, secondPath);
    assert.equal(second.state, "running");
    await host.releaseReservation(reservation.id);
  });
});

test("an abandoned reservation expires before the key can start again", async () => {
  const { controller } = fakeProcessController([]);

  await withTestHost(controller, async (host) => {
    let now = 1_000;
    host.now = () => now;
    host.reservationLeaseMs = 50;
    host.startSessionProxy = async (session) => markStartupReady(session);
    const path = resolve(".agents");
    await host.reserveWorktree(path);

    now += 51;
    const session = await host.startSession(path);

    assert.equal(session.state, "running");
    assert.equal(host.reservations.size, 0);
  });
});

test("host shutdown refuses an active worktree reservation", async () => {
  const { controller } = fakeProcessController([]);

  await withTestHost(controller, async (host) => {
    const reservation = await host.reserveWorktree(resolve(".agents"));

    assert.throws(
      () => host.beginShutdown("reserved"),
      /active worktree mutation reservations/,
    );
    assert.equal(host.shuttingDown, false);

    await host.releaseReservation(reservation.id);
  });
});

test("shutdown rejects new starts and cleans every in-flight start", async () => {
  const { controller } = fakeProcessController([]);

  await withTestHost(controller, async (host, exits) => {
    let releaseStart;
    const startGate = new Promise((resolveStart) => {
      releaseStart = resolveStart;
    });
    host.startSession = async () => {
      const session = ownedSession("shutdown-race", null, null);
      host.sessions.set(session.id, session);
      await startGate;
      return session;
    };
    await host.start();
    const headers = {
      authorization: `Bearer ${host.controlToken}`,
      "content-type": "application/json",
    };
    const firstStart = fetch(
      `http://127.0.0.1:${host.controlPort}/sessions`,
      {
        method: "POST",
        headers,
        body: JSON.stringify({ worktreePath: resolve(".agents") }),
      },
    );
    await waitForCondition(() => host.inFlightStarts.size === 1);

    const shutdown = host.beginShutdown("test-race");
    const unavailable = await fetch(
      `http://127.0.0.1:${host.controlPort}/sessions`,
      {
        method: "POST",
        headers,
        body: JSON.stringify({ worktreePath: resolve(".agents") }),
      },
    );
    assert.equal(unavailable.status, 503);
    assert.deepEqual(await unavailable.json(), {
      error: "Durable terminal host is shutting down",
    });

    releaseStart();
    await firstStart;
    await shutdown;
    assert.equal(host.sessions.size, 0);

    await host.finalizeShutdown("test-race");
    assert.deepEqual(exits, [0]);
  });
});

test("shutdown rejects a start whose body was still arriving at quiescence", async () => {
  const { controller } = fakeProcessController([]);

  await withTestHost(controller, async (host, exits) => {
    await host.start();
    let observeInitialCheck;
    const initialCheck = new Promise((resolveCheck) => {
      observeInitialCheck = resolveCheck;
    });
    const rejectMutation =
      host.rejectMutationDuringShutdown.bind(host);
    let observed = false;
    host.rejectMutationDuringShutdown = (response) => {
      if (!observed) {
        observed = true;
        observeInitialCheck();
      }
      return rejectMutation(response);
    };
    const payload = JSON.stringify({
      worktreePath: resolve(".agents"),
    });
    let slowRequest;
    const response = new Promise((resolveResponse, rejectResponse) => {
      const request = httpRequest(
        {
          host: "127.0.0.1",
          port: host.controlPort,
          method: "POST",
          path: "/sessions",
          headers: {
            authorization: `Bearer ${host.controlToken}`,
            "content-type": "application/json",
            "content-length": Buffer.byteLength(payload),
          },
        },
        (incoming) => {
          const chunks = [];
          incoming.on("data", (chunk) => chunks.push(chunk));
          incoming.on("end", () =>
            resolveResponse({
              status: incoming.statusCode,
              body: JSON.parse(Buffer.concat(chunks).toString("utf8")),
            }),
          );
        },
      );
      request.on("error", rejectResponse);
      request.flushHeaders();
      slowRequest = request;
    });

    await initialCheck;
    await host.beginShutdown("body-race");
    slowRequest.end(payload);
    const unavailable = await response;

    assert.deepEqual(unavailable, {
      status: 503,
      body: { error: "Durable terminal host is shutting down" },
    });
    assert.equal(host.sessions.size, 0);
    await host.finalizeShutdown("body-race");
    assert.deepEqual(exits, [0]);
  });
});

test("shutdown reports cleanup failure and retains the failed session", async () => {
  const root = processIdentity(901);
  const { controller, processes } = fakeProcessController([root], () => {
    throw new Error("Timed out in fake identity-bound termination");
  });

  await withTestHost(controller, async (host, exits) => {
    await host.start();
    const session = ownedSession("shutdown-failure", 901, null, [root]);
    host.sessions.set(session.id, session);
    const response = await fetch(
      `http://127.0.0.1:${host.controlPort}/shutdown`,
      {
        method: "POST",
        headers: {
          authorization: `Bearer ${host.controlToken}`,
        },
      },
    );

    assert.equal(response.status, 500);
    assert.match((await response.json()).error, /could not close 1 session/);
    assert.equal(host.sessions.get(session.id), session);
    assert.equal(session.state, "failed");
    assert.equal(host.shuttingDown, false);
    assert.equal(existsSync(host.statePath), true);

    processes.clear();
    await host.shutdown("test-cleanup");
    assert.deepEqual(exits, [0]);
  });
});
