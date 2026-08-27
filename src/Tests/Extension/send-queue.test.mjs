import test from "node:test";
import assert from "node:assert/strict";
import { createSendQueue } from "../../Extension/send-queue.mjs";

// session.send stays pending until its gate is released, so a payload can be observed while it is
// still queued (never started) versus already in flight.
function gatedSession() {
  const sent = [];
  const gates = [];
  const session = {
    send({ prompt }) {
      sent.push(prompt);
      return new Promise((resolve, reject) => gates.push({ resolve, reject }));
    },
  };
  return { session, sent, gates };
}

const tick = () => new Promise((resolve) => setImmediate(resolve));

async function drain(gates) {
  for (let index = 0; index < gates.length; index += 1) {
    gates[index].resolve();
    await tick();
  }
}

test("enqueue defers delivery so the caller can answer without awaiting session.send", () => {
  const { session, sent } = gatedSession();
  const queue = createSendQueue();

  assert.equal(queue.enqueue(session, "agent-prompt", "sync"), true);
  assert.deepEqual(sent, []);
});

test("identical payloads coalesce only while they are still queued", async () => {
  const { session, sent, gates } = gatedSession();
  const queue = createSendQueue();

  queue.enqueue(session, "canvas", "[canvas] {\"action\":\"refresh\"}");
  await tick();
  assert.deepEqual(sent, ["[canvas] {\"action\":\"refresh\"}"]);

  // Queued behind the in-flight send: the second copy is skipped.
  assert.equal(queue.enqueue(session, "agent-prompt", "sync"), true);
  assert.equal(queue.enqueue(session, "agent-prompt", "sync"), false);

  gates[0].resolve();
  await tick();
  assert.deepEqual(sent, ["[canvas] {\"action\":\"refresh\"}", "sync"]);

  // The queued copy has started sending, so the same payload is eligible again.
  assert.equal(queue.enqueue(session, "agent-prompt", "sync"), true);
  await drain(gates);
  assert.deepEqual(sent, ["[canvas] {\"action\":\"refresh\"}", "sync", "sync"]);
});

test("payloads differing in kind or text stay distinct", async () => {
  const { session, sent, gates } = gatedSession();
  const queue = createSendQueue();

  queue.enqueue(session, "agent-prompt", "blocker");
  await tick();

  assert.equal(queue.enqueue(session, "canvas", "same text"), true);
  assert.equal(queue.enqueue(session, "agent-prompt", "same text"), true);
  assert.equal(queue.enqueue(session, "canvas", "other text"), true);
  assert.equal(queue.enqueue(session, "canvas", "same text"), false);

  await drain(gates);
  assert.deepEqual(sent, ["blocker", "same text", "same text", "other text"]);
});

test("a rejected send leaves no stale pending key", async () => {
  const { session, sent, gates } = gatedSession();
  const failures = [];
  const queue = createSendQueue({ log: (message) => failures.push(message) });

  queue.enqueue(session, "agent-prompt", "retry me");
  await tick();
  gates[0].reject(new Error("bridge closed"));
  await tick();

  assert.equal(queue.enqueue(session, "agent-prompt", "retry me"), true);
  await drain(gates);

  assert.deepEqual(sent, ["retry me", "retry me"]);
  assert.ok(failures.some((message) => message.includes("bridge closed")));
});
