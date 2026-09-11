import test from "node:test";
import assert from "node:assert/strict";
import { Readable } from "node:stream";
import {
  createShutdownHandler,
  isLoopbackRemoteAddress,
} from "../../Extension/shutdown-endpoint.mjs";

function request(capability, remoteAddress = "127.0.0.1") {
  const req = Readable.from([JSON.stringify({ capability })]);
  req.method = "POST";
  req.url = "/shutdown";
  req.headers = { "content-type": "application/json" };
  req.socket = { remoteAddress };
  return req;
}

function response(order = []) {
  return {
    status: 0,
    body: "",
    writeHead(status) {
      this.status = status;
      order.push(`status:${status}`);
    },
    end(body, onFinished) {
      this.body = body;
      order.push("acknowledged");
      onFinished?.();
    },
  };
}

test("shutdown endpoint accepts every loopback address form the listener may expose", () => {
  assert.equal(isLoopbackRemoteAddress("127.0.0.1"), true);
  assert.equal(isLoopbackRemoteAddress("127.23.4.5"), true);
  assert.equal(isLoopbackRemoteAddress("::1"), true);
  assert.equal(isLoopbackRemoteAddress("::ffff:127.0.0.1"), true);
  assert.equal(isLoopbackRemoteAddress("10.0.0.1"), false);
  assert.equal(isLoopbackRemoteAddress(undefined), false);
});

test("shutdown endpoint rejects a non-loopback caller before checking the capability", async () => {
  let invoked = false;
  const handler = createShutdownHandler({
    capability: "expected",
    shutdown: async () => { invoked = true; },
  });
  const res = response();

  assert.equal(await handler(request("expected", "10.0.0.5"), res), true);
  assert.equal(res.status, 403);
  assert.equal(invoked, false);
});

test("shutdown endpoint rejects an invalid capability without invoking the SDK", async () => {
  let invoked = false;
  const handler = createShutdownHandler({
    capability: "expected",
    shutdown: async () => { invoked = true; },
  });
  const res = response();

  assert.equal(await handler(request("wrong"), res), true);
  assert.equal(res.status, 401);
  assert.equal(invoked, false);
});

test("shutdown endpoint rejects a valid request when routine shutdown is unavailable", async () => {
  const handler = createShutdownHandler({ capability: "expected" });
  const res = response();

  assert.equal(await handler(request("expected"), res), true);
  assert.equal(res.status, 409);
});

test("shutdown endpoint acknowledges a valid request before invoking routine shutdown", async () => {
  const order = [];
  let scheduled;
  const handler = createShutdownHandler({
    capability: "expected",
    shutdown: async (value) => {
      assert.deepEqual(value, { type: "routine" });
      order.push("invoked");
    },
    schedule(callback) {
      order.push("scheduled");
      scheduled = callback;
    },
  });
  const res = response(order);

  assert.equal(await handler(request("expected"), res), true);
  assert.equal(res.status, 202);
  assert.deepEqual(JSON.parse(res.body), { accepted: true });
  assert.deepEqual(order, ["status:202", "acknowledged", "scheduled"]);

  scheduled();
  await Promise.resolve();
  assert.deepEqual(order, ["status:202", "acknowledged", "scheduled", "invoked"]);
});
