module Tests.TestUtils

open System
open System.Diagnostics
open System.IO
open System.Text.RegularExpressions
open NUnit.Framework

let resolveCmdShim (fileName: string) =
    if Path.GetExtension(fileName) = "" then
        let cmdPath = $"{fileName}.cmd"
        let pathDirs =
            Environment.GetEnvironmentVariable("PATH")
            |> Option.ofObj
            |> Option.map (fun p -> p.Split(Path.PathSeparator))
            |> Option.defaultValue [||]

        match Array.tryFind (fun dir -> File.Exists(Path.Combine(dir, cmdPath))) pathDirs with
        | Some dir -> Path.Combine(dir, cmdPath)
        | None -> fileName
    else
        fileName

let startProcess (fileName: string) (args: string) (workingDir: string) (envVars: (string * string) list) (redirectOutput: bool) =
    let resolved = resolveCmdShim fileName

    let psi =
        ProcessStartInfo(
            FileName = resolved,
            Arguments = args,
            WorkingDirectory = workingDir,
            UseShellExecute = false,
            RedirectStandardOutput = redirectOutput,
            RedirectStandardError = redirectOutput,
            CreateNoWindow = true
        )

    envVars |> List.iter (fun (k, v) -> psi.Environment.[k] <- v)
    Process.Start(psi)

let killProc (procOpt: Process option) =
    procOpt
    |> Option.iter (fun p ->
        try
            if not p.HasExited then
                p.Kill(entireProcessTree = true)

                match p.WaitForExit(10000) with
                | true -> ()
                | false ->
                    TestContext.Error.WriteLine(
                        $"Process {p.Id} did not exit within 10s after Kill")

            p.Dispose()
        with ex ->
            TestContext.Error.WriteLine($"Failed to kill process: {ex.Message}"))

let killOrphansOnPort (port: int) =
    let extractPidsFromNetstat (output: string) (port: int) =
        let pattern = Regex($@"TCP\s+\S+:{port}\s+\S+\s+LISTENING\s+(\d+)")
        pattern.Matches(output)
        |> Seq.cast<Match>
        |> Seq.map (fun m -> int m.Groups.[1].Value)
        |> Seq.distinct

    try
        let psi =
            ProcessStartInfo(
                FileName = "netstat",
                Arguments = "-ano",
                UseShellExecute = false,
                RedirectStandardOutput = true,
                CreateNoWindow = true
            )

        use proc = Process.Start(psi)
        let output = proc.StandardOutput.ReadToEnd()
        proc.WaitForExit(5000) |> ignore

        extractPidsFromNetstat output port
        |> Seq.iter (fun pid ->
            try
                use orphan = Process.GetProcessById(pid)

                if not orphan.HasExited then
                    TestContext.Out.WriteLine($"[Cleanup] Killing orphaned process PID {pid} on port {port}")
                    orphan.Kill(entireProcessTree = true)
                    orphan.WaitForExit(5000) |> ignore
            with :? ArgumentException ->
                ())
    with ex ->
        TestContext.Error.WriteLine($"[Cleanup] Failed to scan port {port}: {ex.Message}")
