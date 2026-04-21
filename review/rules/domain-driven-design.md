---
autofix: false
model: sonnet
applies-to: "**/*.fs"
---
# Domain-Driven Types

## Rule
Use the type system to make illegal states unrepresentable — but only where it prevents real bugs or clarifies ambiguous interfaces. Don't add wrapper types that only add ceremony.

## Why
The type system prevents invalid data at compile time. But wrapping every string in a single-case DU has diminishing returns — it's only valuable when there's a real risk of mixing up arguments or when it makes an interface's contract clearer.

## Requirements
- Use DUs to model states that are mutually exclusive (e.g. Loading/Loaded/Error, not boolean flags)
- Don't use a record with optional fields when a DU better represents the state space
- Each DU case should carry only the data relevant to that state
- Avoid integer-coded or string-coded states when a DU would be clearer
- Wrap primitive identifiers in single-case DUs when it brings real value:
  - Multiple same-typed parameters that could be swapped at call sites
  - Values passed through many functions/layers where the type proves it's the right thing
  - Values that need validation on creation (e.g. non-empty, specific format)
- Do NOT flag single-string parameters, named DU fields, or locally-scoped values where the type's position/name already prevents confusion
- When a DU already exists for a state, pass the DU through — don't collapse it to a bool at function boundaries. Even when the immediate consumer only needs yes/no, passing the DU is usually equal or less code (e.g. `Option.defaultValue NoPr` vs `Option.exists (function HasPr _ -> true | _ -> false)`) and preserves information for future use. Evaluate the fix concretely: if passing the DU simplifies or doesn't complicate the code, prefer it.

## Wrong
```fsharp
// Boolean flags for mutually exclusive states
type Request =
    { IsLoading: bool
      Data: string option
      Error: string option }

// Three raw strings easily mixed up at call sites
let fetchPr (repoName: string) (branchName: string) (userId: string) = ...
fetchPr userId repoName branchName // compiles, but wrong

// Collapsing an existing DU to bool at function boundary
let hasPr = prData |> Map.tryFind branch |> Option.exists (function HasPr _ -> true | _ -> false)
let executePipeline (hasPr: bool) = if hasPr then push()
```

## Correct
```fsharp
// DU makes illegal states unrepresentable
type Request =
    | Loading
    | Loaded of data: string
    | Failed of error: string

// Wrapper types prevent argument mixups
type RepoName = RepoName of string
type BranchName = BranchName of string
type UserId = UserId of string

let fetchPr (RepoName repo) (BranchName branch) (UserId user) = ...

// But a single named field is fine as raw string — no mixup risk
type Msg = | DeleteWorktree of branch: string

// Pass existing DU through — simpler code, preserves information
let prStatus = prData |> Map.tryFind branch |> Option.defaultValue NoPr
let executePipeline (prStatus: PrStatus) = match prStatus with HasPr _ -> push() | NoPr -> ()
```
