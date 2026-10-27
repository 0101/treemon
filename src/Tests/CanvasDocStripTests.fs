module Tests.CanvasDocStripTests

open System
open NUnit.Framework
open Shared
open CanvasDocStrip

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type CanvasTabPresentationTests() =
    [<TestCase(false, false, false, "canvas-tab")>]
    [<TestCase(false, true, false, "canvas-tab canvas-tab-viewed")>]
    [<TestCase(true, true, false, "canvas-tab active")>]
    [<TestCase(true, false, false, "canvas-tab active")>]
    [<TestCase(false, true, true, "canvas-tab canvas-tab-viewed canvas-terminal-linked")>]
    [<TestCase(true, true, true, "canvas-tab active canvas-terminal-linked")>]
    member _.``Active tabs stay undimmed while terminal linkage remains independent``
        (active: bool, viewed: bool, linked: bool, expected: string) =
        let state: CanvasTab.State =
            { IsActive = active; IsViewed = viewed; IsTerminalLinked = linked }
        Assert.That(CanvasTab.className [ "canvas-tab" ] state, Is.EqualTo(expected))

    [<TestCase("Report-1_2.v3.html", "Report-1_2.v3")>]
    [<TestCase("review.html.details.html", "review.html.details")>]
    member _.``Tab labels remove only the final HTML suffix``(filename: string, expected: string) =
        let document =
            { Filename = filename
              ContentHash = ""
              LastModified = DateTimeOffset.UnixEpoch
              OwnerSessionId = None
              Kind = AgentDoc }
        Assert.That(CanvasTab.labelText document, Is.EqualTo(expected))

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type CanvasDocStripTests() =
    let timestamp = DateTimeOffset(2026, 10, 26, 16, 36, 0, TimeSpan.Zero)
    let doc name kind modified owner =
        { Filename = name
          ContentHash = name
          LastModified = modified
          OwnerSessionId = owner
          Kind = kind }

    [<Test>]
    member _.``Only authored documents contribute even when unowned or offline``() =
        let docs =
            [ doc "generated.html" SystemView timestamp None
              doc "unowned.html" AgentDoc (timestamp.AddMinutes(-3.0)) None
              doc "offline.html" AgentDoc (timestamp.AddHours(-2.0)) (Some "offline-owner")
              doc "diff.html" SystemView timestamp None ]
        Assert.That(orderedDocs docs |> List.map _.Filename,
                    Is.EqualTo([ "unowned.html"; "offline.html" ]))

    [<Test>]
    member _.``Modification instants sort newest first with filename ties``() =
        let docs =
            [ doc "old.html" AgentDoc (timestamp.AddDays(-1.0)) None
              doc "z.html" AgentDoc timestamp None
              doc "a.html" AgentDoc (timestamp.ToOffset(TimeSpan.FromHours(2.0))) None ]
        Assert.That(orderedDocs docs |> List.map _.Filename,
                    Is.EqualTo([ "a.html"; "z.html"; "old.html" ]))

    [<Test>]
    member _.``The last fitting title shrinks before another document is hidden``() =
        Assert.That(fit 300.0 2.0 96.0 [ 140.0; 150.0; 120.0 ] [ 20.0; 20.0 ],
                    Is.EqualTo({ VisibleCount = 2; LastWidth = 138.0 }))

    [<Test>]
    member _.``All documents can use space released by the absent overflow label``() =
        Assert.That(fit 304.0 2.0 96.0 [ 100.0; 100.0; 100.0 ] [ 30.0; 30.0 ],
                    Is.EqualTo({ VisibleCount = 3; LastWidth = 100.0 }))

    [<Test>]
    member _.``Overflow width follows the actual hidden count including extra digits``() =
        let overflowWidths = [ for count in 1 .. 11 -> if count < 10 then 20.0 else 30.0 ]
        Assert.That(fit 230.0 2.0 96.0 (List.replicate 12 100.0) overflowWidths,
                    Is.EqualTo({ VisibleCount = 2; LastWidth = 98.0 }))

    [<TestCase(258.0, 2, 96.0)>]
    [<TestCase(257.0, 1, 140.0)>]
    member _.``The final readable title fits at the exact boundary``(available, count, lastWidth) =
        Assert.That(fit available 2.0 96.0 [ 140.0; 150.0; 120.0 ] [ 20.0; 20.0 ],
                    Is.EqualTo({ VisibleCount = count; LastWidth = lastWidth }))

    [<Test>]
    member _.``A narrow row retains its newest title rather than hiding every document``() =
        Assert.That(fit 90.0 2.0 96.0 [ 140.0; 150.0; 120.0 ] [ 30.0; 30.0 ],
                    Is.EqualTo({ VisibleCount = 1; LastWidth = 60.0 }))

    [<Test>]
    member _.``An empty inventory needs no measured overflow labels``() =
        Assert.That(fit 300.0 2.0 96.0 [] [],
                    Is.EqualTo({ VisibleCount = 0; LastWidth = 0.0 }))
