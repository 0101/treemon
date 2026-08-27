import test from "node:test";
import assert from "node:assert/strict";
import {
  isLoopbackOrigin,
  isTrustedInjectionHeaders,
} from "../../Extension/injection-request.mjs";

test("loopback origins cover the supported local host forms", () => {
  assert.equal(isLoopbackOrigin("http://127.0.0.1:5000"), true);
  assert.equal(isLoopbackOrigin("https://127.12.34.56"), true);
  assert.equal(isLoopbackOrigin("http://localhost:5002"), true);
  assert.equal(isLoopbackOrigin("http://[::1]:5174"), true);
  assert.equal(isLoopbackOrigin("https://example.com"), false);
});

test("injection headers require JSON and reject non-loopback origins", () => {
  assert.equal(isTrustedInjectionHeaders({
    "content-type": "application/json; charset=utf-8",
  }), true);
  assert.equal(isTrustedInjectionHeaders({
    "content-type": "application/json",
    origin: "http://localhost:5002",
  }), true);
  assert.equal(isTrustedInjectionHeaders({
    "content-type": "text/plain",
  }), false);
  assert.equal(isTrustedInjectionHeaders({
    "content-type": "application/json",
    origin: "https://example.com",
  }), false);
  assert.equal(isTrustedInjectionHeaders({
    "content-type": ["application/json", "text/plain"],
  }), false);
  assert.equal(isTrustedInjectionHeaders({
    "content-type": "application/json",
    origin: ["http://localhost:5002", "https://example.com"],
  }), false);
  assert.equal(isTrustedInjectionHeaders({}), false);
});
