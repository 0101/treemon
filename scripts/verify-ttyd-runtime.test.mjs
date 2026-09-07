import assert from "node:assert/strict";
import { test } from "node:test";
import { cleanupRuntimeResources } from "./verify-ttyd-runtime.mjs";

test("verifier cleanup closes browser, dashboard, and isolated TerminalHost", async () => {
  const events = [];
  await cleanupRuntimeResources({
    browser: {
      close: async () => events.push("browser"),
    },
    dashboard: {
      terminate: async () => events.push("dashboard-stopped"),
    },
    host: {
      terminate: async () => events.push("host-stopped"),
    },
  });

  assert.deepEqual(events, ["browser", "dashboard-stopped", "host-stopped"]);
});

test("browser cleanup failure cannot skip dashboard or TerminalHost termination", async () => {
  let dashboardTerminated = false;
  let terminated = false;

  await assert.rejects(
    cleanupRuntimeResources({
      browser: {
        close: async () => {
          throw new Error("browser close failed");
        },
      },
      dashboard: {
        terminate: async () => {
          dashboardTerminated = true;
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

  assert.equal(dashboardTerminated, true);
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
