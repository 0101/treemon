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

test("verifier cleanup deletes tracked terminals before stopping TerminalHost", async () => {
  const events = [];
  await cleanupRuntimeResources({
    terminalSessionId: "tracked-terminal",
    browser: {
      close: async () => events.push("browser"),
    },
    dashboard: {
      terminate: async () => events.push("dashboard-stopped"),
    },
    host: {
      deleteTerminal: async (sessionId) => {
        events.push(`deleted:${sessionId}`);
        return { terminals: [] };
      },
      terminate: async () => events.push("host-stopped"),
    },
  });

  assert.deepEqual(events, [
    "browser",
    "dashboard-stopped",
    "deleted:tracked-terminal",
    "host-stopped",
  ]);
});

test("cleanup attempts every resource and surfaces every failure", async () => {
  const events = [];

  await assert.rejects(
    cleanupRuntimeResources({
      terminalSessionId: "remains-present",
      browser: {
        close: async () => {
          events.push("browser");
          throw new Error("browser close failed");
        },
      },
      dashboard: {
        terminate: async () => {
          events.push("dashboard");
          throw new Error("dashboard close failed");
        },
      },
      host: {
        deleteTerminal: async (sessionId) => {
          events.push(`delete:${sessionId}`);
          return { terminals: [{ sessionId }] };
        },
        terminate: async () => {
          events.push("host");
          throw new Error("host did not stop");
        },
      },
    }),
    (error) => {
      assert.match(error.message, /browser close failed/);
      assert.match(error.message, /dashboard close failed/);
      assert.match(error.message, /left terminal remains-present in the registry/);
      assert.match(error.message, /host did not stop/);
      return true;
    },
  );

  assert.deepEqual(events, [
    "browser",
    "dashboard",
    "delete:remains-present",
    "host",
  ]);
});

test("verification dashboard rejects an unexpected Host without exposing the terminal token", async () => {
  const dashboard = await launchDashboardServer();
  const terminalEndpoint =
    "http://127.0.0.1:61234/_treemon/session/sensitive-token/";
  dashboard.setTerminalEndpoint(terminalEndpoint);

  try {
    assert.notEqual(new URL(dashboard.origin).port, "5000");
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
