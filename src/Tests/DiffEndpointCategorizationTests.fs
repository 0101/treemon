module Tests.DiffEndpointCategorizationTests

open System
open System.IO
open System.Net
open System.Net.Http
open System.Text.Json
open NUnit.Framework
open Shared
open global.Server
open Tests.DiffEndpointTestHelpers

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type DiffEndpointCategorizationTests() =

    let worktree =
        Path.Combine(Path.GetTempPath(), "treemon-diff-categorization")

    // Git order, deliberately unrelated to configuration order, so grouping is visible in the response.
    let changed =
        [ entry "docs/plan.md" None WorktreeDiff.Modified
          entry "README.md" None WorktreeDiff.Modified
          entry "src/Server/Api.fs" None WorktreeDiff.Modified
          entry "src/Tests/ApiTests.fs" None WorktreeDiff.Added
          entry
              "src/Client/App.fs"
              (Some "src/Client/Old.fs")
              WorktreeDiff.Renamed ]

    let categorization =
        DiffCategories.Configured
            [ DiffCategories.Branch
                  { Name = "Production code"
                    Children =
                      [ DiffCategories.Leaf
                            { Name = "Client"
                              Patterns = [ "src/Client/**" ] }
                        DiffCategories.Leaf
                            { Name = "Server"
                              Patterns = [ "src/Server/**" ] } ] }
              DiffCategories.Leaf
                  { Name = "Tests"
                    Patterns = [ "src/Tests/**" ] }
              DiffCategories.Leaf
                  { Name = "Docs"
                    Patterns = [ "docs/**" ] } ]

    let service: WorktreeDiffApi.Service =
        { GetSummary = fun _ _ _ -> async.Return(Ok(summary changed))
          GetLayerCounts =
            fun _ _ -> async.Return(uniformLayerCounts changed.Length)
          GetFile =
            fun _ _ _ _ requested ->
                async.Return(Ok(WorktreeDiff.Text requested.Path)) }

    let newHandlers () =
        WorktreeDiffApi.createHandlersWithStore
            (WorktreeDiffApi.createIdentityStore ())
            service
            (fun _ -> Guid.NewGuid().ToString("N"))

    [<Test>]
    member _.``configured summaries group files in configuration order and carry each category path``() =
        let body =
            summaryResponse
                (newHandlers ())
                categorization
                worktree
                (Guid.NewGuid())

        Assert.Multiple(fun () ->
            Assert.That(
                summaryCategoryPaths body,
                Is.EqualTo(
                    [ "src/Client/App.fs", [ "Production code"; "Client" ]
                      "src/Server/Api.fs", [ "Production code"; "Server" ]
                      "src/Tests/ApiTests.fs", [ "Tests" ]
                      "docs/plan.md", [ "Docs" ]
                      "README.md", [] ]
                )
            )

            Assert.That(
                summaryCategorization body,
                Is.EqualTo(("configured", Option<string>.None))
            ))

    [<Test>]
    member _.``missing and invalid categorizations keep the ungrouped order and empty category paths``() =
        let ungrouped =
            changed |> List.map (fun file -> file.Path, ([]: string list))

        let reason = "each category needs a name"

        let missingBody =
            summaryResponse
                (newHandlers ())
                DiffCategories.Missing
                worktree
                (Guid.NewGuid())

        let invalidBody =
            summaryResponse
                (newHandlers ())
                (DiffCategories.Invalid reason)
                worktree
                (Guid.NewGuid())

        Assert.Multiple(fun () ->
            Assert.That(summaryCategoryPaths missingBody, Is.EqualTo(ungrouped))

            Assert.That(
                summaryCategorization missingBody,
                Is.EqualTo(("missing", Option<string>.None))
            )

            Assert.That(summaryCategoryPaths invalidBody, Is.EqualTo(ungrouped))

            Assert.That(
                summaryCategorization invalidBody,
                Is.EqualTo(("invalid", Some reason))
            ))

    [<Test>]
    member _.``identities issued after grouping still resolve the grouped file``() =
        let handlers = newHandlers ()
        let viewer = Guid.NewGuid()
        let body = summaryResponse handlers categorization worktree viewer
        let moved = summaryIdentity "src/Client/App.fs" body

        let identities =
            use doc = JsonDocument.Parse(body)

            doc.RootElement.GetProperty("files").EnumerateArray()
            |> Seq.map _.GetProperty("identity").GetString()
            |> Set.ofSeq

        Assert.That(identities.Count, Is.EqualTo(changed.Length))

        fileResponse handlers worktree viewer moved
        |> assertJson (
            DiffFileResult.Text(
                { fileSummary
                      moved
                      "src/Client/App.fs"
                      (Some "src/Client/Old.fs")
                      DiffChangeKind.Renamed with
                    CategoryPath = [ "Production code"; "Client" ] },
                "src/Client/App.fs"
            )
            |> WorktreeDiffApi.serializeFileResult
        )

/// The canvas server resolves a diff request's owning repository root from the scheduler and reads
/// `.treemon.json` there on every summary request. These tests drive real HTTP against a real
/// configuration file with a fake diff service, so what they prove is that resolution and re-read —
/// not Git behavior.
[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
[<NonParallelizable>]
type DiffEndpointRepositoryConfigurationTests() =

    // Git order, deliberately unrelated to configuration order.
    let changed =
        [ entry "docs/plan.md" None WorktreeDiff.Modified
          entry "README.md" None WorktreeDiff.Modified
          entry "src/Server/Api.fs" None WorktreeDiff.Modified ]

    let service =
        fakeService
            (Ok(summary changed))
            (fun requested -> Ok(WorktreeDiff.Text requested.Path))

    let rootConfiguration =
        """{ "baseBranch": "main",
             "diffCategories": [
               { "name": "Server", "patterns": ["src/Server/**"] },
               { "name": "Docs", "patterns": ["docs/**"] }
             ] }"""

    let grouped =
        [ "src/Server/Api.fs", [ "Server" ]
          "docs/plan.md", [ "Docs" ]
          "README.md", ([]: string list) ]

    let ungrouped =
        changed |> List.map (fun file -> file.Path, ([]: string list))

    /// A repository root and one linked worktree under a throwaway directory. Only the diff
    /// endpoint's own view of them matters here, so neither is a real Git worktree.
    let withRepository (name: string) (action: string -> string -> unit) =
        TestUtils.withTempDir $"treemon-diff-config-{name}" (fun tempDir ->
            let repoRoot = Path.Combine(tempDir, "repo")
            let linked = Path.Combine(tempDir, "linked")
            Directory.CreateDirectory(repoRoot) |> ignore
            Directory.CreateDirectory(linked) |> ignore
            action repoRoot linked)

    /// A server whose scheduler keys the repository by its root exactly as discovery does, which is
    /// what makes the configuration the endpoint reads the root's own.
    let withServer repoRoot worktreePaths service newIdentity action =
        withDiffServerRepository
            (PathUtils.toRepoId repoRoot)
            ProcessRunner.argumentListResponseDeadlineMs
            worktreePaths
            "origin"
            "main"
            service
            newIdentity
            (fun _ client baseUrl -> action client baseUrl)

    let summaryBody (client: HttpClient) baseUrl worktreePath =
        use response = get client (worktreeUrl baseUrl worktreePath "diff-summary")
        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK))
        getResponseBody response

    [<Test>]
    member _.``a linked worktree is classified by the root repository configuration``() =
        withRepository "linked" (fun repoRoot linked ->
            File.WriteAllText(Path.Combine(repoRoot, ".treemon.json"), rootConfiguration)

            // A worktree-local file must never win: reading it would put every file under one
            // category, so its absence from the responses is what proves the root was used.
            File.WriteAllText(
                Path.Combine(linked, ".treemon.json"),
                """{ "diffCategories": [ { "name": "Worktree local", "patterns": ["**"] } ] }"""
            )

            withServer
                repoRoot
                [ repoRoot; linked ]
                service
                _.Path
                (fun client baseUrl ->
                    let linkedBody = summaryBody client baseUrl linked
                    let rootBody = summaryBody client baseUrl repoRoot

                    Assert.Multiple(fun () ->
                        Assert.That(summaryCategoryPaths linkedBody, Is.EqualTo(grouped))

                        Assert.That(
                            summaryCategorization linkedBody,
                            Is.EqualTo(("configured", Option<string>.None))
                        )

                        Assert.That(summaryCategoryPaths rootBody, Is.EqualTo(grouped))

                        Assert.That(
                            summaryCategorization rootBody,
                            Is.EqualTo(("configured", Option<string>.None))
                        ))))

    [<Test>]
    member _.``an edited configuration reaches the next summary without a scheduler cycle``() =
        withRepository "reread" (fun repoRoot _ ->
            let configPath = Path.Combine(repoRoot, ".treemon.json")

            withServer
                repoRoot
                [ repoRoot ]
                service
                _.Path
                (fun client baseUrl ->
                    // One server, one scheduler snapshot, no refresh in between: every difference
                    // below comes from re-reading the file on the request itself.
                    let beforeConfiguration = summaryBody client baseUrl repoRoot

                    File.WriteAllText(configPath, rootConfiguration)
                    let afterWrite = summaryBody client baseUrl repoRoot

                    File.WriteAllText(
                        configPath,
                        """{ "diffCategories": [ { "name": "Server" } ] }"""
                    )

                    let afterInvalidEdit = summaryBody client baseUrl repoRoot

                    File.Delete(configPath)
                    let afterDelete = summaryBody client baseUrl repoRoot

                    let invalidStatus, invalidReason =
                        summaryCategorization afterInvalidEdit

                    Assert.Multiple(fun () ->
                        Assert.That(
                            summaryCategorization beforeConfiguration,
                            Is.EqualTo(("missing", Option<string>.None))
                        )

                        Assert.That(
                            summaryCategoryPaths beforeConfiguration,
                            Is.EqualTo(ungrouped)
                        )

                        Assert.That(
                            summaryCategorization afterWrite,
                            Is.EqualTo(("configured", Option<string>.None))
                        )

                        Assert.That(summaryCategoryPaths afterWrite, Is.EqualTo(grouped))
                        Assert.That(invalidStatus, Is.EqualTo("invalid"))

                        Assert.That(
                            invalidReason |> Option.defaultValue "",
                            Does.Contain("not both")
                        )

                        Assert.That(
                            summaryCategoryPaths afterInvalidEdit,
                            Is.EqualTo(ungrouped)
                        )

                        Assert.That(
                            summaryCategorization afterDelete,
                            Is.EqualTo(("missing", Option<string>.None))
                        )

                        Assert.That(summaryCategoryPaths afterDelete, Is.EqualTo(ungrouped)))))

    [<Test>]
    member _.``the categorization route answers the current configuration without running Git``() =
        withRepository "poll" (fun repoRoot _ ->
            let configPath = Path.Combine(repoRoot, ".treemon.json")

            // Any Git work here would be a bug: the poll exists so a waiting viewer costs a file
            // read, not a diff.
            let neverCallService: WorktreeDiffApi.Service =
                { GetSummary = fun _ _ _ -> failwith "the categorization route ran a diff"
                  GetLayerCounts =
                    fun _ _ -> failwith "the categorization route counted layers"
                  GetFile = fun _ _ _ _ -> failwith "the categorization route read a file" }

            withServer
                repoRoot
                [ repoRoot ]
                neverCallService
                _.Path
                (fun client baseUrl ->
                    let categorizationBody () =
                        use response =
                            get client (worktreeUrl baseUrl repoRoot "diff-categorization")

                        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK))
                        getResponseBody response

                    let read (body: string) =
                        use doc = JsonDocument.Parse(body)
                        let root = doc.RootElement

                        root.GetProperty("status").GetString(),
                        root.GetProperty("revision").GetString()

                    let missingStatus, missingRevision = read (categorizationBody ())

                    File.WriteAllText(configPath, rootConfiguration)
                    let configuredStatus, configuredRevision = read (categorizationBody ())

                    // Reformatting alone must not read as a change, or a waiting viewer would
                    // refresh on an edit that altered nothing.
                    File.WriteAllText(configPath, rootConfiguration.Replace("\n", "\n  "))
                    let _, reformattedRevision = read (categorizationBody ())

                    File.WriteAllText(
                        configPath,
                        """{ "diffCategories": [ { "name": "Everything", "patterns": ["src/**"] } ] }"""
                    )

                    let _, rewrittenRevision = read (categorizationBody ())

                    Assert.Multiple(fun () ->
                        Assert.That(missingStatus, Is.EqualTo("missing"))
                        Assert.That(configuredStatus, Is.EqualTo("configured"))

                        Assert.That(
                            configuredRevision,
                            Is.Not.EqualTo(missingRevision),
                            "writing a configuration must change the revision"
                        )

                        Assert.That(
                            reformattedRevision,
                            Is.EqualTo(configuredRevision),
                            "reformatting the file must not read as a change"
                        )

                        Assert.That(
                            rewrittenRevision,
                            Is.Not.EqualTo(configuredRevision),
                            "a rewrite that stays configured must still change the revision"
                        ))))

    [<Test>]
    member _.``the categorization route requires a viewer instance and takes no parameters``() =
        withRepository "contract" (fun repoRoot _ ->
            File.WriteAllText(Path.Combine(repoRoot, ".treemon.json"), rootConfiguration)

            let neverCallService: WorktreeDiffApi.Service =
                { GetSummary = fun _ _ _ -> failwith "the categorization route ran a diff"
                  GetLayerCounts =
                    fun _ _ -> failwith "the categorization route counted layers"
                  GetFile = fun _ _ _ _ _ -> failwith "the categorization route read a file" }

            withServer
                repoRoot
                [ repoRoot ]
                neverCallService
                _.Path
                (fun client baseUrl ->
                    let url = worktreeUrl baseUrl repoRoot "diff-categorization"

                    use headerless = new HttpClient()
                    use missingViewer = get headerless url
                    use parameterized = get client $"{url}?committed=true"
                    use accepted = get client url

                    Assert.Multiple(fun () ->
                        Assert.That(
                            missingViewer.StatusCode,
                            Is.EqualTo(HttpStatusCode.BadRequest)
                        )

                        Assert.That(
                            getResponseBody missingViewer,
                            Is.EqualTo("Invalid diff viewer")
                        )

                        Assert.That(
                            parameterized.StatusCode,
                            Is.EqualTo(HttpStatusCode.BadRequest)
                        )

                        Assert.That(
                            getResponseBody parameterized,
                            Is.EqualTo("Invalid diff-categorization query")
                        )

                        Assert.That(accepted.StatusCode, Is.EqualTo(HttpStatusCode.OK)))))

    [<Test>]
    member _.``an unknown worktree still 404s instead of resolving a configuration``() =
        withRepository "unknown" (fun repoRoot _ ->
            File.WriteAllText(Path.Combine(repoRoot, ".treemon.json"), rootConfiguration)

            let neverCallService: WorktreeDiffApi.Service =
                { GetSummary = fun _ _ _ -> failwith "Unknown worktree reached diff summary"
                  GetLayerCounts =
                    fun _ _ -> failwith "Unknown worktree reached diff layer counts"
                  GetFile = fun _ _ _ _ _ -> failwith "Unknown worktree reached diff file" }

            withServer
                repoRoot
                [ repoRoot ]
                neverCallService
                (fun _ -> failwith "Unknown worktree issued an identity")
                (fun client baseUrl ->
                    use response =
                        get
                            client
                            (worktreeUrl
                                baseUrl
                                (Path.Combine(repoRoot, "..", "unknown"))
                                "diff-summary")

                    let body = getResponseBody response

                    Assert.Multiple(fun () ->
                        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.NotFound))
                        Assert.That(body, Is.EqualTo("Unknown worktree")))))
