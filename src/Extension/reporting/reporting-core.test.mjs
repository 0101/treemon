import test from "node:test";
import assert from "node:assert/strict";
import { buildMetadataTitleReport, loadMetadataTitleReport } from "./reporting-core.mjs";

const context = {
  sessionId: "session-1",
  worktreePath: "worktree",
  provider: "copilot_cli",
  eventId: "event-1",
  occurredAt: "2026-07-20T12:31:02.493Z",
};

test("metadata summary maps to title_reported without a live title event", async () => {
  const session = {
    rpc: {
      metadata: {
        snapshot: async () => ({ summary: "Investigate Intent Title Runtime" }),
      },
    },
  };

  assert.deepEqual(await loadMetadataTitleReport(session, context), {
    sessionId: "session-1",
    worktreePath: "worktree",
    provider: "copilot_cli",
    eventId: "event-1",
    occurredAt: "2026-07-20T12:31:02.493Z",
    kind: "title_reported",
    message: {
      text: "Investigate Intent Title Runtime",
      at: "2026-07-20T12:31:02.493Z",
    },
  });
});

test("blank metadata summary emits no title report", () => {
  assert.equal(buildMetadataTitleReport({ summary: "   " }, context), null);
  assert.equal(buildMetadataTitleReport({}, context), null);
});
