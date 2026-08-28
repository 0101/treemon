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

let writeLaunchResult
    (writeOutput: string -> unit)
    (writeError: string -> unit)
    (result: Result<EmbeddedTerminalStartResult, string>)
    =
    match result with
    | Ok _ ->
        writeOutput "✓ Agent launched in embedded terminal"
        0
    | Error error ->
        writeError $"Error: {error}"
        1

let runLaunchApi
    port
    (fn: IWorktreeApi -> Async<Result<EmbeddedTerminalStartResult, string>>)
    =
    tryCallServer port (fun api ->
        fn api
        |> Async.RunSynchronously
        |> writeLaunchResult (printfn "%s") (eprintfn "%s"))

let sanitizeForTerminal (s: string) =
    Regex.Replace(s, @"[\x00-\x1F\x7F]", "")

let formatCodingTool = function
    | Working -> "🔧 Working"
    | WaitingForUser -> "⏳ Waiting"
    | Idle -> "💤 Idle"
    | NoSession -> "⚫ No session"

let formatPr = function
    | NoPr -> "No PR"
    | HasPr pr ->
        let flags =
            [ if pr.IsDraft then "draft"
              if pr.State = PrState.Merged then "merged"
              if pr.AutoMergeEnabled then "auto-merge"
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
            createPr: bool,
            port: int option
        ) =
        withPort port (fun port ->
            let actions =
                [ promptFile |> Option.map Choice1Of2
                  fixPr |> Option.map (FixPr >> Choice2Of2)
                  fixBuild |> Option.map (FixBuild >> Choice2Of2)
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
                            runLaunchApi
                                port
                                (fun api -> api.launchSession { Path = wtPath; Prompt = metaPrompt })
                | Choice2Of2 action ->
                    runLaunchApi
                        port
                        (fun api -> api.launchAction { Path = wtPath; Action = action })
            | _ ->
                eprintfn "Error: Provide exactly one of: --prompt-file, --fix-pr, --fix-build, or --create-pr"
                1)

    command "launch" {
        description "Launch a coding agent in a new embedded terminal"

        inputs (
            option<string> "--path" |> desc "Worktree path" |> required,
            optionMaybe<string> "--prompt-file" |> desc "Path to a prompt file (e.g. instructions.md)",
            optionMaybe<string> "--fix-pr" |> desc "Fix PR comments (provide PR URL)",
            optionMaybe<string> "--fix-build" |> desc "Fix failed build (provide build URL)",
            option<bool> "--create-pr" |> def false |> desc "Create a pull request",
            optionMaybe<int> "--port" |> desc "Server port (default: 5000, env: TREEMON_PORT)"
        )

        setAction handler
    }

let newCmd =
    let handler (repo: string, branch: string, baseBranch: string, port: int option) =
        withPort port (fun port ->
            tryCallServer port (fun api ->
                let request =
                    { RepoId = repo
                      BranchName = BranchName.create branch
                      BaseBranch = BranchName.create baseBranch
                      Prompt = None
                      Skill = None }

                match api.createWorktree request |> Async.RunSynchronously with
                | Ok warnings ->
                    printfn $"✓ Worktree created for branch '%s{branch}'"
                    warnings |> List.iter (fun w -> eprintfn $"⚠ %s{w}")
                    0
                | Error e -> eprintfn $"Error: %s{e}"; 1))

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

/// Folds a per-path root operation (add/remove) into a tri-state exit code:
///   0 = all paths succeeded, 1 = all paths failed, 2 = partial success.
/// A partial batch returns 2 (not 1) because the paths that succeeded WERE persisted
/// server-side immediately, before a later path failed validation. The treemon.ps1 shims
/// restart prod on 0 OR 2 so those persisted roots actually apply, while a full failure (1)
/// skips the restart (don't bounce prod when nothing changed). Any failure still yields a
/// non-zero exit, so callers/scripts can still detect rejected paths.
let foldRootResults (verb: string) (op: string -> Async<Result<unit, string>>) (paths: string[]) : int =
    let anySuccess, anyFailure =
        ((false, false), paths)
        ||> Array.fold (fun (anySuccess, anyFailure) path ->
            match op path |> Async.RunSynchronously with
            | Ok() -> printfn $"✓ %s{verb} %s{path} (applies on next server restart)"; (true, anyFailure)
            | Error e -> eprintfn $"Error: %s{e}"; (anySuccess, true))

    match anySuccess, anyFailure with
    | true, true -> 2
    | false, true -> 1
    | _ -> 0

let addCmd =
    let handler (paths: string[], port: int option) =
        withPort port (fun port ->
            // One tryCallServer for the whole batch so the "server is not running" message
            // (and the connection check) happens once, not once per path.
            tryCallServer port (fun api -> foldRootResults "Added" api.addRoot paths))

    command "add" {
        description "Add one or more worktree roots to watch (applies on next server restart)"

        inputs (
            argument<string[]> "paths" |> arity Arity.OneOrMore |> desc "Worktree root path(s) to watch",
            optionMaybe<int> "--port" |> desc "Server port (default: 5000, env: TREEMON_PORT)"
        )

        setAction handler
    }

let removeCmd =
    let handler (paths: string[], port: int option) =
        withPort port (fun port ->
            tryCallServer port (fun api -> foldRootResults "Removed" api.removeRoot paths))

    command "remove" {
        description "Remove one or more worktree roots (applies on next server restart)"

        inputs (
            argument<string[]> "paths" |> arity Arity.OneOrMore |> desc "Worktree root path(s) to stop watching",
            optionMaybe<int> "--port" |> desc "Server port (default: 5000, env: TREEMON_PORT)"
        )

        setAction handler
    }

let rootsCmd =
    let handler (port: int option) =
        withPort port (fun port ->
            tryCallServer port (fun api ->
                match api.getRoots() |> Async.RunSynchronously with
                | [] -> printfn "No worktree roots configured."; 0
                | roots ->
                    roots |> List.iter (fun root -> printfn $"%s{sanitizeForTerminal root}")
                    0))

    command "roots" {
        description "List the configured worktree roots"
        inputs (optionMaybe<int> "--port" |> desc "Server port (default: 5000, env: TREEMON_PORT)")
        setAction handler
    }

/// Renders a diff category report and the exit code that goes with it: 0 only for a configured
/// repository, so the command is usable as a check and not just a printer. Category names come from
/// the repository's `.treemon.json`, so they are sanitized before they reach the terminal.
let formatDiffCategoryReport (report: DiffCategoryReport) : string list * int =
    match report with
    | DiffCategoryReport.Missing ->
        [ "Diff categories: not configured"
          "The repository root's .treemon.json declares no diffCategories, so the diff view shows a flat file list." ],
        1
    | DiffCategoryReport.Invalid reason ->
        [ "Diff categories: invalid (the diff view falls back to a flat file list)"
          $"Reason: %s{reason}" ],
        1
    | DiffCategoryReport.Configured(leaves, unmatched) ->
        let matched = leaves |> List.sumBy _.FileCount

        let countWidth =
            unmatched :: (leaves |> List.map _.FileCount)
            |> List.map (fun count -> (string count).Length)
            |> List.fold max 1

        let categoryLines =
            leaves
            |> List.map (fun leaf ->
                let name = leaf.CategoryPath |> List.map sanitizeForTerminal |> String.concat " > "
                let marker = if leaf.FileCount = 0 then "  ⚠ matches no tracked file" else ""
                $"""  %s{(string leaf.FileCount).PadLeft(countWidth)}  %s{name}%s{marker}""")

        [ "Diff categories: configured"; "" ]
        @ categoryLines
        @ [ ""
            $"%d{matched} of %d{matched + unmatched} tracked files matched; %d{unmatched} unmatched (the viewer's Other group)" ],
        0

let categoriesCmd =
    let handler (path: string option, port: int option) =
        withPort port (fun port ->
            let target = path |> Option.defaultValue "." |> Path.GetFullPath

            tryCallServer port (fun api ->
                match api.getDiffCategoryReport target |> Async.RunSynchronously with
                | Error e -> eprintfn $"Error: %s{e}"; 1
                | Ok report ->
                    let lines, exitCode = formatDiffCategoryReport report
                    lines |> List.iter (printfn "%s")
                    exitCode))

    command "categories" {
        description "Report what a repository's diff categories match (exit code 0 only when configured)"

        inputs (
            argumentMaybe<string> "path" |> desc "Worktree path (default: current directory)",
            optionMaybe<int> "--port" |> desc "Server port (default: 5000, env: TREEMON_PORT)"
        )

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
        addCommand addCmd
        addCommand removeCmd
        addCommand rootsCmd
        addCommand categoriesCmd
    }
