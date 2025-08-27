# Pinscreen 2

Kiosk-style video loop player with a clock overlay.

- UI: Avalonia (.NET 9)
- Playback: LibVLCSharp (VLC engine)
- Platforms: macOS, Windows, Linux

## Prerequisites

- .NET SDK 9 (from Microsoft, Homebrew on macOS, or your distro)
- VLC media player (64-bit). LibVLCSharp uses VLC's native libraries.

## Getting started

### Windows
1. Install 64-bit VLC from videolan.org (avoid the Microsoft Store version).
2. Optional: set `LibVlcPath` in `Pinscreen2.App/config.json` to your VLC folder (the one with `libvlc.dll`), e.g. `C:\\Program Files\\VideoLAN\\VLC`. If omitted, the app will look in common locations and on PATH.
3. Put videos in `Pinscreen2.App/videos` or pick a folder in-app via "Set Media Folder…".
4. Run:
```powershell
dotnet run --project Pinscreen2.App
```

### macOS
1. Install VLC: `brew install --cask vlc` (or from videolan.org).
2. Run via helper (sets required env vars for the dynamic loader):
```bash
./run-macos.sh
```
3. Or run manually (replace paths if VLC is elsewhere):
```bash
DYLD_LIBRARY_PATH=/Applications/VLC.app/Contents/MacOS/lib \
VLC_PLUGIN_PATH=/Applications/VLC.app/Contents/MacOS/plugins \
dotnet run --project Pinscreen2.App
```

### Linux
1. Install VLC with your package manager (e.g., `sudo apt install vlc`).
2. Run:
```bash
dotnet run --project Pinscreen2.App
```
If LibVLC cannot be found, set `LibVlcPath` in config to a directory that contains `libvlc.so` and a `plugins` directory (or ensure they are on the loader path).

## Configuration

Config file is stored per-user:

- Windows: `%LOCALAPPDATA%/Pinscreen2/config.json`
- macOS: `~/Library/Application Support/Pinscreen2/config.json`
- Linux: `~/.config/Pinscreen2/config.json`

Default (OS-agnostic) contents:
```json
{
  "MediaFolders": [
    "videos"
  ],
  "ClockFormat": "HH:mm:ss",
  "BalanceQueueByGame": true,
  "LibVlcPath": ""
}
```

- MediaFolders: Folders to scan (recursively). Relative paths resolve next to the app.
- ClockFormat: .NET time format string.
- BalanceQueueByGame: Interleave items by immediate parent folder.
- LibVlcPath: Optional override to VLC's library directory.

Other optional fields (saved by the app): `ClockFontFamily`, `ClockColor`, `ClockXPercent`, `ClockYPercent`, `DelaySeconds`.

Supported extensions: `.mp4`, `.mov`, `.m4v`, `.mkv`, `.avi`, `.webm`

## Build

```bash
dotnet build
```

## Publish (self-contained)

No trimming (safer for native deps like VLC):

- macOS (Apple Silicon):
```bash
dotnet publish Pinscreen2.App -c Release -r osx-arm64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false
```

- Windows:
```bash
dotnet publish Pinscreen2.App -c Release -r win-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false
```

- Linux:
```bash
dotnet publish Pinscreen2.App -c Release -r linux-x64 --self-contained true \
  -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false
```

Artifacts are under `Pinscreen2.App/bin/Release/<tfm>/<rid>/publish/`.

## Notes

- macOS: the dynamic loader must know VLC's library locations at process start. Use `./run-macos.sh` which sets `DYLD_LIBRARY_PATH` and `VLC_PLUGIN_PATH` based on your VLC install or `LibVlcPath` in `Pinscreen2.App/config.json`.
- If you prefer manual run on macOS:
```bash
DYLD_LIBRARY_PATH=/Applications/VLC.app/Contents/MacOS/lib \
VLC_PLUGIN_PATH=/Applications/VLC.app/Contents/MacOS/plugins \
dotnet run --project Pinscreen2.App
```
- If playback fails with "libvlc" not found or status shows "VLC: missing":
  - Confirm 64-bit VLC is installed (Windows: avoid Store version; use Program Files, not Program Files (x86)).
  - Set `LibVlcPath` in config to the VLC folder with the library (`libvlc.dll`/`libvlc.dylib`/`libvlc.so`).
  - Alternatively, add VLC to your PATH and restart the app from the same shell.
- The queue rebuilds automatically when it reaches the end.

