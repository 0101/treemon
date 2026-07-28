/// The E2E fixture for per-repository diff categories: nesting and counts, the initial disclosure
/// rules, category-aware selection, and the configure action. Split out of `DiffViewerTests` over
/// the shared `DiffViewerHarness`.
module Tests.DiffCategoryTests

open System
open System.Threading.Tasks
open Microsoft.Playwright
open NUnit.Framework
open Tests.DiffViewerTestHarness

[<TestFixture>]
[<Category("E2E")>]
[<Category("Canvas")>]
type DiffCategoryE2ETests() =
    inherit DiffViewerHarness()

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

            do! this.RouteHighlighter()
            do! this.RouteSummaries(summaries)
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
            do! this.RouteSummaries(summaries)
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

            do! this.RouteSummaries(summaries)
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
                        const originalFetch = window.fetch;
                        window.fetch = function(input) {
                            const url = typeof input === 'string' ? input : input.url;
                            const request = originalFetch.apply(this, arguments);
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
    member this.``activating the configure action shows a spinner and blocks a second request``() =
        task {
            do! this.RouteSummary(readySummaryJson [| firstFile |])
            do!
                this.RouteCategorizations(
                    [| categorizationBody "missing" None "rev-missing" |]
                )
            do! this.GotoEmbedded(pageUrl)

            let action = this.EmbeddedFrame().Locator(configureSelector)
            do! action.WaitForAsync()

            let! before =
                action.EvaluateAsync<string array>(
                    """action => [
                        String(action.disabled),
                        String(action.getAttribute('aria-busy')),
                        String(getComputedStyle(action.querySelector('.toolbar-icon')).display)
                    ]"""
                )

            do! this.ActivateConfigure(action)

            let! during =
                action.EvaluateAsync<string array>(
                    """action => [
                        String(action.disabled),
                        String(action.getAttribute('aria-busy')),
                        String(getComputedStyle(action.querySelector('.toolbar-icon')).display),
                        String(getComputedStyle(action, '::after').animationName),
                        String(action.title)
                    ]"""
                )

            // A disabled control ignores a click, so a second attempt must not reach the transport.
            do! action.DispatchEventAsync("click")
            do! this.Page.WaitForTimeoutAsync(200f)

            let! sent =
                this.Page.EvaluateAsync<int>(
                    "() => window.__canvasMessages.length"
                )

            Assert.Multiple(fun () ->
                Assert.That(
                    before,
                    Is.EqualTo([| "false"; "null"; "block" |]),
                    "the control must start enabled with its icon showing"
                )
                Assert.That(
                    during,
                    Is.EqualTo(
                        [| "true"
                           "true"
                           "none"
                           "spin"
                           "Configuring diff groups — waiting for the agent…" |]
                    ),
                    "an outstanding request must disable the control and replace its icon with the spinner"
                )
                Assert.That(sent, Is.EqualTo(1), "a second activation must not post another request"))
        }

    [<Test>]
    member this.``a rewritten configuration ends the wait and reloads the grouping``() =
        task {
            do!
                this.RouteSummaries(
                    [| summaryJsonWithCategorization
                           (categorizationJsonAt "missing" None "rev-1")
                           2
                           3
                           1
                           [| firstFile |]
                       summaryJsonWithCategorization
                           (categorizationJsonAt "configured" None "rev-2")
                           2
                           3
                           1
                           (categoryFiles [ "Client" ] 1) |]
                )

            // Unchanged on the first poll, rewritten on the next: the viewer must wait for the
            // change rather than refreshing on the first answer it gets.
            do!
                this.RouteCategorizations(
                    [| categorizationBody "missing" None "rev-1"
                       categorizationBody "configured" None "rev-2" |]
                )

            do! this.GotoEmbedded(pageUrl)
            let frame = this.EmbeddedFrame()
            let action = frame.Locator(configureSelector)
            do! action.WaitForAsync()
            do! this.ActivateConfigure(action)

            // The grouping the agent produced appears without the user pressing Refresh.
            do! frame.Locator(".category-entry").First.WaitForAsync()

            let! settled =
                action.EvaluateAsync<string array>(
                    """action => [
                        String(action.disabled),
                        String(action.getAttribute('aria-busy')),
                        String(document.querySelectorAll('.category-entry').length > 0)
                    ]"""
                )

            Assert.That(
                settled,
                Is.EqualTo([| "false"; "false"; "true" |]),
                "an observed rewrite must clear the waiting state and render the new grouping"
            )
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
                            mentions('"**"'),
                            mentions('displayed to the reader, top to bottom'),
                            mentions('tm categories')
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

            let! placement =
                frame.Locator("#category-warning").EvaluateAsync<string array>(
                    """(warning, selector) => [
                        warning.textContent,
                        String(warning.querySelectorAll(selector).length),
                        String(document.querySelectorAll(selector).length)
                    ]""",
                    configureSelector
                )

            // Captured before navigating: reloading the host clears its record of posted messages.
            let! warningPayload =
                this.Page.EvaluateAsync<string>(
                    "() => JSON.stringify(window.__canvasMessages[0])"
                )

            // Activating one control starts the shared wait and disables both, so the toolbar's copy
            // is compared from a fresh page instance rather than by clicking it in this one.
            do! this.GotoEmbedded(pageUrl)
            let toolbarAction =
                this.EmbeddedFrame().Locator($".toolbar {configureSelector}")
            do! toolbarAction.WaitForAsync()
            do! this.ActivateConfigure(toolbarAction)

            let! toolbarPayload =
                this.Page.EvaluateAsync<string>(
                    "() => JSON.stringify(window.__canvasMessages[0])"
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
                Assert.That(warningPayload, Does.Contain("configure-diff-categories"))
                Assert.That(toolbarPayload, Is.EqualTo(warningPayload)))
        }
