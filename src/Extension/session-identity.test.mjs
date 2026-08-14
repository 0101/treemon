import test from "node:test";
import assert from "node:assert/strict";
import { sessionIdFrom } from "./session-identity.mjs";
import { sessionIdFrom as reportingSessionIdFrom } from "./reporting/session-identity.mjs";

test("session identity prefers the current SDK property and trims it", () => {
  assert.equal(
    sessionIdFrom({ sessionId: " current-session ", id: "legacy-session" }),
    "current-session",
  );
});

test("session identity retains the older id compatibility shape", () => {
  assert.equal(sessionIdFrom({ sessionId: null, id: " legacy-session " }), "legacy-session");
  assert.equal(reportingSessionIdFrom({ id: "reporting-session" }), "reporting-session");
});

test("session identity rejects missing, blank, and non-string values", () => {
  assert.equal(sessionIdFrom(null), undefined);
  assert.equal(sessionIdFrom({}), undefined);
  assert.equal(sessionIdFrom({ sessionId: "   ", id: "not-used" }), undefined);
  assert.equal(sessionIdFrom({ sessionId: 42 }), undefined);
});
