module Server.Win32

open System.Runtime.InteropServices
open System.Text

type EnumWindowsProc = delegate of nativeint * nativeint -> bool

[<DllImport("user32.dll", SetLastError = true)>]
extern bool private EnumWindows(EnumWindowsProc lpEnumFunc, nativeint lParam)

[<DllImport("user32.dll", SetLastError = true)>]
extern bool private SetForegroundWindow(nativeint hWnd)

[<DllImport("user32.dll", SetLastError = true)>]
extern uint32 private GetWindowThreadProcessId(nativeint hWnd, uint32& lpdwProcessId)

[<DllImport("user32.dll")>]
extern bool private IsWindowNative(nativeint hWnd)

[<DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Auto, EntryPoint = "GetClassNameW")>]
extern int private GetClassNameNative(nativeint hWnd, StringBuilder lpClassName, int nMaxCount)

[<DllImport("user32.dll")>]
extern bool private IsWindowVisible(nativeint hWnd)

[<DllImport("user32.dll")>]
extern void private keybd_event(byte bVk, byte bScan, uint32 dwFlags, nativeint dwExtraInfo)

let private VK_MENU = 0x12uy
let private KEYEVENTF_EXTENDEDKEY = 0x1u
let private KEYEVENTF_KEYUP = 0x2u

let listTopLevelWindows () =
    let windows = System.Collections.Generic.List<nativeint>()
    let callback = EnumWindowsProc(fun hwnd _ -> windows.Add(hwnd); true)
    EnumWindows(callback, 0n) |> ignore
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
