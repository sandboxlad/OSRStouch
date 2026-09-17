using System;
using System.Diagnostics;
using System.Runtime.InteropServices;
using static OsrsTouch.Native;

namespace OsrsTouch {

/// <summary>
/// The notification-area icon. OsrsTouch runs without a console window, so this is how you
/// see that it's alive, pause it, or quit it.
/// </summary>
static unsafe class Tray
{
    public const uint WM_TRAY = 0x8001;          // WM_APP + 1
    public const int CMD_TOGGLE = 101, CMD_LOG = 102, CMD_SETTINGS = 103, CMD_EXIT = 104;

    static IntPtr owner;
    static bool added;

    public static void Add(IntPtr hwnd)
    {
        owner = hwnd;
        var data = Build(NIF_MESSAGE | NIF_ICON | NIF_TIP, "OsrsTouch - touch controls for OSRS");
        added = Shell_NotifyIconW(NIM_ADD, ref data);
    }

    public static void Update(bool paused)
    {
        if (!added) return;
        var data = Build(NIF_TIP, paused ? "OsrsTouch - paused (Ctrl+Alt+T to resume)" : "OsrsTouch - running");
        Shell_NotifyIconW(NIM_MODIFY, ref data);
    }

    public static void Remove()
    {
        if (!added) return;
        var data = Build(0, "");
        Shell_NotifyIconW(NIM_DELETE, ref data);
        added = false;
    }

    static NOTIFYICONDATAW Build(uint flags, string tip)
    {
        var d = new NOTIFYICONDATAW
        {
            cbSize = (uint)sizeof(NOTIFYICONDATAW),
            hWnd = owner,
            uID = 1,
            uFlags = flags,
            uCallbackMessage = WM_TRAY,
            hIcon = LoadIconW(IntPtr.Zero, (IntPtr)32512),   // the standard application icon
        };
        for (int i = 0; i < tip.Length && i < 127; i++) d.szTip[i] = tip[i];
        return d;
    }

    /// <summary>Right-click menu. Returns the command chosen, or 0.</summary>
    public static int ShowMenu(bool paused)
    {
        var menu = CreatePopupMenu();
        if (menu == IntPtr.Zero) return 0;
        AppendMenuW(menu, 0, (UIntPtr)CMD_TOGGLE, paused ? "Resume (Ctrl+Alt+T)" : "Pause (Ctrl+Alt+T)");
        AppendMenuW(menu, MF_SEPARATOR, UIntPtr.Zero, null);
        AppendMenuW(menu, 0, (UIntPtr)CMD_SETTINGS, "Open settings file");
        AppendMenuW(menu, 0, (UIntPtr)CMD_LOG, "Open log");
        AppendMenuW(menu, MF_SEPARATOR, UIntPtr.Zero, null);
        AppendMenuW(menu, 0, (UIntPtr)CMD_EXIT, "Quit OsrsTouch");

        POINT pt;
        GetCursorPos(out pt);
        SetForegroundWindow(owner);                 // so the menu closes when you click away
        int cmd = TrackPopupMenu(menu, TPM_RIGHTBUTTON | TPM_RETURNCMD | TPM_NONOTIFY, pt.X, pt.Y, 0, owner, IntPtr.Zero);
        DestroyMenu(menu);
        return cmd;
    }

    public static void Open(string path)
    {
        try { Process.Start(new ProcessStartInfo(path) { UseShellExecute = true }); }
        catch (Exception e) { Program.Log("couldn't open " + path + ": " + e.Message); }
    }
}
}
