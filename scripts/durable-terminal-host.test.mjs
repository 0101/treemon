import assert from "node:assert/strict";
import { execFileSync } from "node:child_process";
import { randomUUID } from "node:crypto";
import { EventEmitter, once } from "node:events";
import {
  existsSync,
  mkdirSync,
  readFileSync,
  rmSync,
  writeFileSync,
} from "node:fs";
import { request as httpRequest } from "node:http";
import { join, resolve } from "node:path";
import { PassThrough } from "node:stream";
import { test } from "node:test";
import {
  appendReplayFrame,
  createTerminalJobSupervisor,
  DurableTerminalHost,
  emptyReplayBuffer,
  jobSupervisorPolicyIsSafe,
  manifestOwnership,
  parseInitialHandshake,
  parseResizeFrame,
  publicDiagnosticSession,
  requireKernelTerminalOwnership,
  removeManifestIfOwned,
  replayFramesFrom,
  resizeFrame,
  sameManifestOwner,
  sanitizeMetadataText,
  sessionCookieName,
  TerminalJobSupervisor,
  terminalJobProtocolGeneration,
  terminalSize,
  terminateRetainedChild,
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
    supervisorFactory:
      processController?.supervisorFactory ?? (() => new FakeJobSupervisor()),
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

const fakeProcessController = () => ({ controller: {} });

let nextFakeSupervisorPid = 10_000;

class FakeSupervisorChild extends EventEmitter {
  constructor() {
    super();
    this.pid = nextFakeSupervisorPid++;
    this.exitCode = null;
    this.signalCode = null;
  }

  exit(code = 0) {
    if (this.exitCode !== null) return;
    this.exitCode = code;
    this.emit("exit", code, null);
  }
}

class FakeJobSupervisor {
  constructor({
    startError,
    terminateError,
    terminateGate,
    members,
  } = {}) {
    this.child = new FakeSupervisorChild();
    this.startError = startError;
    this.terminateError = terminateError;
    this.terminateGate = terminateGate;
    this.members = members ?? new Set([2]);
    this.terminateCalls = 0;
    this.containsCalls = [];
    this.exited = false;
  }

  async start() {
    if (this.startError) throw this.startError;
    return {
      event: "ready",
      ttydPid: 1,
      supervisorPid: this.child.pid,
      supervisorStartTimeUtcTicks: "100",
      assignedBeforeResume: true,
      killOnJobClose: true,
      breakawayAllowed: false,
      silentBreakawayAllowed: false,
    };
  }

  async contains(pid) {
    this.containsCalls.push(pid);
    return this.members.has(pid);
  }

  async terminate() {
    this.terminateCalls += 1;
    if (this.terminateGate) await this.terminateGate;
    if (this.terminateError) throw this.terminateError;
    this.exited = true;
    this.child.exit();
  }
}

const markStartupReady = (session) => {
  const supervisor = new FakeJobSupervisor();
  session.jobSupervisor = supervisor;
  session.supervisorProcess = supervisor.child;
  session.supervisorPid = supervisor.child.pid;
  session.supervisorStartTimeUtcTicks = "100";
  session.ttydPid = 1;
  session.shellPid = 2;
  session.publicServer = { listening: true };
  session.upstream = { readyState: 1 };
};

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
  supervisor = new FakeJobSupervisor(),
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
  jobSupervisor: supervisor,
  supervisorProcess: supervisor.child,
  supervisorPid: supervisor.child.pid,
  supervisorStartTimeUtcTicks: "100",
  ttydPid,
  shellPid,
});

test("checked-in supervisor compiles with assign-before-resume no-breakaway policy", () => {
  const supervisorPath = join(
    import.meta.dirname,
    "terminal-job-supervisor.ps1",
  );
  const output = execFileSync(
    "pwsh",
    [
      "-NoProfile",
      "-NonInteractive",
      "-File",
      supervisorPath,
      "-SelfTest",
    ],
    { encoding: "utf8", windowsHide: true },
  );
  const policy = JSON.parse(output);

  assert.equal(jobSupervisorPolicyIsSafe(policy), true);
  assert.equal(policy.descendantsInheritMembership, true);
  assert.equal(policy.createSuspended, 4);
  assert.equal(policy.controlStandardHandlesNonInheritable, true);
  assert.equal(policy.childInheritedHandleCount, 1);
  assert.equal(policy.childStandardHandles, "NUL");
  assert.equal(policy.failedLaunchHandleDelta, 0);
  assert.equal(policy.quoteProbe, '"C:\\path with space\\\\"');
});

test("supervisor launch boundary excludes every control standard handle", () => {
  const source = readFileSync(
    join(import.meta.dirname, "terminal-job-supervisor.ps1"),
    "utf8",
  );
  const prepare = source.indexOf("DisableControlHandleInheritance();");
  const launch = source.indexOf("if (!CreateProcessW(", prepare);

  assert.notEqual(prepare, -1);
  assert.ok(launch > prepare);
  assert.match(source, /GetStdHandle\(standardHandle\)/);
  assert.match(
    source,
    /SetHandleInformation\(handle, HandleFlagInherit, 0\)/,
  );
  assert.match(source, /hStdInput = nullHandle/);
  assert.match(source, /hStdOutput = nullHandle/);
  assert.match(source, /hStdError = nullHandle/);
  assert.match(
    source.slice(launch, launch + 700),
    /IntPtr\.Zero,\s+IntPtr\.Zero,\s+true,[\s\S]*workingDirectory,\s+ref startup/,
  );
});

test("unsupported platforms fail instead of using process enumeration", () => {
  assert.throws(
    () => requireKernelTerminalOwnership("linux"),
    /kernel-enforced .* unsupported on linux/i,
  );
});

test(
  "host control pipe loss closes the real Job Object boundary",
  { skip: process.platform !== "win32" },
  async () => {
    const fixture = testStateDirectory();
    mkdirSync(fixture, { recursive: true });
    let supervisor;

    try {
      supervisor = createTerminalJobSupervisor();
      await supervisor.start({
        sessionId: randomUUID(),
        fileName: process.execPath,
        argumentsList: ["-e", "setInterval(() => {}, 1000)"],
        workingDirectory: fixture,
        environment: { TREEMON_JOB_PIPE_FIXTURE: fixture },
        timeoutMs: 10_000,
      });
      const exited = once(supervisor.child, "exit");

      supervisor.child.stdin.end();
      await new Promise((resolveExit, rejectExit) => {
        const timeout = setTimeout(
          () => rejectExit(new Error("Supervisor did not exit after pipe loss")),
          15_000,
        );
        exited.then(
          (result) => {
            clearTimeout(timeout);
            resolveExit(result);
          },
          (error) => {
            clearTimeout(timeout);
            rejectExit(error);
          },
        );
      });

      assert.equal(supervisor.exited, true);
    } finally {
      if (supervisor && !supervisor.exited) {
        await terminateRetainedChild(supervisor.child);
      }
      rmSync(fixture, { recursive: true, force: true });
    }
  },
);

const protocolSupervisor = (respond) => {
  const child = new EventEmitter();
  child.pid = nextFakeSupervisorPid++;
  child.exitCode = null;
  child.signalCode = null;
  child.stdin = new PassThrough();
  child.stdout = new PassThrough();
  child.stderr = new PassThrough();
  const token = "owned-control-token";
  const sessionId = "owned-protocol-session";
  let buffered = "";
  const response = {
    send: (message) =>
      child.stdout.write(
        `${JSON.stringify({
          token,
          sessionId,
          protocolGeneration: terminalJobProtocolGeneration,
          ...message,
        })}\n`,
      ),
    sendRaw: (line) => child.stdout.write(`${line}\n`),
    closeOutput: () => child.stdout.end(),
    exit: (code = 0, { closeOutput = true } = {}) => {
      child.exitCode = code;
      child.emit("exit", code, null);
      if (closeOutput) child.stdout.end();
    },
  };
  child.stdin.on("data", (data) => {
    buffered += data.toString();
    const lines = buffered.split("\n");
    buffered = lines.pop();
    lines.filter(Boolean).forEach((line) => {
      const request = JSON.parse(line);
      respond(request, response);
    });
  });
  return {
    child,
    response,
    sessionId,
    supervisor: new TerminalJobSupervisor(child, token, 100),
  };
};

const startProtocolSupervisor = ({ child, sessionId, supervisor }) =>
  supervisor.start({
    sessionId,
    fileName: "ttyd.exe",
    argumentsList: [],
    workingDirectory: resolve(".agents"),
    environment: {},
    timeoutMs: 100,
  });

const terminationProtocol = (onTerminate) => {
  const protocol = protocolSupervisor((request, response) => {
    if (request.command === "start") {
      response.send({
        event: "ready",
        requestId: request.requestId,
        ttydPid: 601,
        supervisorPid: protocol.child.pid,
        supervisorStartTimeUtcTicks: "456",
        assignedBeforeResume: true,
        killOnJobClose: true,
        breakawayAllowed: false,
        silentBreakawayAllowed: false,
      });
    } else if (request.command === "terminate") {
      onTerminate(request, response);
    }
  });
  return protocol;
};

test("supervisor protocol preserves argv cwd env and validates job membership", async () => {
  const requests = [];
  const protocol = protocolSupervisor((request, response) => {
    const { child } = protocol;
    requests.push(request);
    if (request.command === "start") {
      response.send({
        event: "ready",
        requestId: request.requestId,
        ttydPid: 501,
        supervisorPid: child.pid,
        supervisorStartTimeUtcTicks: "123",
        assignedBeforeResume: true,
        killOnJobClose: true,
        breakawayAllowed: false,
        silentBreakawayAllowed: false,
      });
    } else if (request.command === "contains") {
      response.send({
        event: "contains",
        requestId: request.requestId,
        processId: request.processId,
        member: request.processId === 502,
      });
    }
  });

  await protocol.supervisor.start({
    sessionId: protocol.sessionId,
    fileName: "ttyd.exe",
    argumentsList: ["-w", "Q:\\path with spaces"],
    workingDirectory: "Q:\\path with spaces",
    environment: { TMTW: "Q:\\path with spaces" },
    timeoutMs: 100,
  });
  assert.equal(await protocol.supervisor.contains(502), true);
  assert.deepEqual(requests[0].arguments, ["-w", "Q:\\path with spaces"]);
  assert.equal(requests[0].workingDirectory, "Q:\\path with spaces");
  assert.equal(requests[0].environment.TMTW, "Q:\\path with spaces");
  assert.equal(requests[0].sessionId, protocol.sessionId);
  assert.equal(
    requests[0].protocolGeneration,
    terminalJobProtocolGeneration,
  );
});

test("supervisor termination requires empty acknowledgement and process exit", async () => {
  const commands = [];
  const protocol = protocolSupervisor((request, response) => {
    const { child } = protocol;
    commands.push(request.command);
    if (request.command === "start") {
      response.send({
        event: "ready",
        requestId: request.requestId,
        ttydPid: 601,
        supervisorPid: child.pid,
        supervisorStartTimeUtcTicks: "456",
        assignedBeforeResume: true,
        killOnJobClose: true,
        breakawayAllowed: false,
        silentBreakawayAllowed: false,
      });
    } else if (request.command === "terminate") {
      response.send({
        event: "terminated",
        requestId: request.requestId,
        empty: true,
      });
      setImmediate(() => response.exit());
    }
  });

  await startProtocolSupervisor(protocol);
  await Promise.all([
    protocol.supervisor.terminate(100),
    protocol.supervisor.terminate(100),
  ]);

  assert.deepEqual(commands, ["start", "terminate"]);
  assert.equal(protocol.supervisor.exited, true);
  assert.equal(
    protocol.supervisor.emptyAcknowledgementSource,
    "terminated",
  );
});

test("natural authenticated empty exit proves cleanup and tolerates duplicates", async () => {
  const protocol = terminationProtocol(() => {});

  await startProtocolSupervisor(protocol);
  const exited = {
    event: "exited",
    empty: true,
    rootExitCode: 0,
  };
  protocol.response.send(exited);
  protocol.response.send(exited);
  protocol.response.exit();
  await protocol.supervisor.terminate(100);

  assert.equal(protocol.supervisor.exited, true);
  assert.equal(protocol.supervisor.emptyAcknowledgementSource, "exited");
});

test("supervisor crash cannot substitute for empty-job proof", async () => {
  const protocol = terminationProtocol(() => {});
  await startProtocolSupervisor(protocol);
  protocol.response.exit(23);

  await assert.rejects(
    protocol.supervisor.terminate(100),
    /exited with code 23 without an authenticated empty-job acknowledgement/,
  );
});

test("forced supervisor exit while the job may be active fails cleanup", async () => {
  const protocol = terminationProtocol((_request, response) =>
    response.exit(),
  );
  await startProtocolSupervisor(protocol);

  await assert.rejects(
    protocol.supervisor.terminate(100),
    /supervisor exited with code 0/i,
  );
  assert.equal(protocol.supervisor.emptyAcknowledged, false);
});

test("spoofed supervisor identity cannot prove an empty job", async () => {
  const spoofs = [
    { token: "spoofed-control-token" },
    { sessionId: "spoofed-protocol-session" },
    { protocolGeneration: terminalJobProtocolGeneration + 1 },
  ];

  await Promise.all(
    spoofs.map(async (spoof) => {
      const protocol = terminationProtocol((_request, response) => {
        response.send({
          event: "exited",
          empty: true,
          rootExitCode: 0,
          ...spoof,
        });
        response.exit();
      });
      await startProtocolSupervisor(protocol);
      await assert.rejects(
        protocol.supervisor.terminate(100),
        /authentication changed/,
      );
      assert.equal(protocol.supervisor.emptyAcknowledged, false);
    }),
  );
});

test("malformed and empty-false exit events cannot prove cleanup", async () => {
  const cases = [
    {
      emit: (response) => response.sendRaw("{"),
      expected: /invalid JSON/,
    },
    {
      emit: (response) =>
        response.send({
          event: "exited",
          empty: false,
          rootExitCode: 0,
        }),
      expected: /invalid empty-job exit acknowledgement/,
    },
  ];

  await Promise.all(
    cases.map(async (testCase) => {
      const protocol = terminationProtocol((_request, response) => {
        testCase.emit(response);
        response.exit();
      });
      await startProtocolSupervisor(protocol);
      await assert.rejects(
        protocol.supervisor.terminate(100),
        testCase.expected,
      );
      assert.equal(protocol.supervisor.emptyAcknowledged, false);
    }),
  );
});

test("supervisor exit waits for a late buffered empty-job proof", async () => {
  const protocol = terminationProtocol((_request, response) => {
    response.exit(0, { closeOutput: false });
    setImmediate(() => {
      response.send({
        event: "exited",
        empty: true,
        rootExitCode: 0,
      });
      response.closeOutput();
    });
  });
  await startProtocolSupervisor(protocol);

  await protocol.supervisor.terminate(100);

  assert.equal(protocol.supervisor.exited, true);
  assert.equal(protocol.supervisor.emptyAcknowledgementSource, "exited");
});

test("supervisor termination times out without empty proof or exit", async () => {
  const protocol = terminationProtocol(() => {});
  await startProtocolSupervisor(protocol);

  await assert.rejects(
    protocol.supervisor.terminate(20),
    /Timed out waiting for (?:terminal Job Object supervisor terminate acknowledgement|authenticated empty-job acknowledgement and supervisor exit)/,
  );
  assert.equal(protocol.supervisor.emptyAcknowledged, false);
  protocol.response.exit();
});

test("close waits for empty acknowledgement and supervisor exit before registry removal", async () => {
  let releaseTermination;
  const terminateGate = new Promise((resolveTermination) => {
    releaseTermination = resolveTermination;
  });
  const supervisor = new FakeJobSupervisor({ terminateGate });

  await withTestHost({}, async (host) => {
    const session = ownedSession("acknowledged", 101, 102, supervisor);
    host.sessions.set(session.id, session);
    const closing = host.closeSession(session, "test");
    await new Promise((resolveImmediate) => setImmediate(resolveImmediate));

    assert.equal(host.sessions.get(session.id), session);
    assert.equal(supervisor.terminateCalls, 1);

    releaseTermination();
    await closing;
    assert.equal(host.sessions.has(session.id), false);
  });
});

test("reservation is granted only after authenticated empty proof and exit", async () => {
  const proven = terminationProtocol((request, response) => {
    response.send({
      event: "terminated",
      requestId: request.requestId,
      empty: true,
    });
    response.exit();
  });
  const unproven = terminationProtocol((_request, response) =>
    response.exit(),
  );
  await Promise.all([
    startProtocolSupervisor(proven),
    startProtocolSupervisor(unproven),
  ]);

  await withTestHost({}, async (host) => {
    host.cleanupTimeouts = { graceful: 0, forced: 100 };
    const provenPath = resolve(".agents");
    const unprovenPath = resolve("scripts");
    const key = (path) =>
      process.platform === "win32" ? path.toLowerCase() : path;
    const provenSession = {
      ...ownedSession("reservation-proven", 601, 602, proven.supervisor),
      worktreePath: provenPath,
      key: key(provenPath),
    };
    host.sessions.set(provenSession.id, provenSession);

    const reservation = await host.reserveWorktree(provenPath);
    assert.equal(host.sessions.has(provenSession.id), false);
    assert.equal(host.reservations.size, 1);
    await host.releaseReservation(reservation.id);

    const unprovenSession = {
      ...ownedSession(
        "reservation-unproven",
        701,
        702,
        unproven.supervisor,
      ),
      worktreePath: unprovenPath,
      key: key(unprovenPath),
    };
    host.sessions.set(unprovenSession.id, unprovenSession);

    await assert.rejects(
      host.reserveWorktree(unprovenPath),
      /without an authenticated empty-job acknowledgement/,
    );
    assert.equal(host.sessions.get(unprovenSession.id), unprovenSession);
    assert.equal(host.reservations.size, 0);
  });
});

test("termination timeout retains a failed retryable session", async () => {
  let failTermination = true;
  const supervisor = new FakeJobSupervisor();
  supervisor.terminate = async function () {
    this.terminateCalls += 1;
    if (failTermination) throw new Error("empty acknowledgement timed out");
    this.exited = true;
    this.child.exit();
  };

  await withTestHost({}, async (host) => {
    const session = ownedSession("retry", 201, 202, supervisor);
    host.sessions.set(session.id, session);

    await assert.rejects(
      host.closeSession(session, "first"),
      /acknowledgement timed out/,
    );
    assert.equal(host.sessions.get(session.id), session);
    assert.equal(session.state, "failed");

    failTermination = false;
    await host.closeSession(session, "retry");
    assert.equal(host.sessions.has(session.id), false);
  });
});

test("closing one session never terminates another session job", async () => {
  const closingSupervisor = new FakeJobSupervisor();
  const retainedSupervisor = new FakeJobSupervisor();

  await withTestHost({}, async (host) => {
    const closing = ownedSession(
      "closing",
      301,
      302,
      closingSupervisor,
    );
    const retained = ownedSession(
      "retained",
      401,
      402,
      retainedSupervisor,
    );
    host.sessions.set(closing.id, closing);
    host.sessions.set(retained.id, retained);

    await host.closeSession(closing, "test");

    assert.equal(closingSupervisor.terminateCalls, 1);
    assert.equal(retainedSupervisor.terminateCalls, 0);
    assert.equal(host.sessions.get(retained.id), retained);
  });
});

test("supervisor pipe loss rejects startup and closes the ownership boundary", async () => {
  const { response, sessionId, supervisor } = protocolSupervisor(
    () => {},
  );
  const starting = supervisor.start({
    sessionId,
    fileName: "ttyd.exe",
    argumentsList: [],
    workingDirectory: resolve(".agents"),
    environment: {},
    timeoutMs: 100,
  });
  response.exit(1);

  await assert.rejects(starting, /supervisor exited/);
  await assert.rejects(
    supervisor.terminate(100),
    /without an authenticated empty-job acknowledgement/,
  );
  assert.equal(supervisor.exited, true);
});

test("startup failure stays failed after authoritative supervisor cleanup", async () => {
  const supervisor = new FakeJobSupervisor({
    startError: new Error("assign-before-resume failed"),
  });

  await withTestHost(
    { supervisorFactory: () => supervisor },
    async (host) => {
      const session = await host.startSession(resolve(".agents"));

      assert.equal(session.state, "failed");
      assert.equal(supervisor.terminateCalls, 1);
      assert.equal(host.sessions.get(session.id), session);
    },
  );
});

test("shell PID is accepted only when the Job Object reports membership", async () => {
  const stateDirectory = testStateDirectory();
  mkdirSync(stateDirectory, { recursive: true });
  const supervisor = new FakeJobSupervisor({ members: new Set([709]) });

  await withTestHost({}, async (host) => {
    const session = {
      ...ownedSession("shell-membership", 708, null, supervisor),
      pidFile: resolve(stateDirectory, "shell.pid"),
    };
    writeFileSync(session.pidFile, "709\n", "utf8");

    assert.equal(await host.waitForShellPid(session), 709);
    assert.deepEqual(supervisor.containsCalls, [709]);
  });
  rmSync(stateDirectory, { recursive: true, force: true });
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
    ["failed supervisor", (session) => (session.jobSupervisor.exited = true)],
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

test("shutdown retains a session whose Job Object does not acknowledge empty", async () => {
  let failTermination = true;
  const supervisor = new FakeJobSupervisor();
  supervisor.terminate = async function () {
    this.terminateCalls += 1;
    if (failTermination) throw new Error("Job Object acknowledgement timed out");
    this.exited = true;
    this.child.exit();
  };

  await withTestHost({}, async (host, exits) => {
    await host.start();
    const session = ownedSession(
      "shutdown-failure",
      901,
      null,
      supervisor,
    );
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

    failTermination = false;
    await host.shutdown("test-cleanup");
    assert.deepEqual(exits, [0]);
  });
});
