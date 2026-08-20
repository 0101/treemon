namespace TerminalHost

open System
open System.Collections
open System.ComponentModel
open System.Runtime.InteropServices
open System.Text
open Microsoft.Win32.SafeHandles

type JobProcessStart =
    { Executable: string
      Arguments: string list
      WorkingDirectory: string
      Environment: (string * string) list }

type OwnedJobProcess =
    private
        { JobHandle: SafeFileHandle
          ProcessHandle: SafeFileHandle
          ThreadHandle: SafeFileHandle
          Pid: int
          StartTimeUtcTicks: int64 }

[<RequireQualifiedAccess>]
module JobProcess =
    [<Literal>]
    let private CreateSuspended = 0x00000004u

    [<Literal>]
    let private CreateUnicodeEnvironment = 0x00000400u

    [<Literal>]
    let private JobObjectExtendedLimitInformationClass = 9

    [<Literal>]
    let private JobObjectLimitKillOnJobClose = 0x00002000u

    [<Literal>]
    let private WaitObject0 = 0u

    [<Literal>]
    let private WaitTimeout = 258u

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type private StartupInfo =
        val mutable Cb: uint32
        val mutable Reserved: nativeint
        val mutable Desktop: nativeint
        val mutable Title: nativeint
        val mutable X: uint32
        val mutable Y: uint32
        val mutable XSize: uint32
        val mutable YSize: uint32
        val mutable XCountChars: uint32
        val mutable YCountChars: uint32
        val mutable FillAttribute: uint32
        val mutable Flags: uint32
        val mutable ShowWindow: uint16
        val mutable Reserved2: uint16
        val mutable Reserved2Pointer: nativeint
        val mutable StandardInput: nativeint
        val mutable StandardOutput: nativeint
        val mutable StandardError: nativeint

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type private ProcessInformation =
        val mutable ProcessHandle: nativeint
        val mutable ThreadHandle: nativeint
        val mutable ProcessId: uint32
        val mutable ThreadId: uint32

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type private FileTime =
        val mutable LowDateTime: uint32
        val mutable HighDateTime: uint32

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type private IoCounters =
        val mutable ReadOperationCount: uint64
        val mutable WriteOperationCount: uint64
        val mutable OtherOperationCount: uint64
        val mutable ReadTransferCount: uint64
        val mutable WriteTransferCount: uint64
        val mutable OtherTransferCount: uint64

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type private BasicLimitInformation =
        val mutable PerProcessUserTimeLimit: int64
        val mutable PerJobUserTimeLimit: int64
        val mutable LimitFlags: uint32
        val mutable MinimumWorkingSetSize: unativeint
        val mutable MaximumWorkingSetSize: unativeint
        val mutable ActiveProcessLimit: uint32
        val mutable Affinity: unativeint
        val mutable PriorityClass: uint32
        val mutable SchedulingClass: uint32

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type private ExtendedLimitInformation =
        val mutable BasicLimitInformation: BasicLimitInformation
        val mutable IoInfo: IoCounters
        val mutable ProcessMemoryLimit: unativeint
        val mutable JobMemoryLimit: unativeint
        val mutable PeakProcessMemoryUsed: unativeint
        val mutable PeakJobMemoryUsed: unativeint

    [<DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true)>]
    extern SafeFileHandle private CreateJobObject(nativeint jobAttributes, nativeint name)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern bool private SetInformationJobObject(
        SafeFileHandle job,
        int informationClass,
        nativeint information,
        uint32 informationLength
    )

    [<DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)>]
    extern bool private CreateProcess(
        string applicationName,
        StringBuilder commandLine,
        nativeint processAttributes,
        nativeint threadAttributes,
        bool inheritHandles,
        uint32 creationFlags,
        nativeint environment,
        string currentDirectory,
        StartupInfo& startupInfo,
        ProcessInformation& processInformation
    )

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern bool private AssignProcessToJobObject(SafeFileHandle job, SafeFileHandle processHandle)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern uint32 private ResumeThread(SafeFileHandle thread)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern bool private TerminateProcess(SafeFileHandle processHandle, uint32 exitCode)

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern bool private GetProcessTimes(
        SafeFileHandle processHandle,
        FileTime& creationTime,
        FileTime& exitTime,
        FileTime& kernelTime,
        FileTime& userTime
    )

    [<DllImport("kernel32.dll", SetLastError = true)>]
    extern uint32 private WaitForSingleObject(SafeFileHandle handle, uint32 milliseconds)

    let private win32Error operation =
        let code = Marshal.GetLastWin32Error()
        $"{operation} failed with Win32 error {code}"

    let private quoteArgument (argument: string) =
        if
            argument.Length > 0
            && not (argument |> Seq.exists (fun character -> Char.IsWhiteSpace character || character = '"'))
        then
            argument
        else
            let rec escape index pendingBackslashes pieces =
                if index = argument.Length then
                    String('\\', pendingBackslashes * 2) :: pieces
                else
                    match argument[index] with
                    | '\\' ->
                        escape (index + 1) (pendingBackslashes + 1) pieces
                    | '"' ->
                        let escaped = String('\\', pendingBackslashes * 2 + 1) + "\""
                        escape (index + 1) 0 (escaped :: pieces)
                    | character ->
                        let literal = String('\\', pendingBackslashes) + string character
                        escape (index + 1) 0 (literal :: pieces)

            escape 0 0 []
            |> List.rev
            |> String.concat ""
            |> fun escaped -> $"\"{escaped}\""

    let internal commandLine executable arguments =
        executable :: arguments
        |> List.map quoteArgument
        |> String.concat " "

    let private environmentBlock additions =
        let inherited =
            Environment.GetEnvironmentVariables()
            |> Seq.cast<DictionaryEntry>
            |> Seq.choose (fun entry ->
                match entry.Key, entry.Value with
                | (:? string as key), (:? string as value) -> Some(key, value)
                | _ -> None)
            |> Seq.toList

        let upsert variables (name, value) =
            (name, value)
            :: (variables
                |> List.filter (fun (existing, _) ->
                    not (String.Equals(existing, name, StringComparison.OrdinalIgnoreCase))))

        additions
        |> List.fold upsert inherited
        |> List.sortWith (fun (left, _) (right, _) ->
            StringComparer.OrdinalIgnoreCase.Compare(left, right))
        |> List.map (fun (name, value) -> $"{name}={value}")
        |> String.concat "\u0000"
        |> fun block -> block + "\u0000\u0000"

    let private configureKillOnClose (job: SafeFileHandle) =
        // These mutable structs are required by the Win32 byref marshalling boundary.
        let mutable information = ExtendedLimitInformation()
        information.BasicLimitInformation.LimitFlags <- JobObjectLimitKillOnJobClose
        let size = Marshal.SizeOf<ExtendedLimitInformation>()
        let pointer = Marshal.AllocHGlobal size

        try
            Marshal.StructureToPtr(information, pointer, false)

            if SetInformationJobObject(job, JobObjectExtendedLimitInformationClass, pointer, uint32 size) then
                Ok()
            else
                Error(win32Error "SetInformationJobObject")
        finally
            Marshal.FreeHGlobal pointer

    let private processStartTime (processHandle: SafeFileHandle) =
        // GetProcessTimes fills Win32 FILETIME structs through byrefs.
        let mutable creation = FileTime()
        let mutable exitTime = FileTime()
        let mutable kernel = FileTime()
        let mutable user = FileTime()

        if GetProcessTimes(processHandle, &creation, &exitTime, &kernel, &user) then
            let fileTime = (int64 creation.HighDateTime <<< 32) ||| int64 creation.LowDateTime
            Ok(DateTime.FromFileTimeUtc(fileTime).Ticks)
        else
            Error(win32Error "GetProcessTimes")

    let private closeHandles owned =
        if not owned.JobHandle.IsClosed then
            owned.JobHandle.Dispose()

        if not owned.ProcessHandle.IsClosed then
            WaitForSingleObject(owned.ProcessHandle, 5_000u) |> ignore

        if not owned.ThreadHandle.IsClosed then
            owned.ThreadHandle.Dispose()

        if not owned.ProcessHandle.IsClosed then
            owned.ProcessHandle.Dispose()

    let private failCreatedProcess
        message
        (job: SafeFileHandle)
        (processHandle: SafeFileHandle)
        (thread: SafeFileHandle)
        =
        TerminateProcess(processHandle, 1u) |> ignore
        WaitForSingleObject(processHandle, 5_000u) |> ignore
        thread.Dispose()
        processHandle.Dispose()
        job.Dispose()
        Error message

    let start specification =
        if not (OperatingSystem.IsWindows()) then
            Error "Terminal process ownership requires Windows Job Objects"
        elif
            String.IsNullOrWhiteSpace specification.Executable
            || String.IsNullOrWhiteSpace specification.WorkingDirectory
        then
            Error "Terminal process launch configuration is invalid"
        elif
            specification.Environment
            |> List.exists (fun (name, value) ->
                String.IsNullOrWhiteSpace name
                || name.Contains('=')
                || name.Contains('\u0000')
                || value.Contains('\u0000'))
        then
            Error "Terminal process environment is invalid"
        else
            use job = CreateJobObject(0n, 0n)

            if job.IsInvalid then
                Error(win32Error "CreateJobObject")
            else
                match configureKillOnClose job with
                | Error error -> Error error
                | Ok() ->
                    let block = environmentBlock specification.Environment
                    let environment = Marshal.StringToHGlobalUni block

                    try
                        // CreateProcessW writes both structs through byrefs.
                        let mutable startup = StartupInfo()
                        startup.Cb <- uint32 (Marshal.SizeOf<StartupInfo>())
                        let mutable processInformation = ProcessInformation()
                        let command = StringBuilder(commandLine specification.Executable specification.Arguments)

                        if
                            not (
                                CreateProcess(
                                    specification.Executable,
                                    command,
                                    0n,
                                    0n,
                                    false,
                                    CreateSuspended ||| CreateUnicodeEnvironment,
                                    environment,
                                    specification.WorkingDirectory,
                                    &startup,
                                    &processInformation
                                )
                            )
                        then
                            Error(win32Error "CreateProcess")
                        else
                            let processHandle = new SafeFileHandle(processInformation.ProcessHandle, true)
                            let thread = new SafeFileHandle(processInformation.ThreadHandle, true)

                            if not (AssignProcessToJobObject(job, processHandle)) then
                                failCreatedProcess
                                    (win32Error "AssignProcessToJobObject")
                                    job
                                    processHandle
                                    thread
                            else
                                match processStartTime processHandle with
                                | Error error ->
                                    failCreatedProcess error job processHandle thread
                                | Ok startTime ->
                                    if ResumeThread(thread) = UInt32.MaxValue then
                                        failCreatedProcess
                                            (win32Error "ResumeThread")
                                            job
                                            processHandle
                                            thread
                                    else
                                        let owned =
                                            { JobHandle = new SafeFileHandle(job.DangerousGetHandle(), true)
                                              ProcessHandle = processHandle
                                              ThreadHandle = thread
                                              Pid = int processInformation.ProcessId
                                              StartTimeUtcTicks = startTime }

                                        job.SetHandleAsInvalid()
                                        Ok owned
                    finally
                        Marshal.FreeHGlobal environment

    let processId owned = owned.Pid
    let processStartTimeUtcTicks owned = owned.StartTimeUtcTicks

    let hasExited owned =
        try
            if owned.ProcessHandle.IsClosed || owned.ProcessHandle.IsInvalid then
                true
            else
                match WaitForSingleObject(owned.ProcessHandle, 0u) with
                | status when status = WaitTimeout -> false
                | status when status = WaitObject0 -> true
                | _ -> true
        with :? ObjectDisposedException ->
            true

    let close owned = closeHandles owned
