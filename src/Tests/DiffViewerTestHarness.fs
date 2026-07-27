/// Scaffolding shared by the diff viewer E2E fixtures: the served template and pinned assets, the
/// JSON payload builders the routed responses are made of, and a `PageTest` base type that carries
/// the asset routing every diff document needs before it can render.
module Tests.DiffViewerTestHarness

open System
open System.IO
open System.Text.Json
open System.Threading.Tasks
open Microsoft.Playwright
open Microsoft.Playwright.NUnit
open NUnit.Framework
open Shared
open Server

let serverPath name =
    Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", "Server", name))

let assetPath name =
    serverPath (Path.Combine("Assets", "diff2html", DiffAssets.Version, name))

let viewerAssetPath name =
    serverPath (Path.Combine("Assets", "diff", name))

let templatePath = serverPath "DiffTemplate.html"

let samplePatch =
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

let categorizedFileJson identity displayPath oldDisplayPath change categoryPath =
    {| identity = identity
       displayPath = displayPath
       oldDisplayPath = oldDisplayPath
       linesAdded = (None: int option)
       linesRemoved = (None: int option)
       change = change
       categoryPath = (categoryPath: string list) |}

let fileJson identity displayPath oldDisplayPath change =
    categorizedFileJson identity displayPath oldDisplayPath change []

let firstFile =
    fileJson "id-1" "src/a.txt" None "modified"

let secondFile =
    fileJson "id-2" "src/new-name.txt" (Some "src/old-name.txt") "renamed"

let readyLayerCounts committed local untracked =
    {| committed =
        {| status = "ready"
           fileCount = committed |}
       local =
        {| status = "ready"
           fileCount = local |}
       untracked =
        {| status = "ready"
           fileCount = untracked |} |}

let categorizationJson status reason =
    {| status = status
       reason = (reason: string option) |}

let summaryJsonWithCategorization categorization committed local untracked files =
    JsonSerializer.Serialize(
        {| status = "ready"
           baseRef = "origin/main"
           fileCount = Array.length files
           files = files
           categorization = categorization
           layerCounts = readyLayerCounts committed local untracked |}
    )

let readySummaryJsonWithCounts committed local untracked files =
    summaryJsonWithCategorization
        (categorizationJson "missing" None)
        committed
        local
        untracked
        files

let readySummaryJson files =
    readySummaryJsonWithCounts 2 3 1 files

let configuredSummaryJson files =
    summaryJsonWithCategorization (categorizationJson "configured" None) 2 3 1 files

let invalidSummaryJson reason files =
    summaryJsonWithCategorization
        (categorizationJson "invalid" (Some reason))
        2
        3
        1
        files

/// Builds `count` changed files that all classify into `path`; an empty path leaves them unmatched
/// so they collect in the synthetic trailing Other group.
let categoryFiles (path: string list) (count: int) =
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
let categoryDisclosureScript =
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
let visibleFileRowsScript =
    """() => [...document.querySelectorAll('.file-entry')]
        .filter(entry => entry.offsetParent !== null)
        .map(entry => entry.querySelector('.file-path').textContent)"""

let configureLabel = "Analyze repository and configure diff groups"

/// The configure affordance is identified the way a user finds it — by its accessible label — so the
/// assertions never depend on an internal id or class.
let configureSelector = $"button[aria-label=\"{configureLabel}\"]"

let embeddedFrameSelector = "#diff-frame"

let embeddedHostUrl = $"{ServerFixture.canvasUrl}/e2e-diff-host.html"

/// A minimal stand-in for the canvas pane. An iframe is the only thing that makes
/// `window.parent !== window` genuinely true, and what the document posts to its parent is the whole
/// observable contract of the configure action, so the harness embeds the real diff document and
/// records every action-bearing message it receives.
let embeddedHostHtml (documentUrl: string) =
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

let fileResultJsonWithPatch patch status identity displayPath oldDisplayPath change =
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

let fileResultJson =
    fileResultJsonWithPatch samplePatch

let pageUrl = $"{ServerFixture.canvasUrl}/e2e-diff-worktree/diff.html"

let template =
    File.ReadAllText(templatePath)
    |> CanvasExport.injectAtHead (CanvasDocServer.buildInjection SystemView "diff.html")

let css = File.ReadAllText(assetPath "diff2html.min.css")
let renderer = File.ReadAllText(assetPath "diff2html.min.js")
let highlighter = File.ReadAllText(assetPath "diff2html-ui-slim.min.js")
let viewerCss = File.ReadAllText(viewerAssetPath "viewer.css")
let viewerScript = File.ReadAllText(viewerAssetPath "viewer.js")

/// The routing and navigation every diff viewer fixture needs. Both E2E fixtures inherit it, so
/// the served template, the pinned bundles and the summary/file endpoints are set up once.
type DiffViewerHarness() =
    inherit PageTest()

    override this.ContextOptions() =
        let options = base.ContextOptions()
        options.IgnoreHTTPSErrors <- true
        options

    member this.RouteBody(glob: string, contentType: string, body: string) =
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

    member this.RouteSummary(body) =
        this.RouteBody("**/diff-summary?*", "application/json", body)

    /// Serves `summaries` one per summary request, in order, so a test can script what Load, each
    /// Refresh and a reload see. The last entry keeps answering once the list is exhausted.
    member this.RouteSummaries(summaries: string array) =
        // Playwright calls the route handler once per request and gives it nowhere to carry a
        // position, so the cursor into the scripted sequence has to survive between invocations.
        let mutable summaryIndex = 0

        this.Page.RouteAsync(
            "**/diff-summary?*",
            fun (route: IRoute) ->
                let body = summaries[Math.Min(summaryIndex, summaries.Length - 1)]
                summaryIndex <- summaryIndex + 1

                route.FulfillAsync(
                    RouteFulfillOptions(
                        ContentType = "application/json",
                        Body = body
                    )
                )
        )

    member this.RouteFiles() =
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

    member this.RouteHighlighter() =
        this.RouteBody(
            $"**/{DiffAssets.Version}/diff2html-ui-slim.min.js",
            "text/javascript",
            highlighter
        )

    member this.Goto() =
        task {
            let! _ =
                this.Page.GotoAsync(
                    pageUrl,
                    PageGotoOptions(WaitUntil = WaitUntilState.Load)
                )

            ()
        }

    member this.RouteEmbeddedHost() =
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

    member this.ActivateFile(identity: string) =
        task {
            let entry =
                this.Page.Locator($".file-entry[data-identity='{identity}']")

            do! entry.WaitForAsync()
            do! entry.ClickAsync()
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
            do! this.RouteBody($"**{DiffAssets.viewerCssPath}", "text/css", viewerCss)
            do!
                this.RouteBody(
                    $"**{DiffAssets.viewerScriptPath}",
                    "text/javascript",
                    viewerScript
                )
        }
