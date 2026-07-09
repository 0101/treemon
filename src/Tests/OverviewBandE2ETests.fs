module Tests.OverviewBandE2ETests

// Live-browser (Playwright) verification that the deployed Overview band matches the agreed prototype
// (.agents/canvas/beads-panel-prototypes.html) — spec docs/spec/beads-overview-band.md Corrections
// v1.1 (g)(k). Boots a dedicated server+vite on its own ports fed by a crafted fixture
// (fixtures/overview-band.json) that yields NON-EMPTY agents AND tasks so every assertion below has
// live DOM to bind to. Assertions are on DOM/CSS structure (classes, order, computed colours,
// proportional widths), never on data values.

open System
open System.Diagnostics
open System.IO
open System.Net.Http
open NUnit.Framework
open Microsoft.Playwright
open Microsoft.Playwright.NUnit
open Newtonsoft.Json.Linq

let private repoRoot =
    Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))

let private serverProjectPath = Path.Combine(repoRoot, "src", "Server")

let private fixturePath =
    Path.Combine(repoRoot, "src", "Tests", "fixtures", "overview-band.json")

let private serverPort = 5087
let private canvasPort = 5088
let private vitePort = 5187
let private serverUrl = $"http://localhost:{serverPort}"
let private viteUrl = $"http://localhost:{vitePort}"

let private serverProcess: Process option ref = ref None
let private viteProcess: Process option ref = ref None

let private tryGet (client: HttpClient) (url: string) =
    async {
        try
            let! response = client.GetAsync(url) |> Async.AwaitTask
            return int response.StatusCode < 500
        with _ ->
            return false
    }

let rec private pollUntilReady (client: HttpClient) (url: string) (deadline: DateTime) =
    async {
        if DateTime.UtcNow > deadline then
            failwith $"Timed out waiting for {url}"
        else
            let! ok = tryGet client url
            if not ok then
                do! Async.Sleep(500)
                return! pollUntilReady client url deadline
    }

let private waitForUrl (url: string) (timeoutMs: int) =
    async {
        use client = new HttpClient()
        let deadline = DateTime.UtcNow.AddMilliseconds(float timeoutMs)
        do! pollUntilReady client url deadline
    }
    |> Async.StartAsTask

let private startServer () =
    task {
        TestUtils.killOrphansOnPort serverPort
        let proc =
            TestUtils.startProcess
                "dotnet"
                $"""run --project "{serverProjectPath}" -- "{repoRoot}" --port {serverPort} --canvas-port {canvasPort} --test-fixtures "{fixturePath}" """
                repoRoot
                []
                false
        serverProcess.Value <- Some proc
        do! waitForUrl serverUrl 30000
    }

let private startVite () =
    task {
        TestUtils.killOrphansOnPort vitePort
        let proc =
            TestUtils.startProcess
                "npx"
                "vite --host"
                repoRoot
                [ "VITE_PORT", string vitePort
                  "API_PORT", string serverPort
                  "CANVAS_PORT", string canvasPort
                  "NODE_OPTIONS", "--max-old-space-size=512" ]
                false
        viteProcess.Value <- Some proc
        do! waitForUrl viteUrl 15000
    }

// Hex #rrggbb -> the "rgb(r, g, b)" string a browser's getComputedStyle returns, so colour
// assertions compare against the exact computed form.
let private rgb (hex: string) =
    let h = hex.TrimStart('#')
    let v (i: int) = Convert.ToInt32(h.Substring(i, 2), 16)
    $"rgb({v 0}, {v 2}, {v 4})"

// Reads a big JSON snapshot of the band's structure in one round-trip.
let private bandProbeJs =
    """
    () => {
      const qa = s => Array.from(document.querySelectorAll(s));
      const band = document.querySelector('.overview-band');
      const dash = document.querySelector('.dashboard');
      const repoList = document.querySelector('.repo-list');
      const sections = qa('.overview-band .overview-section');
      const agentsSec = sections.find(s => s.querySelector('.overview-circles'));
      const tasksSec = sections.find(s => s.querySelector('.overview-bar'));
      const itemByLabel = (sec, label) => Array.from(sec.querySelectorAll('.overview-item'))
        .find(it => { const l = it.querySelector('.overview-label'); return l && l.textContent.trim() === label; });
      const bg = el => el ? getComputedStyle(el).backgroundColor : null;
      const following = (a, b) => !!(a && b && (a.compareDocumentPosition(b) & Node.DOCUMENT_POSITION_FOLLOWING));

      const firstAgent = agentsSec.querySelector('.overview-item');
      const meta = firstAgent.querySelector('.overview-meta');
      const circles = firstAgent.querySelector('.overview-circles');
      const count = meta.querySelector('.overview-count');
      const label = meta.querySelector('.overview-label');

      const bandRect = band.getBoundingClientRect();
      const taskItems = Array.from(tasksSec.querySelectorAll('.overview-item'));
      const bars = taskItems.map(it => {
        const bar = it.querySelector('.overview-bar');
        const r = bar.getBoundingClientRect();
        return { label: it.querySelector('.overview-label').textContent.trim(),
                 barCount: it.querySelectorAll('.overview-bar').length,
                 childCells: bar.childElementCount,
                 width: r.width, right: r.right };
      });

      return JSON.stringify({
        hasBand: !!band,
        insideDash: !!(dash && band && dash.contains(band)),
        bandBeforeRepoList: following(band, repoList),
        sectionCount: sections.length,
        agentsHeader: agentsSec.querySelector('.overview-header').textContent.trim(),
        agentsHeaderTransform: getComputedStyle(agentsSec.querySelector('.overview-header')).textTransform,
        tasksHeader: tasksSec.querySelector('.overview-header').textContent.trim(),
        tasksHeaderTransform: getComputedStyle(tasksSec.querySelector('.overview-header')).textTransform,
        metaBeforeCircles: following(meta, circles),
        countBeforeLabel: following(count, label),
        agentGroupCount: agentsSec.querySelectorAll('.overview-item').length,
        bars,
        bandRight: bandRect.right,
        colors: {
          taskDone: bg(itemByLabel(tasksSec, 'Done') && itemByLabel(tasksSec, 'Done').querySelector('.overview-bar')),
          taskInProgress: bg(itemByLabel(tasksSec, 'In progress') && itemByLabel(tasksSec, 'In progress').querySelector('.overview-bar')),
          taskPlanned: bg(itemByLabel(tasksSec, 'Planned') && itemByLabel(tasksSec, 'Planned').querySelector('.overview-bar')),
          agentPlanning: bg(itemByLabel(agentsSec, 'Planning') && itemByLabel(agentsSec, 'Planning').querySelector('.overview-circle')),
          agentReviewing: bg(itemByLabel(agentsSec, 'Reviewing') && itemByLabel(agentsSec, 'Reviewing').querySelector('.overview-circle')),
          agentWaiting: bg(itemByLabel(agentsSec, 'Waiting') && itemByLabel(agentsSec, 'Waiting').querySelector('.overview-circle'))
        },
        caption: (band.querySelector('.overview-caption') || {}).textContent || '',
        blockedPresent: !!itemByLabel(tasksSec, 'Blocked'),
        queuedPresent: !!itemByLabel(tasksSec, 'Queued'),
        fixingPresent: !!itemByLabel(agentsSec, 'Fixing'),
        genericWorkingPresent: !!itemByLabel(agentsSec, 'Working'),
        zeroTextCount: qa('.overview-band .overview-count').filter(c => c.textContent.trim() === '0').length
      });
    }
    """

let private cardProbeJs =
    """
    () => {
      const cards = Array.from(document.querySelectorAll('.wt-card'));
      const anyActClass = cards.some(c => Array.from(c.classList).some(cl => cl.startsWith('act-')));
      const working = document.querySelector('.wt-card.ct-working');
      const before = working ? getComputedStyle(working, '::before') : null;
      const redDot = document.querySelector('.ct-dot.working');
      return JSON.stringify({
        cardCount: cards.length,
        anyActClass,
        beforeContent: before ? before.content : 'none',
        beforeBackground: before ? before.backgroundColor : '',
        redDotColor: redDot ? getComputedStyle(redDot).backgroundColor : null
      });
    }
    """

[<TestFixture>]
[<Category("E2E")>]
[<Category("OverviewBandE2E")>]
type OverviewBandE2ETests() =
    inherit PageTest()

    let mutable probe: JObject = null
    let mutable cardProbe: JObject = null

    override this.ContextOptions() =
        let opts = base.ContextOptions()
        opts.IgnoreHTTPSErrors <- true
        opts

    [<OneTimeSetUp>]
    member _.StartInfrastructure() =
        task {
            do! startServer ()
            do! ServerFixture.compileFable ()
            do! startVite ()
            TestContext.Out.WriteLine($"Overview-band server ({serverUrl}) and Vite ({viteUrl}) started")
        }

    [<OneTimeTearDown>]
    member _.StopInfrastructure() =
        TestUtils.killProc serverProcess.Value
        TestUtils.killProc viteProcess.Value
        serverProcess.Value <- None
        viteProcess.Value <- None

    // Navigate, wait for the grid, toggle Overview on, wait for the band, then snapshot the DOM once.
    [<SetUp>]
    member this.OpenOverview() =
        task {
            let! _ = this.Page.GotoAsync(viteUrl)
            do! this.Page.Locator(".wt-card .branch-name").First.WaitForAsync(LocatorWaitForOptions(Timeout = 15000.0f))

            let overviewBtn =
                this.Page.Locator(".header-controls .ctrl-btn", PageLocatorOptions(HasText = "Overview"))
            do! overviewBtn.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            do! overviewBtn.ClickAsync()
            do! this.Page.Locator(".overview-band").WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
            do! this.Page.Locator(".overview-band .overview-bar").First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            let! json = this.Page.EvaluateAsync<string>(bandProbeJs)
            probe <- JObject.Parse(json)
            let! cardJson = this.Page.EvaluateAsync<string>(cardProbeJs)
            cardProbe <- JObject.Parse(cardJson)
            TestContext.Out.WriteLine($"band probe: {json}")
            TestContext.Out.WriteLine($"card probe: {cardJson}")
        }

    // Step 1: band placement + two headed sections.
    [<Test>]
    member _.``Step 1 - band sits in dashboard above repo-list with two headed sections``() =
        Assert.That(probe.Value<bool>("hasBand"), Is.True, ".overview-band must render")
        Assert.That(probe.Value<bool>("insideDash"), Is.True, ".overview-band must be inside .dashboard")
        Assert.That(probe.Value<bool>("bandBeforeRepoList"), Is.True, ".overview-band must precede .repo-list in DOM order")
        Assert.That(probe.Value<int>("sectionCount"), Is.EqualTo(2), "exactly TWO .overview-section blocks")

        let agentsHeader = probe.Value<string>("agentsHeader")
        let tasksHeader = probe.Value<string>("tasksHeader")
        Assert.That(agentsHeader.ToUpperInvariant(), Does.StartWith("ACTIVE AGENTS"), "agents header begins ACTIVE AGENTS")
        Assert.That(tasksHeader.ToUpperInvariant(), Does.StartWith("TASKS"), "tasks header begins TASKS")
        Assert.That(probe.Value<string>("agentsHeaderTransform"), Is.EqualTo("uppercase"), "agents header rendered uppercase")
        Assert.That(probe.Value<string>("tasksHeaderTransform"), Is.EqualTo("uppercase"), "tasks header rendered uppercase")

    // Step 2: meta line above circles, count before label.
    [<Test>]
    member _.``Step 2 - agent column meta line sits above circles, count first``() =
        Assert.That(probe.Value<bool>("metaBeforeCircles"), Is.True, "count+label meta line must precede the circles")
        Assert.That(probe.Value<bool>("countBeforeLabel"), Is.True, "count must precede label (count-first)")

    // Step 3: one proportional bar per status, no unit cells, no overflow.
    [<Test>]
    member _.``Step 3 - one proportional bar per status, widest fits the band``() =
        let bars = (probe.["bars"] :?> JArray) |> Seq.cast<JObject> |> List.ofSeq
        Assert.That(bars.Length, Is.EqualTo(3), "three non-empty task buckets -> three bars")

        let widthOf label =
            bars |> List.find (fun b -> b.Value<string>("label") = label)

        bars
        |> List.iter (fun b ->
            let lbl = b.Value<string>("label")
            Assert.That(b.Value<int>("barCount"), Is.EqualTo(1), $"status {lbl} renders exactly ONE .overview-bar")
            Assert.That(b.Value<int>("childCells"), Is.EqualTo(0), $"bar for {lbl} is a single element, NOT a run of unit cells"))

        let done_ = (widthOf "Done").Value<float>("width")
        let planned = (widthOf "Planned").Value<float>("width")
        let inProg = (widthOf "In progress").Value<float>("width")

        Assert.That(done_, Is.GreaterThan(planned), "largest count (Done=40) is widest")
        Assert.That(planned, Is.GreaterThan(inProg), "smaller count (In progress=10) is strictly narrower than Planned=20")

        // Proportional (one true scale), not equal fixed widths: Planned≈½·Done, In progress≈¼·Done.
        Assert.That(planned / done_, Is.EqualTo(0.5).Within(0.06), "Planned width ∝ its count on the shared scale")
        Assert.That(inProg / done_, Is.EqualTo(0.25).Within(0.06), "In progress width ∝ its count on the shared scale")

        let bandRight = probe.Value<float>("bandRight")
        bars
        |> List.iter (fun b ->
            let lbl = b.Value<string>("label")
            Assert.That(b.Value<float>("right"), Is.LessThanOrEqualTo(bandRight + 1.0), $"bar {lbl} must not overflow the band"))

    // Step 4: exact accent palette on tasks and agents.
    [<Test>]
    member _.``Step 4 - accent colours match the Catppuccin palette``() =
        let colors = probe.["colors"] :?> JObject
        Assert.That(colors.Value<string>("taskDone"), Is.EqualTo(rgb "#cba6f7"), "Done = mauve")
        Assert.That(colors.Value<string>("taskInProgress"), Is.EqualTo(rgb "#a6e3a1"), "In progress = green")
        Assert.That(colors.Value<string>("taskPlanned"), Is.EqualTo(rgb "#fab387"), "Planned = peach")
        Assert.That(colors.Value<string>("agentPlanning"), Is.EqualTo(rgb "#cba6f7"), "agent Planning = mauve")
        Assert.That(colors.Value<string>("agentReviewing"), Is.EqualTo(rgb "#f5c2e7"), "agent Reviewing = pink")
        Assert.That(colors.Value<string>("agentWaiting"), Is.EqualTo(rgb "#f9e2af"), "Waiting = yellow")

    // Step 5: footer caption.
    [<Test>]
    member _.``Step 5 - footer caption mentions one true scale``() =
        Assert.That(probe.Value<string>("caption"), Does.Contain("one true scale"), "caption present under the task bars")

    // Step 6: zero-count buckets are omitted, never rendered as 0.
    [<Test>]
    member _.``Step 6 - zero-count status and activity are omitted``() =
        Assert.That(probe.Value<bool>("blockedPresent"), Is.False, "zero-count Blocked bucket must not render")
        Assert.That(probe.Value<bool>("queuedPresent"), Is.False, "zero-count Queued bucket must not render")
        Assert.That(probe.Value<bool>("fixingPresent"), Is.False, "zero-count Fixing activity must not render")
        Assert.That(probe.Value<bool>("genericWorkingPresent"), Is.False, "zero-count generic Working activity must not render")
        Assert.That(probe.Value<int>("zeroTextCount"), Is.EqualTo(0), "no count element ever renders the text '0'")

    // Step 7 (correction k): per-card activity stripe removed; red dot intact.
    [<Test>]
    member _.``Step 7 - worktree cards carry no activity stripe, red dot unchanged``() =
        Assert.That(cardProbe.Value<int>("cardCount"), Is.GreaterThanOrEqualTo(6), "worktree cards render in the grid")
        Assert.That(cardProbe.Value<bool>("anyActClass"), Is.False, "no .wt-card carries an act-* activity-stripe modifier")
        Assert.That(cardProbe.Value<string>("beforeContent"), Is.EqualTo("none"), "no left ::before activity stripe is painted")
        Assert.That(cardProbe.Value<string>("redDotColor"), Is.EqualTo(rgb "#ff0000"), "the ct-dot working red dot is unchanged")
