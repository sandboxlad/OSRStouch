using System;
using System.Runtime.InteropServices;

namespace OsrsTouch {

[StructLayout(LayoutKind.Sequential)]
struct POINT { public int X, Y; }

[StructLayout(LayoutKind.Sequential)]
struct RECT { public int Left, Top, Right, Bottom; }

[StructLayout(LayoutKind.Sequential)]
struct MSG { public IntPtr hwnd; public uint message; public IntPtr wParam; public IntPtr lParam; public uint time; public POINT pt; }

[StructLayout(LayoutKind.Sequential)]
struct MSLLHOOKSTRUCT { public POINT pt; public uint mouseData; public uint flags; public uint time; public UIntPtr dwExtraInfo; }

[StructLayout(LayoutKind.Sequential)]
struct MOUSEINPUT { public int dx, dy; public uint mouseData, dwFlags, time; public UIntPtr dwExtraInfo; }

[StructLayout(LayoutKind.Sequential)]
struct KEYBDINPUT { public ushort wVk, wScan; public uint dwFlags, time; public UIntPtr dwExtraInfo; }

[StructLayout(LayoutKind.Explicit, Size = 40)]
struct INPUT
{
    [FieldOffset(0)] public uint type;
    [FieldOffset(8)] public MOUSEINPUT mi;
    [FieldOffset(8)] public KEYBDINPUT ki;
}

[StructLayout(LayoutKind.Sequential)]
struct RAWINPUTDEVICE { public ushort usUsagePage, usUsage; public uint dwFlags; public IntPtr hwndTarget; }

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
unsafe struct WNDCLASSEXW
{
    public uint cbSize, style;
    public IntPtr lpfnWndProc;
    public int cbClsExtra, cbWndExtra;
    public IntPtr hInstance, hIcon, hCursor, hbrBackground;
    public char* lpszMenuName;
    public char* lpszClassName;
    public IntPtr hIconSm;
}

[StructLayout(LayoutKind.Sequential)]
struct SIZE { public int cx, cy; }

[StructLayout(LayoutKind.Sequential, Pack = 1)]
struct BLENDFUNCTION { public byte BlendOp, BlendFlags, SourceConstantAlpha, AlphaFormat; }

[StructLayout(LayoutKind.Sequential)]
struct BITMAPINFOHEADER
{
    public uint biSize;
    public int biWidth, biHeight;
    public ushort biPlanes, biBitCount;
    public uint biCompression, biSizeImage;
    public int biXPelsPerMeter, biYPelsPerMeter;
    public uint biClrUsed, biClrImportant;
}

[StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
unsafe struct NOTIFYICONDATAW
{
    public uint cbSize;
    public IntPtr hWnd;
    public uint uID, uFlags, uCallbackMessage;
    public IntPtr hIcon;
    public fixed char szTip[128];
    public uint dwState, dwStateMask;
    public fixed char szInfo[256];
    public uint uVersion;
    public fixed char szInfoTitle[64];
    public uint dwInfoFlags;
    public Guid guidItem;
    public IntPtr hBalloonIcon;
}

[UnmanagedFunctionPointer(CallingConvention.Winapi)]
delegate IntPtr WndProcFn(IntPtr h, uint msg, IntPtr w, IntPtr l);
[UnmanagedFunctionPointer(CallingConvention.Winapi)]
delegate IntPtr HookFn(int code, IntPtr w, IntPtr l);
[UnmanagedFunctionPointer(CallingConvention.Winapi)]
delegate bool ConsoleCtrlFn(uint ctrlType);

static unsafe class Native
{
    public const int WH_MOUSE_LL = 14;
    public const uint WM_MOUSEMOVE = 0x200, WM_LBUTTONDOWN = 0x201, WM_LBUTTONUP = 0x202,
        WM_RBUTTONDOWN = 0x204, WM_RBUTTONUP = 0x205, WM_INPUT = 0x00FF, WM_TIMER = 0x0113,
        WM_HOTKEY = 0x0312, WM_DESTROY = 0x0002, WM_CLOSE = 0x0010, WM_MBUTTONDOWN = 0x207, WM_MBUTTONUP = 0x208, WM_MOUSELEAVE = 0x02A3;

    public const uint INPUT_MOUSE = 0, INPUT_KEYBOARD = 1;
    public const uint KEYEVENTF_KEYUP = 0x2;
    public const ushort VK_LEFT = 0x25, VK_UP = 0x26, VK_RIGHT = 0x27, VK_DOWN = 0x28;
    public const uint MOUSEEVENTF_MOVE = 0x1, MOUSEEVENTF_LEFTDOWN = 0x2, MOUSEEVENTF_LEFTUP = 0x4,
        MOUSEEVENTF_RIGHTDOWN = 0x8, MOUSEEVENTF_RIGHTUP = 0x10, MOUSEEVENTF_MIDDLEDOWN = 0x20, MOUSEEVENTF_MIDDLEUP = 0x40, MOUSEEVENTF_WHEEL = 0x800,
        MOUSEEVENTF_VIRTUALDESK = 0x4000, MOUSEEVENTF_ABSOLUTE = 0x8000;

    public const uint WS_POPUP = 0x80000000, WS_EX_LAYERED = 0x80000, WS_EX_TRANSPARENT = 0x20,
        WS_EX_TOOLWINDOW = 0x80, WS_EX_TOPMOST = 0x8, WS_EX_NOACTIVATE = 0x8000000;
    public const int SW_HIDE = 0, SW_SHOWNOACTIVATE = 4, SW_MINIMIZE = 6;
    public const uint NIM_ADD = 0, NIM_MODIFY = 1, NIM_DELETE = 2;
    public const uint NIF_MESSAGE = 1, NIF_ICON = 2, NIF_TIP = 4;
    public const uint MF_SEPARATOR = 0x800;
    public const uint TPM_RIGHTBUTTON = 2, TPM_RETURNCMD = 0x100, TPM_NONOTIFY = 0x80;
    public const uint WM_COMMAND = 0x111, WM_RBUTTONUP_TRAY = 0x205, WM_LBUTTONDBLCLK = 0x203;
    public const uint ULW_ALPHA = 2, AC_SRC_OVER = 0, AC_SRC_ALPHA = 1, DIB_RGB_COLORS = 0, BI_RGB = 0;
    public const uint SWP_NOSIZE = 0x1, SWP_NOMOVE = 0x2, SWP_NOACTIVATE = 0x10, SWP_SHOWWINDOW = 0x40;
    public const uint SPI_SETCURSORS = 0x0057;
    public const uint OCR_NORMAL = 32512, OCR_IBEAM = 32513, OCR_CROSS = 32515, OCR_HAND = 32649;

    public const uint RIDEV_INPUTSINK = 0x100, RIDEV_DEVNOTIFY = 0x2000;
    public const uint RID_INPUT = 0x10000003, RIDI_PREPARSEDDATA = 0x20000005;
    public const int HIDP_STATUS_SUCCESS = 0x00110000;
    public const int SM_XVIRTUALSCREEN = 76, SM_YVIRTUALSCREEN = 77, SM_CXVIRTUALSCREEN = 78, SM_CYVIRTUALSCREEN = 79,
        SM_CXSCREEN = 0, SM_CYSCREEN = 1, SM_CXCURSOR = 13, SM_CYCURSOR = 14;

    [DllImport("user32.dll", SetLastError = true)] public static extern IntPtr SetWindowsHookExW(int idHook, IntPtr fn, IntPtr hMod, uint threadId);
    [DllImport("user32.dll")] public static extern IntPtr CallNextHookEx(IntPtr hhk, int nCode, IntPtr wParam, IntPtr lParam);
    [DllImport("user32.dll")] public static extern bool UnhookWindowsHookEx(IntPtr hhk);
    [DllImport("user32.dll")] public static extern int GetMessageW(out MSG msg, IntPtr hwnd, uint min, uint max);
    [DllImport("user32.dll")] public static extern bool TranslateMessage(ref MSG msg);
    [DllImport("user32.dll")] public static extern IntPtr DispatchMessageW(ref MSG msg);
    [DllImport("user32.dll")] public static extern void PostQuitMessage(int code);
    [DllImport("user32.dll")] public static extern bool PostMessageW(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll")] public static extern IntPtr DefWindowProcW(IntPtr hwnd, uint msg, IntPtr w, IntPtr l);
    [DllImport("user32.dll", SetLastError = true)] public static extern ushort RegisterClassExW(ref WNDCLASSEXW wc);
    [DllImport("user32.dll", SetLastError = true, CharSet = CharSet.Unicode)]
    public static extern IntPtr CreateWindowExW(uint exStyle, string cls, string name, uint style, int x, int y, int w, int h, IntPtr parent, IntPtr menu, IntPtr inst, IntPtr param);
    [DllImport("kernel32.dll")] public static extern IntPtr GetModuleHandleW(IntPtr name);
    [DllImport("user32.dll")] public static extern UIntPtr SetTimer(IntPtr hwnd, UIntPtr id, uint ms, IntPtr fn);
    [DllImport("user32.dll")] public static extern bool KillTimer(IntPtr hwnd, UIntPtr id);
    [DllImport("user32.dll")] public static extern bool RegisterHotKey(IntPtr hwnd, int id, uint mods, uint vk);
    [DllImport("user32.dll", SetLastError = true)] public static extern uint SendInput(uint n, INPUT* inputs, int size);
    [DllImport("user32.dll")] public static extern int GetSystemMetrics(int i);
    [DllImport("user32.dll")] public static extern IntPtr GetForegroundWindow();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr FindWindowW(string cls, string title);
    [DllImport("user32.dll")] public static extern uint GetWindowThreadProcessId(IntPtr hwnd, out uint pid);
    [DllImport("user32.dll")] public static extern bool GetClientRect(IntPtr hwnd, out RECT r);
    [DllImport("user32.dll")] public static extern bool ClientToScreen(IntPtr hwnd, ref POINT p);
    [DllImport("user32.dll")] public static extern bool GetCursorPos(out POINT p);
    [DllImport("user32.dll")] public static extern IntPtr WindowFromPoint(POINT p);
    [DllImport("user32.dll")] public static extern bool ScreenToClient(IntPtr hwnd, ref POINT p);
    [DllImport("user32.dll")] public static extern bool SetProcessDpiAwarenessContext(IntPtr ctx);
    [DllImport("kernel32.dll")] public static extern ulong GetTickCount64();
    [DllImport("kernel32.dll")] public static extern bool SetConsoleCtrlHandler(ConsoleCtrlFn handler, bool add);
    [DllImport("kernel32.dll")] public static extern IntPtr GetConsoleWindow();
    [DllImport("kernel32.dll")] public static extern bool AllocConsole();
    [DllImport("shell32.dll", CharSet = CharSet.Unicode)] public static extern bool Shell_NotifyIconW(uint msg, ref NOTIFYICONDATAW data);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern IntPtr LoadIconW(IntPtr inst, IntPtr name);
    [DllImport("user32.dll")] public static extern IntPtr CreatePopupMenu();
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool AppendMenuW(IntPtr menu, uint flags, UIntPtr id, string text);
    [DllImport("user32.dll")] public static extern int TrackPopupMenu(IntPtr menu, uint flags, int x, int y, int reserved, IntPtr hwnd, IntPtr rect);
    [DllImport("user32.dll")] public static extern bool DestroyMenu(IntPtr menu);
    [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern bool ShowWindow(IntPtr hwnd, int cmd);
    [DllImport("user32.dll")] public static extern bool SetWindowPos(IntPtr hwnd, IntPtr after, int x, int y, int w, int h, uint flags);
    [DllImport("user32.dll")] public static extern IntPtr GetDC(IntPtr hwnd);
    [DllImport("user32.dll")] public static extern int ReleaseDC(IntPtr hwnd, IntPtr hdc);
    [DllImport("user32.dll")] public static extern bool UpdateLayeredWindow(IntPtr hwnd, IntPtr hdcDst, POINT* pptDst, SIZE* psize, IntPtr hdcSrc, POINT* pptSrc, uint crKey, BLENDFUNCTION* blend, uint flags);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateCompatibleDC(IntPtr hdc);
    [DllImport("gdi32.dll")] public static extern IntPtr CreateDIBSection(IntPtr hdc, BITMAPINFOHEADER* bmi, uint usage, out byte* bits, IntPtr section, uint offset);
    [DllImport("gdi32.dll")] public static extern IntPtr SelectObject(IntPtr hdc, IntPtr obj);
    [DllImport("gdi32.dll")] public static extern bool DeleteObject(IntPtr obj);
    [DllImport("gdi32.dll")] public static extern bool DeleteDC(IntPtr hdc);

    // hiding the pointer: swap the system cursors for a blank one, then ask Windows to reload them
    [DllImport("user32.dll")] public static extern IntPtr CreateCursor(IntPtr inst, int xHotspot, int yHotspot, int w, int h, byte[] andPlane, byte[] xorPlane);
    [DllImport("user32.dll")] public static extern bool SetSystemCursor(IntPtr hcur, uint id);
    [DllImport("user32.dll")] public static extern bool DestroyCursor(IntPtr hcur);
    [DllImport("user32.dll", CharSet = CharSet.Unicode)] public static extern bool SystemParametersInfoW(uint action, uint param, IntPtr ptr, uint winIni);

    [DllImport("user32.dll", SetLastError = true)] public static extern bool RegisterRawInputDevices(RAWINPUTDEVICE* devs, uint n, uint size);
    [DllImport("user32.dll")] public static extern uint GetRawInputData(IntPtr hRaw, uint cmd, byte* data, ref uint size, uint headerSize);
    [DllImport("user32.dll")] public static extern uint GetRawInputDeviceInfoW(IntPtr hDev, uint cmd, byte* data, ref uint size);

    [DllImport("hid.dll")] public static extern int HidP_GetCaps(byte* preparsed, byte* caps);
    [DllImport("hid.dll")] public static extern int HidP_GetValueCaps(int reportType, byte* caps, ref ushort len, byte* preparsed);
    [DllImport("hid.dll")] public static extern int HidP_GetUsageValue(int reportType, ushort page, ushort link, ushort usage, out uint value, byte* preparsed, byte* report, uint reportLen);
    [DllImport("hid.dll")] public static extern int HidP_GetUsages(int reportType, ushort page, ushort link, ushort* list, ref uint len, byte* preparsed, byte* report, uint reportLen);
}
}
