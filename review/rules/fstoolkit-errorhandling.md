---
autofix: false
model: sonnet
applies-to: "**/*.fs"
---
# FsToolkit.ErrorHandling Usage

## Rule
Use FsToolkit.ErrorHandling computation expressions and helper functions instead of manual Result/Option threading.

## Why
The project already depends on FsToolkit.ErrorHandling (v5.2.0) and uses `asyncResult`, `Result.requireSome`, and `Result.map` in several places. Manual `match ... with Ok/Error` pyramids are harder to read, more error-prone, and obscure the data flow. The library's CEs and combinators express the same logic with less nesting and clearer intent.

## Requirements

### 1. Nested Result matching in async — use `asyncResult` CE

When an `async` block contains nested `match` on `Result` values (the "pyramid of doom"), use `asyncResult` instead. The CE handles `Error` propagation automatically via `let!` and `do!`.

#### Wrong
```fsharp
let runStep ctx cmd args check =
    async {
        let! result = runProcess ctx.WorktreePath cmd args ctx.Ct
        match result with
        | Error msg ->
            return Error (StepStatus.Failed msg)
        | Ok proc ->
            match check proc with
            | Ok value ->
                return Ok value
            | Error msg ->
                return Error (StepStatus.Failed msg)
    }
```

#### Correct
```fsharp
let runStep ctx cmd args check =
    asyncResult {
        let! proc = runProcess ctx.WorktreePath cmd args ctx.Ct |> AsyncResult.mapError StepStatus.Failed
        let! value = check proc |> Result.mapError StepStatus.Failed
        return value
    }
```

### 2. Manual Result threading — use `result` CE or pipelines

When pure (non-async) code chains multiple `match` expressions on `Result`, use the `result` CE or `Result.bind`/`Result.map` pipelines.

#### Wrong
```fsharp
let process input =
    match validate input with
    | Error e -> Error e
    | Ok validated ->
        match transform validated with
        | Error e -> Error e
        | Ok transformed ->
            match save transformed with
            | Error e -> Error e
            | Ok saved -> Ok saved
```

#### Correct
```fsharp
let process input =
    result {
        let! validated = validate input
        let! transformed = transform validated
        let! saved = save transformed
        return saved
    }
```

Or as a pipeline when each step takes the previous result directly:

```fsharp
let process input =
    validate input
    |> Result.bind transform
    |> Result.bind save
```

### 3. Option-to-Result conversion — use `Result.requireSome`

When converting an `Option` to a `Result` with a custom error message, use `Result.requireSome` instead of a manual `match`. The codebase already uses this pattern in `WorktreeApi.fs`.

#### Wrong
```fsharp
let! ctx, branch =
    match tryResolveWorktreeContext rootPaths state path with
    | None -> Error "No worktree found"
    | Some ({ Branch = Some branch } as ctx) -> Ok (ctx, branch)
    | Some { Branch = None } -> Error "Detached HEAD"
```

#### Correct (when the Option maps directly to Ok/Error)
```fsharp
let! root =
    rootPaths
    |> Map.tryFind repoId
    |> Result.requireSome $"Unknown repo: {repoId}"
```

### 4. Error recovery / fallback — use `AsyncResult.orElseWith`

When a `match` on `Error` triggers a fallback operation that also returns `Result`, use `AsyncResult.orElseWith` (or `Result.orElseWith` for sync code) instead of nesting the recovery inside the `Error` branch of a `match`.

#### Wrong
```fsharp
let remove repoRoot path =
    async {
        let! result = runGitResult repoRoot $"worktree remove --force \"{path}\""
        match result with
        | Ok _ -> return Ok ()
        | Error removeMsg ->
            async {
                let! fallbackResult = tryPrune repoRoot path
                match fallbackResult with
                | Ok _ -> return Ok ()
                | Error pruneMsg -> return Error $"remove failed: {removeMsg}, prune failed: {pruneMsg}"
            }
    }
```

#### Correct
```fsharp
let remove repoRoot path =
    asyncResult {
        do! runGitResult repoRoot $"worktree remove --force \"{path}\""
            |> AsyncResult.orElseWith (fun removeMsg ->
                tryPrune repoRoot path
                |> AsyncResult.mapError (fun pruneMsg -> $"remove failed: {removeMsg}, prune failed: {pruneMsg}"))
    }
```

### 5. List of Results → Result of list — use `List.sequenceResultM` or `List.traverseResultM`

When mapping a list through a fallible function and then aggregating the results, use `List.traverseResultM` (fail-fast) instead of manual `List.map` + fold/filter.

#### Wrong
```fsharp
let results = items |> List.map tryParse
let errors = results |> List.choose (function Error e -> Some e | _ -> None)
if errors.IsEmpty then
    Ok (results |> List.choose (function Ok v -> Some v | _ -> None))
else
    Error errors.Head
```

#### Correct
```fsharp
let parsed = items |> List.traverseResultM tryParse
```

### 6. Applicative validation — use `validation` CE when collecting ALL errors

When you need to validate multiple independent fields and report all failures (not just the first), use the `validation` CE with `and!` instead of sequential `let!`.

#### Wrong
```fsharp
let validate name age email =
    let errors = [
        if String.IsNullOrWhiteSpace name then "Name required"
        if age < 0 then "Age must be positive"
        if not (email.Contains "@") then "Invalid email"
    ]
    if errors.IsEmpty then Ok { Name = name; Age = age; Email = email }
    else Error errors
```

#### Correct
```fsharp
let validate name age email =
    validation {
        let! n = validateName name
        and! a = validateAge age
        and! e = validateEmail email
        return { Name = n; Age = a; Email = e }
    }
```

## Exceptions
- **One-off Result match at the end of a function**: A single `match` on a `Result` at the end of a function is fine — the rule targets pyramids (two or more nested matches), not every `match`.
- **Custom recovery on a specific step**: When one step in a chain has complex recovery logic (retries, fallback paths), extract that step into a helper that returns `Async<Result<_,_>>` and `let!` it from the `asyncResult` CE. Do NOT exempt the entire function from using the CE just because one step has recovery logic.
- **Mixed Ok/Error pattern matching**: When branches destructure the `Ok` value with additional pattern guards (e.g., `| Ok proc when proc.ExitCode = 0 -> ...`), the manual `match` may be clearer than chaining `Result.bind` with separate condition checks.
- **Interop boundaries**: When calling .NET APIs that don't return `Result`, converting at the boundary with a simple `match` or `try/with` is expected.
- **No FsToolkit import**: Only flag this in files that already `open FsToolkit.ErrorHandling` or where adding the import is trivial (the server project has the package reference).
