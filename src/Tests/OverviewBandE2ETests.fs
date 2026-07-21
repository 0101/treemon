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
open NUnit.Framework
open Microsoft.Playwright
open Microsoft.Playwright.NUnit
open Newtonsoft.Json.Linq
open Shared
open OverviewData

let private repoRoot =
    Path.GetFullPath(Path.Combine(__SOURCE_DIRECTORY__, "..", ".."))

let private serverProjectPath = Path.Combine(repoRoot, "src", "Server")

let private fixturePath =
    Path.Combine(repoRoot, "src", "Tests", "fixtures", "overview-band.json")

// Distinct free loopback ports (reserved once at fixture load) instead of fixed ports, so the suite
// never collides with — or kills — a running production instance or a parallel test run. Distinctness
// is only guaranteed within this single reservation, so the fixture below is [<NonParallelizable>].
let private serverPort, canvasPort, vitePort =
    match TestUtils.getFreeTcpPorts 3 with
    | [ s; c; v ] -> s, c, v
    | other -> failwith $"expected 3 free ports, got {other.Length}"

let private serverUrl = $"http://localhost:{serverPort}"
let private viteUrl = $"http://localhost:{vitePort}"

let private serverProcess: Process option ref = ref None
let private viteProcess: Process option ref = ref None

let private startServer () =
    task {
        let proc =
            TestUtils.startServerProcess serverProjectPath repoRoot $"\"{repoRoot}\"" serverPort canvasPort fixturePath
        serverProcess.Value <- Some proc
        do! TestUtils.waitForUrl serverUrl 30000
    }

let private startVite () =
    task {
        let proc =
            TestUtils.startViteProcess repoRoot vitePort serverPort canvasPort
        viteProcess.Value <- Some proc
        do! TestUtils.waitForUrl viteUrl 15000
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
      // Agent circles paint their accent through currentColor (a solid circle fills its background to
      // it; a context donut sweeps a conic-gradient arc from it, leaving background-color transparent),
      // so read the accent from `color` to cover both the solid and the donut render.
      const fg = el => el ? getComputedStyle(el).color : null;
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
        donutCount: qa('.overview-band .overview-donut').length,
        circleCount: qa('.overview-band .overview-circle').length,
        donutCtxRemaining: (() => {
          const d = agentsSec.querySelector('.overview-donut');
          return d ? getComputedStyle(d).getPropertyValue('--ctx-remaining').trim() : '';
        })(),
        donutWidth: (() => {
          const d = agentsSec.querySelector('.overview-donut');
          return d ? getComputedStyle(d).width : '';
        })(),
        donutHeight: (() => {
          const d = agentsSec.querySelector('.overview-donut');
          return d ? getComputedStyle(d).height : '';
        })(),
        plainCircleWidth: (() => {
          const d = agentsSec.querySelector('.overview-circle:not(.overview-donut)');
          return d ? getComputedStyle(d).width : '';
        })(),
        plainCircleHeight: (() => {
          const d = agentsSec.querySelector('.overview-circle:not(.overview-donut)');
          return d ? getComputedStyle(d).height : '';
        })(),
        circleGap: getComputedStyle(circles).gap,
        circleAlignItems: getComputedStyle(circles).alignItems,
        investigatingCircles: (() => {
          const it = itemByLabel(agentsSec, 'Investigating');
          return it ? it.querySelectorAll('.overview-circle').length : 0;
        })(),
        investigatingDonuts: (() => {
          const it = itemByLabel(agentsSec, 'Investigating');
          return it ? it.querySelectorAll('.overview-donut').length : 0;
        })(),
        colors: {
          taskDone: bg(itemByLabel(tasksSec, 'Done') && itemByLabel(tasksSec, 'Done').querySelector('.overview-bar')),
          taskInProgress: bg(itemByLabel(tasksSec, 'In progress') && itemByLabel(tasksSec, 'In progress').querySelector('.overview-bar')),
          taskPlanned: bg(itemByLabel(tasksSec, 'Planned') && itemByLabel(tasksSec, 'Planned').querySelector('.overview-bar')),
          agentPlanning: fg(itemByLabel(agentsSec, 'Planning') && itemByLabel(agentsSec, 'Planning').querySelector('.overview-circle')),
          agentReviewing: fg(itemByLabel(agentsSec, 'Reviewing') && itemByLabel(agentsSec, 'Reviewing').querySelector('.overview-circle')),
          agentPr: fg(itemByLabel(agentsSec, 'PR') && itemByLabel(agentsSec, 'PR').querySelector('.overview-circle')),
          agentWaiting: fg(itemByLabel(agentsSec, 'Waiting') && itemByLabel(agentsSec, 'Waiting').querySelector('.overview-circle')),
          agentIdle: fg(itemByLabel(agentsSec, 'Idle') && itemByLabel(agentsSec, 'Idle').querySelector('.overview-circle'))
        },
        blockedPresent: !!itemByLabel(tasksSec, 'Blocked'),
        queuedPresent: !!itemByLabel(tasksSec, 'Queued'),
        prPresent: !!itemByLabel(agentsSec, 'PR'),
        prHasActivityClass: (() => { const it = itemByLabel(agentsSec, 'PR'); return !!it && it.classList.contains('activity-pr'); })(),
        executingPresent: !!itemByLabel(agentsSec, 'Executing'),
        genericWorkingPresent: !!itemByLabel(agentsSec, 'Working'),
        idlePresent: !!itemByLabel(agentsSec, 'Idle'),
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
      const contextDonut = document.querySelector('.wt-card .ct-dot.ct-donut.working');
      const donutRing = contextDonut ? getComputedStyle(contextDonut, '::before') : null;
      const donutCenter = contextDonut ? getComputedStyle(contextDonut, '::after') : null;
      return JSON.stringify({
        cardCount: cards.length,
        anyActClass,
        beforeContent: before ? before.content : 'none',
        beforeBackground: before ? before.backgroundColor : '',
        redDotColor: redDot ? getComputedStyle(redDot).backgroundColor : null,
        hasContextDonut: !!contextDonut,
        contextDonutWidth: contextDonut ? getComputedStyle(contextDonut).width : '',
        contextDonutHeight: contextDonut ? getComputedStyle(contextDonut).height : '',
        contextDonutAnimation: contextDonut ? getComputedStyle(contextDonut).animationName : '',
        donutRingAnimation: donutRing ? donutRing.animationName : '',
        donutCenterWidth: donutCenter ? donutCenter.width : '',
        donutCenterHeight: donutCenter ? donutCenter.height : '',
        donutCenterColor: donutCenter ? donutCenter.backgroundColor : '',
        donutCenterAnimation: donutCenter ? donutCenter.animationName : '',
        donutCenterDuration: donutCenter ? donutCenter.animationDuration : ''
      });
    }
    """

[<TestFixture>]
[<Category("E2E")>]
[<Category("OverviewBandE2E")>]
[<NonParallelizable>]
type OverviewBandE2ETests() =
    inherit PageTest()

    // DOM snapshots captured once per test in [<SetUp>] (OpenOverview) and read by every [<Test>].
    // NUnit's fixture lifecycle sets these across the setup/test boundary, so they are held in ref
    // cells (immutable bindings, matching the module's serverProcess/viteProcess pattern) rather than
    // `let mutable`. Option makes the "not captured yet" state explicit instead of a null, and the
    // require* accessors below fail loudly if a test body runs without SetUp.
    let probe: JObject option ref = ref None
    let cardProbe: JObject option ref = ref None

    let requireProbe () =
        probe.Value |> Option.defaultWith (fun () -> failwith "band probe not captured (SetUp did not run)")

    let requireCardProbe () =
        cardProbe.Value |> Option.defaultWith (fun () -> failwith "card probe not captured (SetUp did not run)")

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
            probe.Value <- Some(JObject.Parse(json))
            let! cardJson = this.Page.EvaluateAsync<string>(cardProbeJs)
            cardProbe.Value <- Some(JObject.Parse(cardJson))
            TestContext.Out.WriteLine($"band probe: {json}")
            TestContext.Out.WriteLine($"card probe: {cardJson}")
        }

    // Step 1: band placement + two headed sections.
    [<Test>]
    member _.``Step 1 - band sits in dashboard above repo-list with two headed sections``() =
        let probe = requireProbe ()
        Assert.That(probe.Value<bool>("hasBand"), Is.True, ".overview-band must render")
        Assert.That(probe.Value<bool>("insideDash"), Is.True, ".overview-band must be inside .dashboard")
        Assert.That(probe.Value<bool>("bandBeforeRepoList"), Is.True, ".overview-band must precede .repo-list in DOM order")
        Assert.That(probe.Value<int>("sectionCount"), Is.EqualTo(2), "exactly TWO .overview-section blocks")

        let agentsHeader = probe.Value<string>("agentsHeader")
        let tasksHeader = probe.Value<string>("tasksHeader")
        Assert.That(agentsHeader.ToUpperInvariant(), Is.EqualTo("AGENTS"), "agents header is the bare label AGENTS")
        Assert.That(tasksHeader.ToUpperInvariant(), Is.EqualTo("TASKS"), "tasks header is the bare label TASKS")
        Assert.That(probe.Value<string>("agentsHeaderTransform"), Is.EqualTo("uppercase"), "agents header rendered uppercase")
        Assert.That(probe.Value<string>("tasksHeaderTransform"), Is.EqualTo("uppercase"), "tasks header rendered uppercase")

    // Step 2: meta line above circles, count before label.
    [<Test>]
    member _.``Step 2 - agent column meta line sits above circles, count first``() =
        let probe = requireProbe ()
        Assert.That(probe.Value<bool>("metaBeforeCircles"), Is.True, "count+label meta line must precede the circles")
        Assert.That(probe.Value<bool>("countBeforeLabel"), Is.True, "count must precede label (count-first)")

    // Step 3: one proportional bar per status, no unit cells, no overflow.
    [<Test>]
    member _.``Step 3 - one proportional bar per status, widest fits the band``() =
        let probe = requireProbe ()
        let bars = (probe["bars"] :?> JArray) |> Seq.cast<JObject> |> List.ofSeq
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
        let probe = requireProbe ()
        let colors = probe["colors"] :?> JObject
        Assert.That(colors.Value<string>("taskDone"), Is.EqualTo(rgb "#cba6f7"), "Done = mauve")
        Assert.That(colors.Value<string>("taskInProgress"), Is.EqualTo(rgb "#a6e3a1"), "In progress = green")
        Assert.That(colors.Value<string>("taskPlanned"), Is.EqualTo(rgb "#fab387"), "Planned = peach")
        Assert.That(colors.Value<string>("agentPlanning"), Is.EqualTo(rgb "#cba6f7"), "agent Planning = mauve")
        Assert.That(colors.Value<string>("agentReviewing"), Is.EqualTo(rgb "#f5c2e7"), "agent Reviewing = pink")
        Assert.That(colors.Value<string>("agentWaiting"), Is.EqualTo(rgb "#f9e2af"), "Waiting = yellow")
        Assert.That(probe.Value<bool>("idlePresent"), Is.True, "Idle group (blue-dot idle agents) renders in the Agents row")
        Assert.That(colors.Value<string>("agentIdle"), Is.EqualTo(rgb "#89b4fa"), "Idle = blue")

    // The renamed PR activity's positive render path: a PR-skill working agent (fixture: pr-a) must
    // surface a PR group labelled "PR", carrying the activity-pr accent class and its peach circle.
    [<Test>]
    member _.``PR activity renders with its label, activity-pr class and peach accent``() =
        let probe = requireProbe ()
        let colors = probe["colors"] :?> JObject
        Assert.That(probe.Value<bool>("prPresent"), Is.True, "the PR-skill agent renders a PR activity group")
        Assert.That(probe.Value<bool>("prHasActivityClass"), Is.True, "the PR group carries the activity-pr accent class")
        Assert.That(colors.Value<string>("agentPr"), Is.EqualTo(rgb "#fab387"), "agent PR = peach")

    // Step 6: zero-count buckets are omitted, never rendered as 0.
    [<Test>]
    member _.``Step 6 - zero-count status and activity are omitted``() =
        let probe = requireProbe ()
        Assert.That(probe.Value<bool>("blockedPresent"), Is.False, "zero-count Blocked bucket must not render")
        Assert.That(probe.Value<bool>("queuedPresent"), Is.False, "zero-count Queued bucket must not render")
        Assert.That(probe.Value<bool>("executingPresent"), Is.False, "zero-count Executing activity must not render")
        Assert.That(probe.Value<bool>("genericWorkingPresent"), Is.False, "zero-count generic Working activity must not render")
        Assert.That(probe.Value<int>("zeroTextCount"), Is.EqualTo(0), "no count element ever renders the text '0'")

    // Step 7 (correction k): per-card activity stripe removed; red dot intact.
    [<Test>]
    member _.``Step 7 - worktree cards carry no activity stripe, red dot unchanged``() =
        let cardProbe = requireCardProbe ()
        Assert.That(cardProbe.Value<int>("cardCount"), Is.GreaterThanOrEqualTo(6), "worktree cards render in the grid")
        Assert.That(cardProbe.Value<bool>("anyActClass"), Is.False, "no .wt-card carries an act-* activity-stripe modifier")
        Assert.That(cardProbe.Value<string>("beforeContent"), Is.EqualTo("none"), "no left ::before activity stripe is painted")
        Assert.That(cardProbe.Value<string>("redDotColor"), Is.EqualTo(rgb "#ff0000"), "the ct-dot working red dot is unchanged")

    // The per-session context-donut render path. The fixture now carries Sessions with ContextUsage,
    // so these bind to the arc + custom property, the multi-session cluster, and the drill-down chip
    // fill — the core rendering path an empty-Sessions fixture never exercised (it only ever drew the
    // fallback solid circle).
    [<Test>]
    member _.``Donuts - sessions with usage render a context-remaining arc, usage-less stay solid``() =
        let probe = requireProbe ()
        Assert.That(probe.Value<int>("donutCount"), Is.GreaterThanOrEqualTo(1), "sessions with usage render .overview-donut rings")
        Assert.That(probe.Value<int>("circleCount"), Is.GreaterThan(probe.Value<int>("donutCount")), "usage-less sessions stay plain circles (mixed donut + solid)")
        let remaining =
            System.Double.Parse(probe.Value<string>("donutCtxRemaining"), System.Globalization.CultureInfo.InvariantCulture)
        Assert.That(remaining, Is.GreaterThan(0.0).And.LessThanOrEqualTo(1.0), "--ctx-remaining is a fraction in (0,1]")

    [<Test>]
    member _.``Donuts - overview mixes centered 10px dots with thin 15px rings``() =
        let probe = requireProbe ()
        Assert.That(probe.Value<string>("donutWidth"), Is.EqualTo("15px"))
        Assert.That(probe.Value<string>("donutHeight"), Is.EqualTo("15px"))
        Assert.That(probe.Value<string>("plainCircleWidth"), Is.EqualTo("10px"))
        Assert.That(probe.Value<string>("plainCircleHeight"), Is.EqualTo("10px"))
        Assert.That(probe.Value<string>("circleGap"), Is.EqualTo("6px"))
        Assert.That(probe.Value<string>("circleAlignItems"), Is.EqualTo("center"))

    [<Test>]
    member _.``Donuts - card ring stays stable while its fixed-size red center pulses``() =
        let probe = requireCardProbe ()
        Assert.That(probe.Value<bool>("hasContextDonut"), Is.True)
        Assert.That(probe.Value<string>("contextDonutWidth"), Is.EqualTo("15px"))
        Assert.That(probe.Value<string>("contextDonutHeight"), Is.EqualTo("15px"))
        Assert.That(probe.Value<string>("contextDonutAnimation"), Is.EqualTo("none"))
        Assert.That(probe.Value<string>("donutRingAnimation"), Is.EqualTo("none"))
        Assert.That(probe.Value<string>("donutCenterWidth"), Is.EqualTo("10px"))
        Assert.That(probe.Value<string>("donutCenterHeight"), Is.EqualTo("10px"))
        Assert.That(probe.Value<string>("donutCenterColor"), Is.EqualTo(rgb "#ff0000"))
        Assert.That(probe.Value<string>("donutCenterAnimation"), Is.EqualTo("pulse"))
        Assert.That(probe.Value<string>("donutCenterDuration"), Is.EqualTo("2s"))

    [<Test>]
    member _.``Donuts - a multi-session worktree clusters its session circles in one group``() =
        let probe = requireProbe ()
        Assert.That(probe.Value<int>("investigatingCircles"), Is.EqualTo(4), "three investigate worktrees, one with two sessions -> four session circles")
        Assert.That(probe.Value<int>("investigatingDonuts"), Is.EqualTo(3), "three of those sessions reported usage -> three donuts, one plain")

    [<Test>]
    member this.``Drill-down - agent breakdown chips carry a context-used fill``() =
        task {
            let investigating =
                this.Page.Locator(".overview-band .overview-item", PageLocatorOptions(HasText = "Investigating"))
            do! investigating.ClickAsync()
            do! this.Page.Locator(".overview-breakdown .overview-chip").First.WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            let! json =
                this.Page.EvaluateAsync<string>(
                    """() => {
                      const chips = Array.from(document.querySelectorAll('.overview-breakdown .overview-chip'));
                      const used = chips.map(c => parseFloat(getComputedStyle(c).getPropertyValue('--ctx-used')) || 0);
                      return JSON.stringify({ chipCount: chips.length, maxUsed: used.length ? Math.max.apply(null, used) : 0 });
                    }""")

            let bd = JObject.Parse(json)
            Assert.That(bd.Value<int>("chipCount"), Is.EqualTo(3), "the Investigating breakdown lists its three member worktrees")
            Assert.That(bd.Value<float>("maxUsed"), Is.GreaterThan(0.0), "a member's most-loaded session drives a non-zero --ctx-used chip fill")
        }

    [<Test>]
    member this.``History cycle includes 12h and remains mutually exclusive with drill-down``() =
        task {
            let toggle = this.Page.Locator(".overview-band .history-toggle")
            let charts = this.Page.Locator(".overview-band .history-charts")
            let breakdown = this.Page.Locator(".overview-band .overview-breakdown")
            let investigating =
                this.Page.Locator(".overview-band .overview-section .overview-item", PageLocatorOptions(HasText = "Investigating"))

            do! Assertions.Expect(toggle).ToHaveTextAsync("\u25F7 History")
            do! toggle.ClickAsync()
            do! Assertions.Expect(toggle).ToHaveTextAsync("\u25F7 12h")
            do! Assertions.Expect(charts).ToHaveCountAsync(2)

            let! axisLabels = charts.First.Locator(".axis-label-x").AllTextContentsAsync()
            Assert.That(
                axisLabels |> List.ofSeq,
                Is.EqualTo([ "-12h"; "-9h"; "-6h"; "-3h"; "now" ])
            )

            do! toggle.ClickAsync()
            do! Assertions.Expect(toggle).ToHaveTextAsync("\u25F7 24h")
            do! toggle.ClickAsync()
            do! Assertions.Expect(toggle).ToHaveTextAsync("\u25F7 72h")
            do! toggle.ClickAsync()
            do! Assertions.Expect(toggle).ToHaveTextAsync("\u25F7 History")
            do! Assertions.Expect(charts).ToHaveCountAsync(0)

            do! investigating.ClickAsync()
            do! Assertions.Expect(breakdown).ToHaveCountAsync(1)
            do! toggle.ClickAsync()
            do! Assertions.Expect(toggle).ToHaveTextAsync("\u25F7 12h")
            do! Assertions.Expect(breakdown).ToHaveCountAsync(0)
            do! Assertions.Expect(charts).ToHaveCountAsync(2)

            do! investigating.ClickAsync()
            do! Assertions.Expect(toggle).ToHaveTextAsync("\u25F7 History")
            do! Assertions.Expect(charts).ToHaveCountAsync(0)
            do! Assertions.Expect(breakdown).ToHaveCountAsync(1)
        }

    [<Test>]
    member this.``History chart skips an unrelated dashboard poll``() =
        task {
            let toggle = this.Page.Locator(".overview-band .history-toggle")
            let charts = this.Page.Locator(".overview-band .history-charts")

            do! toggle.ClickAsync()
            do! Assertions.Expect(toggle).ToHaveTextAsync("\u25F7 12h")
            do! Assertions.Expect(charts).ToHaveCountAsync(2)

            let firstChart = charts.First
            let! beforePoll = firstChart.GetAttributeAsync("data-geometry-build-count")
            let! rendersBeforePoll = firstChart.GetAttributeAsync("data-render-count")
            Assert.That(beforePoll, Is.EqualTo "1")
            Assert.That(rendersBeforePoll, Is.EqualTo "1")

            let isDashboardPoll (response: IResponse) =
                response.Url.Contains("/IWorktreeApi/getWorktrees")

            let! pollResponse =
                this.Page.WaitForResponseAsync(
                    isDashboardPoll,
                    PageWaitForResponseOptions(Timeout = 5000.0f)
                )
            let! _ = pollResponse.FinishedAsync()
            let! _ =
                this.Page.EvaluateAsync<bool>(
                    "() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(() => resolve(true))))"
                )

            let! afterPoll = firstChart.GetAttributeAsync("data-geometry-build-count")
            let! rendersAfterPoll = firstChart.GetAttributeAsync("data-render-count")
            Assert.That(afterPoll, Is.EqualTo "1")
            Assert.That(rendersAfterPoll, Is.EqualTo "1")
        }

    [<Test>]
    member this.``History window switches keep the old chart mounted and reject stale responses``() =
        task {
            let toggle = this.Page.Locator(".overview-band .history-toggle")
            let charts = this.Page.Locator(".overview-band .history-charts")
            let firstAxisLabel = charts.First.Locator(".axis-label-x").First
            let anchor = DateTimeOffset.UtcNow

            let historyBody count =
                let response: OverviewHistoryResponse =
                    { Anchor = anchor
                      Snapshots =
                        [ { Timestamp = anchor - TimeSpan.FromHours 72.0
                            Tasks = [ { Kind = TaskBucketKind.Done; Count = count } ]
                            Agents = [ { Kind = AgentGroupKind.Idle; Count = count } ] }
                          { Timestamp = anchor
                            Tasks = [ { Kind = TaskBucketKind.Done; Count = count + 1 } ]
                            Agents = [ { Kind = AgentGroupKind.Idle; Count = count + 1 } ] } ] }

                Newtonsoft.Json.JsonConvert.SerializeObject(
                    response,
                    Fable.Remoting.Json.FableJsonConverter()
                )

            let requestCount = ref 0
            let delayed24 = System.Threading.Tasks.TaskCompletionSource<IRoute>()
            let delayed72 = System.Threading.Tasks.TaskCompletionSource<IRoute>()

            do!
                this.Page.RouteAsync(
                    "**/IWorktreeApi/getOverviewHistory",
                    fun route ->
                        requestCount.Value <- requestCount.Value + 1

                        match requestCount.Value with
                        | 1 ->
                            route.FulfillAsync(
                                RouteFulfillOptions(
                                    ContentType = "application/json",
                                    Body = historyBody 1
                                )
                            )
                        | 2 ->
                            delayed24.TrySetResult(route) |> ignore
                            System.Threading.Tasks.Task.CompletedTask
                        | 3 ->
                            delayed72.TrySetResult(route) |> ignore
                            System.Threading.Tasks.Task.CompletedTask
                        | _ -> route.AbortAsync()
                )

            let firstResponse =
                this.Page.WaitForResponseAsync(fun response ->
                    response.Url.Contains("/IWorktreeApi/getOverviewHistory"))

            do! toggle.ClickAsync()
            let! _ = firstResponse
            do! Assertions.Expect(toggle).ToHaveTextAsync("\u25F7 12h")
            do! Assertions.Expect(charts).ToHaveCountAsync(2)
            do! Assertions.Expect(firstAxisLabel).ToHaveTextAsync("-12h")

            let firstChart = charts.First
            let! _ = firstChart.EvaluateAsync<string>("chart => chart.dataset.switchSentinel = 'mounted'")
            let! beforeSwitch = firstChart.BoundingBoxAsync()
            Assert.That(beforeSwitch, Is.Not.Null)

            do! toggle.ClickAsync()
            let! staleRoute = delayed24.Task.WaitAsync(TimeSpan.FromSeconds 5.0)
            do! Assertions.Expect(toggle).ToHaveTextAsync("\u25F7 24h")
            do! Assertions.Expect(firstAxisLabel).ToHaveTextAsync("-12h")
            do! Assertions.Expect(firstChart).ToHaveAttributeAsync("data-switch-sentinel", "mounted")

            let! during24 = firstChart.BoundingBoxAsync()
            Assert.That(during24, Is.Not.Null)
            Assert.That(during24.Height, Is.EqualTo(beforeSwitch.Height).Within(0.5), "chart height stays stable while 24h is pending")

            do! toggle.ClickAsync()
            let! currentRoute = delayed72.Task.WaitAsync(TimeSpan.FromSeconds 5.0)
            do! Assertions.Expect(toggle).ToHaveTextAsync("\u25F7 72h")
            do! Assertions.Expect(firstAxisLabel).ToHaveTextAsync("-12h")

            let! during72 = firstChart.BoundingBoxAsync()
            Assert.That(during72, Is.Not.Null)
            Assert.That(during72.Height, Is.EqualTo(beforeSwitch.Height).Within(0.5), "chart height stays stable during rapid switching")

            let staleResponse =
                this.Page.WaitForResponseAsync(fun response ->
                    response.Url.Contains("/IWorktreeApi/getOverviewHistory"))

            do!
                staleRoute.FulfillAsync(
                    RouteFulfillOptions(
                        ContentType = "application/json",
                        Body = historyBody 24
                    )
                )

            let! _ = staleResponse
            let! _ =
                this.Page.EvaluateAsync<bool>(
                    "() => new Promise(resolve => requestAnimationFrame(() => requestAnimationFrame(() => resolve(true))))"
                )

            do! Assertions.Expect(firstAxisLabel).ToHaveTextAsync("-12h")
            do! Assertions.Expect(firstChart).ToHaveAttributeAsync("data-switch-sentinel", "mounted")

            let currentResponse =
                this.Page.WaitForResponseAsync(fun response ->
                    response.Url.Contains("/IWorktreeApi/getOverviewHistory"))

            do!
                currentRoute.FulfillAsync(
                    RouteFulfillOptions(
                        ContentType = "application/json",
                        Body = historyBody 72
                    )
                )

            let! _ = currentResponse
            do! Assertions.Expect(firstAxisLabel).ToHaveTextAsync("-72h")
            do! Assertions.Expect(firstChart).ToHaveAttributeAsync("data-switch-sentinel", "mounted")
            do! Assertions.Expect(firstChart).ToHaveAttributeAsync("data-geometry-build-count", "2")
        }

    [<Test>]
    member this.``History refresh preserves and re-samples the visible hover``() =
        task {
            let toggle = this.Page.Locator(".overview-band .history-toggle")
            let charts = this.Page.Locator(".overview-band .history-charts")
            let firstChart = charts.First
            let svg = firstChart.Locator("svg")
            let cursor = firstChart.Locator(".cursor-line")
            let total = firstChart.Locator(".tip-total")
            let initialAnchor = DateTimeOffset.UtcNow - TimeSpan.FromSeconds 31.0
            let refreshedAnchor = DateTimeOffset.UtcNow

            let historyBody anchor changeAgo beforeCount changeCount latestCount =
                let response: OverviewHistoryResponse =
                    { Anchor = anchor
                      Snapshots =
                        [ { Timestamp = anchor - TimeSpan.FromHours 12.0
                            Tasks = [ { Kind = TaskBucketKind.Done; Count = beforeCount } ]
                            Agents = [ { Kind = AgentGroupKind.Idle; Count = beforeCount } ] }
                          { Timestamp = anchor - changeAgo
                            Tasks = [ { Kind = TaskBucketKind.Done; Count = changeCount } ]
                            Agents = [ { Kind = AgentGroupKind.Idle; Count = changeCount } ] }
                          { Timestamp = anchor
                            Tasks = [ { Kind = TaskBucketKind.Done; Count = latestCount } ]
                            Agents = [ { Kind = AgentGroupKind.Idle; Count = latestCount } ] } ] }

                Newtonsoft.Json.JsonConvert.SerializeObject(
                    response,
                    Fable.Remoting.Json.FableJsonConverter()
                )

            let requestCount = ref 0
            let delayedRefresh = System.Threading.Tasks.TaskCompletionSource<IRoute>()

            do!
                this.Page.RouteAsync(
                    "**/IWorktreeApi/getOverviewHistory",
                    fun route ->
                        requestCount.Value <- requestCount.Value + 1

                        match requestCount.Value with
                        | 1 ->
                            route.FulfillAsync(
                                RouteFulfillOptions(
                                    ContentType = "application/json",
                                    Body = historyBody initialAnchor (TimeSpan.FromHours 6.0) 1 2 3
                                )
                            )
                        | 2 ->
                            delayedRefresh.TrySetResult(route) |> ignore
                            System.Threading.Tasks.Task.CompletedTask
                        | _ -> route.AbortAsync()
                )

            let initialResponse =
                this.Page.WaitForResponseAsync(fun response ->
                    response.Url.Contains("/IWorktreeApi/getOverviewHistory"))

            do! toggle.ClickAsync()
            let! _ = initialResponse
            do! Assertions.Expect(charts).ToHaveCountAsync(2)
            let! refreshRoute = delayedRefresh.Task.WaitAsync(TimeSpan.FromSeconds 5.0)

            do! svg.ScrollIntoViewIfNeededAsync()
            let! svgBox = svg.BoundingBoxAsync()
            Assert.That(svgBox, Is.Not.Null)

            do!
                svg.HoverAsync(
                    LocatorHoverOptions(
                        Position =
                            Microsoft.Playwright.Position(
                                X = svgBox.Width * 0.65f,
                                Y = svgBox.Height / 2.0f
                            )
                    )
                )

            do! Assertions.Expect(cursor).ToHaveAttributeAsync("x1", "393")
            do! Assertions.Expect(total).ToHaveTextAsync("Total: 2")

            let refreshedResponse =
                this.Page.WaitForResponseAsync(fun response ->
                    response.Url.Contains("/IWorktreeApi/getOverviewHistory"))

            do!
                refreshRoute.FulfillAsync(
                    RouteFulfillOptions(
                        ContentType = "application/json",
                        Body = historyBody refreshedAnchor (TimeSpan.FromHours 4.8) 4 8 9
                    )
                )

            let! _ = refreshedResponse
            do! Assertions.Expect(firstChart).ToHaveAttributeAsync("data-geometry-build-count", "2")
            do! Assertions.Expect(cursor).ToHaveAttributeAsync("x1", "465")
            do! Assertions.Expect(total).ToHaveTextAsync("Total: 8")
        }

    [<Test>]
    member this.``History tooltip stays aligned with the crosshair at both plot edges``() =
        task {
            let toggle = this.Page.Locator(".overview-band .history-toggle")
            let firstChart = this.Page.Locator(".overview-band .history-charts").First
            let svg = firstChart.Locator("svg")
            let cursor = firstChart.Locator(".cursor-line")
            let anchor = DateTimeOffset.UtcNow

            let historyResponse: OverviewHistoryResponse =
                { Anchor = anchor
                  Snapshots =
                    [ { Timestamp = anchor - TimeSpan.FromHours 12.0
                        Tasks = [ { Kind = TaskBucketKind.Done; Count = 1 } ]
                        Agents = [ { Kind = AgentGroupKind.Idle; Count = 1 } ] }
                      { Timestamp = anchor
                        Tasks = [ { Kind = TaskBucketKind.Done; Count = 2 } ]
                        Agents = [ { Kind = AgentGroupKind.Idle; Count = 2 } ] } ] }

            let historyBody =
                Newtonsoft.Json.JsonConvert.SerializeObject(
                    historyResponse,
                    Fable.Remoting.Json.FableJsonConverter()
                )

            do!
                this.Page.RouteAsync(
                    "**/IWorktreeApi/getOverviewHistory",
                    fun route ->
                        route.FulfillAsync(
                            RouteFulfillOptions(ContentType = "application/json", Body = historyBody)
                        )
                )

            let historyRequest =
                this.Page.WaitForResponseAsync(fun response ->
                    response.Url.Contains("/IWorktreeApi/getOverviewHistory"))
            do! toggle.ClickAsync()
            let! _ = historyRequest
            do! Assertions.Expect(toggle).ToHaveTextAsync("\u25F7 12h")
            do! Assertions.Expect(firstChart).ToHaveCountAsync(1)

            do! svg.ScrollIntoViewIfNeededAsync()
            let! svgBox = svg.BoundingBoxAsync()
            Assert.That(svgBox, Is.Not.Null)

            let probeAlignment () =
                task {
                    let! json =
                        firstChart.EvaluateAsync<string>(
                            """chart => {
                              const stage = chart.querySelector('.chart-stage').getBoundingClientRect();
                              const svg = chart.querySelector('svg');
                              const svgRect = svg.getBoundingClientRect();
                              const viewBox = svg.viewBox.baseVal;
                              const cursorX = Number(chart.querySelector('.cursor-line').getAttribute('x1'));
                              const crosshairX = svgRect.left + (cursorX - viewBox.x) / viewBox.width * svgRect.width;
                              const tip = chart.querySelector('.chart-tip').getBoundingClientRect();
                              return JSON.stringify({
                                leftDelta: Math.abs(tip.left - crosshairX),
                                rightDelta: Math.abs(tip.right - crosshairX),
                                contained: tip.left >= stage.left - 1 && tip.right <= stage.right + 1
                              });
                            }""")

                    return JObject.Parse(json)
                }

            do!
                svg.HoverAsync(
                    LocatorHoverOptions(
                        Position = Microsoft.Playwright.Position(X = 1.0f, Y = svgBox.Height / 2.0f)
                    )
                )
            do! Assertions.Expect(cursor).ToHaveAttributeAsync("x1", "34")
            let! left = probeAlignment ()
            Assert.That(left.Value<float>("leftDelta"), Is.LessThanOrEqualTo(1.5))
            Assert.That(left.Value<bool>("contained"), Is.True)

            do!
                svg.HoverAsync(
                    LocatorHoverOptions(
                        Position =
                            Microsoft.Playwright.Position(
                                X = svgBox.Width - 1.0f,
                                Y = svgBox.Height / 2.0f
                            )
                    )
                )
            do! Assertions.Expect(cursor).ToHaveAttributeAsync("x1", "752")
            let! right = probeAlignment ()
            Assert.That(right.Value<float>("rightDelta"), Is.LessThanOrEqualTo(1.5))
            Assert.That(right.Value<bool>("contained"), Is.True)
        }
