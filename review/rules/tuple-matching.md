---
autofix: false
model: sonnet
applies-to: "**/*.fs"
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

## Does not apply
Flattening only holds when the matched values are **independent and simultaneously in scope**. Do NOT flag these cases:

- **Data dependency** — an inner match's input is a value *bound by* an outer match (so it does not exist until the outer case matches). Flattening is impossible / uncompilable here:
  ```fsharp
  match root.TryGetProperty("type") with
  | true, typeProp ->                     // typeProp only exists in this branch
      match typeProp.GetString() with     // depends on typeProp — cannot be lifted into a tuple
      | "skill" -> ...
      | _ -> ...
  | false, _ -> ...
  ```
- **Not a two-value matrix** — an intervening multi-way dispatch (e.g. a single match with many independent cases, or sequential guards that aren't a Cartesian product of two values) is not a nested pair and should stay as is.

