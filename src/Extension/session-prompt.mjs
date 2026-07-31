// Returns the transport kind alongside the session prompt text, so the send queue can compare exact
// payloads without re-parsing the body or conflating a canvas payload with a same-text agent prompt.
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
      return { kind: transport.kind, prompt: `[canvas] ${transport.prompt}` };
    case "agent-prompt":
      return { kind: transport.kind, prompt: transport.prompt };
    default:
      throw new Error("unknown prompt kind");
  }
}
