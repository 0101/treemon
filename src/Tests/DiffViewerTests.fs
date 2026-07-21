module Tests.DiffViewerTests

open System
open System.IO
open System.Net.Http
open System.Text.Json
open System.Threading.Tasks
open Microsoft.Playwright
open Microsoft.Playwright.NUnit
open NUnit.Framework
open Shared
open Server

let private serverPath name =
    Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "Server", name))

let private assetPath name =
    serverPath (Path.Combine("Assets", "diff2html", DiffAssets.Version, name))

let private templatePath = serverPath "DiffTemplate.html"

let private samplePatch =
    String.concat
        Environment.NewLine
        [ "diff --git a/src/a.txt b/src/a.txt"
          "index 1111111..2222222 100644"
          "--- a/src/a.txt"
          "+++ b/src/a.txt"
          "@@ -1,3 +1,4 @@"
          " one"
          "-two"
          "+TWO"
          " three"
          "+four"
          "" ]

let private fileJson identity displayPath oldDisplayPath change =
    {| identity = identity
       displayPath = displayPath
       oldDisplayPath = oldDisplayPath
       change = change |}

let private firstFile =
    fileJson "id-1" "src/a.txt" None "modified"

let private secondFile =
    fileJson "id-2" "src/new-name.txt" (Some "src/old-name.txt") "renamed"

let private readySummaryJson files =
    JsonSerializer.Serialize(
        {| status = "ready"
           baseRef = "origin/main"
           fileCount = Array.length files
           files = files |}
    )

let private fileResultJson status identity displayPath oldDisplayPath change =
    let file = fileJson identity displayPath oldDisplayPath change

    match status with
    | "text"
    | "deleted" ->
        JsonSerializer.Serialize(
            {| status = status
               file = file
               patch = samplePatch |}
        )
    | "symlink" ->
        JsonSerializer.Serialize(
            {| status = status
               file = file
               patch = "src/target.txt" |}
        )
    | _ ->
        JsonSerializer.Serialize(
            {| status = status
               file = file |}
        )

let private summaryStateJson status =
    match status with
    | "clean" ->
        """{"status":"clean","baseRef":"origin/main","fileCount":0,"files":[]}"""
    | "too-many-files" ->
        """{"status":"too-many-files","minimumFileCount":1001}"""
    | _ ->
        JsonSerializer.Serialize {| status = status |}

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type DiffProvisioningTests() =
    // NUnit lifecycle requires the per-test directory to survive between SetUp and TearDown.
    let mutable tempDir = ""

    [<SetUp>]
    member _.Setup() =
        tempDir <- Path.Combine(Path.GetTempPath(), $"treemon-diff-viewer-{Guid.NewGuid():N}")
        Directory.CreateDirectory(tempDir) |> ignore

    [<TearDown>]
    member _.Teardown() =
        if Directory.Exists(tempDir) then
            Directory.Delete(tempDir, true)

    [<Test>]
    member _.``diff viewer is provisioned and synchronized from the embedded template``() =
        let first = DiffProvisioner.provisionViewer tempDir
        let diffPath = Path.Combine(tempDir, ".agents", "canvas", "diff.html")
        let second = DiffProvisioner.provisionViewer tempDir
        File.WriteAllText(diffPath, "<!doctype html><title>stale</title>")
        let third = DiffProvisioner.provisionViewer tempDir

        Assert.Multiple(fun () ->
            Assert.That(first.IsSome, Is.True)
            Assert.That(second, Is.EqualTo(None))
            Assert.That(third.IsSome, Is.True)
            Assert.That(File.ReadAllText(diffPath), Is.EqualTo(DiffTemplate.html)))

    [<Test>]
    member _.``diff viewer is a non-authored SystemView``() =
        Assert.Multiple(fun () ->
            Assert.That(CanvasDocKinds.classify "diff.html", Is.EqualTo(SystemView))
            Assert.That(CanvasDocKinds.classify "DIFF.HTML", Is.EqualTo(SystemView))
            Assert.That(
                CanvasDocServer.buildInjection (CanvasDocKinds.classify "diff.html") "diff.html",
                Does.Not.Contain(IdiomorphScript.idiomorphJs)
            ))

    [<Test>]
    member _.``template pins only self-hosted diff2html assets``() =
        let template = File.ReadAllText(templatePath)
        let expectedRoot = $"/assets/diff2html/{DiffAssets.Version}/"

        Assert.Multiple(fun () ->
            Assert.That(template, Does.Contain(expectedRoot + "diff2html.min.css"))
            Assert.That(template, Does.Contain(expectedRoot + "diff2html.min.js"))
            Assert.That(template, Does.Contain(expectedRoot + "diff2html-ui-slim.min.js"))
            Assert.That(template, Does.Not.Contain("cdn.jsdelivr.net"))
            Assert.That(template, Does.Not.Contain("unpkg.com")))

    [<Test>]
    member _.``embedded pinned assets match the vendored files exactly``() =
        let cases =
            [ DiffAssets.cssPath, "diff2html.min.css"
              DiffAssets.rendererPath, "diff2html.min.js"
              DiffAssets.highlighterPath, "diff2html-ui-slim.min.js" ]

        cases
        |> List.iter (fun (url, filename) ->
            let asset =
                DiffAssets.tryFind url
                |> Option.defaultWith (fun () -> failwith $"Missing embedded asset {url}")

            Assert.That(asset.Content, Is.EqualTo(File.ReadAllText(assetPath filename)), filename))

[<TestFixture>]
[<Category("E2E")>]
[<Category("Canvas")>]
type DiffViewerE2ETests() =
    inherit PageTest()

    let pageUrl = $"{ServerFixture.canvasUrl}/e2e-diff-worktree/diff.html"
    let template =
        File.ReadAllText(templatePath)
        |> CanvasExport.injectAtHead (CanvasDocServer.buildInjection SystemView "diff.html")
    let css = File.ReadAllText(assetPath "diff2html.min.css")
    let renderer = File.ReadAllText(assetPath "diff2html.min.js")
    let highlighter = File.ReadAllText(assetPath "diff2html-ui-slim.min.js")

    override this.ContextOptions() =
        let options = base.ContextOptions()
        options.IgnoreHTTPSErrors <- true
        options

    member private this.RouteBody(glob: string, contentType: string, body: string) =
        this.Page.RouteAsync(
            glob,
            fun (route: IRoute) ->
                route.FulfillAsync(
                    RouteFulfillOptions(
                        ContentType = contentType,
                        Body = body
                    )
                )
        )

    member private this.RouteSummary(body) =
        this.RouteBody("**/diff-summary", "application/json", body)

    member private this.RouteFiles() =
        this.Page.RouteAsync(
            "**/diff-file?*",
            fun route ->
                let uri = Uri(route.Request.Url)
                let identity = Uri.UnescapeDataString(uri.Query.Substring("?identity=".Length))
                let file =
                    if identity = "id-2" then secondFile else firstFile

                route.FulfillAsync(
                    RouteFulfillOptions(
                        ContentType = "application/json",
                        Body =
                            fileResultJson
                                "text"
                                file.identity
                                file.displayPath
                                file.oldDisplayPath
                                file.change
                    )
                )
        )

    member private this.RouteFileStatus(status) =
        this.RouteBody(
            "**/diff-file?*",
            "application/json",
            fileResultJson
                status
                firstFile.identity
                firstFile.displayPath
                firstFile.oldDisplayPath
                firstFile.change
        )

    member private this.RouteHighlighter() =
        this.RouteBody(
            $"**/{DiffAssets.Version}/diff2html-ui-slim.min.js",
            "text/javascript",
            highlighter
        )

    member private this.Goto() =
        task {
            let! _ =
                this.Page.GotoAsync(
                    pageUrl,
                    PageGotoOptions(WaitUntil = WaitUntilState.Load)
                )

            ()
        }

    [<SetUp>]
    member this.RouteTemplateAndCoreAssets() =
        task {
            do! this.RouteBody("**/diff.html", "text/html; charset=utf-8", template)
            do!
                this.RouteBody(
                    $"**/{DiffAssets.Version}/diff2html.min.css",
                    "text/css",
                    css
                )
            do!
                this.RouteBody(
                    $"**/{DiffAssets.Version}/diff2html.min.js",
                    "text/javascript",
                    renderer
                )
        }

    [<Test>]
    member this.``canvas server serves the exact immutable pinned renderer asset``() =
        task {
            use client = new HttpClient()
            let! response =
                client.GetAsync($"{ServerFixture.canvasUrl}{DiffAssets.rendererPath}")
            let! body = response.Content.ReadAsStringAsync()

            Assert.Multiple(fun () ->
                Assert.That(int response.StatusCode, Is.EqualTo(200))
                Assert.That(body, Is.EqualTo(renderer))
                Assert.That(
                    response.Headers.CacheControl.ToString(),
                    Is.EqualTo("public, max-age=31536000, immutable")
                ))
        }

    [<Test>]
    member this.``summary loads before one file and highlighting starts only after the plain standalone patch``() =
        task {
            let plainBeforeHighlighter =
                TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)

            do!
                this.Page.AddInitScriptAsync(
                    """(() => {
                        window.__diffFetches = [];
                        const originalFetch = window.fetch;
                        window.fetch = function(input) {
                            const url = typeof input === 'string' ? input : input.url;
                            if (url.includes('diff-summary') || url.includes('diff-file')) {
                                window.__diffFetches.push(new URL(url, location.href).href);
                            }
                            return originalFetch.apply(this, arguments);
                        };
                    })()"""
                )
            do! this.RouteSummary(readySummaryJson [| firstFile |])
            do! this.RouteFiles()
            do!
                this.Page.RouteAsync(
                    $"**/{DiffAssets.Version}/diff2html-ui-slim.min.js",
                    fun route ->
                        task {
                            let! plain =
                                this.Page.EvaluateAsync<bool>(
                                    "() => Boolean(document.querySelector('#patch[data-render-status=\"plain\"] .d2h-wrapper'))"
                                )
                            plainBeforeHighlighter.TrySetResult(plain) |> ignore
                            do!
                                route.FulfillAsync(
                                    RouteFulfillOptions(
                                        ContentType = "text/javascript",
                                        Body = highlighter
                                    )
                                )
                        }
                        :> Task
                )

            do! this.Goto()
            do!
                this.Page.Locator("#patch[data-highlight-status='ready'] .d2h-wrapper").WaitForAsync(
                    LocatorWaitForOptions(Timeout = 15000.0f)
                )

            let! wasPlain =
                plainBeforeHighlighter.Task.WaitAsync(TimeSpan.FromSeconds(10.0))
            let! requests =
                this.Page.EvaluateAsync<string array>("() => window.__diffFetches")
            let requestPaths = requests |> Array.map (fun url -> Uri(url).PathAndQuery)
            let! isStandalone =
                this.Page.EvaluateAsync<bool>("() => window.top === window")
            let! selected =
                this.Page.Locator(".file-entry.active").GetAttributeAsync("data-identity")

            Assert.Multiple(fun () ->
                Assert.That(wasPlain, Is.True)
                Assert.That(
                    requestPaths,
                    Is.EqualTo(
                        [| "/e2e-diff-worktree/diff-summary"
                           "/e2e-diff-worktree/diff-file?identity=id-1" |]
                    )
                )
                Assert.That(isStandalone, Is.True)
                Assert.That(selected, Is.EqualTo("id-1")))
        }

    [<Test>]
    member this.``standalone diff selection action stays visible and reports unavailable transport``() =
        task {
            do! this.RouteHighlighter()
            do! this.RouteSummary(readySummaryJson [| firstFile |])
            do! this.RouteFiles()
            do! this.Goto()
            let codeLine = this.Page.Locator("#patch .d2h-code-line-ctn").First
            do! CanvasTestHelpers.assertStandaloneSelectionUnavailable this.Page codeLine
        }

    [<Test>]
    member this.``a valid prior selection is restored and a missing one falls back to the first file``() =
        task {
            do! this.RouteHighlighter()
            do! this.RouteSummary(readySummaryJson [| firstFile; secondFile |])
            do! this.RouteFiles()
            do! this.Goto()
            do! this.Page.Locator("#patch .d2h-wrapper").WaitForAsync()

            do! this.Page.Locator(".file-entry[data-identity='id-2']").ClickAsync()
            do!
                this.Page.Locator(".file-entry[data-identity='id-2'].active").WaitForAsync()
            let! _ = this.Page.ReloadAsync()
            do!
                this.Page.Locator(".file-entry[data-identity='id-2'].active").WaitForAsync()

            do! this.Page.UnrouteAsync("**/diff-summary")
            do! this.RouteSummary(readySummaryJson [| firstFile |])
            let! _ = this.Page.ReloadAsync()
            do!
                this.Page.Locator(".file-entry[data-identity='id-1'].active").WaitForAsync()

            let! selected =
                this.Page.Locator(".file-entry.active").GetAttributeAsync("data-identity")
            Assert.That(selected, Is.EqualTo("id-1"))
        }

    [<TestCase("clean", "No changes")>]
    [<TestCase("base-error", "Comparison base unavailable")>]
    [<TestCase("git-error", "Diff unavailable")>]
    [<TestCase("too-many-files", "Too many changed files")>]
    member this.``summary state is explicit``(status: string, expectedTitle: string) =
        task {
            do! this.RouteSummary(summaryStateJson status)
            do! this.Goto()
            let card = this.Page.Locator($"[data-state='{status}']")
            do! card.WaitForAsync()
            let! title = card.Locator(".state-title").TextContentAsync()
            Assert.That(title, Is.EqualTo(expectedTitle))
        }

    [<TestCase("deleted", "")>]
    [<TestCase("binary", "Binary file")>]
    [<TestCase("oversized", "File is too large")>]
    [<TestCase("truncated", "Patch is too long")>]
    [<TestCase("symlink", "Symbolic link")>]
    [<TestCase("unavailable", "File unavailable")>]
    [<TestCase("git-error", "Could not load file diff")>]
    member this.``every selected-file state renders explicitly``(status: string, expectedTitle: string) =
        task {
            do! this.RouteHighlighter()
            do! this.RouteSummary(readySummaryJson [| firstFile |])
            do! this.RouteFileStatus(status)
            do! this.Goto()

            match status with
            | "deleted" ->
                do! this.Page.Locator("#patch .d2h-wrapper").WaitForAsync()
                let! fileName =
                    this.Page.Locator("#patch .d2h-file-name").TextContentAsync()
                Assert.That(fileName, Is.EqualTo("src/a.txt"))
            | "symlink" ->
                let state = this.Page.Locator("pre[data-state='symlink']")
                do! state.WaitForAsync()
                let! title = state.GetAttributeAsync("aria-label")
                Assert.That(title, Is.EqualTo(expectedTitle))
            | _ ->
                let state = this.Page.Locator($"[data-state='{status}']")
                do! state.WaitForAsync()
                let! title = state.Locator(".state-title").TextContentAsync()
                Assert.That(title, Is.EqualTo(expectedTitle))
        }

    [<Test>]
    member this.``unsupported and over-limit states never reach diff2html``() =
        task {
            do! this.RouteHighlighter()
            do! this.RouteSummary(readySummaryJson [| firstFile |])
            do! this.RouteFiles()
            do! this.Goto()
            do! this.Page.Locator("#patch .d2h-wrapper").WaitForAsync()

            let! calls =
                this.Page.EvaluateAsync<int>(
                    """() => {
                        let calls = 0;
                        const original = Diff2Html.html;
                        Diff2Html.html = function() { calls += 1; return original.apply(this, arguments); };
                        const file = { identity: 'id-1', displayPath: 'src/a.txt', oldDisplayPath: null, change: 'modified' };
                        [
                            { status: 'binary', file },
                            { status: 'oversized', file },
                            { status: 'truncated', file },
                            { status: 'symlink', file, patch: 'src/target.txt' },
                            { status: 'unavailable', file },
                            { status: 'git-error', file }
                        ].forEach(renderFileResult);
                        Diff2Html.html = original;
                        return calls;
                    }"""
                )

            Assert.That(calls, Is.EqualTo(0))
        }

    [<Test>]
    member this.``unified is default and split preference persists``() =
        task {
            do! this.RouteHighlighter()
            do! this.RouteSummary(readySummaryJson [| firstFile |])
            do! this.RouteFiles()
            do! this.Goto()
            do! this.Page.Locator("#patch .d2h-file-diff").WaitForAsync()

            let! unifiedPressed =
                this.Page.Locator("#unified-view").GetAttributeAsync("aria-pressed")
            Assert.That(unifiedPressed, Is.EqualTo("true"))

            do! this.Page.Locator("#split-view").ClickAsync()
            do! this.Page.Locator("#patch .d2h-files-diff").WaitForAsync()
            let! _ = this.Page.ReloadAsync()
            do! this.Page.Locator("#patch .d2h-files-diff").WaitForAsync()

            let! splitPressed =
                this.Page.Locator("#split-view").GetAttributeAsync("aria-pressed")
            let! stored =
                this.Page.EvaluateAsync<string>("() => localStorage.getItem('treemon.diff.view')")
            Assert.Multiple(fun () ->
                Assert.That(splitPressed, Is.EqualTo("true"))
                Assert.That(stored, Is.EqualTo("split")))
        }

    [<Test>]
    member this.``patch stays usable when lazy syntax highlighting fails``() =
        task {
            do! this.RouteSummary(readySummaryJson [| firstFile |])
            do! this.RouteFiles()
            do!
                this.Page.RouteAsync(
                    $"**/{DiffAssets.Version}/diff2html-ui-slim.min.js",
                    fun route -> route.AbortAsync()
                )
            do! this.Goto()
            do!
                this.Page.Locator("#patch[data-highlight-status='failed'] .d2h-wrapper").WaitForAsync(
                    LocatorWaitForOptions(Timeout = 15000.0f)
                )
            do! this.Page.Locator("#split-view").ClickAsync()
            do! this.Page.Locator("#patch .d2h-files-diff").WaitForAsync()

            let! fileCount = this.Page.Locator(".file-entry").CountAsync()
            Assert.That(fileCount, Is.EqualTo(1))
        }

    [<Test>]
    member this.``diff selection metadata extracts exact ranges in unified and split views``() =
        task {
            do! this.RouteHighlighter()
            do! this.RouteSummary(readySummaryJson [| firstFile |])
            do! this.RouteFiles()
            do! this.Goto()
            do! this.Page.Locator("#patch .d2h-wrapper").WaitForAsync()

            let sourceContext selector =
                this.Page.Locator(selector).EvaluateAsync<string>(
                    """element => {
                        const range = document.createRange();
                        range.selectNodeContents(element);
                        return JSON.stringify(window.canvasSelectionMetadata({ range }));
                    }"""
                )

            let! context =
                sourceContext
                    "tr[data-old-line='1'][data-new-line='1'] .d2h-code-line-ctn"
            let! deletion =
                sourceContext
                    "tr[data-old-line='2']:not([data-new-line]) .d2h-code-line-ctn"
            let! addition =
                sourceContext
                    "tr[data-new-line='2']:not([data-old-line]) .d2h-code-line-ctn"

            let prefix =
                """{"kind":"diff","fileIdentity":"id-1","displayPath":"src/a.txt","oldDisplayPath":null,"hunkHeader":"@@ -1,3 +1,4 @@","""
            let expectedContext =
                prefix + """"oldLineRange":{"start":1,"end":1},"newLineRange":{"start":1,"end":1}}"""
            let expectedDeletion =
                prefix + """"oldLineRange":{"start":2,"end":2},"newLineRange":null}"""
            let expectedAddition =
                prefix + """"oldLineRange":null,"newLineRange":{"start":2,"end":2}}"""

            Assert.Multiple(fun () ->
                Assert.That(context, Is.EqualTo(expectedContext))
                Assert.That(deletion, Is.EqualTo(expectedDeletion))
                Assert.That(addition, Is.EqualTo(expectedAddition)))

            do! this.Page.Locator("#split-view").ClickAsync()
            do! this.Page.Locator("#patch .d2h-files-diff").WaitForAsync()

            let! splitContext =
                sourceContext
                    ".d2h-file-side-diff:first-child tr[data-old-line='1'][data-new-line='1'] .d2h-code-line-ctn"
            let! splitDeletion =
                sourceContext
                    ".d2h-file-side-diff:first-child tr[data-old-line='2']:not([data-new-line]) .d2h-code-line-ctn"
            let! splitAddition =
                sourceContext
                    ".d2h-file-side-diff:last-child tr[data-new-line='2']:not([data-old-line]) .d2h-code-line-ctn"

            Assert.Multiple(fun () ->
                Assert.That(splitContext, Is.EqualTo(expectedContext))
                Assert.That(splitDeletion, Is.EqualTo(expectedDeletion))
                Assert.That(splitAddition, Is.EqualTo(expectedAddition)))
        }
