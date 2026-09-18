# OsrsTouch

Touch controls for Old School RuneScape on a Windows touchscreen. The mobile clients
have proper gestures, Windows doesn't - this gives you them on a Surface or any other
touch laptop.

<!-- drop a gif or screenshot here -->

## Gestures

| gesture | does |
| --- | --- |
| tap | left click |
| press and hold | right click (the menu) |
| one finger swipe | turn / tilt the camera |
| two finger pinch | zoom in and out |

It also hides the mouse pointer while the game is in front and leaves a yellow blip
where you tap, like the mobile clients.

## Download

Grab the latest zip from [Releases](../../releases/latest). Windows 10 or 11 with a
touchscreen, nothing to install.

1. Extract the zip somewhere permanent - not straight out of the zip window
2. Run `OsrsTouch.exe`. It sits in the tray next to the clock, no window
3. In OSRS: Settings > Controls > turn ON "middle mouse button controls camera" and
"scroll wheel can change zoom distance"
4. In RuneLite: turn OFF "Highlight hovered tile" in the Tile Indicators plugin

`Install-Autostart.bat` starts it with Windows. `Remove-Autostart.bat` undoes that and
closes it cleanly.

The exe is not code-signed, so SmartScreen will warn you: More info > Run anyway.
Everything it does is in the source here if you would rather read it or build it yourself.

## Is this bannable?

I can't speak for Jagex, so make your own call - but here is exactly what it does:

- It reads your touchscreen directly, through the same Windows API any app can use
- It works out which gesture you are making, and blocks the click Windows would have
sent from a touch
- It sends the equivalent mouse input instead: a swipe becomes a middle mouse drag,
a pinch becomes the scroll wheel, a tap becomes a click

It never reads game memory, never injects anything into the client, never modifies any
game file, and does nothing on its own. You play every second of it yourself - it just
lets you use your finger instead of a trackpad.

## Settings

`OsrsTouch.ini` appears next to the exe the first time you run it. Every setting is
commented in the file. The ones people usually touch:

| setting | for |
| --- | --- |
| `InvertRotateX` | camera turns the wrong way |
| `RotateSensitivity` | camera too fast or too slow |
| `ZoomStepPercent` | zoom speed (lower is faster) |
| `LongPressMs` | how long a hold takes to become a right click |
| `HideCursor` | set false to keep the pointer visible |

Restart OsrsTouch after editing. Right-click the tray icon to open the settings file
or the log.

## Rotated screens and second monitors

A touchscreen reports in its own fixed frame, tied to the glass - it has no idea the
display was rotated, and no idea which monitor it is. So OsrsTouch asks Windows which
monitor the game window is on and which way up it is, and turns the panel's coordinates
to match. All four orientations work, and so does a touchscreen that isn't the primary
display. There is nothing to set up and nothing to calibrate.

If a rotation ever does land taps in the wrong place, `ScreenRotation = 0 / 90 / 180 / 270`
in the ini overrides what Windows reports. Please open an issue too.

## Building it yourself

Eight C# files, no dependencies, targets .NET Framework 4.x. `build-with-mono.sh` has the
one-line compile command.
