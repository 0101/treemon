module Tests.DiffViewerTests

open System
open System.Diagnostics
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

let private verificationArtifactPath filename =
    Path.GetFullPath(
        Path.Combine(
            __SOURCE_DIRECTORY__,
            "..",
            "..",
            ".agents",
            "verify",
            filename
        )
    )

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
        prefix + String.replicate 40 $"{word} "

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

let private exactLinePatch =
    let longLine prefix word =
        prefix + String.replicate 80 $"{word} "

    String.concat
        Environment.NewLine
        [ "diff --git a/src/a.txt b/src/a.txt"
          "index 1111111..2222222 100644"
          "--- a/src/a.txt"
          "+++ b/src/a.txt"
          "@@ -40,4 +40,5 @@"
          longLine " context line 40 " "context"
          longLine "+inserted line 41 " "inserted"
          longLine "-deleted old line 41 " "removed"
          longLine "+added new line 42 " "added"
          longLine " following context " "following"
          " trailing context"
          "" ]

let private categorizedFileJson identity displayPath oldDisplayPath change categoryPath =
    {| identity = identity
       displayPath = displayPath
       oldDisplayPath = oldDisplayPath
       linesAdded = (None: int option)
       linesRemoved = (None: int option)
       change = change
       categoryPath = (categoryPath: string list) |}

let private fileJson identity displayPath oldDisplayPath change =
    categorizedFileJson identity displayPath oldDisplayPath change []

let private fileJsonWithStats
    identity
    displayPath
    oldDisplayPath
    linesAdded
    linesRemoved
    change
    =
    {| identity = identity
       displayPath = displayPath
       oldDisplayPath = oldDisplayPath
       linesAdded = Some linesAdded
       linesRemoved = Some linesRemoved
       change = change
       categoryPath = List.empty<string> |}

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

let private readyLayerCounts committed local untracked =
    {| committed =
        {| status = "ready"
           fileCount = committed |}
       local =
        {| status = "ready"
           fileCount = local |}
       untracked =
        {| status = "ready"
           fileCount = untracked |} |}

let private categorizationJson status reason =
    {| status = status
       reason = (reason: string option) |}

let private summaryJsonWithCategorization categorization committed local untracked files =
    JsonSerializer.Serialize(
        {| status = "ready"
           baseRef = "origin/main"
           fileCount = Array.length files
           files = files
           categorization = categorization
           layerCounts = readyLayerCounts committed local untracked |}
    )

let private readySummaryJsonWithCounts committed local untracked files =
    summaryJsonWithCategorization
        (categorizationJson "missing" None)
        committed
        local
        untracked
        files

let private readySummaryJson files =
    readySummaryJsonWithCounts 2 3 1 files

let private configuredSummaryJson files =
    summaryJsonWithCategorization (categorizationJson "configured" None) 2 3 1 files

let private invalidSummaryJson reason files =
    summaryJsonWithCategorization
        (categorizationJson "invalid" (Some reason))
        2
        3
        1
        files

/// Builds `count` changed files that all classify into `path`; an empty path leaves them unmatched
/// so they collect in the synthetic trailing Other group.
let private categoryFiles (path: string list) (count: int) =
    let label =
        match path with
        | [] -> "unmatched"
        | names -> String.concat "-" names

    Array.init count (fun index ->
        categorizedFileJson
            $"id-{label}-{index}"
            $"src/{label}/file{index}.fs"
            (None: string option)
            "modified"
            path)

/// Reads the rendered disclosure state as `Ancestor > Child|aria-expanded` lines in document order,
/// which is the whole observable contract of the initial-disclosure rules.
let private categoryDisclosureScript =
    """() => {
        const lines = [];
        const walk = (section, ancestors) => {
            const button = section.querySelector(':scope > .category-entry');
            const path = ancestors.concat([button.querySelector('.category-name').textContent]);
            lines.push(path.join(' > ') + '|' + button.getAttribute('aria-expanded'));
            section
                .querySelectorAll(':scope > .category-panel > .category-item')
                .forEach(child => walk(child, path));
        };
        document
            .querySelectorAll('#file-list > .category-item')
            .forEach(section => walk(section, []));
        return lines;
    }"""

/// File rows the reader can actually see, so collapsed categories are proven to hide their files
/// rather than merely to flip an attribute.
let private visibleFileRowsScript =
    """() => [...document.querySelectorAll('.file-entry')]
        .filter(entry => entry.offsetParent !== null)
        .map(entry => entry.querySelector('.file-path').textContent)"""

let private configureLabel = "Analyze repository and configure diff groups"

/// The configure affordance is identified the way a user finds it — by its accessible label — so the
/// assertions never depend on an internal id or class.
let private configureSelector = $"button[aria-label=\"{configureLabel}\"]"

let private embeddedFrameSelector = "#diff-frame"

let private embeddedHostUrl = $"{ServerFixture.canvasUrl}/e2e-diff-host.html"

/// A minimal stand-in for the canvas pane. An iframe is the only thing that makes
/// `window.parent !== window` genuinely true, and what the document posts to its parent is the whole
/// observable contract of the configure action, so the harness embeds the real diff document and
/// records every action-bearing message it receives.
let private embeddedHostHtml (documentUrl: string) =
    String.concat
        ""
        [ "<!doctype html><html><head><meta charset=\"utf-8\"><title>diff pane</title></head>"
          "<body style=\"margin:0\">"
          "<script>window.__canvasMessages=[];"
          "window.addEventListener('message',function(event){"
          "if(event.data&&typeof event.data.action==='string')"
          "window.__canvasMessages.push(event.data)});"
          "</script>"
          "<iframe id=\"diff-frame\" style=\"width:100vw;height:100vh;border:0\" src=\""
          documentUrl
          "\"></iframe></body></html>" ]

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

let private replacementResultJson replacement =
    JsonSerializer.Serialize(
        {| status = "replacement"
           file = firstFile
           patch = samplePatch
           replacement = replacement |}
    )

let private summaryStateJson status =
    let layerCounts = readyLayerCounts 2 3 1

    match status with
    | "clean" ->
        JsonSerializer.Serialize(
            {| status = "clean"
               baseRef = "origin/main"
               fileCount = 0
               files = Array.empty<obj>
               layerCounts = layerCounts |}
        )
    | "filtered-empty" ->
        JsonSerializer.Serialize(
            {| status = "filtered-empty"
               fileCount = 0
               files = Array.empty<obj>
               layerCounts = layerCounts |}
        )
    | "too-many-files" ->
        JsonSerializer.Serialize(
            {| status = "too-many-files"
               minimumFileCount = 1001
               layerCounts = layerCounts |}
        )
    | _ ->
        JsonSerializer.Serialize
            {| status = status
               layerCounts = layerCounts |}

let private layerFilterQuery committed local untracked =
    $"?committed={committed.ToString().ToLowerInvariant()}&local={local.ToString().ToLowerInvariant()}&untracked={untracked.ToString().ToLowerInvariant()}"

let private createSummaryPerformanceRepo repoDir =
    let trackedPaths =
        [ 1..225 ]
        |> List.map (fun index -> Path.Combine("tracked", $"{index:D3}.txt"))

    let untrackedPaths =
        [ 1..25 ]
        |> List.map (fun index -> Path.Combine("untracked", $"{index:D3}.txt"))

    GitTestHelpers.initRepoOnMain repoDir

    trackedPaths
    |> List.iter (fun relativePath ->
        let path = Path.Combine(repoDir, relativePath)
        Path.GetDirectoryName(path) |> Directory.CreateDirectory |> ignore
        File.WriteAllText(path, "base"))

    GitTestHelpers.gitOk repoDir [ "add"; "--"; "." ]
    GitTestHelpers.gitOk repoDir [ "commit"; "-m"; "performance base" ]
    GitTestHelpers.gitOk repoDir [ "checkout"; "-b"; "performance" ]

    trackedPaths
    |> List.iter (fun relativePath ->
        File.WriteAllText(Path.Combine(repoDir, relativePath), "changed"))

    untrackedPaths
    |> List.iter (fun relativePath ->
        let path = Path.Combine(repoDir, relativePath)
        Path.GetDirectoryName(path) |> Directory.CreateDirectory |> ignore
        File.WriteAllText(path, "untracked"))

    DiffProvisioner.provisionViewer repoDir |> ignore

let private performanceAgentKnowing worktreePath =
    let agent = RefreshScheduler.createAgent ()

    let info: GitWorktree.WorktreeInfo =
        { Path = PathUtils.normalizePath worktreePath
          Head = ""
          Branch = Some "performance" }

    agent.Post(
        RefreshScheduler.UpdateWorktreeList(
            RepoId "diff-summary-performance",
            [ info ]
        )
    )

    agent.PostAndAsyncReply(RefreshScheduler.GetState)
    |> TestUtils.runAsync
    |> ignore

    agent

let private performanceSummaryUrl baseUrl worktreePath =
    let encoded =
        worktreePath
        |> PathUtils.normalizePath
        |> Uri.EscapeDataString

    $"{baseUrl}/{encoded}/diff-summary?committed=true&local=true&untracked=true"

let private summaryPathCounts (json: string) =
    use doc = JsonDocument.Parse(json)

    let files =
        doc.RootElement.GetProperty("files").EnumerateArray()
        |> Seq.toList

    let untracked =
        files
        |> List.filter (fun file ->
            file.GetProperty("change").GetString() = "untracked")
        |> List.length

    let withStats =
        files
        |> List.filter (fun file ->
            file.GetProperty("linesAdded").ValueKind = JsonValueKind.Number
            && file.GetProperty("linesRemoved").ValueKind = JsonValueKind.Number)
        |> List.length

    files.Length, untracked, withStats

let private geometryRow side selector name =
    {| selector = selector; name = name; side = side; mustWrap = true |}

let private geometryProbe (page: IPage) mode checkAllRows includeMetrics unifiedRows splitRows =
    task {
        let config =
            {| mode = mode; checkAllRows = checkAllRows; includeMetrics = includeMetrics
               rows = if mode = "unified" then unifiedRows else splitRows |}
        let! raw =
            page.Locator("#patch").EvaluateAsync<string>(
                """(patch, config) => {
                    const failures = [];
                    const metrics = {};
                    const inside = (inner, outer) =>
                        inner.left >= outer.left - 1 &&
                        inner.right <= outer.right + 1 &&
                        inner.top >= outer.top - 1 && inner.bottom <= outer.bottom + 1;
                    const noOverflow = (element, name) => {
                        if (!element) { failures.push(name + ' is missing'); return; }
                        if (config.includeMetrics) metrics[name] =
                            { clientWidth: element.clientWidth, scrollWidth: element.scrollWidth };
                        if (element.scrollWidth > element.clientWidth + 1)
                            failures.push(name + ' overflows horizontally');
                    };
                    const visibleNumberRect = element => {
                        if (!element || !element.textContent.trim()) return null;
                        const range = document.createRange(); range.selectNodeContents(element);
                        return range.getBoundingClientRect();
                    };
                    const checkNumbers = (root, name, side) => {
                        if (!root) { failures.push(name + ' is missing'); return; }
                        root.querySelectorAll('tr[data-old-line], tr[data-new-line]').forEach(row => {
                            const checkNumber = (selector, key) => {
                                const numbers = row.querySelectorAll(selector);
                                if (numbers.length > 1) failures.push(name + ' row has multiple ' + key + ' numbers');
                                if (numbers[0] && numbers[0].textContent.trim() !==
                                    (row.dataset[key + 'Line'] || ''))
                                    failures.push(name + ' ' + key + ' number does not match row metadata');
                            };
                            checkNumber('.line-num1', 'old');
                            checkNumber('.line-num2', 'new');
                            const sideNumber = row.querySelector('.d2h-code-side-linenumber');
                            if (sideNumber) {
                                const expected = side === 'old' ? row.dataset.oldLine || '' : row.dataset.newLine || '';
                                if (sideNumber.textContent.trim() !== expected) failures.push(name + ' side number does not match row metadata');
                            }
                        });
                    };
                    const checkRow = (row, name, mustWrap) => {
                        if (!row) { failures.push(name + ' is missing'); return; }
                        const gutter = row.cells[0];
                        const code = row.cells[1];
                        if (!gutter || !code) { failures.push(name + ' gutter or code is missing'); return; }
                        const line = code.querySelector('.d2h-code-line, .d2h-code-side-line');
                        if (!line && config.includeMetrics) { failures.push(name + ' wrapped content is missing'); return; }
                        const rowRect = row.getBoundingClientRect(), gutterRect = gutter.getBoundingClientRect();
                        const codeRect = code.getBoundingClientRect();
                        const lineRect = line && line.getBoundingClientRect();
                        const lineHeight = line ? parseFloat(getComputedStyle(line).lineHeight) : 0;
                        if (config.includeMetrics) metrics[name] = {
                            row: [rowRect.left, rowRect.top, rowRect.right, rowRect.bottom],
                            gutter: [gutterRect.left, gutterRect.top, gutterRect.right, gutterRect.bottom],
                            code: [codeRect.left, codeRect.top, codeRect.right, codeRect.bottom],
                            line: [lineRect.left, lineRect.top, lineRect.right, lineRect.bottom], lineHeight
                        };
                        if (!config.includeMetrics && Math.abs(gutterRect.top - rowRect.top) > 1) failures.push(name + ' gutter is not aligned to the first visual line');
                        if (gutterRect.right > codeRect.left + 1) failures.push(name + ' gutter overlaps code');
                        if (lineRect && !inside(lineRect, rowRect)) failures.push(name + ' wrapped content escapes its logical row');
                        if (mustWrap && rowRect.height < lineHeight * 2) failures.push(name + ' did not wrap');

                        if (!config.includeMetrics) {
                            [...gutter.children].filter(child => child.textContent.trim()).forEach(number => {
                                if (!inside(number.getBoundingClientRect(), gutterRect)) failures.push(name + ' number escapes its gutter');
                            });
                        } else {
                            const oldRect = visibleNumberRect(row.querySelector('.line-num1'));
                            const newRect = visibleNumberRect(row.querySelector('.line-num2'));
                            [oldRect, newRect].filter(Boolean).forEach(numberRect => {
                                if (numberRect.left < gutterRect.left - 1 || numberRect.right > gutterRect.right + 1)
                                    failures.push(name + ' number escapes its gutter');
                            });
                            if (oldRect && newRect && oldRect.right > newRect.left + 1) failures.push(name + ' old and new gutters overlap');
                        }
                    };

                    if (config.includeMetrics) noOverflow(document.body, config.mode + ' body');
                    noOverflow(document.querySelector('.content'), config.mode + ' content');
                    noOverflow(patch, config.mode + ' patch');
                    noOverflow(patch.querySelector('.d2h-wrapper'), config.mode + ' wrapper');

                    const split = config.mode === 'split';
                    const roots = split ? [...patch.querySelectorAll('.d2h-file-side-diff')]
                        : [patch.querySelector('.d2h-file-diff')];
                    if (split) {
                        if (config.includeMetrics) metrics.splitColumnCount = roots.length;
                        if (roots.length !== 2) failures.push('split does not retain two columns');
                        if (roots.length === 2) {
                            const leftRect = roots[0].getBoundingClientRect(), rightRect = roots[1].getBoundingClientRect();
                            if (config.includeMetrics)
                                metrics.splitColumns = [[leftRect.left, leftRect.right], [rightRect.left, rightRect.right]];
                            if (leftRect.right > rightRect.left + 1) failures.push('split columns overlap');
                        }
                    }
                    roots.forEach((root, index) => {
                        const name = split ? 'split side ' + index : 'unified diff';
                        noOverflow(root, name);
                        if (config.includeMetrics)
                            noOverflow(root && root.querySelector('.d2h-diff-table'),
                                split ? 'split table ' + index : 'unified table');
                        if (config.checkAllRows) {
                            const side = split ? index === 0 ? 'old' : 'new' : null;
                            checkNumbers(root, split ? name : 'unified', side);
                            root && root.querySelectorAll('tr').forEach((row, rowIndex) =>
                                checkRow(row, split ? 'split row ' + index + ':' + rowIndex
                                    : 'unified row ' + rowIndex, false));
                        }
                    });

                    config.rows.forEach(spec => {
                        const root = spec.side < 0 ? roots[0] : roots[spec.side];
                        checkRow(root && root.querySelector(spec.selector),
                            spec.name, spec.mustWrap);
                    });
                    return JSON.stringify({ viewportWidth: window.innerWidth, mode: config.mode, failures, metrics });
                }""",
                config
            )
        use document = JsonDocument.Parse(raw)
        let failures =
            document.RootElement.GetProperty("failures").EnumerateArray() |> Seq.map _.GetString() |> Seq.toArray

        return raw, failures
    }

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
        // A viewer generated by an older Treemon: no marker yet, but it carries the pinned renderer
        // route, so it must still receive template updates.
        File.WriteAllText(
            diffPath,
            "<!doctype html><link rel=\"stylesheet\" href=\"/assets/diff2html/3.4.52/diff2html.min.css\">")
        let third = DiffProvisioner.provisionViewer tempDir

        Assert.Multiple(fun () ->
            Assert.That(first.IsSome, Is.True)
            Assert.That(second, Is.EqualTo(None))
            Assert.That(third.IsSome, Is.True)
            Assert.That(File.ReadAllText(diffPath), Is.EqualTo(DiffTemplate.html)))

    [<Test>]
    member _.``provisioning preserves a document the user authored at the viewer path``() =
        let diffPath = Path.Combine(tempDir, ".agents", "canvas", "diff.html")
        Directory.CreateDirectory(Path.GetDirectoryName(diffPath)) |> ignore
        let authored = "<!doctype html><title>My notes</title><p>hand written</p>"
        File.WriteAllText(diffPath, authored)

        let outcome = DiffProvisioner.provisionViewer tempDir

        Assert.Multiple(fun () ->
            Assert.That(
                File.ReadAllText(diffPath),
                Is.EqualTo(authored),
                "An AgentDoc authored before this filename became a SystemView must survive provisioning")
            Assert.That(outcome.IsSome, Is.True, "Skipping an unknown document is worth reporting"))

    [<Test>]
    member _.``diff viewer is a non-authored SystemView``() =
        Assert.Multiple(fun () ->
            Assert.That(CanvasDocKinds.classify "diff.html", Is.EqualTo(SystemView))
            Assert.That(CanvasDocKinds.classify "DIFF.HTML", Is.EqualTo(SystemView))
            Assert.That(
                CanvasDocServer.buildInjection (CanvasDocKinds.classify "diff.html") "diff.html",
                Does.Not.Contain(IdiomorphScript.idiomorphJs)
            ))

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type DiffViewerVisibilityTests() =

    let doc filename kind : CanvasDoc =
        { Filename = filename
          ContentHash = "hash"
          LastModified = DateTimeOffset.UnixEpoch
          OwnerSessionId = None
          Kind = kind }

    let inventory = [ doc "diff.html" SystemView; doc "beads.html" SystemView; doc "notes.html" AgentDoc ]

    let filenames comparison =
        DiffProvisioner.visibleDocs comparison inventory |> List.map _.Filename

    [<Test>]
    member _.``a confirmed clean worktree offers no diff view``() =
        Assert.That(filenames GitWorktree.Clean, Is.EqualTo([ "beads.html"; "notes.html" ]))

    [<Test>]
    member _.``a worktree with content keeps every document``() =
        Assert.That(filenames GitWorktree.HasContent, Is.EqualTo([ "diff.html"; "beads.html"; "notes.html" ]))

    [<Test>]
    member _.``an unevaluated comparison keeps the view that reports the failure``() =
        Assert.That(
            filenames GitWorktree.Undetermined,
            Is.EqualTo([ "diff.html"; "beads.html"; "notes.html" ]),
            "Only a confirmed clean worktree may hide the diff view")

    [<Test>]
    member _.``the reserved viewer path is hidden whoever wrote it``() =
        // CanvasDocKinds.classify recognises diff.html by filename alone, so the scanner cannot
        // report an authored document there as an AgentDoc. Hiding is keyed on the reserved path.
        let authored = [ doc "diff.html" AgentDoc ]

        Assert.That(
            DiffProvisioner.visibleDocs GitWorktree.Clean authored,
            Is.Empty,
            "A clean worktree offers nothing at the reserved diff path")

[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type DiffAssetsTests() =

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

    member private this.RouteDashboardWithDiffDoc(branch: string) =
        let converter = Fable.Remoting.Json.FableJsonConverter()
        let diffDoc: CanvasDoc =
            { Filename = "diff.html"
              ContentHash = "e2e-diff"
              LastModified = DateTimeOffset(2026, 7, 22, 12, 0, 0, TimeSpan.Zero)
              OwnerSessionId = None
              Kind = CanvasDocKind.SystemView }

        let routeHandler =
            Func<IRoute, Task>(fun route ->
                (task {
                    let! upstream = route.FetchAsync()
                    let! json = upstream.TextAsync()
                    let response =
                        Newtonsoft.Json.JsonConvert.DeserializeObject<DashboardResponse>(json, converter)

                    let addDiff (worktree: WorktreeStatus) =
                        if worktree.Branch <> branch
                           || worktree.CanvasDocs |> List.exists _.Filename.Equals(diffDoc.Filename, StringComparison.OrdinalIgnoreCase)
                        then
                            worktree
                        else
                            { worktree with
                                CanvasDocs = worktree.CanvasDocs @ [ diffDoc ] }

                    let withDiff =
                        { response with
                            Repos =
                                response.Repos
                                |> List.map (fun repo ->
                                    { repo with
                                        Worktrees = repo.Worktrees |> List.map addDiff }) }

                    let body =
                        Newtonsoft.Json.JsonConvert.SerializeObject(withDiff, converter)
                    do!
                        route.FulfillAsync(
                            RouteFulfillOptions(
                                ContentType = "application/json",
                                Body = body
                            )
                        )
                } :> Task))

        this.Page.RouteAsync("**/IWorktreeApi/getWorktrees", routeHandler)

    member private this.Goto() =
        task {
            let! _ =
                this.Page.GotoAsync(
                    pageUrl,
                    PageGotoOptions(WaitUntil = WaitUntilState.Load)
                )

            ()
        }

    member private this.RouteEmbeddedHost() =
        this.Page.RouteAsync(
            "**/e2e-diff-host.html*",
            fun route ->
                let documentUrl =
                    Uri(route.Request.Url).Query.Substring("?doc=".Length)
                    |> Uri.UnescapeDataString

                route.FulfillAsync(
                    RouteFulfillOptions(
                        ContentType = "text/html; charset=utf-8",
                        Body = embeddedHostHtml documentUrl
                    )
                )
        )

    /// Opens the diff document the way the pane does — inside an iframe — so the availability rule
    /// is exercised against a real parent window instead of a simulated one.
    member private this.GotoEmbedded(documentUrl: string) =
        task {
            let! _ =
                this.Page.GotoAsync(
                    $"{embeddedHostUrl}?doc={Uri.EscapeDataString documentUrl}",
                    PageGotoOptions(WaitUntil = WaitUntilState.Load)
                )

            ()
        }

    member private this.EmbeddedFrame() =
        this.Page.FrameLocator(embeddedFrameSelector)

    /// Activates a configure affordance and waits for the host to record the resulting message, so
    /// an assertion can never read the harness before the document has posted.
    member private this.ActivateConfigure(action: ILocator) =
        task {
            let! before =
                this.Page.EvaluateAsync<int>(
                    "() => window.__canvasMessages.length"
                )

            do! action.ClickAsync()

            let! _ =
                this.Page.WaitForFunctionAsync(
                    "expected => window.__canvasMessages.length === expected",
                    before + 1
                )

            ()
        }

    /// Opens one repository's embedded diff view, activates the toolbar action, and returns the
    /// exact request text that repository's document posted.
    member private this.PostedConfigureRequest(documentUrl: string) =
        task {
            do! this.GotoEmbedded(documentUrl)
            let action = this.EmbeddedFrame().Locator(configureSelector)
            do! action.WaitForAsync()
            do! this.ActivateConfigure(action)

            return!
                this.Page.EvaluateAsync<string>(
                    "() => window.__canvasMessages[0].request"
                )
        }

    member private this.ActivateFile(identity: string) =
        task {
            let entry =
                this.Page.Locator($".file-entry[data-identity='{identity}']")

            do! entry.WaitForAsync()
            do! entry.ClickAsync()
        }

    member private this.ToggleCategory(name: string) =
        task {
            let header =
                this.Page.Locator($".category-entry:has(.category-name:text-is('{name}'))")

            do! header.WaitForAsync()
            do! header.ClickAsync()
        }

    /// Marks the rendered tree, runs an action that reloads the summary, and waits for the
    /// replacement tree, so an assertion can never read the pre-refresh DOM.
    member private this.RerenderCategories(act: unit -> Task) =
        task {
            let! _ =
                this.Page.EvaluateAsync<obj>(
                    """() => document
                        .querySelectorAll('#file-list > *')
                        .forEach(node => { node.dataset.stale = 'true'; })"""
                )

            do! act ()

            let! _ =
                this.Page.WaitForFunctionAsync(
                    """() => document.querySelectorAll('#file-list [data-stale]').length === 0
                        && document.querySelectorAll('#file-list .category-entry').length > 0"""
                )

            ()
        }

    member private this.RefreshCategories() =
        this.RerenderCategories(fun () -> this.Page.Locator("#refresh").ClickAsync())

    member private this.ToggleUntrackedLayer() =
        this.RerenderCategories(fun () ->
            task {
                let! _ =
                    this.Page.EvaluateAsync<obj>(
                        """() => {
                            const untracked = document.getElementById('filter-untracked');
                            untracked.checked = !untracked.checked;
                            untracked.dispatchEvent(new Event('change', { bubbles: true }));
                        }"""
                    )

                ()
            }
            :> Task)

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
                do!
                    this.Page.Locator(
                        ".file-entry[data-identity='id-1']"
                    ).WaitForAsync()

            let! accordionCounts =
                this.Page.EvaluateAsync<int array>(
                    """() => [
                        document.querySelectorAll('.file-entry.active').length,
                        document.querySelectorAll('.file-entry[aria-expanded="true"]').length,
                        document.querySelectorAll('.file-panel').length,
                        document.querySelectorAll('#patch').length
                    ]"""
                )

            Assert.That(accordionCounts, Is.EqualTo([| 0; 0; 0; 0 |]))
        }

    [<SetUp>]
    member this.RouteTemplateAndCoreAssets() =
        task {
            do! this.RouteBody("**/diff.html", "text/html; charset=utf-8", template)
            do! this.RouteEmbeddedHost()
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
            do! this.ActivateFile("id-js")
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
            do! this.ActivateFile("id-js")
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
            do! this.ActivateFile("id-1")
            do!
                this.Page.Locator(
                    "#patch[data-highlight-status='ready'], #patch[data-highlight-status='plain'], #patch[data-highlight-status='failed']"
                ).WaitForAsync(LocatorWaitForOptions(Timeout = 15000.0f))
            let codeLine = this.Page.Locator("#patch .d2h-code-line-ctn").First
            do! CanvasTestHelpers.assertStandaloneSelectionUnavailable this.Page codeLine
        }

    [<Test>]
    member this.``file action selects the filename and opens file-level selection actions``() =
        task {
            do! this.RouteHighlighter()
            do! this.RouteSummary(readySummaryJson [| firstFile |])
            do! this.RouteFiles()
            do! this.Goto()

            let action = this.Page.Locator(".file-actions-button")
            do! action.WaitForAsync()
            do! action.ClickAsync()
            let! _ =
                this.Page.WaitForFunctionAsync(
                    "() => { const host = document.querySelector('canvas-selection-context'); return host?.style.display === 'block' && host.style.visibility === 'visible'; }",
                    null,
                    PageWaitForFunctionOptions(Timeout = 5000.0f)
                )

            let! state =
                this.Page.EvaluateAsync<string array>(
                    """() => {
                        const selection = window.getSelection();
                        const range = selection.getRangeAt(0);
                        return [
                            selection.toString(),
                            document.querySelector('.file-actions-button').getAttribute('aria-label'),
                            String(document.querySelectorAll('.file-entry.active').length),
                            String(document.querySelectorAll('.file-panel').length),
                            JSON.stringify(window.canvasSelectionMetadata({ range }))
                        ];
                    }"""
                )

            Assert.That(
                state,
                Is.EqualTo(
                    [| "src/a.txt"
                       "Actions for src/a.txt"
                       "0"
                       "0"
                       """{"kind":"diff","fileIdentity":"id-1","displayPath":"src/a.txt","oldDisplayPath":null,"hunkHeader":null,"oldLineRange":null,"newLineRange":null}""" |]
                )
            )
        }

    [<Test>]
    member this.``pane-hosted diff selection exposes and emits all generic actions with exact source context``() =
        task {
            let browserErrors =
                System.Collections.Concurrent.ConcurrentQueue<string>()

            this.Page.Console.Add(fun message ->
                if message.Type = "error" then
                    browserErrors.Enqueue($"console: {message.Text}"))

            this.Page.PageError.Add(fun error ->
                browserErrors.Enqueue($"pageerror: {error}"))

            do!
                this.Page.AddInitScriptAsync(
                    """(() => {
                        if (window.top !== window) return;
                        window.__paneSelectionMessages = [];
                        window.addEventListener('message', event => {
                            const active = document.querySelector('.canvas-iframe-active');
                            if (
                                active &&
                                event.source === active.contentWindow &&
                                event.data?.action === 'canvas-selection'
                            ) {
                                window.__paneSelectionMessages.push(event.data);
                            }
                        });
                    })()"""
                )

            do! this.RouteDashboardWithDiffDoc("feature-active")

            let forwardedRequests =
                Array.init 3 (fun _ ->
                    TaskCompletionSource<string>(
                        TaskCreationOptions.RunContinuationsAsynchronously
                    ))
            // Playwright invokes this impure route callback for each pane-to-server delivery.
            let mutable forwardedIndex = 0
            let successBody =
                Newtonsoft.Json.JsonConvert.SerializeObject(
                    CanvasMessageResult.Ok,
                    Fable.Remoting.Json.FableJsonConverter()
                )

            do!
                this.Page.RouteAsync(
                    "**/IWorktreeApi/sendCanvasMessage",
                    fun route ->
                        task {
                            let index = forwardedIndex
                            forwardedIndex <- forwardedIndex + 1

                            if index < forwardedRequests.Length then
                                let postData =
                                    route.Request.PostData
                                    |> Option.ofObj
                                    |> Option.defaultValue ""
                                forwardedRequests[index].TrySetResult(postData)
                                |> ignore

                            do!
                                route.FulfillAsync(
                                    RouteFulfillOptions(
                                        ContentType = "application/json",
                                        Body = successBody
                                    )
                                )
                        }
                        :> Task
                )

            do! this.RouteHighlighter()
            do! this.RouteSummary(readySummaryJson [| firstFile |])
            do! this.RouteFiles()

            let! _ =
                this.Page.GotoAsync(
                    ServerFixture.viteUrl,
                    PageGotoOptions(WaitUntil = WaitUntilState.Load)
                )

            let card =
                this.Page.Locator(
                    ".wt-card",
                    PageLocatorOptions(
                        Has =
                            this.Page.Locator(
                                ".branch-name",
                                PageLocatorOptions(HasText = "feature-active")
                            )
                    )
                )

            do! card.Locator(".diff-action-btn").ClickAsync()

            let activeIframe = this.Page.Locator(".canvas-pane .canvas-iframe-active")
            do!
                activeIframe.WaitForAsync(
                    LocatorWaitForOptions(Timeout = 10000.0f)
                )
            let! paneUrl = activeIframe.GetAttributeAsync("src")
            TestContext.Out.WriteLine(
                $"SIDE_BY_SIDE_PANE dashboard={ServerFixture.viteUrl} api={ServerFixture.serverUrl} canvas={ServerFixture.canvasUrl} iframe={paneUrl}"
            )

            let diffFrame =
                this.Page.FrameLocator(".canvas-pane .canvas-iframe-active")

            do!
                diffFrame.Locator(
                    ".file-entry[data-identity='id-1']"
                ).ClickAsync()
            do!
                diffFrame.Locator(
                    "#patch[data-highlight-status='ready'], #patch[data-highlight-status='plain'], #patch[data-highlight-status='failed']"
                ).WaitForAsync(LocatorWaitForOptions(Timeout = 15000.0f))

            let selectChangedLines () =
                task {
                    let deletion =
                        diffFrame.Locator(
                            "tr[data-old-line='2']:not([data-new-line]) .d2h-code-line-ctn"
                        )

                    do! deletion.WaitForAsync()
                    let! _ =
                        deletion.EvaluateAsync(
                            """start => {
                                const end = document.querySelector(
                                    "tr[data-new-line='2']:not([data-old-line]) .d2h-code-line-ctn"
                                );
                                const range = document.createRange();
                                range.setStartBefore(start);
                                range.setEndAfter(end);
                                const selection = window.getSelection();
                                selection.removeAllRanges();
                                selection.addRange(range);
                                document.dispatchEvent(new Event('selectionchange'));
                            }"""
                        )

                    do!
                        diffFrame.Locator(
                            "canvas-selection-context button[data-intent='explain']"
                        ).WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
                }

            do! selectChangedLines ()

            let explain =
                diffFrame.Locator(
                    "canvas-selection-context button[data-intent='explain']"
                )
            let remove =
                diffFrame.Locator(
                    "canvas-selection-context button[data-intent='remove']"
                )
            let comment =
                diffFrame.Locator("canvas-selection-context button[data-comment]")
            let actionButtons =
                diffFrame.Locator("canvas-selection-context .actions button")

            let! actionLabels = actionButtons.AllTextContentsAsync()
            let! explainVisible = explain.IsVisibleAsync()
            let! removeVisible = remove.IsVisibleAsync()
            let! commentVisible = comment.IsVisibleAsync()
            let! isPaneHosted =
                diffFrame.Locator("body").EvaluateAsync<bool>(
                    "body => window.top !== window && window.parent === window.top"
                )

            Assert.Multiple(fun () ->
                Assert.That(isPaneHosted, Is.True)
                Assert.That(
                    actionLabels,
                    Is.EqualTo([| "Explain"; "Remove"; "Comment" |])
                )
                Assert.That(explainVisible, Is.True)
                Assert.That(removeVisible, Is.True)
                Assert.That(commentVisible, Is.True))

            do! explain.ClickAsync()
            let! _ =
                this.Page.WaitForFunctionAsync(
                    "() => window.__paneSelectionMessages.length === 1",
                    null,
                    PageWaitForFunctionOptions(Timeout = 5000.0f)
                )
            let! firstForwarded =
                forwardedRequests[0].Task.WaitAsync(TimeSpan.FromSeconds(5.0))

            do! selectChangedLines ()
            do! remove.ClickAsync()
            let! _ =
                this.Page.WaitForFunctionAsync(
                    "() => window.__paneSelectionMessages.length === 2",
                    null,
                    PageWaitForFunctionOptions(Timeout = 5000.0f)
                )
            let! secondForwarded =
                forwardedRequests[1].Task.WaitAsync(TimeSpan.FromSeconds(5.0))

            do! selectChangedLines ()
            do! comment.ClickAsync()
            let commentInput =
                diffFrame.Locator("canvas-selection-context input")
            do! commentInput.FillAsync("Verify this change")
            do! commentInput.PressAsync("Enter")
            let! _ =
                this.Page.WaitForFunctionAsync(
                    "() => window.__paneSelectionMessages.length === 3",
                    null,
                    PageWaitForFunctionOptions(Timeout = 5000.0f)
                )
            let! thirdForwarded =
                forwardedRequests[2].Task.WaitAsync(TimeSpan.FromSeconds(5.0))
            let! _ =
                this.Page.EvaluateAsync(
                    "() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))"
                )

            let! rawMessages =
                this.Page.EvaluateAsync<string>(
                    "() => JSON.stringify(window.__paneSelectionMessages)"
                )
            TestContext.Out.WriteLine(
                $"Pane-hosted diff selection payloads:{Environment.NewLine}{rawMessages}"
            )
            TestContext.Out.WriteLine(
                $"Pane-forwarded selection requests:{Environment.NewLine}{JsonSerializer.Serialize([| firstForwarded; secondForwarded; thirdForwarded |])}"
            )

            let! evidence =
                this.Page.EvaluateAsync<string>(
                    """() => JSON.stringify(window.__paneSelectionMessages.map(message => ({
                        intent: message.intent,
                        doc: message.doc,
                        hasContextBefore: Object.hasOwn(message, 'contextBefore'),
                        hasContextAfter: Object.hasOwn(message, 'contextAfter'),
                        hasSelectedText: typeof message.selectedText === 'string' && message.selectedText.length > 0,
                        request: message.request,
                        action: message.action,
                        sourceContext: message.sourceContext
                    })))"""
                )

            let sourceContext =
                """{"kind":"diff","fileIdentity":"id-1","displayPath":"src/a.txt","oldDisplayPath":null,"hunkHeader":"@@ -1,3 +1,4 @@","oldLineRange":{"start":2,"end":2},"newLineRange":{"start":2,"end":2}}"""

            let expected =
                "["
                + $"""{{"intent":"explain","doc":"diff.html","hasContextBefore":false,"hasContextAfter":false,"hasSelectedText":true,"request":"User asked to explain/expand this","action":"canvas-selection","sourceContext":{sourceContext}}}"""
                + ","
                + $"""{{"intent":"remove","doc":"diff.html","hasContextBefore":false,"hasContextAfter":false,"hasSelectedText":true,"request":"User asked to remove this","action":"canvas-selection","sourceContext":{sourceContext}}}"""
                + ","
                + $"""{{"intent":"comment","doc":"diff.html","hasContextBefore":false,"hasContextAfter":false,"hasSelectedText":true,"request":"User commented: Verify this change","action":"canvas-selection","sourceContext":{sourceContext}}}"""
                + "]"

            let browserErrorEvidence = browserErrors.ToArray()
            TestContext.Out.WriteLine(
                $"Pane browser errors:{Environment.NewLine}{JsonSerializer.Serialize(browserErrorEvidence)}"
            )

            Assert.Multiple(fun () ->
                Assert.That(evidence, Is.EqualTo(expected))
                Assert.That(forwardedIndex, Is.EqualTo(3))
                Assert.That(browserErrorEvidence, Is.Empty))
        }

    [<Test>]
    member this.``side-by-side pane and standalone smoke match across viewer controls``() =
        task {
            let browserErrors =
                System.Collections.Concurrent.ConcurrentQueue<string>()

            this.Page.Console.Add(fun message ->
                if message.Type = "error" then
                    browserErrors.Enqueue($"console: {message.Text}"))

            this.Page.PageError.Add(fun error ->
                browserErrors.Enqueue($"pageerror: {error}"))

            let files =
                [| syntaxFile
                   firstFile
                   secondFile
                   fileJson "id-added" "src/added.txt" None "added"
                   fileJson "id-deleted" "src/deleted.txt" None "deleted"
                   fileJson "id-untracked" "src/untracked.txt" None "untracked" |]

            do! this.RouteDashboardWithDiffDoc("feature-active")
            do! this.RouteHighlighter()
            do! this.RouteSummary(readySummaryJson files)
            do!
                this.Page.RouteAsync(
                    "**/diff-file?*",
                    fun route ->
                        let uri = Uri(route.Request.Url)
                        let identity =
                            Uri.UnescapeDataString(
                                uri.Query.Substring("?identity=".Length)
                            )
                        let file =
                            files
                            |> Array.find (fun candidate ->
                                candidate.identity = identity)
                        let patch =
                            if identity = syntaxFile.identity then
                                syntaxPatch
                            else
                                exactLinePatch

                        route.FulfillAsync(
                            RouteFulfillOptions(
                                ContentType = "application/json",
                                Body =
                                    fileResultJsonWithPatch
                                        patch
                                        "text"
                                        file.identity
                                        file.displayPath
                                        file.oldDisplayPath
                                        file.change
                            )
                        )
                )

            let! _ =
                this.Page.GotoAsync(
                    ServerFixture.viteUrl,
                    PageGotoOptions(WaitUntil = WaitUntilState.Load)
                )

            let card =
                this.Page.Locator(
                    ".wt-card",
                    PageLocatorOptions(
                        Has =
                            this.Page.Locator(
                                ".branch-name",
                                PageLocatorOptions(HasText = "feature-active")
                            )
                    )
                )

            do! card.Locator(".diff-action-btn").ClickAsync()
            let activeIframe =
                this.Page.Locator(".canvas-pane .canvas-iframe-active")
            do! activeIframe.WaitForAsync()
            let! paneUrl = activeIframe.GetAttributeAsync("src")
            let paneFrame =
                this.Page.Frames
                |> Seq.find (fun frame ->
                    frame.Url.EndsWith("/diff.html", StringComparison.Ordinal))

            let runSmoke (frame: IFrame) host =
                task {
                    let syntaxEntry =
                        frame.Locator(".file-entry[data-identity='id-js']")
                    do! syntaxEntry.WaitForAsync()
                    do! syntaxEntry.ClickAsync()
                    do!
                        frame.Locator(
                            "#patch[data-highlight-status='ready'] .hljs-keyword"
                        ).First.WaitForAsync(
                            LocatorWaitForOptions(Timeout = 15000.0f)
                        )
                    let! highlightTokenCount =
                        frame.Locator(
                            "#patch .d2h-code-line-ctn span[class*='hljs-']"
                        ).CountAsync()

                    let setUntracked value =
                        frame.EvaluateAsync(
                            """value => {
                                const input = document.getElementById('filter-untracked');
                                input.checked = value;
                                input.dispatchEvent(new Event('change', { bubbles: true }));
                            }""",
                            value
                        )

                    let waitForSummary () =
                        frame.Locator(
                            ".file-entry[data-identity='id-untracked']"
                        ).WaitForAsync(
                            LocatorWaitForOptions(Timeout = 10000.0f)
                        )

                    let first =
                        frame.Locator(".file-entry[data-identity='id-1']")
                    let second =
                        frame.Locator(".file-entry[data-identity='id-2']")

                    let! _ = setUntracked false
                    do! waitForSummary ()
                    let! _ = setUntracked true
                    do! waitForSummary ()

                    do! second.ClickAsync()
                    do! second.Locator("xpath=../..").Locator("#patch").WaitForAsync()
                    let! pointerActive =
                        frame.Locator(".file-entry.active").GetAttributeAsync(
                            "data-identity"
                        )

                    do! first.FocusAsync()
                    do! first.PressAsync("Enter")
                    do! first.Locator("xpath=../..").Locator("#patch").WaitForAsync()
                    let! keyboardActive =
                        frame.Locator(".file-entry.active").GetAttributeAsync(
                            "data-identity"
                        )

                    do! frame.Locator("#unified-view").ClickAsync()
                    do! frame.Locator("#patch .d2h-file-diff").WaitForAsync()
                    let! unified =
                        frame.Locator("#patch").EvaluateAsync<string>(
                            """patch => {
                                const failures = [];
                                const roots = [
                                    document.getElementById('content'),
                                    patch,
                                    patch.querySelector('.d2h-wrapper'),
                                    patch.querySelector('.d2h-file-diff')
                                ];
                                roots.forEach((root, index) => {
                                    if (root.scrollWidth > root.clientWidth + 1) {
                                        failures.push('unified overflow ' + index);
                                    }
                                });
                                [
                                    "tr[data-old-line='41']:not([data-new-line])",
                                    "tr[data-new-line='42']:not([data-old-line])",
                                    "tr[data-old-line='42'][data-new-line='43']"
                                ].forEach(selector => {
                                    const row = patch.querySelector(selector);
                                    const gutter = row.cells[0].getBoundingClientRect();
                                    const code = row.cells[1].getBoundingClientRect();
                                    const line = row.querySelector('.d2h-code-line');
                                    const lineHeight =
                                        parseFloat(getComputedStyle(line).lineHeight);
                                    if (gutter.right > code.left + 1) {
                                        failures.push(selector + ' gutter overlap');
                                    }
                                    if (row.getBoundingClientRect().height < lineHeight * 2) {
                                        failures.push(selector + ' did not wrap');
                                    }
                                });
                                return JSON.stringify({
                                    failures,
                                    splitColumns:
                                        patch.querySelectorAll('.d2h-file-side-diff').length
                                });
                            }"""
                        )

                    do! frame.Locator("#split-view").ClickAsync()
                    do! frame.Locator("#patch .d2h-files-diff").WaitForAsync()
                    let! split =
                        frame.Locator("#patch").EvaluateAsync<string>(
                            """patch => {
                                const failures = [];
                                const sides =
                                    [...patch.querySelectorAll('.d2h-file-side-diff')];
                                if (sides.length !== 2) {
                                    failures.push('split column count ' + sides.length);
                                }
                                if (sides.length === 2) {
                                    const left = sides[0].getBoundingClientRect();
                                    const right = sides[1].getBoundingClientRect();
                                    if (left.right > right.left + 1) {
                                        failures.push('split columns overlap');
                                    }
                                    sides.forEach((side, index) => {
                                        if (side.scrollWidth > side.clientWidth + 1) {
                                            failures.push('split overflow ' + index);
                                        }
                                    });
                                }
                                return JSON.stringify({
                                    failures,
                                    splitColumns: sides.length
                                });
                            }"""
                        )

                    let! semantics =
                        frame.EvaluateAsync<string>(
                            """() => JSON.stringify({
                                host: window.top === window ? 'standalone' : 'pane',
                                filters: [
                                    document.getElementById('filter-committed').checked,
                                    document.getElementById('filter-local').checked,
                                    document.getElementById('filter-untracked').checked
                                ],
                                active: document.querySelector('.file-entry.active')
                                    .dataset.identity,
                                expanded:
                                    document.querySelectorAll(
                                        '.file-entry[aria-expanded="true"]'
                                    ).length,
                                panels: document.querySelectorAll('.file-panel').length,
                                patches: document.querySelectorAll('#patch').length,
                                controls: ['unified-view', 'split-view', 'refresh']
                                    .map(id => {
                                        const button = document.getElementById(id);
                                        return [
                                            button.getAttribute('aria-label'),
                                            button.getAttribute('title'),
                                            button.querySelectorAll(':scope > svg').length,
                                            button.textContent.trim()
                                        ];
                                    }),
                                statuses: [...document.querySelectorAll('.change-badge')]
                                    .map(badge => [
                                        badge.className,
                                        badge.textContent,
                                        badge.getAttribute('aria-label'),
                                        badge.getAttribute('title')
                                    ])
                            })"""
                        )

                    TestContext.Out.WriteLine(
                        $"SIDE_BY_SIDE_SMOKE host={host} unified={unified} split={split} semantics={semantics}"
                    )

                    use unifiedDocument = JsonDocument.Parse(unified)
                    use splitDocument = JsonDocument.Parse(split)
                    let unifiedFailures =
                        unifiedDocument.RootElement
                            .GetProperty("failures")
                            .GetArrayLength()
                    let splitFailures =
                        splitDocument.RootElement
                            .GetProperty("failures")
                            .GetArrayLength()

                    Assert.Multiple(fun () ->
                        Assert.That(pointerActive, Is.EqualTo("id-2"))
                        Assert.That(keyboardActive, Is.EqualTo("id-1"))
                        Assert.That(highlightTokenCount, Is.GreaterThan(0))
                        Assert.That(unifiedFailures, Is.Zero)
                        Assert.That(splitFailures, Is.Zero))

                    return semantics
                }

            let! paneEvidence = runSmoke paneFrame "pane"
            let! _ =
                this.Page.GotoAsync(
                    paneUrl,
                    PageGotoOptions(WaitUntil = WaitUntilState.Load)
                )
            let! standaloneEvidence =
                runSmoke this.Page.MainFrame "standalone"

            let normalizeHost (value: string) =
                value
                    .Replace("\"host\":\"pane\"", "\"host\":\"host\"")
                    .Replace("\"host\":\"standalone\"", "\"host\":\"host\"")

            let browserErrorEvidence = browserErrors.ToArray()
            TestContext.Out.WriteLine(
                $"Side-by-side browser errors:{Environment.NewLine}{JsonSerializer.Serialize(browserErrorEvidence)}"
            )

            Assert.Multiple(fun () ->
                Assert.That(
                    normalizeHost standaloneEvidence,
                    Is.EqualTo(normalizeHost paneEvidence)
                )
                Assert.That(browserErrorEvidence, Is.Empty))
        }

    [<Test>]
    member this.``initial refresh and filter summaries stay collapsed without explicit selection``() =
        task {
            do!
                this.Page.AddInitScriptAsync(
                    """(() => {
                        window.__diffFileRequests = [];
                        window.__summaryRequests = 0;
                        const originalFetch = window.fetch;
                        window.fetch = function(input) {
                            const url = typeof input === 'string' ? input : input.url;
                            if (url.includes('diff-file')) {
                                window.__diffFileRequests.push(new URL(url, location.href).href);
                            }
                            if (url.includes('diff-summary')) {
                                window.__summaryRequests += 1;
                            }
                            return originalFetch.apply(this, arguments);
                        };
                    })()"""
                )
            do! this.RouteHighlighter()
            do! this.RouteSummary(readySummaryJson [| firstFile; secondFile |])
            do! this.RouteFiles()
            do! this.Goto()
            do!
                this.Page.Locator(
                    ".file-entry[data-identity='id-2']"
                ).WaitForAsync()

            let accordionState () =
                this.Page.EvaluateAsync<string array>(
                    """() => [
                        String(document.querySelectorAll('.file-entry.active').length),
                        String(document.querySelectorAll('.file-entry[aria-expanded="true"]').length),
                        String(document.querySelectorAll('.file-panel').length),
                        String(document.querySelectorAll('[data-state="loading-file"]').length),
                        String(document.querySelectorAll('#patch').length),
                        String(state.selected === null),
                        String(state.currentResult === null),
                        String(state.currentPatch === null),
                        String(state.panel === null),
                        String(state.selectedButton === null),
                        String(window.__diffFileRequests.length)
                    ]"""
                )

            let expectedCollapsed requests =
                [| "0"
                   "0"
                   "0"
                   "0"
                   "0"
                   "true"
                   "true"
                   "true"
                   "true"
                   "true"
                   string requests |]

            let! initial = accordionState ()
            Assert.That(initial, Is.EqualTo(expectedCollapsed 0))

            do! this.Page.Locator("#refresh").ClickAsync()
            let! _ =
                this.Page.WaitForFunctionAsync(
                    "() => window.__summaryRequests === 2 && document.querySelectorAll('.file-entry').length === 2"
                )
            let! refreshed = accordionState ()
            Assert.That(refreshed, Is.EqualTo(expectedCollapsed 0))

            do! this.Page.Locator("#filter-untracked").CheckAsync()
            let! _ =
                this.Page.WaitForFunctionAsync(
                    "() => window.__summaryRequests === 3 && document.querySelectorAll('.file-entry').length === 2"
                )
            let! filtered = accordionState ()
            Assert.That(filtered, Is.EqualTo(expectedCollapsed 0))
        }

    [<Test>]
    member this.``ready summary removes summary loading and renders nonzero change-kind totals``() =
        task {
            let files =
                [| fileJson "added" "added.txt" None "added"
                   fileJson "untracked" "untracked.txt" None "untracked"
                   fileJson "modified" "modified.txt" None "modified"
                   fileJson "renamed" "new.txt" (Some "old.txt") "renamed"
                   fileJson "deleted" "deleted.txt" None "deleted" |]

            do! this.RouteHighlighter()
            do! this.RouteSummary(readySummaryJson files)
            do! this.RouteFiles()
            do! this.Goto()
            do! this.Page.Locator(".file-entry").Nth(4).WaitForAsync()

            let summary = this.Page.Locator("#change-summary")
            let! text = summary.TextContentAsync()
            let! label = summary.GetAttributeAsync("aria-label")
            let! staleLoading = this.Page.Locator("[data-state='loading-summary']").CountAsync()
            let! staleSummaryState = this.Page.Locator("#summary-state > *").CountAsync()
            let! staleHeading = this.Page.GetByText("Changed files").CountAsync()
            let! colors =
                summary.EvaluateAsync<string array>(
                    """element => {
                        const color = selector => getComputedStyle(element.querySelector(selector)).color;
                        return [
                            color('.change-summary-added .change-summary-label'),
                            color('.change-summary-added .change-summary-count'),
                            color('.change-summary-modified .change-summary-label'),
                            color('.change-summary-modified .change-summary-count'),
                            color('.change-summary-removed .change-summary-label'),
                            color('.change-summary-removed .change-summary-count')
                        ];
                    }"""
                )

            Assert.Multiple(fun () ->
                Assert.That(text, Is.EqualTo("Added 2Modified 2Removed 1"))
                Assert.That(label, Is.EqualTo("Added 2, Modified 2, Removed 1"))
                Assert.That(
                    colors,
                    Is.EqualTo(
                        [| "rgb(186, 194, 222)"
                           "rgb(166, 227, 161)"
                           "rgb(186, 194, 222)"
                           "rgb(137, 180, 250)"
                           "rgb(186, 194, 222)"
                           "rgb(243, 139, 168)" |]
                    )
                )
                Assert.That(staleLoading, Is.Zero)
                Assert.That(staleSummaryState, Is.Zero)
                Assert.That(staleHeading, Is.Zero))
        }

    [<Test>]
    member this.``configured summaries nest categories with subtree counts and text-only names``() =
        task {
            let files =
                [| categorizedFileJson
                       "id-client-app"
                       "src/Client/App.fs"
                       None
                       "modified"
                       [ "Production code"; "Client" ]
                   categorizedFileJson
                       "id-client-view"
                       "src/Client/View.fs"
                       None
                       "modified"
                       [ "Production code"; "Client" ]
                   categorizedFileJson
                       "id-server-api"
                       "src/Server/Api.fs"
                       None
                       "modified"
                       [ "Production code"; "Server" ]
                   categorizedFileJson
                       "id-tests"
                       "src/Tests/ApiTests.fs"
                       None
                       "added"
                       [ "Tests" ]
                   categorizedFileJson
                       "id-docs"
                       "docs/spec/api.md"
                       None
                       "modified"
                       [ "<b>Docs</b>" ]
                   fileJson "id-other" "notes.txt" None "untracked" |]

            do! this.RouteSummary(configuredSummaryJson files)
            do! this.Goto()
            do! this.Page.Locator(".category-entry").Nth(4).WaitForAsync()

            let! outline =
                this.Page.EvaluateAsync<string array>(
                    """() => {
                        const lines = [];
                        const walk = (section, depth) => {
                            const button = section.querySelector(':scope > .category-entry');
                            const panel = section.querySelector(':scope > .category-panel');
                            const count = button.querySelector('.category-count');
                            lines.push([
                                depth,
                                button.querySelector('.category-name').textContent,
                                count.textContent,
                                count.getAttribute('aria-label'),
                                button.getAttribute('aria-expanded')
                            ].join('|'));
                            [...panel.children].forEach(child => {
                                if (child.classList.contains('category-item')) walk(child, depth + 1);
                                else lines.push(
                                    [depth, 'file', child.querySelector('.file-path').textContent].join('|')
                                );
                            });
                        };
                        document.querySelectorAll('#file-list > .category-item')
                            .forEach(section => walk(section, 1));
                        return lines;
                    }"""
                )

            let! presentation =
                this.Page.EvaluateAsync<string array>(
                    """() => {
                        const padding = element => parseFloat(getComputedStyle(element).paddingLeft);
                        const branch = document.querySelector('#file-list > .category-item > .category-entry');
                        const nested = document.querySelector(
                            '#file-list > .category-item > .category-panel > .category-item > .category-entry'
                        );
                        const row = document.querySelector('.category-panel .file-entry');
                        return [
                            String(document.querySelectorAll('#file-list b').length),
                            String(document.querySelectorAll('.file-item').length),
                            document.querySelector(
                                '#file-list > .category-item:last-child .category-name'
                            ).textContent,
                            getComputedStyle(document.getElementById('category-warning')).display,
                            String(padding(nested) > padding(branch)),
                            String(padding(row) > padding(nested)),
                            getComputedStyle(document.querySelector('.category-name')).fontSize,
                            getComputedStyle(document.querySelector('.category-count')).fontSize
                        ];
                    }"""
                )

            Assert.Multiple(fun () ->
                Assert.That(
                    outline,
                    Is.EqualTo(
                        [| "1|Production code|3|3 files|true"
                           "2|Client|2|2 files|true"
                           "2|file|src/Client/App.fs"
                           "2|file|src/Client/View.fs"
                           "2|Server|1|1 file|true"
                           "2|file|src/Server/Api.fs"
                           "1|Tests|1|1 file|true"
                           "1|file|src/Tests/ApiTests.fs"
                           "1|<b>Docs</b>|1|1 file|true"
                           "1|file|docs/spec/api.md"
                           "1|Other|1|1 file|true"
                           "1|file|notes.txt" |]
                    )
                )
                Assert.That(
                    presentation,
                    Is.EqualTo(
                        [| "0"
                           "6"
                           "Other"
                           "none"
                           "true"
                           "true"
                           "13px"
                           "11px" |]
                    )
                ))
        }

    [<Test>]
    member this.``configured summaries render only changed categories and omit an empty other group``() =
        task {
            let files =
                [| categorizedFileJson
                       "id-tests-a"
                       "src/Tests/ApiTests.fs"
                       None
                       "modified"
                       [ "Tests" ]
                   categorizedFileJson
                       "id-tests-b"
                       "src/Tests/ViewTests.fs"
                       None
                       "added"
                       [ "Tests" ] |]

            do! this.RouteSummary(configuredSummaryJson files)
            do! this.Goto()
            do! this.Page.Locator(".category-entry").WaitForAsync()

            let! rendered =
                this.Page.EvaluateAsync<string array>(
                    """() => [
                        String(document.querySelectorAll('#file-list > .category-item').length),
                        String(document.querySelectorAll('.category-item').length),
                        document.querySelector('.category-name').textContent,
                        document.querySelector('.category-count').textContent,
                        String([...document.querySelectorAll('.category-name')]
                            .some(name => name.textContent === 'Other')),
                        String(document.querySelectorAll('.file-item').length)
                    ]"""
                )

            Assert.That(
                rendered,
                Is.EqualTo([| "1"; "1"; "Tests"; "2"; "false"; "2" |])
            )
        }

    [<Test>]
    member this.``missing categorization renders the flat file list``() =
        task {
            do! this.RouteSummary(readySummaryJson [| firstFile; secondFile |])
            do! this.Goto()
            do!
                this.Page.Locator(
                    ".file-entry[data-identity='id-2']"
                ).WaitForAsync()

            let! rendered =
                this.Page.EvaluateAsync<string array>(
                    """() => [
                        String(document.querySelectorAll('#file-list > .file-item').length),
                        String(document.querySelectorAll('.category-item').length),
                        String(document.querySelectorAll('.category-entry').length),
                        [...document.querySelectorAll('#file-list > .file-item .file-path')]
                            .map(path => path.textContent)
                            .join(','),
                        getComputedStyle(document.getElementById('category-warning')).display,
                        document.getElementById('category-warning').textContent
                    ]"""
                )

            Assert.That(
                rendered,
                Is.EqualTo(
                    [| "2"
                       "0"
                       "0"
                       "src/a.txt,src/new-name.txt"
                       "none"
                       "" |]
                )
            )
        }

    [<Test>]
    member this.``invalid categorization warns above the unchanged flat list``() =
        task {
            let summaries =
                [| invalidSummaryJson
                       "each category needs a name"
                       [| firstFile; secondFile |]
                   configuredSummaryJson
                       [| categorizedFileJson
                              "id-configured"
                              "src/Server/Api.fs"
                              None
                              "modified"
                              [ "Production code" ] |] |]
            // The route callback owns the sequence of summaries served to Refresh.
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
            do!
                this.Page.Locator(
                    ".file-entry[data-identity='id-2']"
                ).WaitForAsync()

            let! warned =
                this.Page.EvaluateAsync<string array>(
                    """() => {
                        const warning = document.getElementById('category-warning');
                        const list = document.getElementById('file-list');
                        const totals = document.getElementById('change-summary');
                        return [
                            warning.textContent,
                            warning.getAttribute('role'),
                            String(warning.querySelectorAll('.category-warning-text').length),
                            String(getComputedStyle(warning).display !== 'none'),
                            String(Boolean(
                                warning.compareDocumentPosition(list) & Node.DOCUMENT_POSITION_FOLLOWING
                            )),
                            String(Boolean(
                                warning.compareDocumentPosition(totals) & Node.DOCUMENT_POSITION_PRECEDING
                            )),
                            String(warning.getBoundingClientRect().bottom
                                <= list.getBoundingClientRect().top + 1),
                            totals.textContent,
                            String(document.querySelectorAll('#file-list > .file-item').length),
                            String(document.querySelectorAll('.category-item').length)
                        ];
                    }"""
                )

            do! this.ActivateFile("id-1")
            do! this.Page.Locator("#patch .d2h-wrapper").WaitForAsync()
            let! selectable = this.Page.Locator("#patch").CountAsync()

            do! this.Page.Locator("#refresh").ClickAsync()
            do! this.Page.Locator(".category-entry").WaitForAsync()

            let! afterRefresh =
                this.Page.EvaluateAsync<string array>(
                    """() => [
                        document.getElementById('category-warning').textContent,
                        String(document.querySelectorAll('#category-warning > *').length),
                        document.querySelector('.category-name').textContent,
                        String(document.querySelectorAll('#file-list > .file-item').length)
                    ]"""
                )

            Assert.Multiple(fun () ->
                Assert.That(
                    warned,
                    Is.EqualTo(
                        [| "Diff groups are not applied: each category needs a name."
                           "status"
                           "1"
                           "true"
                           "true"
                           "true"
                           "true"
                           "Modified 2"
                           "2"
                           "0" |]
                    )
                )
                Assert.That(selectable, Is.EqualTo(1))
                Assert.That(
                    afterRefresh,
                    Is.EqualTo([| ""; "0"; "Production code"; "0" |])
                ))
        }

    [<Test>]
    member this.``initial disclosure opens the worked example as an architectural outline``() =
        task {
            let files =
                Array.concat
                    [ categoryFiles [ "Production code"; "Client" ] 3
                      categoryFiles [ "Production code"; "Server" ] 4
                      categoryFiles [ "Production code"; "Shared" ] 1
                      categoryFiles [ "Tests" ] 6
                      categoryFiles [ "Docs" ] 2
                      categoryFiles [ "Instructions" ] 1 ]

            do! this.RouteSummary(configuredSummaryJson files)
            do! this.Goto()
            do! this.Page.Locator(".category-entry").Nth(6).WaitForAsync()

            let! disclosure =
                this.Page.EvaluateAsync<string array>(categoryDisclosureScript)
            let! visible =
                this.Page.EvaluateAsync<string array>(visibleFileRowsScript)

            Assert.Multiple(fun () ->
                Assert.That(
                    disclosure,
                    Is.EqualTo(
                        [| "Production code|true"
                           "Production code > Client|false"
                           "Production code > Server|false"
                           "Production code > Shared|false"
                           "Tests|false"
                           "Docs|true"
                           "Instructions|true" |]
                    )
                )
                Assert.That(
                    visible,
                    Is.EqualTo(
                        [| "src/Docs/file0.fs"
                           "src/Docs/file1.fs"
                           "src/Instructions/file0.fs" |]
                    )
                ))
        }

    [<Test>]
    member this.``leaf categories expand at five files and collapse at six``() =
        task {
            let files =
                Array.concat
                    [ categoryFiles [ "Five" ] 5; categoryFiles [ "Six" ] 6 ]

            do! this.RouteSummary(configuredSummaryJson files)
            do! this.Goto()
            do! this.Page.Locator(".category-entry").Nth(1).WaitForAsync()

            let! disclosure =
                this.Page.EvaluateAsync<string array>(categoryDisclosureScript)
            let! visible =
                this.Page.EvaluateAsync<string array>(visibleFileRowsScript)

            Assert.Multiple(fun () ->
                Assert.That(
                    disclosure,
                    Is.EqualTo([| "Five|true"; "Six|false" |])
                )
                Assert.That(
                    visible,
                    Is.EqualTo(
                        [| "src/Five/file0.fs"
                           "src/Five/file1.fs"
                           "src/Five/file2.fs"
                           "src/Five/file3.fs"
                           "src/Five/file4.fs" |]
                    )
                ))
        }

    [<Test>]
    member this.``branches expand at five direct children and collapse at six``() =
        task {
            let childFiles parent count =
                Array.init count (fun index ->
                    categoryFiles [ parent; $"Child{index}" ] 1)
                |> Array.concat

            let files = Array.append (childFiles "Narrow" 5) (childFiles "Wide" 6)

            do! this.RouteSummary(configuredSummaryJson files)
            do! this.Goto()
            do! this.Page.Locator(".category-entry").Nth(6).WaitForAsync()

            let! disclosure =
                this.Page.EvaluateAsync<string array>(categoryDisclosureScript)
            let! visible =
                this.Page.EvaluateAsync<string array>(visibleFileRowsScript)

            Assert.Multiple(fun () ->
                Assert.That(
                    disclosure,
                    Is.EqualTo(
                        [| "Narrow|true"
                           "Narrow > Child0|true"
                           "Narrow > Child1|true"
                           "Narrow > Child2|true"
                           "Narrow > Child3|true"
                           "Narrow > Child4|true"
                           "Wide|false"
                           "Wide > Child0|true"
                           "Wide > Child1|true"
                           "Wide > Child2|true"
                           "Wide > Child3|true"
                           "Wide > Child4|true"
                           "Wide > Child5|true" |]
                    )
                )
                Assert.That(visible.Length, Is.EqualTo(5)))
        }

    [<Test>]
    member this.``a branch over the file limit forces only its direct children collapsed``() =
        task {
            let files =
                Array.concat
                    [ categoryFiles [ "Production code"; "Client"; "Views" ] 2
                      categoryFiles [ "Production code"; "Client"; "State" ] 1
                      categoryFiles [ "Production code"; "Server" ] 4
                      categoryFiles [ "Production code"; "Shared"; "Contracts" ] 4
                      categoryFiles [ "Production code"; "Shared"; "Utils" ] 3 ]

            do! this.RouteSummary(configuredSummaryJson files)
            do! this.Goto()
            do!
                this.Page.Locator(
                    ".category-entry:has(.category-name:text-is('Shared'))"
                ).WaitForAsync()

            let! forced =
                this.Page.EvaluateAsync<string array>(categoryDisclosureScript)
            let! hidden =
                this.Page.EvaluateAsync<string array>(visibleFileRowsScript)

            // Opening a forced-collapsed branch must reveal its normal outline: the small subtree
            // under Client opens through to its files, while Shared keeps forcing its own children.
            do! this.ToggleCategory("Client")
            let! openedSmall =
                this.Page.EvaluateAsync<string array>(visibleFileRowsScript)

            do! this.ToggleCategory("Shared")
            let! opened =
                this.Page.EvaluateAsync<string array>(categoryDisclosureScript)
            let! openedLarge =
                this.Page.EvaluateAsync<string array>(visibleFileRowsScript)

            Assert.Multiple(fun () ->
                Assert.That(
                    forced,
                    Is.EqualTo(
                        [| "Production code|true"
                           "Production code > Client|false"
                           "Production code > Client > Views|true"
                           "Production code > Client > State|true"
                           "Production code > Server|false"
                           "Production code > Shared|false"
                           "Production code > Shared > Contracts|false"
                           "Production code > Shared > Utils|false" |]
                    )
                )
                Assert.That(hidden, Is.Empty)
                Assert.That(
                    openedSmall,
                    Is.EqualTo(
                        [| "src/Production code-Client-Views/file0.fs"
                           "src/Production code-Client-Views/file1.fs"
                           "src/Production code-Client-State/file0.fs" |]
                    )
                )
                Assert.That(
                    opened,
                    Is.EqualTo(
                        [| "Production code|true"
                           "Production code > Client|true"
                           "Production code > Client > Views|true"
                           "Production code > Client > State|true"
                           "Production code > Server|false"
                           "Production code > Shared|true"
                           "Production code > Shared > Contracts|false"
                           "Production code > Shared > Utils|false" |]
                    )
                )
                Assert.That(openedLarge, Is.EqualTo(openedSmall)))
        }

    [<Test>]
    member this.``the other group follows the leaf disclosure rule``() =
        task {
            let summaries =
                [| configuredSummaryJson (
                       Array.append (categoryFiles [ "Tests" ] 1) (categoryFiles [] 6)
                   )
                   configuredSummaryJson (
                       Array.append (categoryFiles [ "Tests" ] 1) (categoryFiles [] 5)
                   ) |]
            // The route callback owns the sequence of summaries served to Refresh.
            let mutable summaryIndex = 0

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
            do! this.Goto()
            do! this.Page.Locator(".category-entry").Nth(1).WaitForAsync()

            let! overLimit =
                this.Page.EvaluateAsync<string array>(categoryDisclosureScript)
            let! visibleOverLimit =
                this.Page.EvaluateAsync<string array>(visibleFileRowsScript)

            do! this.RefreshCategories()

            let! atLimit =
                this.Page.EvaluateAsync<string array>(categoryDisclosureScript)
            let! visibleAtLimit =
                this.Page.EvaluateAsync<string array>(visibleFileRowsScript)

            Assert.Multiple(fun () ->
                Assert.That(
                    overLimit,
                    Is.EqualTo([| "Tests|true"; "Other|false" |])
                )
                Assert.That(
                    visibleOverLimit,
                    Is.EqualTo([| "src/Tests/file0.fs" |])
                )
                Assert.That(atLimit, Is.EqualTo([| "Tests|true"; "Other|true" |]))
                Assert.That(visibleAtLimit.Length, Is.EqualTo(6)))
        }

    [<Test>]
    member this.``explicit toggles are keyed by path so delimiters in names cannot collide``() =
        task {
            let files =
                [| categorizedFileJson
                       "id-slash-parent"
                       "src/slash-parent.fs"
                       None
                       "modified"
                       [ "A/B"; "C" ]
                   categorizedFileJson
                       "id-slash-child"
                       "src/slash-child.fs"
                       None
                       "modified"
                       [ "A"; "B/C" ] |]

            do! this.RouteSummary(configuredSummaryJson files)
            do! this.Goto()
            do! this.Page.Locator(".category-entry").Nth(3).WaitForAsync()

            let! before =
                this.Page.EvaluateAsync<string array>(categoryDisclosureScript)

            do! this.ToggleCategory("C")

            let! afterToggle =
                this.Page.EvaluateAsync<string array>(categoryDisclosureScript)
            let! visible =
                this.Page.EvaluateAsync<string array>(visibleFileRowsScript)

            do! this.RefreshCategories()

            let! afterRefresh =
                this.Page.EvaluateAsync<string array>(categoryDisclosureScript)

            Assert.Multiple(fun () ->
                Assert.That(
                    before,
                    Is.EqualTo(
                        [| "A/B|true"
                           "A/B > C|true"
                           "A|true"
                           "A > B/C|true" |]
                    )
                )
                Assert.That(
                    afterToggle,
                    Is.EqualTo(
                        [| "A/B|true"
                           "A/B > C|false"
                           "A|true"
                           "A > B/C|true" |]
                    )
                )
                Assert.That(visible, Is.EqualTo([| "src/slash-child.fs" |]))
                Assert.That(afterRefresh, Is.EqualTo(afterToggle)))
        }

    [<Test>]
    member this.``explicit toggles survive refresh, a layer filter change, and a category returning``() =
        task {
            let full =
                configuredSummaryJson (
                    Array.concat
                        [ categoryFiles [ "Production code"; "Client" ] 2
                          categoryFiles [ "Production code"; "Server" ] 1
                          categoryFiles [ "Tests" ] 6
                          categoryFiles [ "Docs" ] 1 ]
                )

            let withoutDocs =
                configuredSummaryJson (
                    Array.concat
                        [ categoryFiles [ "Production code"; "Client" ] 2
                          categoryFiles [ "Production code"; "Server" ] 1
                          categoryFiles [ "Tests" ] 6 ]
                )

            // Load, Refresh, layer-filter change, Refresh: Docs disappears from the third summary
            // and returns in the fourth.
            let summaries = [| full; full; withoutDocs; full |]
            let mutable summaryIndex = 0

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
            do! this.Goto()
            do! this.Page.Locator(".category-entry").Nth(4).WaitForAsync()

            let! computed =
                this.Page.EvaluateAsync<string array>(categoryDisclosureScript)

            do! this.ToggleCategory("Tests")
            do! this.ToggleCategory("Docs")
            do! this.ToggleCategory("Production code")

            let! chosen =
                this.Page.EvaluateAsync<string array>(categoryDisclosureScript)

            do! this.RefreshCategories()

            let! afterRefresh =
                this.Page.EvaluateAsync<string array>(categoryDisclosureScript)

            do! this.ToggleUntrackedLayer()

            let! afterFilter =
                this.Page.EvaluateAsync<string array>(categoryDisclosureScript)

            do! this.RefreshCategories()

            let! afterReturn =
                this.Page.EvaluateAsync<string array>(categoryDisclosureScript)

            let expectedChoices =
                [| "Production code|false"
                   "Production code > Client|true"
                   "Production code > Server|true"
                   "Tests|true"
                   "Docs|false" |]

            Assert.Multiple(fun () ->
                Assert.That(
                    computed,
                    Is.EqualTo(
                        [| "Production code|true"
                           "Production code > Client|true"
                           "Production code > Server|true"
                           "Tests|false"
                           "Docs|true" |]
                    )
                )
                Assert.That(chosen, Is.EqualTo(expectedChoices))
                Assert.That(afterRefresh, Is.EqualTo(expectedChoices))
                Assert.That(
                    afterFilter,
                    Is.EqualTo(
                        [| "Production code|false"
                           "Production code > Client|true"
                           "Production code > Server|true"
                           "Tests|true" |]
                    )
                )
                Assert.That(afterReturn, Is.EqualTo(expectedChoices)))
        }

    [<Test>]
    member this.``reloading the page returns categories to their computed defaults``() =
        task {
            let files =
                Array.append (categoryFiles [ "Tests" ] 6) (categoryFiles [ "Docs" ] 1)

            do! this.RouteSummary(configuredSummaryJson files)
            do! this.Goto()
            do! this.Page.Locator(".category-entry").Nth(1).WaitForAsync()

            do! this.ToggleCategory("Tests")
            do! this.ToggleCategory("Docs")

            let! chosen =
                this.Page.EvaluateAsync<string array>(categoryDisclosureScript)

            let! _ =
                this.Page.ReloadAsync(
                    PageReloadOptions(WaitUntil = WaitUntilState.Load)
                )

            do! this.Page.Locator(".category-entry").Nth(1).WaitForAsync()

            let! afterReload =
                this.Page.EvaluateAsync<string array>(categoryDisclosureScript)
            let! storage =
                this.Page.EvaluateAsync<string array>(
                    """() => {
                        const keys = [...Object.keys(localStorage), ...Object.keys(sessionStorage)];
                        return [
                            String(keys.filter(key => /categor/i.test(key)).length),
                            String(sessionStorage.length)
                        ];
                    }"""
                )

            Assert.Multiple(fun () ->
                Assert.That(chosen, Is.EqualTo([| "Tests|true"; "Docs|false" |]))
                Assert.That(
                    afterReload,
                    Is.EqualTo([| "Tests|false"; "Docs|true" |])
                )
                Assert.That(storage, Is.EqualTo([| "0"; "0" |])))
        }

    [<Test>]
    member this.``a remembered selection expands its collapsed ancestors on load and after refresh``() =
        task {
            // Client is forced collapsed by its parent's size, so the remembered file sits two
            // levels down behind a computed default that no explicit toggle has touched.
            let rememberedPath = "src/Production code-Client-Views/file1.fs"
            let rememberedIdentity = "id-Production code-Client-Views-1"
            let rememberedKey = $"""["modified",null,"{rememberedPath}"]"""

            let files =
                Array.concat
                    [ categoryFiles [ "Production code"; "Client"; "Views" ] 2
                      categoryFiles [ "Production code"; "Client"; "State" ] 1
                      categoryFiles [ "Production code"; "Server" ] 4 ]

            do!
                this.Page.AddInitScriptAsync(
                    $"""localStorage.setItem(
                        'treemon.diff.selection:/e2e-diff-worktree',
                        '{rememberedKey}'
                    );"""
                )
            do! this.RouteSummary(configuredSummaryJson files)
            do! this.RouteFiles()
            do! this.Goto()

            let restoredPanel () =
                this.Page
                    .Locator($".file-entry[data-identity='{rememberedIdentity}'].active")
                    .Locator("xpath=..")
                    .Locator("xpath=..")
                    .Locator("#patch .d2h-wrapper")

            do! restoredPanel().WaitForAsync()

            let! restored =
                this.Page.EvaluateAsync<string array>(categoryDisclosureScript)
            let! visible =
                this.Page.EvaluateAsync<string array>(visibleFileRowsScript)

            do! this.RefreshCategories()
            do! restoredPanel().WaitForAsync()

            let! afterRefresh =
                this.Page.EvaluateAsync<string array>(categoryDisclosureScript)
            let! afterRefreshVisible =
                this.Page.EvaluateAsync<string array>(visibleFileRowsScript)
            let! panelState =
                this.Page.EvaluateAsync<string array>(
                    """() => [
                        document.querySelector('.file-entry.active').dataset.identity,
                        String(document.querySelectorAll('.file-panel').length),
                        String(document.querySelectorAll('#patch').length)
                    ]"""
                )

            let expectedDisclosure =
                [| "Production code|true"
                   "Production code > Client|true"
                   "Production code > Client > Views|true"
                   "Production code > Client > State|true"
                   "Production code > Server|false" |]

            let expectedVisible =
                [| "src/Production code-Client-Views/file0.fs"
                   rememberedPath
                   "src/Production code-Client-State/file0.fs" |]

            Assert.Multiple(fun () ->
                Assert.That(restored, Is.EqualTo(expectedDisclosure))
                Assert.That(visible, Is.EqualTo(expectedVisible))
                Assert.That(afterRefresh, Is.EqualTo(expectedDisclosure))
                Assert.That(afterRefreshVisible, Is.EqualTo(expectedVisible))
                Assert.That(
                    panelState,
                    Is.EqualTo([| rememberedIdentity; "1"; "1" |])
                ))
        }

    [<Test>]
    member this.``collapsing an ancestor clears the selection and ignores a late patch response``() =
        task {
            let fileStarted =
                TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
            let releaseFile =
                TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
            let fileHandlerFinished =
                TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)

            let files =
                Array.concat
                    [ categoryFiles [ "Production code"; "Client" ] 2
                      categoryFiles [ "Production code"; "Server" ] 1
                      categoryFiles [ "Docs" ] 1 ]

            do!
                this.Page.AddInitScriptAsync(
                    """(() => {
                        window.__categoryFileOutcome = null;
                        window.__summaryRequests = 0;
                        const originalFetch = window.fetch;
                        window.fetch = function(input) {
                            const url = typeof input === 'string' ? input : input.url;
                            const request = originalFetch.apply(this, arguments);
                            if (url.includes('diff-summary')) window.__summaryRequests += 1;
                            if (url.includes('diff-file')) {
                                request.then(
                                    () => { window.__categoryFileOutcome = 'completed'; },
                                    error => { window.__categoryFileOutcome = error.name; }
                                );
                            }
                            return request;
                        };
                    })()"""
                )
            do! this.RouteSummary(configuredSummaryJson files)
            // The patch response is held open, so the collapse happens while the request is still
            // in flight and the response is only released afterwards.
            do!
                this.Page.RouteAsync(
                    "**/diff-file?*",
                    fun route ->
                        task {
                            fileStarted.TrySetResult(true) |> ignore
                            let! _ = releaseFile.Task

                            try
                                do!
                                    route.FulfillAsync(
                                        RouteFulfillOptions(
                                            ContentType = "application/json",
                                            Body =
                                                fileResultJson
                                                    "text"
                                                    "id-Production code-Client-0"
                                                    "src/Production code-Client/file0.fs"
                                                    (None: string option)
                                                    "modified"
                                        )
                                    )
                            with _ ->
                                ()

                            fileHandlerFinished.TrySetResult(true) |> ignore
                        }
                        :> Task
                )
            do! this.Goto()
            do! this.ActivateFile("id-Production code-Client-0")
            let! _ = fileStarted.Task.WaitAsync(TimeSpan.FromSeconds(10.0))

            // Collapsing the grandparent, not the leaf that holds the row.
            do! this.ToggleCategory("Production code")
            let! _ =
                this.Page.WaitForFunctionAsync(
                    "() => window.__categoryFileOutcome === 'AbortError'"
                )

            let collapsedState () =
                this.Page.EvaluateAsync<string array>(
                    """() => [
                        String(document.querySelectorAll('.file-entry.active').length),
                        String(document.querySelectorAll('.file-entry[aria-expanded="true"]').length),
                        String(document.querySelectorAll('.file-panel').length),
                        String(document.querySelectorAll('#patch').length),
                        String(state.selected === null),
                        String(state.currentResult === null),
                        String(localStorage.getItem('treemon.diff.selection:/e2e-diff-worktree') === null)
                    ]"""
                )

            let! collapsed = collapsedState ()

            releaseFile.TrySetResult(true) |> ignore
            let! _ = fileHandlerFinished.Task.WaitAsync(TimeSpan.FromSeconds(10.0))
            // A summary round-trip gives the released response every chance to render before the
            // second reading, so "ignored" is proven rather than merely not yet observed.
            do! this.RefreshCategories()
            let! _ =
                this.Page.WaitForFunctionAsync("() => window.__summaryRequests === 2")
            let! afterLateResponse = collapsedState ()

            let! disclosure =
                this.Page.EvaluateAsync<string array>(categoryDisclosureScript)

            let expected =
                [| "0"; "0"; "0"; "0"; "true"; "true"; "true" |]

            Assert.Multiple(fun () ->
                Assert.That(collapsed, Is.EqualTo(expected))
                Assert.That(afterLateResponse, Is.EqualTo(expected))
                Assert.That(
                    disclosure,
                    Is.EqualTo(
                        [| "Production code|false"
                           "Production code > Client|true"
                           "Production code > Server|true"
                           "Docs|true" |]
                    )
                ))
        }

    [<Test>]
    member this.``opening a file in another category keeps a single panel open``() =
        task {
            let files =
                Array.append (categoryFiles [ "Docs" ] 2) (categoryFiles [ "Instructions" ] 1)

            do! this.RouteSummary(configuredSummaryJson files)
            do! this.RouteFiles()
            do! this.Goto()

            let openPatch (identity: string) =
                task {
                    do! this.ActivateFile(identity)
                    do!
                        this.Page
                            .Locator($".file-entry[data-identity='{identity}'].active")
                            .Locator("xpath=..")
                            .Locator("xpath=..")
                            .Locator("#patch .d2h-wrapper")
                            .WaitForAsync()
                }

            do! openPatch "id-Docs-0"
            do! openPatch "id-Instructions-0"

            let! accordion =
                this.Page.EvaluateAsync<string array>(
                    """() => [
                        document.querySelector('.file-entry.active').dataset.identity,
                        String(document.querySelectorAll('.file-entry[aria-expanded="true"]').length),
                        String(document.querySelectorAll('.file-panel').length),
                        String(document.querySelectorAll('#patch').length),
                        document
                            .querySelector('.file-panel')
                            .closest('.category-item')
                            .querySelector('.category-name')
                            .textContent
                    ]"""
                )
            let! disclosure =
                this.Page.EvaluateAsync<string array>(categoryDisclosureScript)

            Assert.Multiple(fun () ->
                Assert.That(
                    accordion,
                    Is.EqualTo([| "id-Instructions-0"; "1"; "1"; "1"; "Instructions" |])
                )
                Assert.That(
                    disclosure,
                    Is.EqualTo([| "Docs|true"; "Instructions|true" |])
                ))
        }

    [<Test>]
    member this.``arrow home and end skip file rows under collapsed categories``() =
        task {
            let files =
                Array.concat
                    [ categoryFiles [ "Docs" ] 2
                      categoryFiles [ "Tests" ] 6
                      categoryFiles [ "Instructions" ] 1 ]

            do! this.RouteSummary(configuredSummaryJson files)
            do! this.RouteFiles()
            do! this.Goto()
            do! this.Page.Locator(".category-entry").Nth(2).WaitForAsync()

            let focusedPath () =
                this.Page.EvaluateAsync<string>(
                    "() => document.activeElement.querySelector('.file-path').textContent"
                )

            let pressFrom (identity: string) (key: string) =
                task {
                    do! this.Page.Locator($".file-entry[data-identity='{identity}']").FocusAsync()
                    do! this.Page.Keyboard.PressAsync(key)
                    return! focusedPath ()
                }

            let press (key: string) =
                task {
                    do! this.Page.Keyboard.PressAsync(key)
                    return! focusedPath ()
                }

            // The six Tests rows sit between Docs and Instructions in document order but are hidden.
            let! down = pressFrom "id-Docs-1" "ArrowDown"
            let! wrapDown = press "ArrowDown"
            let! wrapUp = press "ArrowUp"
            let! home = press "Home"
            let! last = press "End"

            do! this.ToggleCategory("Tests")

            let! revealed = pressFrom "id-Docs-1" "ArrowDown"
            let! revealedHome = press "Home"

            Assert.Multiple(fun () ->
                Assert.That(down, Is.EqualTo("src/Instructions/file0.fs"))
                Assert.That(wrapDown, Is.EqualTo("src/Docs/file0.fs"))
                Assert.That(wrapUp, Is.EqualTo("src/Instructions/file0.fs"))
                Assert.That(home, Is.EqualTo("src/Docs/file0.fs"))
                Assert.That(last, Is.EqualTo("src/Instructions/file0.fs"))
                Assert.That(revealed, Is.EqualTo("src/Tests/file0.fs"))
                Assert.That(revealedHome, Is.EqualTo("src/Docs/file0.fs")))
        }

    [<Test>]
    member this.``the embedded configure action reuses the refresh control's treatment``() =
        task {
            do! this.RouteSummary(readySummaryJson [| firstFile |])
            do! this.GotoEmbedded(pageUrl)

            let frame = this.EmbeddedFrame()
            do!
                frame.Locator(
                    ".file-entry[data-identity='id-1']"
                ).WaitForAsync()

            let action = frame.Locator(configureSelector)
            do! action.WaitForAsync()

            let! treatment =
                action.EvaluateAsync<string array>(
                    """(action, selector) => {
                        const refresh = document.getElementById('refresh');
                        const treatment = element => {
                            const rect = element.getBoundingClientRect();
                            const style = getComputedStyle(element);
                            return [
                                Math.round(rect.width) + 'x' + Math.round(rect.height),
                                style.border,
                                style.borderRadius,
                                style.backgroundColor,
                                style.color,
                                style.opacity
                            ].join('|');
                        };
                        return [
                            treatment(action),
                            treatment(refresh),
                            action.getAttribute('aria-label'),
                            action.getAttribute('title'),
                            action.tagName + ':' + action.type,
                            action.textContent,
                            String(action.querySelectorAll('svg.toolbar-icon').length),
                            String(action.closest('.toolbar') === refresh.closest('.toolbar')),
                            String(action.nextElementSibling === refresh),
                            String(document.querySelectorAll(selector).length)
                        ];
                    }""",
                    configureSelector
                )

            Assert.Multiple(fun () ->
                Assert.That(treatment[0], Is.EqualTo(treatment[1]))
                Assert.That(
                    treatment[2..],
                    Is.EqualTo(
                        [| configureLabel
                           configureLabel
                           "BUTTON:button"
                           ""
                           "1"
                           "true"
                           "true"
                           "1" |]
                    )
                ))
        }

    [<Test>]
    member this.``the configure action is absent top-level and without a canvas transport``() =
        task {
            do! this.RouteSummary(readySummaryJson [| firstFile |])
            do! this.Goto()
            do!
                this.Page.Locator(
                    ".file-entry[data-identity='id-1']"
                ).WaitForAsync()

            let! standalone =
                this.Page.EvaluateAsync<string array>(
                    """selector => [
                        String(document.querySelectorAll(selector).length),
                        String(window.parent === window),
                        typeof window.canvasSend
                    ]""",
                    configureSelector
                )

            // canvas-send.js installs the helper once and bails when this flag is already set, so
            // the embedded document below loads with a genuinely missing transport rather than a
            // stubbed one.
            do!
                this.Page.AddInitScriptAsync(
                    "window.__canvasSendInstalled = true;"
                )
            do! this.GotoEmbedded(pageUrl)
            let frame = this.EmbeddedFrame()
            do!
                frame.Locator(
                    ".file-entry[data-identity='id-1']"
                ).WaitForAsync()

            let! withoutTransport =
                frame.Locator("body").EvaluateAsync<string array>(
                    """(body, selector) => [
                        String(document.querySelectorAll(selector).length),
                        String(window.parent === window),
                        typeof window.canvasSend
                    ]""",
                    configureSelector
                )

            Assert.Multiple(fun () ->
                Assert.That(
                    standalone,
                    Is.EqualTo([| "0"; "true"; "function" |])
                )
                Assert.That(
                    withoutTransport,
                    Is.EqualTo([| "0"; "false"; "undefined" |])
                ))
        }

    [<Test>]
    member this.``activating the configure action posts the fixed request``() =
        task {
            do! this.RouteSummary(readySummaryJson [| firstFile |])
            do! this.GotoEmbedded(pageUrl)

            let action = this.EmbeddedFrame().Locator(configureSelector)
            do! action.WaitForAsync()
            do! this.ActivateConfigure(action)

            let! posted =
                this.Page.EvaluateAsync<string array>(
                    """() => {
                        const message = window.__canvasMessages[0] || {};
                        const request = message.request;
                        const mentions = text => String(String(request).includes(text));
                        return [
                            String(window.__canvasMessages.length),
                            String(message.action),
                            Object.keys(message).sort().join(','),
                            typeof request,
                            String(String(request).trim().length > 0),
                            mentions('.treemon.json'),
                            mentions('"diffCategories"'),
                            mentions('4 levels'),
                            mentions('"Other"'),
                            mentions('"**"')
                        ];
                    }"""
                )

            Assert.That(
                posted,
                Is.EqualTo(
                    [| "1"
                       "configure-diff-categories"
                       "action,request"
                       "string"
                       "true"
                       "true"
                       "true"
                       "true"
                       "true"
                       "true" |]
                )
            )
        }

    [<Test>]
    member this.``two repositories post a byte-identical configure request``() =
        task {
            let otherPageUrl =
                $"{ServerFixture.canvasUrl}/e2e-other-repo-worktree/diff.html"

            do!
                this.Page.RouteAsync(
                    "**/diff-summary?*",
                    fun route ->
                        let body =
                            if
                                route.Request.Url.Contains(
                                    "e2e-other-repo-worktree"
                                )
                            then
                                configuredSummaryJson
                                    [| categorizedFileJson
                                           "id-lexer"
                                           "lib/parser/Lexer.rs"
                                           None
                                           "modified"
                                           [ "Runtime" ] |]
                            else
                                configuredSummaryJson
                                    [| categorizedFileJson
                                           "id-app"
                                           "src/Client/App.fs"
                                           None
                                           "modified"
                                           [ "Production code"; "Client" ] |]

                        route.FulfillAsync(
                            RouteFulfillOptions(
                                ContentType = "application/json",
                                Body = body
                            )
                        )
                )

            let! first = this.PostedConfigureRequest(pageUrl)
            let! second = this.PostedConfigureRequest(otherPageUrl)

            // Every value the browser could possibly derive from the repository it is showing: the
            // worktree keys in the two document URLs, the changed paths, and the category names.
            let derived =
                [| "e2e-diff-worktree"
                   "e2e-other-repo-worktree"
                   "src/Client/App.fs"
                   "lib/parser/Lexer.rs"
                   "Production code"
                   "Runtime"
                   ServerFixture.canvasUrl |]

            Assert.Multiple(fun () ->
                Assert.That(first, Is.Not.Empty)
                Assert.That(second, Is.EqualTo(first))

                derived
                |> Array.iter (fun value ->
                    Assert.That(first, Does.Not.Contain(value))))
        }

    [<Test>]
    member this.``the invalid warning offers the same configure action as the toolbar``() =
        task {
            let reason = "each category needs a name"
            let warningText = $"Diff groups are not applied: {reason}."

            do! this.RouteSummary(invalidSummaryJson reason [| firstFile |])
            do! this.Goto()
            do!
                this.Page.Locator(
                    ".file-entry[data-identity='id-1']"
                ).WaitForAsync()

            let! standaloneWarning =
                this.Page.EvaluateAsync<string array>(
                    """selector => [
                        document.getElementById('category-warning').textContent,
                        String(document.querySelectorAll(selector).length)
                    ]""",
                    configureSelector
                )

            do! this.GotoEmbedded(pageUrl)
            let frame = this.EmbeddedFrame()
            let warningAction =
                frame.Locator($"#category-warning {configureSelector}")
            do! warningAction.WaitForAsync()

            do! this.ActivateConfigure(warningAction)
            do!
                this.ActivateConfigure(
                    frame.Locator($".toolbar {configureSelector}")
                )

            let! payloads =
                this.Page.EvaluateAsync<string array>(
                    "() => window.__canvasMessages.map(message => JSON.stringify(message))"
                )

            let! placement =
                frame.Locator("#category-warning").EvaluateAsync<string array>(
                    """(warning, selector) => [
                        warning.textContent,
                        String(warning.querySelectorAll(selector).length),
                        String(document.querySelectorAll(selector).length)
                    ]""",
                    configureSelector
                )

            Assert.Multiple(fun () ->
                Assert.That(
                    standaloneWarning,
                    Is.EqualTo([| warningText; "0" |])
                )
                Assert.That(
                    placement,
                    Is.EqualTo([| warningText; "1"; "2" |])
                )
                Assert.That(payloads.Length, Is.EqualTo(2))
                Assert.That(payloads[0], Does.Contain("configure-diff-categories"))
                Assert.That(payloads[1], Is.EqualTo(payloads[0])))
        }

    [<Test>]
    member this.``file headers lead with status and render only nonzero line stats``() =
        task {
            let files =
                [| fileJsonWithStats
                       "added-lines"
                       "added-lines.txt"
                       None
                       12
                       0
                       "modified"
                   fileJsonWithStats
                       "removed-lines"
                       "removed-lines.txt"
                       None
                       0
                       7
                       "deleted"
                   fileJson "unavailable" "binary.dat" None "modified" |]

            do! this.RouteHighlighter()
            do! this.RouteSummary(readySummaryJson files)
            do! this.RouteFiles()
            do! this.Goto()
            do! this.Page.Locator(".file-entry").Nth(1).WaitForAsync()

            let! state =
                this.Page.EvaluateAsync<string array>(
                    """() => {
                        const headings = [...document.querySelectorAll('.file-heading')];
                        const entries = [...document.querySelectorAll('.file-entry')];
                        const addedStats = headings[0].querySelector('.file-stats');
                        const removedStats = headings[1].querySelector('.file-stats');
                        const added = addedStats.querySelector('.file-lines-added');
                        const removed = removedStats.querySelector('.file-lines-removed');
                        const path = entries[0].querySelector('.file-path');
                        const badge = entries[0].querySelector('.change-badge');
                        const action = headings[0].querySelector('.file-actions-button');
                        const badgeRect = badge.getBoundingClientRect();
                        const pathRect = path.getBoundingClientRect();
                        const actionRect = action.getBoundingClientRect();
                        return [
                            addedStats.textContent,
                            addedStats.getAttribute('aria-label'),
                            getComputedStyle(added).color,
                            String(addedStats.querySelectorAll('.file-lines-removed').length),
                            removedStats.textContent,
                            removedStats.getAttribute('aria-label'),
                            getComputedStyle(removed).color,
                            String(removedStats.querySelectorAll('.file-lines-added').length),
                            String(path.previousElementSibling.classList.contains('change-badge')),
                            String(action.previousElementSibling === entries[0]),
                            String(addedStats.previousElementSibling === action),
                            String(actionRect.left - pathRect.right >= 2 && actionRect.left - pathRect.right <= 6),
                            String(Math.round(actionRect.width)),
                            String(Math.round(actionRect.height)),
                            getComputedStyle(action).borderRadius,
                            String(Math.round(pathRect.left - badgeRect.right)),
                            String(headings[2].querySelectorAll('.file-stats').length)
                        ];
                    }"""
                )

            Assert.That(
                state,
                Is.EqualTo(
                    [| "+12"
                       "12 lines added"
                       "rgb(166, 227, 161)"
                       "0"
                       "−7"
                       "7 lines removed"
                       "rgb(243, 139, 168)"
                       "0"
                       "true"
                       "true"
                       "true"
                       "true"
                       "18"
                       "18"
                       "50%"
                       "8"
                       "0" |]
                )
            )
        }

    [<Test>]
    member this.``oversized tracked text keeps renderer-neutral line stats``() =
        task {
            let file =
                fileJsonWithStats
                    "large"
                    "large.txt"
                    None
                    24_000
                    3
                    "modified"

            let fileResult =
                JsonSerializer.Serialize(
                    {| status = "oversized"
                       file = file |}
                )

            do! this.RouteSummary(readySummaryJson [| file |])
            do! this.RouteBody("**/diff-file?*", "application/json", fileResult)
            do! this.Goto()
            do! this.ActivateFile("large")
            do! this.Page.Locator("[data-state='oversized']").WaitForAsync()

            let stats = this.Page.Locator(".file-heading .file-stats")
            let! statsCount = stats.CountAsync()
            let! statsText = stats.TextContentAsync()
            let! statsLabel = stats.GetAttributeAsync("aria-label")

            Assert.Multiple(fun () ->
                Assert.That(statsCount, Is.EqualTo(1))
                Assert.That(statsText, Is.EqualTo("+24000−3"))
                Assert.That(
                    statsLabel,
                    Is.EqualTo("24000 lines added, 3 lines removed")
                ))
        }

    [<Test>]
    member this.``layer counts stay independent of selection and zero or failed counts are explicit``() =
        task {
            let summary =
                """{"status":"ready","baseRef":"HEAD","fileCount":1,"files":[{"identity":"id-1","displayPath":"src/a.txt","oldDisplayPath":null,"linesAdded":null,"linesRemoved":null,"change":"modified"}],"layerCounts":{"committed":{"status":"base-error","fileCount":null},"local":{"status":"ready","fileCount":0},"untracked":{"status":"ready","fileCount":4}}}"""

            do! this.RouteHighlighter()
            do! this.RouteSummary(summary)
            do! this.RouteFiles()
            do! this.Goto()
            do!
                this.Page.Locator(
                    ".file-entry[data-identity='id-1']"
                ).WaitForAsync()

            let countState () =
                this.Page.EvaluateAsync<string array>(
                    """() => [
                        document.getElementById('count-committed').textContent,
                        document.getElementById('count-local').textContent,
                        document.getElementById('count-untracked').textContent,
                        String(document.getElementById('filter-committed').disabled),
                        String(document.getElementById('filter-local').disabled),
                        String(document.getElementById('filter-untracked').disabled),
                        document.getElementById('count-committed').title
                    ]"""
                )

            let! initial = countState ()
            do! this.Page.Locator("#filter-untracked").CheckAsync()
            do!
                this.Page.Locator(
                    ".file-entry[data-identity='id-1']"
                ).WaitForAsync()
            let! afterSelectionChange = countState ()

            let expected =
                [| "(unavailable)"
                   "(0)"
                   "(4)"
                   "false"
                   "true"
                   "false"
                   "File count unavailable because the comparison base could not be resolved." |]

            Assert.Multiple(fun () ->
                Assert.That(initial, Is.EqualTo(expected))
                Assert.That(afterSelectionChange, Is.EqualTo(expected)))
        }

    [<Test>]
    member this.``viewer chrome is border-light and code gutters use identical typography``() =
        task {
            do! this.RouteHighlighter()
            do! this.RouteSummary(readySummaryJson [| firstFile |])
            do! this.RouteFiles()
            do! this.Goto()
            do! this.ActivateFile("id-1")
            do! this.Page.Locator("#patch .d2h-wrapper").WaitForAsync()

            let! unified =
                this.Page.EvaluateAsync<string array>(
                    """() => {
                        const style = selector => getComputedStyle(document.querySelector(selector));
                        const code = style('#patch td.d2h-cntx .d2h-code-line');
                        const number = style('#patch td.d2h-code-linenumber:not(.d2h-info)');
                        const cell = getComputedStyle(document.querySelector('#patch .d2h-code-line').closest('td'));
                        const unifiedButton = style('#unified-view');
                        const refreshButton = style('#refresh');
                        return [
                            style('.file-entry').borderTopWidth,
                            style('.file-item').borderBottomWidth,
                            style('.file-panel').borderTopWidth,
                            style('#patch .d2h-file-wrapper').borderTopWidth,
                            style('#patch .d2h-file-header').display,
                            code.fontSize,
                            number.fontSize,
                            code.lineHeight,
                            number.lineHeight,
                            unifiedButton.width,
                            refreshButton.width,
                            unifiedButton.borderTopColor,
                            refreshButton.borderTopColor,
                            unifiedButton.backgroundColor,
                            style('.view-toggle').borderTopWidth,
                            style('body').fontSize,
                            style('.title strong').fontSize,
                            style('.title span').fontSize,
                            style('.layer-filter').fontSize,
                            style('.layer-count').fontSize,
                            style('.change-summary').fontSize,
                            style('.file-path').fontSize,
                            cell.borderTopWidth,
                            cell.borderRightWidth,
                            cell.borderBottomWidth,
                            cell.borderLeftWidth,
                            cell.paddingTop,
                            cell.paddingRight,
                            cell.paddingBottom,
                            cell.paddingLeft,
                            number.borderRightWidth,
                            number.color,
                            code.color,
                            style('.file-list').paddingLeft,
                            style('.file-entry').paddingLeft
                        ];
                    }"""
                )

            do! this.Page.Locator("#split-view").ClickAsync()
            do! this.Page.Locator("#patch .d2h-files-diff").WaitForAsync()

            let! splitTypography =
                this.Page.EvaluateAsync<string array>(
                    """() => {
                        const code = getComputedStyle(document.querySelector('#patch td.d2h-cntx .d2h-code-side-line'));
                        const number = getComputedStyle(document.querySelector('#patch td.d2h-code-side-linenumber:not(.d2h-info)'));
                        const right = getComputedStyle(document.querySelectorAll('#patch .d2h-file-side-diff')[1]);
                        return [code.fontSize, number.fontSize, code.lineHeight, number.lineHeight, right.borderLeftWidth];
                    }"""
                )

            Assert.Multiple(fun () ->
                Assert.That(unified[0..4], Is.EqualTo([| "0px"; "1px"; "0px"; "0px"; "none" |]))
                Assert.That(unified[5], Is.EqualTo(unified[6]))
                Assert.That(unified[7], Is.EqualTo(unified[8]))
                Assert.That(unified[9], Is.EqualTo(unified[10]))
                Assert.That(unified[11], Is.EqualTo("rgb(137, 180, 250)"))
                Assert.That(unified[12], Is.EqualTo("rgb(69, 71, 90)"))
                Assert.That(unified[13], Is.EqualTo("rgb(46, 52, 82)"))
                Assert.That(unified[14], Is.EqualTo("0px"))
                Assert.That(unified[15..21], Is.EqualTo([| "13px"; "13px"; "13px"; "13px"; "11px"; "13px"; "13px" |]))
                Assert.That(unified[22..29], Is.All.EqualTo("0px"))
                Assert.That(unified[30], Is.EqualTo("1px"))
                Assert.That(unified[31], Is.EqualTo("rgb(108, 112, 134)"))
                Assert.That(unified[32], Is.Not.EqualTo(unified[31]))
                Assert.That(unified[33], Is.EqualTo("0px"))
                Assert.That(unified[34], Is.EqualTo("12px"))
                Assert.That(splitTypography[0], Is.EqualTo(splitTypography[1]))
                Assert.That(splitTypography[2], Is.EqualTo(splitTypography[3]))
                Assert.That(splitTypography[4], Is.EqualTo("1px")))
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

            let lastHeader = this.Page.Locator(".file-entry[data-identity='id-scroll-24']")
            do! lastHeader.WaitForAsync()

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
    member this.``explicit selection restores across refresh filter and reload with new identities``() =
        task {
            let filteredSecondFile =
                fileJson
                    "id-2-filtered"
                    secondFile.displayPath
                    secondFile.oldDisplayPath
                    secondFile.change

            let reloadedSecondFile =
                fileJson
                    "id-2-reloaded"
                    secondFile.displayPath
                    secondFile.oldDisplayPath
                    secondFile.change

            let summaries =
                [| readySummaryJson [| firstFile; secondFile |]
                   readySummaryJson [| refreshedFirstFile; refreshedSecondFile |]
                   readySummaryJson [| refreshedFirstFile; filteredSecondFile |]
                   readySummaryJson [| refreshedFirstFile; reloadedSecondFile |] |]
            // The route callback owns the sequence of refreshed identity snapshots.
            let mutable summaryIndex = 0
            let fileRequests =
                System.Collections.Concurrent.ConcurrentQueue<string>()

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
            do!
                this.Page.RouteAsync(
                    "**/diff-file?*",
                    fun route ->
                        let uri = Uri(route.Request.Url)
                        let identity =
                            Uri.UnescapeDataString(uri.Query.Substring("?identity=".Length))
                        fileRequests.Enqueue(identity)
                        route.FulfillAsync(
                            RouteFulfillOptions(
                                ContentType = "application/json",
                                Body =
                                    fileResultJson
                                        "text"
                                        identity
                                        secondFile.displayPath
                                        secondFile.oldDisplayPath
                                        secondFile.change
                            )
                        )
                )
            do! this.Goto()

            do! this.Page.Locator(".file-entry[data-identity='id-2']").ClickAsync()
            do! this.Page.Locator("#patch .d2h-wrapper").WaitForAsync()
            do! this.Page.Locator("#refresh").ClickAsync()
            do!
                this.Page.Locator(
                    ".file-entry[data-identity='id-2-refreshed'].active"
                ).Locator("xpath=..").Locator("xpath=..").Locator("#patch .d2h-wrapper").WaitForAsync()

            do! this.Page.Locator("#filter-untracked").CheckAsync()
            do!
                this.Page.Locator(
                    ".file-entry[data-identity='id-2-filtered'].active"
                ).Locator("xpath=..").Locator("xpath=..").Locator("#patch .d2h-wrapper").WaitForAsync()

            let! _ = this.Page.ReloadAsync()
            do!
                this.Page.Locator(
                    ".file-entry[data-identity='id-2-reloaded'].active"
                ).Locator("xpath=..").Locator("xpath=..").Locator("#patch .d2h-wrapper").WaitForAsync()

            let! restored =
                this.Page.EvaluateAsync<string array>(
                    """() => [
                        document.querySelector('.file-entry.active').dataset.identity,
                        state.currentResult.file.identity,
                        String(document.querySelectorAll('.file-entry[aria-expanded="true"]').length),
                        String(document.querySelectorAll('.file-panel').length),
                        String(document.querySelectorAll('#patch').length),
                        localStorage.getItem('treemon.diff.selection:/e2e-diff-worktree')
                    ]"""
                )

            Assert.Multiple(fun () ->
                Assert.That(
                    fileRequests.ToArray(),
                    Is.EqualTo(
                        [| "id-2"
                           "id-2-refreshed"
                           "id-2-filtered"
                           "id-2-reloaded" |]
                    )
                )
                Assert.That(
                    restored,
                    Is.EqualTo(
                        [| "id-2-reloaded"
                           "id-2-reloaded"
                           "1"
                           "1"
                           "1"
                           """["renamed","src/old-name.txt","src/new-name.txt"]""" |]
                    )
                ))
        }

    [<Test>]
    member this.``stale or invalid remembered selection stays collapsed without fallback``() =
        task {
            do!
                this.Page.AddInitScriptAsync(
                    """(() => {
                        const marker = 'treemon.diff.selection-test-seeded';
                        if (!localStorage.getItem(marker)) {
                            localStorage.setItem(
                                'treemon.diff.selection:/e2e-diff-worktree',
                                JSON.stringify(['renamed', 'src/old-name.txt', 'src/new-name.txt'])
                            );
                            localStorage.setItem(marker, 'true');
                        }
                        window.__diffFileRequests = 0;
                        const originalFetch = window.fetch;
                        window.fetch = function(input) {
                            const url = typeof input === 'string' ? input : input.url;
                            if (url.includes('diff-file')) window.__diffFileRequests += 1;
                            return originalFetch.apply(this, arguments);
                        };
                    })()"""
                )
            do! this.RouteSummary(readySummaryJson [| firstFile |])
            do! this.RouteFiles()
            do! this.Goto()
            do!
                this.Page.Locator(
                    ".file-entry[data-identity='id-1']"
                ).WaitForAsync()

            let collapsedState () =
                this.Page.EvaluateAsync<string array>(
                    """() => [
                        String(document.querySelectorAll('.file-entry.active').length),
                        String(document.querySelectorAll('.file-entry[aria-expanded="true"]').length),
                        String(document.querySelectorAll('.file-panel').length),
                        String(document.querySelectorAll('#patch').length),
                        String(state.selected === null),
                        String(window.__diffFileRequests),
                        localStorage.getItem('treemon.diff.selection:/e2e-diff-worktree')
                    ]"""
                )

            let! stale = collapsedState ()

            let! _ =
                this.Page.EvaluateAsync<obj>(
                    """() => localStorage.setItem(
                        'treemon.diff.selection:/e2e-diff-worktree',
                        'not-json'
                    )"""
                )
            let! _ = this.Page.ReloadAsync()
            do!
                this.Page.Locator(
                    ".file-entry[data-identity='id-1']"
                ).WaitForAsync()
            let! invalid = collapsedState ()

            Assert.Multiple(fun () ->
                Assert.That(
                    stale,
                    Is.EqualTo(
                        [| "0"
                           "0"
                           "0"
                           "0"
                           "true"
                           "0"
                           """["renamed","src/old-name.txt","src/new-name.txt"]""" |]
                    )
                )
                Assert.That(
                    invalid,
                    Is.EqualTo([| "0"; "0"; "0"; "0"; "true"; "0"; "not-json" |])
                ))
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
            do! this.ActivateFile("id-1")
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

    [<Test>]
    member this.``collapsing the active file clears memory and later summaries stay collapsed``() =
        task {
            let fileStarted =
                TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
            let releaseFile =
                TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously)
            let fileRequests =
                System.Collections.Concurrent.ConcurrentQueue<unit>()

            do!
                this.Page.AddInitScriptAsync(
                    """(() => {
                        window.__collapsedFileOutcome = null;
                        window.__summaryRequests = 0;
                        const originalFetch = window.fetch;
                        window.fetch = function(input) {
                            const url = typeof input === 'string' ? input : input.url;
                            const request = originalFetch.apply(this, arguments);
                            if (url.includes('diff-summary')) window.__summaryRequests += 1;
                            if (url.includes('diff-file?identity=id-1')) {
                                request.then(
                                    () => { window.__collapsedFileOutcome = 'completed'; },
                                    error => { window.__collapsedFileOutcome = error.name; }
                                );
                            }
                            return request;
                        };
                    })()"""
                )
            do! this.RouteSummary(readySummaryJson [| firstFile |])
            do!
                this.Page.RouteAsync(
                    "**/diff-file?*",
                    fun route ->
                        task {
                            fileRequests.Enqueue(())
                            fileStarted.TrySetResult(true) |> ignore
                            let! _ = releaseFile.Task
                            try
                                do!
                                    route.FulfillAsync(
                                        RouteFulfillOptions(
                                            ContentType = "application/json",
                                            Body =
                                                fileResultJson
                                                    "text"
                                                    firstFile.identity
                                                    firstFile.displayPath
                                                    firstFile.oldDisplayPath
                                                    firstFile.change
                                        )
                                    )
                            with _ ->
                                ()
                        }
                        :> Task
                )

            do! this.Goto()
            do! this.ActivateFile("id-1")
            let! _ = fileStarted.Task.WaitAsync(TimeSpan.FromSeconds(10.0))
            do! this.Page.Locator(".file-entry[data-identity='id-1']").ClickAsync()
            let! _ =
                this.Page.WaitForFunctionAsync(
                    "() => window.__collapsedFileOutcome === 'AbortError'"
                )

            let collapsedState () =
                this.Page.EvaluateAsync<string array>(
                    """() => [
                        String(document.querySelectorAll('.file-entry.active').length),
                        String(document.querySelectorAll('.file-entry[aria-expanded="true"]').length),
                        String(document.querySelectorAll('.file-panel').length),
                        String(document.querySelectorAll('[data-state="loading-file"]').length),
                        String(state.selected === null),
                        String(state.currentResult === null),
                        String(localStorage.getItem('treemon.diff.selection:/e2e-diff-worktree') === null)
                    ]"""
                )

            let! collapsed = collapsedState ()
            releaseFile.TrySetResult(true) |> ignore
            do! this.Page.Locator("#refresh").ClickAsync()
            let! _ =
                this.Page.WaitForFunctionAsync(
                    "() => window.__summaryRequests === 2 && document.querySelectorAll('.file-entry').length === 1"
                )
            let! refreshed = collapsedState ()

            let! _ = this.Page.ReloadAsync()
            let! _ =
                this.Page.WaitForFunctionAsync(
                    "() => window.__summaryRequests === 1 && document.querySelectorAll('.file-entry').length === 1"
                )
            let! reopened = collapsedState ()

            let expected =
                [| "0"; "0"; "0"; "0"; "true"; "true"; "true" |]

            Assert.Multiple(fun () ->
                Assert.That(collapsed, Is.EqualTo(expected))
                Assert.That(refreshed, Is.EqualTo(expected))
                Assert.That(reopened, Is.EqualTo(expected))
                Assert.That(fileRequests.Count, Is.EqualTo(1)))
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
            do! this.ActivateFile("id-1")

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

    [<TestCase(
        "binary",
        "Binary replacement",
        "The tracked deletion is shown above. Binary replacement content is not rendered."
    )>]
    [<TestCase(
        "symlink",
        "Symbolic link replacement",
        "The tracked deletion is shown above. The replacement link target is unavailable."
    )>]
    member this.``composed replacement renders the tracked patch and special marker``(
        replacement: string,
        expectedTitle: string,
        expectedDetail: string
    ) =
        task {
            do! this.RouteHighlighter()
            do! this.RouteSummary(readySummaryJson [| firstFile |])
            do!
                this.RouteBody(
                    "**/diff-file?*",
                    "application/json",
                    replacementResultJson replacement
                )
            do! this.Goto()
            do! this.ActivateFile("id-1")

            let marker =
                this.Page.Locator(
                    $"#patch .replacement-marker[data-state='{replacement}-replacement']"
                )

            do! this.Page.Locator("#patch .d2h-wrapper").WaitForAsync()
            do! marker.WaitForAsync()

            let! initial =
                this.Page.EvaluateAsync<string array>(
                    """() => {
                        const marker = document.querySelector('#patch .replacement-marker');
                        const wrapper = document.querySelector('#patch .d2h-wrapper');
                        return [
                            document.querySelector('#patch .d2h-file-name').textContent,
                            marker.getAttribute('aria-label'),
                            marker.querySelector('.replacement-detail').textContent,
                            String(Boolean(wrapper.compareDocumentPosition(marker) & Node.DOCUMENT_POSITION_FOLLOWING)),
                            state.currentResult.status,
                            state.currentResult.replacement
                        ];
                    }"""
                )

            do! this.Page.Locator("#split-view").ClickAsync()
            do! this.Page.Locator("#patch .d2h-files-diff").WaitForAsync()
            let! markerCount =
                this.Page.Locator(
                    $"#patch .replacement-marker[data-state='{replacement}-replacement']"
                ).CountAsync()

            Assert.Multiple(fun () ->
                Assert.That(
                    initial,
                    Is.EqualTo(
                        [| "src/a.txt"
                           expectedTitle
                           expectedDetail
                           "true"
                           "replacement"
                           replacement |]
                    )
                )
                Assert.That(markerCount, Is.EqualTo(1)))
        }

    [<Test>]
    member this.``unsupported and over-limit states never reach diff2html``() =
        task {
            do! this.RouteHighlighter()
            do! this.RouteSummary(readySummaryJson [| firstFile |])
            do! this.RouteFiles()
            do! this.Goto()
            do! this.ActivateFile("id-1")
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
            do! this.ActivateFile("id-1")

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
            do! this.ActivateFile("id-1")
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
            do! this.ActivateFile("id-1")
            do!
                this.Page.Locator("#patch[data-highlight-status='plain'] .d2h-file-diff").WaitForAsync(
                    LocatorWaitForOptions(Timeout = 15000.0f)
                )

            let geometry mode =
                geometryProbe this.Page mode true false
                    [| geometryRow -1 "tr[data-old-line='1'][data-new-line='1']" "unified context"
                       geometryRow -1 "tr[data-old-line='2']:not([data-new-line])" "unified deletion"
                       geometryRow -1 "tr[data-new-line='2']:not([data-old-line])" "unified addition"
                       geometryRow -1 "tr[data-old-line='10'][data-new-line='10']" "unified hunk context" |]
                    [| geometryRow 0 "tr[data-old-line='2']:not([data-new-line])" "split deletion"
                       geometryRow 1 "tr[data-new-line='2']:not([data-old-line])" "split addition"
                       geometryRow 0 "tr[data-old-line='10'][data-new-line='10']" "split hunk context left"
                       geometryRow 1 "tr[data-old-line='10'][data-new-line='10']" "split hunk context right" |]

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

            let! (_, unifiedFailures) = geometry "unified"
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

            let! (_, splitFailures) = geometry "split"
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
    member this.``exact old 41 new 42 fixture wraps without overflow at desktop and narrow widths``() =
        task {
            let apiPort = Uri(ServerFixture.serverUrl).Port
            let canvasPort = Uri(ServerFixture.canvasUrl).Port
            let vitePort = Uri(ServerFixture.viteUrl).Port
            TestContext.Out.WriteLine(
                $"SIDE_BY_SIDE_STANDALONE dashboard={ServerFixture.viteUrl} api={ServerFixture.serverUrl} canvas={ServerFixture.canvasUrl} document={pageUrl}"
            )
            Assert.Multiple(fun () ->
                Assert.That(apiPort, Is.Not.EqualTo(5000))
                Assert.That(canvasPort, Is.Not.EqualTo(5002))
                Assert.That(vitePort, Is.Not.EqualTo(5000)))

            let browserErrors =
                System.Collections.Concurrent.ConcurrentQueue<string>()

            this.Page.Console.Add(fun message ->
                if message.Type = "error" then
                    browserErrors.Enqueue($"console: {message.Text}"))

            this.Page.PageError.Add(fun error ->
                browserErrors.Enqueue($"pageerror: {error}"))

            do! this.Page.SetViewportSizeAsync(1280, 900)
            do! this.RouteHighlighter()
            do! this.RouteSummary(readySummaryJson [| firstFile |])
            do! this.RoutePatch(exactLinePatch)
            do! this.Goto()
            do! this.ActivateFile("id-1")
            do!
                this.Page.Locator(
                    "#patch[data-highlight-status='plain'] .d2h-file-diff"
                ).WaitForAsync(LocatorWaitForOptions(Timeout = 15000.0f))

            let settleLayout () =
                this.Page.EvaluateAsync(
                    "() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(resolve)))"
                )

            let sourceContext selector =
                this.Page.Locator(selector).EvaluateAsync<string>(
                    """element => {
                        const range = document.createRange();
                        range.selectNodeContents(element);
                        return JSON.stringify(window.canvasSelectionMetadata({ range }));
                    }"""
                )

            let geometry mode =
                geometryProbe this.Page mode false true
                    [| geometryRow -1 "tr[data-old-line='41']:not([data-new-line])" "unified deletion old 41"
                       geometryRow -1 "tr[data-new-line='42']:not([data-old-line])" "unified addition new 42"
                       geometryRow -1 "tr[data-old-line='42'][data-new-line='43']" "unified following context" |]
                    [| geometryRow 0 "tr[data-old-line='41']:not([data-new-line])" "split deletion old 41"
                       geometryRow 1 "tr[data-new-line='42']:not([data-old-line])" "split addition new 42"
                       geometryRow 1 "tr[data-old-line='42'][data-new-line='43']" "split following context" |]

            let expected oldRange newRange =
                $"""{{"kind":"diff","fileIdentity":"id-1","displayPath":"src/a.txt","oldDisplayPath":null,"hunkHeader":"@@ -40,4 +40,5 @@","oldLineRange":{oldRange},"newLineRange":{newRange}}}"""

            let assertGeometry (width: int) (mode: string) ((raw, failures): string * string array) =
                TestContext.Out.WriteLine(
                    $"DIFF_EXACT_GEOMETRY width={width} mode={mode} evidence={raw}"
                )
                Assert.That(failures, Is.Empty)

            let captureScreenshot name =
                task {
                    let path = verificationArtifactPath name
                    Path.GetDirectoryName(path)
                    |> Directory.CreateDirectory
                    |> ignore

                    let! _ =
                        this.Page.Locator("#patch").ScreenshotAsync(
                            LocatorScreenshotOptions(Path = path)
                        )

                    ()
                }

            for width in [ 1280; 360 ] do
                do! this.Page.SetViewportSizeAsync(width, 900)
                do! this.Page.Locator("#unified-view").ClickAsync()
                do! this.Page.Locator("#patch .d2h-file-diff").WaitForAsync()
                let! _ = settleLayout ()
                let! unifiedGeometry = geometry "unified"
                let! unifiedDeletion =
                    sourceContext
                        "tr[data-old-line='41']:not([data-new-line]) .d2h-code-line-ctn"
                let! unifiedAddition =
                    sourceContext
                        "tr[data-new-line='42']:not([data-old-line]) .d2h-code-line-ctn"
                let! unifiedFollowing =
                    sourceContext
                        "tr[data-old-line='42'][data-new-line='43'] .d2h-code-line-ctn"

                assertGeometry width "unified" unifiedGeometry
                Assert.Multiple(fun () ->
                    Assert.That(
                        unifiedDeletion,
                        Is.EqualTo(expected """{"start":41,"end":41}""" "null")
                    )
                    Assert.That(
                        unifiedAddition,
                        Is.EqualTo(expected "null" """{"start":42,"end":42}""")
                    )
                    Assert.That(
                        unifiedFollowing,
                        Is.EqualTo(
                            expected
                                """{"start":42,"end":42}"""
                                """{"start":43,"end":43}"""
                        )
                    ))
                TestContext.Out.WriteLine(
                    $"DIFF_EXACT_SOURCE_CONTEXT width={width} mode=unified deletion={unifiedDeletion} addition={unifiedAddition} following={unifiedFollowing}"
                )

                if width = 1280 then
                    do!
                        captureScreenshot
                            "tm-diff-viewer-ain-unified.png"

                do! this.Page.Locator("#split-view").ClickAsync()
                do! this.Page.Locator("#patch .d2h-files-diff").WaitForAsync()
                let! _ = settleLayout ()
                let! splitGeometry = geometry "split"
                let! splitDeletion =
                    sourceContext
                        ".d2h-file-side-diff:first-child tr[data-old-line='41']:not([data-new-line]) .d2h-code-line-ctn"
                let! splitAddition =
                    sourceContext
                        ".d2h-file-side-diff:last-child tr[data-new-line='42']:not([data-old-line]) .d2h-code-line-ctn"
                let! splitFollowing =
                    sourceContext
                        ".d2h-file-side-diff:last-child tr[data-old-line='42'][data-new-line='43'] .d2h-code-line-ctn"

                assertGeometry width "split" splitGeometry
                Assert.Multiple(fun () ->
                    Assert.That(
                        splitDeletion,
                        Is.EqualTo(expected """{"start":41,"end":41}""" "null")
                    )
                    Assert.That(
                        splitAddition,
                        Is.EqualTo(expected "null" """{"start":42,"end":42}""")
                    )
                    Assert.That(
                        splitFollowing,
                        Is.EqualTo(
                            expected
                                """{"start":42,"end":42}"""
                                """{"start":43,"end":43}"""
                        )
                    ))
                TestContext.Out.WriteLine(
                    $"DIFF_EXACT_SOURCE_CONTEXT width={width} mode=split deletion={splitDeletion} addition={splitAddition} following={splitFollowing}"
                )

                if width = 1280 then
                    do!
                        captureScreenshot
                            "tm-diff-viewer-ain-split.png"

            let browserErrorEvidence = browserErrors.ToArray()
            TestContext.Out.WriteLine(
                $"Standalone browser errors:{Environment.NewLine}{JsonSerializer.Serialize(browserErrorEvidence)}"
            )
            Assert.That(browserErrorEvidence, Is.Empty)
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
            do! this.ActivateFile("id-js")
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
            do! this.ActivateFile("id-js")
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
            do! this.ActivateFile("id-1")
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

[<TestFixture>]
[<Category("E2E")>]
[<Category("Canvas")>]
[<NonParallelizable>]
type DiffSummaryPerformanceE2ETests() =
    inherit PageTest()

    [<Test>]
    member this.``warm 250-path summary appears within five seconds``() =
        task {
            let tempDir =
                Path.Combine(
                    Path.GetTempPath(),
                    $"treemon-diff-summary-performance-{Guid.NewGuid():N}"
                )

            let repoDir = Path.Combine(tempDir, "repo")
            Directory.CreateDirectory(tempDir) |> ignore

            try
                createSummaryPerformanceRepo repoDir

                let port = TestUtils.getFreeTcpPort ()
                let agent = performanceAgentKnowing repoDir

                use host =
                    CanvasDocServer.createHost
                        agent
                        WorktreeDiffApi.liveService
                        WorktreeDiffApi.newOpaqueIdentity
                        port

                host.StartAsync(System.Threading.CancellationToken.None)
                    .GetAwaiter()
                    .GetResult()

                try
                    let baseUrl = $"http://127.0.0.1:{port}"
                    let summaryUrl = performanceSummaryUrl baseUrl repoDir

                    use warmClient = new HttpClient()
                    warmClient.DefaultRequestHeaders.Add(
                        WorktreeDiffApi.viewerHeaderName,
                        Guid.NewGuid().ToString("D")
                    )

                    let warmStopwatch = Stopwatch.StartNew()
                    let! warmResponse = warmClient.GetAsync(summaryUrl)
                    let! warmBody = warmResponse.Content.ReadAsStringAsync()
                    warmStopwatch.Stop()

                    let warmPathCount, warmUntrackedCount, warmStatsCount =
                        summaryPathCounts warmBody

                    Assert.Multiple(fun () ->
                        Assert.That(int warmResponse.StatusCode, Is.EqualTo(200))
                        Assert.That(warmPathCount, Is.EqualTo(250))
                        Assert.That(warmUntrackedCount, Is.EqualTo(25))
                        Assert.That(warmStatsCount, Is.EqualTo(250)))

                    do!
                        this.Page.AddInitScriptAsync(
                            """(() => {
                                const filterKey =
                                    'treemon.diff.layers:' +
                                    location.pathname.replace(/\/diff\.html$/, '');
                                localStorage.setItem(
                                    filterKey,
                                    JSON.stringify({
                                        committed: true,
                                        local: true,
                                        untracked: true
                                    })
                                );

                                window.__summaryPerformance = {};
                                const originalFetch = window.fetch.bind(window);
                                window.fetch = async function(input, options) {
                                    const url =
                                        typeof input === 'string' ? input : input.url;
                                    if (!url.includes('diff-summary')) {
                                        return originalFetch(input, options);
                                    }

                                    const started = performance.now();
                                    window.__summaryPerformance = { started };
                                    const response = await originalFetch(input, options);
                                    const readJson = response.json.bind(response);
                                    response.json = async function() {
                                        const body = await readJson();
                                        window.__summaryPerformance.responseMs =
                                            performance.now() - started;
                                        return body;
                                    };
                                    return response;
                                };

                                const observeSummary = () => {
                                    const list = document.getElementById('file-list');
                                    const capture = () => {
                                        const pathCount =
                                            list.querySelectorAll('.file-entry').length;
                                        const untrackedCount =
                                            list.querySelectorAll(
                                                '.change-badge.untracked'
                                            ).length;
                                        const statsCount =
                                            list.querySelectorAll('.file-stats').length;
                                        const timing = window.__summaryPerformance;
                                        if (
                                            pathCount === 250 &&
                                            untrackedCount === 25 &&
                                            statsCount === 250 &&
                                            timing.started !== undefined &&
                                            !timing.displayScheduled
                                        ) {
                                            timing.displayScheduled = true;
                                            requestAnimationFrame(() =>
                                                requestAnimationFrame(() => {
                                                    timing.pathCount = pathCount;
                                                    timing.untrackedCount =
                                                        untrackedCount;
                                                    timing.statsCount = statsCount;
                                                    timing.displayMs =
                                                        performance.now() -
                                                        timing.started;
                                                })
                                            );
                                        }
                                    };

                                    new MutationObserver(capture).observe(list, {
                                        childList: true,
                                        subtree: true
                                    });
                                    capture();
                                };

                                if (document.readyState === 'loading') {
                                    document.addEventListener(
                                        'DOMContentLoaded',
                                        observeSummary,
                                        { once: true }
                                    );
                                } else {
                                    observeSummary();
                                }
                            })()"""
                        )

                    let documentUrl =
                        summaryUrl.Replace(
                            "diff-summary?committed=true&local=true&untracked=true",
                            "diff.html"
                        )

                    let! _ =
                        this.Page.GotoAsync(
                            documentUrl,
                            PageGotoOptions(WaitUntil = WaitUntilState.Load)
                        )

                    let! _ =
                        this.Page.WaitForFunctionAsync(
                            "() => window.__summaryPerformance.displayMs !== undefined",
                            null,
                            PageWaitForFunctionOptions(Timeout = 5000.0f)
                        )

                    let! rawTiming =
                        this.Page.EvaluateAsync<string>(
                            "() => JSON.stringify(window.__summaryPerformance)"
                        )

                    use timing = JsonDocument.Parse(rawTiming)
                    let root = timing.RootElement
                    let pathCount = root.GetProperty("pathCount").GetInt32()
                    let untrackedCount =
                        root.GetProperty("untrackedCount").GetInt32()
                    let statsCount = root.GetProperty("statsCount").GetInt32()
                    let responseMs =
                        root.GetProperty("responseMs").GetDouble()
                    let displayMs =
                        root.GetProperty("displayMs").GetDouble()

                    TestContext.Out.WriteLine(
                        $"DIFF_SUMMARY_PERFORMANCE pathCount={pathCount} trackedCount={pathCount - untrackedCount} untrackedCount={untrackedCount} statsCount={statsCount} warmResponseMs={warmStopwatch.Elapsed.TotalMilliseconds:F3} responseMs={responseMs:F3} displayMs={displayMs:F3}"
                    )

                    Assert.Multiple(fun () ->
                        Assert.That(pathCount, Is.EqualTo(250))
                        Assert.That(untrackedCount, Is.EqualTo(25))
                        Assert.That(statsCount, Is.EqualTo(250))
                        Assert.That(
                            responseMs,
                            Is.LessThan(5000.0),
                            $"Warm summary response took {responseMs:F3} ms"
                        )
                        Assert.That(
                            displayMs,
                            Is.LessThan(5000.0),
                            $"Warm summary display took {displayMs:F3} ms"
                        ))
                finally
                    host.StopAsync(System.Threading.CancellationToken.None)
                        .GetAwaiter()
                        .GetResult()
            finally
                if Directory.Exists(tempDir) then
                    try
                        Directory.Delete(tempDir, recursive = true)
                    with _ ->
                        ()
        }
