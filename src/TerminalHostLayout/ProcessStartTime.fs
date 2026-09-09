namespace Treemon.TerminalHosting

open System
open System.Diagnostics
open System.IO

/// A process start time that every reader agrees on, so a recorded value can be compared for exact
/// equality later to answer "is this still the process I recorded, or one that reused its pid".
///
/// Windows takes the value from the kernel's FILETIME, which is exact and stable. .NET's Linux value
/// is not usable as an identity: it is derived as (now - uptime) + the process's own jiffies, and the
/// boot-time term is recomputed on every call, so two reads of one live process disagree by
/// microseconds. Recomputing it from the integers /proc publishes - the boot time in `btime` and the
/// process's start in USER_HZ ticks - yields the same number for every reader and every read.
[<RequireQualifiedAccess>]
module ProcessStartTime =
    /// /proc reports a process start time in USER_HZ, which the kernel fixes at 100 for this
    /// interface regardless of the configured clock rate.
    let [<Literal>] private UserHz = 100L

    let private bootTimeUtcTicks () =
        File.ReadLines "/proc/stat"
        |> Seq.tryPick (fun line ->
            if line.StartsWith("btime ", StringComparison.Ordinal) then
                match Int64.TryParse(line.Substring 6) with
                | true, seconds -> Some(DateTime.UnixEpoch.Ticks + seconds * TimeSpan.TicksPerSecond)
                | _ -> None
            else
                None)

    /// `/proc/<pid>/stat` is `pid (comm) state ...` and comm can contain both spaces and ')', so the
    /// fields are counted from after the last ')'. That makes state the first of them, which puts
    /// starttime - field 22 overall - at index 19.
    let private startTicksSinceBoot pid =
        let stat = File.ReadAllText $"/proc/{pid}/stat"

        stat.Substring(stat.LastIndexOf(')') + 1).Split(' ', StringSplitOptions.RemoveEmptyEntries)
        |> Array.tryItem 19
        |> Option.bind (fun field ->
            match Int64.TryParse field with
            | true, ticks -> Some ticks
            | _ -> None)

    /// Only Linux needs the correction. Windows reads the kernel's FILETIME, and macOS reads the
    /// start time recorded in the kernel's proc structure through sysctl; both are exact and repeat
    /// the same value for the same process, which is all an identity comparison needs.
    let utcTicks (proc: Process) =
        if not (OperatingSystem.IsLinux()) then
            proc.StartTime.ToUniversalTime().Ticks
        else
            // Falling back to the unstable reading keeps a process without a readable /proc entry
            // behaving as it did before, which for an identity check means "does not match".
            try
                match bootTimeUtcTicks (), startTicksSinceBoot proc.Id with
                | Some bootTicks, Some sinceBoot ->
                    bootTicks + sinceBoot * TimeSpan.TicksPerSecond / UserHz
                | _ -> proc.StartTime.ToUniversalTime().Ticks
            with
            | :? IOException
            | :? UnauthorizedAccessException -> proc.StartTime.ToUniversalTime().Ticks
