/// End-to-end verification of per-repository diff categories over the real path: a temporary Git
/// repository, a repository-root `.treemon.json`, the live diff service, a real HTTP diff server on
/// a free port, and — for the rendered outline — a real browser. Nothing is routed or faked here, so
/// what these fixtures prove is classification, ordering, immediate re-reading of an edited
/// configuration, rename precedence, and the rendered hierarchy exactly as a reader gets them.
module Tests.DiffCategoryEndToEndTests

open System
open System.IO
open System.Net
open System.Net.Http
open System.Text.Json
open System.Threading
open Microsoft.Playwright
open Microsoft.Playwright.NUnit
open NUnit.Framework
open Shared
open global.Server
open Tests.DiffEndpointTestHelpers

/// A file long enough that changing one line keeps Git's rename detection above its similarity
/// threshold when the file is later moved to another category.
let private movedFileText (firstLine: string) =
    [ 1..12 ]
    |> List.map (fun index ->
        if index = 1 then firstLine else $"let value{index} = {index}")
    |> String.concat Environment.NewLine

/// The committed baseline. `.treemon.json` and `.agents/` are ignored so the configuration the test
/// rewrites and the provisioned viewer never enter the diff the assertions are about.
let private baselineFiles =
    [ ".gitignore", String.concat Environment.NewLine [ ".treemon.json"; ".agents/"; "" ]
      "README.md", "readme baseline"
      "docs/plan.md", "plan baseline"
      "scripts/tool.ps1", "Write-Output 'baseline'"
      "src/Client/App.fs", "let app = 0"
      "src/Client/Moved.fs", movedFileText "let value1 = 0"
      "src/Server/Api.fs", "let api = 0"
      "src/Shared/Types.fs", "type Payload = { Value: int }" ]

/// The feature branch's changes: one file in every declared area plus an unmatched script.
let private featureFiles =
    [ "README.md", "readme changed"
      "docs/plan.md", "plan changed"
      "scripts/tool.ps1", "Write-Output 'changed'"
      "src/Client/App.fs", "let app = 1"
      "src/Client/Moved.fs", movedFileText "let value1 = 1"
      "src/Client/View.fs", "let view = 1"
      "src/Server/Api.fs", "let api = 1"
      "src/Shared/Types.fs", "type Payload = { Value: string }"
      "src/Tests/ApiTests.fs", "let apiTests = 1" ]

/// Exactly the paths the fixture changes; any missing or extra path in a summary is a failure.
let private declaredPaths =
    featureFiles |> List.map fst

let private writeRepoFile (repoRoot: string) (relativePath: string) (contents: string) =
    let path = Path.Combine(repoRoot, relativePath)
    Path.GetDirectoryName(path) |> Directory.CreateDirectory |> ignore
    File.WriteAllText(path, contents)

let private commitAll repoRoot message =
    GitTestHelpers.gitOk repoRoot [ "add"; "--"; "." ]
    GitTestHelpers.gitOk repoRoot [ "commit"; "-m"; message ]

let private createCategoryRepo (repoRoot: string) =
    GitTestHelpers.initRepoOnMain repoRoot

    baselineFiles
    |> List.iter (fun (path, contents) -> writeRepoFile repoRoot path contents)

    commitAll repoRoot "category baseline"
    GitTestHelpers.gitOk repoRoot [ "checkout"; "-b"; "feature" ]

    featureFiles
    |> List.iter (fun (path, contents) -> writeRepoFile repoRoot path contents)

    commitAll repoRoot "category feature"
    DiffProvisioner.provisionViewer repoRoot |> ignore

/// The specification's own example, plus the unrelated field an edit must preserve.
let private specConfiguration =
    """{
  "baseBranch": "main",
  "diffCategories": [
    {
      "name": "Production code",
      "children": [
        { "name": "Client", "patterns": ["src/Client/**"] },
        { "name": "Server", "patterns": ["src/Server/**"] },
        { "name": "Shared", "patterns": ["src/Shared/**"] }
      ]
    },
    { "name": "Tests", "patterns": ["src/Tests/**", "**/*Tests.fs"] },
    { "name": "Docs", "patterns": ["docs/**"] },
    { "name": "Instructions", "patterns": ["AGENTS.md", ".github/instructions/**"] }
  ]
}"""

/// The same node set with one pattern moved from `Shared` to `Server`, so exactly one known source
/// path changes category and every other classification has to stay put.
let private movedPatternConfiguration =
    """{
  "baseBranch": "main",
  "diffCategories": [
    {
      "name": "Production code",
      "children": [
        { "name": "Client", "patterns": ["src/Client/**"] },
        { "name": "Server", "patterns": ["src/Server/**", "src/Shared/**"] },
        { "name": "Shared", "patterns": ["src/Shared/Legacy/**"] }
      ]
    },
    { "name": "Tests", "patterns": ["src/Tests/**", "**/*Tests.fs"] },
    { "name": "Docs", "patterns": ["docs/**"] },
    { "name": "Instructions", "patterns": ["AGENTS.md", ".github/instructions/**"] }
  ]
}"""

/// Configuration order, which is the order groups must appear in; unmatched files sort after all of
/// them.
let private configurationOrder =
    [ [ "Production code"; "Client" ]
      [ "Production code"; "Server" ]
      [ "Production code"; "Shared" ]
      [ "Tests" ]
      [ "Docs" ]
      [ "Instructions" ] ]

let private groupIndex (categoryPath: string list) =
    configurationOrder
    |> List.tryFindIndex ((=) categoryPath)
    |> Option.defaultValue (List.length configurationOrder)

let private formatClassification (path: string, categoryPath: string list) =
    let names = String.concat " > " categoryPath
    $"{path} -> [{names}]"

let private formatted classified =
    classified |> List.map formatClassification

/// What the server actually answered, written to the test's output so a verification run's evidence
/// stands on its own rather than only as a green assertion.
let private report (label: string) (lines: string seq) =
    TestContext.Out.WriteLine($"--- {label} ---")
    lines |> Seq.iter TestContext.Out.WriteLine

let private allLayersQuery = "?committed=true&local=true&untracked=true"

let private summaryStatus (json: string) =
    use doc = JsonDocument.Parse(json)
    doc.RootElement.GetProperty("status").GetString()

/// Whether every browser-facing file carries a `categoryPath` array, which the wire contract
/// requires of a ready summary regardless of configuration state.
let private everyFileCarriesCategoryPath (json: string) =
    use doc = JsonDocument.Parse(json)

    doc.RootElement.GetProperty("files").EnumerateArray()
    |> Seq.forall (fun file ->
        match file.TryGetProperty("categoryPath") with
        | true, value -> value.ValueKind = JsonValueKind.Array
        | _ -> false)

let private renamedEntries (json: string) =
    use doc = JsonDocument.Parse(json)

    doc.RootElement.GetProperty("files").EnumerateArray()
    |> Seq.filter (fun file -> file.GetProperty("change").GetString() = "renamed")
    |> Seq.map (fun file ->
        let displayPath = file.GetProperty("displayPath").GetString()
        let oldValue = file.GetProperty("oldDisplayPath")

        let oldPath =
            if oldValue.ValueKind = JsonValueKind.Null then
                ""
            else
                oldValue.GetString()

        $"{oldPath} => {displayPath}")
    |> List.ofSeq

let private configuredBaseBranch (configPath: string) =
    use doc = JsonDocument.Parse(File.ReadAllText(configPath))
    doc.RootElement.GetProperty("baseBranch").GetString()

let private configurationState (repoRoot: string) =
    match DiffCategories.read repoRoot with
    | DiffCategories.Configured _ -> "configured"
    | DiffCategories.Missing -> "missing"
    | DiffCategories.Invalid reason -> $"invalid: {reason}"

/// A scheduler that knows the repository exactly as discovery does — keyed by its root — because
/// the diff endpoint reads the categorization from that key.
let private agentKnowing (repoRoot: string) =
    let agent = RefreshScheduler.createAgent ()

    let info: GitWorktree.WorktreeInfo =
        { Path = PathUtils.normalizePath repoRoot
          Head = ""
          Branch = Some "feature" }

    agent.Post(
        RefreshScheduler.repositoryDiscoveryUpdate
            (PathUtils.toRepoId repoRoot)
            (Some [ info ])
            "origin"
            "main"
    )

    agent.PostAndAsyncReply(RefreshScheduler.GetState)
    |> TestUtils.runAsync
    |> ignore

    agent

[<TestFixture>]
[<Category("E2E")>]
[<Category("Canvas")>]
[<NonParallelizable>]
type DiffCategoryRepositoryE2ETests() =

    let requestSummary (client: HttpClient) baseUrl repoRoot =
        use response =
            get client (worktreeUrl baseUrl repoRoot "diff-summary" + allLayersQuery)

        Assert.That(response.StatusCode, Is.EqualTo(HttpStatusCode.OK))
        getResponseBody response

    [<Test>]
    member _.``a real repository groups, orders, and reclassifies through the live diff server``() =
        TestUtils.withTempDir "treemon-diff-category-e2e" (fun tempDir ->
            let repoRoot = Path.Combine(tempDir, "repo")
            createCategoryRepo repoRoot
            let configPath = Path.Combine(repoRoot, ".treemon.json")

            withDiffServerRepository
                (PathUtils.toRepoId repoRoot)
                ProcessRunner.argumentListResponseDeadlineMs
                [ repoRoot ]
                "origin"
                "main"
                WorktreeDiffApi.liveService
                WorktreeDiffApi.newOpaqueIdentity
                (fun _ client baseUrl ->
                    // 1. The fixture's diff, before any categorization exists.
                    let ungroupedBody = requestSummary client baseUrl repoRoot
                    let ungrouped = summaryCategoryPaths ungroupedBody
                    let ungroupedPaths = ungrouped |> List.map fst
                    report "1. summary before any configuration" (formatted ungrouped)

                    Assert.Multiple(fun () ->
                        Assert.That(
                            List.sort ungroupedPaths,
                            Is.EqualTo(List.sort declaredPaths),
                            "the diff path set must equal the fixture declaration"
                        )

                        Assert.That(summaryStatus ungroupedBody, Is.EqualTo("ready"))

                        Assert.That(
                            summaryCategorization ungroupedBody,
                            Is.EqualTo(("missing", Option<string>.None))
                        ))

                    // 2. The specification's configuration at the repository root.
                    File.WriteAllText(configPath, specConfiguration)
                    Assert.That(configurationState repoRoot, Is.EqualTo("configured"))

                    // 3. The same server, the same scheduler snapshot, one more request.
                    let groupedBody = requestSummary client baseUrl repoRoot
                    let grouped = summaryCategoryPaths groupedBody
                    report "3. raw configured summary" [ groupedBody ]
                    report "3. configured summary in response order" (formatted grouped)

                    Assert.Multiple(fun () ->
                        Assert.That(summaryStatus groupedBody, Is.EqualTo("ready"))

                        Assert.That(
                            summaryCategorization groupedBody,
                            Is.EqualTo(("configured", Option<string>.None))
                        )

                        Assert.That(
                            everyFileCarriesCategoryPath groupedBody,
                            Is.True,
                            "every file must carry a categoryPath"
                        ))

                    // 4. Exact membership.
                    Assert.That(
                        formatted (grouped |> List.sortBy fst),
                        Is.EqualTo(
                            formatted
                                [ "README.md", []
                                  "docs/plan.md", [ "Docs" ]
                                  "scripts/tool.ps1", []
                                  "src/Client/App.fs", [ "Production code"; "Client" ]
                                  "src/Client/Moved.fs", [ "Production code"; "Client" ]
                                  "src/Client/View.fs", [ "Production code"; "Client" ]
                                  "src/Server/Api.fs", [ "Production code"; "Server" ]
                                  "src/Shared/Types.fs", [ "Production code"; "Shared" ]
                                  "src/Tests/ApiTests.fs", [ "Tests" ] ]
                        )
                    )

                    // 5. Exact ordering: the ungrouped summary's own order, stably regrouped into
                    //    configuration order with the unmatched files last. `List.sortBy` is stable,
                    //    so this states contiguity, group order and within-group order at once
                    //    without restating Git's enumeration.
                    let classification = Map.ofList grouped

                    let regrouped =
                        ungroupedPaths
                        |> List.map (fun path -> path, classification[path])
                        |> List.sortBy (snd >> groupIndex)

                    Assert.Multiple(fun () ->
                        Assert.That(formatted grouped, Is.EqualTo(formatted regrouped))

                        Assert.That(
                            formatted grouped,
                            Is.EqualTo(
                                formatted
                                    [ "src/Client/App.fs", [ "Production code"; "Client" ]
                                      "src/Client/Moved.fs", [ "Production code"; "Client" ]
                                      "src/Client/View.fs", [ "Production code"; "Client" ]
                                      "src/Server/Api.fs", [ "Production code"; "Server" ]
                                      "src/Shared/Types.fs", [ "Production code"; "Shared" ]
                                      "src/Tests/ApiTests.fs", [ "Tests" ]
                                      "docs/plan.md", [ "Docs" ]
                                      "README.md", []
                                      "scripts/tool.ps1", [] ]
                            )
                        ))

                    // 7. Only `diffCategories` is rewritten, and the next request sees it without a
                    //    restart or a scheduler cycle.
                    File.WriteAllText(configPath, movedPatternConfiguration)
                    let editedBody = requestSummary client baseUrl repoRoot

                    report
                        "7. after rewriting only diffCategories"
                        (formatted (summaryCategoryPaths editedBody))

                    Assert.Multiple(fun () ->
                        Assert.That(configuredBaseBranch configPath, Is.EqualTo("main"))

                        Assert.That(
                            summaryCategorization editedBody,
                            Is.EqualTo(("configured", Option<string>.None))
                        )

                        Assert.That(
                            formatted (summaryCategoryPaths editedBody |> List.sortBy fst),
                            Is.EqualTo(
                                formatted
                                    [ "README.md", []
                                      "docs/plan.md", [ "Docs" ]
                                      "scripts/tool.ps1", []
                                      "src/Client/App.fs", [ "Production code"; "Client" ]
                                      "src/Client/Moved.fs", [ "Production code"; "Client" ]
                                      "src/Client/View.fs", [ "Production code"; "Client" ]
                                      "src/Server/Api.fs", [ "Production code"; "Server" ]
                                      "src/Shared/Types.fs", [ "Production code"; "Server" ]
                                      "src/Tests/ApiTests.fs", [ "Tests" ] ]
                            )
                        ))

                    // 8. A move across categories, classified on its new path.
                    File.WriteAllText(configPath, specConfiguration)

                    GitTestHelpers.gitOk
                        repoRoot
                        [ "mv"; "src/Client/Moved.fs"; "src/Server/Moved.fs" ]

                    let renamedBody = requestSummary client baseUrl repoRoot

                    report
                        "8. after git mv src/Client/Moved.fs src/Server/Moved.fs"
                        (formatted (summaryCategoryPaths renamedBody))

                    Assert.Multiple(fun () ->
                        Assert.That(
                            renamedEntries renamedBody,
                            Is.EqualTo([ "src/Client/Moved.fs => src/Server/Moved.fs" ])
                        )

                        Assert.That(
                            summaryCategorization renamedBody,
                            Is.EqualTo(("configured", Option<string>.None))
                        )

                        Assert.That(
                            formatted (summaryCategoryPaths renamedBody |> List.sortBy fst),
                            Is.EqualTo(
                                formatted
                                    [ "README.md", []
                                      "docs/plan.md", [ "Docs" ]
                                      "scripts/tool.ps1", []
                                      "src/Client/App.fs", [ "Production code"; "Client" ]
                                      "src/Client/View.fs", [ "Production code"; "Client" ]
                                      "src/Server/Api.fs", [ "Production code"; "Server" ]
                                      "src/Server/Moved.fs", [ "Production code"; "Server" ]
                                      "src/Shared/Types.fs", [ "Production code"; "Shared" ]
                                      "src/Tests/ApiTests.fs", [ "Tests" ] ]
                            )
                        ))))

/// The rendered outline of the same real repository, read out of a real browser against the same
/// server, so nesting, group order and header counts are proven as displayed rather than as JSON.
[<TestFixture>]
[<Category("E2E")>]
[<Category("Canvas")>]
[<NonParallelizable>]
type DiffCategoryDocumentE2ETests() =
    inherit PageTest()

    /// The layer preference the document reads before its first request, so the browser asks for the
    /// same three layers the HTTP verification does.
    let allLayersInitScript =
        """(() => {
            const filterKey =
                'treemon.diff.layers:' + location.pathname.replace(/\/diff\.html$/, '');
            localStorage.setItem(
                filterKey,
                JSON.stringify({ committed: true, local: true, untracked: true })
            );
        })()"""

    /// The whole displayed hierarchy: depth, name, the header's own count, the number of file rows
    /// actually beneath it, and its disclosure — plus every file row in document order.
    let outlineScript =
        """() => {
            const lines = [];
            const walk = (section, depth) => {
                const button = section.querySelector(':scope > .category-entry');
                const panel = section.querySelector(':scope > .category-panel');
                lines.push([
                    depth,
                    button.querySelector('.category-name').textContent,
                    button.querySelector('.category-count').textContent,
                    String(panel.querySelectorAll('.file-entry').length),
                    button.getAttribute('aria-expanded')
                ].join('|'));
                [...panel.children].forEach(child => {
                    if (child.classList.contains('category-item')) walk(child, depth + 1);
                    else lines.push(
                        [depth + 1, 'file', child.querySelector('.file-path').textContent].join('|')
                    );
                });
            };
            document
                .querySelectorAll('#file-list > .category-item')
                .forEach(section => walk(section, 1));
            return lines;
        }"""

    [<Test>]
    member this.``the served diff document renders the repository's real category hierarchy``() =
        task {
            let tempDir =
                Path.Combine(
                    Path.GetTempPath(),
                    $"treemon-diff-category-doc-{Guid.NewGuid():N}"
                )

            Directory.CreateDirectory(tempDir) |> ignore
            let repoRoot = Path.Combine(tempDir, "repo")

            try
                createCategoryRepo repoRoot

                File.WriteAllText(
                    Path.Combine(repoRoot, ".treemon.json"),
                    specConfiguration
                )

                let port = TestUtils.getFreeTcpPort ()

                use host =
                    CanvasDocServer.createHost
                        (agentKnowing repoRoot)
                        WorktreeDiffApi.liveService
                        WorktreeDiffApi.newOpaqueIdentity
                        port

                do! host.StartAsync(CancellationToken.None)

                try
                    do! this.Page.AddInitScriptAsync(allLayersInitScript)

                    let! _ =
                        this.Page.GotoAsync(
                            worktreeUrl $"http://127.0.0.1:{port}" repoRoot "diff.html",
                            PageGotoOptions(WaitUntil = WaitUntilState.Load)
                        )

                    do!
                        this.Page.Locator(".category-entry")
                            .Nth(6)
                            .WaitForAsync(LocatorWaitForOptions(Timeout = 15000.0f))

                    let! outline = this.Page.EvaluateAsync<string array>(outlineScript)
                    report "6. browser-rendered category outline" outline

                    Assert.That(
                        outline,
                        Is.EqualTo(
                            [| "1|Production code|5|5|true"
                               "2|Client|3|3|true"
                               "3|file|src/Client/App.fs"
                               "3|file|src/Client/Moved.fs"
                               "3|file|src/Client/View.fs"
                               "2|Server|1|1|true"
                               "3|file|src/Server/Api.fs"
                               "2|Shared|1|1|true"
                               "3|file|src/Shared/Types.fs"
                               "1|Tests|1|1|true"
                               "2|file|src/Tests/ApiTests.fs"
                               "1|Docs|1|1|true"
                               "2|file|docs/plan.md"
                               "1|Other|2|2|true"
                               "2|file|README.md"
                               "2|file|scripts/tool.ps1" |]
                        )
                    )
                finally
                    host.StopAsync(CancellationToken.None).GetAwaiter().GetResult()
            finally
                try
                    Directory.Delete(tempDir, recursive = true)
                with _ ->
                    ()
        }
