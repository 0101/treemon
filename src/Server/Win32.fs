module Server.Win32

open System
open System.Runtime.InteropServices
open System.Text

type EnumWindowsProc = delegate of nativeint * nativeint -> bool

[<DllImport("user32.dll", SetLastError = true)>]
extern bool private EnumWindows(EnumWindowsProc lpEnumFunc, nativeint lParam)

[<DllImport("user32.dll", SetLastError = true)>]
extern bool private SetForegroundWindow(nativeint hWnd)

[<DllImport("user32.dll")>]
extern nativeint private GetForegroundWindow()

[<DllImport("user32.dll", SetLastError = true)>]
extern bool private AttachThreadInput(uint32 idAttach, uint32 idAttachTo, bool fAttach)

[<DllImport("user32.dll")>]
extern bool private IsIconic(nativeint hWnd)

[<DllImport("user32.dll")>]
extern bool private ShowWindowAsync(nativeint hWnd, int nCmdShow)

[<DllImport("user32.dll")>]
extern void private SwitchToThisWindow(nativeint hWnd, bool fUnknown)

[<DllImport("user32.dll", SetLastError = true)>]
extern uint32 private GetWindowThreadProcessId(nativeint hWnd, uint32& lpdwProcessId)

[<DllImport("kernel32.dll")>]
extern uint32 private GetCurrentThreadId()

[<DllImport("user32.dll", EntryPoint = "IsWindow")>]
extern bool private IsWindowNative(nativeint hWnd)

[<DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")>]
extern int private GetClassNameNative(nativeint hWnd, StringBuilder lpClassName, int nMaxCount)

[<DllImport("user32.dll")>]
extern bool private IsWindowVisible(nativeint hWnd)

[<DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)>]
extern bool private PostMessageNative(nativeint hWnd, uint32 Msg, nativeint wParam, nativeint lParam)

let private SW_RESTORE = 9
let private WM_CLOSE = 0x0010u

type internal WindowActivationApi =
    { IsWindow: nativeint -> bool
      IsIconic: nativeint -> bool
      RestoreWindow: nativeint -> unit
      GetForegroundWindow: unit -> nativeint
      GetCurrentThreadId: unit -> uint32
      GetWindowThreadId: nativeint -> uint32
      AttachThreadInput: uint32 -> uint32 -> bool -> bool
      SetForegroundWindow: nativeint -> bool
      SwitchToThisWindow: nativeint -> unit }

let listTopLevelWindows () =
    let windows = System.Collections.Generic.List<nativeint>()
    let callback = EnumWindowsProc(fun hwnd _ -> windows.Add(hwnd); true)
    EnumWindows(callback, 0n) |> ignore
    GC.KeepAlive(callback)
    windows |> Seq.toList

let isWindowValid (hwnd: nativeint) =
    IsWindowNative(hwnd)

let getWindowClassName (hwnd: nativeint) =
    let sb = StringBuilder(256)
    let len = GetClassNameNative(hwnd, sb, sb.Capacity)
    if len > 0 then sb.ToString() else ""

let getWindowPid (hwnd: nativeint) =
    let mutable pid = 0u
    GetWindowThreadProcessId(hwnd, &pid) |> ignore
    int pid

let private getWindowThreadId hwnd =
    let mutable pid = 0u
    GetWindowThreadProcessId(hwnd, &pid)

let internal focusWindowWith (api: WindowActivationApi) (hwnd: nativeint) =
    if not (api.IsWindow hwnd) then
        false
    else
        if api.IsIconic hwnd then
            api.RestoreWindow hwnd

        let foreground = api.GetForegroundWindow()

        if foreground = hwnd then
            true
        else
            let currentThread = api.GetCurrentThreadId()

            let foregroundThread =
                if foreground = 0n then
                    0u
                else
                    api.GetWindowThreadId foreground

            let attached =
                foregroundThread <> 0u
                && foregroundThread <> currentThread
                && api.AttachThreadInput foregroundThread currentThread true

            let setForeground =
                try
                    api.SetForegroundWindow hwnd
                finally
                    if attached then
                        api.AttachThreadInput foregroundThread currentThread false |> ignore

            api.SwitchToThisWindow hwnd

            let observedForeground = api.GetForegroundWindow()
            let noForegroundOwner = foreground = 0n && observedForeground = 0n

            api.IsWindow hwnd
            && (setForeground || observedForeground = hwnd || noForegroundOwner)

let private windowActivationApi =
    { IsWindow = IsWindowNative
      IsIconic = IsIconic
      RestoreWindow = fun hwnd -> ShowWindowAsync(hwnd, SW_RESTORE) |> ignore
      GetForegroundWindow = GetForegroundWindow
      GetCurrentThreadId = GetCurrentThreadId
      GetWindowThreadId = getWindowThreadId
      AttachThreadInput = fun source target attach -> AttachThreadInput(source, target, attach)
      SetForegroundWindow = SetForegroundWindow
      SwitchToThisWindow = fun hwnd -> SwitchToThisWindow(hwnd, true) }

let focusWindow hwnd =
    focusWindowWith windowActivationApi hwnd

let listWindowsTerminalWindows () =
    listTopLevelWindows ()
    |> List.filter (fun hwnd ->
        IsWindowVisible(hwnd) && getWindowClassName hwnd = "CASCADIA_HOSTING_WINDOW_CLASS")

let closeWindow (hwnd: nativeint) =
    PostMessageNative(hwnd, WM_CLOSE, 0n, 0n)

// System metrics P/Invoke

[<Struct; StructLayout(LayoutKind.Sequential)>]
type MEMORYSTATUSEX =
    val mutable dwLength: uint32
    val mutable dwMemoryLoad: uint32
    val mutable ullTotalPhys: uint64
    val mutable ullAvailPhys: uint64
    val mutable ullTotalPageFile: uint64
    val mutable ullAvailPageFile: uint64
    val mutable ullTotalVirtual: uint64
    val mutable ullAvailVirtual: uint64
    val mutable ullAvailExtendedVirtual: uint64

[<DllImport("kernel32.dll", SetLastError = true)>]
extern bool private GlobalMemoryStatusEx(MEMORYSTATUSEX& lpBuffer)

[<Struct; StructLayout(LayoutKind.Sequential)>]
type FILETIME =
    val mutable dwLowDateTime: uint32
    val mutable dwHighDateTime: uint32

[<DllImport("kernel32.dll", SetLastError = true)>]
extern bool private GetSystemTimes(FILETIME& lpIdleTime, FILETIME& lpKernelTime, FILETIME& lpUserTime)

let readMemoryStatus () =
    let mutable status = MEMORYSTATUSEX()
    status.dwLength <- uint32 (Marshal.SizeOf<MEMORYSTATUSEX>())
    if GlobalMemoryStatusEx(&status) then Some status else None

let readSystemTimes () =
    let mutable idle = FILETIME()
    let mutable kernel = FILETIME()
    let mutable user = FILETIME()
    if GetSystemTimes(&idle, &kernel, &user) then Some (idle, kernel, user) else None
