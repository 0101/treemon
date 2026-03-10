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
```

## F# Style Guide

This project uses strict functional F# style. These rules are non-negotiable.

**Critical requirements:**
- **NEVER use loops** — use recursion or higher-order functions (`List.map`, `List.filter`, `List.fold`, `Array.map`, `Seq.map`, etc.)
- **NEVER use `let mutable`** — all bindings must be immutable; use recursion with accumulators or `fold` instead
- **NEVER use `break` or `continue`** — restructure with LINQ/higher-order functions
- **NEVER pass collections into methods to be mutated** — return new collections instead

**Patterns:**
- **Pipe-forward** `|>` for data transformations
- **Pattern matching** over if/else chains — but use `if/then/else` for simple booleans
- **Tuple matching** — flatten nested `match` into `match a, b with` tuple patterns
- **Discriminated unions** for domain modeling — make illegal states unrepresentable
- **Option types** instead of null
- **Module organization** — group functions in modules, avoid classes
- **Computation expressions** — `async`, `seq`, `result` for workflows
- **Type inference** — only annotate when needed for clarity
- **`Path.Combine()`** for paths, **`Environment.NewLine`** for line endings

**Code style:**
- Concise, readable code — optimize for clarity, not premature performance
- Single responsibility per function/module
- Expression-oriented — prefer expressions over statements
- No unnecessary comments — code should be self-documenting
- No backwards-compatibility shims — make breaking changes, update everything at once

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

`treemon.ps1` manages the application lifecycle: `dev`, `deploy`, `start`, `stop`, `restart`, `status`, `log`.

## Architecture & Specs

For project architecture, domain types, and implementation details read `docs/spec/worktree-monitor.md`. Domain types are in `src/Shared/Types.fs`.
