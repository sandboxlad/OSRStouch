mcs -langversion:7 -unsafe -platform:x64 -optimize+ -target:winexe -sdk:4.5 -out:OsrsTouch.exe \
  Native.cs TouchReader.cs TapOverlay.cs Tray.cs Calibration.cs Screen.cs Config.cs Program.cs
