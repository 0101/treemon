param(
    [switch]$SelfTest,
    [switch]$HandleProbeChild,
    [string]$SentinelHandle,
    [string]$ProbeResultPath
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

public sealed class TerminalLaunchException : Exception
{
    public bool EmptyProven { get; }

    public TerminalLaunchException(Exception launchError, Exception cleanupError, bool emptyProven)
        : base(
            cleanupError == null
                ? launchError.Message
                : launchError.Message + "; startup cleanup failed: " + cleanupError.Message,
            launchError)
    {
        EmptyProven = emptyProven;
    }
}

public sealed class TerminalJobOwner : IDisposable
{
    public const uint JobObjectLimitBreakawayOk = 0x00000800;
    public const uint JobObjectLimitSilentBreakawayOk = 0x00001000;
    public const uint JobObjectLimitKillOnJobClose = 0x00002000;
    public const uint CreateSuspended = 0x00000004;

    private const uint CreateNoWindow = 0x08000000;
    private const uint CreateUnicodeEnvironment = 0x00000400;
    private const uint ExtendedStartupInfoPresent = 0x00080000;
    private const uint StartfUseStdHandles = 0x00000100;
    private const UInt32 ProcThreadAttributeHandleList = 0x00020002;
    private const uint ProcessQueryLimitedInformation = 0x00001000;
    private const uint GenericRead = 0x80000000;
    private const uint GenericWrite = 0x40000000;
    private const uint FileShareRead = 0x00000001;
    private const uint FileShareWrite = 0x00000002;
    private const uint OpenExisting = 3;
    private const uint Infinite = 0xffffffff;
    private const uint WaitObject0 = 0x00000000;
    private const uint WaitTimeout = 0x00000102;
    private const uint StillActive = 259;
    private const uint HandleFlagInherit = 0x00000001;
    private const int ErrorInsufficientBuffer = 122;
    private const int StdInputHandle = -10;
    private const int StdOutputHandle = -11;
    private const int StdErrorHandle = -12;
    private static readonly IntPtr InvalidHandleValue = new IntPtr(-1);

    private IntPtr jobHandle;
    private IntPtr rootProcessHandle;

    public int RootProcessId { get; }
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
    }

    public static TerminalJobOwner Start(
        string fileName,
        string[] arguments,
        string workingDirectory,
        IDictionary<string, string> environmentOverrides,
        string failureStage)
    {
        if (!RuntimeInformation.IsOSPlatform(OSPlatform.Windows))
            throw new PlatformNotSupportedException(
                "Windows Job Object terminal ownership is supported only on Windows");
        if (String.IsNullOrWhiteSpace(fileName))
            throw new ArgumentException("Terminal executable is required", nameof(fileName));
        if (String.IsNullOrWhiteSpace(workingDirectory))
            throw new ArgumentException("Terminal working directory is required", nameof(workingDirectory));

        IntPtr job = IntPtr.Zero;
        IntPtr processHandle = IntPtr.Zero;
        IntPtr threadHandle = IntPtr.Zero;
        IntPtr environmentBlock = IntPtr.Zero;
        IntPtr nullHandle = IntPtr.Zero;
        IntPtr attributeList = IntPtr.Zero;
        IntPtr handleList = IntPtr.Zero;
        bool attributeListInitialized = false;
        bool assignedToJob = false;
        try
        {
            FailAt(failureStage, "before-job");
            job = CreateJobObjectW(IntPtr.Zero, null);
            if (job == IntPtr.Zero)
                throw Win32("CreateJobObjectW");

            var limits = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            limits.BasicLimitInformation.LimitFlags = JobObjectLimitKillOnJobClose;
            SetJobInformation(job, limits);

            environmentBlock = BuildEnvironmentBlock(environmentOverrides);
            nullHandle = OpenInheritedNull();
            var startup = new STARTUPINFOEX
            {
                StartupInfo = new STARTUPINFO
                {
                    cb = Marshal.SizeOf<STARTUPINFOEX>(),
                    dwFlags = StartfUseStdHandles,
                    hStdInput = nullHandle,
                    hStdOutput = nullHandle,
                    hStdError = nullHandle,
                },
            };
            InitializeHandleWhitelist(
                nullHandle,
                ref startup,
                out attributeList,
                out handleList,
                out attributeListInitialized);
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
                    CreateSuspended |
                        CreateNoWindow |
                        CreateUnicodeEnvironment |
                        ExtendedStartupInfoPresent,
                    environmentBlock,
                    workingDirectory,
                    ref startup,
                    out var processInformation))
                throw Win32("CreateProcessW");

            processHandle = processInformation.hProcess;
            threadHandle = processInformation.hThread;
            FailAt(failureStage, "after-process-suspended");

            if (!AssignProcessToJobObject(job, processHandle))
            {
                var error = Marshal.GetLastWin32Error();
                throw Win32("AssignProcessToJobObject", error);
            }
            assignedToJob = true;
            FailAt(failureStage, "after-assignment");

            if (ResumeThread(threadHandle) == UInt32.MaxValue)
            {
                var error = Marshal.GetLastWin32Error();
                throw Win32("ResumeThread", error);
            }
            FailAt(failureStage, "after-resume");

            CloseRequiredHandle(threadHandle);
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
        catch (Exception launchError)
        {
            Exception cleanupError = null;
            bool emptyProven = false;
            try
            {
                emptyProven = ProveFailedLaunchEmpty(
                    job,
                    processHandle,
                    assignedToJob,
                    10000);
            }
            catch (Exception error)
            {
                cleanupError = error;
            }
            throw new TerminalLaunchException(
                launchError,
                cleanupError,
                emptyProven && cleanupError == null);
        }
        finally
        {
            if (attributeListInitialized)
                DeleteProcThreadAttributeList(attributeList);
            if (handleList != IntPtr.Zero)
                Marshal.FreeHGlobal(handleList);
            if (attributeList != IntPtr.Zero)
                Marshal.FreeHGlobal(attributeList);
            if (environmentBlock != IntPtr.Zero)
                Marshal.FreeHGlobal(environmentBlock);
            CloseAllRequiredHandles(
                threadHandle,
                processHandle,
                nullHandle == InvalidHandleValue ? IntPtr.Zero : nullHandle,
                job);
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
            CloseRequiredHandle(process);
        }
    }

    public Task<int> WaitForRootExitAsync()
    {
        var retained = rootProcessHandle;
        return Task.Run(() =>
        {
            var wait = WaitForSingleObject(retained, Infinite);
            if (wait != WaitObject0)
                throw Win32("WaitForSingleObject");
            return GetExitCodeProcess(retained, out var exitCode)
                ? unchecked((int)exitCode)
                : throw Win32("GetExitCodeProcess");
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
        return ActiveProcessCount(jobHandle);
    }

    private static uint ActiveProcessCount(IntPtr job)
    {
        if (job == IntPtr.Zero) return 0;
        var size = Marshal.SizeOf<JOBOBJECT_BASIC_ACCOUNTING_INFORMATION>();
        var buffer = Marshal.AllocHGlobal(size);
        try
        {
            if (!QueryInformationJobObject(
                    job,
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

    public static IDictionary<string, object> Policy(
        string pwshPath,
        string scriptPath,
        string probeResultPath)
    {
        DisableControlHandleInheritance();
        var flags = JobObjectLimitKillOnJobClose;
        var probe = ProbeHandleWhitelist(
            pwshPath,
            scriptPath,
            probeResultPath);
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
            ["childStandardHandles"] = "NUL",
            ["sentinelHandleExcluded"] = probe.Item1,
            ["intendedStandardHandlesUsable"] = probe.Item2,
            ["failedLaunchHandleDelta"] = FailedLaunchHandleDelta(),
        };
    }

    public void Dispose()
    {
        var job = Interlocked.Exchange(ref jobHandle, IntPtr.Zero);
        var process = Interlocked.Exchange(ref rootProcessHandle, IntPtr.Zero);
        CloseAllRequiredHandles(job, process);
    }

    public static void RunHandleProbe(long sentinelHandle, string resultPath)
    {
        var sentinelUsable = SetEvent(new IntPtr(sentinelHandle));
        var standardHandles = ControlStandardHandles();
        var standardHandlesUsable =
            StandardHandleIsUsable(standardHandles[0]) &&
            StandardHandleIsUsable(standardHandles[1]) &&
            StandardHandleIsUsable(standardHandles[2]) &&
            ReadFile(
                standardHandles[0],
                new byte[1],
                1,
                out var bytesRead,
                IntPtr.Zero) &&
            WriteFile(
                standardHandles[1],
                new byte[] { 0x20 },
                1,
                out var outputBytes,
                IntPtr.Zero) &&
            outputBytes == 1 &&
            WriteFile(
                standardHandles[2],
                new byte[] { 0x20 },
                1,
                out var errorBytes,
                IntPtr.Zero) &&
            errorBytes == 1;
        File.WriteAllLines(
            resultPath,
            new[]
            {
                "sentinelUsable=" + sentinelUsable.ToString().ToLowerInvariant(),
                "standardHandlesUsable=" +
                    standardHandlesUsable.ToString().ToLowerInvariant(),
            });
    }

    private static Tuple<bool, bool> ProbeHandleWhitelist(
        string pwshPath,
        string scriptPath,
        string probeResultPath)
    {
        var attributes = new SECURITY_ATTRIBUTES
        {
            nLength = Marshal.SizeOf<SECURITY_ATTRIBUTES>(),
            bInheritHandle = true,
        };
        var sentinel = CreateEventW(ref attributes, true, false, null);
        if (sentinel == IntPtr.Zero)
            throw Win32("CreateEventW");

        try
        {
            if (File.Exists(probeResultPath))
                File.Delete(probeResultPath);
            using (var owner = Start(
                pwshPath,
                new[]
                {
                    "-NoProfile",
                    "-NonInteractive",
                    "-File",
                    scriptPath,
                    "-HandleProbeChild",
                    "-SentinelHandle",
                    sentinel.ToInt64().ToString(),
                    "-ProbeResultPath",
                    probeResultPath,
                },
                Environment.CurrentDirectory,
                new Dictionary<string, string>(),
                null))
            {
                if (!owner.WaitForRootExitAsync().Wait(30000))
                    throw new TimeoutException(
                        "Handle-whitelist probe child did not exit");
                if (!owner.TerminateAndWait(10000))
                    throw new InvalidOperationException(
                        "Handle-whitelist probe Job Object did not become empty");
            }

            var lines = File.ReadAllLines(probeResultPath);
            var values = lines
                .Select(line => line.Split(new[] { '=' }, 2))
                .Where(parts => parts.Length == 2)
                .ToDictionary(parts => parts[0], parts => parts[1]);
            var sentinelWait = WaitForSingleObject(sentinel, 0);
            if (sentinelWait != WaitObject0 && sentinelWait != WaitTimeout)
                throw Win32("WaitForSingleObject");
            var sentinelExcluded =
                values.TryGetValue("sentinelUsable", out var sentinelUsable) &&
                sentinelUsable == "false" &&
                sentinelWait == WaitTimeout;
            var intendedHandlesUsable =
                values.TryGetValue(
                    "standardHandlesUsable",
                    out var standardHandlesUsable) &&
                standardHandlesUsable == "true";
            return Tuple.Create(sentinelExcluded, intendedHandlesUsable);
        }
        finally
        {
            if (File.Exists(probeResultPath))
                File.Delete(probeResultPath);
            CloseRequiredHandle(sentinel);
        }
    }

    private static bool StandardHandleIsUsable(IntPtr handle)
    {
        var type = GetFileType(handle);
        if (type == 0 && Marshal.GetLastWin32Error() != 0)
            return false;
        return true;
    }

    private static void FailAt(string configured, string stage)
    {
        if (String.Equals(configured, stage, StringComparison.Ordinal))
            throw new InvalidOperationException(
                "Injected terminal supervisor failure at " + stage);
    }

    private static void InitializeHandleWhitelist(
        IntPtr inheritedHandle,
        ref STARTUPINFOEX startup,
        out IntPtr attributeList,
        out IntPtr handleList,
        out bool initialized)
    {
        attributeList = IntPtr.Zero;
        handleList = IntPtr.Zero;
        initialized = false;
        UIntPtr size = UIntPtr.Zero;
        if (InitializeProcThreadAttributeList(
                IntPtr.Zero,
                1,
                0,
                ref size))
            throw new InvalidOperationException(
                "InitializeProcThreadAttributeList size probe unexpectedly succeeded");
        if (Marshal.GetLastWin32Error() != ErrorInsufficientBuffer)
            throw Win32("InitializeProcThreadAttributeList(size)");
        var bytes = checked((int)size.ToUInt64());
        if (bytes <= 0)
            throw new InvalidOperationException(
                "Process attribute-list size was invalid");

        attributeList = Marshal.AllocHGlobal(bytes);
        if (!InitializeProcThreadAttributeList(
                attributeList,
                1,
                0,
                ref size))
            throw Win32("InitializeProcThreadAttributeList");
        initialized = true;

        handleList = Marshal.AllocHGlobal(IntPtr.Size);
        Marshal.WriteIntPtr(handleList, inheritedHandle);
        if (!UpdateProcThreadAttribute(
                attributeList,
                0,
                new IntPtr(ProcThreadAttributeHandleList),
                handleList,
                new UIntPtr(checked((uint)IntPtr.Size)),
                IntPtr.Zero,
                IntPtr.Zero))
            throw Win32("UpdateProcThreadAttribute(HANDLE_LIST)");
        startup.lpAttributeList = attributeList;
    }

    private static bool ProveFailedLaunchEmpty(
        IntPtr job,
        IntPtr processHandle,
        bool assignedToJob,
        int timeoutMilliseconds)
    {
        if (processHandle != IntPtr.Zero)
        {
            if (assignedToJob)
            {
                if (ActiveProcessCount(job) != 0 &&
                    !TerminateJobObject(job, 1))
                    throw Win32("TerminateJobObject");
            }
            else
            {
                if (!TerminateProcess(processHandle, 1))
                {
                    if (!GetExitCodeProcess(processHandle, out var exitCode))
                        throw Win32("GetExitCodeProcess");
                    if (exitCode == StillActive)
                        throw Win32("TerminateProcess");
                }
            }

            var processWait = WaitForSingleObject(
                processHandle,
                checked((uint)timeoutMilliseconds));
            if (processWait == WaitTimeout) return false;
            if (processWait != WaitObject0)
                throw Win32("WaitForSingleObject");
        }

        if (job == IntPtr.Zero)
        {
            if (processHandle == IntPtr.Zero) return true;
            var finalWait = WaitForSingleObject(processHandle, 0);
            if (finalWait == WaitObject0) return true;
            if (finalWait == WaitTimeout) return false;
            throw Win32("WaitForSingleObject");
        }
        var stopwatch = Stopwatch.StartNew();
        while (ActiveProcessCount(job) != 0)
        {
            if (stopwatch.ElapsedMilliseconds >= timeoutMilliseconds)
                return false;
            Thread.Sleep(10);
        }
        return true;
    }

    private static void CloseRequiredHandle(IntPtr handle)
    {
        if (handle != IntPtr.Zero && !CloseHandle(handle))
            throw Win32("CloseHandle");
    }

    private static void CloseAllRequiredHandles(params IntPtr[] handles)
    {
        Exception firstFailure = null;
        foreach (var handle in handles)
        {
            try
            {
                CloseRequiredHandle(handle);
            }
            catch (Exception error)
            {
                firstFailure = firstFailure ?? error;
            }
        }
        if (firstFailure != null) throw firstFailure;
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
                new Dictionary<string, string>(),
                null);
            throw new InvalidOperationException(
                "Missing launch probe unexpectedly started");
        }
        catch (TerminalLaunchException error) when (
            error.InnerException is Win32Exception &&
            (((Win32Exception)error.InnerException).NativeErrorCode == 2 ||
             ((Win32Exception)error.InnerException).NativeErrorCode == 3))
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
    private struct STARTUPINFOEX
    {
        public STARTUPINFO StartupInfo;
        public IntPtr lpAttributeList;
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
        ref STARTUPINFOEX startupInfo,
        out PROCESS_INFORMATION processInformation);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool InitializeProcThreadAttributeList(
        IntPtr attributeList,
        int attributeCount,
        int flags,
        ref UIntPtr size);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateProcThreadAttribute(
        IntPtr attributeList,
        uint flags,
        IntPtr attribute,
        IntPtr value,
        UIntPtr size,
        IntPtr previousValue,
        IntPtr returnSize);

    [DllImport("kernel32.dll")]
    private static extern void DeleteProcThreadAttributeList(
        IntPtr attributeList);

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

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr CreateEventW(
        ref SECURITY_ATTRIBUTES eventAttributes,
        [MarshalAs(UnmanagedType.Bool)] bool manualReset,
        [MarshalAs(UnmanagedType.Bool)] bool initialState,
        string name);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool SetEvent(IntPtr eventHandle);

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

    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern uint GetFileType(IntPtr handle);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool ReadFile(
        IntPtr file,
        [Out] byte[] buffer,
        uint bytesToRead,
        out uint bytesRead,
        IntPtr overlapped);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool WriteFile(
        IntPtr file,
        byte[] buffer,
        uint bytesToWrite,
        out uint bytesWritten,
        IntPtr overlapped);

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

if ($HandleProbeChild) {
    if (
        [string]::IsNullOrWhiteSpace($SentinelHandle) -or
        [string]::IsNullOrWhiteSpace($ProbeResultPath)
    ) {
        throw "Handle probe arguments are required"
    }
    [TerminalJobOwner]::RunHandleProbe(
        [Int64]::Parse($SentinelHandle),
        [IO.Path]::GetFullPath($ProbeResultPath)
    )
    exit 0
}

if ($SelfTest) {
    $probeDirectory = [IO.Path]::Combine(
        (Get-Location).Path,
        ".agents",
        "terminal-supervisor-self-test"
    )
    [IO.Directory]::CreateDirectory($probeDirectory) | Out-Null
    $probePath = [IO.Path]::Combine(
        $probeDirectory,
        "handle-probe-$PID-$([Guid]::NewGuid().ToString('N')).txt"
    )
    try {
        [TerminalJobOwner]::Policy(
            [Environment]::ProcessPath,
            $PSCommandPath,
            $probePath
        ) | ConvertTo-Json -Compress
        exit 0
    } finally {
        [IO.File]::Delete($probePath)
        if (
            [IO.Directory]::Exists($probeDirectory) -and
            [IO.Directory]::GetFileSystemEntries($probeDirectory).Length -eq 0
        ) {
            [IO.Directory]::Delete($probeDirectory)
        }
    }
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

function Get-LaunchEmptyProof([Exception]$Exception) {
    $candidate = $Exception
    while ($null -ne $candidate) {
        if ($candidate -is [TerminalLaunchException]) {
            return $candidate.EmptyProven
        }
        $candidate = $candidate.InnerException
    }
    $false
}

function Write-EmptyWitness(
    [string]$Path,
    [string]$Generation,
    [string]$WorktreePath,
    [string]$SessionId,
    [string]$Nonce,
    [string]$SupervisorStartTimeUtcTicks
) {
    if (
        [string]::IsNullOrWhiteSpace($Path) -or
        -not [IO.Path]::IsPathFullyQualified($Path) -or
        [string]::IsNullOrWhiteSpace($Generation) -or
        [string]::IsNullOrWhiteSpace($WorktreePath) -or
        [string]::IsNullOrWhiteSpace($SessionId) -or
        [string]::IsNullOrWhiteSpace($Nonce) -or
        $SupervisorStartTimeUtcTicks -notmatch '^\d+$'
    ) {
        throw "Supervisor empty-witness metadata is invalid"
    }

    $payload = [ordered]@{
        version = 1
        generation = $Generation
        worktreePath = $WorktreePath
        sessionId = $SessionId
        supervisorPid = $PID
        supervisorStartTimeUtcTicks = $SupervisorStartTimeUtcTicks
        nonce = $Nonce
        observedAt = [DateTimeOffset]::UtcNow.ToString("O")
    }
    $directory = [IO.Path]::GetDirectoryName($Path)
    [IO.Directory]::CreateDirectory($directory) | Out-Null
    $temporaryPath = "$Path.$PID.$([Guid]::NewGuid().ToString('N')).tmp"
    $bytes = [Text.UTF8Encoding]::new($false).GetBytes(
        (($payload | ConvertTo-Json -Compress) + [Environment]::NewLine)
    )

    try {
        $stream = [IO.FileStream]::new(
            $temporaryPath,
            [IO.FileMode]::CreateNew,
            [IO.FileAccess]::Write,
            [IO.FileShare]::None,
            4096,
            [IO.FileOptions]::WriteThrough
        )
        try {
            $stream.Write($bytes, 0, $bytes.Length)
            $stream.Flush($true)
        } finally {
            $stream.Dispose()
        }
        [IO.File]::Move($temporaryPath, $Path, $true)
    } finally {
        [IO.File]::Delete($temporaryPath)
    }
}

$owner = $null
$token = $null
$sessionId = $null
$protocolGeneration = 1
$startRequestId = $null
$generation = $null
$worktreePath = $null
$witnessPath = $null
$witnessNonce = $null
$supervisorStartTimeUtcTicks = [string](
    [Diagnostics.Process]::GetCurrentProcess().StartTime.ToUniversalTime().Ticks
)
$emptyWitnessWritten = $false
$startInvoked = $false

function Publish-EmptyWitness {
    if ($emptyWitnessWritten) {
        return
    }
    Write-EmptyWitness `
        $witnessPath `
        $generation `
        $worktreePath `
        $sessionId `
        $witnessNonce `
        $supervisorStartTimeUtcTicks
    $script:emptyWitnessWritten = $true
}

try {
    $startLine = [Console]::In.ReadLine()
    if ($null -eq $startLine) {
        exit 2
    }

    $start = Read-Message $startLine
    $token = [string]$start.token
    $sessionId = [string]$start.sessionId
    $startRequestId = [string]$start.requestId
    $generation = [string]$start.generation
    $worktreePath = [string]$start.worktreePath
    $witnessPath = [string]$start.witness.path
    $witnessNonce = [string]$start.witness.nonce
    if (
        $start.command -ne "start" -or
        [string]::IsNullOrWhiteSpace($token) -or
        [string]::IsNullOrWhiteSpace($sessionId) -or
        $start.protocolGeneration -ne $protocolGeneration -or
        $generation -notmatch '^[A-Za-z0-9_-]{1,128}$' -or
        [string]::IsNullOrWhiteSpace($worktreePath) -or
        [string]::IsNullOrWhiteSpace($witnessPath) -or
        -not [IO.Path]::IsPathFullyQualified($witnessPath) -or
        $witnessNonce -notmatch '^[A-Za-z0-9_-]{24,128}$'
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

    $failureStage =
        if ($env:TREEMON_TERMINAL_SUPERVISOR_TEST_MODE -eq "1") {
            [string]$start.testFailureStage
        } else {
            $null
        }
    $startInvoked = $true
    $owner = [TerminalJobOwner]::Start(
        [string]$start.fileName,
        $arguments,
        [string]$start.workingDirectory,
        $environment,
        $failureStage
    )

    Write-Protocol ([ordered]@{
        event = "ready"
        token = $token
        sessionId = $sessionId
        protocolGeneration = $protocolGeneration
        requestId = [string]$start.requestId
        ttydPid = $owner.RootProcessId
        supervisorPid = $PID
        supervisorStartTimeUtcTicks = $supervisorStartTimeUtcTicks
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
                Publish-EmptyWitness
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
            if ($owner.TerminateAndWait(10000)) {
                Publish-EmptyWitness
            }
            break
        }

        $message = Read-Message $line
        $requestId = [string]$message.requestId
        if (
            [string]$message.token -cne $token -or
            [string]$message.sessionId -cne $sessionId -or
            $message.protocolGeneration -ne $protocolGeneration
        ) {
            throw "Supervisor control authentication failed"
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
        } elseif (
            $message.command -eq "terminate" -or
            $message.command -eq "startup-failed"
        ) {
            $timeoutMilliseconds = [Math]::Max(1, [int]$message.timeoutMilliseconds)
            if ($owner.TerminateAndWait($timeoutMilliseconds)) {
                Publish-EmptyWitness
                Write-Protocol ([ordered]@{
                    event =
                        if ($message.command -eq "startup-failed") {
                            "startup-failure-empty"
                        } else {
                            "terminated"
                        }
                    token = $token
                    sessionId = $sessionId
                    protocolGeneration = $protocolGeneration
                    requestId = $requestId
                    empty = $true
                    supervisorPid = $PID
                    supervisorStartTimeUtcTicks = $supervisorStartTimeUtcTicks
                    error =
                        if ($message.command -eq "startup-failed") {
                            Bounded-Error ([Exception]::new(
                                [string]$message.error
                            ))
                        } else {
                            $null
                        }
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
    $failure = $_.Exception
    $emptyProven =
        if ($null -ne $owner) {
            try {
                $owner.TerminateAndWait(10000)
            } catch {
                $false
            }
        } elseif (-not $startInvoked) {
            $true
        } else {
            Get-LaunchEmptyProof $failure
        }

    if ($emptyProven) {
        try {
            Publish-EmptyWitness
        } catch {
            $emptyProven = $false
            $failure = [Exception]::new(
                "$($failure.Message); empty witness persistence failed: $($_.Exception.Message)",
                $failure
            )
        }
    }

    if ($null -ne $token) {
        try {
            Write-Protocol ([ordered]@{
                event =
                    if ($emptyProven) {
                        "startup-failure-empty"
                    } else {
                        "start-failed"
                    }
                token = $token
                sessionId = $sessionId
                protocolGeneration = $protocolGeneration
                requestId = $startRequestId
                empty = if ($emptyProven) { $true } else { $null }
                supervisorPid = if ($emptyProven) { $PID } else { $null }
                supervisorStartTimeUtcTicks =
                    if ($emptyProven) {
                        $supervisorStartTimeUtcTicks
                    } else {
                        $null
                    }
                error = Bounded-Error $failure
            })
        } catch {
        }
    }
    exit 1
} finally {
    if ($null -ne $owner) {
        if (-not $emptyWitnessWritten) {
            try {
                if ($owner.TerminateAndWait(10000)) {
                    Publish-EmptyWitness
                }
            } catch {
            }
        }
        $owner.Dispose()
    }
}
