# Worktree Prompt → Auto-launch Investigate Session

## Goals

- Add an optional multi-line **prompt** to the Create-worktree modal (the `+` button flow).
- When a prompt is provided, after the worktree is created, **spawn a coding-agent terminal**
  in the new worktree seeded with the prompt wrapped in a **skill invocation** — starting with
  the `investigate` skill — so a task can fire off preliminary research in one click.
- Make the skill **config-driven** (per-repo `.treemon.json`, default `investigate`), forced to a
  single skill for now, with room for a future radio-group of skills.
- **Reuse** the existing session-launch machinery (the same path used by the FixBuild/CreatePr
  action buttons); add no bespoke spawn logic.

## Expected Behavior

### Prompt field

The Create-worktree modal (`Open` state) gains a multi-line **prompt textarea** below the base-branch
select:
- Placeholder communicates it is optional and that a non-empty value launches the investigate skill.
- **Enter inserts a newline** — it does **not** submit. Submit stays on the **Create** button;
  **Escape** still closes the modal. The branch-name input keeps `autoFocus` and its Enter-to-submit.
- A blank/whitespace-only prompt is treated as "no prompt" (empty ⇒ current behavior).

### Create + auto-launch

On submit the request carries the prompt (as `string option`). The server:
1. Creates the worktree exactly as today (unchanged git/fork/post-fork behavior).
2. If the prompt is present and non-blank, **fire-and-forget** spawns a tracked Windows Terminal
   window running the coding tool in the new worktree, seeded with the skill-wrapped prompt.
3. Returns the same warnings list as today. The modal closes/warns exactly as before — it does **not**
   wait for the terminal to appear (the spawn resolves its window for up to ~10s in the background).

With an **empty** prompt, no terminal is spawned — the flow is identical to today.

### Skill invocation (provider-aware)

The prompt is wrapped per coding-tool provider, matching the existing `actionPrompt` convention:
- **Copilot** ⇒ `use <skill> skill with <prompt>`
- **Claude** ⇒ `/<skill> <prompt>`

For now `<skill>` is `investigate` (from config, defaulted). The wrapped prompt is passed to the tool
in interactive mode (`copilot --yolo -i '<...>'` / `claude --dangerously-skip-permissions '<...>'`).

### Config-driven skill

`<skill>` is read from the repo's `.treemon.json` `"defaultSkill"` field, defaulting to `investigate`
when absent. We do **not** validate the value or check that the skill exists — a misspelled or unknown
skill is the user's problem, and the coding tool simply reports it in the launched session.

### Edge cases

- **Multi-line prompt** must work end-to-end (it is a first-class use case, not just tolerated).
- **Post-fork failure**: `createWorktree` returns `Ok` with warnings even when `post-fork.ps1` fails.
  The agent is launched **anyway** (the `investigate` skill is research, not building — low harm).
- **Spawn failure** (e.g. `copilot` not installed) is non-fatal: the worktree still exists; the
  failure is logged, not surfaced in the modal.
- **Subsequent normal session**: if the user later starts a session for the same worktree,
  `spawnSession` replaces the auto-launched window (one tracked window per path) — acceptable.

## Technical Approach

### Shared contract

Add `Prompt: string option` to `CreateWorktreeRequest` (`Shared/Types.fs`). The
`IWorktreeApi.createWorktree` return type is **unchanged** (`Async<Result<CreateWorktreeWarnings, string>>`);
warnings still surface to the modal, and the launch is a server-side side effect.

### Server: unify the skill-invocation string

The provider-aware skill wrapping is currently inlined per case in `actionPrompt`
(`use pr skill with {url}` / `/pr {url}`, and the `fix-build` equivalents). Extract a shared helper in
`CodingToolStatus.fs`:

```fsharp
let skillInvocation (provider: CodingToolProvider option) (skill: string) (arg: string) =
    match provider |> Option.defaultValue CodingToolProvider.Default with
    | Copilot -> $"use {skill} skill with {arg}"
    | Claude  -> $"/{skill} {arg}"
```

Refactor `actionPrompt`'s `FixPr`/`FixBuild` cases to call it — **byte-identical output**, so
`CommandBuilderTests.fs` assertions stay green. The create-flow uses the same helper with the
configured skill and the user's prompt.

### Server: config reader

Add `TreemonConfig.readDefaultSkill (repoRoot) : string`, default `"investigate"` — mirrors the
existing `readBaseBranch` (read the string field, apply the default when absent). **No validation and
no fallback chain:** if the value is present it is used as-is; if absent, `investigate`. A bad skill
name is the user's responsibility — the wrapped prompt is single-quote-escaped by `CodingToolCli.escape`,
so an odd value is a no-op for the coding tool, not an injection concern.

### Server: `createWorktree` returns the new path

`GitWorktree.createWorktree` already computes the worktree path (via `resolveWorktreeCommand`) but
returns only warnings. Change its return to carry both, e.g.
`Result<{ WorktreePath: string; Warnings: CreateWorktreeWarnings }, string>`, so the API binding can
launch into the exact path. Update the ~8 test call sites in `CreateWorktreeServerTests.fs`.

### Server: orchestration in the `createWorktree` binding

In the live `createWorktree` binding (`WorktreeApi.fs`), after a successful create, when
`req.Prompt` is `Some` non-blank:

```fsharp
let provider = CodingToolStatus.readConfiguredProvider newPath
               |> Option.orElse (CodingToolStatus.readConfiguredProvider root)
let skill    = TreemonConfig.readDefaultSkill root            // default "investigate"; no validation
let wrapped  = CodingToolStatus.skillInvocation provider skill prompt
let cmd      = (CodingToolCli.build provider (CodingToolCli.Interactive wrapped)).AsShellString
async {
    try
        match! SessionManager.launchAction sessionAgent (WorktreePath newPath) cmd with
        | Ok () -> ()
        | Error msg -> Log.log "API" $"Auto-launch failed for {newPath}: {msg}"
    with ex -> Log.log "API" $"Auto-launch crashed for {newPath}: {ex}"
} |> Async.Start
```

- Reuses `SessionManager.launchAction` (the same call FixBuild/CreatePr use): for a brand-new path
  with no tracked window it spawns+tracks a fresh window.
- The `try/with` is required: `launchAction` uses `PostAndAsyncReply(timeout=30s)` which **throws** on
  timeout, and `Async.Ignore` would swallow the `Error` case — an unguarded `Async.Start` could fault
  silently.
- Provider/skill are read from the new worktree first (its `.treemon.json` exists once create
  returns) because it can differ from the root's working copy.

### Server: escape the worktree path in `buildScript` (hardening)

`SessionManager.buildScript` emits `Set-Location '{nativePath}'; {cmd}` without doubling single quotes
in `nativePath`. A path containing `'` breaks parsing (or injects). Fix centrally by escaping
(`nativePath.Replace("'", "''")`). Pre-existing on every session launch; fixed here because this
feature adds another launch path. Prompt escaping (`CodingToolCli.escape`) is already correct.

### Client: modal textarea

- `CreateWorktreeForm` gains `Prompt: string` (init `""`); new `Msg` `SetPrompt of string`.
- The `Open` view renders an `Html.textarea` (class `modal-textarea`, ~3 rows) after the base-branch
  select; `onKeyDown` allows Enter (newline) and Escape (close) but does not submit.
- `SubmitCreateWorktree` sets `Prompt = (let t = form.Prompt.Trim() in if t = "" then None else Some t)`
  on the request.
- Add `.modal-textarea` CSS in `index.html` mirroring `.modal-input` plus `resize: vertical`.

### Multi-line safety

A multi-line prompt survives because `SessionManager` base64-encodes the whole pwsh script
(`pwsh -EncodedCommand`), so newlines never reach shell parsing, and a PowerShell single-quoted string
may span newlines — the arg reaches the tool intact. Verified by design; exercised by a manual E2E step.

## Decisions

- **Server-side orchestration inside `createWorktree`** over a client-side `launchSession` follow-up:
  the repo root, provider, skill config, and new path are all in scope on the server, and it keeps all
  provider/skill logic on one tier (consistent with `launchAction`/`launchSession`).
- **Fire-and-forget spawn** over awaiting: the modal closes immediately; a failed spawn is logged, not
  surfaced (the worktree still exists).
- **Reuse `SessionManager.launchAction`** over a new spawn path: it already spawns+tracks when no
  window exists, so no new machinery.
- **Config default with a forced single skill now**: ship `investigate` while keeping the skill a
  single config-backed value — a radio-group of multiple skills is a later increment (add
  `Skill: string option` to the request as a client override).
- **No skill validation**: `readDefaultSkill` reads the configured value and defaults to `investigate`
  when absent — it does **not** check the value or whether the skill exists. A misspelled/unknown skill
  is the user's problem; the coding tool reports it. The prompt is already single-quote-escaped, so
  there is no injection concern, making validation pure complication.
- **Launch even after a post-fork-failure warning**: the first skill is research; suppressing would
  require a structured warning type (today it is a flat `string list`).
- **Wording unified on `with`**: matches the existing `actionPrompt` house style; a one-line change if
  `for:` is preferred later.
- **CSRF hardening of the remoting surface (review F7)**: `createWorktree`'s auto-launch turns the
  auth-less, CSRF-token-less Fable.Remoting API into an agent-execution sink reachable by a
  preflight-free cross-origin `text/plain` POST (Fable.Remoting.Server 5.42 does not enforce a
  content type). Hardened systemically — not with a `createWorktree`-only patch — via one Giraffe
  guard (`Server.HttpSecurity.csrfGuard`) composed **before** the remoting handler in `Program.fs`:
  a state-changing (non GET/HEAD/OPTIONS) request whose `Origin` (else `Referer`) is **present and
  not loopback** is rejected `403`; a **missing** one is allowed so the non-browser Cli (sends
  neither header) and the same-origin SPA (loopback origin) keep working. Kestrel binds
  localhost-only, so this closes the browser cross-origin vector. Content-type enforcement was
  weighed as defense-in-depth but **not** added: the Origin check already blocks the vector, and
  coupling to the clients' exact content-type risks breaking the Cli. The loopback decision is a
  pure, unit-tested function (`isSameOriginRequest`).
- **Create-path provider read is deliberately NOT `resolveProvider` (review F8)**: a just-created
  worktree is not yet in the scheduler's KnownPaths/CodingToolData, so `resolveProvider` returns
  `None` there; the create path must read `.treemon.json` directly. Kept as a direct read (comment
  strengthened to say so) rather than extracting a shared helper — there is no second caller to
  deduplicate and no current bug, so a helper would add indirection without removing duplication.
- **Test-hygiene follow-up (review F2-F6, task `tm-quicklaunch-fx2`)**: The `zero-value-tests` rule
  over-fired on this test-heavy diff (see the report's Rule Quality Notes), so judgement was applied
  rather than acting on every finding. **Acted:** removed the three `preserves other form fields`
  tests (F#'s `{ r with x = v }` copy-update is a language guarantee) and the redundant `SetPrompt
  produces no command` test (command production is shared through the `just` helper, already covered
  by the pre-existing `SetNewWorktreeName`/`SetBaseBranch` siblings) in `CreateWorktreeTests.fs`;
  added the immutability-rule-required justifying comment to the three NUnit `let mutable tempDir`
  fixtures in `UpstreamRemoteTests.fs` (a shared base fixture was weighed but rejected — it would
  churn dozens of `member`/closure references for a low-priority nit). **Consciously deferred (F3,
  F6):** the `SetPrompt ignored when Closed`/`ignored when Creating` pair (both hit the same
  `| SetPrompt _, _` wildcard arm) and the `readDefaultSkill coexists with other fields` test were
  left as-is. Both are the newest instance of a repo-wide symmetric pattern; the reviewer marked them
  optional and warned against fixing only the diff-local case, and the family-wide alternative would
  churn untouched pre-existing tests for negligible benefit while the symmetric test matrices carry
  documentation value.

## Key Files

| File | Changes |
|------|---------|
| `src/Shared/Types.fs` | Add `Prompt: string option` to `CreateWorktreeRequest` |
| `src/Server/GitWorktree.fs` | `createWorktree` returns the new worktree path alongside warnings |
| `src/Server/CodingToolStatus.fs` | Extract `skillInvocation`; refactor `actionPrompt` onto it |
| `src/Server/TreemonConfig.fs` | Add `readDefaultSkill` (reads config, default `investigate`; no validation) |
| `src/Server/SessionManager.fs` | Escape single quotes in `buildScript` `nativePath` |
| `src/Server/WorktreeApi.fs` | Orchestrate guarded fire-and-forget launch in `createWorktree` |
| `src/Server/HttpSecurity.fs` | New: `csrfGuard` + pure loopback-origin decision (review F7) |
| `src/Server/Program.fs` | Compose `HttpSecurity.csrfGuard` before the remoting handler (review F7) |
| `src/Client/CreateWorktreeModal.fs` | `Prompt` field, `SetPrompt` msg, textarea, request wiring |
| `src/Client/index.html` | CSS for `.modal-textarea` |
| `src/Tests/CreateWorktreeServerTests.fs` | Update `createWorktree` return-type call sites |
| `src/Tests/CreateWorktreeTests.fs` | `SetPrompt` update tests + `SubmitCreateWorktree` prompt→request mapping tests (capturing fake `IWorktreeApi`) |
| `src/Tests/CommandBuilderTests.fs` | Add `skillInvocation` coverage (existing `actionPrompt` stay green) |
| `src/Tests/HttpSecurityTests.fs` | New: unit tests for the loopback same-origin / allow decision (review F7) |

## Related Specs

- `docs/spec/contextual-actions.md` — the provider-aware action-launch pattern this feature reuses
- `docs/spec/native-session-management.md` — session spawning/tracking foundation
- `docs/spec/worktree-monitor.md` — Multi-Repo config (`.treemon.json`) and card/modal layout
