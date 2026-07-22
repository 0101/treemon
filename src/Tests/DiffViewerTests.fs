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

let private secondFile =
    fileJson "id-2" "src/new-name.txt" (Some "src/old-name.txt") "renamed"

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
            let! viewerHeaders =
                this.Page.EvaluateAsync<string array>("() => window.__diffViewerHeaders")
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
                Assert.That(viewerHeaders.Length, Is.EqualTo(2))
                Assert.That(viewerHeaders[1], Is.EqualTo(viewerHeaders[0]))
                Assert.That(Guid.TryParseExact(viewerHeaders[0], "D") |> fst, Is.True)
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
            Assert.That(title, Is.EqualTo(expectedTitle))
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

            do! this.Page.UnrouteAsync("**/diff-summary")
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
    member this.``wrapped rows keep gutters separate and source ranges exact in both views``() =
        task {
            do! this.Page.SetViewportSizeAsync(860, 900)
            do! this.RouteHighlighter()
            do! this.RouteSummary(readySummaryJson [| firstFile |])
            do! this.RoutePatch(wrappedPatch)
            do! this.Goto()
            do!
                this.Page.Locator("#patch[data-highlight-status='ready'] .d2h-file-diff").WaitForAsync(
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
    member this.``syntax highlighting retries after a transient failure without reloading``() =
        task {
            // The asynchronous route callback must count requests across browser callbacks.
            let mutable requests = 0
            do! this.RouteSummary(readySummaryJson [| firstFile |])
            do! this.RouteFiles()
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
                this.Page.Locator("#patch[data-highlight-status='ready'] .d2h-files-diff").WaitForAsync(
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
