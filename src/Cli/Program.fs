module Cli.Program

open System
open FSharp.SystemCommandLine
open FSharp.SystemCommandLine.Input
open Fable.Remoting.DotnetClient
open Shared

let createApi (port: int) =
    Remoting.createApi $"http://localhost:{port}"
    |> Remoting.buildProxy<IWorktreeApi>

let resolvePort (portMaybe: int option) =
    portMaybe
    |> Option.orElseWith (fun () ->
        Environment.GetEnvironmentVariable "TREEMON_PORT"
        |> Option.ofObj
        |> Option.bind (fun s ->
            match Int32.TryParse s with
            | true, p -> Some p
            | _ -> None))
    |> Option.defaultValue 5000

let serverError port =
    eprintfn $"Error: Treemon server is not running on port %d{port}. Start with: .\\treemon.ps1 start <path>"
    1

let runApi port (fn: IWorktreeApi -> Async<Result<unit, string>>) successMsg =
    try
        let api = createApi port

        match fn api |> Async.RunSynchronously with
        | Ok() -> printfn $"✓ %s{successMsg}"; 0
        | Error e -> eprintfn $"Error: %s{e}"; 1
    with
    | :? AggregateException -> serverError port
    | :? Net.Http.HttpRequestException -> serverError port

let formatCodingTool = function
    | Working -> "🔧 Working"
    | WaitingForUser -> "⏳ Waiting"
    | Done -> "✅ Done"
    | Idle -> "💤 Idle"

let formatPr = function
    | NoPr -> "No PR"
    | HasPr pr ->
        let flags =
            [ if pr.IsDraft then "draft"
              if pr.IsMerged then "merged"
              if pr.HasConflicts then "conflicts" ]

        let flagStr =
            match flags with
            | [] -> ""
            | fs -> fs |> String.concat ", " |> sprintf " [%s]"

        $"PR #{pr.Id}%s{flagStr}: %s{pr.Title}"

let launchCmd =
    let handler
        (
            path: string,
            prompt: string option,
            fixPr: string option,
            fixBuild: string option,
            fixTests: bool,
            createPr: bool,
            port: int option
        ) =
        let port = resolvePort port

        let actions =
            [ prompt |> Option.map Choice1Of2
              fixPr |> Option.map (FixPr >> Choice2Of2)
              fixBuild |> Option.map (FixBuild >> Choice2Of2)
              (if fixTests then Some(Choice2Of2 FixTests) else None)
              (if createPr then Some(Choice2Of2 CreatePr) else None) ]
            |> List.choose id

        match actions with
        | [ single ] ->
            let wtPath = WorktreePath.create path

            match single with
            | Choice1Of2 text ->
                runApi port (fun api -> api.launchSession { Path = wtPath; Prompt = text }) "Session launched"
            | Choice2Of2 action ->
                runApi port (fun api -> api.launchAction { Path = wtPath; Action = action }) "Action launched"
        | _ ->
            eprintfn "Error: Provide exactly one of: <prompt>, --fix-pr, --fix-build, --fix-tests, or --create-pr"
            1

    command "launch" {
        description "Launch a coding agent in a worktree terminal tab"

        inputs (
            option<string> "--path" |> desc "Worktree path" |> required,
            argumentMaybe<string> "prompt" |> desc "Free-form prompt text",
            optionMaybe<string> "--fix-pr" |> desc "Fix PR comments (provide PR URL)",
            optionMaybe<string> "--fix-build" |> desc "Fix failed build (provide build URL)",
            option<bool> "--fix-tests" |> def false |> desc "Fix failing tests",
            option<bool> "--create-pr" |> def false |> desc "Create a pull request",
            optionMaybe<int> "--port" |> desc "Server port (default: 5000, env: TREEMON_PORT)"
        )

        setAction handler
    }

let newCmd =
    let handler (repo: string, branch: string, baseBranch: string, port: int option) =
        let port = resolvePort port

        runApi
            port
            (fun api ->
                api.createWorktree
                    { RepoId = repo
                      BranchName = BranchName.create branch
                      BaseBranch = BranchName.create baseBranch })
            $"Worktree created for branch '%s{branch}'"

    command "new" {
        description "Create a new worktree"

        inputs (
            option<string> "--repo" |> desc "Repository root path" |> required,
            option<string> "--branch" |> desc "New branch name" |> required,
            option<string> "--base" |> def "main" |> desc "Base branch to fork from (default: main)",
            optionMaybe<int> "--port" |> desc "Server port (default: 5000, env: TREEMON_PORT)"
        )

        setAction handler
    }

let worktreesCmd =
    let handler (port: int option) =
        let port = resolvePort port

        try
            let api = createApi port
            let dashboard = api.getWorktrees() |> Async.RunSynchronously

            match dashboard.Repos with
            | [] -> printfn "No worktrees found."
            | repos ->
                repos
                |> List.iter (fun repo ->
                    printfn $"\n📁 %s{repo.RootFolderName}"

                    repo.Worktrees
                    |> List.iter (fun wt ->
                        let path = WorktreePath.value wt.Path
                        let tool = formatCodingTool wt.CodingTool
                        let pr = formatPr wt.Pr
                        printfn $"  %-50s{path}  %-15s{wt.Branch}  %-15s{tool}  %s{pr}"))

            0
        with
        | :? AggregateException -> serverError port
        | :? Net.Http.HttpRequestException -> serverError port

    command "worktrees" {
        description "List all tracked worktrees"
        inputs (optionMaybe<int> "--port" |> desc "Server port (default: 5000, env: TREEMON_PORT)")
        setAction handler
    }

[<EntryPoint>]
let main argv =
    rootCommand argv {
        description "Treemon CLI — control the worktree dashboard from the command line"
        inputs (context)
        helpAction
        addCommand launchCmd
        addCommand newCmd
        addCommand worktreesCmd
    }
