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
            do! this.Page.Locator(".overview-agents-band").WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))
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
    member this.``One agent band morphs to pinned circles and closes the drill-down``() =
        task {
            let canvasBtn =
                this.Page.Locator(".header-controls .ctrl-btn", PageLocatorOptions(HasText = "Canvas"))
            do! canvasBtn.ClickAsync()
            do! this.Page.Locator(".canvas-tab-bar").WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            let investigating =
                this.Page.Locator(".overview-agents-band .overview-item", PageLocatorOptions(HasText = "Investigating"))
            do! investigating.ClickAsync()
            do! this.Page.Locator(".overview-breakdown").WaitForAsync(LocatorWaitForOptions(Timeout = 5000.0f))

            let! morphJson =
                this.Page.EvaluateAsync<string>(
                    """() => {
                      const dashboard = document.querySelector('.dashboard');
                      const agents = document.querySelector('.overview-agents-band');
                      const morphRange = parseFloat(getComputedStyle(dashboard).getPropertyValue('--overview-agents-morph-range'));
                      dashboard.scrollTop = morphRange / 2;
                      return new Promise(resolve =>
                        requestAnimationFrame(() => requestAnimationFrame(() =>
                          resolve(JSON.stringify({
                            bandCount: document.querySelectorAll('.overview-agents-band').length,
                            compactCount: document.querySelectorAll('.overview-agents-compact').length,
                            headerOpacity: parseFloat(getComputedStyle(agents.querySelector('.overview-header')).opacity),
                            metaOpacity: parseFloat(getComputedStyle(agents.querySelector('.overview-meta')).opacity)
                          })))));
                    }""")

            let morph = JObject.Parse(morphJson)
            Assert.That(morph.Value<int>("bandCount"), Is.EqualTo(1))
            Assert.That(morph.Value<int>("compactCount"), Is.EqualTo(0))
            Assert.That(morph.Value<float>("headerOpacity"), Is.InRange(0.1, 0.9))
            Assert.That(morph.Value<float>("metaOpacity"), Is.InRange(0.1, 0.9))

            let! _ =
                this.Page.EvaluateAsync(
                    """() => {
                      const dashboard = document.querySelector('.dashboard');
                      const morphRange = parseFloat(getComputedStyle(dashboard).getPropertyValue('--overview-agents-morph-range'));
                      dashboard.scrollTop = morphRange + 1;
                      window.__overviewStickyScrollTop = dashboard.scrollTop;
                    }""")

            let! _ =
                this.Page.WaitForFunctionAsync(
                    """() => {
                      const agents = document.querySelector('.overview-agents-band');
                      const meta = agents.querySelector('.overview-meta');
                      return parseFloat(getComputedStyle(meta).opacity) < 0.01
                        && !document.querySelector('.overview-breakdown')
                        && !document.querySelector('.overview-item-selected');
                    }""",
                    null,
                    PageWaitForFunctionOptions(Timeout = 5000.0f))

            let! stickyJson =
                this.Page.EvaluateAsync<string>(
                    """() => new Promise(resolve => {
                      requestAnimationFrame(() => requestAnimationFrame(() => {
                        const dashboard = document.querySelector('.dashboard');
                        const agents = document.querySelector('.overview-agents-band');
                        const canvasHeader = document.querySelector('.canvas-tab-bar');
                        const dashboardRect = dashboard.getBoundingClientRect();
                        const circleCenters = Array.from(agents.querySelectorAll('.overview-circle'))
                          .map(circle => {
                            const rect = circle.getBoundingClientRect();
                            return rect.top + rect.height / 2 - dashboardRect.top;
                          });
                        const transform = new DOMMatrix(getComputedStyle(agents).transform);
                        const line = getComputedStyle(agents, '::after');
                        resolve(JSON.stringify({
                          scrollTop: dashboard.scrollTop,
                          expectedScrollTop: window.__overviewStickyScrollTop,
                          position: getComputedStyle(agents).position,
                          canvasHeight: canvasHeader.getBoundingClientRect().height,
                          visualHeight: transform.m42 + parseFloat(line.top) + 1,
                          minCircleCenter: Math.min(...circleCenters),
                          maxCircleCenter: Math.max(...circleCenters),
                          headerOpacity: parseFloat(getComputedStyle(agents.querySelector('.overview-header')).opacity),
                          metaOpacity: parseFloat(getComputedStyle(agents.querySelector('.overview-meta')).opacity),
                          lineOpacity: parseFloat(line.opacity),
                          lineColor: line.borderBottomColor,
                          groupGap: getComputedStyle(agents.querySelector('.overview-items')).columnGap,
                          canvasBorderColor: getComputedStyle(canvasHeader).borderBottomColor
                        }));
                      }));
                    })""")

            let sticky = JObject.Parse(stickyJson)
            Assert.That(sticky.Value<float>("scrollTop"), Is.EqualTo(sticky.Value<float>("expectedScrollTop")).Within(1.0), "pinning must not move the scroll position")
            Assert.That(sticky.Value<string>("position"), Is.EqualTo("sticky"))
            Assert.That(sticky.Value<float>("visualHeight"), Is.EqualTo(sticky.Value<float>("canvasHeight")).Within(1.0), "agent and canvas chrome heights match")
            Assert.That(sticky.Value<float>("minCircleCenter"), Is.EqualTo(sticky.Value<float>("maxCircleCenter")).Within(1.0), "all circle groups share one compact row")
            Assert.That(sticky.Value<float>("minCircleCenter"), Is.EqualTo(sticky.Value<float>("canvasHeight") / 2.0).Within(1.5), "circles are vertically centered")
            Assert.That(sticky.Value<float>("headerOpacity"), Is.LessThan(0.01))
            Assert.That(sticky.Value<float>("metaOpacity"), Is.LessThan(0.01))
            Assert.That(sticky.Value<float>("lineOpacity"), Is.EqualTo(1.0).Within(0.01))
            Assert.That(sticky.Value<string>("lineColor"), Is.EqualTo(sticky.Value<string>("canvasBorderColor")))
            Assert.That(sticky.Value<string>("groupGap"), Is.EqualTo("22px"))

            do! investigating.Locator(".overview-circle").First.ClickAsync()
            let! _ =
                this.Page.WaitForFunctionAsync(
                    """() => {
                      const dashboard = document.querySelector('.dashboard');
                      return dashboard.scrollTop <= 0.5
                        && !!document.querySelector('.overview-agents-band .overview-item-selected')
                        && !!document.querySelector('.overview-breakdown');
                    }""",
                    null,
                    PageWaitForFunctionOptions(Timeout = 5000.0f))
            ()
        }
