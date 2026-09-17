using System;
using static OsrsTouch.Native;

namespace OsrsTouch {

/// <summary>
/// The yellow tap blip, like the mobile clients: a dot that appears where you tapped,
/// grows and fades out over about half a second.
///
/// It's drawn into a click-through, always-on-top layered window with per-pixel alpha,
/// so it floats over the game without ever taking input or a click.
/// </summary>
unsafe class TapOverlay
{
    IntPtr hwnd, memDc, bmp, oldBmp;
    byte* pixels;
    int size;                       // the window is square, big enough for the largest ring
    bool active;
    ulong startTick;
    int cx, cy;                     // centre of the blip, in screen pixels

    readonly int durationMs, startRadius, endRadius, peakAlpha;
    readonly byte r, g, b;

    public TapOverlay(IntPtr wndProcPtr, int maxDiameter, int durationMs, int peakAlpha, byte red, byte green, byte blue)
    {
        this.durationMs = Math.Max(80, durationMs);
        this.peakAlpha = Math.Max(16, Math.Min(255, peakAlpha));
        r = red; g = green; b = blue;
        size = Math.Max(16, maxDiameter) + 4;
        startRadius = Math.Max(2, size / 8);
        endRadius = size / 2 - 2;

        const string cls = "OsrsTouchBlip";
        fixed (char* pCls = cls)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = wndProcPtr,
                hInstance = GetModuleHandleW(IntPtr.Zero),
                lpszClassName = pCls,
            };
            RegisterClassExW(ref wc);
        }
        hwnd = CreateWindowExW(WS_EX_LAYERED | WS_EX_TRANSPARENT | WS_EX_TOOLWINDOW | WS_EX_TOPMOST | WS_EX_NOACTIVATE,
            cls, "OsrsTouchBlip", WS_POPUP, 0, 0, size, size, IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(IntPtr.Zero), IntPtr.Zero);
        if (hwnd == IntPtr.Zero) { Program.Log("tap blip unavailable (window creation failed)"); return; }

        var screen = GetDC(IntPtr.Zero);
        memDc = CreateCompatibleDC(screen);
        var bmi = new BITMAPINFOHEADER
        {
            biSize = (uint)sizeof(BITMAPINFOHEADER),
            biWidth = size,
            biHeight = -size,       // negative: top-down rows
            biPlanes = 1,
            biBitCount = 32,
            biCompression = BI_RGB,
        };
        byte* bits;
        bmp = CreateDIBSection(memDc, &bmi, DIB_RGB_COLORS, out bits, IntPtr.Zero, 0);
        pixels = bits;
        ReleaseDC(IntPtr.Zero, screen);
        if (bmp == IntPtr.Zero || pixels == null) { Program.Log("tap blip unavailable (bitmap creation failed)"); hwnd = IntPtr.Zero; return; }
        oldBmp = SelectObject(memDc, bmp);
    }

    public bool Ready { get { return hwnd != IntPtr.Zero && pixels != null; } }

    public void Trigger(double x, double y)
    {
        if (!Ready) return;
        cx = (int)Math.Round(x); cy = (int)Math.Round(y);
        startTick = GetTickCount64();
        active = true;
        Draw(0);
        ShowWindow(hwnd, SW_SHOWNOACTIVATE);
    }

    /// <summary>Called from the main timer; advances or finishes the animation.</summary>
    public void Tick()
    {
        if (!active || !Ready) return;
        double t = (GetTickCount64() - startTick) / (double)durationMs;
        if (t >= 1) { active = false; ShowWindow(hwnd, SW_HIDE); return; }
        Draw(t);
    }

    public void Hide()
    {
        active = false;
        if (Ready) ShowWindow(hwnd, SW_HIDE);
    }

    void Draw(double t)
    {
        // grow quickly then ease off, and fade to nothing by the end
        double ease = 1 - (1 - t) * (1 - t);
        double radius = startRadius + (endRadius - startRadius) * ease;
        double fade = 1 - t * t;                     // stays bright early, drops away at the end
        double alphaMax = peakAlpha * fade;

        double mid = size / 2.0;
        for (int py = 0; py < size; py++)
        {
            byte* row = pixels + py * size * 4;
            double dy = py + 0.5 - mid;
            for (int px = 0; px < size; px++)
            {
                double dx = px + 0.5 - mid;
                double d = Math.Sqrt(dx * dx + dy * dy);

                // solid inside, soft 1.5px edge, nothing outside
                double edge = radius - d;
                double cover = edge >= 1.5 ? 1.0 : (edge <= 0 ? 0.0 : edge / 1.5);
                byte a = (byte)(alphaMax * cover);

                // layered windows want premultiplied alpha, in B G R A order
                row[px * 4 + 0] = (byte)(b * a / 255);
                row[px * 4 + 1] = (byte)(g * a / 255);
                row[px * 4 + 2] = (byte)(r * a / 255);
                row[px * 4 + 3] = a;
            }
        }

        var dst = new POINT { X = cx - size / 2, Y = cy - size / 2 };
        var src = new POINT { X = 0, Y = 0 };
        var sz = new SIZE { cx = size, cy = size };
        var blend = new BLENDFUNCTION { BlendOp = (byte)AC_SRC_OVER, BlendFlags = 0, SourceConstantAlpha = 255, AlphaFormat = (byte)AC_SRC_ALPHA };
        UpdateLayeredWindow(hwnd, IntPtr.Zero, &dst, &sz, memDc, &src, 0, &blend, ULW_ALPHA);
    }

    public void Dispose()
    {
        if (!Ready) return;
        ShowWindow(hwnd, SW_HIDE);
        if (oldBmp != IntPtr.Zero) SelectObject(memDc, oldBmp);
        if (bmp != IntPtr.Zero) DeleteObject(bmp);
        if (memDc != IntPtr.Zero) DeleteDC(memDc);
    }
}

/// <summary>
/// Hides the mouse pointer by swapping the system cursors for a blank one while the game
/// is in front, and putting the real ones back the moment it isn't (or we quit).
/// </summary>
static class CursorHider
{
    static bool hidden;
    static bool warned;
    static readonly uint[] Shapes = { OCR_NORMAL, OCR_IBEAM, OCR_CROSS, OCR_HAND };

    public static void Set(bool hide)
    {
        if (hide == hidden) return;
        if (hide) { hidden = Apply(); }
        else
        {
            SystemParametersInfoW(SPI_SETCURSORS, 0, IntPtr.Zero, 0);   // reload the user's real cursors
            hidden = false;
        }
    }

    /// <summary>Re-assert the blank cursor (some apps set their own and undo it).</summary>
    public static void Refresh() { if (hidden) Apply(); }

    static bool Apply()
    {
        // The cursor must match the system cursor size, which is larger than 32x32 on a
        // scaled display - a mismatched bitmap is simply rejected and the pointer stays visible.
        int w = GetSystemMetrics(SM_CXCURSOR), h = GetSystemMetrics(SM_CYCURSOR);
        if (w <= 0) w = 32;
        if (h <= 0) h = 32;
        int stride = ((w + 15) / 16) * 2;          // monochrome rows are WORD aligned
        var and = new byte[stride * h];            // AND all 1s + XOR all 0s = fully transparent
        var xor = new byte[stride * h];
        for (int i = 0; i < and.Length; i++) { and[i] = 0xFF; xor[i] = 0x00; }

        bool any = false;
        foreach (var shape in Shapes)
        {
            // SetSystemCursor takes ownership of the handle, so make a fresh one each time
            var blank = CreateCursor(GetModuleHandleW(IntPtr.Zero), 0, 0, w, h, and, xor);
            if (blank == IntPtr.Zero) continue;
            if (SetSystemCursor(blank, shape)) any = true;
            else DestroyCursor(blank);
        }
        if (!any && !warned)
        {
            warned = true;
            Program.Log("couldn't hide the mouse pointer (Windows refused the blank cursor, size " + w + "x" + h + ")");
        }
        return any;
    }

    public static bool Hidden { get { return hidden; } }
}
}
