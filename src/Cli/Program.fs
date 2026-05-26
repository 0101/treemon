module Cli.Program

open System
open System.IO
open System.Text.RegularExpressions
open FSharp.SystemCommandLine
open FSharp.SystemCommandLine.Input
open Fable.Remoting.DotnetClient
open Shared

let createApi (port: int) =
    Remoting.createApi $"http://localhost:{port}"
    |> Remoting.buildProxy<IWorktreeApi>

let defaultPort = 5000

let resolvePort (portMaybe: int option) (envPort: string option) =
    match portMaybe, envPort with
    | Some port, _ -> port
    | None, Some portStr ->
        match Int32.TryParse portStr with
        | true, port -> port
        | _ -> defaultPort
    | None, None -> defaultPort

let serverError port =
    eprintfn $"Error: Treemon server is not running on port %d{port}. Start with: .\\treemon.ps1 start <path>"
    1

let isConnectionError (ex: exn) =
    match ex with
    | :? Net.Http.HttpRequestException -> true
    | :? Net.Sockets.SocketException -> true
    | _ -> false

let tryCallServer port (fn: IWorktreeApi -> int) =
    try
        fn (createApi port)
    with
    | :? AggregateException as ae when ae.InnerExceptions |> Seq.exists isConnectionError -> serverError port
    | :? Net.Http.HttpRequestException -> serverError port
    | ex ->
        eprintfn $"Server error: {ex.Message}"
        1

let withPort portMaybe fn =
    let port = resolvePort portMaybe (Environment.GetEnvironmentVariable "TREEMON_PORT" |> Option.ofObj)

    if port < 1 || port > 65535 then
        eprintfn $"Error: Invalid port %d{port}. Must be between 1 and 65535."
        1
    else
        fn port

let runApi port (fn: IWorktreeApi -> Async<Result<unit, string>>) successMsg =
    tryCallServer port (fun api ->
        match fn api |> Async.RunSynchronously with
        | Ok() -> printfn $"✓ %s{successMsg}"; 0
        | Error e -> eprintfn $"Error: %s{e}"; 1)

let sanitizeForTerminal (s: string) =
    Regex.Replace(s, @"[\x00-\x1F\x7F]", "")

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

        $"PR #{pr.Id}%s{flagStr}: %s{sanitizeForTerminal pr.Title}"

let metaPrompt = "Read and follow the instructions in @.agents/prompt.md"

let copyPromptFile (wtPath: WorktreePath) (promptFilePath: string) =
    let fullPromptPath = Path.GetFullPath promptFilePath
    let agentsDir = Path.Combine(WorktreePath.value wtPath, ".agents")
    let destPath = Path.Combine(agentsDir, "prompt.md")
    let fullDestPath = Path.GetFullPath destPath

    if fullPromptPath = fullDestPath then
        Ok()
    else
        try
            Directory.CreateDirectory(agentsDir) |> ignore
            File.Copy(fullPromptPath, fullDestPath, overwrite = true)
            Ok()
        with ex ->
            Error $"Failed to copy prompt file: {ex.Message}"

let launchCmd =
    let handler
        (
            path: string,
            promptFile: string option,
            fixPr: string option,
            fixBuild: string option,
            fixTests: bool,
            createPr: bool,
            port: int option
        ) =
        withPort port (fun port ->
            let actions =
                [ promptFile |> Option.map Choice1Of2
                  fixPr |> Option.map (FixPr >> Choice2Of2)
                  fixBuild |> Option.map (FixBuild >> Choice2Of2)
                  (if fixTests then Some(Choice2Of2 FixTests) else None)
                  (if createPr then Some(Choice2Of2 CreatePr) else None) ]
                |> List.choose id

            match actions with
            | [ single ] ->
                let wtPath = path |> Path.GetFullPath |> WorktreePath

                match single with
                | Choice1Of2 filePath ->
                    if not (File.Exists filePath) then
                        eprintfn $"Error: Prompt file not found: %s{filePath}"
                        1
                    elif not (Directory.Exists(WorktreePath.value wtPath)) then
                        eprintfn $"Error: Worktree path does not exist: %s{WorktreePath.value wtPath}"
                        1
                    else
                        match copyPromptFile wtPath filePath with
                        | Error e ->
                            eprintfn $"Error: %s{e}"
                            1
                        | Ok() ->
                            runApi port (fun api -> api.launchSession { Path = wtPath; Prompt = metaPrompt }) "Session launched"
                | Choice2Of2 action ->
                    runApi port (fun api -> api.launchAction { Path = wtPath; Action = action }) "Action launched"
            | _ ->
                eprintfn "Error: Provide exactly one of: --prompt-file, --fix-pr, --fix-build, --fix-tests, or --create-pr"
                1)

    command "launch" {
        description "Launch a coding agent in a worktree terminal tab"

        inputs (
            option<string> "--path" |> desc "Worktree path" |> required,
            optionMaybe<string> "--prompt-file" |> desc "Path to a prompt file (e.g. instructions.md)",
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
        withPort port (fun port ->
            runApi
                port
                (fun api ->
                    api.createWorktree
                        { RepoId = repo
                          BranchName = BranchName.create branch
                          BaseBranch = BranchName.create baseBranch })
                $"Worktree created for branch '%s{branch}'")

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
        withPort port (fun port ->
            tryCallServer port (fun api ->
                let dashboard = api.getWorktrees() |> Async.RunSynchronously

                match dashboard.Repos with
                | [] -> printfn "No worktrees found."; 0
                | repos ->
                    repos
                    |> List.iter (fun repo ->
                        printfn $"\n📁 %s{sanitizeForTerminal repo.RootFolderName}"

                        repo.Worktrees
                        |> List.iter (fun wt ->
                            let path = WorktreePath.value wt.Path |> sanitizeForTerminal
                            let branch = sanitizeForTerminal wt.Branch
                            let tool = formatCodingTool wt.CodingTool
                            let pr = formatPr wt.Pr
                            printfn $"  %-50s{path}  %-15s{branch}  %-15s{tool}  %s{pr}"))

                    0))

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
