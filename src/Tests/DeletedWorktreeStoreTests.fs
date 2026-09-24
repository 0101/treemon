module Tests.DeletedWorktreeStoreTests

open System.IO
open NUnit.Framework
open Server
open Server.DeletedWorktreeStore
open Tests.TestUtils

let private assertStoredPaths file (expected: Set<string>) =
    match readAtPath file with
    | Ok paths -> Assert.That(paths, Is.EqualTo(expected))
    | Error error -> Assert.Fail($"Could not read deleted worktrees: {error}")

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
[<NonParallelizable>]
type DeletedWorktreeStoreTests() =

    [<Test>]
    member _.``port-qualified files keep deletion records isolated``() =
        withTempConfigDir "treemon-deleted-worktrees" (fun dir ->
            let firstPort = filePathForPort 6101
            let secondPort = filePathForPort 6102
            let path = Path.Combine(dir, "tm-leftover") |> PathUtils.normalizePath

            assertOk (recordAtPath firstPort path) "record first port"
            Assert.Multiple(fun () ->
                Assert.That(firstPort, Is.Not.EqualTo secondPort)
                assertStoredPaths firstPort (Set.singleton path)
                assertStoredPaths secondPort Set.empty))

    [<Test>]
    member _.``records survive reload and the file disappears after the last cleanup``() =
        withTempDir "treemon-deleted-worktrees" (fun dir ->
            let file = Path.Combine(dir, "deleted-worktrees-6101.json")
            let first = Path.Combine(dir, "tm-first") |> PathUtils.normalizePath
            let second = Path.Combine(dir, "tm-second") |> PathUtils.normalizePath

            assertOk (recordAtPath file first) "record first path"
            assertOk (recordAtPath file second) "record second path"
            assertStoredPaths file (Set.ofList [ first; second ])

            assertOk (forgetAtPath file first) "forget first path"
            Assert.Multiple(fun () ->
                assertStoredPaths file (Set.singleton second)
                Assert.That(File.Exists file, Is.True))

            assertOk (forgetAtPath file second) "forget last path"
            Assert.Multiple(fun () ->
                assertStoredPaths file Set.empty
                Assert.That(File.Exists file, Is.False)))

    [<Test>]
    member _.``cannot clear a tombstone while its worktree is still on disk``() =
        withTempDir "treemon-deleted-worktrees" (fun dir ->
            let file = Path.Combine(dir, "deleted-worktrees-6101.json")
            let worktree = Path.Combine(dir, "tm-leftover") |> PathUtils.normalizePath
            Directory.CreateDirectory worktree |> ignore

            assertOk (recordAtPath file worktree) "record leftover path"

            Assert.Multiple(fun () ->
                match forgetAtPath file worktree with
                | Error error -> Assert.That(error, Is.EqualTo("Worktree path still exists on disk"))
                | Ok () -> Assert.Fail("An existing worktree must keep its deletion record")

                assertStoredPaths file (Set.singleton worktree)))

    [<TestCase("""{"paths":[42]}""")>]
    [<TestCase("{not valid JSON")>]
    member _.``malformed state fails closed instead of discarding earlier tombstones``(invalid: string) =
        withTempDir "treemon-deleted-worktrees" (fun dir ->
            let file = Path.Combine(dir, "deleted-worktrees-6101.json")
            File.WriteAllText(file, invalid)

            Assert.Multiple(fun () ->
                Assert.That(readAtPath file |> Result.isError, Is.True)
                Assert.That(recordAtPath file (Path.Combine(dir, "tm-new")) |> Result.isError, Is.True)
                Assert.That(File.ReadAllText file, Is.EqualTo(invalid))))

    [<Test>]
    member _.``concurrent records preserve every worktree path``() =
        withTempDir "treemon-deleted-worktrees" (fun dir ->
            let file = Path.Combine(dir, "deleted-worktrees-6101.json")
            let paths =
                [| for index in 0 .. 11 ->
                       Path.Combine(dir, $"tm-{index}") |> PathUtils.normalizePath |]

            paths
            |> Array.Parallel.map (recordAtPath file)
            |> Array.iter (fun result -> assertOk result "record concurrent path")

            assertStoredPaths file (Set.ofArray paths))
