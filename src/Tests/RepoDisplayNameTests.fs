module Tests.RepoDisplayNameTests

open NUnit.Framework
open Server

/// A card is labelled by its folder, which stops telling cards apart when an agent keeps its own
/// clone of a shared repository.
[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type RepoDisplayNameTests() =

    [<Test>]
    member _.``a folder name that is already unique is left alone``() =
        let names = PathUtils.displayNames [ "/home/me/code/treemon"; "/home/me/code/other" ]

        Assert.Multiple(fun () ->
            Assert.That(names["/home/me/code/treemon"], Is.EqualTo "treemon")
            Assert.That(names["/home/me/code/other"], Is.EqualTo "other"))

    // The case that prompted this: one repository, cloned once per agent.
    [<Test>]
    member _.``clones of one repository are told apart by the segment that differs``() =
        let names =
            PathUtils.displayNames
                [ "/home/me/git/CentroSpectrum/blue/git/Centro"
                  "/home/me/git/CentroSpectrum/red/git/Centro" ]

        Assert.Multiple(fun () ->
            Assert.That(names["/home/me/git/CentroSpectrum/blue/git/Centro"], Is.EqualTo "blue/…/Centro")
            Assert.That(names["/home/me/git/CentroSpectrum/red/git/Centro"], Is.EqualTo "red/…/Centro"))

    // Nothing is elided when the distinguishing segment is the parent: there is nothing in between.
    [<Test>]
    member _.``an adjacent parent is shown whole``() =
        let names = PathUtils.displayNames [ "/a/blue/Centro"; "/a/red/Centro" ]

        Assert.Multiple(fun () ->
            Assert.That(names["/a/blue/Centro"], Is.EqualTo "blue/Centro")
            Assert.That(names["/a/red/Centro"], Is.EqualTo "red/Centro"))

    [<Test>]
    member _.``only the roots that collide are lengthened``() =
        let names =
            PathUtils.displayNames
                [ "/home/me/git/CentroSpectrum/blue/git/Centro"
                  "/home/me/git/CentroSpectrum/red/git/Centro"
                  "/home/me/git/CentroSpectrum/green" ]

        Assert.Multiple(fun () ->
            Assert.That(names["/home/me/git/CentroSpectrum/green"], Is.EqualTo "green")
            Assert.That(names["/home/me/git/CentroSpectrum/blue/git/Centro"], Is.EqualTo "blue/…/Centro"))

    [<Test>]
    member _.``a Windows path is split on its own separator``() =
        let names =
            PathUtils.displayNames [ @"C:\code\blue\git\Centro"; @"C:\code\red\git\Centro" ]

        Assert.That(names[@"C:\code\blue\git\Centro"], Is.EqualTo "blue/…/Centro")

    [<Test>]
    member _.``a single root needs no disambiguation``() =
        Assert.That(PathUtils.displayNames [ "/home/me/git/CentroSpectrum/blue/git/Centro" ]
                    |> Map.find "/home/me/git/CentroSpectrum/blue/git/Centro",
                    Is.EqualTo "Centro")

    // The same folder watched twice is genuinely one thing; sharing a label says so.
    [<Test>]
    member _.``the same path twice does not loop looking for a difference``() =
        let names = PathUtils.displayNames [ "/a/b/Centro"; "/a/b/Centro" ]
        Assert.That(names["/a/b/Centro"], Is.EqualTo "Centro")
