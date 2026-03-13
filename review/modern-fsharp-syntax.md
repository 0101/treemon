---
autofix: true
model: inherit
applies-to: "**/*.fs"
source: "CLAUDE.md"
---
# Modern F# Syntax

## Rule
Use modern F# 8/9 syntax features instead of legacy patterns. LLMs often generate outdated F# — flag and fix it.

## Why
F# 8 and 9 introduced cleaner syntax that reduces noise and improves readability. These features are underrepresented in LLM training data, so generated code often uses verbose legacy patterns. Keeping the codebase consistently modern prevents style drift.

## Requirements
- Use `_.Property` shorthand lambda (F# 9) instead of `fun x -> x.Property` in pipelines. Only applies to simple single-member-access lambdas — do NOT convert lambdas that do computation, call methods with arguments, or access nested members.
- Use `collection[index]` indexer syntax instead of `collection.[index]` (the dot-bracket form has been obsolete since F# 6).
- Use string interpolation `$"text {expr}"` instead of `sprintf "text %s" expr` or `String.Format`. Exception: `sprintf` is acceptable in partial application (e.g., `List.map (sprintf "prefix-%s")`).
- Use `_.Method()` shorthand (F# 9) instead of `fun x -> x.Method()` for simple no-argument method calls in pipelines.
- Use `list[i..j]` slice syntax instead of verbose `List.skip`/`List.take` combinations where the intent is slicing a contiguous range.
- Use `{| |}` anonymous records when a one-off record shape is needed and no named type exists, instead of defining a single-use record type.
- Use `nameof` instead of hardcoded string literals for member/type names in error messages and attributes.

## Wrong
```fsharp
// Legacy lambda instead of shorthand
worktrees |> List.filter (fun w -> w.IsActive)
branches |> List.map (fun b -> b.Name)
items |> List.sortBy (fun x -> x.Priority)
results |> List.exists (fun r -> r.HasError)

// Old dot-bracket indexer
let first = myList.[0]
let ch = myString.[3]
array.[i] <- newValue

// sprintf instead of interpolation
let msg = sprintf "Branch %s has %d commits" name count
log (sprintf "Processing %s..." path)
printfn "Error: %s at line %d" message line

// Verbose lambda for no-arg method call
lines |> List.map (fun l -> l.Trim())
paths |> List.filter (fun p -> p.Exists())
```

## Correct
```fsharp
// F# 9 shorthand lambda
worktrees |> List.filter _.IsActive
branches |> List.map _.Name
items |> List.sortBy _.Priority
results |> List.exists _.HasError

// Modern indexer (no dot)
let first = myList[0]
let ch = myString[3]
array[i] <- newValue

// String interpolation
let msg = $"Branch {name} has {count} commits"
log $"Processing {path}..."
printfn $"Error: {message} at line {line}"

// F# 9 shorthand for no-arg method calls
lines |> List.map _.Trim()
paths |> List.filter _.Exists()
```
