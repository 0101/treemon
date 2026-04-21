# Treemon Dashboard

Git worktree monitoring dashboard — at-a-glance visibility into all active worktrees across multiple repos.

## Setup

```
npm install
dotnet test src/Tests/Tests.fsproj                          # all tests
dotnet test src/Tests/Tests.fsproj --filter "Category=Fast" # fast suite (<60s)
dotnet test src/Tests/Tests.fsproj --filter "Category=Unit" # unit tests only
.\treemon.ps1 dev "Q:\code\AITestAgent"                     # dev mode
.\treemon.ps1 deploy                                        # build + restart production
.\treemon.ps1 add "Q:\code\OtherProject"                    # add a root to config
.\treemon.ps1 remove "Q:\code\OtherProject"                 # remove a root from config
```

## F# Style Guide

This project uses strict functional F# style. These rules are non-negotiable.

**Critical requirements:**
- **NEVER use loops** — use recursion or higher-order functions (`List.map`, `List.filter`, `List.fold`, `Array.map`, `Seq.map`, etc.)
- **NEVER use `let mutable`** — all bindings must be immutable; use recursion with accumulators or `fold` instead
- **NEVER use `break` or `continue`** — restructure with higher-order functions
- **NEVER pass collections into methods to be mutated** — return new collections instead

**Patterns:**
- **Pipe-forward** `|>` for data transformations
- **Pattern matching** over if/else chains — but use `if/then/else` for simple booleans
- **Tuple matching** — flatten nested `match` into `match a, b with` tuple patterns
- **Discriminated unions** for domain modeling — make illegal states unrepresentable
- **Option types** instead of null
- **Module organization** — group functions in modules, avoid classes
- **Computation expressions** — `async`, `seq`, `result`, `asyncResult` for workflows
- **F# 9 shorthand lambdas** — `_.Property`, `_.Method(arg)`, chained `_.Trim().ToUpper()`, nested `_.Value.Name` instead of `fun x -> ...`. No wrapping parens needed: `List.exists _.StartsWith("x")`. Only works with `.` member access — not operators or indexers.
- **Type inference** — only annotate when needed for clarity
- **Immutable collections** — use F# `list`, `Map`, `Set` instead of `Dictionary<>`, `ResizeArray`, `List<T>` (mutable .NET types)
- **String interpolation** — `$"text {x}"` instead of `sprintf "text %s" x`
- **Modern indexing** — `collection[0]` not `collection.[0]` (dot-bracket obsolete since F# 6)
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
- Single responsibility per function/module
- Expression-oriented — prefer expressions over statements
- No unnecessary comments — code should be self-documenting
- No backwards-compatibility shims — make breaking changes, update everything at once

## Before Writing New Code

Before implementing a helper, utility, or any non-trivial logic, **search the codebase** for existing functions that do the same thing. Grep for the underlying command, operation, or concept — not just the function name you have in mind. Reuse what exists; don't duplicate.

## Testing
- Focus on business logic and transformations
- Do not test trivial property accessors or simple constructors
- E2E tests use Playwright + NUnit against live data
- Tests should assert on CSS classes and DOM structure, not specific data values

## Code Review

This project uses [focused-review](https://github.com/0101/focused-review) for automated code review. The `review/` directory contains 18 review rules covering: immutability, no-loops, no-null, security, simplicity, pure functions, domain-driven design, and more.

## Ports

| Environment | Port |
|---|---|
| Production server | 5000 |
| Dev server (API) | 5001 |
| Dev client (Vite) | 5174 |

## Operations

`treemon.ps1` manages the application lifecycle: `dev`, `deploy`, `start`, `stop`, `restart`, `status`, `log`, `add`, `remove`.

## Tech Stack

- **Client**: F# with Fable (compiles to JS), Feliz for React bindings, Vite for bundling
- **Server**: F# with ASP.NET Core
- **Shared**: F# types shared between client and server
- **Tests**: F# with NUnit + Playwright (E2E against live data)

There is no TypeScript or JavaScript application code — all UI logic is in F# under `src/Client/`.

## Architecture & Specs

For project architecture, domain types, and implementation details read `docs/spec/worktree-monitor.md`. Domain types are in `src/Shared/Types.fs`.
