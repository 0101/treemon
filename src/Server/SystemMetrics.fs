module Server.SystemMetrics

open Shared

let private fileTimeToUint64 (ft: Win32.FILETIME) =
    (uint64 ft.dwHighDateTime <<< 32) ||| uint64 ft.dwLowDateTime

type private CpuSample =
    { Idle: uint64; Kernel: uint64; User: uint64 }

let private readCpuTimes () =
    let mutable idle = Win32.FILETIME()
    let mutable kernel = Win32.FILETIME()
    let mutable user = Win32.FILETIME()
    if Win32.GetSystemTimes(&idle, &kernel, &user) then
        Some { Idle = fileTimeToUint64 idle; Kernel = fileTimeToUint64 kernel; User = fileTimeToUint64 user }
    else
        None

let private readMemory () =
    let mutable status = Win32.MEMORYSTATUSEX()
    status.dwLength <- uint32 (System.Runtime.InteropServices.Marshal.SizeOf<Win32.MEMORYSTATUSEX>())
    if Win32.GlobalMemoryStatusEx(&status) then
        let totalMb = int (status.ullTotalPhys / 1048576UL)
        let usedMb = totalMb - int (status.ullAvailPhys / 1048576UL)
        Some (usedMb, totalMb)
    else
        None

let private cpuState = ref Option<CpuSample>.None

let private computeCpuPercent (prev: CpuSample) (curr: CpuSample) =
    let idleDelta = curr.Idle - prev.Idle
    let kernelDelta = curr.Kernel - prev.Kernel
    let userDelta = curr.User - prev.User
    let totalDelta = kernelDelta + userDelta
    if totalDelta = 0UL then 0.0
    else
        let busy = totalDelta - idleDelta
        System.Math.Round(float busy / float totalDelta * 100.0, 1)

let getSystemMetrics () : SystemMetrics option =
    let cpuPercent =
        match readCpuTimes () with
        | None -> None
        | Some curr ->
            let prev = System.Threading.Interlocked.Exchange(cpuState, Some curr)
            match prev with
            | None -> Some 0.0
            | Some prev -> Some (computeCpuPercent prev curr)

    match cpuPercent, readMemory () with
    | Some cpu, Some (usedMb, totalMb) ->
        Some { CpuPercent = cpu; MemoryUsedMb = usedMb; MemoryTotalMb = totalMb }
    | _ -> None
