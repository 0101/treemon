---
autofix: false
model: sonnet
applies-to: "*.fs"
---
# Domain-Driven Types

## Rule
Use the type system to make illegal states unrepresentable — DUs for mutually exclusive states, single-case DUs for stringly-typed identifiers.

## Why
The type system prevents invalid data at compile time — no need for runtime validation of impossible states. Wrapping primitive types in single-case DUs prevents mixing up arguments of the same underlying type and makes domain concepts explicit.

## Requirements
- Use DUs to model states that are mutually exclusive (e.g. Loading/Loaded/Error, not boolean flags)
- Don't use a record with optional fields when a DU better represents the state space
- Each DU case should carry only the data relevant to that state
- Wrap stringly-typed domain identifiers in single-case DUs (e.g. `type RepoId = RepoId of string`) — don't pass raw strings for things like IDs, paths-with-meaning, or keys that could be mixed up
- Avoid integer-coded or string-coded states when a DU would be clearer

## Wrong
```fsharp
type Request =
    { IsLoading: bool
      Data: string option
      Error: string option }

let fetchPr (repoName: string) (branchName: string) (userId: string) =
    // easy to mix up three string arguments
    ...
```

## Correct
```fsharp
type Request =
    | Loading
    | Loaded of data: string
    | Failed of error: string

type RepoName = RepoName of string
type BranchName = BranchName of string
type UserId = UserId of string

let fetchPr (RepoName repo) (BranchName branch) (UserId user) =
    ...
```
