import assert from "node:assert/strict";
import { test } from "node:test";
import { cleanupRuntimeResources } from "./verify-ttyd-runtime.mjs";

test("verifier cleanup closes browser and authoritative job boundary", async () => {
  const events = [];
  await cleanupRuntimeResources({
    browser: {
      close: async () => events.push("browser"),
    },
    supervisor: {
      terminate: async () => events.push("job-empty"),
    },
  });

  assert.deepEqual(events, ["browser", "job-empty"]);
});

test("browser cleanup failure cannot skip Job Object termination", async () => {
  let terminated = false;

  await assert.rejects(
    cleanupRuntimeResources({
      browser: {
        close: async () => {
          throw new Error("browser close failed");
        },
      },
      supervisor: {
        terminate: async () => {
          terminated = true;
        },
      },
    }),
    /browser close failed/,
  );

  assert.equal(terminated, true);
});

test("job acknowledgement failure is not hidden by successful browser cleanup", async () => {
  await assert.rejects(
    cleanupRuntimeResources({
      browser: { close: async () => {} },
      supervisor: {
        terminate: async () => {
          throw new Error("job did not become empty");
        },
      },
    }),
    /job did not become empty/,
  );
});
