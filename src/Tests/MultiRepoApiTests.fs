module Tests.MultiRepoApiTests

open System.Net.Http
open System.Text
open NUnit.Framework
open Newtonsoft.Json
open Shared

let private converter = Fable.Remoting.Json.FableJsonConverter()

let private deserializeDashboard (json: string) =
    JsonConvert.DeserializeObject<DashboardResponse>(json, converter)

[<TestFixture>]
[<Category("E2E")>]
type MultiRepoApiTests() =

    let serverUrl = ServerFixture.serverUrl

    let fetchDashboard () =
        task {
            use client = new HttpClient()
            let content = new StringContent("[]", Encoding.UTF8, "application/json")
            let! response = client.PostAsync($"{serverUrl}/IWorktreeApi/getWorktrees", content)
            Assert.That(int response.StatusCode, Is.EqualTo(200), "POST /IWorktreeApi/getWorktrees should return 200")
            let! body = response.Content.ReadAsStringAsync()
            TestContext.Out.WriteLine($"API response (first 500 chars): {body.Substring(0, System.Math.Min(500, body.Length))}")
            return deserializeDashboard body
        }

    [<Test>]
    member _.``API returns DashboardResponse with 2 repos``() =
        task {
            let! dashboard = fetchDashboard ()
            Assert.That(dashboard.Repos.Length, Is.EqualTo(2), "DashboardResponse should contain exactly 2 repo entries")
        }

    [<Test>]
    member _.``API response contains TestProject repo``() =
        task {
            let! dashboard = fetchDashboard ()
            let repoIds = dashboard.Repos |> List.map _.RepoId
            Assert.That(repoIds, Does.Contain(RepoId "TestProject"), "Repos should include TestProject")
        }

    [<Test>]
    member _.``API response contains treemon repo``() =
        task {
            let! dashboard = fetchDashboard ()
            let repoIds = dashboard.Repos |> List.map _.RepoId
            Assert.That(repoIds, Does.Contain(RepoId "treemon"), "Repos should include treemon")
        }

    [<Test>]
    member _.``TestProject repo has non-empty Worktrees``() =
        task {
            let! dashboard = fetchDashboard ()

            let testProject =
                dashboard.Repos
                |> List.find (fun r -> r.RepoId = RepoId "TestProject")

            Assert.That(testProject.Worktrees.Length, Is.GreaterThan(0), "TestProject repo should have at least one worktree")
            TestContext.Out.WriteLine($"TestProject has {testProject.Worktrees.Length} worktrees")
        }

    [<Test>]
    member _.``treemon repo has non-empty Worktrees``() =
        task {
            let! dashboard = fetchDashboard ()

            let treemon =
                dashboard.Repos
                |> List.find (fun r -> r.RepoId = RepoId "treemon")

            Assert.That(treemon.Worktrees.Length, Is.GreaterThan(0), "treemon repo should have at least one worktree")
            TestContext.Out.WriteLine($"treemon has {treemon.Worktrees.Length} worktrees")
        }

    [<Test>]
    member _.``Each repo has IsReady set to true``() =
        task {
            let! dashboard = fetchDashboard ()

            dashboard.Repos
            |> List.iter (fun repo ->
                Assert.That(repo.IsReady, Is.True, $"Repo '{RepoId.value repo.RepoId}' should have IsReady=true in fixture mode"))
        }

    [<Test>]
    member _.``Each repo has matching RootFolderName``() =
        task {
            let! dashboard = fetchDashboard ()

            dashboard.Repos
            |> List.iter (fun repo ->
                Assert.That(repo.RootFolderName, Is.EqualTo(RepoId.value repo.RepoId),
                    $"Repo '{RepoId.value repo.RepoId}' RootFolderName should match RepoId"))
        }

    [<Test>]
    member _.``DashboardResponse has non-empty AppVersion``() =
        task {
            let! dashboard = fetchDashboard ()
            Assert.That(dashboard.AppVersion, Is.Not.Null.And.Not.Empty, "AppVersion should not be null or empty")
        }

    [<Test>]
    member _.``DashboardResponse has LatestByCategory map``() =
        task {
            let! dashboard = fetchDashboard ()
            Assert.That(dashboard.LatestByCategory.Count, Is.GreaterThan(0), "LatestByCategory should have entries")
        }

    [<Test>]
    member _.``Worktrees across repos have distinct branch names``() =
        task {
            let! dashboard = fetchDashboard ()

            let allBranches =
                dashboard.Repos
                |> List.collect (fun r -> r.Worktrees |> List.map _.Branch)

            Assert.That(allBranches.Length, Is.EqualTo(allBranches |> List.distinct |> List.length),
                "Branch names should be distinct across all repos in fixture data")
        }

    [<Test>]
    member _.``TestProject worktrees include AzDo PR data``() =
        task {
            let! dashboard = fetchDashboard ()

            let testProject =
                dashboard.Repos |> List.find (fun r -> r.RepoId = RepoId "TestProject")

            let hasPr =
                testProject.Worktrees
                |> List.exists (fun wt ->
                    match wt.Pr with
                    | HasPr info -> info.Url.Contains("dev.azure.com")
                    | NoPr -> false)

            Assert.That(hasPr, Is.True, "TestProject should have at least one worktree with AzDo PR URL")
        }

    [<Test>]
    member _.``treemon worktrees include GitHub PR data``() =
        task {
            let! dashboard = fetchDashboard ()

            let treemon =
                dashboard.Repos |> List.find (fun r -> r.RepoId = RepoId "treemon")

            let hasPr =
                treemon.Worktrees
                |> List.exists (fun wt ->
                    match wt.Pr with
                    | HasPr info -> info.Url.Contains("github.com")
                    | NoPr -> false)

            Assert.That(hasPr, Is.True, "treemon should have at least one worktree with GitHub PR URL")
        }

    [<Test>]
    member _.``GitHub PR uses WithResolution comment format``() =
        task {
            let! dashboard = fetchDashboard ()

            let treemon =
                dashboard.Repos |> List.find (fun r -> r.RepoId = RepoId "treemon")

            let githubPr =
                treemon.Worktrees
                |> List.pick (fun wt ->
                    match wt.Pr with
                    | HasPr info when info.Url.Contains("github.com") -> Some info
                    | _ -> None)

            match githubPr.Comments with
            | WithResolution _ -> Assert.Pass("GitHub PR correctly uses WithResolution comment format")
        }

    [<Test>]
    member _.``AzDo PR uses WithResolution comment format``() =
        task {
            let! dashboard = fetchDashboard ()

            let testProject =
                dashboard.Repos |> List.find (fun r -> r.RepoId = RepoId "TestProject")

            let azdoPr =
                testProject.Worktrees
                |> List.pick (fun wt ->
                    match wt.Pr with
                    | HasPr info when info.Url.Contains("dev.azure.com") -> Some info
                    | _ -> None)

            match azdoPr.Comments with
            | WithResolution _ -> Assert.Pass("AzDo PR correctly uses WithResolution comment format")
        }
