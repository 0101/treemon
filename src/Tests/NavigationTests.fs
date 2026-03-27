module Tests.NavigationTests

open System
open NUnit.Framework
open Shared
open Navigation

module NavHelpers =
    let makeWorktree branch : WorktreeStatus =
        { Path = WorktreePath.create $"/repo/{branch}"
          Branch = branch
          LastCommitMessage = "msg"
          LastCommitTime = DateTimeOffset.UtcNow
          Beads = BeadsSummary.zero
          CodingTool = CodingToolStatus.Idle
          CodingToolProvider = None
          LastUserMessage = None
          Pr = PrStatus.NoPr
          MainBehindCount = 0
          IsDirty = false
          WorkMetrics = None
          HasActiveSession = false
          HasTestFailureLog = false
          IsArchived = false }

    let makeRepo repoId branches : RepoModel =
        { RepoId = RepoId.create repoId
          Name = repoId
          Worktrees = branches |> List.map makeWorktree
          ArchivedWorktrees = []
          IsReady = true
          IsCollapsed = false
          Provider = None }

    let scrollHint (_, _, hint) = hint

    let cardTarget repoId branch = Card $"{RepoId.value (RepoId.create repoId)}/{branch}"

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type ScrollHintTests() =

    [<Test>]
    member _.``ArrowDown from last row of last section wraps to ScrollToTop``() =
        let repos = [ NavHelpers.makeRepo "repo" [ "a"; "b"; "c" ] ]
        let focused = Some (NavHelpers.cardTarget "repo" "c")
        let result = navigateSpatial "ArrowDown" 3 repos focused
        Assert.That(NavHelpers.scrollHint result, Is.EqualTo(ScrollToTop))

    [<Test>]
    member _.``ArrowDown from middle row returns Normal``() =
        // 9 cards, cols=3: rows are [a,b,c] [d,e,f] [g,h,i]
        // "b" is row 0 index 1, ArrowDown -> "e" at index 4, row 1 (middle row)
        let repos = [ NavHelpers.makeRepo "repo" [ "a"; "b"; "c"; "d"; "e"; "f"; "g"; "h"; "i" ] ]
        let focused = Some (NavHelpers.cardTarget "repo" "b")
        let result = navigateSpatial "ArrowDown" 3 repos focused
        Assert.That(NavHelpers.scrollHint result, Is.EqualTo(Normal))

    [<Test>]
    member _.``ArrowDown from second-to-last row to last row returns ScrollToBottom``() =
        let repos = [ NavHelpers.makeRepo "repo" [ "a"; "b"; "c"; "d"; "e"; "f" ] ]
        let focused = Some (NavHelpers.cardTarget "repo" "a")
        let result = navigateSpatial "ArrowDown" 3 repos focused
        Assert.That(NavHelpers.scrollHint result, Is.EqualTo(ScrollToBottom))

    [<Test>]
    member _.``ArrowUp from first row of first section returns ScrollToTop``() =
        let repos = [ NavHelpers.makeRepo "repo" [ "a"; "b"; "c"; "d" ] ]
        let focused = Some (NavHelpers.cardTarget "repo" "a")
        let result = navigateSpatial "ArrowUp" 3 repos focused
        Assert.That(NavHelpers.scrollHint result, Is.EqualTo(ScrollToTop))

    [<Test>]
    member _.``ArrowUp from middle row returns Normal``() =
        // 12 cards, cols=3: rows are [a,b,c] [d,e,f] [g,h,i] [j,k,l]
        // "g" is row 2 index 6, ArrowUp -> "d" at index 3, row 1 (middle row)
        let repos = [ NavHelpers.makeRepo "repo" [ "a"; "b"; "c"; "d"; "e"; "f"; "g"; "h"; "i"; "j"; "k"; "l" ] ]
        let focused = Some (NavHelpers.cardTarget "repo" "g")
        let result = navigateSpatial "ArrowUp" 3 repos focused
        Assert.That(NavHelpers.scrollHint result, Is.EqualTo(Normal))

    [<Test>]
    member _.``ArrowUp from second row to first row returns ScrollToTop``() =
        let repos = [ NavHelpers.makeRepo "repo" [ "a"; "b"; "c"; "d"; "e"; "f" ] ]
        let focused = Some (NavHelpers.cardTarget "repo" "d")
        let result = navigateSpatial "ArrowUp" 3 repos focused
        Assert.That(NavHelpers.scrollHint result, Is.EqualTo(ScrollToTop))

    [<Test>]
    member _.``ArrowRight wrapping from last card returns ScrollToTop``() =
        let repos = [ NavHelpers.makeRepo "repo" [ "a"; "b"; "c" ] ]
        let focused = Some (NavHelpers.cardTarget "repo" "c")
        let result = navigateSpatial "ArrowRight" 3 repos focused
        Assert.That(NavHelpers.scrollHint result, Is.EqualTo(ScrollToTop))

    [<Test>]
    member _.``ArrowLeft from first card of first section returns ScrollToTop``() =
        let repos = [ NavHelpers.makeRepo "repo" [ "a"; "b"; "c" ] ]
        let focused = Some (NavHelpers.cardTarget "repo" "a")
        let result = navigateSpatial "ArrowLeft" 3 repos focused
        Assert.That(NavHelpers.scrollHint result, Is.EqualTo(ScrollToTop))

    [<Test>]
    member _.``No focus returns ScrollToTop``() =
        let repos = [ NavHelpers.makeRepo "repo" [ "a"; "b" ] ]
        let result = navigateSpatial "ArrowDown" 3 repos None
        Assert.That(NavHelpers.scrollHint result, Is.EqualTo(ScrollToTop))

    [<Test>]
    member _.``Empty repos returns Normal``() =
        let result = navigateSpatial "ArrowDown" 3 [] None
        Assert.That(NavHelpers.scrollHint result, Is.EqualTo(Normal))

    [<Test>]
    member _.``Multiple sections last row detection only applies to last section``() =
        let repos =
            [ NavHelpers.makeRepo "first" [ "a"; "b"; "c" ]
              NavHelpers.makeRepo "second" [ "x"; "y"; "z" ] ]
        let focused = Some (NavHelpers.cardTarget "first" "c")
        let result = navigateSpatial "ArrowDown" 3 repos focused
        Assert.That(NavHelpers.scrollHint result, Is.EqualTo(Normal))

    [<Test>]
    member _.``Single column every card is its own row and last card is ScrollToBottom``() =
        let repos = [ NavHelpers.makeRepo "repo" [ "a"; "b"; "c" ] ]
        let focused = Some (NavHelpers.cardTarget "repo" "b")
        let result = navigateSpatial "ArrowDown" 1 repos focused
        Assert.That(NavHelpers.scrollHint result, Is.EqualTo(ScrollToBottom))

    [<Test>]
    member _.``Single column first card ArrowUp returns ScrollToTop``() =
        let repos = [ NavHelpers.makeRepo "repo" [ "a"; "b"; "c" ] ]
        let focused = Some (NavHelpers.cardTarget "repo" "a")
        let result = navigateSpatial "ArrowUp" 1 repos focused
        Assert.That(NavHelpers.scrollHint result, Is.EqualTo(ScrollToTop))

    [<Test>]
    member _.``Single column last card ArrowDown wraps to ScrollToTop``() =
        let repos = [ NavHelpers.makeRepo "repo" [ "a"; "b"; "c" ] ]
        let focused = Some (NavHelpers.cardTarget "repo" "c")
        let result = navigateSpatial "ArrowDown" 1 repos focused
        Assert.That(NavHelpers.scrollHint result, Is.EqualTo(ScrollToTop))
