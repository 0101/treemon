module Log

open System
open System.IO

[<RequireQualifiedAccess>]
type Destination =
    | Production
    | Isolated of directory: string option

let private configuredPathKey = "Treemon.Server.LogPath"
let private lockObj = obj ()

let private containsControlCharacter (value: string) =
    value |> Seq.exists Char.IsControl

let private tryResolveDirectory (workingDirectory: string) (directory: string) =
    if String.IsNullOrWhiteSpace directory then
        Error "Log directory must not be blank"
    elif containsControlCharacter directory then
        Error "Log directory must not contain control characters"
    else
        try
            let baseDirectory = Path.GetFullPath workingDirectory

            if Path.IsPathFullyQualified directory then
                Path.GetFullPath directory |> Ok
            else
                Path.GetFullPath(directory, baseDirectory) |> Ok
        with
        | :? ArgumentException
        | :? NotSupportedException
        | :? PathTooLongException ->
            Error "Log directory is not a valid filesystem path"

let internal resolvePath
    (workingDirectory: string)
    (tempDirectory: string)
    (port: int)
    (processId: int)
    (instanceId: Guid)
    (destination: Destination)
    =
    match destination with
    | Destination.Production ->
        tryResolveDirectory workingDirectory workingDirectory
        |> Result.map (fun directory ->
            Path.Combine(directory, "logs", "server.log"))
    | Destination.Isolated configuredDirectory ->
        let directory =
            match configuredDirectory with
            | Some path -> tryResolveDirectory workingDirectory path
            | None ->
                tryResolveDirectory workingDirectory tempDirectory
                |> Result.map (fun path ->
                    Path.Combine(path, "treemon", "server-logs"))

        directory
        |> Result.map (fun path ->
            Path.Combine(
                path,
                $"server-{port}-{processId}-{instanceId:N}.log"
            ))

let private fallbackLogPath =
    Path.Combine(
        Path.GetTempPath(),
        "treemon",
        "server-logs",
        $"process-{Environment.ProcessId}-{Guid.NewGuid():N}.log"
    )

// AppContext keeps the one-time destination process-local; unlike an environment variable, it
// cannot leak the production sink into terminals or other child processes.
let private logPath =
    lazy
        match AppContext.GetData(configuredPathKey) with
        | :? string as path -> path
        | _ -> fallbackLogPath

let private ensureLogDirectory (path: string) =
    path
    |> Path.GetDirectoryName
    |> Option.ofObj
    |> Option.filter (String.IsNullOrWhiteSpace >> not)
    |> Option.iter (Directory.CreateDirectory >> ignore)

let init path =
    if String.IsNullOrWhiteSpace path
       || containsControlCharacter path
       || not (Path.IsPathFullyQualified path) then
        invalidArg (nameof path) "Log path must be an absolute control-free path"

    if logPath.IsValueCreated then
        invalidOp "The log destination was already selected"

    AppContext.SetData(configuredPathKey, path)
    let selectedPath = logPath.Value

    try
        ensureLogDirectory selectedPath
        use stream =
            new FileStream(
                selectedPath,
                FileMode.Create,
                FileAccess.Write,
                FileShare.ReadWrite
            )
        ()
    with _ -> ()

let internal currentPath () = logPath.Value

let internal isSlowOperation (elapsed: TimeSpan) =
    elapsed >= TimeSpan.FromSeconds 5.0

let log (context: string) (message: string) =
    let timestamp = DateTimeOffset.Now.ToString("yyyy-MM-ddTHH:mm:ss.fff")
    let line = $"{timestamp} [{context}] {message}{Environment.NewLine}"
    lock lockObj (fun () ->
        try
            let path = logPath.Value
            ensureLogDirectory path
            use stream =
                new FileStream(
                    path,
                    FileMode.Append,
                    FileAccess.Write,
                    FileShare.ReadWrite
                )
            use writer = new StreamWriter(stream)
            writer.Write(line)
        with _ -> ())

let logException (context: string) (message: string) (error: exn) =
    log context $"{message}{Environment.NewLine}{error}"

let timed (context: string) (label: string) (work: Async<'T>) =
    async {
        let sw = Diagnostics.Stopwatch.StartNew()
        let! result = work
        sw.Stop()
        log context $"{label} completed in {sw.ElapsedMilliseconds}ms"
        return result
    }
