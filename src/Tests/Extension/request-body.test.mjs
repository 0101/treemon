import test from "node:test";
import assert from "node:assert/strict";
import { Readable } from "node:stream";
import { readBody } from "../../Extension/request-body.mjs";

test("request bodies use the one MiB default cap", async () => {
  const accepted = "a".repeat(1024 * 1024);

  assert.equal(await readBody(Readable.from([accepted])), accepted);
  await assert.rejects(
    readBody(Readable.from([accepted + "a"])),
    /body too large/,
  );
});

test("request body reading rejects stream errors", async () => {
  const request = new Readable({
    read() {
      this.destroy(new Error("connection reset"));
    },
  });

  await assert.rejects(readBody(request), /connection reset/);
});
