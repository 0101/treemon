export function promptForSession(body) {
  let transport;
  try {
    transport = JSON.parse(body);
  } catch {
    throw new Error("invalid JSON");
  }

  if (typeof transport?.prompt !== "string") {
    throw new Error("missing prompt");
  }

  switch (transport.kind) {
    case "canvas":
      return `[canvas] ${transport.prompt}`;
    case "agent-prompt":
      return transport.prompt;
    default:
      throw new Error("unknown prompt kind");
  }
}
