---
autofix: false
model: sonnet
applies-to: "**/*.fs"
---
# No Unnecessary Comments

## Rule
No comments that repeat names, change history, or TODOs; only include brief explanations for non-obvious algorithms or critical edge cases.

## Why
Redundant comments add noise and become stale. Code should be self-documenting through clear naming.

## Requirements
- No comments that simply restate the function/variable name or its type signature
- No change history comments (e.g. "// Added 2024-01-15", "// Changed from X to Y")
- No TODO comments
- Only include comments that explain non-obvious algorithms or warn about critical edge cases that can't be expressed through naming
- XML doc comments on public API are acceptable but should add value beyond the name

## Wrong
```fsharp
// Gets the user name
let getUserName user = user.Name

// TODO: handle edge case
let process items = items |> List.map transform

// Changed from string to int on 2024-03-01
let maxRetries = 3
```

## Correct
```fsharp
let getUserName user = user.Name

let process items = items |> List.map transform

let maxRetries = 3

// Claude encodes paths by replacing :, \, / with - (e.g. Q:\code\foo -> Q--code-foo)
let encodePath (path: string) =
    path.Replace(":", "-").Replace("\\", "-").Replace("/", "-")
```
