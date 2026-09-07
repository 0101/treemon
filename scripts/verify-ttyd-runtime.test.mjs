import assert from "node:assert/strict";
import { request } from "node:http";
import { test } from "node:test";
import {
  cleanupRuntimeResources,
  launchDashboardServer,
} from "./verify-ttyd-runtime.mjs";

function requestWithHost(url, host) {
  return new Promise((resolve, reject) => {
    const pending = request(url, { headers: { Host: host } }, (response) => {
      let body = "";
      response.setEncoding("utf8");
      response.on("data", (chunk) => {
        body += chunk;
      });
      response.on("end", () =>
        resolve({ status: response.statusCode, body }),
      );
    });
    pending.on("error", reject);
    pending.end();
  });
}

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

test("verification dashboard rejects an unexpected Host without exposing the terminal token", async () => {
  const dashboard = await launchDashboardServer();
  const terminalEndpoint =
    "http://127.0.0.1:61234/_treemon/session/sensitive-token/";
  dashboard.setTerminalEndpoint(terminalEndpoint);

  try {
    const accepted = await fetch(dashboard.origin);
    assert.equal(accepted.status, 200);
    assert.match(await accepted.text(), /sensitive-token/);

    const rejected = await requestWithHost(
      dashboard.origin,
      "attacker.example",
    );
    assert.equal(rejected.status, 400);
    assert.doesNotMatch(rejected.body, /sensitive-token/);
  } finally {
    await dashboard.terminate();
  }
});
