namespace TerminalHost

open System
open System.Collections
open System.ComponentModel
open System.Diagnostics
open System.Runtime.InteropServices
open System.Text
open System.Threading
open Microsoft.Win32.SafeHandles

type JobProcessStart = { Executable: string; Arguments: string list; WorkingDirectory: string; Environment: (string * string) list }

type OwnedJobProcess = private { ProcessHandle: SafeFileHandle; ThreadHandle: SafeFileHandle; Pid: int; StartTimeUtcTicks: int64; BeginClose: unit -> Result<(unit -> Result<unit, string>), string> }

[<RequireQualifiedAccess>]
module JobProcess =
    let private CreateSuspended, CreateUnicodeEnvironment = 0x00000004u, 0x00000400u
    let private JobObjectBasicProcessIdListClass, JobObjectExtendedLimitInformationClass = 3, 9
    let private JobObjectLimitKillOnJobClose = 0x00002000u
    let private ProcessQueryLimitedInformation, ProcessSynchronize, ProcessTerminate =
        0x00001000u, 0x00100000u, 0x00000001u
    let private SnapshotProcesses = 0x00000002u
    let private ErrorNoMoreFiles, ErrorInvalidParameter, ErrorMoreData = 18, 87, 234
    let private MaximumOwnedProcesses, MaximumProcessSnapshotEntries = 4096, 32768
    let private WaitObject0, WaitTimeout, WaitFailed = 0u, 258u, UInt32.MaxValue

    [<Struct>] type internal ProcessIdentity = { ProcessId: int; StartTimeUtcTicks: int64 }
    [<Struct>] type internal ProcessTreeEntry = { ProcessId: int; ParentProcessId: int }

    type internal ProcessTreeSnapshot = { CapturedAtUtcTicks: int64; Entries: ProcessTreeEntry list }
    type internal CleanupOperations = { Capture: Set<ProcessIdentity> -> Set<ProcessIdentity>; CloseJob: unit -> unit; DisposeHandles: unit -> unit; WaitForExit: ProcessIdentity list -> ProcessIdentity list; Terminate: ProcessIdentity -> unit }
    type private CleanupState = Ready | Prepared of Set<ProcessIdentity> | JobClosed of Set<ProcessIdentity> | Complete
    exception private CleanupFailure of string

    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type private StartupInfo =
        val mutable Cb: uint32; val mutable Reserved: nativeint; val mutable Desktop: nativeint; val mutable Title: nativeint; val mutable X: uint32; val mutable Y: uint32
        val mutable XSize: uint32; val mutable YSize: uint32; val mutable XCountChars: uint32; val mutable YCountChars: uint32; val mutable FillAttribute: uint32; val mutable Flags: uint32
        val mutable ShowWindow: uint16; val mutable Reserved2: uint16; val mutable Reserved2Pointer: nativeint; val mutable StandardInput: nativeint; val mutable StandardOutput: nativeint; val mutable StandardError: nativeint
    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type private ProcessInformation =
        val mutable ProcessHandle: nativeint; val mutable ThreadHandle: nativeint; val mutable ProcessId: uint32; val mutable ThreadId: uint32
    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type private FileTime =
        val mutable LowDateTime: uint32; val mutable HighDateTime: uint32
    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type private IoCounters =
        val mutable ReadOperationCount: uint64; val mutable WriteOperationCount: uint64; val mutable OtherOperationCount: uint64; val mutable ReadTransferCount: uint64; val mutable WriteTransferCount: uint64; val mutable OtherTransferCount: uint64
    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type private BasicLimitInformation =
        val mutable PerProcessUserTimeLimit: int64; val mutable PerJobUserTimeLimit: int64; val mutable LimitFlags: uint32; val mutable MinimumWorkingSetSize: unativeint; val mutable MaximumWorkingSetSize: unativeint
        val mutable ActiveProcessLimit: uint32; val mutable Affinity: unativeint; val mutable PriorityClass: uint32; val mutable SchedulingClass: uint32
    [<Struct; StructLayout(LayoutKind.Sequential)>]
    type private ExtendedLimitInformation =
        val mutable BasicLimitInformation: BasicLimitInformation; val mutable IoInfo: IoCounters; val mutable ProcessMemoryLimit: unativeint; val mutable JobMemoryLimit: unativeint; val mutable PeakProcessMemoryUsed: unativeint; val mutable PeakJobMemoryUsed: unativeint
    [<Struct; StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)>]
    type private ProcessEntry32 =
        val mutable Size: uint32; val mutable UsageCount: uint32; val mutable ProcessId: uint32; val mutable DefaultHeapId: unativeint; val mutable ModuleId: uint32; val mutable ThreadCount: uint32
        val mutable ParentProcessId: uint32; val mutable BasePriority: int32; val mutable Flags: uint32; [<MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)>] val mutable ExecutableName: string

    [<DllImport("kernel32.dll", EntryPoint = "CreateJobObjectW", SetLastError = true)>] extern SafeFileHandle private CreateJobObject(nativeint jobAttributes, nativeint name)
    [<DllImport("kernel32.dll", SetLastError = true)>] extern bool private SetInformationJobObject(SafeFileHandle job, int informationClass, nativeint information, uint32 informationLength)
    [<DllImport("kernel32.dll", SetLastError = true)>] extern bool private QueryInformationJobObject(SafeFileHandle job, int informationClass, nativeint information, uint32 informationLength, uint32& returnLength)
    [<DllImport("kernel32.dll", EntryPoint = "CreateProcessW", CharSet = CharSet.Unicode, SetLastError = true)>] extern bool private CreateProcess(string applicationName, StringBuilder commandLine, nativeint processAttributes, nativeint threadAttributes, bool inheritHandles, uint32 creationFlags, nativeint environment, string currentDirectory, StartupInfo& startupInfo, ProcessInformation& processInformation)
    [<DllImport("kernel32.dll", SetLastError = true)>] extern bool private AssignProcessToJobObject(SafeFileHandle job, SafeFileHandle processHandle)
    [<DllImport("kernel32.dll", SetLastError = true)>] extern uint32 private ResumeThread(SafeFileHandle thread)
    [<DllImport("kernel32.dll", SetLastError = true)>] extern bool private TerminateProcess(SafeFileHandle processHandle, uint32 exitCode)
    [<DllImport("kernel32.dll", SetLastError = true)>] extern bool private GetProcessTimes(SafeFileHandle processHandle, FileTime& creationTime, FileTime& exitTime, FileTime& kernelTime, FileTime& userTime)
    [<DllImport("kernel32.dll", SetLastError = true)>] extern uint32 private WaitForSingleObject(SafeFileHandle handle, uint32 milliseconds)
    [<DllImport("kernel32.dll", SetLastError = true)>] extern SafeFileHandle private OpenProcess(uint32 desiredAccess, bool inheritHandle, uint32 processId)
    [<DllImport("kernel32.dll", SetLastError = true)>] extern SafeFileHandle private CreateToolhelp32Snapshot(uint32 flags, uint32 processId)
    [<DllImport("kernel32.dll", EntryPoint = "Process32FirstW", CharSet = CharSet.Unicode, SetLastError = true)>] extern bool private Process32First(SafeFileHandle snapshot, ProcessEntry32& entry)
    [<DllImport("kernel32.dll", EntryPoint = "Process32NextW", CharSet = CharSet.Unicode, SetLastError = true)>] extern bool private Process32Next(SafeFileHandle snapshot, ProcessEntry32& entry)

    let private win32Error operation = $"{operation} failed with Win32 error {Marshal.GetLastWin32Error()}"

    let private quoteArgument (argument: string) =
        if argument.Length > 0 && not (argument |> Seq.exists (fun character -> Char.IsWhiteSpace character || character = '"')) then argument
        else
            let rec escape index pendingBackslashes pieces =
                if index = argument.Length then String('\\', pendingBackslashes * 2) :: pieces
                else
                    match argument[index] with
                    | '\\' -> escape (index + 1) (pendingBackslashes + 1) pieces
                    | '"' -> escape (index + 1) 0 ((String('\\', pendingBackslashes * 2 + 1) + "\"") :: pieces)
                    | character -> escape (index + 1) 0 ((String('\\', pendingBackslashes) + string character) :: pieces)
            escape 0 0 [] |> List.rev |> String.concat "" |> fun escaped -> $"\"{escaped}\""

    let internal commandLine executable arguments =
        executable :: arguments |> List.map quoteArgument |> String.concat " "

    let private environmentBlock additions =
        let inherited =
            Environment.GetEnvironmentVariables() |> Seq.cast<DictionaryEntry>
            |> Seq.choose (fun entry ->
                match entry.Key, entry.Value with
                | (:? string as key), (:? string as value) -> Some(key, value)
                | _ -> None)
            |> Seq.toList
        let upsert variables (name, value) =
            (name, value) :: (variables |> List.filter (fun (existing, _) ->
                not (String.Equals(existing, name, StringComparison.OrdinalIgnoreCase))))
        additions
        |> List.fold upsert inherited
        |> List.sortWith (fun (left, _) (right, _) -> StringComparer.OrdinalIgnoreCase.Compare(left, right))
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
            if SetInformationJobObject(job, JobObjectExtendedLimitInformationClass, pointer, uint32 size) then Ok()
            else Error(win32Error (nameof SetInformationJobObject))
        finally Marshal.FreeHGlobal pointer

    let private processStartTime (processHandle: SafeFileHandle) =
        // GetProcessTimes fills Win32 FILETIME structs through byrefs.
        let mutable creation = FileTime()
        let mutable exitTime = FileTime()
        let mutable kernel = FileTime()
        let mutable user = FileTime()
        if GetProcessTimes(processHandle, &creation, &exitTime, &kernel, &user) then
            let fileTime = (int64 creation.HighDateTime <<< 32) ||| int64 creation.LowDateTime
            Ok(DateTime.FromFileTimeUtc(fileTime).Ticks)
        else Error(win32Error (nameof GetProcessTimes))

    let private descendantPids (roots: ProcessIdentity list) entries =
        let children =
            entries |> List.fold (fun map entry ->
                map |> Map.change entry.ParentProcessId (fun current -> Some(entry.ProcessId :: Option.defaultValue [] current))) Map.empty
        let rootPids = roots |> List.map _.ProcessId |> Set.ofList
        let rec collect pending visited descendants =
            match pending with
            | [] -> descendants
            | (processId, _) :: remaining when Set.contains processId visited -> collect remaining visited descendants
            | (processId, rootStartTicks) :: remaining ->
                let next =
                    children |> Map.tryFind processId |> Option.defaultValue [] |> List.filter (fun child -> not (Set.contains child rootPids))
                if Map.count descendants + next.Length > MaximumOwnedProcesses then
                    raise (CleanupFailure "Terminal descendant snapshot exceeded its process limit")
                else
                    let updated =
                        next |> List.fold (fun current child ->
                            current |> Map.change child (function
                                | None -> Some rootStartTicks
                                | Some existing -> Some(min existing rootStartTicks))) descendants
                    collect ((next |> List.map (fun child -> child, rootStartTicks)) @ remaining)
                        (Set.add processId visited) updated
        collect (roots |> List.map (fun root -> root.ProcessId, root.StartTimeUtcTicks)) Set.empty Map.empty

    let internal captureOwnershipWith jobMembers processTree resolveIdentity (known: Set<ProcessIdentity>) =
        let memberCapturedAt, memberPids = jobMembers ()
        let members =
            memberPids |> List.choose (fun processId ->
                resolveIdentity processId |> Option.filter (fun identity -> identity.StartTimeUtcTicks <= memberCapturedAt))
        let tree = processTree ()
        let roots =
            Set.union known (Set.ofList members)
            |> Set.filter (fun root ->
                match resolveIdentity root.ProcessId with
                | Some current when current <> root && current.StartTimeUtcTicks <= tree.CapturedAtUtcTicks -> false
                | _ -> true)
            |> Set.toList
        let knownByPid =
            known |> Seq.groupBy _.ProcessId
            |> Seq.map (fun (processId, identities) -> processId, identities |> Seq.map _.StartTimeUtcTicks |> Set.ofSeq)
            |> Map.ofSeq
        let descendants =
            descendantPids roots tree.Entries
            |> Map.toList
            |> List.choose (fun (processId, rootStartTicks) ->
                resolveIdentity processId
                |> Option.filter (fun identity ->
                    identity.StartTimeUtcTicks >= rootStartTicks
                    && identity.StartTimeUtcTicks <= tree.CapturedAtUtcTicks
                    && (knownByPid |> Map.tryFind processId |> Option.forall (Set.contains identity.StartTimeUtcTicks))))
        Set.union (Set.ofList members) (Set.ofList descendants)

    let private unresolvedMessage (identities: ProcessIdentity list) =
        let shown =
            identities |> List.truncate 16
            |> List.map (fun identity -> $"{identity.ProcessId}@{identity.StartTimeUtcTicks}") |> String.concat ", "
        $"Terminal cleanup left {identities.Length} exact process identities: {shown}"

    let internal createCleanup (operations: CleanupOperations) =
        let gate = obj()
        // Cleanup state is confined to this one-shot Win32 resource owner; retries must retain exact identities after the Job handle closes.
        let mutable state = Ready

        let protect action =
            try action (); Ok()
            with
            | CleanupFailure error -> Error error
            | error -> Error $"Terminal process cleanup failed ({error.GetType().Name})"

        let finishClosed identities =
            match operations.WaitForExit(Set.toList identities) with
            | [] -> state <- Complete
            | survivors ->
                survivors |> List.iter (fun identity -> try operations.Terminate identity with _ -> ())
                match operations.WaitForExit survivors with
                | [] -> state <- Complete
                | remaining -> raise (CleanupFailure(unresolvedMessage remaining))
        let complete () =
            lock gate (fun () ->
                protect (fun () ->
                    match state with
                    | Complete -> ()
                    | JobClosed identities -> finishClosed identities
                    | Ready -> raise (CleanupFailure "Terminal cleanup was not prepared")
                    | Prepared initial ->
                        let identities = Set.union initial (operations.Capture initial)
                        operations.CloseJob(); state <- JobClosed identities; operations.DisposeHandles()
                        finishClosed identities))
        fun () ->
            lock gate (fun () ->
                match state with
                | Ready ->
                    protect (fun () -> state <- Prepared(operations.Capture Set.empty)) |> Result.map (fun () -> complete)
                | Prepared _ | JobClosed _ | Complete -> Ok complete)

    let private queryJobMembers (job: SafeFileHandle) =
        let rec query capacity =
            let byteCount = 8 + capacity * IntPtr.Size
            let buffer = Marshal.AllocHGlobal byteCount
            try
                Marshal.WriteInt32(buffer, 0, 0); Marshal.WriteInt32(buffer, 4, 0)
                let mutable returned = 0u
                let succeeded = QueryInformationJobObject(job, JobObjectBasicProcessIdListClass, buffer, uint32 byteCount, &returned)
                let assigned = max 0 (Marshal.ReadInt32(buffer, 0))
                let listed = max 0 (Marshal.ReadInt32(buffer, 4))
                let errorCode = if succeeded then 0 else Marshal.GetLastWin32Error()
                if assigned > MaximumOwnedProcesses || listed > MaximumOwnedProcesses then
                    raise (CleanupFailure "Terminal Job Object membership exceeded its process limit")
                elif not succeeded && errorCode = ErrorMoreData then
                    let nextCapacity = min MaximumOwnedProcesses (max (capacity * 2) assigned)
                    if nextCapacity <= capacity then
                        raise (CleanupFailure "Terminal Job Object membership changed beyond its process limit")
                    else query nextCapacity
                elif not succeeded then
                    raise (CleanupFailure $"{nameof QueryInformationJobObject} failed with Win32 error {errorCode}")
                else
                    let processIds =
                        [ 0 .. listed - 1 ]
                        |> List.map (fun index -> Marshal.ReadIntPtr(buffer, 8 + index * IntPtr.Size).ToInt64())
                    if processIds |> List.exists (fun processId -> processId <= 0L || processId > int64 Int32.MaxValue) then
                        raise (CleanupFailure "Terminal Job Object returned an invalid process identity")
                    else DateTime.UtcNow.Ticks, processIds |> List.map int
            finally Marshal.FreeHGlobal buffer
        query 64

    let private processTreeSnapshot () =
        use snapshot = CreateToolhelp32Snapshot(SnapshotProcesses, 0u)
        if snapshot.IsInvalid then
            raise (CleanupFailure(win32Error (nameof CreateToolhelp32Snapshot)))
        else
            let capturedAt = DateTime.UtcNow.Ticks
            // Toolhelp fills one mutable byref record for each recursive read.
            let mutable entry = ProcessEntry32()
            entry.Size <- uint32 (Marshal.SizeOf<ProcessEntry32>())
            if not (Process32First(snapshot, &entry)) then
                raise (CleanupFailure(win32Error (nameof Process32First)))
            else
                let rec collect count entries =
                    let current = { ProcessId = int entry.ProcessId; ParentProcessId = int entry.ParentProcessId }
                    entry.Size <- uint32 (Marshal.SizeOf<ProcessEntry32>())
                    if Process32Next(snapshot, &entry) then
                        if count >= MaximumProcessSnapshotEntries then
                            raise (CleanupFailure "System process snapshot exceeded its process limit")
                        else collect (count + 1) (current :: entries)
                    elif Marshal.GetLastWin32Error() = ErrorNoMoreFiles then
                        { CapturedAtUtcTicks = capturedAt; Entries = List.rev (current :: entries) }
                    else
                        raise (CleanupFailure(win32Error (nameof Process32Next)))
                collect 1 []

    let private withProcess access processId action =
        use processHandle = OpenProcess(access, false, uint32 processId)
        if processHandle.IsInvalid then
            let errorCode = Marshal.GetLastWin32Error()
            if errorCode = ErrorInvalidParameter then None
            else
                raise (CleanupFailure $"{nameof OpenProcess} failed for PID {processId} with Win32 error {errorCode}")
        else Some(action processHandle)

    let private resolveIdentity processId =
        withProcess (ProcessQueryLimitedInformation ||| ProcessSynchronize) processId (fun processHandle ->
            match processStartTime processHandle with
            | Ok startTime -> { ProcessId = processId; StartTimeUtcTicks = startTime }
            | Error error -> raise (CleanupFailure error))

    let private exactProcessIsAlive (identity: ProcessIdentity) =
        withProcess (ProcessQueryLimitedInformation ||| ProcessSynchronize) identity.ProcessId (fun processHandle ->
            match processStartTime processHandle with
            | Error error -> raise (CleanupFailure error)
            | Ok startTime when startTime <> identity.StartTimeUtcTicks -> false
            | Ok _ ->
                match WaitForSingleObject(processHandle, 0u) with
                | status when status = WaitTimeout -> true
                | status when status = WaitObject0 -> false
                | status when status = WaitFailed -> raise (CleanupFailure(win32Error (nameof WaitForSingleObject)))
                | status -> raise (CleanupFailure $"Unexpected process wait status {status}"))
        |> Option.defaultValue false

    let private waitForExactExit (identities: ProcessIdentity list) =
        let stopwatch = Stopwatch.StartNew()
        let rec live remaining survivors =
            match remaining with
            | [] -> List.rev survivors
            | identity :: tail -> live tail (if exactProcessIsAlive identity then identity :: survivors else survivors)
        let rec wait remaining =
            match live remaining [] with
            | [] -> []
            | survivors when stopwatch.Elapsed >= TimeSpan.FromSeconds 5.0 -> survivors
            | survivors ->
                Thread.Sleep 25
                wait survivors
        wait identities

    let private terminateExact (identity: ProcessIdentity) =
        withProcess (ProcessQueryLimitedInformation ||| ProcessSynchronize ||| ProcessTerminate)
            identity.ProcessId (fun processHandle ->
                match processStartTime processHandle with
                | Error error -> raise (CleanupFailure error)
                | Ok startTime when startTime <> identity.StartTimeUtcTicks -> ()
                | Ok _ when WaitForSingleObject(processHandle, 0u) = WaitObject0 -> ()
                | Ok _ when TerminateProcess(processHandle, 1u) -> ()
                | Ok _ when WaitForSingleObject(processHandle, 0u) = WaitObject0 -> ()
                | Ok _ -> raise (CleanupFailure(win32Error (nameof TerminateProcess))))
        |> ignore

    let private cleanupOperations (job: SafeFileHandle) (processHandle: SafeFileHandle) (thread: SafeFileHandle) =
        { Capture =
            captureOwnershipWith (fun () -> queryJobMembers job) processTreeSnapshot resolveIdentity
          CloseJob =
            fun () -> if not job.IsClosed then job.Dispose()
          DisposeHandles =
            fun () ->
                if not thread.IsClosed then thread.Dispose()
                if not processHandle.IsClosed then processHandle.Dispose()
          WaitForExit = waitForExactExit
          Terminate = terminateExact }

    let private failCreatedProcess message (job: SafeFileHandle) (processHandle: SafeFileHandle) (thread: SafeFileHandle) =
        TerminateProcess(processHandle, 1u) |> ignore; WaitForSingleObject(processHandle, 5_000u) |> ignore
        thread.Dispose(); processHandle.Dispose(); job.Dispose()
        Error message

    let start specification =
        if not (OperatingSystem.IsWindows()) then Error "Terminal process ownership requires Windows Job Objects"
        elif String.IsNullOrWhiteSpace specification.Executable || String.IsNullOrWhiteSpace specification.WorkingDirectory then
            Error "Terminal process launch configuration is invalid"
        elif specification.Environment |> List.exists (fun (name, value) ->
            String.IsNullOrWhiteSpace name || name.Contains('=') || name.Contains('\u0000') || value.Contains('\u0000')) then
            Error "Terminal process environment is invalid"
        else
            use job = CreateJobObject(0n, 0n)
            if job.IsInvalid then Error(win32Error (nameof CreateJobObject))
            else
                match configureKillOnClose job with
                | Error error -> Error error
                | Ok() ->
                    let environment = specification.Environment |> environmentBlock |> Marshal.StringToHGlobalUni
                    try
                        // CreateProcessW writes both structs through byrefs.
                        let mutable startup = StartupInfo()
                        startup.Cb <- uint32 (Marshal.SizeOf<StartupInfo>())
                        let mutable processInformation = ProcessInformation()
                        let command = StringBuilder(commandLine specification.Executable specification.Arguments)
                        if not (CreateProcess(specification.Executable, command, 0n, 0n, false,
                            CreateSuspended ||| CreateUnicodeEnvironment, environment,
                            specification.WorkingDirectory, &startup, &processInformation)) then
                            Error(win32Error (nameof CreateProcess))
                        else
                            let processHandle = new SafeFileHandle(processInformation.ProcessHandle, true)
                            let thread = new SafeFileHandle(processInformation.ThreadHandle, true)
                            if not (AssignProcessToJobObject(job, processHandle)) then
                                failCreatedProcess (win32Error (nameof AssignProcessToJobObject)) job processHandle thread
                            else
                                match processStartTime processHandle with
                                | Error error -> failCreatedProcess error job processHandle thread
                                | Ok startTime ->
                                    if ResumeThread(thread) = UInt32.MaxValue then
                                        failCreatedProcess (win32Error (nameof ResumeThread)) job processHandle thread
                                    else
                                        let jobHandle = new SafeFileHandle(job.DangerousGetHandle(), true)
                                        job.SetHandleAsInvalid()
                                        Ok
                                            { ProcessHandle = processHandle; ThreadHandle = thread
                                              Pid = int processInformation.ProcessId; StartTimeUtcTicks = startTime
                                              BeginClose = cleanupOperations jobHandle processHandle thread |> createCleanup }
                    finally
                        Marshal.FreeHGlobal environment

    let processId (owned: OwnedJobProcess) = owned.Pid
    let processStartTimeUtcTicks (owned: OwnedJobProcess) = owned.StartTimeUtcTicks

    let hasExited (owned: OwnedJobProcess) =
        try
            if owned.ProcessHandle.IsClosed || owned.ProcessHandle.IsInvalid then
                true
            else
                match WaitForSingleObject(owned.ProcessHandle, 0u) with
                | status when status = WaitTimeout -> false
                | status when status = WaitObject0 -> true
                | _ -> true
        with :? ObjectDisposedException -> true

    let beginClose (owned: OwnedJobProcess) = owned.BeginClose()

    let close (owned: OwnedJobProcess) = beginClose owned |> Result.bind (fun complete -> complete())
