using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using System.Threading;
using static OsrsTouch.Native;

namespace OsrsTouch {

/// <summary>
/// OsrsTouch: touch gestures for OSRS on Windows.
///
///   single tap          -> left click
///   press and hold      -> right click (the game menu)
///   single-finger swipe -> turn/tilt the camera
///   two-finger pinch    -> zoom
///
/// Every gesture is recognised from the raw touchscreen reports, and Windows' own
/// "turn a touch into a mouse click" conversion is blocked while we handle a touch,
/// so a swipe or a pinch can't also click or hover something in the game.
/// It only sends mouse and key input you could produce by hand.
/// </summary>
static unsafe class Program
{
    const ulong MARKER = 0x4F53525455; // tags our own injected input so the hook ignores it
    const int TIMER_TICK = 2, HOTKEY_TOGGLE = 1;

    enum Mode { None, Rotate, RightDone, Pinch }

    static Config cfg;
    static TouchReader reader;
    static IntPtr hwnd, hook;
    static bool enabled = true;

    // current touch sequence
    static bool touchActive;   // a finger is down
    static bool handling;      // ...and it started somewhere we act on
    static Mode mode;
    static double sx, sy;      // where the finger landed
    static ulong startTick;
    static ulong graceUntil;   // keep blocking Windows' late touch-clicks until this tick
    static int touchCount;
    static ulong lastRawTick;
    static bool liftPending;      // saw zero fingers; wait a moment in case it was a reporting gap
    static ulong liftTick;
    static RECT gameRect;

    // camera drag
    static bool middleDown;
    static double vx, vy, anchorX, anchorY, lastX, lastY;
    static POINT savedCursor;
    static bool savedCursorOk;
    static ushort heldKeyX, heldKeyY;

    // pinch
    static double zoomRef, pinchX, pinchY;
    static bool menuMaybeOpen;      // a right-click menu is probably on screen

    // look and feel
    static TapOverlay blip;
    static bool gameWasInFront;
    static ulong lastGameFrontTick;
    static ulong lastCursorRefresh;
    static ulong parkAt;

    // The hook runs on its own thread and may only read these - no API calls, no logging,
    // no allocation. Windows ignores a low-level hook that takes too long, and a hook that
    // gets ignored stops blocking anything.
    static volatile int winTouchX, winTouchY;    // where Windows says the finger is (already rotated)
    static long winTouchTick;
    static readonly Calibrator calib = new Calibrator();
    static int tapDiagLeft = 20;        // log the first few taps' positions even without Verbose
    static bool startWinValid; static double startWinX, startWinY;
    static double lastContactU, lastContactV; static ulong lastContactTick; static bool haveContact;
    static long pairedStamp;
    static int winTouchSeen;

    static volatile bool blockTouch;
    static volatile int bLeft, bTop, bRight, bBottom;
    static int blockedCount;

    static readonly Dictionary<uint, string> procNames = new Dictionary<uint, string>();
    static string lastFgName = "?";
    static uint ourPid;
    static WndProcFn wndProcKeep; static HookFn hookKeep; static ConsoleCtrlFn ctrlKeep; // keep delegates alive

    static void Main(string[] args)
    {
        try { Run(args); }
        catch (Exception e)
        {
            // With no console there is nothing to see when we fall over, so write it down.
            Log("FATAL: " + e.GetType().Name + ": " + e.Message);
            Log(e.StackTrace ?? "");
            try { Cleanup(); Tray.Remove(); } catch { }
        }
    }

    static void Run(string[] args)
    {
        SetProcessDpiAwarenessContext((IntPtr)(-4)); // per-monitor v2: all coords in real pixels

        // OsrsTouch normally runs with no window at all - just the tray icon - so it doesn't
        // sit in the taskbar. The diagnostic modes ask for a console to print into.
        foreach (var a0 in args)
        {
            if (a0.StartsWith("--diagnose", StringComparison.OrdinalIgnoreCase) || a0.StartsWith("--verbose", StringComparison.OrdinalIgnoreCase)
             || a0.StartsWith("--rawlog", StringComparison.OrdinalIgnoreCase) || a0.StartsWith("--blocktest", StringComparison.OrdinalIgnoreCase)
             || a0.StartsWith("--console", StringComparison.OrdinalIgnoreCase))
            {
                if (AllocConsole())
                {
                    var stdout = new System.IO.StreamWriter(Console.OpenStandardOutput());
                    stdout.AutoFlush = true;
                    Console.SetOut(stdout);
                    haveConsole = true;
                    Console.Title = "OsrsTouch";
                }
                break;
            }
        }
        // --quit asks a running copy to shut down properly, so it restores the mouse pointer
        // and clears its tray icon on the way out. Never kill it with taskkill: that skips all
        // of that and can leave the pointer hidden.
        foreach (var a1 in args)
        {
            if (a1.Equals("--quit", StringComparison.OrdinalIgnoreCase))
            {
                var other = FindWindowW("OsrsTouchWnd", null);
                if (other != IntPtr.Zero) PostMessageW(other, WM_CLOSE, IntPtr.Zero, IntPtr.Zero);
                return;
            }
        }

        bool first;
        var mutex = new Mutex(true, "OsrsTouch.SingleInstance", out first);
        if (!first) { Log("OsrsTouch is already running."); Thread.Sleep(2500); return; }

        ourPid = (uint)Process.GetCurrentProcess().Id;
        cfg = Config.Load();
        foreach (var a in args)
        {
            if (a.Equals("--diagnose", StringComparison.OrdinalIgnoreCase)) cfg.DiagnoseOnly = true;
            if (a.Equals("--verbose", StringComparison.OrdinalIgnoreCase)) cfg.Verbose = true;
            if (a.Equals("--rawlog", StringComparison.OrdinalIgnoreCase)) { cfg.LogRaw = true; cfg.Verbose = true; }
            if (a.Equals("--blocktest", StringComparison.OrdinalIgnoreCase)) { cfg.BlockTest = true; cfg.Verbose = true; }
            if (a.Equals("--minimized", StringComparison.OrdinalIgnoreCase)) cfg.StartMinimized = true;
        }
        try { Process.GetCurrentProcess().PriorityClass = ProcessPriorityClass.AboveNormal; } catch { }

        if (haveConsole)
        {
            Console.WriteLine("=================================================");
            Console.WriteLine(" OsrsTouch - touch controls for OSRS");
            Console.WriteLine("   tap = click      hold = right click");
            Console.WriteLine("   swipe = camera   pinch = zoom");
            Console.WriteLine("   Ctrl+Alt+T = pause/resume    Ctrl+C = quit");
            Console.WriteLine("=================================================");
        }
        Log("Settings: " + Config.PathFor());
        Log("Active in: " + cfg.GameProcesses + "   camera: " + (cfg.RotateMode == "keys" ? "arrow keys" : "middle-mouse drag"));
        if (cfg.DiagnoseOnly) Log("DIAGNOSE MODE: logging only, no input will be changed.");
        if (cfg.BlockTest) Log("BLOCK TEST: Windows' touch-clicks are blocked and NOTHING is sent. Touching the game should do nothing at all.");

        CreateMessageWindow();

        if (cfg.TapBlip)
        {
            byte br, bg, bb;
            ParseColor(cfg.BlipColor, out br, out bg, out bb);
            blip = new TapOverlay(Marshal.GetFunctionPointerForDelegate(wndProcKeep), cfg.BlipSizePx, cfg.BlipMs, cfg.BlipAlpha, br, bg, bb);
        }

        if (cfg.ScreenRotation != "auto")
        {
            int forced;
            if (int.TryParse(cfg.ScreenRotation, out forced) && (forced == 0 || forced == 90 || forced == 180 || forced == 270))
                ScreenMap.Forced = forced;
            else Log("ScreenRotation = " + cfg.ScreenRotation + " isn't one of auto/0/90/180/270 - asking Windows instead.");
        }
        ScreenMap.Refresh(IntPtr.Zero);
        Log("screen: " + ScreenMap.Describe());

        reader = new TouchReader { Verbose = cfg.Verbose, LogRaw = cfg.LogRaw };
        reader.Frame += OnFrame;
        if (!TouchReader.Register(hwnd)) Log("ERROR: couldn't listen to the touchscreen (code " + Marshal.GetLastWin32Error() + ").");
        else Log("Listening to touchscreen. Try a tap, a swipe and a pinch in the game.");

        var hookThread = new Thread(HookThread);
        hookThread.IsBackground = true;
        hookThread.Priority = ThreadPriority.Highest;
        hookThread.Start();

        if (!RegisterHotKey(hwnd, HOTKEY_TOGGLE, 0x0001 | 0x0002 | 0x4000, 0x54)) Log("Note: Ctrl+Alt+T hotkey is taken by another app.");
        SetTimer(hwnd, (UIntPtr)TIMER_TICK, 25, IntPtr.Zero);

        try { Tray.Add(hwnd); } catch (Exception e) { Log("tray icon unavailable: " + e.Message); }
        Log("Running. Tray icon added; waiting for the game.");

        Console.CancelKeyPress += (s, e) => { e.Cancel = true; PostMessageW(hwnd, WM_CLOSE, IntPtr.Zero, IntPtr.Zero); };
        // closing the console window, logging off or an unexpected crash must not leave the pointer hidden
        ctrlKeep = delegate(uint type) { Cleanup(); Tray.Remove(); return false; };
        SetConsoleCtrlHandler(ctrlKeep, true);
        AppDomain.CurrentDomain.ProcessExit += (s, e) => { Cleanup(); Tray.Remove(); };
        AppDomain.CurrentDomain.UnhandledException += (s, e) => { Cleanup(); Tray.Remove(); };

        MSG msg;
        while (GetMessageW(out msg, IntPtr.Zero, 0, 0) > 0)
        {
            TranslateMessage(ref msg);
            DispatchMessageW(ref msg);
        }

        Cleanup();
        Tray.Remove();
        if (hook != IntPtr.Zero) UnhookWindowsHookEx(hook);
        Log("Bye.");
        GC.KeepAlive(mutex);
    }

    static bool haveConsole;
    static string logPath;
    static readonly object logLock = new object();

    public static void Log(string s)
    {
        string line = "[" + DateTime.Now.ToString("HH:mm:ss.fff") + "] " + s;
        if (haveConsole) Console.WriteLine(line);
        lock (logLock)
        {
            try
            {
                if (logPath == null) logPath = System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "OsrsTouch.log");
                // keep the log from growing forever
                var info = new System.IO.FileInfo(logPath);
                if (info.Exists && info.Length > 512 * 1024) info.Delete();
                System.IO.File.AppendAllText(logPath, line + Environment.NewLine);
            }
            catch { }
        }
    }

    public static string LogPath
    {
        get { return System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "OsrsTouch.log"); }
    }

    static void Cleanup()
    {
        if (middleDown) { Button(MOUSEEVENTF_MIDDLEUP); middleDown = false; }
        ReleaseKeys();
        CursorHider.Set(false);
        if (blip != null) blip.Hide();
    }

    static void ParseColor(string hex, out byte r, out byte g, out byte b)
    {
        r = 0xFF; g = 0xDD; b = 0x33;
        try
        {
            if (hex != null && hex.Length >= 6)
            {
                r = Convert.ToByte(hex.Substring(0, 2), 16);
                g = Convert.ToByte(hex.Substring(2, 2), 16);
                b = Convert.ToByte(hex.Substring(4, 2), 16);
            }
        }
        catch { }
    }

    // ---------------------------------------------------------------- window / messages

    static void CreateMessageWindow()
    {
        const string cls = "OsrsTouchWnd";
        wndProcKeep = WndProc;
        fixed (char* pCls = cls)
        {
            var wc = new WNDCLASSEXW
            {
                cbSize = (uint)sizeof(WNDCLASSEXW),
                lpfnWndProc = Marshal.GetFunctionPointerForDelegate(wndProcKeep),
                hInstance = GetModuleHandleW(IntPtr.Zero),
                lpszClassName = pCls,
            };
            RegisterClassExW(ref wc);
        }
        hwnd = CreateWindowExW(0, cls, "OsrsTouch", 0, 0, 0, 0, 0, IntPtr.Zero, IntPtr.Zero, GetModuleHandleW(IntPtr.Zero), IntPtr.Zero);
        if (hwnd == IntPtr.Zero) { Log("FATAL: CreateWindow failed (" + Marshal.GetLastWin32Error() + ")"); Environment.Exit(1); }
    }

    static IntPtr WndProc(IntPtr h, uint msg, IntPtr w, IntPtr l)
    {
        try
        {
            if (h != hwnd && hwnd != IntPtr.Zero) return DefWindowProcW(h, msg, w, l);  // the blip window
            switch (msg)
            {
                case WM_INPUT:
                    if (enabled) reader.OnWmInput(l);
                    break;
                case WM_TIMER:
                    if (w.ToInt64() == TIMER_TICK) OnTick();
                    return IntPtr.Zero;
                case WM_HOTKEY:
                    TogglePause();
                    return IntPtr.Zero;

                case Tray.WM_TRAY:
                    {
                        uint ev = (uint)l.ToInt64();
                        if (ev == WM_RBUTTONUP_TRAY || ev == WM_LBUTTONUP)
                        {
                            int cmd = Tray.ShowMenu(!enabled);
                            if (cmd == Tray.CMD_TOGGLE) TogglePause();
                            else if (cmd == Tray.CMD_LOG) Tray.Open(LogPath);
                            else if (cmd == Tray.CMD_SETTINGS) Tray.Open(Config.PathFor());
                            else if (cmd == Tray.CMD_EXIT) PostQuitMessage(0);
                        }
                        else if (ev == WM_LBUTTONDBLCLK) TogglePause();
                    }
                    return IntPtr.Zero;
                case WM_CLOSE:
                    PostQuitMessage(0);
                    return IntPtr.Zero;
            }
        }
        catch (Exception e) { Log("error: " + e.Message); }
        return DefWindowProcW(h, msg, w, l);
    }

    /// <summary>
    /// The hook lives alone on this thread so nothing else can stall it: if the callback is
    /// slow, Windows stops honouring what it returns and its touch-clicks reach the game.
    /// </summary>
    static void HookThread()
    {
        hookKeep = HookProc;
        hook = SetWindowsHookExW(WH_MOUSE_LL, Marshal.GetFunctionPointerForDelegate(hookKeep), GetModuleHandleW(IntPtr.Zero), 0);
        if (hook == IntPtr.Zero) { Log("ERROR: couldn't install mouse hook (code " + Marshal.GetLastWin32Error() + ")."); return; }
        MSG m;
        while (GetMessageW(out m, IntPtr.Zero, 0, 0) > 0) { TranslateMessage(ref m); DispatchMessageW(ref m); }
    }

    static void TogglePause()
    {
        enabled = !enabled;
        if (!enabled) { Cleanup(); touchActive = false; handling = false; mode = Mode.None; }
        Tray.Update(!enabled);
        Log(enabled ? "Resumed." : "Paused (Ctrl+Alt+T to resume).");
    }

    // ---------------------------------------------------------------- mouse hook
    // Its only job: stop Windows' touch-generated mouse events from reaching the game
    // while we handle a touch. Everything the game sees, we send ourselves.

    static IntPtr HookProc(int code, IntPtr w, IntPtr l)
    {
        if (code >= 0 && blockTouch && enabled)
        {
            var info = (MSLLHOOKSTRUCT*)l;
            ulong extra = info->dwExtraInfo.ToUInt64();
            bool fromTouch = extra != MARKER && (extra & 0xFFFFFF00UL) == 0xFF515700UL && (extra & 0x80UL) != 0;
            if (fromTouch)
            {
                int x = info->pt.X, y = info->pt.Y;
                winTouchX = x; winTouchY = y;        // cheap: two stores, no calls
                winTouchSeen++;
                System.Threading.Interlocked.Exchange(ref winTouchTick, (long)GetTickCount64());
                if (x >= bLeft && x < bRight && y >= bTop && y < bBottom)
                {
                    blockedCount++;      // everything the game sees, we send ourselves
                    return (IntPtr)1;
                }
            }
        }
        return CallNextHookEx(hook, code, w, l);
    }

    static void OnTick()
    {
        ulong now = GetTickCount64();
        CheckDisplay();
        CatchWindowsTouchPoint(now);
        if (blip != null) blip.Tick();
        UpdateCursorVisibility();
        if (liftPending && now - liftTick >= (ulong)cfg.LiftConfirmMs) { liftPending = false; EndTouch(); }
        RefreshBlockArea();
        if (parkAt != 0 && now >= parkAt)
        {
            parkAt = 0;
            if (!touchActive) Park();
        }
        if (touchActive && handling && mode == Mode.None && now - startTick >= (ulong)cfg.LongPressMs)
        {
            RightClick();
            mode = Mode.RightDone;
        }
        if ((touchActive || mode == Mode.Pinch) && now - lastRawTick > (ulong)cfg.StaleTouchMs)
        {
            if (cfg.Verbose) Log("touchscreen went quiet - ending gesture");
            EndTouch();
        }
    }

    /// <summary>
    /// Keeps an eye on the display the game is on - which monitor, and which way up. Rotating the
    /// screen changes where a given spot on the glass appears, so anything learned before the
    /// rotation is now wrong and has to go.
    /// </summary>
    static void CheckDisplay()
    {
        // Follow the game's own window, so a second monitor with a browser on it doesn't drag the
        // mapping away. Before the game has ever been in front, the primary display will do.
        IntPtr h = ForegroundIsGame() ? GetForegroundWindow() : IntPtr.Zero;
        if (h == IntPtr.Zero && ScreenMap.Known) return;
        if (!ScreenMap.Refresh(h)) return;
        Log("screen: " + ScreenMap.Describe());
        calib.Reset();
    }

    /// <summary>
    /// Works out, off the hook's thread, which screen area Windows' touch-clicks must be kept out
    /// of: the game's window minus any pass-through bands. The hook only compares against this.
    /// </summary>
    static void RefreshBlockArea()
    {
        RECT r;
        if (cfg.DiagnoseOnly || !ForegroundIsGame(out r)) { blockTouch = false; return; }
        bLeft = r.Left; bTop = r.Top;
        bRight = cfg.PassThroughRightPx > 0 ? Math.Max(r.Left, r.Right - cfg.PassThroughRightPx) : r.Right;
        bBottom = cfg.PassThroughBottomPx > 0 ? Math.Max(r.Top, r.Bottom - cfg.PassThroughBottomPx) : r.Bottom;
        blockTouch = true;

        if (cfg.Verbose && blockedCount > 0)
        {
            Log("blocked " + blockedCount + " touch-click event(s) from Windows");
            blockedCount = 0;
        }
    }

    /// <summary>Pointer stays hidden only while the game has focus, so it's never lost elsewhere.</summary>
    static void UpdateCursorVisibility()
    {
        if (!cfg.HideCursor) { if (CursorHider.Hidden) CursorHider.Set(false); return; }
        // Our own windows (the tap blip, the message window) count as "still the game": showing
        // the blip must never look like the game lost focus, or the real pointer flashes back
        // for a frame on every tap.
        bool inFront = enabled && (ForegroundIsGame() || ForegroundIsOurs());
        ulong tick = GetTickCount64();
        if (inFront) lastGameFrontTick = tick;

        // Only give the pointer back after the game has genuinely been away for a moment.
        bool wantHidden = inFront || tick - lastGameFrontTick < 400;
        if (wantHidden != gameWasInFront)
        {
            gameWasInFront = wantHidden;
            CursorHider.Set(wantHidden);
            if (cfg.Verbose) Log(wantHidden ? "game in front - hiding pointer" : "game not in front - pointer back");
        }
        ulong now = GetTickCount64();
        if (CursorHider.Hidden && now - lastCursorRefresh > 250) { lastCursorRefresh = now; CursorHider.Refresh(); }
    }

    // ---------------------------------------------------------------- gesture recognition

    static void OnFrame(List<Contact> frame)
    {
        lastRawTick = GetTickCount64();

        // Put every contact through the learned panel-to-screen mapping.
        for (int i = 0; i < frame.Count; i++)
        {
            var raw = frame[i];
            double mx, my;
            calib.Map(raw.U, raw.V, out mx, out my);
            frame[i] = new Contact(raw.Id, mx, my, raw.U, raw.V);
        }
        if (cfg.Calibrate) LearnMapping(frame);
        int n = frame.Count;
        if (n != touchCount && (cfg.Verbose || cfg.DiagnoseOnly))
            Log("fingers: " + n + (n > 0 ? "  " + string.Join(" ", frame.ConvertAll(t => "#" + t.Id + "(" + t.X.ToString("0") + "," + t.Y.ToString("0") + ")").ToArray()) : ""));
        int prev = touchCount;
        touchCount = n;

        if (cfg.DiagnoseOnly)
        {
            if (n >= 1 && prev == 0)
            {
                bool ok = PointInGame(frame[0].X, frame[0].Y);
                Log(ok ? "  -> would handle this touch (front window: " + lastFgName + ")"
                       : "  -> ignored. Front window is '" + lastFgName + "'. If that IS your game, put that name in GameProcesses in OsrsTouch.ini (or use *).");
            }
            return;
        }

        if (cfg.BlockTest) return;

        if (n == 0)
        {
            // Panels occasionally drop a frame mid-gesture. Confirm the lift a beat later
            // instead of ending the gesture (or firing a tap) on a single empty frame.
            if (touchActive || mode == Mode.Pinch) { liftPending = true; liftTick = lastRawTick; }
            return;
        }
        liftPending = false;

        if (n >= 2)
        {
            if (mode != Mode.Pinch) StartPinch(frame);
            else UpdatePinch(frame);
            return;
        }

        // ----- exactly one finger
        if (mode == Mode.Pinch) return;        // a finger left over from a pinch: wait for a clean lift
        var c = frame[0];
        if (!touchActive) { StartTouch(c); return; }
        if (!handling || mode == Mode.RightDone) return;

        double dist = Dist(c.X - sx, c.Y - sy);
        if (mode == Mode.None)
        {
            if (dist >= cfg.SwipeStartPx) StartRotate(c);
            return;
        }
        if (mode == Mode.Rotate) UpdateRotate(c);
    }

    /// <summary>
    /// Windows turns the first touch into a mouse click about 85ms after your finger lands, and
    /// that click carries the correctly rotated screen position. Watch for it arriving, rather
    /// than looking for it at touch-down when it doesn't exist yet.
    /// </summary>
    static void CatchWindowsTouchPoint(ulong now)
    {
        if (!cfg.Calibrate) return;
        long stamp = System.Threading.Interlocked.Read(ref winTouchTick);
        if (stamp == 0 || stamp == pairedStamp) return;

        // Use it as this touch's click position (the first one we see for this touch).
        if (touchActive && !startWinValid && (ulong)stamp >= startTick && (ulong)stamp - startTick < 700)
        {
            startWinValid = true;
            startWinX = winTouchX; startWinY = winTouchY;
            if (cfg.Verbose) Log("windows puts this touch at " + winTouchX + "," + winTouchY);
        }

        // And teach the mapping, by pairing it with where the finger was at that moment.
        if (haveContact && Math.Abs((long)lastContactTick - stamp) <= 120)
        {
            pairedStamp = stamp;
            calib.Add(lastContactU, lastContactV, winTouchX, winTouchY);
        }
    }

    /// <summary>
    /// Pair a finger with the screen position Windows worked out for it. Windows knows the real
    /// transform (rotation, which display), so a handful of these teach us the same thing.
    /// </summary>
    static void LearnMapping(List<Contact> frame)
    {
        // Only the primary contact produces a Windows click, so only single-finger frames help.
        haveContact = frame.Count == 1;
        if (!haveContact) return;
        lastContactU = frame[0].U; lastContactV = frame[0].V; lastContactTick = lastRawTick;
    }

    static void StartTouch(Contact c)
    {
        touchActive = true;
        mode = Mode.None;
        sx = c.X; sy = c.Y;
        startTick = GetTickCount64();

        startWinValid = false;      // Windows' click for this touch hasn't happened yet; see OnTick

        handling = PointInGame(c.X, c.Y) && !InPassThroughBand(c.X, c.Y);
        if (cfg.Verbose) Log(handling ? "finger down - deciding tap / hold / swipe" : "finger down outside our area - leaving it to Windows");

        // Move the pointer onto the finger right away. The game reads a middle-drag as movement
        // from wherever the pointer was when the button went down, so if we only moved it at the
        // moment we press, that jump counts as camera movement - a violent spin at the start of
        // every swipe. Arriving early costs nothing (the pointer is hidden anyway).
        if (handling && cfg.ClickMode != "post")
        {
            MoveTo(c.X, c.Y);
            CursorHider.Refresh();   // the pointer just became "active"; re-blank it straight away
        }
    }

    static void EndTouch()
    {
        bool wasHandling = handling;
        bool wasActive = touchActive;
        Mode m = mode;

        touchActive = false; handling = false; mode = Mode.None; liftPending = false;

        if (m == Mode.Rotate) EndRotate();
        else if (m == Mode.Pinch) EndPinch();
        else if (m == Mode.None && wasHandling && wasActive)
        {
            ulong held = GetTickCount64() - startTick;
            if (held >= (ulong)cfg.MinTapMs) Tap();
            else if (cfg.Verbose) Log("ignoring a " + held + "ms blip of contact (too brief to be a tap)");
        }

        if (wasHandling) graceUntil = GetTickCount64() + (ulong)cfg.SwallowGraceMs;
    }

    // ----- tap / hold

    static void Tap()
    {
        UseWindowsPoint();
        if (cfg.Verbose) Log("tap -> left click");
        if (blip != null) blip.Trigger(sx, sy);

        // If a right-click menu may be open, click for real: picking an entry depends on the
        // pointer being on it. Otherwise post the click, which never moves the pointer.
        if (menuMaybeOpen)
        {
            menuMaybeOpen = false;
            MoveTo(sx, sy);
            Thread.Sleep(Math.Max(5, cfg.RightHoldMs));
            Button(MOUSEEVENTF_LEFTDOWN);
            Button(MOUSEEVENTF_LEFTUP);
            ParkLater();
            return;
        }
        Click(MOUSEEVENTF_LEFTDOWN, MOUSEEVENTF_LEFTUP);
        ParkLater();
    }

    static void RightClick()
    {
        UseWindowsPoint();
        if (cfg.Verbose) Log("hold -> right click");
        if (blip != null && cfg.BlipOnHold) blip.Trigger(sx, sy);

        // The game's right-click menu is tied to where the pointer actually is: it closes itself
        // when the pointer isn't on it. So a right-click always moves the real pointer, even in
        // post mode, and holds the button briefly rather than pressing and releasing instantly.
        MoveTo(sx, sy);
        Thread.Sleep(Math.Max(5, cfg.RightHoldMs));
        Button(MOUSEEVENTF_RIGHTDOWN);
        Thread.Sleep(Math.Max(5, cfg.RightHoldMs));
        Button(MOUSEEVENTF_RIGHTUP);
        menuMaybeOpen = true;
    }

    /// <summary>Post the click to whichever of the game's windows is under that point.</summary>
    static bool PostClick(bool right)
    {
        var pt = new POINT { X = (int)Math.Round(sx), Y = (int)Math.Round(sy) };
        IntPtr target = WindowFromPoint(pt);
        if (target == IntPtr.Zero) return false;

        uint pid, fgPid;
        GetWindowThreadProcessId(target, out pid);
        GetWindowThreadProcessId(GetForegroundWindow(), out fgPid);
        if (pid != fgPid) return false;          // not the game's own window: fall back to a real click

        var cp = pt;
        if (!ScreenToClient(target, ref cp)) return false;
        IntPtr l = (IntPtr)(((cp.Y & 0xFFFF) << 16) | (cp.X & 0xFFFF));

        // Tell the game the mouse is here, then give it a couple of frames before clicking.
        // The client decides which tile is under the mouse while it draws, not the moment the
        // message lands - so a move and a click sent back to back get judged against the OLD
        // hover position, and the character walks to wherever the pointer used to be.
        PostMessageW(target, WM_MOUSEMOVE, IntPtr.Zero, l);
        PostMessageW(target, WM_MOUSEMOVE, IntPtr.Zero, l);
        Thread.Sleep(Math.Max(0, Math.Min(200, cfg.PostHoverMs)));
        PostMessageW(target, WM_MOUSEMOVE, IntPtr.Zero, l);
        PostMessageW(target, right ? WM_RBUTTONDOWN : WM_LBUTTONDOWN, (IntPtr)(right ? 2 : 1), l);
        PostMessageW(target, right ? WM_RBUTTONUP : WM_LBUTTONUP, IntPtr.Zero, l);
        if (cfg.Verbose) Log("posted " + (right ? "right" : "left") + " click at client " + cp.X + "," + cp.Y);
        return true;
    }

    /// <summary>
    /// After a tap the pointer sits where you touched, and the client keeps that tile lit -
    /// which then appears to trail your character. Clear it without disturbing anything else:
    ///
    ///   leave = tell the client its canvas lost the mouse, and don't move the pointer at all
    ///   edge  = move the pointer just above the game window (never onto the taskbar)
    ///   off   = leave the pointer where it landed
    /// </summary>
    static void Park()
    {
        if (menuMaybeOpen) return;                  // never disturb an open right-click menu
        string mode = cfg.ParkCursor ? cfg.ParkMode : "off";
        if (mode == "off") return;
        if (mode == "leave" && PostMouseLeave()) return;
        EdgePark();
    }

    /// <summary>Post "the mouse left" to the game's canvas; Java clears its hover on this.</summary>
    static bool PostMouseLeave()
    {
        POINT cur;
        if (!GetCursorPos(out cur)) return false;
        IntPtr target = WindowFromPoint(cur);
        if (target == IntPtr.Zero) return false;
        uint pid, fgPid;
        GetWindowThreadProcessId(target, out pid);
        GetWindowThreadProcessId(GetForegroundWindow(), out fgPid);
        if (pid != fgPid) return false;
        PostMessageW(target, WM_MOUSELEAVE, IntPtr.Zero, IntPtr.Zero);
        if (cfg.Verbose) Log("told the game the mouse left its canvas");
        return true;
    }

    /// <summary>Last resort: move the pointer above the window. Never below - that's the taskbar.</summary>
    static void EdgePark()
    {
        double x = (gameRect.Left + gameRect.Right) / 2.0;
        if (gameRect.Top > 1) { MoveTo(x, gameRect.Top - 2); }
        else MoveTo(gameRect.Left + 3, gameRect.Bottom - 3);   // inside the window, chat corner
        if (cfg.Verbose) Log("pointer parked off the game view");
    }

    // Park a moment later, so the click lands first and the game acts on the right tile.
    static void ParkLater() { parkAt = GetTickCount64() + (ulong)Math.Max(20, cfg.ParkDelayMs); }

    /// <summary>
    /// Deliver a click. In "post" mode the click is posted straight to the game's window with
    /// the coordinates inside the message, so the mouse pointer never moves and so can never
    /// flash into view. In "send" mode we move the pointer and click for real.
    /// </summary>
    /// <summary>
    /// Click where Windows saw the touch rather than where we mapped it, when the two disagree.
    /// Windows already applies the screen's rotation, so its position is right by definition -
    /// this keeps taps landing correctly even before the mapping has been learned.
    /// </summary>
    static void UseWindowsPoint()
    {
        // A quick tap can be over before Windows produces its click, so check one last time
        // here - the click may have landed in the few ms between the lift and this moment.
        if (!startWinValid && cfg.Calibrate)
        {
            long stamp = System.Threading.Interlocked.Read(ref winTouchTick);
            if (stamp != 0 && (ulong)stamp >= startTick && (ulong)stamp - startTick < 900)
            {
                startWinValid = true;
                startWinX = winTouchX; startWinY = winTouchY;
            }
        }

        if (cfg.Verbose || tapDiagLeft > 0)
        {
            if (tapDiagLeft > 0) tapDiagLeft--;
            long stamp = System.Threading.Interlocked.Read(ref winTouchTick);
            Log("tap: ours=" + sx.ToString("0") + "," + sy.ToString("0")
                + "  windows=" + (startWinValid ? startWinX.ToString("0") + "," + startWinY.ToString("0") : "NONE")
                + "  lastWinEvent=" + (stamp == 0 ? "never" : ((long)GetTickCount64() - stamp) + "ms ago")
                + "  winEvents=" + winTouchSeen + "  mapping=" + (calib.Ready ? "learned" : "from Windows")
                + "  rotation=" + ScreenMap.Rotation);
        }

        if (!startWinValid) return;
        if (Dist(startWinX - sx, startWinY - sy) > 4)
        {
            if (cfg.Verbose) Log("using Windows' touch position (" + startWinX.ToString("0") + "," + startWinY.ToString("0") + ") instead of ours");
            sx = startWinX; sy = startWinY;
        }
    }

    static void Click(uint down, uint up)
    {
        if (cfg.ClickMode == "post" && PostClick(down == MOUSEEVENTF_RIGHTDOWN)) return;
        if (cfg.ClickDelayMs > 0)
        {
            MoveTo(sx, sy);
            Thread.Sleep(Math.Min(60, cfg.ClickDelayMs));
            Button(down); Button(up);
            return;
        }
        int nx, ny;
        Normalize(sx, sy, out nx, out ny);
        var batch = stackalloc INPUT[3];
        batch[0] = MouseInput(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK, nx, ny, 0);
        batch[1] = MouseInput(down, 0, 0, 0);
        batch[2] = MouseInput(up, 0, 0, 0);
        SendInput(3, batch, sizeof(INPUT));
    }

    // ----- swipe = camera

    static void StartRotate(Contact c)
    {
        mode = Mode.Rotate;
        menuMaybeOpen = false;
        lastX = c.X; lastY = c.Y;
        if (cfg.Verbose) Log("swipe -> camera");
        if (cfg.RotateMode == "keys") return;

        savedCursorOk = GetCursorPos(out savedCursor);
        int mx, my;
        Margins(out mx, out my);
        // Anchor inside the safe area, so a re-grab doesn't land somewhere that needs another one.
        anchorX = vx = Clamp(sx, gameRect.Left + mx + 4, gameRect.Right - mx - 4);
        anchorY = vy = Clamp(sy, gameRect.Top + my + 4, gameRect.Bottom - my - 4);
        MoveTo(vx, vy);
        // In post mode the pointer hasn't been following the finger, so let the game see it
        // arrive before the button goes down - otherwise that jump reads as camera movement.
        if (cfg.ClickMode == "post") Thread.Sleep(15);
        Button(MOUSEEVENTF_MIDDLEDOWN);
        middleDown = true;
    }

    static void UpdateRotate(Contact c)
    {
        if (cfg.RotateMode == "keys") { UpdateRotateKeys(c); return; }

        double dx = (c.X - lastX) * cfg.RotateSensitivity * (cfg.InvertRotateX ? -1 : 1);
        double dy = cfg.EnablePitch ? (c.Y - lastY) * cfg.RotateSensitivity * (cfg.InvertRotateY ? -1 : 1) : 0;
        lastX = c.X; lastY = c.Y;
        if (dx == 0 && dy == 0) return;

        // A bad frame (a stale contact, a long gap) must not fling the camera across the world.
        dx = Clamp(dx, -cfg.MaxStepPx, cfg.MaxStepPx);
        dy = Clamp(dy, -cfg.MaxStepPx, cfg.MaxStepPx);
        if (cfg.Verbose) Log("camera step " + dx.ToString("0") + "," + dy.ToString("0"));

        double nx = vx + dx, ny = vy + dy;
        int mx, my;
        Margins(out mx, out my);
        if (nx < gameRect.Left + mx || nx > gameRect.Right - mx || ny < gameRect.Top + my || ny > gameRect.Bottom - my)
        {
            // ran out of room: let go, jump back to where the swipe started, grab again
            Button(MOUSEEVENTF_MIDDLEUP);
            vx = anchorX; vy = anchorY;
            MoveTo(vx, vy);
            Button(MOUSEEVENTF_MIDDLEDOWN);
            nx = vx + dx; ny = vy + dy;
        }
        vx = nx; vy = ny;
        MoveTo(vx, vy);
    }

    static void Margins(out int mx, out int my)
    {
        mx = Math.Min(Math.Max(8, (gameRect.Right - gameRect.Left) / 10), 140);
        my = Math.Min(Math.Max(8, (gameRect.Bottom - gameRect.Top) / 10), 140);
    }

    // arrow-key mode: the finger acts like a joystick, held away from where it landed
    static void UpdateRotateKeys(Contact c)
    {
        double dx = c.X - sx, dy = c.Y - sy;
        double dz = cfg.SwipeStartPx;
        ushort wantX = 0, wantY = 0;
        if (Math.Abs(dx) >= dz) wantX = ((dx > 0) == cfg.InvertRotateX) ? VK_LEFT : VK_RIGHT;
        if (cfg.EnablePitch && Math.Abs(dy) >= dz) wantY = ((dy > 0) == cfg.InvertRotateY) ? VK_UP : VK_DOWN;
        if (wantX != heldKeyX) { if (heldKeyX != 0) Key(heldKeyX, false); if (wantX != 0) Key(wantX, true); heldKeyX = wantX; }
        if (wantY != heldKeyY) { if (heldKeyY != 0) Key(heldKeyY, false); if (wantY != 0) Key(wantY, true); heldKeyY = wantY; }
    }

    static void EndRotate()
    {
        ReleaseKeys();
        if (middleDown) { Button(MOUSEEVENTF_MIDDLEUP); middleDown = false; }
        RestoreCursor();
        if (cfg.Verbose) Log("camera done");
    }

    static void ReleaseKeys()
    {
        if (heldKeyX != 0) { Key(heldKeyX, false); heldKeyX = 0; }
        if (heldKeyY != 0) { Key(heldKeyY, false); heldKeyY = 0; }
    }

    // ----- pinch = zoom

    static void StartPinch(List<Contact> f)
    {
        if (mode == Mode.Rotate) EndRotate();
        double cx, cy, d;
        Centroid(f, out cx, out cy, out d);
        mode = Mode.Pinch;
        touchActive = true;
        zoomRef = d;
        handling = PointInGame(cx, cy);
        if (cfg.Verbose) Log(handling ? "pinch -> zoom" : "pinch outside our area - ignoring");
        if (!handling) return;

        // The wheel always goes to whatever sits under the pointer, so put the pointer between
        // your fingers - that's the 3D view you're pinching on, not whatever panel it was over.
        savedCursorOk = GetCursorPos(out savedCursor);
        MoveTo(Clamp(cx, gameRect.Left + 1, gameRect.Right - 2), Clamp(cy, gameRect.Top + 1, gameRect.Bottom - 2));
        pinchX = cx; pinchY = cy;
    }

    static void UpdatePinch(List<Contact> f)
    {
        if (!handling) return;
        double cx, cy, d;
        Centroid(f, out cx, out cy, out d);

        // keep the pointer with the fingers if the pinch wanders
        if (Dist(cx - pinchX, cy - pinchY) > 40)
        {
            pinchX = cx; pinchY = cy;
            MoveTo(Clamp(cx, gameRect.Left + 1, gameRect.Right - 2), Clamp(cy, gameRect.Top + 1, gameRect.Bottom - 2));
        }
        double step = 1 + Math.Max(0.5, cfg.ZoomStepPercent) / 100.0;
        int sign = cfg.InvertZoom ? -1 : 1;
        int guard = 0;
        while (d / zoomRef >= step && guard++ < 40) { Wheel(sign * cfg.ZoomWheelDelta); zoomRef *= step; }
        while (d / zoomRef <= 1 / step && guard++ < 40) { Wheel(-sign * cfg.ZoomWheelDelta); zoomRef /= step; }
    }

    static void EndPinch() { RestoreCursor(); if (cfg.Verbose) Log("zoom done"); }

    static void RestoreCursor()
    {
        bool had = savedCursorOk;
        savedCursorOk = false;
        if (cfg.RestoreCursor && had) { MoveTo(savedCursor.X, savedCursor.Y); return; }
        Park();
    }

    // ---------------------------------------------------------------- helpers

    static void Centroid(List<Contact> f, out double cx, out double cy, out double dist)
    {
        f.Sort((p, q) => p.Id.CompareTo(q.Id));
        var a = f[0]; var b = f[1];
        cx = (a.X + b.X) / 2; cy = (a.Y + b.Y) / 2;
        dist = Math.Max(1, Dist(a.X - b.X, a.Y - b.Y));
    }

    static double Dist(double dx, double dy) { return Math.Sqrt(dx * dx + dy * dy); }
    static double Clamp(double v, double lo, double hi) { return v < lo ? lo : (v > hi ? hi : v); }
    static bool InRect(double x, double y, RECT r) { return x >= r.Left && x < r.Right && y >= r.Top && y < r.Bottom; }

    static bool InPassThroughBand(double x, double y) { return InBand(gameRect, x, y); }

    static bool InBand(RECT r, double x, double y)
    {
        return (cfg.PassThroughRightPx > 0 && x >= r.Right - cfg.PassThroughRightPx)
            || (cfg.PassThroughBottomPx > 0 && y >= r.Bottom - cfg.PassThroughBottomPx);
    }

    /// <summary>True if the game is in front and this point is inside its window (fills gameRect).</summary>
    static bool PointInGame(double x, double y)
    {
        RECT rect;
        if (!GameWindowRect(out rect)) return false;
        if (!InRect(x, y, rect)) return false;
        gameRect = rect;
        return true;
    }

    static bool ForegroundIsGame() { RECT r; return GameWindowRect(out r); }

    /// <summary>True when the window in front is one of ours (the blip overlay, say).</summary>
    static bool ForegroundIsOurs()
    {
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        uint pid;
        GetWindowThreadProcessId(fg, out pid);
        return pid == ourPid;
    }
    static bool ForegroundIsGame(out RECT r) { return GameWindowRect(out r); }

    static bool GameWindowRect(out RECT rect)
    {
        rect = new RECT();
        var fg = GetForegroundWindow();
        if (fg == IntPtr.Zero) return false;
        uint pid;
        GetWindowThreadProcessId(fg, out pid);
        string name;
        if (!procNames.TryGetValue(pid, out name))
        {
            try { name = Process.GetProcessById((int)pid).ProcessName; } catch { name = ""; }
            procNames[pid] = name;
        }
        lastFgName = name;
        if (cfg.GameProcesses.Trim() != "*")
        {
            bool match = false;
            foreach (var p0 in cfg.GameProcesses.Split(new[] { ',' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var p = p0.Trim();
                if (name.Equals(p, StringComparison.OrdinalIgnoreCase) || name.Equals(p.Replace(".exe", ""), StringComparison.OrdinalIgnoreCase)) match = true;
            }
            if (!match) { if (cfg.Verbose) Log("front window is '" + name + "', not in GameProcesses - ignoring"); return false; }
        }
        RECT r;
        GetClientRect(fg, out r);
        var tl = new POINT { X = 0, Y = 0 };
        ClientToScreen(fg, ref tl);
        rect = new RECT { Left = tl.X, Top = tl.Y, Right = tl.X + r.Right, Bottom = tl.Y + r.Bottom };
        return true;
    }

    static string Name(uint m)
    {
        switch (m)
        {
            case WM_LBUTTONDOWN: return "LeftDown";
            case WM_LBUTTONUP: return "LeftUp";
            case WM_RBUTTONDOWN: return "RightDown";
            case WM_RBUTTONUP: return "RightUp";
            default: return "0x" + m.ToString("X");
        }
    }

    // ---------------------------------------------------------------- input injection

    static INPUT MouseInput(uint flags, int dx, int dy, int data)
    {
        var inp = new INPUT { type = INPUT_MOUSE };
        inp.mi = new MOUSEINPUT { dx = dx, dy = dy, mouseData = unchecked((uint)data), dwFlags = flags, dwExtraInfo = new UIntPtr(MARKER) };
        return inp;
    }

    static void SendMouse(uint flags, int dx, int dy, int data)
    {
        var inp = MouseInput(flags, dx, dy, data);
        SendInput(1, &inp, sizeof(INPUT));
    }

    static void Normalize(double x, double y, out int nx, out int ny)
    {
        int left = GetSystemMetrics(SM_XVIRTUALSCREEN), top = GetSystemMetrics(SM_YVIRTUALSCREEN);
        int w = Math.Max(2, GetSystemMetrics(SM_CXVIRTUALSCREEN)), h = Math.Max(2, GetSystemMetrics(SM_CYVIRTUALSCREEN));
        nx = (int)Math.Round((x - left) * 65535.0 / (w - 1));
        ny = (int)Math.Round((y - top) * 65535.0 / (h - 1));
    }

    static void MoveTo(double x, double y)
    {
        int nx, ny;
        Normalize(x, y, out nx, out ny);
        SendMouse(MOUSEEVENTF_MOVE | MOUSEEVENTF_ABSOLUTE | MOUSEEVENTF_VIRTUALDESK, nx, ny, 0);
    }

    static void Button(uint flag) { SendMouse(flag, 0, 0, 0); }
    static void Wheel(int delta) { if (cfg.Verbose) Log(delta > 0 ? "zoom in" : "zoom out"); SendMouse(MOUSEEVENTF_WHEEL, 0, 0, delta); }

    static void Key(ushort vk, bool down)
    {
        var inp = new INPUT { type = INPUT_KEYBOARD };
        inp.ki = new KEYBDINPUT { wVk = vk, wScan = 0, dwFlags = down ? 0u : KEYEVENTF_KEYUP, time = 0, dwExtraInfo = new UIntPtr(MARKER) };
        SendInput(1, &inp, sizeof(INPUT));
    }
}
}
