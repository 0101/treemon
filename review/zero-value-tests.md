---
autofix: false
model: inherit
applies-to: "**/*Tests*.fs"
---
# Zero-Value Tests

## Rule
Flag tests that provide no meaningful bug-catching value — tests of trivial behavior, language features, or framework guarantees that add maintenance cost and execution time with no realistic chance of catching a real bug now or in the future.

## Why
Every test has a cost: code to maintain, time to run, cognitive load when reading. Tests should justify that cost by guarding against plausible regressions. Tests that verify compiler behavior, trivial property access, or framework contracts waste time and create false confidence.

## Requirements
- Flag tests that only verify a constructor sets a property (trivial accessor tests)
- Flag tests that verify basic language features (e.g., pattern matching works, list concatenation works, string interpolation produces expected output)
- Flag tests that verify framework/library guarantees (e.g., `List.map` transforms elements, `Option.isSome` returns true for `Some`)
- Flag tests that assert on hardcoded values with no logic under test (e.g., `Assert.AreEqual("hello", "hello")`)
- Flag tests where the assertion is a tautology — the test can never fail under any realistic code change
- Flag tests that duplicate exact coverage of another test with no additional edge case or scenario
- Do NOT flag tests that verify parsing logic, business rules, edge cases, error handling, or integration behavior — even if they look simple, these catch real bugs
- Do NOT flag tests that verify serialization/deserialization round-trips — format changes cause real bugs
- Do NOT flag tests that serve as regression tests for previously-discovered bugs

## Wrong
```fsharp
[<Test>]
let ``Option.IsSome returns true for Some`` () =
    let x = Some 42
    Assert.IsTrue(x.IsSome)

[<Test>]
let ``list append works`` () =
    let result = [1; 2] @ [3; 4]
    Assert.AreEqual([1; 2; 3; 4], result)

[<Test>]
let ``record fields are set correctly`` () =
    let r = { Name = "test"; Value = 42 }
    Assert.AreEqual("test", r.Name)
    Assert.AreEqual(42, r.Value)
```

## Correct
```fsharp
// Good: tests parsing logic that could break with format changes
[<Test>]
let ``parseCommitOutput handles missing timestamp`` () =
    let result = parseCommitOutput "abc123\nsome message\n"
    Assert.IsTrue(result.IsNone)

// Good: tests business rule edge case
[<Test>]
let ``stale session detected after timeout`` () =
    let status = resolveStatus (DateTimeOffset.UtcNow.AddMinutes(-30.0)) entries
    Assert.AreEqual(Idle, status)
```
