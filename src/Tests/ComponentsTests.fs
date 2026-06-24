module Tests.ComponentsTests

open System
open NUnit.Framework
open Components

/// Unit coverage for Components.relativeTimeCompact — the compact ("now"/"3m"/"2h"/"2d") sibling of
/// relativeTime used in the canvas tab strip. Pins the threshold boundaries (minute/hour/day) and
/// the int truncation, plus the contract that it drops the " ago" suffix relativeTime carries.
[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type RelativeTimeCompactTests() =

    // Fixed reference instant; each case renders `now - span` so the test is deterministic.
    let now = DateTimeOffset(2026, 6, 22, 12, 0, 0, TimeSpan.Zero)
    let ago (span: TimeSpan) = relativeTimeCompact now (now - span)

    [<Test>]
    member _.``zero elapsed renders now``() =
        Assert.That(ago TimeSpan.Zero, Is.EqualTo("now"))

    [<Test>]
    member _.``thirty seconds renders now``() =
        Assert.That(ago (TimeSpan.FromSeconds 30.0), Is.EqualTo("now"))

    [<Test>]
    member _.``fifty-nine seconds (just under a minute) renders now``() =
        Assert.That(ago (TimeSpan.FromSeconds 59.0), Is.EqualTo("now"))

    [<Test>]
    member _.``exactly one minute renders 1m``() =
        Assert.That(ago (TimeSpan.FromMinutes 1.0), Is.EqualTo("1m"))

    [<Test>]
    member _.``three minutes renders 3m``() =
        Assert.That(ago (TimeSpan.FromMinutes 3.0), Is.EqualTo("3m"))

    [<Test>]
    member _.``ninety seconds truncates to 1m (int truncation, not rounding)``() =
        Assert.That(ago (TimeSpan.FromSeconds 90.0), Is.EqualTo("1m"))

    [<Test>]
    member _.``ninety minutes truncates to 1h (hour-bucket truncation)``() =
        Assert.That(ago (TimeSpan.FromMinutes 90.0), Is.EqualTo("1h"))

    [<Test>]
    member _.``fifty-nine minutes (just under an hour) renders 59m``() =
        Assert.That(ago (TimeSpan.FromMinutes 59.0), Is.EqualTo("59m"))

    [<Test>]
    member _.``exactly one hour renders 1h``() =
        Assert.That(ago (TimeSpan.FromHours 1.0), Is.EqualTo("1h"))

    [<Test>]
    member _.``two hours renders 2h``() =
        Assert.That(ago (TimeSpan.FromHours 2.0), Is.EqualTo("2h"))

    [<Test>]
    member _.``twenty-three hours (just under a day) renders 23h``() =
        Assert.That(ago (TimeSpan.FromHours 23.0), Is.EqualTo("23h"))

    [<Test>]
    member _.``exactly twenty-four hours renders 1d``() =
        Assert.That(ago (TimeSpan.FromHours 24.0), Is.EqualTo("1d"))

    [<Test>]
    member _.``two days renders 2d``() =
        Assert.That(ago (TimeSpan.FromDays 2.0), Is.EqualTo("2d"))

    [<Test>]
    member _.``thirty-six hours truncates to 1d (day-bucket truncation)``() =
        Assert.That(ago (TimeSpan.FromHours 36.0), Is.EqualTo("1d"))

    [<Test>]
    member _.``compact form drops the ' ago' suffix that relativeTime carries``() =
        // Same threshold input, different suffix: guards the compact/verbose contract.
        let earlier = now - TimeSpan.FromMinutes 3.0
        Assert.That(relativeTimeCompact now earlier, Is.EqualTo("3m"))
        Assert.That(relativeTime now earlier, Is.EqualTo("3m ago"))
