import { promptForSession } from "../../Extension/session-prompt.mjs";
import { createSendQueue } from "../../Extension/send-queue.mjs";

process.stdin.setEncoding("utf8");
let body = "";
for await (const chunk of process.stdin) body += chunk;
const { kind, prompt } = promptForSession(body);
await new Promise((resolve) => {
  const fakeSession = {
    send(message) {
      process.stdout.write(JSON.stringify(message));
      resolve();
    },
  };
  createSendQueue().enqueue(fakeSession, kind, prompt);
});
