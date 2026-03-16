---
autofix: false
model: sonnet
source: "built-in"
---
# Code Duplication

## Rule
Flag new or changed code that duplicates logic already present in the codebase.

## Why
Duplicated code increases maintenance burden — bugs must be fixed in multiple places, and behavior diverges over time. Catching duplication at review time prevents it from accumulating.

## Requirements
- New code should not replicate logic that already exists elsewhere in the codebase
- If a diff adds code similar to an existing function/method/pattern, flag it and suggest reusing or extracting a shared abstraction
- Use grep to search the codebase for similar patterns when reviewing added code
- Minor duplication (a single repeated line, common boilerplate) is acceptable — focus on duplicated logic or algorithms
- Structural similarity is NOT duplication. Do NOT flag these patterns:
  - Factory methods or builders that follow a parallel structure (each creates a different type with different configuration)
  - Constructor overloads or method overloads that delegate to each other or handle different parameter combinations
  - Platform-specific implementations that look similar but target different OS/runtime paths
  - Test setup methods that follow the same arrange/act/assert shape but test different scenarios
  - Enum-to-value mappings (switch/match expressions mapping each case to a distinct value)
- Only flag duplication when the duplicated logic can actually be shared through a common method or base class without losing clarity or adding coupling

## Wrong (real duplication — flag this)
```
// In UserService.cs — new code in diff
public string FormatUserName(User user) =>
    $"{user.LastName}, {user.FirstName} ({user.Email})";

// Already exists in DisplayHelper.cs
public string FormatName(Person p) =>
    $"{p.LastName}, {p.FirstName} ({p.Email})";
```

## Correct
```
// Reuse the existing helper
public string FormatUserName(User user) =>
    DisplayHelper.FormatName(user);
```

## Wrong (false positive — do NOT flag structural similarity)
```
// Reviewer flags these factory methods as duplicated, but each creates a
// different type with different config. They share a pattern, not logic.
public static IHandler CreateHttpHandler(HttpConfig cfg) =>
    new HttpHandler(cfg.Endpoint, cfg.Timeout, cfg.RetryPolicy);

public static IHandler CreateGrpcHandler(GrpcConfig cfg) =>
    new GrpcHandler(cfg.Endpoint, cfg.Timeout, cfg.MaxStreams);
```
