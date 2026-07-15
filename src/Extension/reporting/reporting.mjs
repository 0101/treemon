import { joinSession } from "@github/copilot-sdk/extension";
import { randomUUID } from "node:crypto";

// treemon-reporting — the passive, reporting-only extension (Phase 1 of the push status model).
//
// It ONLY subscribes to the SDK session event stream and forwards a tiny closed set of status
// events to Treemon over HTTP (POST /api/session/activity). It is deliberately orthogonal to the
// canvas-bridge extension: it registers NO tools and NO canvas, never calls session.send, and never
// injects context — so the agent transcript is identical with reporting on or off, and there is no
// canvas_take_ownership tool collision when both extensions load in the same session.
//
// All the ambiguity-resolving filtering lives HERE (at the source), so the server fold stays a tiny
// pure state machine with no branches for sub-agents or skill injections:
//   * sub-agent events (any event carrying `agentId`) are dropped;
//   * a skill's own `<skill-context>` injection (a `user.message` tagged `source: skill-*`) is dropped;
//   * only the ~7 relevant SDK event types are mapped — everything else is ignored.
//
// The wire contract (the single coupling point with the F# handler, Server/SessionActivityService.fs):
//   { sessionId, worktreePath, provider, eventId, occurredAt, kind, message?, skillName? }
// where `kind` is exactly one of the seven the fold consumes and maps 1:1 onto its SessionEvent union:
//   assistant.turn_start   -> turn_started
//   user.message (genuine) -> user_prompt         (message required)
//   assistant.message      -> assistant_message   (message required)
//   skill.invoked          -> skill_invoked        (skillName required)
//   user_input.requested   -> awaiting_user_input  (message = the ask_user question, optional)
//   assistant.turn_end     -> turn_ended
//   session.idle           -> went_idle
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

// Only ever displayed as <=120 chars server-side; this cap only bounds the wire payload for a runaway
// multi-KB message. The raw (untruncated within the cap) text is forwarded — the server owns display
// truncation, keeping this extension a thin forwarder.
const MAX_MESSAGE_CHARS = 2000;

// The relevant SDK event types (the seven mapped kinds + user_input.completed, which is unmapped but
// closes the ask_user window). Subscribing per-type avoids handling the high-volume streaming/delta
// events at all.
const SUBSCRIBED_TYPES = [
  "assistant.turn_start",
  "assistant.turn_end",
  "session.idle",
  "skill.invoked",
  "assistant.message",
  "user.message",
  "user_input.requested",
  "user_input.completed",
];

const log = (msg) => console.error(`[treemon-reporting] ${msg}`);

const worktreePath = process.cwd();
let sessionId = null;

// --- Local status mirror (drives the heartbeat) ------------------------------------------------
// A minimal reflection of what the server fold derives, kept only so the heartbeat can re-assert the
// current status without corrupting the server's skill/message state. `lastStatusMs` is a newest-wins
// guard: replaying older history must never rewind a status already advanced by a live event.
let currentStatus = null; // "working" | "waiting" | "done" | "idle" | null
let lastStatusMs = -Infinity;

// True between a `user_input.requested` (ask_user) and its `user_input.completed`. While pending, a
// `session.idle` must not be forwarded — the SDK may report the session idle while it blocks on the
// user, and the fold would otherwise flip the card off WaitingForUser to Idle.
let pendingAskUser = false;

function statusForKind(kind) {
  switch (kind) {
    case "turn_started":
    case "user_prompt":
    case "assistant_message":
      return "working";
    case "awaiting_user_input":
      return "waiting";
    case "turn_ended":
      return "done";
    case "went_idle":
      return "idle";
    default:
      return null; // skill_invoked sets the skill, not the status
  }
}

function updateStatus(kind, tsIso) {
  const s = statusForKind(kind);
  if (s === null) return;
  const ms = Date.parse(tsIso);
  if (Number.isNaN(ms)) return;
  if (ms >= lastStatusMs) {
    currentStatus = s;
    lastStatusMs = ms;
  }
}

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

function cap(text) {
  const s = String(text ?? "");
  return s.length > MAX_MESSAGE_CHARS ? s.slice(0, MAX_MESSAGE_CHARS) : s;
}

function base(event, kind) {
  return {
    sessionId,
    worktreePath,
    provider: PROVIDER,
    eventId: event.id,
    occurredAt: event.timestamp,
    kind,
  };
}

function message(text, at) {
  return { text: cap(text), at };
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
      const text = String(data.content ?? "");
      return text.trim() ? { ...base(event, "assistant_message"), message: message(text, event.timestamp) } : null;
    }
    case "user.message": {
      if (isSkillContextInjection(data)) return null;
      const text = String(data.content ?? "");
      return text.trim() ? { ...base(event, "user_prompt"), message: message(text, event.timestamp) } : null;
    }
    case "user_input.requested": {
      const question = String(data.question ?? "");
      return question.trim()
        ? { ...base(event, "awaiting_user_input"), message: message(question, event.timestamp) }
        : base(event, "awaiting_user_input");
    }
    default:
      return null;
  }
}

// Handle one event. `isLive` distinguishes the live stream from the join-time getEvents() replay: the
// ask_user window and the went_idle suppression are live-only concerns (user_input.* and session.idle
// are ephemeral and never replayed), while replayed events still reconstruct status/skill/messages.
function handle(event, isLive) {
  if (event.agentId) return; // sub-agent event — never the user's top-level status

  if (isLive) {
    if (event.type === "user_input.requested") pendingAskUser = true;
    else if (event.type === "user_input.completed") pendingAskUser = false;
  }

  const report = mapEvent(event);
  if (!report) return;

  if (isLive) {
    // A genuine user prompt implies the pending ask_user was answered (belt-and-suspenders clear).
    if (report.kind === "user_prompt") pendingAskUser = false;
    // Keep WaitingForUser visible while a prompt is unanswered.
    if (report.kind === "went_idle" && pendingAskUser) return;
  }

  updateStatus(report.kind, event.timestamp);
  postReport(report);
}

// --- Heartbeat ---------------------------------------------------------------------------------

// Re-assert the current status so `last_seen` stays fresh. Only the two long-lived active states are
// refreshed: a quiet Working (a long tool run) or a pending WaitingForUser must not decay to Idle via
// the staleness net. Done is transient (session.idle follows) and Idle needs no refresh, so both are
// left to decay naturally. The synthetic event uses a status-preserving kind (re-folding it is a
// no-op on skill/messages) with a fresh eventId + now timestamp, so the server bumps last_seen without
// altering anything else.
function heartbeatTick() {
  let kind = null;
  if (currentStatus === "working") kind = "turn_started";
  else if (currentStatus === "waiting") kind = "awaiting_user_input";
  if (!kind) return;

  postReport({
    sessionId,
    worktreePath,
    provider: PROVIDER,
    eventId: randomUUID(),
    occurredAt: new Date().toISOString(),
    kind,
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

// Read the id defensively: the native runtime populates `session.sessionId`, older/other SDK shapes
// expose `session.id`. A missing id yields anonymous reports the server can still fold (keyed by the
// empty session id) but which never collapse per-session — so prefer whichever is present.
sessionId = session.sessionId ?? session.id ?? "";

// Subscribe to the live stream first so no event is missed, then replay history. Overlap between the
// two is harmless: the server dedupes on eventId, and the newest-wins status guard keeps live status
// from being rewound by older replayed events.
const unsubscribes = SUBSCRIBED_TYPES.map((type) => session.on(type, (event) => handle(event, true)));

try {
  const history = await session.getEvents();
  history
    .slice()
    .sort((a, b) => Date.parse(a.timestamp) - Date.parse(b.timestamp))
    .forEach((event) => handle(event, false));
  log(
    `joined ${sessionId || "(anonymous)"} — replayed ${history.length} historical event(s), reporting to ${activityUrls.join(", ")}`,
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
