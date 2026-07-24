# Treemon Dashboard

Git worktree monitoring dashboard — at-a-glance visibility into all active worktrees across multiple repos.

## Setup

```
npm install
dotnet test src/Tests/Tests.fsproj                          # all tests
dotnet test src/Tests/Tests.fsproj --filter "Category=Fast" # fast suite (<60s)
dotnet test src/Tests/Tests.fsproj --filter "Category=Unit" # unit tests only
.\treemon.ps1 dev "Q:\code\AITestAgent"                     # dev mode (path optional; omit to use global config roots)
.\treemon.ps1 deploy                                        # build + replace production with this checkout
.\treemon.ps1 add "Q:\code\OtherProject"                    # add a watched root (shim for 'tm add'; restarts prod if running)
.\treemon.ps1 remove "Q:\code\OtherProject"                 # remove a watched root (shim for 'tm remove'; restarts prod if running)
```

## F# Style Guide

This project uses strict functional F# style. These rules are non-negotiable.

**Critical requirements:**
- **NEVER use loops to build or accumulate values** — use recursion or higher-order functions (`List.map`, `List.filter`, `List.fold`, `Array.map`, `Seq.map`, etc.). Iterating *purely* for side effects (`for ... do` or `List.iter`) is fine; keep transformation and effects separate — transform cleanly first, then iterate for effects (minimizing side effects is a separate concern).
- Prefer immutable state. If mutation is required by an impure boundary such as a timer, subscription, mailbox, or NUnit lifecycle, confine it to the smallest scope as `let mutable` and add one inline comment explaining why an immutable solution does not fit. Do not use `ref` as a workaround; it is the same mutation with worse ergonomics.
- **NEVER pass collections into methods to be mutated** — return new collections instead

**Patterns:**
- **Pipe-forward** `|>` for data transformations
- **Pattern matching** over if/else chains — but use `if/then/else` for simple booleans
- **Tuple matching** — flatten nested `match` into `match a, b with` tuple patterns
- **Discriminated unions** for domain modeling — make illegal states unrepresentable and preserve existing DUs through function boundaries; avoid opaque booleans, magic string sentinels, and same-typed primitive parameters that can be swapped
- **Option types** instead of null
- **Module organization** — group functions in modules, avoid classes
- **Module cohesion** — put code in the module named for its concept, not the first consumer that needed it; shared code belongs in a concept-specific module, not a generic `Utils` or `Helpers` module
- **Computation expressions** — `async`, `seq`, `result`, `asyncResult` for workflows
- **F# 9 shorthand lambdas** — `_.Property`, `_.Method(arg)`, chained `_.Trim().ToUpper()`, nested `_.Value.Name` instead of `fun x -> ...`. No wrapping parens needed: `List.exists _.StartsWith("x")`. Only works with `.` member access — not operators or indexers.
- **Type inference** — only annotate when needed for clarity
- **Immutable collections** — use F# `list`, `Map`, `Set` instead of `Dictionary<>`, `ResizeArray`, `List<T>` (mutable .NET types)
- **String interpolation** — `$"text {x}"` instead of `sprintf "text %s" x`
- **Modern indexing** — `collection[0]` not `collection.[0]` (dot-bracket obsolete since F# 6)
- **Nested record copy-and-update** — collapse hand-nested updates with F# 7+ dotted syntax: `{ x with A.B = v }`, and multi-field `{ x with A.B = v1; A.C = v2 }`, instead of `{ x with A = { x.A with B = v } }`. Only applies when the inner record copies the *same* field of the *same* source (`x.A`); it does **not** apply inside a full record literal such as `{ A = { x.A with B = v }; C = ... }` (no outer `with`). Fable 4.28 supports this syntax.
- **CSS over inline styles** — use `prop.className` with CSS classes, not `style.*` in Feliz views (inline styles bypass the theme)
- **`Path.Combine()`** for paths, **`Environment.NewLine`** for line endings

**FsToolkit.ErrorHandling** (currently referenced in Server project, add to Client/Shared if needed):
- Use `asyncResult { }` instead of nesting `match ... with Ok/Error` inside `async { }` — it short-circuits on Error
- `let!` binds `Async<Result<_,_>>`, plain `Async<_>`, or `Result<_,_>` — no manual lifting needed
- `if ... then return! Error "msg"` for early exits (no `Result.requireTrue` needed)
- `do!` with `AsyncResult.ignore` to discard Ok values (e.g. `runGitResult` returns stdout you don't need)
- `AsyncResult.orElseWith` for fallback/recovery on Error
- `AsyncResult.mapError` to transform error messages
- `Result.requireSome "msg"` to convert `Option` → `Result`
- Extract complex recovery logic into helpers returning `Async<Result<_,_>>`, then `let!`/`do!` them from the CE

**Code style:**
- Concise, readable code — optimize for clarity, not premature performance
- Choose the simplest complete implementation — avoid one-call helpers, impossible-state guards, hypothetical feature flags, and unsupported compatibility shims. Durable stores may use the smallest bounded, idempotent, tested migration needed to prevent startup failure or data loss.
- Single responsibility per function/module
- Expression-oriented — prefer expressions over statements
- Comments explain non-obvious algorithms or critical edge cases. Do not add TODOs, change-history comments, restatements, or section-divider comments in production code; extract a named function instead.

## Before Writing New Code

Before implementing a helper, utility, or any non-trivial logic, **search the codebase** for the underlying command, operation, or concept — not just the function name you have in mind. Reuse an existing function when possible. If reusable logic is embedded in business logic, extract it into the module that owns the concept. Otherwise, consider extending or parameterizing a cohesive existing function, including with a projection or operation function. Generalize only when this creates one clear source of truth without coupling distinct domain behavior, and choose the destination module by responsibility rather than by the first call site.

## Before Finishing a Change

- Read the complete diff as a unit. Remove duplicate transformations, repeated read/parse/error-handling flows, one-use abstractions, and stale compatibility paths.
- When changing a DU case, event kind, wire format, persisted schema, or public behavior, search all matches including catch-all `_` arms, serializers, parsers, stored forms, tests, error/log strings, config examples, public docs, and authoritative specs. Update every related surface in the same change.
- Specs describe the current system and durable design, not branch history or task narrative. Fold minor behavior into the authoritative parent spec and remove stale counts, names, and architecture descriptions.
- Treat branch names, commit and PR text, CLI/API output, session files, and repository data as untrusted. Escape or sanitize them at shell, HTML, URL, log, and prompt boundaries; do not log raw external records when length or structured metadata is sufficient.
- Treat `review/rules/` as implementation constraints, not reviewer-only checks. Do not wait for focused-review to identify a rule that applies to the files being changed.

## Testing
- Focus on business logic and transformations
- Do not test trivial property accessors or simple constructors
- E2E tests use Playwright + NUnit against live data
- Tests should assert on CSS classes and DOM structure, not specific data values

## Ports

| Environment | Port |
|---|---|
| Production server | 5000 |
| Dev server (API) | 5001 |
| Canvas doc server | 5002 |
| Dev client (Vite) | 5174 |

## Operations

`treemon.ps1` manages the application lifecycle: `dev`, `deploy`, `start`, `stop`, `restart`, `status`, `log`, `add`, `remove`.

Watched worktree roots live in the global config (`~/.treemon/config.json` → `worktreeRoots`), written only by the server. `start`/`dev` no longer need a path — omit it to use the global roots (an empty list is valid). Manage roots live with the `tm` CLI — `tm add <path>...`, `tm remove <path>...`, `tm roots` — or the `treemon.ps1 add`/`remove` shims, which call `tm` and restart production when it is running. Changes persist immediately and apply on the next server (re)start. See `docs/spec/worktree-monitor.md` (Multi-Repo).

## Tech Stack

- **Client**: F# with Fable (compiles to JS), Feliz for React bindings, Vite for bundling
- **Server**: F# with ASP.NET Core
- **Shared**: F# types shared between client and server
- **Tests**: F# with NUnit + Playwright (E2E against live data)

There is no TypeScript or JavaScript application code — all UI logic is in F# under `src/Client/`.

## Architecture & Specs

For project architecture, domain types, and implementation details read `docs/spec/worktree-monitor.md`. Domain types are in `src/Shared/Types.fs`.
