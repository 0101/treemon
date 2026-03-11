---
autofix: false
model: sonnet
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

## Wrong
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
