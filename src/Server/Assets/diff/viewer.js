var VIEW_KEY = 'treemon.diff.view';
var SELECTION_KEY = 'treemon.diff.selection:' + location.pathname.replace(/\/diff\.html$/, '');
var FILTER_KEY = 'treemon.diff.layers:' + location.pathname.replace(/\/diff\.html$/, '');
var HIGHLIGHTER_URL = '/assets/diff2html/3.4.52/diff2html-ui-slim.min.js';
var OTHER_CATEGORY = 'Other';
// Joins the names of a flattened single-child chain into one label, matching how `tm categories`
// writes a root-to-leaf path.
var CATEGORY_CHAIN_SEPARATOR = ' > ';
var CATEGORY_WARNING = 'Diff groups are not applied';
var CONFIGURE_ACTION = 'configure-diff-categories';
var CONFIGURE_LABEL = 'Analyze repository and configure diff groups';
// The whole request, fixed in the template. Nothing in it is derived from the repository, the
// document URL, or any other browser-side value, so the generated diff.html stays byte-identical
// across repositories and the agent — not the browser — decides which paths exist.
var CONFIGURE_REQUEST = [
    'Analyze this repository and configure its diff groups for the Treemon worktree diff viewer.',
    '',
    'Locate the root worktree that owns the shared .treemon.json — linked worktrees read the repository root\'s file — and edit that file in place. Preserve every existing field exactly as it is and modify only the "diffCategories" key.',
    '',
    '"diffCategories" is a non-empty ordered array of category nodes. A node has a "name" string plus exactly one of "patterns" (a non-empty array of glob strings) or "children" (a non-empty array of nodes) — never both, and never an empty array. Order is meaningful in two ways: leaves are visited depth-first and the first matching leaf wins, and the array order is also the order the groups are displayed to the reader, top to bottom, at every level.',
    '',
    'Rules:',
    '- Nest at most 4 levels deep.',
    '- Keep sibling names non-blank and unique.',
    '- Do not use "Other" as a top-level name; Treemon reserves it for the trailing group of unmatched files.',
    '- Patterns are repository-relative and match the whole path. The supported glob subset is literal text, "?" for one character other than "/", "*" for any run of characters within one path segment, and "**" for zero or more whole path segments. No other metacharacter is special, and regular expressions are not accepted.',
    '- Write patterns that describe a place and a shape — a directory prefix, a file extension, a naming convention — instead of listing individual file names. A group spelled out as today\'s file list stops being correct the moment someone adds a file.',
    '- Wherever you split an area into subcategories, end that area with a group whose pattern covers the rest of it, so a file added there later joins its own area instead of leaving it. That scoped fallback is expected; a repository-wide catch-all is not.',
    '- Do not write repository-wide catch-all patterns; Treemon already collects everything unmatched under "Other". "Other" is for paths that belong to no area you declared, never for a new file inside an area you did declare.',
    '',
    'Cover the repository\'s production, test, documentation, and instruction areas, nesting production code into subcategories where the layout makes that meaningful, and order every level so that someone reviewing a change reads what matters most about it first and the incidental parts of it last.',
    '',
    'Then verify the result instead of trusting it: run `tm categories` inside the repository. It fails when the configuration is missing or invalid, and otherwise prints every declared group in order with the number of tracked files it matches, followed by how many files no group matched. Let that command tell you what a pattern really covers rather than working the matching out by hand. A group matching nothing is a mistake to fix; a large unmatched count means the patterns are missing part of the repository, so widen them or add a real group — never a repository-wide catch-all. The counts come from the files tracked today, so they show that a pattern matches something, not that it will still be right after a file is added: judge each pattern on the place it describes and use the counts to catch the ones that match nothing or far more than you meant. Re-run the check after each edit, and report the final per-group counts together with the unmatched count.'
].join('\n');
// The one size that decides every initial disclosure: a group holding more than this many files, or
// a branch with more direct children than this, opens as a header instead of exposed rows.
var CATEGORY_DISCLOSURE_LIMIT = 5;
// How the viewer waits for the agent to write `diffCategories`. Analyzing a repository is a
// multi-minute job, so the window is generous; the poll only reads and validates `.treemon.json`,
// never a diff, so the cadence costs the server almost nothing.
var CONFIGURE_POLL_MS = 2500;
var CONFIGURE_TIMEOUT_MS = 600000;
var CONFIGURE_PENDING_LABEL = 'Configuring diff groups — waiting for the agent…';
var VIEWER_ID = crypto.randomUUID();
var state = {
    summary: null,
    selected: null,
    currentResult: null,
    currentPatch: null,
    view: readViewPreference(),
    filters: readFilterPreference(),
    fileRequest: 0,
    fileAbort: null,
    summaryRequest: 0,
    selectedButton: null,
    panel: null,
    // Explicit expand/collapse choices for this page instance only, keyed by a JSON-serialized
    // category path so a name containing a delimiter cannot collide with a different path. Never
    // written to browser storage: a reload deliberately returns to the computed defaults.
    categoryToggles: new Map(),
    // Set while an agent has been asked to write `diffCategories` and the viewer is watching for the
    // result. Held in state rather than on the button because the warning's copy of the control is
    // recreated on every render and must come back still pending.
    configurePending: false,
    configurePoll: null
};
var highlighterPromise = null;

function readStorage(key) {
    try { return localStorage.getItem(key); } catch (_) { return null; }
}

function writeStorage(key, value) {
    try { localStorage.setItem(key, value); } catch (_) {}
}

function removeStorage(key) {
    try { localStorage.removeItem(key); } catch (_) {}
}

function readViewPreference() {
    return readStorage(VIEW_KEY) === 'split' ? 'split' : 'unified';
}

function defaultFilters() {
    return { committed: true, local: true, untracked: false };
}

function readFilterPreference() {
    var stored = readStorage(FILTER_KEY);
    if (!stored) return defaultFilters();
    try {
        var parsed = JSON.parse(stored);
        if (
            typeof parsed.committed === 'boolean' &&
            typeof parsed.local === 'boolean' &&
            typeof parsed.untracked === 'boolean'
        ) {
            return {
                committed: parsed.committed,
                local: parsed.local,
                untracked: parsed.untracked
            };
        }
    } catch (_) {}
    return defaultFilters();
}

function updateFilterInputs() {
    document.getElementById('filter-committed').checked = state.filters.committed;
    document.getElementById('filter-local').checked = state.filters.local;
    document.getElementById('filter-untracked').checked = state.filters.untracked;
}

function layerCountPresentation(result) {
    if (!result || result.status === 'git-error') {
        return { text: 'unavailable', title: 'File count unavailable because Git failed.' };
    }
    if (result.status === 'base-error') {
        return { text: 'unavailable', title: 'File count unavailable because the comparison base could not be resolved.' };
    }
    if (result.status === 'timeout') {
        return { text: 'unavailable', title: 'File count unavailable because Git timed out.' };
    }
    return {
        text: String(result.fileCount),
        title: result.fileCount + (result.fileCount === 1 ? ' file' : ' files')
    };
}

function applyLayerCount(name, result) {
    var input = document.getElementById('filter-' + name);
    var count = document.getElementById('count-' + name);
    var presentation = layerCountPresentation(result);
    count.textContent = '(' + presentation.text + ')';
    count.title = presentation.title;
    input.disabled = Boolean(result && result.status === 'ready' && result.fileCount === 0);
}

function applyLayerCounts(counts) {
    counts = counts || {};
    applyLayerCount('committed', counts.committed);
    applyLayerCount('local', counts.local);
    applyLayerCount('untracked', counts.untracked);
}

function summaryUrl() {
    var query = new URLSearchParams({
        committed: String(state.filters.committed),
        local: String(state.filters.local),
        untracked: String(state.filters.untracked)
    });
    return 'diff-summary?' + query.toString();
}

function filtersChanged() {
    state.filters = {
        committed: document.getElementById('filter-committed').checked,
        local: document.getElementById('filter-local').checked,
        untracked: document.getElementById('filter-untracked').checked
    };
    writeStorage(FILTER_KEY, JSON.stringify(state.filters));
    loadSummary();
}

function fileSelectionKey(file) {
    return JSON.stringify([file.change, file.oldDisplayPath || null, file.displayPath]);
}

function configForView() {
    return {
        drawFileList: false,
        matching: 'lines',
        outputFormat: state.view === 'split' ? 'side-by-side' : 'line-by-line',
        colorScheme: 'dark',
        renderNothingWhenEmpty: false
    };
}

function updateViewButtons() {
    document.getElementById('unified-view').setAttribute('aria-pressed', String(state.view === 'unified'));
    document.getElementById('split-view').setAttribute('aria-pressed', String(state.view === 'split'));
}

function setView(view) {
    if (state.view === view) return;
    state.view = view;
    writeStorage(VIEW_KEY, view);
    updateViewButtons();
    if (state.currentResult && isRenderablePatch(state.currentResult)) {
        renderPatch(state.currentResult.patch, state.currentResult.replacement);
    }
}

function replaceContent(node) {
    (state.panel || document.getElementById('summary-state')).replaceChildren(node);
}

function renderState(status, title, detail, loading) {
    state.currentResult = null;
    state.currentPatch = null;
    var card = document.createElement('div');
    card.className = 'state-card';
    card.dataset.state = status;
    if (loading) {
        var spinner = document.createElement('div');
        spinner.className = 'spinner';
        spinner.setAttribute('aria-hidden', 'true');
        card.appendChild(spinner);
    }
    var heading = document.createElement('div');
    heading.className = 'state-title';
    heading.textContent = title;
    card.appendChild(heading);
    if (detail) {
        var body = document.createElement('div');
        body.className = 'state-detail';
        body.textContent = detail;
        card.appendChild(body);
    }
    replaceContent(card);
}

function clearNavigator() {
    state.summary = null;
    state.selected = null;
    state.currentResult = null;
    state.currentPatch = null;
    state.selectedButton = null;
    state.panel = null;
    document.getElementById('file-list').replaceChildren();
    document.getElementById('category-warning').replaceChildren();
    document.getElementById('summary-state').replaceChildren();
    document.getElementById('change-summary').replaceChildren();
    document.getElementById('change-summary').removeAttribute('aria-label');
}

function changePresentation(change) {
    return {
        added: { symbol: '+', label: 'Added file' },
        modified: { symbol: '~', label: 'Modified file' },
        deleted: { symbol: '−', label: 'Deleted file' },
        renamed: { symbol: '→', label: 'Renamed file' },
        untracked: { symbol: '+', label: 'Untracked file' }
    }[change] || { symbol: '?', label: 'Changed file' };
}

function fileEntries() {
    return Array.from(document.querySelectorAll('#file-list .file-entry'));
}

// The category headers a node sits under, innermost first. A flat list nests nothing, so every row
// there has an empty chain and is trivially reachable.
function ancestorCategoryHeaders(node) {
    var headers = [];
    var panel = node.closest('.category-panel');
    while (panel) {
        headers.push(panel.previousElementSibling);
        panel = panel.parentElement.closest('.category-panel');
    }
    return headers;
}

// Category disclosure decides what a reader can reach, so navigation reads the same source of truth
// the CSS does: a row counts as visible only while every ancestor header is expanded.
function visibleFileEntries() {
    return fileEntries().filter(function(entry) {
        return ancestorCategoryHeaders(entry).every(function(header) {
            return header.getAttribute('aria-expanded') === 'true';
        });
    });
}

function showFileActions(path) {
    var range = document.createRange();
    range.selectNodeContents(path);
    var selection = window.getSelection();
    selection.removeAllRanges();
    selection.addRange(range);
    document.dispatchEvent(new Event('selectionchange'));
}

function renderFileList(files) {
    var list = document.getElementById('file-list');
    list.replaceChildren.apply(list, files.map(createFileItem));
}

// Line stats a change actually carries. A binary entry or a pure rename reports none, so one rule
// decides both whether a file row shows numbers and what a category header's subtree sum counts.
function fileLineStats(file) {
    if (!Number.isInteger(file.linesAdded) || !Number.isInteger(file.linesRemoved)) return null;
    return { added: file.linesAdded, removed: file.linesRemoved };
}

// The +X/−Y pair, built the same way for a file row and for a category header's subtree sum so the
// two levels read as one scale. A zero side is omitted and nothing at all is rendered when neither
// side has lines to report; the colour classes name the number, not the level that shows it.
function createLineStats(className, stats) {
    if (!stats || (stats.added <= 0 && stats.removed <= 0)) return null;

    var element = document.createElement('span');
    element.className = className;
    var labels = [];

    if (stats.added > 0) {
        var added = document.createElement('span');
        added.className = 'file-lines-added';
        added.textContent = '+' + stats.added;
        labels.push(stats.added + ' lines added');
        element.appendChild(added);
    }

    if (stats.removed > 0) {
        var removed = document.createElement('span');
        removed.className = 'file-lines-removed';
        removed.textContent = '−' + stats.removed;
        labels.push(stats.removed + ' lines removed');
        element.appendChild(removed);
    }

    element.setAttribute('aria-label', labels.join(', '));
    return element;
}

function createFileItem(file, index) {
    var item = document.createElement('section');
    item.className = 'file-item';
    item.dataset.identity = file.identity;

    var heading = document.createElement('h2');
    heading.className = 'file-heading';

    var button = document.createElement('button');
    button.type = 'button';
    button.className = 'file-entry';
    button.dataset.identity = file.identity;
    button.id = 'file-header-' + index;
    button.setAttribute('aria-expanded', 'false');
    button.setAttribute('aria-controls', 'file-panel-' + index);
    button.title = file.oldDisplayPath
        ? file.oldDisplayPath + ' → ' + file.displayPath
        : file.displayPath;

    var presentation = changePresentation(file.change);
    var badge = document.createElement('span');
    badge.className = 'change-badge ' + file.change;
    badge.textContent = presentation.symbol;
    badge.setAttribute('aria-label', presentation.label);
    badge.title = presentation.label;
    button.appendChild(badge);

    var path = document.createElement('span');
    path.className = 'file-path';
    path.textContent = file.displayPath;
    button.appendChild(path);

    var stats = createLineStats('file-stats', fileLineStats(file));

    if (file.oldDisplayPath) {
        var oldPath = document.createElement('span');
        oldPath.className = 'old-path';
        oldPath.textContent = 'from ' + file.oldDisplayPath;
        button.appendChild(oldPath);
    }

    button.addEventListener('keydown', function(event) {
        var buttons = visibleFileEntries();
        var current = buttons.indexOf(button);
        var target = null;
        if (event.key === 'ArrowDown') target = buttons[(current + 1) % buttons.length];
        else if (event.key === 'ArrowUp') target = buttons[(current - 1 + buttons.length) % buttons.length];
        else if (event.key === 'Home') target = buttons[0];
        else if (event.key === 'End') target = buttons[buttons.length - 1];
        if (target) {
            event.preventDefault();
            target.focus();
        }
    });

    var actionsButton = document.createElement('button');
    actionsButton.type = 'button';
    actionsButton.className = 'file-actions-button';
    actionsButton.setAttribute('aria-label', 'Actions for ' + file.displayPath);
    actionsButton.title = 'Actions for ' + file.displayPath;
    actionsButton.innerHTML =
        '<svg class="toolbar-icon" viewBox="0 0 16 16" fill="currentColor" aria-hidden="true" focusable="false">' +
        '<circle cx="3" cy="8" r="1"></circle>' +
        '<circle cx="8" cy="8" r="1"></circle>' +
        '<circle cx="13" cy="8" r="1"></circle>' +
        '</svg>';
    actionsButton.addEventListener('click', function(event) {
        event.stopPropagation();
        showFileActions(path);
    });
    heading.addEventListener('click', function() { selectFile(file, button, item); });

    heading.append(button, actionsButton);
    if (stats) heading.appendChild(stats);
    item.appendChild(heading);
    return item;
}

// Adds one file's line stats to a running subtree total. A change that reports none — a binary
// entry, a pure rename — contributes zero rather than breaking the sum.
function accumulateLineStats(totals, stats) {
    if (stats) {
        if (stats.added > 0) totals.added += stats.added;
        if (stats.removed > 0) totals.removed += stats.removed;
    }
    return totals;
}

// Rebuilds the tree of categories that actually contain changed files from the server-ordered
// category paths, so a configured category without changes never renders. Every category on a
// file's path counts it and adds its lines, so a branch header reports its whole subtree rather
// than its direct files. Unmatched files collect in the synthetic trailing Other group.
function categoryRoots(files) {
    var root = { children: new Map(), files: [], path: [] };

    files.forEach(function(file, index) {
        var path = Array.isArray(file.categoryPath) ? file.categoryPath : [];
        var stats = fileLineStats(file);
        var category = path.reduce(function(parent, name) {
            var child = parent.children.get(name);
            if (!child) {
                child = {
                    name: name,
                    path: parent.path.concat([name]),
                    children: new Map(),
                    files: [],
                    count: 0,
                    lines: { added: 0, removed: 0 }
                };
                parent.children.set(name, child);
            }
            child.count += 1;
            accumulateLineStats(child.lines, stats);
            return child;
        }, root);
        category.files.push({ file: file, index: index });
    });

    var configured = Array.from(root.children.values());
    if (!root.files.length) return configured;
    return configured.concat([
        {
            name: OTHER_CATEGORY,
            path: [OTHER_CATEGORY],
            children: new Map(),
            files: root.files,
            count: root.files.length,
            lines: root.files.reduce(function(totals, entry) {
                return accumulateLineStats(totals, fileLineStats(entry.file));
            }, { added: 0, removed: 0 })
        }
    ]);
}

// A run of categories that each hold exactly one child and no files of their own tells the reader
// nothing at each step: every level costs a row, an indent, and a click to reveal a single row. Such
// a run becomes one row carrying the joined names and the deepest category's contents, count, and
// path — that node is the one the row discloses, so the remembered toggle stays with those files
// even when a new sibling later breaks the run apart. Flattening runs before disclosure and
// rendering, so the disclosure rules, the indentation depth, and keyboard reachability all see the
// shape the reader sees.
function flattenCategoryChain(node) {
    var names = [node.name];
    var tip = node;
    while (tip.children.size === 1 && !tip.files.length) {
        tip = tip.children.values().next().value;
        names.push(tip.name);
    }
    return {
        names: names,
        path: tip.path,
        children: Array.from(tip.children.values(), flattenCategoryChain),
        files: tip.files,
        count: tip.count,
        lines: tip.lines
    };
}

// The intrinsic default of one category, independent of its ancestors: a leaf (including the
// synthetic Other group) opens while its files fit the limit, and a branch closes only when it has
// more direct children than the limit, so a wide branch shows a summary instead of a wall of headers.
function categoryOpensByDefault(node) {
    if (node.children.length) return node.children.length <= CATEGORY_DISCLOSURE_LIMIT;
    return node.files.length <= CATEGORY_DISCLOSURE_LIMIT;
}

// An expanded branch holding more files than the limit shows its children as headers only. The
// forcing reaches exactly one level and is computed from the intrinsic tree, so every descendant
// keeps the state it would have had without it and opening a forced category reveals its normal
// outline rather than a fully collapsed subtree.
function forcesChildrenCollapsed(node) {
    return categoryOpensByDefault(node) && node.count > CATEGORY_DISCLOSURE_LIMIT;
}

function categoryToggleKey(path) {
    return JSON.stringify(path);
}

function categoryExpanded(node, forcedCollapsed) {
    var explicit = state.categoryToggles.get(categoryToggleKey(node.path));
    if (explicit !== undefined) return explicit;
    return forcedCollapsed ? false : categoryOpensByDefault(node);
}

function createCategorySection(node, depth, forcedCollapsed) {
    var section = document.createElement('section');
    section.className = 'category-item';
    section.style.setProperty('--category-depth', String(depth));

    var label = node.names.join(CATEGORY_CHAIN_SEPARATOR);
    var countLabel = node.count + (node.count === 1 ? ' file' : ' files');
    var stats = createLineStats('category-stats', node.lines);

    var button = document.createElement('button');
    button.type = 'button';
    button.className = 'category-entry';
    button.setAttribute('aria-expanded', String(categoryExpanded(node, forcedCollapsed)));
    button.title = label;
    // A joined chain reads as one breadcrumb on screen, but its separators are punctuation, so the
    // accessible name spells the names out and carries the size the row shows beside them: how many
    // files the group holds and how many lines they change.
    button.setAttribute(
        'aria-label',
        node.names
            .concat([countLabel], stats ? [stats.getAttribute('aria-label')] : [])
            .join(', ')
    );

    var name = document.createElement('span');
    name.className = 'category-name';
    name.textContent = label;

    var count = document.createElement('span');
    count.className = 'category-count';
    count.textContent = String(node.count);

    button.append(name, count);
    if (stats) button.appendChild(stats);

    var panel = document.createElement('div');
    panel.className = 'category-panel';
    var forcesChildren = forcesChildrenCollapsed(node);
    var children = node.children.map(function(child) {
        return createCategorySection(child, depth + 1, forcesChildren);
    });
    var rows = node.files.map(function(entry) {
        return createFileItem(entry.file, entry.index);
    });
    panel.append.apply(panel, children.concat(rows));

    button.addEventListener('click', function() {
        var expanded = button.getAttribute('aria-expanded') !== 'true';
        button.setAttribute('aria-expanded', String(expanded));
        state.categoryToggles.set(categoryToggleKey(node.path), expanded);
        // Hiding the open file must not leave a selected-but-invisible patch behind, so the one
        // collapse path runs: it aborts the in-flight request, invalidates a late response, and
        // drops the remembered selection.
        if (!expanded && state.selectedButton && panel.contains(state.selectedButton)) {
            collapseFilePanel();
        }
    });

    section.append(button, panel);
    return section;
}

function renderCategoryTree(files) {
    var list = document.getElementById('file-list');
    // Top-level categories are evaluated individually; nothing forces them collapsed on behalf of
    // the diff as a whole.
    var sections = categoryRoots(files)
        .map(flattenCategoryChain)
        .map(function(node) {
            return createCategorySection(node, 1, false);
        });
    list.replaceChildren.apply(list, sections);
}

// The configure action only makes sense when this view is embedded in a pane that can carry the
// message to the worktree's agent session. A standalone top-level tab has no such parent, so the
// control is never rendered there rather than rendered dead.
function canvasTransportAvailable() {
    return window.parent !== window && typeof window.canvasSend === 'function';
}

// The single place the configure payload is built. Existing SystemView routing owns delivery,
// session startup, and the waiting/error banners, so this only posts the fixed request.
// The revision the repository has right now, for the case where no rendered summary carries one.
// Null when the read fails, which leaves the wait to establish its baseline from the first poll.
async function fetchCurrentRevision() {
    try {
        var current = await fetchJson('diff-categorization', { cache: 'no-store' });
        return current.revision;
    } catch (_) {
        return null;
    }
}

// The baseline is captured before the request is dispatched, not on the first poll: an agent can
// write `.treemon.json` within one poll interval, and a baseline read afterwards would already be
// the new configuration, so the change the viewer is waiting for would never be observed.
async function sendConfigureRequest() {
    if (state.configurePending) return;
    // Marked pending across the baseline read too, so a second activation during it cannot dispatch
    // a second request.
    state.configurePending = true;
    applyConfigurePending();

    var baseline = configuredRevision();
    if (baseline === null) baseline = await fetchCurrentRevision();

    // canvasSend reports only whether the message left the page. A dropped message never reaches an
    // agent, so entering the waiting state then would strand the control until it timed out.
    if (window.canvasSend(CONFIGURE_ACTION, { request: CONFIGURE_REQUEST }) === false) {
        endConfigureWait();
        return;
    }

    beginConfigureWait(baseline);
}

function configuredRevision() {
    var categorization = state.summary && state.summary.categorization;
    return categorization ? categorization.revision : null;
}

// Applies the waiting state to every configure control currently in the document. Both the toolbar
// and the warning render one, and a render can replace either, so the state is reapplied rather than
// remembered per element. The accessible name stays fixed — the control is still the same action;
// only its state changes — and `aria-busy` is what reports that.
function applyConfigurePending() {
    var buttons = document.querySelectorAll('.configure-button');
    for (var i = 0; i < buttons.length; i += 1) {
        var button = buttons[i];
        button.disabled = state.configurePending;
        button.setAttribute('aria-busy', String(state.configurePending));
        button.title = state.configurePending ? CONFIGURE_PENDING_LABEL : CONFIGURE_LABEL;
    }
}

function endConfigureWait() {
    if (state.configurePoll) clearInterval(state.configurePoll);
    state.configurePoll = null;
    state.configurePending = false;
    applyConfigurePending();
}

// Watches the repository's categorization rather than the agent: an agent can finish its turn
// without writing anything, and what the viewer must react to is the configuration actually
// changing. The revision covers a rewrite that leaves the status `configured`.
function beginConfigureWait(baseline) {
    var deadline = Date.now() + CONFIGURE_TIMEOUT_MS;
    var polling = false;
    state.configurePending = true;
    applyConfigurePending();

    state.configurePoll = setInterval(async function() {
        if (Date.now() > deadline) {
            endConfigureWait();
            return;
        }

        // A slow poll must not stack requests behind itself.
        if (polling) return;
        polling = true;

        try {
            var current = await fetchJson('diff-categorization', { cache: 'no-store' });
            if (!state.configurePending) return;
            // Only reached when the pre-dispatch read failed: the first poll that answers then
            // stands in for it.
            if (baseline === null) baseline = current.revision;
            if (current.revision === baseline) return;
            endConfigureWait();
            loadSummary();
        } catch (_) {
            // A failed poll is not a failed configuration — the next tick retries until the deadline.
        } finally {
            polling = false;
        }
    }, CONFIGURE_POLL_MS);
}

// Reuses the existing neutral icon-only toolbar treatment (.refresh-button), so the action reads as
// one more ordinary control instead of an accent that competes with the diff.
function createConfigureButton() {
    var button = document.createElement('button');
    button.type = 'button';
    button.className = 'refresh-button configure-button';
    button.setAttribute('aria-label', CONFIGURE_LABEL);
    button.title = CONFIGURE_LABEL;
    button.innerHTML =
        '<svg class="toolbar-icon" viewBox="0 0 16 16" fill="none" stroke="currentColor" stroke-width="1.5" stroke-linecap="round" aria-hidden="true" focusable="false">' +
        '<path d="M2.5 4.5h11M2.5 11.5h11"></path>' +
        '<circle cx="6" cy="4.5" r="1.7" fill="var(--bg-hover)"></circle>' +
        '<circle cx="10" cy="11.5" r="1.7" fill="var(--bg-hover)"></circle>' +
        '</svg>';
    button.addEventListener('click', sendConfigureRequest);
    return button;
}

function renderCategorizationWarning(reason) {
    var text = document.createElement('span');
    text.className = 'category-warning-text';
    text.textContent = reason
        ? CATEGORY_WARNING + ': ' + reason + '.'
        : CATEGORY_WARNING + '.';
    var warning = document.getElementById('category-warning');
    warning.replaceChildren(text);
    // The warning is where an author lands after a bad edit, so it carries the same affordance as
    // the toolbar — the same factory, so neither the label nor the payload can drift.
    if (canvasTransportAvailable()) {
        warning.appendChild(createConfigureButton());
        // This copy is new, so it starts unaware of a wait that is already running.
        applyConfigurePending();
    }
}

function renderCategorizedFiles(files, categorization) {
    var status = categorization && categorization.status;
    if (status === 'invalid') renderCategorizationWarning(categorization.reason);
    if (status === 'configured') renderCategoryTree(files);
    else renderFileList(files);
}

function mountFilePanel(button, item) {
    if (state.selectedButton) {
        state.selectedButton.classList.remove('active');
        state.selectedButton.setAttribute('aria-expanded', 'false');
    }
    if (state.panel) state.panel.remove();

    var panel = document.createElement('div');
    panel.id = button.getAttribute('aria-controls');
    panel.className = 'file-panel';
    panel.setAttribute('role', 'region');
    panel.setAttribute('aria-labelledby', button.id);
    item.appendChild(panel);

    button.classList.add('active');
    button.setAttribute('aria-expanded', 'true');
    state.selectedButton = button;
    state.panel = panel;
}

function collapseFilePanel() {
    if (state.fileAbort) state.fileAbort.abort();
    state.fileAbort = null;
    state.fileRequest += 1;
    removeStorage(SELECTION_KEY);
    state.currentResult = null;
    state.currentPatch = null;
    state.selected = null;
    if (state.selectedButton) {
        state.selectedButton.classList.remove('active');
        state.selectedButton.setAttribute('aria-expanded', 'false');
    }
    if (state.panel) state.panel.remove();
    state.selectedButton = null;
    state.panel = null;
}

function openFile(file, button, item) {
    mountFilePanel(button, item);
    state.selected = file;
    loadFile(file);
}

function selectFile(file, button, item) {
    if (state.selectedButton === button) {
        collapseFilePanel();
        return;
    }
    writeStorage(SELECTION_KEY, fileSelectionKey(file));
    openFile(file, button, item);
}

function restoreFileSelection(files) {
    var stored = readStorage(SELECTION_KEY);
    var selected = files.find(function(file) { return fileSelectionKey(file) === stored; });
    if (!selected) return;
    var button = fileEntries()
        .find(function(candidate) { return candidate.dataset.identity === selected.identity; });
    if (!button) return;
    // A remembered file outranks computed disclosure: every category between it and the root opens
    // so the restored panel is on screen rather than selected inside a collapsed subtree.
    ancestorCategoryHeaders(button).forEach(function(header) {
        header.setAttribute('aria-expanded', 'true');
    });
    openFile(selected, button, button.closest('.file-item'));
}

function renderChangeSummary(files) {
    var counts = files.reduce(function(result, file) {
        if (file.change === 'added' || file.change === 'untracked') result.added += 1;
        else if (file.change === 'deleted') result.removed += 1;
        else result.modified += 1;
        return result;
    }, { added: 0, modified: 0, removed: 0 });
    var summary = document.getElementById('change-summary');
    var parts = [
        ['added', 'Added', counts.added],
        ['modified', 'Modified', counts.modified],
        ['removed', 'Removed', counts.removed]
    ].filter(function(part) { return part[2] > 0; });
    summary.replaceChildren.apply(summary, parts.map(function(part) {
        var item = document.createElement('span');
        item.className = 'change-summary-' + part[0];
        var label = document.createElement('span');
        label.className = 'change-summary-label';
        label.textContent = part[1];
        var count = document.createElement('span');
        count.className = 'change-summary-count';
        count.textContent = String(part[2]);
        item.append(label, document.createTextNode(' '), count);
        return item;
    }));
    summary.setAttribute(
        'aria-label',
        parts.map(function(part) { return part[1] + ' ' + part[2]; }).join(', ')
    );
}

function isRenderablePatch(result) {
    return result.status === 'text' || result.status === 'deleted' || result.status === 'replacement';
}

function plainSpecialState(result, title, detail) {
    state.currentResult = result;
    state.currentPatch = null;
    if (result.patch) {
        var pre = document.createElement('pre');
        pre.className = 'plain-special';
        pre.dataset.state = result.status;
        pre.setAttribute('aria-label', title);
        pre.textContent = result.patch;
        replaceContent(pre);
    } else {
        renderState(result.status, title, detail, false);
        state.currentResult = result;
    }
}

function appendReplacementMarker(patch, replacement) {
    if (!replacement) return;
    var presentation = replacement === 'binary'
        ? {
            title: 'Binary replacement',
            detail: 'The tracked deletion is shown above. Binary replacement content is not rendered.'
        }
        : {
            title: 'Symbolic link replacement',
            detail: 'The tracked deletion is shown above. The replacement link target is unavailable.'
        };
    var marker = document.createElement('div');
    marker.className = 'replacement-marker';
    marker.dataset.state = replacement + '-replacement';
    marker.setAttribute('role', 'note');
    marker.setAttribute('aria-label', presentation.title);
    var title = document.createElement('div');
    title.className = 'replacement-title';
    title.textContent = presentation.title;
    marker.appendChild(title);
    var detail = document.createElement('div');
    detail.className = 'replacement-detail';
    detail.textContent = presentation.detail;
    marker.appendChild(detail);
    patch.appendChild(marker);
}

function renderFileResult(result) {
    state.currentResult = result;
    switch (result.status) {
        case 'text':
        case 'deleted':
            renderPatch(result.patch);
            break;
        case 'replacement':
            renderPatch(result.patch, result.replacement);
            break;
        case 'binary':
            renderState('binary', 'Binary file', 'Binary content is not rendered.', false);
            state.currentResult = result;
            break;
        case 'oversized':
            renderState('oversized', 'File is too large', 'The patch exceeds the 2 MiB rendering limit.', false);
            state.currentResult = result;
            break;
        case 'truncated':
            renderState('truncated', 'Patch is too long', 'The patch exceeds the 20,000-line rendering limit.', false);
            state.currentResult = result;
            break;
        case 'symlink':
            plainSpecialState(result, 'Symbolic link', 'The link target is unavailable.');
            break;
        case 'unavailable':
            renderState('unavailable', 'File unavailable', 'The selected file changed or is no longer available.', false);
            state.currentResult = result;
            break;
        case 'timeout':
            renderState(
                'timeout',
                'File diff timed out',
                'Select the file again to retry, or use Refresh to reload the comparison.',
                false
            );
            state.currentResult = result;
            break;
        case 'git-error':
            renderState('git-error', 'Could not load file diff', 'Git could not produce the selected patch.', false);
            state.currentResult = result;
            break;
        default:
            renderState('git-error', 'Could not load file diff', 'The server returned an unknown file state.', false);
            state.currentResult = result;
            break;
    }
}

function lineNumber(text) {
    var value = parseInt((text || '').trim(), 10);
    return Number.isFinite(value) ? value : null;
}

function assignLine(row, name, value) {
    if (row && value !== null) row.dataset[name] = String(value);
}

function annotateUnified(wrapper) {
    var hunk = null;
    wrapper.querySelectorAll('.d2h-file-diff tr').forEach(function(row) {
        var header = row.querySelector('.d2h-info .d2h-code-line');
        if (header && header.textContent.trim().startsWith('@@')) hunk = header.textContent.trim();
        if (hunk) row.dataset.hunk = hunk;
        var oldNumber = row.querySelector('.line-num1');
        var newNumber = row.querySelector('.line-num2');
        assignLine(row, 'oldLine', oldNumber ? lineNumber(oldNumber.textContent) : null);
        assignLine(row, 'newLine', newNumber ? lineNumber(newNumber.textContent) : null);
    });
}

function isContextRow(row) {
    return Boolean(row && row.querySelector('td.d2h-cntx') && !row.querySelector('.d2h-emptyplaceholder'));
}

function sideNumber(row) {
    var cell = row && row.querySelector('.d2h-code-side-linenumber');
    return cell ? lineNumber(cell.textContent) : null;
}

function annotateSplit(wrapper) {
    var sides = wrapper.querySelectorAll('.d2h-file-side-diff');
    if (sides.length < 2) return;
    var leftRows = Array.from(sides[0].querySelectorAll('tr'));
    var rightRows = Array.from(sides[1].querySelectorAll('tr'));
    var hunk = null;
    var count = Math.max(leftRows.length, rightRows.length);
    for (var index = 0; index < count; index += 1) {
        var left = leftRows[index] || null;
        var right = rightRows[index] || null;
        var header = left && left.querySelector('.d2h-info .d2h-code-side-line');
        if (header && header.textContent.trim().startsWith('@@')) hunk = header.textContent.trim();
        if (hunk && left) left.dataset.hunk = hunk;
        if (hunk && right) right.dataset.hunk = hunk;

        var oldNumber = sideNumber(left);
        var newNumber = sideNumber(right);
        assignLine(left, 'oldLine', oldNumber);
        assignLine(right, 'newLine', newNumber);

        if (isContextRow(left) && isContextRow(right)) {
            assignLine(left, 'newLine', newNumber);
            assignLine(right, 'oldLine', oldNumber);
        }
    }
}

function annotateDiffRows(patch) {
    patch.querySelectorAll('.d2h-file-wrapper').forEach(function(wrapper) {
        annotateUnified(wrapper);
        annotateSplit(wrapper);
    });
}

function loadHighlighter() {
    if (window.Diff2HtmlUI) return Promise.resolve(window.Diff2HtmlUI);
    if (highlighterPromise) return highlighterPromise;
    highlighterPromise = new Promise(function(resolve, reject) {
        var script = document.createElement('script');
        script.src = HIGHLIGHTER_URL;
        script.onload = function() {
            if (window.Diff2HtmlUI) resolve(window.Diff2HtmlUI);
            else reject(new Error('Diff2HtmlUI was not registered'));
        };
        script.onerror = function() { reject(new Error('Syntax highlighter failed to load')); };
        document.head.appendChild(script);
    }).catch(function(error) {
        highlighterPromise = null;
        throw error;
    });
    return highlighterPromise;
}

function hasVisibleSyntaxTokens(patch) {
    return Array.from(patch.querySelectorAll('.d2h-code-line-ctn span')).some(function(token) {
        var hasTokenClass = Array.from(token.classList).some(function(name) {
            return name.startsWith('hljs-');
        });
        var line = token.closest('.d2h-code-line-ctn');
        return hasTokenClass && line && getComputedStyle(token).color !== getComputedStyle(line).color;
    });
}

function highlightAfterPaint(patch, patchText) {
    patch.dataset.highlightStatus = 'waiting';
    requestAnimationFrame(function() {
        requestAnimationFrame(function() {
            if (!patch.isConnected || state.currentPatch !== patchText) return;
            patch.dataset.highlightStatus = 'loading';
            loadHighlighter()
                .then(function(Diff2HtmlUI) {
                    if (!patch.isConnected || state.currentPatch !== patchText) return;
                    var ui = new Diff2HtmlUI(patch, patchText, configForView());
                    ui.highlightCode();
                    patch.dataset.highlightStatus = hasVisibleSyntaxTokens(patch) ? 'ready' : 'plain';
                })
                .catch(function() {
                    if (patch.isConnected) patch.dataset.highlightStatus = 'failed';
                });
        });
    });
}

function renderPatch(patchText, replacement) {
    state.currentPatch = patchText;
    var patch = document.createElement('div');
    patch.id = 'patch';
    patch.dataset.renderStatus = 'plain';
    try {
        patch.innerHTML = Diff2Html.html(patchText, configForView());
        annotateDiffRows(patch);
        appendReplacementMarker(patch, replacement);
        replaceContent(patch);
        highlightAfterPaint(patch, patchText);
    } catch (_) {
        var pre = document.createElement('pre');
        pre.className = 'plain-special';
        pre.dataset.state = 'renderer-fallback';
        pre.textContent = patchText;
        patch.replaceChildren(pre);
        appendReplacementMarker(patch, replacement);
        patch.dataset.highlightStatus = 'failed';
        replaceContent(patch);
    }
}

async function fetchJson(url, options) {
    options = options || {};
    var requestOptions = Object.assign({}, options, {
        headers: Object.assign({}, options.headers || {}, {
            'X-Treemon-Diff-Viewer': VIEWER_ID
        })
    });
    var response = await fetch(url, requestOptions);
    if (!response.ok) throw new Error('HTTP ' + response.status);
    return response.json();
}

async function loadFile(file) {
    if (state.fileAbort) state.fileAbort.abort();
    var request = ++state.fileRequest;
    var controller = new AbortController();
    state.fileAbort = controller;
    state.currentResult = null;
    state.currentPatch = null;
    renderState('loading-file', 'Loading ' + file.displayPath + '…', '', true);

    try {
        var result = await fetchJson(
            'diff-file?identity=' + encodeURIComponent(file.identity),
            { cache: 'no-store', signal: controller.signal }
        );
        if (request !== state.fileRequest) return;
        state.fileAbort = null;
        renderFileResult(result);
    } catch (error) {
        if (error && error.name === 'AbortError') return;
        if (request !== state.fileRequest) return;
        state.fileAbort = null;
        renderState('git-error', 'Could not load file diff', 'The selected patch request failed.', false);
    }
}

function comparisonLabel(baseRef) {
    if (state.filters.committed) return 'Compared with ' + baseRef;
    if (state.filters.local) return 'Local changes from HEAD';
    return 'Untracked files';
}

function renderSummaryState(summary) {
    clearNavigator();
    switch (summary.status) {
        case 'clean':
            document.getElementById('base-label').textContent = comparisonLabel(summary.baseRef);
            renderState(
                'clean',
                'No changes',
                state.filters.untracked && !state.filters.committed && !state.filters.local
                    ? 'There are no untracked files.'
                    : 'The selected tracked layers contain no changes.',
                false
            );
            break;
        case 'filtered-empty':
            document.getElementById('base-label').textContent = 'All change layers hidden';
            renderState(
                'filtered-empty',
                'No change layers selected',
                'Select at least one inclusion filter to show changed files.',
                false
            );
            break;
        case 'stale':
            document.getElementById('base-label').textContent = 'Comparison superseded';
            renderState('stale', 'Newer comparison available', 'A newer refresh replaced this summary.', false);
            break;
        case 'base-error':
            document.getElementById('base-label').textContent = 'Comparison unavailable';
            renderState('base-error', 'Comparison base unavailable', 'Treemon could not resolve the configured base branch.', false);
            break;
        case 'too-many-files':
            document.getElementById('base-label').textContent = 'Comparison stopped';
            renderState(
                'too-many-files',
                'Too many changed files',
                'At least ' + summary.minimumFileCount + ' paths changed; the limit is 1,000.',
                false
            );
            break;
        case 'timeout':
            document.getElementById('base-label').textContent = 'Comparison timed out';
            renderState(
                'timeout',
                'Diff timed out',
                'Git did not finish within 10 seconds. Use Refresh to try again.',
                false
            );
            break;
        case 'git-error':
        default:
            document.getElementById('base-label').textContent = 'Comparison failed';
            renderState('git-error', 'Diff unavailable', 'Git could not produce a worktree summary.', false);
            break;
    }
}

function renderReadySummary(summary) {
    var files = Array.isArray(summary.files) ? summary.files : [];
    if (!files.length) {
        renderSummaryState({ status: 'clean', baseRef: summary.baseRef || 'base' });
        return;
    }

    clearNavigator();
    state.summary = summary;
    document.getElementById('base-label').textContent = comparisonLabel(summary.baseRef);
    renderChangeSummary(files);
    renderCategorizedFiles(files, summary.categorization);
    restoreFileSelection(files);
}

async function loadSummary() {
    if (state.fileAbort) state.fileAbort.abort();
    state.fileAbort = null;
    state.fileRequest += 1;
    var request = ++state.summaryRequest;
    clearNavigator();
    document.getElementById('base-label').textContent = 'Loading comparison…';
    renderState('loading-summary', 'Loading changed files…', '', true);

    try {
        var summary = await fetchJson(summaryUrl(), { cache: 'no-store' });
        if (request !== state.summaryRequest) return;
        applyLayerCounts(summary.layerCounts);
        if (summary.status === 'ready') renderReadySummary(summary);
        else renderSummaryState(summary);
    } catch (_) {
        if (request !== state.summaryRequest) return;
        renderSummaryState({ status: 'git-error' });
    }
}

function rangeFor(rows, name) {
    var values = rows
        .map(function(row) { return lineNumber(row.dataset[name]); })
        .filter(function(value) { return value !== null; });
    if (!values.length) return null;
    return { start: Math.min.apply(null, values), end: Math.max.apply(null, values) };
}

function intersectingRows(range) {
    return Array.from(document.querySelectorAll('#patch tr[data-hunk]')).filter(function(row) {
        try { return range.intersectsNode(row); } catch (_) { return false; }
    });
}

function fileForRange(range) {
    if (!range || !state.summary || !Array.isArray(state.summary.files)) return null;
    var start = range.startContainer;
    var element = start && (start.nodeType === Node.ELEMENT_NODE ? start : start.parentElement);
    var item = element && element.closest('.file-item');
    if (!item) return null;
    return state.summary.files.find(function(file) {
        return file.identity === item.dataset.identity;
    }) || null;
}

window.canvasSelectionMetadata = function(selectionContext) {
    var range = selectionContext && selectionContext.range;
    var file = fileForRange(range) || state.selected;
    if (!file) return { kind: 'diff' };
    var rows = range ? intersectingRows(range) : [];
    var hunks = Array.from(new Set(rows.map(function(row) { return row.dataset.hunk; }).filter(Boolean)));
    var hasExactHunk = hunks.length === 1;
    return {
        kind: 'diff',
        fileIdentity: file.identity,
        displayPath: file.displayPath,
        oldDisplayPath: file.oldDisplayPath || null,
        hunkHeader: hasExactHunk ? hunks[0] : null,
        oldLineRange: hasExactHunk ? rangeFor(rows, 'oldLine') : null,
        newLineRange: hasExactHunk ? rangeFor(rows, 'newLine') : null
    };
};

document.getElementById('unified-view').addEventListener('click', function() { setView('unified'); });
document.getElementById('split-view').addEventListener('click', function() { setView('split'); });
document.getElementById('refresh').addEventListener('click', loadSummary);
if (canvasTransportAvailable()) {
    document
        .querySelector('.toolbar')
        .insertBefore(createConfigureButton(), document.getElementById('refresh'));
}
document.getElementById('filter-committed').addEventListener('change', filtersChanged);
document.getElementById('filter-local').addEventListener('change', filtersChanged);
document.getElementById('filter-untracked').addEventListener('change', filtersChanged);
updateViewButtons();
updateFilterInputs();
loadSummary();
