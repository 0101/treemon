import assert from "node:assert/strict";
import { test } from "node:test";
import { isolatedTreemonEnvironment } from "./durable-terminal-verifier-env.mjs";

test("isolated verifier overrides both reporting ports and state paths", () => {
  const environment = isolatedTreemonEnvironment(
    {
      TREEMON_PORT: "5000",
      TREEMON_PORTS: "5000,5001",
      TREEMON_CONFIG_DIR: "inherited-config",
      TREEMON_TERMINAL_STATE_DIR: "inherited-state",
      PRESERVED: "value",
    },
    {
      apiPort: 43123,
      configDirectory: "isolated-config-path",
      terminalStateDirectory: "isolated-terminal-state-path",
    },
  );

  assert.equal(environment.TREEMON_PORT, "43123");
  assert.equal(environment.TREEMON_PORTS, "43123");
  assert.equal(environment.TREEMON_CONFIG_DIR, "isolated-config-path");
  assert.equal(
    environment.TREEMON_TERMINAL_STATE_DIR,
    "isolated-terminal-state-path",
  );
  assert.equal(environment.PRESERVED, "value");
});
