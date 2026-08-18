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
  const host = new DurableTerminalHost({
    stateDirectory,
    ttydPath: resolve("unused-ttyd.exe"),
    shellCommand: "pwsh",
    replayBytes: 1024,
    diagnosticBytes: 1024,
    processController,
    cleanupTimeouts: { graceful: 0, forced: 0 },
    wait: async () => {},
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

const ownedSession = (id, ttydPid, shellPid) => ({
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
  ttydPid,
  shellPid,
});

test("graceful close removes the registry entry without forcing a process", async () => {
  const forced = [];
  const controller = {
    isAlive: () => false,
    forceTree: async (pid) => forced.push(pid),
  };

  await withTestHost(controller, async (host) => {
    const session = ownedSession("graceful", 101, 102);
    host.sessions.set(session.id, session);

    await host.closeSession(session, "test");

    assert.equal(host.sessions.has(session.id), false);
    assert.deepEqual(forced, []);
  });
});

test("close force-cleans only the tracked owned process tree", async () => {
  const alive = new Set([201, 202, 999]);
  const forced = [];
  const controller = {
    isAlive: (pid) => alive.has(pid),
    forceTree: async (pid) => {
      forced.push(pid);
      if (pid === 201) {
        alive.delete(201);
        alive.delete(202);
      }
    },
  };

  await withTestHost(controller, async (host) => {
    const session = ownedSession("forced", 201, 202);
    host.sessions.set(session.id, session);

    await host.closeSession(session, "test");

    assert.deepEqual(forced, [201]);
    assert.equal(alive.has(999), true);
    assert.equal(host.sessions.has(session.id), false);
  });
});

test("cleanup timeout reports failure while owned processes remain", async () => {
  const controller = {
    isAlive: (pid) => pid === 301,
    forceTree: async () => {},
  };

  await withTestHost(controller, async (host) => {
    const session = ownedSession("timeout", 301, 302);
    host.sessions.set(session.id, session);

    await assert.rejects(
      host.closeSession(session, "test"),
      /Owned terminal processes remain: 301/,
    );
    assert.equal(session.state, "failed");
    assert.match(session.error, /cleanup did not complete/i);
  });
});

test("failed cleanup retains a retryable registry entry", async () => {
  const alive = new Set([401]);
  let canForce = false;
  const controller = {
    isAlive: (pid) => alive.has(pid),
    forceTree: async (pid) => {
      if (canForce) alive.delete(pid);
    },
  };

  await withTestHost(controller, async (host) => {
    const session = ownedSession("retry", 401, null);
    host.sessions.set(session.id, session);

    await assert.rejects(host.closeSession(session, "first"));
    assert.equal(host.sessions.get(session.id), session);

    canForce = true;
    await host.closeSession(session, "retry");
    assert.equal(host.sessions.has(session.id), false);
  });
});

test("closing one session never signals another session's tracked PIDs", async () => {
  const alive = new Set([501, 502, 601, 602]);
  const forced = [];
  const controller = {
    isAlive: (pid) => alive.has(pid),
    forceTree: async (pid) => {
      forced.push(pid);
      if (pid === 501) {
        alive.delete(501);
        alive.delete(502);
      }
    },
  };

  await withTestHost(controller, async (host) => {
    const closing = ownedSession("closing", 501, 502);
    const retained = ownedSession("retained", 601, 602);
    host.sessions.set(closing.id, closing);
    host.sessions.set(retained.id, retained);

    await host.closeSession(closing, "test");

    assert.deepEqual(forced, [501]);
    assert.equal(host.sessions.has(retained.id), true);
    assert.equal(alive.has(601), true);
    assert.equal(alive.has(602), true);
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
  const controller = { isAlive: () => false, forceTree: async () => {} };

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

test("shutdown rejects new starts and cleans every in-flight start", async () => {
  const controller = { isAlive: () => false, forceTree: async () => {} };

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
  const controller = { isAlive: () => false, forceTree: async () => {} };

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
  const alive = new Set([901]);
  const controller = {
    isAlive: (pid) => alive.has(pid),
    forceTree: async () => {},
  };

  await withTestHost(controller, async (host, exits) => {
    await host.start();
    const session = ownedSession("shutdown-failure", 901, null);
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

    alive.clear();
    await host.shutdown("test-cleanup");
    assert.deepEqual(exits, [0]);
  });
});
