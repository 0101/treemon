module Server.Win32

open System
open System.Runtime.InteropServices
open System.Text

type EnumWindowsProc = delegate of nativeint * nativeint -> bool

[<DllImport("user32.dll", SetLastError = true)>]
extern bool private EnumWindows(EnumWindowsProc lpEnumFunc, nativeint lParam)

[<DllImport("user32.dll", SetLastError = true)>]
extern bool private SetForegroundWindow(nativeint hWnd)

[<DllImport("user32.dll", SetLastError = true)>]
extern uint32 private GetWindowThreadProcessId(nativeint hWnd, uint32& lpdwProcessId)

[<DllImport("user32.dll", EntryPoint = "IsWindow")>]
extern bool private IsWindowNative(nativeint hWnd)

[<DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode, EntryPoint = "GetClassNameW")>]
extern int private GetClassNameNative(nativeint hWnd, StringBuilder lpClassName, int nMaxCount)

[<DllImport("user32.dll")>]
extern bool private IsWindowVisible(nativeint hWnd)

[<DllImport("user32.dll", EntryPoint = "PostMessageW", SetLastError = true)>]
extern bool private PostMessageNative(nativeint hWnd, uint32 Msg, nativeint wParam, nativeint lParam)

[<DllImport("user32.dll")>]
extern void private keybd_event(byte bVk, byte bScan, uint32 dwFlags, nativeint dwExtraInfo)

let private VK_MENU = 0x12uy
let private KEYEVENTF_EXTENDEDKEY = 0x1u
let private KEYEVENTF_KEYUP = 0x2u
let private WM_CLOSE = 0x0010u

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

let focusWindow (hwnd: nativeint) =
    if not (IsWindowNative(hwnd)) then
        false
    else
        keybd_event(VK_MENU, 0uy, KEYEVENTF_EXTENDEDKEY, 0n)
        let result = SetForegroundWindow(hwnd)
        keybd_event(VK_MENU, 0uy, KEYEVENTF_EXTENDEDKEY ||| KEYEVENTF_KEYUP, 0n)
        result

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
