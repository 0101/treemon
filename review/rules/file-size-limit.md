---
autofix: false
model: sonnet
---
# File Size Limit

## Rule
Flag files over 1000 LOC where the diff is significantly growing the file. Already-large files that aren't growing much are not flagged.

## Why
Large files become hard to navigate, understand, and maintain. This rule catches files that are actively getting worse — not files that were already large before this change. The goal is to nudge contributors to extract code when they're adding substantial amounts to an already-large file.

## Requirements
- Only flag files that are **both**: (a) over 1000 total lines, AND (b) the diff adds a significant net increase (roughly 50+ net new lines)
- A file that was already 1500 lines and grows by 10 lines should NOT be flagged — it's not getting meaningfully worse in this change
- A file that was 1100 lines and grows by 100 lines SHOULD be flagged — this change is actively bloating it
- Use the diff to estimate net lines added (added lines minus removed lines in that file)
- The violation should suggest what could be extracted (e.g. related functions, a helper module, a type definition group)
- This applies to source files only, not generated files, test fixtures, or config

## Wrong
```fsharp
// App.fs — was 1100 lines, this diff adds 120 more lines of view helpers
// bringing it to 1220 lines. Should be flagged.
module App

type Model = { ... }
let update msg model = ...
let view model dispatch = ...
// +120 lines of new card rendering helpers added in this diff
let renderCard ... = ...
let renderCardHeader ... = ...
let renderCardBody ... = ...
// etc.
```

## Correct
```fsharp
// App.fs — stays focused, new code extracted to a separate file
module App

type Model = { ... }
let update msg model = ...
let view model dispatch = ...

// CardViews.fs — new file with the extracted view helpers
module CardViews

let renderCard ... = ...
let renderCardHeader ... = ...
let renderCardBody ... = ...
```
