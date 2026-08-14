import test from "node:test";
import assert from "node:assert/strict";
import { readFileSync } from "node:fs";

function json(relativeUrl) {
  return JSON.parse(readFileSync(new URL(relativeUrl, import.meta.url), "utf8"));
}

test("extension packages and static checking use one pinned Copilot SDK version", () => {
  const root = json("../../package.json");
  const canvas = json("./package.json");
  const reporting = json("./reporting/package.json");

  assert.equal(canvas.dependencies["@github/copilot-sdk"], "1.0.9");
  assert.equal(reporting.dependencies["@github/copilot-sdk"], "1.0.9");
  assert.equal(root.devDependencies["@github/copilot-sdk"], "1.0.9");
});

test("reporting installation replaces its source wrapper with the canonical session adapter", () => {
  const installer = readFileSync(new URL("../../treemon.ps1", import.meta.url), "utf8");

  assert.match(
    installer,
    /Copy-Item \(Join-Path \$PSScriptRoot "src" "Extension" "session-identity\.mjs"\) \$dest -Force/,
  );
});
