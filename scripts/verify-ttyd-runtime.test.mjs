import assert from "node:assert/strict";
import { test } from "node:test";
import { cleanupRuntimeResources } from "./verify-ttyd-runtime.mjs";

test("verifier cleanup closes browser and isolated TerminalHost", async () => {
  const events = [];
  await cleanupRuntimeResources({
    browser: {
      close: async () => events.push("browser"),
    },
    host: {
      terminate: async () => events.push("host-stopped"),
    },
  });

  assert.deepEqual(events, ["browser", "host-stopped"]);
});

test("browser cleanup failure cannot skip TerminalHost termination", async () => {
  let terminated = false;

  await assert.rejects(
    cleanupRuntimeResources({
      browser: {
        close: async () => {
          throw new Error("browser close failed");
        },
      },
      host: {
        terminate: async () => {
          terminated = true;
        },
      },
    }),
    /browser close failed/,
  );

  assert.equal(terminated, true);
});

test("host shutdown failure is not hidden by successful browser cleanup", async () => {
  await assert.rejects(
    cleanupRuntimeResources({
      browser: { close: async () => {} },
      host: {
        terminate: async () => {
          throw new Error("host did not stop");
        },
      },
    }),
    /host did not stop/,
  );
});
