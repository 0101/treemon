import { joinSession } from "@github/copilot-sdk/extension";
import { randomUUID } from "node:crypto";
import { buildNonBlankMessageReport, buildReport } from "./reporting-core.mjs";

// treemon-reporting — the passive, reporting-only extension (Phase 1 of the push status model).
//
// It ONLY subscribes to the SDK session event stream and forwards a tiny closed set of status
// events to Treemon over HTTP (POST /api/session/activity). It is deliberately orthogonal to the
// canvas-bridge extension: it registers NO tools and NO canvas, never calls session.send, and never
// injects context — so the agent transcript is identical with reporting on or off, and there is no
// canvas_take_ownership tool collision when both extensions load in the same session.
//
// Source-metadata filtering lives HERE, while lifecycle state stays in the server's pure fold:
//   * sub-agent events (any event carrying `agentId`) are dropped;
//   * a skill's own `<skill-context>` injection (a `user.message` tagged `source: skill-*`) is dropped;
//   * runtime `<system_reminder>` user-channel messages are classified by the server;
//   * ask_user request/completion and session.idle are forwarded as facts for the server to resolve;
//   * only the relevant SDK event types are mapped — everything else is ignored.
//
// The wire contract (the single coupling point with the F# handler, Server/SessionActivityService.fs):
//   { sessionId, worktreePath, provider, eventId, occurredAt, kind, message?, skillName?, currentTokens?, tokenLimit? }
// where `kind` is one of the closed set mapped 1:1 onto the server's SessionEvent union:
//   assistant.turn_start   -> turn_started
//   user.message           -> user_prompt         (message required; server drops system reminders)
//   assistant.message      -> assistant_message   (message required)
//   skill.invoked          -> skill_invoked        (skillName required)
//   elicitation.requested / user_input.requested -> awaiting_user_input (message = the ask_user question, optional)
//   elicitation.completed / user_input.completed -> user_input_completed
//   assistant.intent       -> intent_reported     (message = the intent text, required)
//   session.title_changed  -> title_reported      (message = the session title, required)
//   metadata.snapshot      -> title_bootstrap     (message = the persisted session summary, required)
//   assistant.turn_end     -> turn_ended
//   session.idle           -> went_idle
//   session.usage_info     -> usage_info           (currentTokens + tokenLimit; a status-preserving context-window gauge)
//   (timer, no SDK event)  -> heartbeat            (liveness only; bumps last_seen, never folded/stored)
// `message` is { text, at }; the server truncates for display, so raw text is forwarded (bounded by
// MAX_MESSAGE_CHARS only to keep the POST body sane). An unknown kind is rejected server-side, so
// this file is the authoritative producer of the contract.

const PROVIDER = "copilot_cli";

// Fan-out target ports. A single instance by default; a comma-separated TREEMON_PORTS lets one
// session feed several Treemon instances at once (side-by-side validation posts to the main and the
// new push-only instance so both dashboards see the same sessions). A non-owning instance simply
// 404s the route — harmless and swallowed. Falls back to the single TREEMON_PORT (shared with the
// canvas-bridge convention), then 5000.
const portsRaw = process.env.TREEMON_PORTS || process.env.TREEMON_PORT || "5000";
const ports = portsRaw
  .split(",")
  .map((p) => p.trim())
  .filter(Boolean);
const activityUrls = ports.map((p) => `http://127.0.0.1:${p}/api/session/activity`);

// Bound every Treemon fetch so a TCP-alive-but-unresponsive server can't stall the caller. These
// posts are best-effort (fire-and-forget); a timeout degrades exactly like an unreachable Treemon.
const TREEMON_FETCH_TIMEOUT_MS = 5000;

// Heartbeat cadence. Comfortably under the server's ~5-min staleness net (a few missed beats), so a
// genuinely-active session that emits no events for a while (a long tool run, or a pending ask_user)
// is not wrongly decayed to Idle. Within the spec's ~30–120s band.
const HEARTBEAT_INTERVAL_MS = 60000;

// The relevant SDK event types. ask_user in Copilot CLI 1.0.71+ emits
// `elicitation.requested`/`elicitation.completed` (question in `data.message`); older builds emitted
// `user_input.requested`/`user_input.completed` (question in `data.question`) — both shapes are
// subscribed for forward/backward compat. `session.usage_info` is the context-window gauge (ephemeral
// upstream, so it only ever arrives live, never in the getEvents() replay). Subscribing per-type
// avoids handling the high-volume streaming/delta events at all.
const SUBSCRIBED_TYPES = [
  "assistant.turn_start",
  "assistant.turn_end",
  "assistant.intent",
  "session.title_changed",
  "session.idle",
  "skill.invoked",
  "assistant.message",
  "user.message",
  "session.usage_info",
  "elicitation.requested",
  "elicitation.completed",
  "user_input.requested",
  "user_input.completed",
];

const log = (msg) => console.error(`[treemon-reporting] ${msg}`);

const worktreePath = process.cwd();
let sessionId = null;

// --- HTTP forwarding ---------------------------------------------------------------------------

let lastErrorLogMs = 0;
function logErrorThrottled(msg) {
  const now = Date.now();
  if (now - lastErrorLogMs > 60000) {
    lastErrorLogMs = now;
    log(msg);
  }
}

// Fire-and-forget POST to every configured instance. Never awaited by callers so the event handler
// stays non-blocking; each promise carries its own catch so a failure never becomes an unhandled
// rejection. A 404 is expected from a non-owning instance and is not logged as an error.
function postReport(report) {
  const payload = JSON.stringify(report);
  for (const url of activityUrls) {
    fetch(url, {
      method: "POST",
      headers: { "Content-Type": "application/json" },
      body: payload,
      signal: AbortSignal.timeout(TREEMON_FETCH_TIMEOUT_MS),
    })
      .then((res) => {
        if (!res.ok && res.status !== 404) {
          logErrorThrottled(`POST ${url} -> ${res.status} ${res.statusText}`);
        }
      })
      .catch((err) => logErrorThrottled(`POST ${url} failed: ${err?.message ?? err}`));
  }
}

// --- Event mapping -----------------------------------------------------------------------------

function eventContext(event) {
  return {
    sessionId,
    worktreePath,
    provider: PROVIDER,
    eventId: event.id,
    occurredAt: event.timestamp,
  };
}

function base(event, kind) {
  return buildReport(eventContext(event), kind);
}

function messageReport(event, kind, text) {
  return buildNonBlankMessageReport(eventContext(event), kind, text);
}

// A skill's own context injection arrives as a `user.message` tagged with BOTH a `source` of
// `skill-<name>` AND a `<skill-context …>` content preamble. Both markers are required: the source
// alone is system-controlled and trustworthy, but a genuine user message could legitimately begin
// with the literal "<skill-context", so requiring the content check without the system-set source
// would let such a message masquerade as an injection (and vice-versa).
function isSkillContextInjection(data) {
  const source = String(data?.source ?? "").toLowerCase();
  const content = String(data?.content ?? "").replace(/^\s+/, "").toLowerCase();
  return source.startsWith("skill-") && content.startsWith("<skill-context");
}

// Map one SDK event onto a wire report, or null when it bears no status (and is therefore dropped at
// the source). Messages with no text are dropped: an empty assistant.message is a pure tool-call
// turn, and a content-less user.message boundary is already covered by the paired assistant.turn_start.
function mapEvent(event) {
  const data = event.data ?? {};
  switch (event.type) {
    case "assistant.turn_start":
      return base(event, "turn_started");
    case "assistant.turn_end":
      return base(event, "turn_ended");
    case "session.idle":
      return base(event, "went_idle");
    case "skill.invoked": {
      const name = String(data.name ?? "").trim();
      return name ? { ...base(event, "skill_invoked"), skillName: name } : null;
    }
    case "assistant.message": {
      return messageReport(event, "assistant_message", data.content);
    }
    case "assistant.intent": {
      // The agent's short description of what it's currently doing/planning. Blank is dropped so a
      // "cleared" intent never regresses the card — the last non-empty intent is retained.
      return messageReport(event, "intent_reported", data.intent);
    }
    case "session.title_changed": {
      // The session's rolling title/summary — the same text the CLI shows in its tab. The server
      // combines it with the intent (freshest of the two wins), so a fresh title supersedes a stale
      // intent. Blank is dropped (nothing to show).
      return messageReport(event, "title_reported", data.title);
    }
    case "user.message": {
      if (isSkillContextInjection(data)) return null;
      return messageReport(event, "user_prompt", data.content);
    }
    case "session.usage_info": {
      // The context-window gauge: currentTokens of tokenLimit. It never perturbs lifecycle state.
      // A non-positive or non-finite limit is degenerate and dropped; a negative current is clamped
      // to 0. Values are rounded to plain integers for the F# int DTO.
      const cur = Number(data.currentTokens);
      const lim = Number(data.tokenLimit);
      if (!Number.isFinite(cur) || !Number.isFinite(lim) || lim <= 0) return null;
      return { ...base(event, "usage_info"), currentTokens: Math.max(0, Math.round(cur)), tokenLimit: Math.round(lim) };
    }
    case "elicitation.requested":
    case "user_input.requested": {
      // ask_user in Copilot CLI 1.0.71+ emits elicitation.requested carrying the prompt in
      // `data.message`; older builds emitted user_input.requested carrying it in `data.question`.
      // Accept either shape so the ask_user question surfaces as LastAssistantMessage.
      return messageReport(event, "awaiting_user_input", data.message ?? data.question)
        ?? base(event, "awaiting_user_input");
    }
    case "elicitation.completed":
    case "user_input.completed":
      return base(event, "user_input_completed");
    default:
      return null;
  }
}

// Handle one event from either the live stream or join-time replay. The extension only filters using
// trusted source metadata and maps SDK events to wire facts; the server owns lifecycle state.
function handle(event) {
  if (event.agentId) return; // sub-agent event — never the user's top-level status

  const report = mapEvent(event);
  if (!report) return;

  postReport(report);
}

// --- Heartbeat ---------------------------------------------------------------------------------

// Re-assert liveness so `last_seen` stays fresh — the server's OPENNESS signal that separates an
// idle-but-OPEN session (blue) from a closed one that has decayed (grey). A heartbeat is a dedicated
// liveness-only report (kind "heartbeat"): the server bumps `last_seen` WITHOUT re-folding status,
// moving the last-write-wins clock, or appending to the event history. Keeping it distinct from real
// events (rather than re-sending a synthetic turn_started / awaiting_user_input / went_idle) means a
// heartbeat can never overtake a slightly-earlier real event and drop it via the server's ordering
// guard, and never inflates the activity_events history with synthetic rows. The server ignores a
// heartbeat until that session has a status row, keeping this extension free of a local status
// mirror. Cadence (60s) stays comfortably under the server openWindow.
function heartbeatTick() {
  postReport({
    sessionId,
    worktreePath,
    provider: PROVIDER,
    eventId: randomUUID(),
    occurredAt: new Date().toISOString(),
    kind: "heartbeat",
  });
}

// --- Bootstrap ---------------------------------------------------------------------------------

let session;
try {
  // Passive join: no tools, no canvas, no session.send — the extension only subscribes.
  session = await joinSession();
} catch (err) {
  log(`joinSession failed: ${err?.message ?? err}`);
  // Nothing to fall back to; don't take the CLI down over a reporting extension.
  process.exit(0);
}

// Read the id defensively: the native runtime populates `session.sessionId`; older/other SDK shapes
// expose `session.id`. The wire contract AND the server's parseReport both require a NON-EMPTY
// sessionId — a blank id is rejected as "missing sessionId", so a `?? ""` fallback would silently
// drop EVERY report from this session. There is no useful anonymous fallback either: reports must
// key onto the real session so they collapse per-session and the stored id can drive `--resume`
// (a fabricated id would resume nothing, and `--continue` is the correct never-reported fallback).
// So when no real id is present we simply don't report — bailing the same clean way, and for the
// same reason, as the joinSession failure above, rather than POSTing blanks the server will reject.
const rawSessionId = session.sessionId ?? session.id;
sessionId = typeof rawSessionId === "string" ? rawSessionId.trim() : "";
if (!sessionId) {
  log("no session id (session.sessionId/session.id both absent) — reporting disabled for this session");
  process.exit(0);
}

// Subscribe to the live stream first so no event is missed, then replay history. Overlap between the
// two is harmless: the server dedupes on eventId, and the newest-wins status guard keeps live status
// from being rewound by older replayed events.
let liveTitleSeen = false;
const unsubscribes = SUBSCRIBED_TYPES.map((type) =>
  session.on(type, (event) => {
    if (
      event.type === "session.title_changed" &&
      !event.agentId &&
      String(event.data?.title ?? "").trim()
    ) {
      liveTitleSeen = true;
    }
    handle(event);
  }),
);

try {
  const history = await session.getEvents();
  // Deterministic replay order. Subtracting Date.parse() values breaks when a timestamp is
  // malformed/unparseable: Date.parse returns NaN, `NaN - x` is NaN, and a comparator that returns
  // NaN is inconsistent — the sort becomes unpredictable and can let the newest-wins status guard be
  // rewound by out-of-order replay. Normalize unparseable timestamps to a sentinel that sorts them
  // first (oldest), and compare with </> rather than subtraction so the comparator is a stable total
  // order for any input.
  const replayMs = (event) => {
    const ms = Date.parse(event?.timestamp);
    return Number.isNaN(ms) ? -Infinity : ms;
  };
  history
    .slice()
    .sort((a, b) => {
      const ta = replayMs(a);
      const tb = replayMs(b);
      return ta < tb ? -1 : ta > tb ? 1 : 0;
    })
    .forEach((event) => handle(event));
  log(
    `joined ${sessionId} — replayed ${history.length} historical event(s), reporting to ${activityUrls.join(", ")}`,
  );
} catch (err) {
  log(`getEvents replay failed: ${err?.message ?? err}`);
}

const heartbeatTimer = setInterval(heartbeatTick, HEARTBEAT_INTERVAL_MS);

const cleanup = () => {
  clearInterval(heartbeatTimer);
  unsubscribes.forEach((unsub) => {
    try {
      unsub();
    } catch {
      // best-effort teardown
    }
  });
};
process.on("SIGTERM", cleanup);
process.on("SIGINT", cleanup);

void (async () => {
  try {
    const snapshot = await session.rpc.metadata.snapshot();
    const report = buildNonBlankMessageReport(
      {
        sessionId,
        worktreePath,
        provider: PROVIDER,
        eventId: randomUUID(),
        occurredAt: new Date().toISOString(),
      },
      "title_bootstrap",
      snapshot?.summary,
    );
    if (!liveTitleSeen && report) postReport(report);
  } catch (err) {
    log(`metadata title bootstrap failed: ${err?.message ?? err}`);
  }
})();
