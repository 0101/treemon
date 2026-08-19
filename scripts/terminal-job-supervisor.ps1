param(
    [switch]$SelfTest
)

$ErrorActionPreference = "Stop"

$source = @'
using System;
using System.Collections;
using System.Collections.Generic;
using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

public sealed class TerminalJobOwner : IDisposable
{
    public const uint JobObjectLimitBreakawayOk = 0x00000800;
    public const uint JobObjectLimitSilentBreakawayOk = 0x00001000;
    public const uint JobObjectLimitKillOnJobClose = 0x00002000;
    public const uint CreateSuspended = 0x00000004;

    private const uint CreateNoWindow = 0x08000000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint StartfUseStdHandles = 0x00000100;
    private const uint ProcessQueryLimitedInformation = 0x00001000;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint Infinite = 0xffffffff;
    private const uint HandleFlagInherit = 0x00000001;
    private const int StdInputHandle = -10;
    private const int StdOutputHandle = -11;
    private const int StdErrorHandle = -12;
    private static readonly IntPtr InvalidHandleValue = new IntPtr(-1);

    private IntPtr jobHandle;
    private IntPtr rootProcessHandle;

    public int RootProcessId { get; }
    public long SupervisorStartTimeUtcTicks { get; }
    public bool AssignedBeforeResume { get; }
    public uint LimitFlags { get; }

    private TerminalJobOwner(
        IntPtr jobHandle,
        IntPtr rootProcessHandle,
        int rootProcessId,
        bool assignedBeforeResume,
        uint limitFlags)
    {
        this.jobHandle = jobHandle;
        this.rootProcessHandle = rootProcessHandle;
        RootProcessId = rootProcessId;
        AssignedBeforeResume = assignedBeforeResume;
        LimitFlags = limitFlags;
        SupervisorStartTimeUtcTicks =
            Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks;
    }

    public static TerminalJobOwner Start(
        string fileName,
        string[] arguments,
        string workingDirectory,
        IDictionary<string, string> environmentOverrides)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            throw new PlatformNotSupportedException(
                "Windows Job Object terminal ownership is supported only on Windows");
        if (String.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("Terminal executable is required", nameof(fileName));
        if (String.IsNullOrWhiteSpace(workingDirectory))
            throw new ArgumentException("Terminal working directory is required", nameof(workingDirectory));

        var job = CreateJobObjectW(IntPtr.Zero, null);
        if (job == IntPtr.Zero)
            throw Win32("CreateJobObjectW");

        IntPtr processHandle = IntPtr.Zero;
        IntPtr threadHandle = IntPtr.Zero;
        IntPtr environmentBlock = IntPtr.Zero;
        IntPtr nullHandle = IntPtr.Zero;
        try
        {
            var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            limits.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
            SetJobInformation(job, limits);

            environmentBlock = BuildEnvironmentBlock(environmentOverrides);
            nullHandle = OpenInheritedNull();
            var startup = new STARTUPINFO
            {
                cb = Marshal.SizeOf<STARTUPINFO>(),
                dwFlags = StartfUseStdHandles,
                hStdInput = nullHandle,
                hStdOutput = nullHandle,
                hStdError = nullHandle,
            };
            var commandLine = new StringBuilder(
                String.Join(
                    " ",
                    new[] { fileName }
                        .Concat(arguments ?? Array.Empty<string>())
                        .Select(QuoteWindowsArgument)));
            DisableControlHandleInheritance();

            if (!CreateProcessW(
                    fileName,
                    commandLine,
                    IntPtr.Zero,
                    IntPtr.Zero,
                    true,
                    CreateSuspended | CreateNoWindow | CreateUnicodeEnvironment,
                    environmentBlock,
                    workingDirectory,
                    ref startup,
                    out var processInformation))
                throw Win32("CreateProcessW");

            processHandle = processInformation.hProcess;
            threadHandle = processInformation.hThread;

            if (!AssignProcessToJobObject(job, processHandle))
            {
                var error = Marshal.GetLastWin32Error();
                TerminateProcess(processHandle, 1);
                WaitForSingleObject(processHandle, 5000);
                throw Win32("AssignProcessToJobObject", error);
            }

            if (ResumeThread(threadHandle) == UInt32.MaxValue)
            {
                var error = Marshal.GetLastWin32Error();
                TerminateJobObject(job, 1);
                WaitForSingleObject(processHandle, 5000);
                throw Win32("ResumeThread", error);
            }

            CloseHandle(threadHandle);
            threadHandle = IntPtr.Zero;
            var owner = new TerminalJobOwner(
                job,
                processHandle,
                checked((int)processInformation.dwProcessId),
                true,
                limits.BasicLimitInformation.LimitFlags);
            job = IntPtr.Zero;
            processHandle = IntPtr.Zero;
            return owner;
        }
        finally
        {
            if (threadHandle != IntPtr.Zero) CloseHandle(threadHandle);
            if (processHandle != IntPtr.Zero) CloseHandle(processHandle);
            if (nullHandle != IntPtr.Zero && nullHandle != InvalidHandleValue)
                CloseHandle(nullHandle);
            if (environmentBlock != IntPtr.Zero)
                Marshal.FreeHGlobal(environmentBlock);
            if (job != IntPtr.Zero) CloseHandle(job);
        }
    }

    public bool ContainsProcess(int processId)
    {
        if (processId <= 0 || jobHandle == IntPtr.Zero) return false;
        var process = OpenProcess(
            ProcessQueryLimitedInformation,
            false,
            checked((uint)processId));
        if (process == IntPtr.Zero) return false;
        try
        {
            if (!IsProcessInJob(process, jobHandle, out var contained))
                throw Win32("IsProcessInJob");
            return contained;
        }
        finally
        {
            CloseHandle(process);
        }
    }

    public Task<int> WaitForRootExitAsync()
    {
        var retained = rootProcessHandle;
        return Task.Run(() =>
        {
            WaitForSingleObject(retained, Infinite);
            return GetExitCodeProcess(retained, out var exitCode)
                ? unchecked((int)exitCode)
                : -1;
        });
    }

    public bool TerminateAndWait(int timeoutMilliseconds)
    {
        if (jobHandle == IntPtr.Zero) return true;
        if (ActiveProcessCount() == 0) return true;
        if (!TerminateJobObject(jobHandle, 1))
            throw Win32("TerminateJobObject");

        var stopwatch = Stopwatch.StartNew();
        while (ActiveProcessCount() != 0)
        {
            if (stopwatch.ElapsedMilliseconds >= timeoutMilliseconds)
                return false;
            Thread.Sleep(10);
        }
        return true;
    }

    public uint ActiveProcessCount()
    {
        if (jobHandle == IntPtr.Zero) return 0;
        var size = Marshal.SizeOf<JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!QueryInformationJobObject(
                    jobHandle,
                    JOBOBJECTINFOCLASS.JobObjectBasicAccountingInformation,
                    buffer,
                    checked((uint)size),
                    IntPtr.Zero))
                throw Win32("QueryInformationJobObject");
            return Marshal.PtrToStructure<JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>(
                buffer).ActiveProcesses;
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    public static IDictionary<string, object> Policy()
    {
        DisableControlHandleInheritance();
        var flags = JobObjectLimitKillOnJobClose;
        return new Dictionary<string, object>
        {
            ["createSuspended"] = CreateSuspended,
            ["assignedBeforeResume"] = true,
            ["killOnJobClose"] = (flags & JobObjectLimitKillOnJobClose) != 0,
            ["breakawayAllowed"] = (flags & JobObjectLimitBreakawayOk) != 0,
            ["silentBreakawayAllowed"] =
                (flags & JobObjectLimitSilentBreakawayOk) != 0,
            ["descendantsInheritMembership"] =
                (flags & (JobObjectLimitBreakawayOk | JobObjectLimitSilentBreakawayOk)) == 0,
            ["quoteProbe"] = QuoteWindowsArgument("C:\\path with space\\"),
            ["controlStandardHandlesNonInheritable"] =
                ControlStandardHandles().All(handle => !HandleIsInheritable(handle)),
            ["childInheritedHandleCount"] = 1,
            ["childStandardHandles"] = "NUL",
            ["failedLaunchHandleDelta"] = FailedLaunchHandleDelta(),
        };
    }

    public void Dispose()
    {
        var job = Interlocked.Exchange(ref jobHandle, IntPtr.Zero);
        var process = Interlocked.Exchange(ref rootProcessHandle, IntPtr.Zero);
        if (job != IntPtr.Zero) CloseHandle(job);
        if (process != IntPtr.Zero) CloseHandle(process);
    }

    private static string QuoteWindowsArgument(string value)
    {
        value = value ?? String.Empty;
        if (value.Length > 0 && value.All(ch => !Char.IsWhiteSpace(ch) && ch != '"'))
            return value;

        var quoted = new StringBuilder("\"");
        var backslashes = 0;
        foreach (var ch in value)
        {
            if (ch == '\\')
            {
                backslashes++;
            }
            else if (ch == '"')
            {
                quoted.Append('\\', backslashes * 2 + 1);
                quoted.Append(ch);
                backslashes = 0;
            }
            else
            {
                quoted.Append('\\', backslashes);
                quoted.Append(ch);
                backslashes = 0;
            }
        }
        quoted.Append('\\', backslashes * 2);
        quoted.Append('"');
        return quoted.ToString();
    }

    private static IntPtr BuildEnvironmentBlock(
        IDictionary<string, string> overrides)
    {
        var values = new SortedDictionary<string, string>(
            StringComparer.OrdinalIgnoreCase);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
            values[(string)entry.Key] = Convert.ToString(entry.Value) ?? String.Empty;

        if (overrides != null)
        {
            foreach (var pair in overrides)
            {
                if (String.IsNullOrEmpty(pair.Key) ||
                    pair.Key.IndexOfAny(new[] { '=', '\0' }) >= 0)
                    throw new ArgumentException("Terminal environment key is invalid");
                if ((pair.Value ?? String.Empty).IndexOf('\0') >= 0)
                    throw new ArgumentException("Terminal environment value is invalid");
                values[pair.Key] = pair.Value ?? String.Empty;
            }
        }

        var block = String.Join(
            "\0",
            values.Select(pair => pair.Key + "=" + pair.Value)) + "\0\0";
        return Marshal.StringToHGlobalUni(block);
    }

    private static IntPtr OpenInheritedNull()
    {
        var attributes = new SECURITY_ATTRIBUTES
        {
            nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
            bInheritHandle = true,
        };
        var handle = CreateFileW(
            "NUL",
            GenericRead | GenericWrite,
            FileShareRead | FileShareWrite,
            ref attributes,
            OpenExisting,
            0,
            IntPtr.Zero);
        if (handle == InvalidHandleValue)
            throw Win32("CreateFileW(NUL)");
        return handle;
    }

    private static IntPtr[] ControlStandardHandles() =>
        new[] { StdInputHandle, StdOutputHandle, StdErrorHandle }
            .Select(GetRequiredStandardHandle)
            .ToArray();

    private static IntPtr GetRequiredStandardHandle(int standardHandle)
    {
        var handle = GetStdHandle(standardHandle);
        if (handle == InvalidHandleValue)
            throw Win32("GetStdHandle");
        if (handle == IntPtr.Zero)
            throw new InvalidOperationException(
                "Supervisor control standard handle is unavailable");
        return handle;
    }

    private static void DisableControlHandleInheritance()
    {
        foreach (var handle in ControlStandardHandles())
        {
            if (!SetHandleInformation(handle, HandleFlagInherit, 0))
                throw Win32("SetHandleInformation");
        }
    }

    private static bool HandleIsInheritable(IntPtr handle)
    {
        if (!GetHandleInformation(handle, out var flags))
            throw Win32("GetHandleInformation");
        return (flags & HandleFlagInherit) != 0;
    }

    private static int FailedLaunchHandleDelta()
    {
        ProbeFailedLaunch();
        var before = CurrentProcessHandleCount();
        ProbeFailedLaunch();
        return checked((int)CurrentProcessHandleCount() - (int)before);
    }

    private static void ProbeFailedLaunch()
    {
        try
        {
            Start(
                Path.Combine(
                    Environment.CurrentDirectory,
                    "treemon-missing-" + Guid.NewGuid().ToString("N") + ".exe"),
                Array.Empty<string>(),
                Environment.CurrentDirectory,
                new Dictionary<string, string>());
            throw new InvalidOperationException(
                "Missing launch probe unexpectedly started");
        }
        catch (Win32Exception error) when (
            error.NativeErrorCode == 2 ||
            error.NativeErrorCode == 3)
        {
        }
    }

    private static uint CurrentProcessHandleCount()
    {
        if (!GetProcessHandleCount(GetCurrentProcess(), out var count))
            throw Win32("GetProcessHandleCount");
        return count;
    }

    private static void SetJobInformation(
        IntPtr job,
        JOBOBJECT_EXTENDED_LIMIT_INFORMATION limits)
    {
        var size = Marshal.SizeOf<JOBOBJECT_EXTENDED_LIMIT_INFORMATION>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            Marshal.StructureToPtr(limits, buffer, false);
            if (!SetInformationJobObject(
                    job,
                    JOBOBJECTINFOCLASS.JobObjectExtendedLimitInformation,
                    buffer,
                    checked((uint)size)))
                throw Win32("SetInformationJobObject");
        }
        finally
        {
            Marshal.FreeHGlobal(buffer);
        }
    }

    private static Exception Win32(string operation) =>
        Win32(operation, Marshal.GetLastWin32Error());

    private static Exception Win32(string operation, int error) =>
        new System.ComponentModel.Win32Exception(
            error,
            operation + " failed");

    private enum JOBOBJECTINFOCLASS
    {
        JobObjectBasicAccountingInformation = 1,
        JobObjectExtendedLimitInformation = 9,
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct SECURITY_ATTRIBUTES
    {
        public int nLength;
        public IntPtr lpSecurityDescriptor;
        [MarshalAs(UnmanagedType.Bool)]
        public bool bInheritHandle;
    }

    [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
    private struct STARTUPINFO
    {
        public int cb;
        public string lpReserved;
        public string lpDesktop;
        public string lpTitle;
        public uint dwX;
        public uint dwY;
        public uint dwXSize;
        public uint dwYSize;
        public uint dwXCountChars;
        public uint dwYCountChars;
        public uint dwFillAttribute;
        public uint dwFlags;
        public ushort wShowWindow;
        public ushort cbReserved2;
        public IntPtr lpReserved2;
        public IntPtr hStdInput;
        public IntPtr hStdOutput;
        public IntPtr hStdError;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct PROCESS_INFORMATION
    {
        public IntPtr hProcess;
        public IntPtr hThread;
        public uint dwProcessId;
        public uint dwThreadId;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_LIMIT_INFORMATION
    {
        public long PerProcessUserTimeLimit;
        public long PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize;
        public UIntPtr MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass;
        public uint SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IO_COUNTERS
    {
        public ulong ReadOperationCount;
        public ulong WriteOperationCount;
        public ulong OtherOperationCount;
        public ulong ReadTransferCount;
        public ulong WriteTransferCount;
        public ulong OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
    {
        public JOBOBJECT_BASIC_LIMIT_INFORMATION BasicLimitInformation;
        public IO_COUNTERS IoInfo;
        public UIntPtr ProcessMemoryLimit;
        public UIntPtr JobMemoryLimit;
        public UIntPtr PeakProcessMemoryUsed;
        public UIntPtr PeakJobMemoryUsed;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct JOBOBJECT_BASIC_ACCOUNTING_INFORMATION
    {
        public long TotalUserTime;
        public long TotalKernelTime;
        public long ThisPeriodTotalUserTime;
        public long ThisPeriodTotalKernelTime;
        public uint TotalPageFaultCount;
        public uint TotalProcesses;
        public uint ActiveProcesses;
        public uint TotalTerminatedProcesses;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateJobObjectW(
        IntPtr jobAttributes,
        string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetInformationJobObject(
        IntPtr job,
        JOBOBJECTINFOCLASS informationClass,
        IntPtr information,
        uint informationLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool QueryInformationJobObject(
        IntPtr job,
        JOBOBJECTINFOCLASS informationClass,
        IntPtr information,
        uint informationLength,
        IntPtr returnLength);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool AssignProcessToJobObject(
        IntPtr job,
        IntPtr process);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateJobObject(
        IntPtr job,
        uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool IsProcessInJob(
        IntPtr process,
        IntPtr job,
        [MarshalAs(UnmanagedType.Bool)] out bool result);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CreateProcessW(
        string applicationName,
        StringBuilder commandLine,
        IntPtr processAttributes,
        IntPtr threadAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandles,
        uint creationFlags,
        IntPtr environment,
        string currentDirectory,
        ref STARTUPINFO startupInfo,
        out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint ResumeThread(IntPtr thread);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool TerminateProcess(
        IntPtr process,
        uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint WaitForSingleObject(
        IntPtr handle,
        uint milliseconds);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetExitCodeProcess(
        IntPtr process,
        out uint exitCode);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr OpenProcess(
        uint desiredAccess,
        [MarshalAs(UnmanagedType.Bool)] bool inheritHandle,
        uint processId);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateFileW(
        string fileName,
        uint desiredAccess,
        uint shareMode,
        ref SECURITY_ATTRIBUTES securityAttributes,
        uint creationDisposition,
        uint flagsAndAttributes,
        IntPtr templateFile);

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern IntPtr GetStdHandle(int standardHandle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetHandleInformation(
        IntPtr handle,
        uint mask,
        uint flags);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetHandleInformation(
        IntPtr handle,
        out uint flags);

    [DllImport("kernel32.dll")]
    private static extern IntPtr GetCurrentProcess();

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool GetProcessHandleCount(
        IntPtr process,
        out uint handleCount);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool CloseHandle(IntPtr handle);
}
'@

Add-Type -TypeDefinition $source -Language CSharp

if ($SelfTest) {
    [TerminalJobOwner]::Policy() | ConvertTo-Json -Compress
    exit 0
}

function Write-Protocol([hashtable]$Message) {
    [Console]::Out.WriteLine(($Message | ConvertTo-Json -Compress -Depth 8))
    [Console]::Out.Flush()
}

function Read-Message([string]$Line) {
    if ([string]::IsNullOrWhiteSpace($Line)) {
        throw "Supervisor protocol message was empty"
    }

    $Line | ConvertFrom-Json -Depth 16
}

function Bounded-Error([Exception]$Exception) {
    $message = ($Exception.Message -replace '[\x00-\x1f\x7f]', ' ')
    if ($message.Length -le 240) { $message } else { $message.Substring(0, 240) }
}

$owner = $null
$token = $null
$sessionId = $null
$protocolGeneration = 1
$startRequestId = $null

try {
    $startLine = [Console]::In.ReadLine()
    if ($null -eq $startLine) {
        exit 2
    }

    $start = Read-Message $startLine
    $token = [string]$start.token
    $sessionId = [string]$start.sessionId
    $startRequestId = [string]$start.requestId
    if (
        $start.command -ne "start" -or
        [string]::IsNullOrWhiteSpace($token) -or
        [string]::IsNullOrWhiteSpace($sessionId) -or
        $start.protocolGeneration -ne $protocolGeneration
    ) {
        throw "First supervisor protocol message must be an authenticated start"
    }

    $arguments = @($start.arguments | ForEach-Object { [string]$_ })
    $environment = [Collections.Generic.Dictionary[string,string]]::new(
        [StringComparer]::OrdinalIgnoreCase
    )
    if ($null -ne $start.environment) {
        $start.environment.PSObject.Properties | ForEach-Object {
            $environment[[string]$_.Name] = [string]$_.Value
        }
    }

    $owner = [TerminalJobOwner]::Start(
        [string]$start.fileName,
        $arguments,
        [string]$start.workingDirectory,
        $environment
    )

    Write-Protocol ([ordered]@{
        event = "ready"
        token = $token
        sessionId = $sessionId
        protocolGeneration = $protocolGeneration
        requestId = [string]$start.requestId
        ttydPid = $owner.RootProcessId
        supervisorPid = $PID
        supervisorStartTimeUtcTicks = [string]$owner.SupervisorStartTimeUtcTicks
        assignedBeforeResume = $owner.AssignedBeforeResume
        limitFlags = $owner.LimitFlags
        killOnJobClose = (($owner.LimitFlags -band [TerminalJobOwner]::JobObjectLimitKillOnJobClose) -ne 0)
        breakawayAllowed = (($owner.LimitFlags -band [TerminalJobOwner]::JobObjectLimitBreakawayOk) -ne 0)
        silentBreakawayAllowed = (($owner.LimitFlags -band [TerminalJobOwner]::JobObjectLimitSilentBreakawayOk) -ne 0)
    })

    $readTask = [Console]::In.ReadLineAsync()
    $rootExitTask = $owner.WaitForRootExitAsync()

    while ($true) {
        $completed = [Threading.Tasks.Task]::WhenAny(
            [Threading.Tasks.Task[]]@($readTask, $rootExitTask)
        ).GetAwaiter().GetResult()

        if ([object]::ReferenceEquals($completed, $rootExitTask)) {
            $empty = $owner.TerminateAndWait(10000)
            if ($empty) {
                Write-Protocol ([ordered]@{
                    event = "exited"
                    token = $token
                    sessionId = $sessionId
                    protocolGeneration = $protocolGeneration
                    empty = $true
                    rootExitCode = $rootExitTask.GetAwaiter().GetResult()
                })
                break
            }

            Write-Protocol ([ordered]@{
                event = "boundary-failed"
                token = $token
                sessionId = $sessionId
                protocolGeneration = $protocolGeneration
                error = "Timed out emptying the terminal Job Object after ttyd exited"
            })
            $rootExitTask = [Threading.Tasks.Task]::Delay(-1)
            continue
        }

        $line = $readTask.GetAwaiter().GetResult()
        if ($null -eq $line) {
            $owner.TerminateAndWait(10000) | Out-Null
            break
        }

        $message = Read-Message $line
        $requestId = [string]$message.requestId
        if (
            [string]$message.token -cne $token -or
            [string]$message.sessionId -cne $sessionId -or
            $message.protocolGeneration -ne $protocolGeneration
        ) {
            Write-Protocol ([ordered]@{
                event = "request-failed"
                token = $token
                sessionId = $sessionId
                protocolGeneration = $protocolGeneration
                requestId = $requestId
                error = "Supervisor control authentication failed"
            })
        } elseif ($message.command -eq "contains") {
            $processId = [int]$message.processId
            Write-Protocol ([ordered]@{
                event = "contains"
                token = $token
                sessionId = $sessionId
                protocolGeneration = $protocolGeneration
                requestId = $requestId
                processId = $processId
                member = $owner.ContainsProcess($processId)
            })
        } elseif ($message.command -eq "terminate") {
            $timeoutMilliseconds = [Math]::Max(1, [int]$message.timeoutMilliseconds)
            if ($owner.TerminateAndWait($timeoutMilliseconds)) {
                Write-Protocol ([ordered]@{
                    event = "terminated"
                    token = $token
                    sessionId = $sessionId
                    protocolGeneration = $protocolGeneration
                    requestId = $requestId
                    empty = $true
                })
                break
            }

            Write-Protocol ([ordered]@{
                event = "terminate-failed"
                token = $token
                sessionId = $sessionId
                protocolGeneration = $protocolGeneration
                requestId = $requestId
                error = "Timed out waiting for the terminal Job Object to become empty"
            })
        } else {
            Write-Protocol ([ordered]@{
                event = "request-failed"
                token = $token
                sessionId = $sessionId
                protocolGeneration = $protocolGeneration
                requestId = $requestId
                error = "Unsupported supervisor command"
            })
        }

        $readTask = [Console]::In.ReadLineAsync()
    }
} catch {
    if ($null -ne $token) {
        try {
            Write-Protocol ([ordered]@{
                event = "start-failed"
                token = $token
                sessionId = $sessionId
                protocolGeneration = $protocolGeneration
                requestId = $startRequestId
                error = Bounded-Error $_.Exception
            })
        } catch {
        }
    }
    exit 1
} finally {
    if ($null -ne $owner) {
        $owner.Dispose()
    }
}
