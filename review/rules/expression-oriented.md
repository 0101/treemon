---
autofix: false
model: sonnet
applies-to: "**/*.fs"
---
# Expression-Oriented

## Rule
Prefer expressions over statements when possible.

## Why
Expressions return values and compose naturally. Statement-heavy code relies on side effects and mutable state.

## Requirements
- Prefer `let x = if ... then ... else ...` over `if ... then x <- ...`
- Use `match` expressions that return values rather than performing side effects in each branch
- Prefer single-expression function bodies over multi-statement blocks where practical
- Use computation expressions for sequencing effects rather than imperative statement blocks

## Wrong
```fsharp
let describe status =
    let mutable result = ""
    if status = Active then
        result <- "active"
    else
        result <- "inactive"
    result
```

## Correct
```fsharp
let describe status =
    if status = Active then "active"
    else "inactive"
```
