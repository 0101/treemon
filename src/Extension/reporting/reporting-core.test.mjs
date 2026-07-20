import test from "node:test";
import assert from "node:assert/strict";
import { buildMessageReport, buildTitleBootstrapReport } from "./reporting-core.mjs";

const context = {
  sessionId: "session-1",
  worktreePath: "worktree",
  provider: "copilot_cli",
  eventId: "event-1",
  occurredAt: "2026-07-20T12:31:02.493Z",
};

test("metadata summary maps to title_bootstrap without a live title event", () => {
  assert.deepEqual(buildTitleBootstrapReport({ summary: "Investigate Intent Title Runtime" }, context), {
    sessionId: "session-1",
    worktreePath: "worktree",
    provider: "copilot_cli",
    eventId: "event-1",
    occurredAt: "2026-07-20T12:31:02.493Z",
    kind: "title_bootstrap",
    message: {
      text: "Investigate Intent Title Runtime",
      at: "2026-07-20T12:31:02.493Z",
    },
  });
});

test("blank metadata summary emits no title report", () => {
  assert.equal(buildTitleBootstrapReport({ summary: "   " }, context), null);
  assert.equal(buildTitleBootstrapReport({}, context), null);
});

test("live and bootstrap messages share the canonical report shape", () => {
  assert.deepEqual(buildMessageReport(context, "title_reported", "Live title"), {
    sessionId: "session-1",
    worktreePath: "worktree",
    provider: "copilot_cli",
    eventId: "event-1",
    occurredAt: "2026-07-20T12:31:02.493Z",
    kind: "title_reported",
    message: {
      text: "Live title",
      at: "2026-07-20T12:31:02.493Z",
    },
  });
});
