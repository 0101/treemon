---
autofix: false
model: sonnet
applies-to: "*.fs"
source: ".claude/CLAUDE.md"
---
# Tuple Matching

## Rule
Flatten nested match expressions into `match a, b with` tuple patterns.

## Why
Makes the case matrix explicit and eliminates nesting, improving readability.

## Requirements
- When matching on two or more values, use tuple matching instead of nested match expressions
- Each case combination should be a single flat pattern

## Wrong
```fsharp
match a with
| Some x ->
    match b with
    | Some y -> combine x y
    | None -> useA x
| None ->
    match b with
    | Some y -> useB y
    | None -> defaultValue
```

## Correct
```fsharp
match a, b with
| Some x, Some y -> combine x y
| Some x, None -> useA x
| None, Some y -> useB y
| None, None -> defaultValue
```
