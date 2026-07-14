---
autofix: false
model: haiku
applies-to: "**/*.fs"
---
# No Loops

## Rule
No for/while loops for building or accumulating a collection or value; use higher-order functions or recursion instead. Iterating purely for side effects (`for ... do` or `List.iter`) is fine — this rule does not police side effects.

## Why
Loops with mutable state are imperative and error-prone. Functional iteration with List.map, List.filter, List.fold, Seq.map, Array.map etc. is safer and more composable.

## Requirements
- No `for ... do` / `while ... do` loop that builds or accumulates a collection or value — use higher-order functions or recursion
- Use higher-order functions (List.map, List.filter, List.fold, Seq.collect, Array.map, etc.) for data transformation
- Use recursion with accumulators when higher-order functions don't fit
- Side-effecting iteration is allowed in either form — `for x in xs do <effect>` and `xs |> List.iter <effect>` are both fine, use whichever reads best. This rule does not flag *pure* side-effecting iteration (whether the side effect is warranted is left to other rules)
- Do not mix transformation with effects: a side-effect loop/`iter` must accumulate nothing and compute nothing — transform cleanly first with higher-order functions (pure), then iterate for effects. Equally, never perform side effects inside `map`/`filter`/`fold` (those must stay pure). A loop that both derives a value and performs effects is still a violation
- `for` inside computation expressions (seq { for x in xs do ... }) is acceptable — that's a generator, not an imperative loop

## Wrong
```fsharp
let result = ResizeArray()
for item in items do
    if item.IsActive then
        result.Add(item.Name)
```

Mixing transformation with effects in one loop is also wrong:
```fsharp
for item in items do
    let name = item.Name.Trim().ToUpper()
    log name
```

## Correct
```fsharp
let result =
    items
    |> List.filter (fun item -> item.IsActive)
    |> List.map (fun item -> item.Name)
```

Transform cleanly first (pure), then iterate for effects — both forms are fine:
```fsharp
let names = items |> List.map (fun item -> item.Name.Trim().ToUpper())

for name in names do
    log name

names |> List.iter log
```
