using System;
using static OsrsTouch.Native;

namespace OsrsTouch {

/// <summary>
/// Where the touchscreen's own coordinates land on the display, worked out from Windows itself.
///
/// The panel reports in a frame tied to the physical glass and never rotates. Windows applies the
/// display rotation for ordinary apps, but we read the panel directly, so when the screen is turned
/// upside down (or into portrait) our idea of where a finger is comes out mirrored. Windows knows
/// the rotation, so we simply ask it: MonitorFromWindow tells us which display the game is on, and
/// EnumDisplaySettingsEx tells us how that display is rotated. Applying that transform makes touches
/// land correctly the moment the screen is rotated - nothing has to be learned first.
/// </summary>
unsafe static class ScreenMap
{
    // DEVMODEW field offsets we care about (the struct is 220 bytes on Win64).
    const int DM_SIZE_OFFSET = 68, DM_FIELDS_OFFSET = 72, DM_ORIENTATION_OFFSET = 84, DEVMODE_BYTES = 220;
    const uint DM_DISPLAYORIENTATION = 0x00000080;

    static int mx, my, mw = 1, mh = 1;      // the display the game is on, in screen pixels
    static int rot;                          // 0, 90, 180 or 270
    static ulong lastCheck;
    static bool known;

    /// <summary>0/90/180/270 from the ini, or -1 for "ask Windows".</summary>
    public static int Forced = -1;

    public static int Rotation { get { return rot; } }
    public static bool Known { get { return known; } }

    /// <summary>Re-reads the display geometry now and then; returns true if anything changed.</summary>
    public static bool Refresh(IntPtr hwnd)
    {
        ulong now = GetTickCount64();
        if (known && now - lastCheck < 1000) return false;
        lastCheck = now;

        byte* mi = stackalloc byte[128];
        for (int i = 0; i < 128; i++) mi[i] = 0;
        *(int*)mi = 104;                    // cbSize of MONITORINFOEX
        IntPtr mon = hwnd != IntPtr.Zero
            ? MonitorFromWindow(hwnd, MONITOR_DEFAULTTONEAREST)
            : MonitorFromPoint(new POINT { X = 0, Y = 0 }, MONITOR_DEFAULTTOPRIMARY);
        if (mon == IntPtr.Zero || !GetMonitorInfoW(mon, mi)) return false;

        int left = *(int*)(mi + 4), top = *(int*)(mi + 8), right = *(int*)(mi + 12), bottom = *(int*)(mi + 16);
        string device = new string((char*)(mi + 40));
        int i2 = device.IndexOf('\0');
        if (i2 >= 0) device = device.Substring(0, i2);

        int r = 0;
        if (Forced >= 0) r = Forced;
        else if (device.Length > 0)
        {
            byte* dm = stackalloc byte[DEVMODE_BYTES + 32];
            for (int i = 0; i < DEVMODE_BYTES + 32; i++) dm[i] = 0;
            *(ushort*)(dm + DM_SIZE_OFFSET) = (ushort)DEVMODE_BYTES;
            if (EnumDisplaySettingsExW(device, ENUM_CURRENT_SETTINGS, dm, 0))
            {
                uint fields = *(uint*)(dm + DM_FIELDS_OFFSET);
                if ((fields & DM_DISPLAYORIENTATION) != 0)
                {
                    uint o = *(uint*)(dm + DM_ORIENTATION_OFFSET);
                    r = o == 1 ? 90 : o == 2 ? 180 : o == 3 ? 270 : 0;
                }
            }
        }

        bool changed = !known || r != rot || left != mx || top != my || (right - left) != mw || (bottom - top) != mh;
        mx = left; my = top; mw = Math.Max(1, right - left); mh = Math.Max(1, bottom - top);
        rot = r;
        known = true;
        return changed;
    }

    /// <summary>Panel coordinates (0..1 on each axis) to screen pixels, rotation applied.</summary>
    public static void Map(double u, double v, out double x, out double y)
    {
        if (!known)
        {
            x = u * GetSystemMetrics(SM_CXSCREEN);
            y = v * GetSystemMetrics(SM_CYSCREEN);
            return;
        }
        double a, b;
        switch (rot)
        {
            // Windows measures rotation clockwise from the panel's own landscape. At 90 the
            // desktop's top-left sits at the top-right of the glass, its X runs down the glass
            // and its Y runs back along the glass; at 270 both reverse.
            case 90:  a = v;     b = 1 - u; break;
            case 180: a = 1 - u; b = 1 - v; break;
            case 270: a = 1 - v; b = u;     break;
            default:  a = u;     b = v;     break;
        }
        x = mx + a * mw;
        y = my + b * mh;
    }

    public static string Describe()
    {
        if (!known) return "display unknown";
        return "display " + mw + "x" + mh + " at " + mx + "," + my
            + (rot == 0 ? ", upright" : ", rotated " + rot + " degrees")
            + (Forced >= 0 ? " (set by hand)" : "");
    }
}
}
