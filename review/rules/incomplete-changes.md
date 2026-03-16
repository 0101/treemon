---
autofix: false
model: inherit
---
# Incomplete Changes

## Rule
Flag changes that are partially applied — missing updates to related code that should have changed together.

## Why
Partial changes cause runtime errors, dead code, and semantic drift. A rename in one place but not another, a new DU case without exhaustive match updates, or a changed API without caller updates are common sources of bugs that slip through because each file looks fine in isolation.

## Requirements
- When a new DU case is added, check all match expressions with catch-all `_` or `| _ ->` patterns — the compiler won't warn, but the new case silently falls into the default branch, which may be wrong
- Error messages and log strings must describe the actual operation, not a stale/copy-pasted one
- README, config examples, and public API docs must be updated when the behavior they describe changes

## Wrong
```fsharp
// Added new DU case
type Status = Active | Inactive | Archived

// Catch-all hides the new case — compiler is happy, but Archived
// silently gets "unknown" instead of its own label
let describe status =
    match status with
    | Active -> "active"
    | Inactive -> "inactive"
    | _ -> "unknown"
```

## Correct
```fsharp
type Status = Active | Inactive | Archived

let describe status =
    match status with
    | Active -> "active"
    | Inactive -> "inactive"
    | Archived -> "archived"
```
