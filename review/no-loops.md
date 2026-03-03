---
autofix: false
model: haiku
applies-to: "*.fs"
source: ".claude/CLAUDE.md"
---
# No Loops

## Rule
No for/while loops; use recursion or higher-order functions instead.

## Why
Loops with mutable state are imperative and error-prone. Functional iteration with List.map, List.filter, List.fold, Seq.map, Array.map etc. is safer and more composable.

## Requirements
- No `for ... do` loops
- No `while ... do` loops
- Use higher-order functions (List.map, List.filter, List.fold, Seq.collect, Array.map, etc.) for data transformation
- Use recursion with accumulators when higher-order functions don't fit
- `for` inside computation expressions (seq { for x in xs do ... }) is acceptable — that's a generator, not an imperative loop

## Wrong
```fsharp
let result = ResizeArray()
for item in items do
    if item.IsActive then
        result.Add(item.Name)
```

## Correct
```fsharp
let result =
    items
    |> List.filter (fun item -> item.IsActive)
    |> List.map (fun item -> item.Name)
```
