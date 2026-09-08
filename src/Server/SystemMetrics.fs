module Server.SystemMetrics

open System
open System.Globalization
open System.IO
open System.Runtime.InteropServices
open System.Threading
open Shared

let private fileTimeToUint64 (ft: Win32.FILETIME) =
    (uint64 ft.dwHighDateTime <<< 32) ||| uint64 ft.dwLowDateTime

/// Busy time is always derived as `Total - Idle`, so every platform only has to report those two
/// counters: Windows kernel time already includes idle, and Linux folds iowait into idle.
type private CpuSample = { Idle: uint64; Total: uint64 }

let private readWindowsCpuTimes () =
    Win32.readSystemTimes ()
    |> Option.map (fun (idle, kernel, user) ->
        { Idle = fileTimeToUint64 idle
          Total = fileTimeToUint64 kernel + fileTimeToUint64 user })

let private readWindowsMemory () =
    Win32.readMemoryStatus ()
    |> Option.map (fun status ->
        let totalMb = int (status.ullTotalPhys / 1048576UL)
        let usedMb = totalMb - int (status.ullAvailPhys / 1048576UL)
        (usedMb, totalMb))

let private readProcLines path =
    try
        Some(File.ReadAllLines(path: string))
    with
    | :? IOException
    | :? UnauthorizedAccessException -> None

let private parseUInt64 (value: string) =
    match UInt64.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture) with
    | true, parsed -> Some parsed
    | _ -> None

let private whitespaceFields (line: string) =
    line.Split(' ', StringSplitOptions.RemoveEmptyEntries)

/// The first `/proc/stat` line aggregates every CPU as
/// `cpu user nice system idle iowait irq softirq steal guest guest_nice`. A core parked in iowait is
/// idle for this purpose, matching what Windows reports through GetSystemTimes. Only the first eight
/// counters are summed: the kernel adds guest time into `user` and `nice` as well as reporting it
/// again in `guest`/`guest_nice`, so totalling all ten would count a KVM host's guest time twice and
/// report less CPU than the machine is using.
let private accountedCpuCounters = 8

let private readLinuxCpuTimes () =
    readProcLines "/proc/stat"
    |> Option.bind Array.tryHead
    |> Option.filter _.StartsWith("cpu ")
    |> Option.bind (fun line ->
        let counters =
            whitespaceFields line
            |> Array.skip 1
            |> Array.map parseUInt64

        // A field that does not parse would shift every later one, moving idle off index 3, so a
        // partially readable line is treated as unreadable rather than silently misaligned.
        if counters.Length < accountedCpuCounters || Array.exists Option.isNone counters then
            None
        else
            let accounted = counters |> Array.take accountedCpuCounters |> Array.map Option.get
            Some { Idle = accounted[3] + accounted[4]; Total = Array.sum accounted })

/// `/proc/meminfo` reports kB as `MemTotal:  16316412 kB`. MemAvailable is the kernel's own estimate
/// of what a new workload could claim without swapping, so it is the analogue of `ullAvailPhys` —
/// MemFree alone would report reclaimable page cache as used.
let private readLinuxMemory () =
    readProcLines "/proc/meminfo"
    |> Option.bind (fun lines ->
        let field name =
            lines
            |> Array.tryFind _.StartsWith($"{name}:")
            |> Option.bind (whitespaceFields >> Array.tryItem 1)
            |> Option.bind parseUInt64

        match field "MemTotal", field "MemAvailable" with
        | Some totalKb, Some availableKb when totalKb >= availableKb ->
            Some(int ((totalKb - availableKb) / 1024UL), int (totalKb / 1024UL))
        | _ -> None)

// The previous CPU sample only exists to turn two monotonic counters into a rate, and the polling
// loop that reads it is an impure boundary, so the sample is swapped atomically rather than threaded
// through the call. A `let mutable` keeps that mutation visible at its one use site; a ref cell would
// hide the same thing behind an allocation.
let mutable private cpuState = Option<CpuSample>.None

let private computeCpuPercent (prev: CpuSample) (curr: CpuSample) =
    // These are unsigned counters that are supposed to only rise, but Linux documents that iowait
    // can go backwards, and idle folds iowait in. A bare subtraction would wrap to an enormous
    // number and report a CPU percentage far above 100, so a counter that moved backwards is read
    // as no movement, and idle can never exceed the total it is a part of.
    let delta (before: uint64) (after: uint64) = if after > before then after - before else 0UL

    let totalDelta = delta prev.Total curr.Total
    let idleDelta = min (delta prev.Idle curr.Idle) totalDelta

    if totalDelta = 0UL then
        0.0
    else
        Math.Round(float (totalDelta - idleDelta) / float totalDelta * 100.0, 1)

let private sampleSystemMetrics readCpuTimes readMemory : SystemMetrics option =
    let cpuPercent =
        match readCpuTimes () with
        | None -> None
        | Some curr ->
            match Interlocked.Exchange(&cpuState, Some curr) with
            | None -> None
            | Some prev -> Some(computeCpuPercent prev curr)

    match cpuPercent, readMemory () with
    | Some cpu, Some (usedMb, totalMb) ->
        Some { CpuPercent = cpu; MemoryUsedMb = usedMb; MemoryTotalMb = totalMb }
    | _ -> None

let getSystemMetrics () =
    if RuntimeInformation.IsOSPlatform(OSPlatform.Windows) then
        sampleSystemMetrics readWindowsCpuTimes readWindowsMemory
    elif RuntimeInformation.IsOSPlatform(OSPlatform.Linux) then
        sampleSystemMetrics readLinuxCpuTimes readLinuxMemory
    else
        None
