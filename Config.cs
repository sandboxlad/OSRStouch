using System;
using System.Globalization;
using System.IO;

namespace OsrsTouch {

class Config
{
    public string GameProcesses = "RuneLite,osclient";

    // tap / hold
    public int LongPressMs = 450;
    public double SwipeStartPx = 18;
    public int ClickDelayMs = 0;
    public string ClickMode = "send";
    public int RightHoldMs = 40;
    public int PostHoverMs = 60;
    public int MinTapMs = 40;
    public int LiftConfirmMs = 45;

    // swipe = camera
    public string RotateMode = "middle";      // "middle" or "keys"
    public double RotateSensitivity = 1.5;
    public bool InvertRotateX = false;
    public bool InvertRotateY = false;
    public bool EnablePitch = true;
    public bool RestoreCursor = false;
    public bool ParkCursor = true;
    public int ParkDelayMs = 120;
    public string ParkMode = "leave";
    public double MaxStepPx = 220;

    // pinch = zoom
    public double ZoomStepPercent = 6;
    public int ZoomWheelDelta = 120;
    public bool InvertZoom = false;

    // look and feel
    public bool HideCursor = true;
    public bool TapBlip = true;
    public bool BlipOnHold = true;
    public int BlipSizePx = 72;
    public int BlipMs = 500;
    public int BlipAlpha = 200;
    public string BlipColor = "FFDD33";

    // areas left to Windows
    public int PassThroughRightPx = 0;
    public int PassThroughBottomPx = 0;

    // safety / troubleshooting
    public int SwallowGraceMs = 400;
    public int StaleTouchMs = 2000;
    public bool DiagnoseOnly = false;
    public bool BlockTest = false;
    public bool Verbose = false;
    public bool LogRaw = false;
    public bool StartMinimized = false;

    public static string PathFor() { return System.IO.Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "OsrsTouch.ini"); }

    const string Template =
@"; OsrsTouch settings. Edit, save, then restart OsrsTouch.
;
; Gestures:  tap = left click       press and hold = right click
;            swipe = camera         two-finger pinch = zoom

; Only act when one of these programs is in front (comma separated, no extension). Use * for any window.
GameProcesses = RuneLite,osclient

; ---- tap / hold
; Hold this long without moving to get a right-click.
LongPressMs = 450
; Move this many pixels and it becomes a swipe instead of a tap.
SwipeStartPx = 18
; How a tap reaches the game.
;   send = move the real pointer and click, exactly like a mouse. Reliable; the pointer may
;          wink into view for a frame. This is the right setting.
;   post = deliver the click as a message without moving the pointer. Looks cleaner, but the
;          game decides what you clicked from where the POINTER is, so taps can act on the
;          previously hovered tile. Left in for experimenting only.
ClickMode = send
; post mode only: how long to let the game notice the mouse has moved before clicking. The
; client works out which tile you are pointing at while it draws a frame, so if this is too
; short your character walks to where the pointer was before. Raise it if taps act on the
; wrong tile; lower it for snappier taps.
PostHoverMs = 60
; Only used by ClickMode = send. Normally 0: the move and the click go out in one batch.
ClickDelayMs = 0
; A right-click always moves the real pointer (the game's menu follows the pointer) and holds
; the button this long. Raise it if the menu ever flickers away as it opens.
RightHoldMs = 40
; Contact shorter than this is treated as noise rather than a tap.
MinTapMs = 40
; Wait this long after the last finger disappears before ending a gesture, so a dropped
; report mid-swipe doesn't cut it short. Raise it if gestures break up; lower for snappier taps.
LiftConfirmMs = 45

; ---- swipe = camera
; middle = hold middle mouse and move (1:1, like the mobile client)
; keys   = hold the arrow keys while your finger stays away from where it landed
RotateMode = middle
RotateSensitivity = 1.5
InvertRotateX = false
InvertRotateY = false
; Vertical swipes tilt the camera up and down
EnablePitch = true
; Put the mouse pointer back where it was once the gesture ends
RestoreCursor = false
; After a tap, stop the game highlighting the tile under the resting pointer (which otherwise
; appears to trail your character around).
ParkCursor = true
; How:
;   leave = tell the game its canvas lost the mouse; the pointer itself never moves
;   edge  = move the pointer just above the game window (inside it if there's no room above)
ParkMode = leave
; How long to wait after the click before parking. Too short and the click may act on the
; parked spot instead; raise it if taps start missing.
ParkDelayMs = 120
; Largest camera movement one touchscreen frame may produce, in pixels. Stops a bad frame
; from flinging the camera. Lower it if the camera ever lurches.
MaxStepPx = 220

; ---- pinch = zoom
; How much the gap between your fingers must change (percent) per zoom notch. Lower = faster.
ZoomStepPercent = 6
; Scroll amount per notch (120 = one mouse-wheel click)
ZoomWheelDelta = 120
InvertZoom = false

; ---- look and feel
; Hide the mouse pointer while the game is in front (it comes straight back when you
; switch away, pause with Ctrl+Alt+T, or quit).
HideCursor = true
; The yellow blip that marks where you tapped, like the mobile clients.
TapBlip = true
; Show it for a press-and-hold (right click) too.
BlipOnHold = true
; Full size in pixels, how long it lasts (ms), how strong it is (0-255), and its colour (RRGGBB).
BlipSizePx = 72
BlipMs = 500
BlipAlpha = 200
BlipColor = FFDD33

; ---- advanced
; Touches starting within this many pixels of the right/bottom edge of the game window are left
; to Windows instead of being treated as gestures. 0 = off, which is almost always what you want.
PassThroughRightPx = 0
PassThroughBottomPx = 0
; Start with the console window minimised (the autostart shortcut sets this itself).
StartMinimized = false

; ---- safety / troubleshooting
; Keep ignoring Windows' own touch-clicks for this long after a gesture ends.
SwallowGraceMs = 400
; End a gesture if the touchscreen goes quiet this long (ms).
StaleTouchMs = 2000
; DiagnoseOnly logs touches but changes nothing. Verbose logs each decision.
DiagnoseOnly = false
Verbose = false
; LogRaw dumps every single touchscreen report - very noisy, only for troubleshooting.
LogRaw = false
";

    static bool B(string v) { return v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1" || v.Equals("yes", StringComparison.OrdinalIgnoreCase); }
    static double D(string v) { return double.Parse(v, CultureInfo.InvariantCulture); }
    static int I(string v) { return int.Parse(v, CultureInfo.InvariantCulture); }

    public static Config Load()
    {
        var c = new Config();
        var path = PathFor();
        try
        {
            if (!File.Exists(path)) { File.WriteAllText(path, Template); return c; }
            foreach (var raw in File.ReadAllLines(path))
            {
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == ';' || line[0] == '#') continue;
                int eq = line.IndexOf('=');
                if (eq < 0) continue;
                string k = line.Substring(0, eq).Trim().ToLowerInvariant();
                string v = line.Substring(eq + 1).Trim();
                switch (k)
                {
                    case "gameprocesses": c.GameProcesses = v; break;
                    case "longpressms": c.LongPressMs = I(v); break;
                    case "swipestartpx": c.SwipeStartPx = D(v); break;
                    case "clickdelayms": c.ClickDelayMs = I(v); break;
                    case "clickmode": c.ClickMode = v.ToLowerInvariant(); break;
                    case "rightholdms": c.RightHoldMs = I(v); break;
                    case "posthoverms": c.PostHoverMs = I(v); break;
                    case "mintapms": c.MinTapMs = I(v); break;
                    case "liftconfirmms": c.LiftConfirmMs = I(v); break;
                    case "rotatemode": c.RotateMode = v.ToLowerInvariant(); break;
                    case "rotatesensitivity": c.RotateSensitivity = D(v); break;
                    case "invertrotatex": c.InvertRotateX = B(v); break;
                    case "invertrotatey": c.InvertRotateY = B(v); break;
                    case "enablepitch": c.EnablePitch = B(v); break;
                    case "restorecursor": c.RestoreCursor = B(v); break;
                    case "parkcursor": c.ParkCursor = B(v); break;
                    case "parkdelayms": c.ParkDelayMs = I(v); break;
                    case "parkmode": c.ParkMode = v.ToLowerInvariant(); break;
                    case "maxsteppx": c.MaxStepPx = D(v); break;
                    case "zoomsteppercent": c.ZoomStepPercent = D(v); break;
                    case "zoomwheeldelta": c.ZoomWheelDelta = I(v); break;
                    case "invertzoom": c.InvertZoom = B(v); break;
                    case "hidecursor": c.HideCursor = B(v); break;
                    case "tapblip": c.TapBlip = B(v); break;
                    case "bliponhold": c.BlipOnHold = B(v); break;
                    case "blipsizepx": c.BlipSizePx = I(v); break;
                    case "blipms": c.BlipMs = I(v); break;
                    case "blipalpha": c.BlipAlpha = I(v); break;
                    case "blipcolor": c.BlipColor = v.Replace("#", "").Trim(); break;
                    case "passthroughrightpx": c.PassThroughRightPx = I(v); break;
                    case "passthroughbottompx": c.PassThroughBottomPx = I(v); break;
                    case "swallowgracems": c.SwallowGraceMs = I(v); break;
                    case "staletouchms": c.StaleTouchMs = I(v); break;
                    case "diagnoseonly": c.DiagnoseOnly = B(v); break;
                    case "verbose": c.Verbose = B(v); break;
                    case "lograw": c.LogRaw = B(v); break;
                    case "startminimized": c.StartMinimized = B(v); break;
                }
            }
        }
        catch (Exception e) { Program.Log("Couldn't read settings (" + e.Message + "); using defaults."); }
        return c;
    }
}
}
