module Tests.BeadsDataTests

open System
open System.IO
open Microsoft.Data.Sqlite
open NUnit.Framework
open Shared
open Server.BeadsStatus
open Server.RefreshScheduler
open Server.SchedulerState

/// Tests for the .beads/issues.jsonl parser and status summary in Server.BeadsStatus — the
/// beads-schema knowledge isolated in that module. They pin the parent-child edge direction
/// (issue_id = child, depends_on_id = parent), the deliberate ignoring of `blocks` edges, and
/// the "count ALL issue types by status" rule for the summary.
[<TestFixture>]
[<Category("Unit")>]
[<Category("Fast")>]
type BeadsDataTests() =

    // Real beads JSONL shape: one object per line, dependency edges inline on the child record.
    let jsonl (lines: string list) = String.concat "\n" lines

    let feature id status =
        $$"""{"id":"{{id}}","status":"{{status}}","issue_type":"feature"}"""

    // A task carrying a single parent-child edge to its parent feature.
    let childTask id status parent =
        $$"""{"id":"{{id}}","status":"{{status}}","issue_type":"task","dependencies":[{"issue_id":"{{id}}","depends_on_id":"{{parent}}","type":"parent-child","created_by":"unknown"}]}"""

    // A task whose ONLY edge is a blocks edge (blocked-by a blocker) — must NOT become a parent.
    let blockedTask id status blocker =
        $$"""{"id":"{{id}}","status":"{{status}}","issue_type":"task","dependencies":[{"issue_id":"{{id}}","depends_on_id":"{{blocker}}","type":"blocks","created_by":"unknown"}]}"""

    // A task with no dependencies field at all.
    let looseTask id status =
        $$"""{"id":"{{id}}","status":"{{status}}","issue_type":"task"}"""

    // A task whose parent-child edge is MALFORMED: it omits issue_id, so the edge cannot be
    // proven to belong to THIS record (the direction guard) and must be rejected — never a match.
    let childTaskMissingIssueId id status parent =
        $$"""{"id":"{{id}}","status":"{{status}}","issue_type":"task","dependencies":[{"depends_on_id":"{{parent}}","type":"parent-child","created_by":"unknown"}]}"""

    [<Test>]
    member _.``parseIssues resolves ParentId from a parent-child edge (depends_on_id = parent)``() =
        let issues = parseIssues (jsonl [ feature "feat-1" "open"; childTask "task-1" "open" "feat-1" ])
        let task = issues |> List.find (fun i -> i.Id = "task-1")
        Assert.That(task.ParentId, Is.EqualTo(Some "feat-1"))
        Assert.That(task.IssueType, Is.EqualTo("task"))
        Assert.That(task.Status, Is.EqualTo("open"))

    [<Test>]
    member _.``parseIssues never treats a blocks edge as a parent``() =
        let issues = parseIssues (jsonl [ blockedTask "task-1" "open" "blocker-1" ])
        Assert.That((List.exactlyOne issues).ParentId, Is.EqualTo(None))

    [<Test>]
    member _.``parseIssues yields no parent when the dependencies field is absent``() =
        let issues = parseIssues (jsonl [ looseTask "task-1" "open" ])
        Assert.That((List.exactlyOne issues).ParentId, Is.EqualTo(None))

    [<Test>]
    member _.``parseIssues rejects a parent-child edge missing issue_id (cannot prove it belongs to this record)``() =
        // A malformed edge with no issue_id must NOT match this record: without the child id we
        // cannot prove the edge belongs here, so depends_on_id must not leak into ParentId.
        let issues =
            parseIssues (jsonl [ feature "feat-1" "open"; childTaskMissingIssueId "task-1" "open" "feat-1" ])
        let task = issues |> List.find (fun i -> i.Id = "task-1")
        Assert.That(task.ParentId, Is.EqualTo(None))

    [<Test>]
    member _.``parseIssues picks the parent-child edge when a record also has a blocks edge``() =
        // Real records (e.g. tm-planning-box-1rf) carry BOTH a parent-child and a blocks edge.
        let record =
            """{"id":"task-1","status":"open","issue_type":"task","dependencies":[{"issue_id":"task-1","depends_on_id":"feat-1","type":"parent-child"},{"issue_id":"task-1","depends_on_id":"blocker-1","type":"blocks"}]}"""
        let issues = parseIssues record
        Assert.That((List.exactlyOne issues).ParentId, Is.EqualTo(Some "feat-1"))

    [<Test>]
    member _.``parseIssues skips blank lines and parses every record``() =
        let content = jsonl [ feature "feat-1" "open"; ""; childTask "task-1" "open" "feat-1"; "   " ]
        Assert.That(parseIssues content |> List.length, Is.EqualTo(2))

    [<Test>]
    member _.``parseIssues skips a row with no id (malformed, not counted)``() =
        // A row that is valid JSON but has no "id" is malformed and must be skipped, not counted
        // as a phantom task. The sibling valid row still parses.
        let noId = """{"status":"open","issue_type":"task"}"""
        let issues = parseIssues (jsonl [ noId; looseTask "task-1" "open" ])
        Assert.That(issues |> List.length, Is.EqualTo(1))
        Assert.That((List.exactlyOne issues).Id, Is.EqualTo("task-1"))

    [<Test>]
    member _.``parseIssues of empty content is empty``() =
        Assert.That(parseIssues "", Is.Empty)

    [<Test>]
    member _.``summarize counts ALL issue types by status including features``() =
        let issues =
            parseIssues (
                jsonl
                    [ feature "feat-1" "open" // a feature counts toward Open too
                      childTask "t1" "open" "feat-1"
                      childTask "t2" "in_progress" "feat-1"
                      childTask "t3" "blocked" "feat-1"
                      childTask "t4" "closed" "feat-1" ])
        let summary = summarize issues
        Assert.That(summary.Open, Is.EqualTo(2)) // feat-1 + t1
        Assert.That(summary.InProgress, Is.EqualTo(1)) // t2
        Assert.That(summary.Blocked, Is.EqualTo(1)) // t3
        Assert.That(summary.Closed, Is.EqualTo(1)) // t4

    [<Test>]
    member _.``summarize of empty issues is zero``() =
        Assert.That(summarize [], Is.EqualTo(BeadsSummary.zero))

    [<Test>]
    member _.``parse then classify buckets an open task under an in_progress feature as Queued``() =
        let planning =
            parseIssues (jsonl [ feature "feat-1" "in_progress"; childTask "task-1" "open" "feat-1" ])
            |> Planning.classify
        Assert.That(planning.Queued, Is.EqualTo(1))
        Assert.That(planning.Planned, Is.EqualTo(0))
        Assert.That(planning.Loose, Is.EqualTo(0))

    [<Test>]
    member _.``parse then classify: a blocks edge to an in_progress feature stays Loose (not Queued)``() =
        // Proves the parser does not leak a blocks edge into ParentId: task-1 is only *blocked by*
        // an in_progress feature, so it must land in Loose, never Queued.
        let planning =
            parseIssues (jsonl [ feature "feat-1" "in_progress"; blockedTask "task-1" "open" "feat-1" ])
            |> Planning.classify
        Assert.That(planning.Loose, Is.EqualTo(1))
        Assert.That(planning.Queued, Is.EqualTo(0))
        Assert.That(planning.Planned, Is.EqualTo(0))

    [<Test>]
    member _.``parse then classify: a parent-child edge missing issue_id does not bucket the task (stays Loose)``() =
        // Regression guard: a malformed edge with no child issue_id must not match ANY issue.
        // task-1 stays Loose even though a matching OPEN feature is present — with the old bug
        // (missing issue_id treated as a match) it would have been miscounted as Planned.
        let planning =
            parseIssues (jsonl [ feature "feat-1" "open"; childTaskMissingIssueId "task-1" "open" "feat-1" ])
            |> Planning.classify
        Assert.That(planning.Loose, Is.EqualTo(1))
        Assert.That(planning.Planned, Is.EqualTo(0))
        Assert.That(planning.Queued, Is.EqualTo(0))

[<TestFixture>]
[<Category("Integration")>]
[<Category("Fast")>]
type BeadsStorageTests() =

    let repoId = RepoId "beads-fixture"

    let withWorktree action =
        let path =
            Path.GetFullPath(
                Path.Combine(__SOURCE_DIRECTORY__, "..", "..", ".agents", $"beads-test-{Guid.NewGuid():N}")
            )
        Directory.CreateDirectory(Path.Combine(path, ".beads")) |> ignore

        try
            action path
        finally
            Directory.Delete(path, true)

    let databasePath path = Path.Combine(path, ".beads", "beads.db")
    let exportPath path = Path.Combine(path, ".beads", "issues.jsonl")
    let pagePath path = Path.Combine(path, ".agents", "canvas", "beads.html")

    let readBytes path =
        use stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite)
        use buffer = new MemoryStream()
        stream.CopyTo(buffer)
        buffer.ToArray()

    let createDatabase path rows =
        SqliteTestDatabase.execute (databasePath path) (
            """
CREATE TABLE issues (
    id TEXT PRIMARY KEY,
    status TEXT NOT NULL DEFAULT 'open',
    issue_type TEXT NOT NULL DEFAULT 'task'
);
CREATE TABLE dependencies (
    issue_id TEXT NOT NULL,
    depends_on_id TEXT NOT NULL,
    type TEXT NOT NULL DEFAULT 'blocks',
    PRIMARY KEY (issue_id, depends_on_id),
    FOREIGN KEY (issue_id) REFERENCES issues(id) ON DELETE CASCADE,
    FOREIGN KEY (depends_on_id) REFERENCES issues(id) ON DELETE CASCADE
);
""" + rows)

    let refresh (agent: MailboxProcessor<StateMsg>) path =
        let services =
            { StartPromptedAgent =
                fun _ _ _ -> async.Return(Error PromptedLaunchError.TerminalStartFailed)
              ActivityStore = None
              MergedPrStore = Server.MergedPrStore.create (Path.Combine(path, "merged-prs.json"))
              AutoSyncStore = Server.AutoSyncStore.create (Path.Combine(path, "auto-sync.json")) }

        executeTask agent services (Map.ofList [ repoId, path ]) (RefreshBeads(repoId, path))
        |> Async.RunSynchronously

    let register (agent: MailboxProcessor<StateMsg>) path =
        agent.Post(UpdateWorktreeList(repoId, [ { Path = path; Head = "fixture"; Branch = Some "fixture" } ]))

    let readRepo (agent: MailboxProcessor<StateMsg>) =
        (agent.PostAndAsyncReply(GetState) |> Async.RunSynchronously).Repos[repoId]

    [<Test>]
    member _.``Database-only refresh counts every status and directed parent relationship and provisions the page``() =
        withWorktree (fun path ->
            createDatabase path """
INSERT INTO issues (id, status, issue_type) VALUES
('feature-open', 'open', 'feature'),
('feature-active', 'in_progress', 'feature'),
('feature-blocked', 'blocked', 'feature'),
('feature-closed', 'closed', 'feature'),
('planned', 'open', 'task'),
('queued', 'open', 'task'),
('blocks-only', 'open', 'task'),
('reverse-edge', 'open', 'task'),
('nested', 'open', 'task'),
('loose', 'open', 'task'),
('under-blocked', 'open', 'task'),
('under-closed', 'open', 'task'),
('active', 'in_progress', 'task'),
('blocked', 'blocked', 'bug'),
('done', 'closed', 'task');
INSERT INTO dependencies (issue_id, depends_on_id, type) VALUES
('planned', 'feature-open', 'parent-child'),
('planned', 'feature-active', 'blocks'),
('queued', 'feature-active', 'PARENT-CHILD'),
('blocks-only', 'feature-active', 'blocks'),
('feature-active', 'reverse-edge', 'parent-child'),
('nested', 'planned', 'parent-child'),
('under-blocked', 'feature-blocked', 'parent-child'),
('under-closed', 'feature-closed', 'parent-child');
"""
            let before = readBytes (databasePath path)
            use agent = createAgent ()
            register agent path
            refresh agent path
            let repo = readRepo agent

            Assert.Multiple(fun () ->
                Assert.That(repo.BeadsData[path], Is.EqualTo({ Open = 9; InProgress = 2; Blocked = 2; Closed = 2 }))
                Assert.That(
                    repo.PlanningData[path],
                    Is.EqualTo({ Planned = 1; Queued = 1; Loose = 6; InProgress = 1; Blocked = 1; Closed = 1 }))
                Assert.That(File.ReadAllText(pagePath path), Is.EqualTo(Server.BeadspaceTemplate.html))
                Assert.That(readBytes (databasePath path), Is.EqualTo(before), "Reading must not change the database")
                Assert.That(File.Exists(exportPath path), Is.False, "Reading must not create an export")))

    [<Test>]
    member _.``Database remains authoritative over a newer export and reads later WAL commits without writing``() =
        withWorktree (fun path ->
            createDatabase path "INSERT INTO issues (id) VALUES ('task');"
            let staleExport = """{"id":"stale","issue_type":"task","status":"closed"}"""
            File.WriteAllText(exportPath path, staleExport)
            File.SetLastWriteTimeUtc(exportPath path, DateTime(2030, 1, 1, 0, 0, 0, DateTimeKind.Utc))

            use writer = SqliteTestDatabase.openConnection (databasePath path)
            use command = writer.CreateCommand()
            command.CommandText <- "PRAGMA journal_mode=WAL;"
            Assert.That(command.ExecuteScalar(), Is.EqualTo("wal"), "Fixture must use WAL")
            Assert.That(
                getBeadsData path |> Async.RunSynchronously,
                Is.EqualTo(
                    ({ Open = 1; InProgress = 0; Blocked = 0; Closed = 0 },
                     { BeadsPlanning.zero with Loose = 1 })))

            command.CommandText <- """
UPDATE issues SET status = 'closed' WHERE id = 'task';
INSERT INTO issues (id, status) VALUES ('new', 'blocked');
"""
            command.ExecuteNonQuery() |> ignore
            let before = readBytes (databasePath path)
            let walBefore = readBytes (databasePath path + "-wal")
            let summary, planning = getBeadsData path |> Async.RunSynchronously

            Assert.Multiple(fun () ->
                Assert.That(summary, Is.EqualTo({ Open = 0; InProgress = 0; Blocked = 1; Closed = 1 }))
                Assert.That(planning, Is.EqualTo({ BeadsPlanning.zero with Blocked = 1; Closed = 1 }))
                Assert.That(File.ReadAllText(exportPath path), Is.EqualTo(staleExport))
                Assert.That(readBytes (databasePath path), Is.EqualTo(before))
                Assert.That(readBytes (databasePath path + "-wal"), Is.EqualTo(walBefore))))

    [<Test>]
    member _.``Closed-only database is not filtered or limited and still provisions the page``() =
        withWorktree (fun path ->
            let rows =
                [ 1 .. 75 ]
                |> List.map (fun id -> $"('closed-{id}', 'closed')")
                |> String.concat ","
            createDatabase path $"INSERT INTO issues (id, status) VALUES {rows};"
            use agent = createAgent ()
            register agent path
            refresh agent path
            let repo = readRepo agent

            Assert.Multiple(fun () ->
                Assert.That(repo.BeadsData[path], Is.EqualTo({ BeadsSummary.zero with Closed = 75 }))
                Assert.That(repo.PlanningData[path], Is.EqualTo({ BeadsPlanning.zero with Closed = 75 }))
                Assert.That(File.ReadAllText(pagePath path), Is.EqualTo(Server.BeadspaceTemplate.html))))

    [<Test>]
    member _.``JSONL-only worktree preserves summary planning and page provisioning without creating a database``() =
        withWorktree (fun path ->
            File.WriteAllText(exportPath path, """{"id":"feature","status":"in_progress","issue_type":"feature"}
{"id":"task","status":"open","issue_type":"task","dependencies":[{"issue_id":"task","depends_on_id":"feature","type":"parent-child"}]}""")
            use agent = createAgent ()
            register agent path
            refresh agent path
            let repo = readRepo agent

            Assert.Multiple(fun () ->
                Assert.That(repo.BeadsData[path], Is.EqualTo({ Open = 1; InProgress = 1; Blocked = 0; Closed = 0 }))
                Assert.That(repo.PlanningData[path], Is.EqualTo({ BeadsPlanning.zero with Queued = 1 }))
                Assert.That(File.ReadAllText(pagePath path), Is.EqualTo(Server.BeadspaceTemplate.html))
                Assert.That(File.Exists(databasePath path), Is.False)))

    [<Test>]
    member _.``Empty database overrides nonempty export and removes a previously provisioned page``() =
        withWorktree (fun path ->
            createDatabase path "INSERT INTO issues (id) VALUES ('task');"
            File.WriteAllText(exportPath path, """{"id":"stale","status":"open","issue_type":"task"}""")
            use agent = createAgent ()
            register agent path
            refresh agent path
            SqliteTestDatabase.execute (databasePath path) "DELETE FROM issues;"
            refresh agent path
            let repo = readRepo agent

            Assert.Multiple(fun () ->
                Assert.That(repo.BeadsData[path], Is.EqualTo(BeadsSummary.zero))
                Assert.That(repo.PlanningData[path], Is.EqualTo(BeadsPlanning.zero))
                Assert.That(File.Exists(pagePath path), Is.False)))

    [<TestCase("corrupt")>]
    [<TestCase("unsupported-schema")>]
    [<TestCase("database-directory")>]
    member _.``Database errors fail the refresh without falling back to export or clearing the last good state``(failure: string) =
        withWorktree (fun path ->
            createDatabase path "INSERT INTO issues (id) VALUES ('task');"
            use agent = createAgent ()
            register agent path
            refresh agent path
            File.Delete(databasePath path)
            if failure = "corrupt" then
                File.WriteAllText(databasePath path, "not a database")
            elif failure = "database-directory" then
                Directory.CreateDirectory(databasePath path) |> ignore
            else
                SqliteTestDatabase.execute (databasePath path) "CREATE TABLE unrelated (id TEXT);"
            File.WriteAllText(exportPath path, """{"id":"stale","status":"closed","issue_type":"task"}""")

            Assert.Throws<SqliteException>(fun () -> refresh agent path) |> ignore
            let repo = readRepo agent

            Assert.Multiple(fun () ->
                Assert.That(repo.BeadsData[path], Is.EqualTo({ BeadsSummary.zero with Open = 1 }))
                Assert.That(repo.PlanningData[path], Is.EqualTo({ BeadsPlanning.zero with Loose = 1 }))
                Assert.That(File.ReadAllText(pagePath path), Is.EqualTo(Server.BeadspaceTemplate.html))))

    [<TestCase(false)>]
    [<TestCase(true)>]
    member _.``Absent storage and empty JSONL produce zero without creating storage``(emptyExport: bool) =
        withWorktree (fun path ->
            if emptyExport then
                File.WriteAllText(exportPath path, "")
            else
                Directory.Delete(Path.Combine(path, ".beads"))

            Assert.That(
                getBeadsData path |> Async.RunSynchronously,
                Is.EqualTo((BeadsSummary.zero, BeadsPlanning.zero)))
            Assert.That(File.Exists(databasePath path), Is.False))
