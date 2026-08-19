import assert from "node:assert/strict";
import { test } from "node:test";
import {
  hostStateMatchesObservation,
  stopObservedHost,
} from "./durable-terminal-observation.mjs";

const originalIdentity = {
  pid: 4101,
  parentPid: 1,
  startIdentity: "test:4101:original",
};

const observation = {
  hostProtocolVersion: 3,
  hostGeneration: "observed-generation",
  hostBundleHash: "a".repeat(64),
  hostScriptHash: "b".repeat(64),
  supervisorScriptHash: "c".repeat(64),
  processIdentityHelperHash: "d".repeat(64),
  supervisorProtocolGeneration: 2,
  hostCapabilities: [
    "immutable-runtime-bundle-v1",
    "strict-evidence-paths-v1",
    "trusted-empty-supervisor-v1",
  ],
  hostPid: 4101,
  hostProcessStartTicks: "100",
  hostProcessStartExact: true,
  hostProcessIdentity: originalIdentity,
  hostStartedAt: "2026-08-18T12:00:00.000Z",
  controlPort: 61234,
  controlToken: "observed-credential",
};

const hostState = {
  version: 3,
  generation: observation.hostGeneration,
  bundleHash: observation.hostBundleHash,
  hostScriptHash: observation.hostScriptHash,
  supervisorScriptHash: observation.supervisorScriptHash,
  processIdentityHelperHash: observation.processIdentityHelperHash,
  supervisorProtocolGeneration: observation.supervisorProtocolGeneration,
  capabilities: observation.hostCapabilities,
  pid: observation.hostPid,
  processStartTicks: observation.hostProcessStartTicks,
  processStartExact: observation.hostProcessStartExact,
  startedAt: observation.hostStartedAt,
  controlPort: observation.controlPort,
  controlToken: observation.controlToken,
};

const stoppingDependencies = (actualIdentity) => {
  const calls = [];
  return {
    calls,
    dependencies: {
      inspectProcess: async () => actualIdentity,
      sendShutdown: async () => calls.push("shutdown"),
      waitForExit: async () => calls.push("wait"),
    },
  };
};

test("replacement manifest is reported without shutdown or PID wait", async () => {
  const replacement = {
    ...hostState,
    generation: "replacement-generation",
  };
  const { calls, dependencies } = stoppingDependencies(originalIdentity);

  const result = await stopObservedHost(
    observation,
    replacement,
    dependencies,
  );

  assert.equal(hostStateMatchesObservation(observation, replacement), false);
  assert.deepEqual(calls, []);
  assert.deepEqual(result, {
    shutdownSent: false,
    stopped: false,
    ownershipChanged: true,
    reason:
      "Current host manifest runtime, process identity, or credentials differ from the observation",
  });
});

test("changed host credential is never used to stop the endpoint", async () => {
  const changedCredential = {
    ...hostState,
    controlToken: "replacement-credential",
  };
  const { calls, dependencies } = stoppingDependencies(originalIdentity);

  const result = await stopObservedHost(
    observation,
    changedCredential,
    dependencies,
  );

  assert.equal(result.shutdownSent, false);
  assert.deepEqual(calls, []);
});

test("changed runtime bundle is never treated as the observed host", async () => {
  const changedBundle = {
    ...hostState,
    bundleHash: "e".repeat(64),
  };
  const { calls, dependencies } = stoppingDependencies(originalIdentity);

  const result = await stopObservedHost(
    observation,
    changedBundle,
    dependencies,
  );

  assert.equal(hostStateMatchesObservation(observation, changedBundle), false);
  assert.equal(result.ownershipChanged, true);
  assert.deepEqual(calls, []);
});

test("reused PID is never sent shutdown or waited on", async () => {
  const reusedIdentity = {
    ...originalIdentity,
    startIdentity: "test:4101:replacement",
  };
  const { calls, dependencies } = stoppingDependencies(reusedIdentity);

  const result = await stopObservedHost(
    observation,
    hostState,
    dependencies,
  );

  assert.equal(result.shutdownSent, false);
  assert.equal(result.ownershipChanged, true);
  assert.match(result.reason, /process creation identity/);
  assert.deepEqual(calls, []);
});

test("exact observed owner receives shutdown and identity-scoped wait", async () => {
  const calls = [];
  let inspected = originalIdentity;
  const result = await stopObservedHost(observation, hostState, {
    inspectProcess: async () => inspected,
    sendShutdown: async (state) => {
      calls.push(["shutdown", state.controlToken]);
      inspected = null;
    },
    waitForExit: async (predicate) => {
      calls.push(["wait", await predicate()]);
    },
  });

  assert.deepEqual(calls, [
    ["shutdown", observation.controlToken],
    ["wait", true],
  ]);
  assert.deepEqual(result, {
    shutdownSent: true,
    stopped: true,
    ownershipChanged: false,
    reason: null,
  });
});
