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

let private syntaxPatch =
    String.concat
        Environment.NewLine
        [ "diff --git a/src/example.js b/src/example.js"
          "index 1111111..2222222 100644"
          "--- a/src/example.js"
          "+++ b/src/example.js"
          "@@ -1,2 +1,3 @@"
          "-const answer = 41;"
          "+const answer = 42;"
          "+console.log(\"answer\", answer);"
          " export { answer };"
          "" ]

let private wrappedPatch =
    let longLine prefix word =
        prefix + String.replicate 24 $"{word} "

    String.concat
        Environment.NewLine
        [ "diff --git a/src/a.txt b/src/a.txt"
          "index 1111111..2222222 100644"
          "--- a/src/a.txt"
          "+++ b/src/a.txt"
          "@@ -1,4 +1,4 @@"
          longLine " first context " "context"
          longLine "-removed line " "removed"
          longLine "+added line " "added"
          " short context"
          " trailing context"
          "@@ -10,2 +10,2 @@"
          longLine " second hunk context " "boundary"
          longLine "-second removed line " "old"
          longLine "+second added line " "new"
          "" ]

let private fileJson identity displayPath oldDisplayPath change =
    {| identity = identity
       displayPath = displayPath
       oldDisplayPath = oldDisplayPath
       change = change |}

let private firstFile =
    fileJson "id-1" "src/a.txt" None "modified"

let private syntaxFile =
    fileJson "id-js" "src/example.js" (None: string option) "modified"

let private secondFile =
    fileJson "id-2" "src/new-name.txt" (Some "src/old-name.txt") "renamed"

let private refreshedFirstFile =
    fileJson "id-1-refreshed" "src/a.txt" None "modified"

let private refreshedSecondFile =
    fileJson
        "id-2-refreshed"
        "src/new-name.txt"
        (Some "src/old-name.txt")
        "renamed"

let private readySummaryJson files =
    JsonSerializer.Serialize(
        {| status = "ready"
           baseRef = "origin/main"
           fileCount = Array.length files
           files = files |}
    )

let private fileResultJsonWithPatch patch status identity displayPath oldDisplayPath change =
    let file = fileJson identity displayPath oldDisplayPath change

    match status with
    | "text"
    | "deleted" ->
        JsonSerializer.Serialize(
            {| status = status
               file = file
               patch = patch |}
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

let private fileResultJson =
    fileResultJsonWithPatch samplePatch

let private summaryStateJson status =
    match status with
    | "clean" ->
        """{"status":"clean","baseRef":"origin/main","fileCount":0,"files":[]}"""
    | "filtered-empty" ->
        """{"status":"filtered-empty","fileCount":0,"files":[]}"""
    | "too-many-files" ->
        """{"status":"too-many-files","minimumFileCount":1001}"""
    | _ ->
        JsonSerializer.Serialize {| status = status |}

let private layerFilterQuery committed local untracked =
    $"?committed={committed.ToString().ToLowerInvariant()}&local={local.ToString().ToLowerInvariant()}&untracked={untracked.ToString().ToLowerInvariant()}"

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
        this.RouteBody("**/diff-summary?*", "application/json", body)

    member private this.RouteFiles() =
        this.Page.RouteAsync(
            "**/diff-file?*",
            fun route ->
                let uri = Uri(route.Request.Url)
                let identity = Uri.UnescapeDataString(uri.Query.Substring("?identity=".Length))
                let file =
                    if identity.StartsWith("id-2", StringComparison.Ordinal) then
                        fileJson
                            identity
                            secondFile.displayPath
                            secondFile.oldDisplayPath
                            secondFile.change
                    else
                        fileJson
                            identity
                            firstFile.displayPath
                            firstFile.oldDisplayPath
                            firstFile.change

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

    member private this.RoutePatch(patch) =
        this.RouteBody(
            "**/diff-file?*",
            "application/json",
            fileResultJsonWithPatch
                patch
                "text"
                firstFile.identity
                firstFile.displayPath
                firstFile.oldDisplayPath
                firstFile.change
        )

    member private this.RouteSyntaxPatch() =
        this.RouteBody(
            "**/diff-file?*",
            "application/json",
            fileResultJsonWithPatch
                syntaxPatch
                "text"
                syntaxFile.identity
                syntaxFile.displayPath
                syntaxFile.oldDisplayPath
                syntaxFile.change
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

    member private this.SetupLayerFilterPage() =
        task {
            do!
                this.Page.AddInitScriptAsync(
                    """(() => {
                        window.__summaryQueries = [];
                        const originalFetch = window.fetch;
                        window.fetch = function(input) {
                            const url = typeof input === 'string' ? input : input.url;
                            if (url.includes('diff-summary')) {
                                window.__summaryQueries.push(new URL(url, location.href).search);
                            }
                            return originalFetch.apply(this, arguments);
                        };
                    })()"""
                )

            do!
                this.Page.RouteAsync(
                    "**/diff-summary?*",
                    fun route ->
                        let query = Uri(route.Request.Url).Query
                        let body =
                            if
                                query
                                = layerFilterQuery false false false
                            then
                                summaryStateJson "filtered-empty"
                            else
                                readySummaryJson [| firstFile |]

                        route.FulfillAsync(
                            RouteFulfillOptions(
                                ContentType = "application/json",
                                Body = body
                            )
                        )
                )
            do! this.RouteFiles()
            do! this.Goto()
        }

    member private this.ApplyLayerFilters(committed, local, untracked) =
        task {
            let expected = layerFilterQuery committed local untracked

            let! _ =
                this.Page.EvaluateAsync<obj>(
                    """values => {
                        document.getElementById('filter-committed').checked = values[0];
                        document.getElementById('filter-local').checked = values[1];
                        document.getElementById('filter-untracked').checked = values[2];
                        document.getElementById('filter-untracked')
                            .dispatchEvent(new Event('change', { bubbles: true }));
                    }""",
                    [| committed; local; untracked |]
                )

            let! _ =
                this.Page.WaitForFunctionAsync(
                    "expected => window.__summaryQueries.at(-1) === expected",
                    expected
                )

            if not committed && not local && not untracked then
                do!
                    this.Page.Locator("[data-state='filtered-empty']").WaitForAsync()
            else
                do! this.Page.Locator(".file-entry.active").WaitForAsync()
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
    member this.``supported code is plain before loading and visibly tokenized before highlighting is ready``() =
        task {
            let plainBeforeHighlighter =
                TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)

            do!
                this.Page.AddInitScriptAsync(
                    """(() => {
                        window.__diffFetches = [];
                        window.__diffViewerHeaders = [];
                        const originalFetch = window.fetch;
                        window.fetch = function(input) {
                            const url = typeof input === 'string' ? input : input.url;
                            if (url.includes('diff-summary') || url.includes('diff-file')) {
                                window.__diffFetches.push(new URL(url, location.href).href);
                                const options = arguments[1] || {};
                                window.__diffViewerHeaders.push(
                                    new Headers(options.headers || {}).get('X-Treemon-Diff-Viewer')
                                );
                            }
                            return originalFetch.apply(this, arguments);
                        };
                    })()"""
                )
            do! this.RouteSummary(readySummaryJson [| syntaxFile |])
            do! this.RouteSyntaxPatch()
            do!
                this.Page.RouteAsync(
                    $"**/{DiffAssets.Version}/diff2html-ui-slim.min.js",
                    fun route ->
                        task {
                            let! plain =
                                this.Page.EvaluateAsync<bool>(
                                    """() => Boolean(
                                        document.querySelector('#patch[data-render-status="plain"] .d2h-wrapper') &&
                                        !document.querySelector('#patch .d2h-code-line-ctn [class*="hljs-"]')
                                    )"""
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
                this.Page.Locator(
                    "#patch[data-highlight-status='ready'] .d2h-file-wrapper[data-lang='js'] .d2h-code-line-ctn.hljs.javascript .hljs-keyword"
                ).First.WaitForAsync(
                    LocatorWaitForOptions(Timeout = 15000.0f)
                )

            let! wasPlain =
                plainBeforeHighlighter.Task.WaitAsync(TimeSpan.FromSeconds(10.0))
            let! requests =
                this.Page.EvaluateAsync<string array>("() => window.__diffFetches")
            let! viewerHeaders =
                this.Page.EvaluateAsync<string array>("() => window.__diffViewerHeaders")
            let requestPaths = requests |> Array.map (fun url -> Uri(url).PathAndQuery)
            let! isStandalone =
                this.Page.EvaluateAsync<bool>("() => window.top === window")
            let! selected =
                this.Page.Locator(".file-entry.active").GetAttributeAsync("data-identity")
            let token = this.Page.Locator("#patch .d2h-code-line-ctn .hljs-keyword").First
            let! tokenColors =
                token.EvaluateAsync<string array>(
                    """element => [
                        getComputedStyle(element).color,
                        getComputedStyle(element.closest('.d2h-code-line-ctn')).color
                    ]"""
                )
            let! tokenCount =
                this.Page.Locator("#patch .d2h-code-line-ctn span[class*='hljs-']").CountAsync()

            Assert.Multiple(fun () ->
                Assert.That(wasPlain, Is.True)
                Assert.That(
                    requestPaths,
                    Is.EqualTo(
                        [| "/e2e-diff-worktree/diff-summary?committed=true&local=true&untracked=false"
                           "/e2e-diff-worktree/diff-file?identity=id-js" |]
                    )
                )
                Assert.That(viewerHeaders.Length, Is.EqualTo(2))
                Assert.That(viewerHeaders[1], Is.EqualTo(viewerHeaders[0]))
                Assert.That(Guid.TryParseExact(viewerHeaders[0], "D") |> fst, Is.True)
                Assert.That(isStandalone, Is.True)
                Assert.That(selected, Is.EqualTo("id-js"))
                Assert.That(tokenCount, Is.GreaterThan(0))
                Assert.That(tokenColors[0], Is.Not.EqualTo(tokenColors[1])))
        }

    [<Test>]
    member this.``highlighting cannot report ready when the bundle produces no token markup``() =
        task {
            do! this.RouteSummary(readySummaryJson [| syntaxFile |])
            do! this.RouteSyntaxPatch()
            do!
                this.Page.RouteAsync(
                    $"**/{DiffAssets.Version}/diff2html-ui-slim.min.js",
                    fun route ->
                        route.FulfillAsync(
                            RouteFulfillOptions(
                                ContentType = "text/javascript",
                                Body =
                                    """window.Diff2HtmlUI = function(target) {
                                        this.highlightCode = function() {
                                            target.querySelectorAll('.d2h-code-line-ctn')
                                                .forEach(function(line) { line.classList.add('hljs'); });
                                        };
                                    };"""
                            )
                        )
                )
            do! this.Goto()
            do!
                this.Page.Locator("#patch[data-highlight-status='plain'] .d2h-wrapper").WaitForAsync(
                    LocatorWaitForOptions(Timeout = 15000.0f)
                )

            let! readyCount =
                this.Page.Locator("#patch[data-highlight-status='ready']").CountAsync()
            let! tokenCount =
                this.Page.Locator("#patch .d2h-code-line-ctn span[class*='hljs-']").CountAsync()

            Assert.Multiple(fun () ->
                Assert.That(readyCount, Is.Zero)
                Assert.That(tokenCount, Is.Zero))
        }

    [<Test>]
    member this.``standalone diff selection action stays visible and reports unavailable transport``() =
        task {
            do! this.RouteHighlighter()
            do! this.RouteSummary(readySummaryJson [| firstFile |])
            do! this.RouteFiles()
            do! this.Goto()
            do!
                this.Page.Locator(
                    "#patch[data-highlight-status='ready'], #patch[data-highlight-status='plain'], #patch[data-highlight-status='failed']"
                ).WaitForAsync(LocatorWaitForOptions(Timeout = 15000.0f))
            let codeLine = this.Page.Locator("#patch .d2h-code-line-ctn").First
            do! CanvasTestHelpers.assertStandaloneSelectionUnavailable this.Page codeLine
        }

    [<Test>]
    member this.``initial summary expands exactly one file and mounts exactly one patch``() =
        task {
            do! this.RouteHighlighter()
            do! this.RouteSummary(readySummaryJson [| firstFile; secondFile |])
            do! this.RouteFiles()
            do! this.Goto()
            do! this.Page.Locator("#patch .d2h-wrapper").WaitForAsync()

            let! state =
                this.Page.EvaluateAsync<string array>(
                    """() => [
                        document.querySelector('.file-entry.active').dataset.identity,
                        document.querySelector('.file-entry.active').getAttribute('aria-expanded'),
                        String(document.querySelectorAll('.file-entry[aria-expanded="true"]').length),
                        String(document.querySelectorAll('.file-panel').length),
                        String(document.querySelectorAll('#patch').length)
                    ]"""
                )

            Assert.That(state, Is.EqualTo([| "id-1"; "true"; "1"; "1"; "1" |]))
        }

    [<Test>]
    member this.``accordion content scrolls to every file header and expanded patch``() =
        task {
            do! this.Page.SetViewportSizeAsync(900, 520)

            let files =
                Array.init 24 (fun index ->
                    let number = index + 1
                    fileJson
                        $"id-scroll-{number}"
                        $"src/file-{number:D2}.txt"
                        (None: string option)
                        "modified")

            do! this.RouteHighlighter()
            do! this.RouteSummary(readySummaryJson files)
            do! this.RouteFiles()
            do! this.Goto()
            do! this.Page.Locator("#patch .d2h-wrapper").WaitForAsync()

            let lastHeader = this.Page.Locator(".file-entry[data-identity='id-scroll-24']")

            let! headerState =
                this.Page.EvaluateAsync<bool array>(
                    """() => {
                        const content = document.getElementById('content');
                        const workspace = document.querySelector('.workspace');
                        const header = document.querySelector(
                            ".file-entry[data-identity='id-scroll-24']"
                        );
                        content.scrollTop = content.scrollHeight;
                        const contentRect = content.getBoundingClientRect();
                        const headerRect = header.getBoundingClientRect();
                        return [
                            getComputedStyle(content).overflowY === 'auto',
                            content.clientHeight <= workspace.clientHeight,
                            content.scrollHeight > content.clientHeight,
                            content.scrollTop > 0,
                            headerRect.top >= contentRect.top - 1 &&
                                headerRect.bottom <= contentRect.bottom + 1,
                            document.scrollingElement.scrollTop === 0
                        ];
                    }"""
                )

            do! lastHeader.ClickAsync()
            do!
                lastHeader.Locator("xpath=../..").Locator("#patch .d2h-wrapper").WaitForAsync()

            let! patchState =
                this.Page.EvaluateAsync<bool array>(
                    """() => {
                        const content = document.getElementById('content');
                        const patch = document.querySelector(
                            ".file-entry[data-identity='id-scroll-24']"
                        ).closest('.file-item').querySelector('#patch');
                        content.scrollTop = content.scrollHeight;
                        const contentRect = content.getBoundingClientRect();
                        const patchRect = patch.getBoundingClientRect();
                        return [
                            content.scrollTop > 0,
                            patchRect.bottom <= contentRect.bottom + 1,
                            patchRect.bottom > contentRect.top
                        ];
                    }"""
                )

            Assert.Multiple(fun () ->
                Assert.That(headerState, Is.All.True)
                Assert.That(patchState, Is.All.True))
        }

    [<Test>]
    member this.``layer filters use defaults and cover every query-string combination``() =
        task {
            do! this.SetupLayerFilterPage()

            let! defaults =
                this.Page.EvaluateAsync<bool array>(
                    """() => [
                        document.getElementById('filter-committed').checked,
                        document.getElementById('filter-local').checked,
                        document.getElementById('filter-untracked').checked
                    ]"""
                )

            Assert.That(defaults, Is.EqualTo([| true; true; false |]))

            for (committed, local, untracked) in
                [ false, false, false
                  false, false, true
                  false, true, false
                  false, true, true
                  true, false, false
                  true, false, true
                  true, true, false
                  true, true, true ] do
                do! this.ApplyLayerFilters(committed, local, untracked)
        }

    [<Test>]
    member this.``layer filters persist per worktree across reload``() =
        task {
            do! this.SetupLayerFilterPage()
            do! this.ApplyLayerFilters(false, true, true)

            let! _ = this.Page.ReloadAsync()

            let expectedPersistedQuery = layerFilterQuery false true true

            let! _ =
                this.Page.WaitForFunctionAsync(
                    "expected => window.__summaryQueries.at(-1) === expected",
                    expectedPersistedQuery
                )

            let! persisted =
                this.Page.EvaluateAsync<string array>(
                    """() => [
                        String(document.getElementById('filter-committed').checked),
                        String(document.getElementById('filter-local').checked),
                        String(document.getElementById('filter-untracked').checked),
                        localStorage.getItem('treemon.diff.layers:/e2e-diff-worktree')
                    ]"""
                )

            Assert.That(
                persisted,
                Is.EqualTo(
                    [| "false"
                       "true"
                       "true"
                       """{"committed":false,"local":true,"untracked":true}""" |]
                )
            )
        }

    [<Test>]
    member this.``pointer and keyboard switching replace the single expanded patch``() =
        task {
            do! this.RouteHighlighter()
            do! this.RouteSummary(readySummaryJson [| firstFile; secondFile |])
            do! this.RouteFiles()
            do! this.Goto()
            do! this.Page.Locator("#patch .d2h-wrapper").WaitForAsync()

            let second = this.Page.Locator(".file-entry[data-identity='id-2']")
            do! second.ClickAsync()
            do! second.Locator("xpath=..").Locator("xpath=..").Locator("#patch .d2h-wrapper").WaitForAsync()

            let! pointerState =
                this.Page.EvaluateAsync<string array>(
                    """() => [
                        document.querySelector('.file-entry.active').dataset.identity,
                        String(document.querySelectorAll('.file-entry[aria-expanded="true"]').length),
                        String(document.querySelectorAll('.file-panel').length),
                        String(document.querySelectorAll('#patch').length)
                    ]"""
                )

            let first = this.Page.Locator(".file-entry[data-identity='id-1']")
            do! first.FocusAsync()
            do! first.PressAsync("Enter")
            do! first.Locator("xpath=..").Locator("xpath=..").Locator("#patch .d2h-wrapper").WaitForAsync()

            let! keyboardState =
                this.Page.EvaluateAsync<string array>(
                    """() => [
                        document.querySelector('.file-entry.active').dataset.identity,
                        String(document.querySelectorAll('.file-entry[aria-expanded="true"]').length),
                        String(document.querySelectorAll('.file-panel').length),
                        String(document.querySelectorAll('#patch').length)
                    ]"""
                )

            Assert.Multiple(fun () ->
                Assert.That(pointerState, Is.EqualTo([| "id-2"; "1"; "1"; "1" |]))
                Assert.That(keyboardState, Is.EqualTo([| "id-1"; "1"; "1"; "1" |])))
        }

    [<Test>]
    member this.``refresh restores by paths and change kind then falls back to the first file``() =
        task {
            let summaries =
                [| readySummaryJson [| firstFile; secondFile |]
                   readySummaryJson [| refreshedFirstFile; refreshedSecondFile |]
                   readySummaryJson [| refreshedFirstFile |] |]
            // The route callback owns the sequence of refreshed identity snapshots.
            let mutable summaryIndex = 0

            do! this.RouteHighlighter()
            do!
                this.Page.RouteAsync(
                    "**/diff-summary?*",
                    fun route ->
                        let body = summaries[Math.Min(summaryIndex, summaries.Length - 1)]
                        summaryIndex <- summaryIndex + 1
                        route.FulfillAsync(
                            RouteFulfillOptions(
                                ContentType = "application/json",
                                Body = body
                            )
                        )
                )
            do! this.RouteFiles()
            do! this.Goto()
            do! this.Page.Locator("#patch .d2h-wrapper").WaitForAsync()

            do! this.Page.Locator(".file-entry[data-identity='id-2']").ClickAsync()
            do! this.Page.Locator(".file-entry[data-identity='id-2'].active").WaitForAsync()
            do! this.Page.Locator("#refresh").ClickAsync()
            do!
                this.Page.Locator(".file-entry[data-identity='id-2-refreshed'].active").WaitForAsync()

            let! restored =
                this.Page.Locator(".file-entry.active").GetAttributeAsync("data-identity")

            do! this.Page.Locator("#refresh").ClickAsync()
            do!
                this.Page.Locator(".file-entry[data-identity='id-1-refreshed'].active").WaitForAsync()

            let! fallback =
                this.Page.Locator(".file-entry.active").GetAttributeAsync("data-identity")
            let! patchCount = this.Page.Locator("#patch").CountAsync()

            Assert.Multiple(fun () ->
                Assert.That(restored, Is.EqualTo("id-2-refreshed"))
                Assert.That(fallback, Is.EqualTo("id-1-refreshed"))
                Assert.That(patchCount, Is.EqualTo(1)))
        }

    [<Test>]
    member this.``switching files aborts and replaces the in-flight patch request``() =
        task {
            let firstStarted =
                TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
            let releaseFirst =
                TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
            let firstHandlerFinished =
                TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)

            do!
                this.Page.AddInitScriptAsync(
                    """(() => {
                        window.__firstFileOutcome = null;
                        const originalFetch = window.fetch;
                        window.fetch = function(input) {
                            const url = typeof input === 'string' ? input : input.url;
                            const request = originalFetch.apply(this, arguments);
                            if (url.includes('diff-file?identity=id-1')) {
                                request.then(
                                    () => { window.__firstFileOutcome = 'completed'; },
                                    error => { window.__firstFileOutcome = error.name; }
                                );
                            }
                            return request;
                        };
                    })()"""
                )
            do! this.RouteHighlighter()
            do! this.RouteSummary(readySummaryJson [| firstFile; secondFile |])
            do!
                this.Page.RouteAsync(
                    "**/diff-file?*",
                    fun route ->
                        task {
                            let uri = Uri(route.Request.Url)
                            let identity =
                                Uri.UnescapeDataString(uri.Query.Substring("?identity=".Length))
                            let file = if identity = "id-1" then firstFile else secondFile

                            if identity = "id-1" then
                                firstStarted.TrySetResult(true) |> ignore
                                let! _ = releaseFirst.Task
                                ()

                            try
                                do!
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
                            with _ ->
                                ()

                            if identity = "id-1" then
                                firstHandlerFinished.TrySetResult(true) |> ignore
                        }
                        :> Task
                )

            do! this.Goto()
            let! _ = firstStarted.Task.WaitAsync(TimeSpan.FromSeconds(10.0))
            do! this.Page.Locator(".file-entry[data-identity='id-2']").ClickAsync()
            let! _ =
                this.Page.WaitForFunctionAsync(
                    "() => window.__firstFileOutcome === 'AbortError'"
                )
            do!
                this.Page.Locator(
                    ".file-entry[data-identity='id-2'].active"
                ).Locator("xpath=..").Locator("xpath=..").Locator("#patch .d2h-wrapper").WaitForAsync()

            releaseFirst.TrySetResult(true) |> ignore
            let! _ = firstHandlerFinished.Task.WaitAsync(TimeSpan.FromSeconds(10.0))

            let! replacementState =
                this.Page.EvaluateAsync<string array>(
                    """() => [
                        window.__firstFileOutcome,
                        document.querySelector('.file-entry.active').dataset.identity,
                        state.currentResult.file.identity,
                        String(document.querySelectorAll('.file-panel').length),
                        String(document.querySelectorAll('#patch').length)
                    ]"""
                )

            Assert.That(
                replacementState,
                Is.EqualTo([| "AbortError"; "id-2"; "id-2"; "1"; "1" |])
            )
        }

    [<TestCase("clean", "No changes")>]
    [<TestCase("filtered-empty", "No change layers selected")>]
    [<TestCase("base-error", "Comparison base unavailable")>]
    [<TestCase("timeout", "Diff timed out")>]
    [<TestCase("git-error", "Diff unavailable")>]
    [<TestCase("too-many-files", "Too many changed files")>]
    member this.``summary state is explicit``(status: string, expectedTitle: string) =
        task {
            do! this.RouteSummary(summaryStateJson status)
            do! this.Goto()
            let card = this.Page.Locator($"[data-state='{status}']")
            do! card.WaitForAsync()
            let! title = card.Locator(".state-title").TextContentAsync()
            let! accordionCounts =
                this.Page.EvaluateAsync<int array>(
                    """() => [
                        document.querySelectorAll('.file-entry[aria-expanded="true"]').length,
                        document.querySelectorAll('.file-panel').length,
                        document.querySelectorAll('#patch').length
                    ]"""
                )

            Assert.Multiple(fun () ->
                Assert.That(title, Is.EqualTo(expectedTitle))
                Assert.That(accordionCounts, Is.EqualTo([| 0; 0; 0 |])))
        }

    [<TestCase("deleted", "")>]
    [<TestCase("binary", "Binary file")>]
    [<TestCase("oversized", "File is too large")>]
    [<TestCase("truncated", "Patch is too long")>]
    [<TestCase("symlink", "Symbolic link")>]
    [<TestCase("unavailable", "File unavailable")>]
    [<TestCase("timeout", "File diff timed out")>]
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
                            { status: 'timeout', file },
                            { status: 'git-error', file }
                        ].forEach(renderFileResult);
                        Diff2Html.html = original;
                        return calls;
                    }"""
                )

            Assert.That(calls, Is.EqualTo(0))
        }

    [<Test>]
    member this.``timeout states explain how to retry``() =
        task {
            do! this.RouteSummary(summaryStateJson "timeout")
            do! this.Goto()

            let summary = this.Page.Locator("[data-state='timeout']")
            do! summary.WaitForAsync()
            let! summaryText = summary.TextContentAsync()

            do! this.Page.UnrouteAsync("**/diff-summary?*")
            do! this.RouteSummary(readySummaryJson [| firstFile |])
            do! this.RouteFileStatus("timeout")
            let! _ = this.Page.ReloadAsync()

            let file = this.Page.Locator("[data-state='timeout']")
            do! file.WaitForAsync()
            let! fileText = file.TextContentAsync()

            Assert.Multiple(fun () ->
                Assert.That(
                    summaryText,
                    Is.EqualTo(
                        "Diff timed outGit did not finish within 10 seconds. Use Refresh to try again."
                    )
                )
                Assert.That(
                    fileText,
                    Is.EqualTo(
                        "File diff timed outSelect the file again to retry, or use Refresh to reload the comparison."
                    )
                ))
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
    member this.``toolbar glyphs and every change status have exact accessible semantics``() =
        task {
            let files =
                [| firstFile
                   fileJson "id-added" "src/added.txt" None "added"
                   fileJson "id-deleted" "src/deleted.txt" None "deleted"
                   secondFile
                   fileJson "id-untracked" "src/untracked.txt" None "untracked" |]

            do! this.RouteHighlighter()
            do! this.RouteSummary(readySummaryJson files)
            do! this.RouteFiles()
            do! this.Goto()
            do! this.Page.Locator(".file-entry").Nth(4).WaitForAsync()

            let! controls =
                this.Page.EvaluateAsync<string array array>(
                    """() => ['unified-view', 'split-view', 'refresh'].map(id => {
                        const button = document.getElementById(id);
                        return [
                            button.id,
                            button.getAttribute('aria-label'),
                            button.getAttribute('title'),
                            button.getAttribute('aria-pressed') || '',
                            button.firstElementChild?.tagName.toLowerCase() || '',
                            button.firstElementChild?.getAttribute('aria-hidden') || '',
                            String(button.querySelectorAll(':scope > svg').length),
                            button.textContent.trim()
                        ];
                    })"""
                )

            let! statuses =
                this.Page.EvaluateAsync<string array array>(
                    """() => [...document.querySelectorAll('.change-badge')].map(badge => [
                        badge.className,
                        badge.textContent,
                        badge.getAttribute('aria-label'),
                        badge.getAttribute('title')
                    ])"""
                )

            let! renamePaths =
                this.Page.EvaluateAsync<string array>(
                    """() => {
                        const entry = document.querySelector(
                            ".file-entry[data-identity='id-2']"
                        );
                        return [
                            entry.querySelector('.file-path').textContent,
                            entry.querySelector('.old-path').textContent,
                            entry.getAttribute('title')
                        ];
                    }"""
                )

            Assert.Multiple(fun () ->
                Assert.That(
                    controls,
                    Is.EqualTo(
                        [| [| "unified-view"; "Unified view"; "Unified view"; "true"; "svg"; "true"; "1"; "" |]
                           [| "split-view"; "Split view"; "Split view"; "false"; "svg"; "true"; "1"; "" |]
                           [| "refresh"; "Refresh diff"; "Refresh diff"; ""; "svg"; "true"; "1"; "" |] |]
                    )
                )
                Assert.That(
                    statuses,
                    Is.EqualTo(
                        [| [| "change-badge modified"; "~"; "Modified file"; "Modified file" |]
                           [| "change-badge added"; "+"; "Added file"; "Added file" |]
                           [| "change-badge deleted"; "−"; "Deleted file"; "Deleted file" |]
                           [| "change-badge renamed"; "→"; "Renamed file"; "Renamed file" |]
                           [| "change-badge untracked"; "+"; "Untracked file"; "Untracked file" |] |]
                    )
                )
                Assert.That(
                    renamePaths,
                    Is.EqualTo(
                        [| "src/new-name.txt"
                           "from src/old-name.txt"
                           "src/old-name.txt → src/new-name.txt" |]
                    )
                ))
        }

    [<Test>]
    member this.``wrapped rows keep gutters separate and source ranges exact in both views``() =
        task {
            do! this.Page.SetViewportSizeAsync(860, 900)
            do! this.RouteHighlighter()
            do! this.RouteSummary(readySummaryJson [| firstFile |])
            do! this.RoutePatch(wrappedPatch)
            do! this.Goto()
            do!
                this.Page.Locator("#patch[data-highlight-status='plain'] .d2h-file-diff").WaitForAsync(
                    LocatorWaitForOptions(Timeout = 15000.0f)
                )

            let geometryFailures mode =
                this.Page.Locator("#patch").EvaluateAsync<string array>(
                    $"""(patch) => {{
                        const failures = [];
                        const close = (left, right) => Math.abs(left - right) <= 1;
                        const inside = (inner, outer) =>
                            inner.left >= outer.left - 1 &&
                            inner.right <= outer.right + 1 &&
                            inner.top >= outer.top - 1 &&
                            inner.bottom <= outer.bottom + 1;
                        const noOverflow = (element, name) => {{
                            if (element.scrollWidth > element.clientWidth + 1)
                                failures.push(name + ' overflows horizontally');
                        }};
                        const checkRow = (row, name, mustWrap) => {{
                            if (!row) {{
                                failures.push(name + ' is missing');
                                return;
                            }}
                            const gutter = row.cells[0];
                            const code = row.cells[1];
                            const rowRect = row.getBoundingClientRect();
                            const gutterRect = gutter.getBoundingClientRect();
                            const codeRect = code.getBoundingClientRect();
                            const line = code.querySelector('.d2h-code-line, .d2h-code-side-line');
                            const lineHeight = line ? parseFloat(getComputedStyle(line).lineHeight) : 0;
                            if (!close(gutterRect.top, rowRect.top))
                                failures.push(name + ' gutter is not aligned to the first visual line');
                            if (gutterRect.right > codeRect.left + 1)
                                failures.push(name + ' gutter overlaps code');
                            [...gutter.children].filter(child => child.textContent.trim()).forEach(number => {{
                                if (!inside(number.getBoundingClientRect(), gutterRect))
                                    failures.push(name + ' number escapes its gutter');
                            }});
                            if (line && !inside(line.getBoundingClientRect(), rowRect))
                                failures.push(name + ' wrapped content escapes its logical row');
                            if (mustWrap && rowRect.height < lineHeight * 2)
                                failures.push(name + ' did not wrap');
                        }};
                        const checkNumbers = (root, name, side) => {{
                            root.querySelectorAll('tr[data-old-line], tr[data-new-line]').forEach(row => {{
                                const oldNumber = row.querySelector('.line-num1');
                                const newNumber = row.querySelector('.line-num2');
                                const sideNumber = row.querySelector('.d2h-code-side-linenumber');
                                if (row.querySelectorAll('.line-num1').length > 1)
                                    failures.push(name + ' row has multiple old numbers');
                                if (row.querySelectorAll('.line-num2').length > 1)
                                    failures.push(name + ' row has multiple new numbers');
                                if (oldNumber && oldNumber.textContent.trim() !== (row.dataset.oldLine || ''))
                                    failures.push(name + ' old number does not match row metadata');
                                if (newNumber && newNumber.textContent.trim() !== (row.dataset.newLine || ''))
                                    failures.push(name + ' new number does not match row metadata');
                                if (sideNumber) {{
                                    const visible = sideNumber.textContent.trim();
                                    const expected =
                                        side === 'old'
                                            ? row.dataset.oldLine || ''
                                            : row.dataset.newLine || '';
                                    if (visible !== expected)
                                        failures.push(name + ' side number does not match row metadata');
                                }}
                            }});
                        }};
                        noOverflow(document.querySelector('.content'), '{mode} content');
                        noOverflow(patch, '{mode} patch');
                        noOverflow(patch.querySelector('.d2h-wrapper'), '{mode} wrapper');
                        if ('{mode}' === 'unified') {{
                            const diff = patch.querySelector('.d2h-file-diff');
                            noOverflow(diff, 'unified diff');
                            checkNumbers(diff, 'unified', null);
                            checkRow(diff.querySelector("tr[data-old-line='1'][data-new-line='1']"), 'unified context', true);
                            checkRow(diff.querySelector("tr[data-old-line='2']:not([data-new-line])"), 'unified deletion', true);
                            checkRow(diff.querySelector("tr[data-new-line='2']:not([data-old-line])"), 'unified addition', true);
                            checkRow(diff.querySelector("tr[data-old-line='10'][data-new-line='10']"), 'unified hunk context', true);
                            diff.querySelectorAll('tr').forEach((row, index) =>
                                checkRow(row, 'unified row ' + index, false));
                        }} else {{
                            const sides = [...patch.querySelectorAll('.d2h-file-side-diff')];
                            if (sides.length !== 2) failures.push('split does not retain two columns');
                            if (sides.length === 2) {{
                                const leftRect = sides[0].getBoundingClientRect();
                                const rightRect = sides[1].getBoundingClientRect();
                                if (leftRect.right > rightRect.left + 1)
                                    failures.push('split columns overlap');
                            }}
                            sides.forEach((side, sideIndex) => {{
                                noOverflow(side, 'split side ' + sideIndex);
                                checkNumbers(
                                    side,
                                    'split side ' + sideIndex,
                                    sideIndex === 0 ? 'old' : 'new'
                                );
                                side.querySelectorAll('tr').forEach((row, rowIndex) =>
                                    checkRow(row, 'split row ' + sideIndex + ':' + rowIndex, false));
                            }});
                            checkRow(sides[0].querySelector("tr[data-old-line='2']:not([data-new-line])"), 'split deletion', true);
                            checkRow(sides[1].querySelector("tr[data-new-line='2']:not([data-old-line])"), 'split addition', true);
                            checkRow(sides[0].querySelector("tr[data-old-line='10'][data-new-line='10']"), 'split hunk context left', true);
                            checkRow(sides[1].querySelector("tr[data-old-line='10'][data-new-line='10']"), 'split hunk context right', true);
                        }}
                        return failures;
                    }}"""
                )

            let sourceContext selector =
                this.Page.Locator(selector).EvaluateAsync<string>(
                    """element => {
                        const range = document.createRange();
                        range.selectNodeContents(element);
                        return JSON.stringify(window.canvasSelectionMetadata({ range }));
                    }"""
                )

            let expected hunk oldRange newRange =
                $"""{{"kind":"diff","fileIdentity":"id-1","displayPath":"src/a.txt","oldDisplayPath":null,"hunkHeader":"{hunk}","oldLineRange":{oldRange},"newLineRange":{newRange}}}"""

            let! unifiedFailures = geometryFailures "unified"
            let! unifiedDeletion =
                sourceContext
                    "tr[data-old-line='2']:not([data-new-line]) .d2h-code-line-ctn"
            let! unifiedContext =
                sourceContext
                    "tr[data-old-line='10'][data-new-line='10'] .d2h-code-line-ctn"

            Assert.Multiple(fun () ->
                Assert.That(unifiedFailures, Is.Empty)
                Assert.That(
                    unifiedDeletion,
                    Is.EqualTo(expected "@@ -1,4 +1,4 @@" """{"start":2,"end":2}""" "null")
                )
                Assert.That(
                    unifiedContext,
                    Is.EqualTo(
                        expected
                            "@@ -10,2 +10,2 @@"
                            """{"start":10,"end":10}"""
                            """{"start":10,"end":10}"""
                    )
                ))

            do! this.Page.Locator("#split-view").ClickAsync()
            do! this.Page.Locator("#patch .d2h-files-diff").WaitForAsync()

            let! splitFailures = geometryFailures "split"
            let! splitAddition =
                sourceContext
                    ".d2h-file-side-diff:last-child tr[data-new-line='2']:not([data-old-line]) .d2h-code-line-ctn"
            let! splitContext =
                sourceContext
                    ".d2h-file-side-diff:last-child tr[data-old-line='10'][data-new-line='10'] .d2h-code-line-ctn"

            Assert.Multiple(fun () ->
                Assert.That(splitFailures, Is.Empty)
                Assert.That(
                    splitAddition,
                    Is.EqualTo(expected "@@ -1,4 +1,4 @@" "null" """{"start":2,"end":2}""")
                )
                Assert.That(
                    splitContext,
                    Is.EqualTo(
                        expected
                            "@@ -10,2 +10,2 @@"
                            """{"start":10,"end":10}"""
                            """{"start":10,"end":10}"""
                    )
                ))
        }

    [<Test>]
    member this.``patch stays usable when lazy syntax highlighting fails``() =
        task {
            do! this.RouteSummary(readySummaryJson [| syntaxFile |])
            do! this.RouteSyntaxPatch()
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
            do!
                this.Page.Locator("#patch[data-highlight-status='failed'] .d2h-files-diff").WaitForAsync(
                    LocatorWaitForOptions(Timeout = 15000.0f)
                )

            let! fileCount = this.Page.Locator(".file-entry").CountAsync()
            let! codeLineCount = this.Page.Locator("#patch .d2h-code-line-ctn").CountAsync()

            Assert.Multiple(fun () ->
                Assert.That(fileCount, Is.EqualTo(1))
                Assert.That(codeLineCount, Is.GreaterThan(0)))
        }

    [<Test>]
    member this.``syntax highlighting retries after a transient failure without reloading``() =
        task {
            // The asynchronous route callback must count requests across browser callbacks.
            let mutable requests = 0
            do! this.RouteSummary(readySummaryJson [| syntaxFile |])
            do! this.RouteSyntaxPatch()
            do!
                this.Page.RouteAsync(
                    $"**/{DiffAssets.Version}/diff2html-ui-slim.min.js",
                    fun route ->
                        requests <- requests + 1

                        if requests = 1 then
                            route.AbortAsync()
                        else
                            route.FulfillAsync(
                                RouteFulfillOptions(
                                    ContentType = "text/javascript",
                                    Body = highlighter
                                )
                            )
                )
            do! this.Goto()
            do!
                this.Page.Locator("#patch[data-highlight-status='failed'] .d2h-wrapper").WaitForAsync(
                    LocatorWaitForOptions(Timeout = 15000.0f)
                )

            do! this.Page.Locator("#split-view").ClickAsync()
            do!
                this.Page.Locator(
                    "#patch[data-highlight-status='ready'] .d2h-files-diff .d2h-code-line-ctn .hljs-keyword"
                ).First.WaitForAsync(
                    LocatorWaitForOptions(Timeout = 15000.0f)
                )

            Assert.That(requests, Is.EqualTo(2))
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
