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
dotnet publish Pinscreen2.App -c Release -r osx-arm64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false
```

- Windows:
```bash
dotnet publish Pinscreen2.App -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false
```

- Linux:
```bash
dotnet publish Pinscreen2.App -c Release -r linux-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:PublishTrimmed=false
```

Artifacts are under `Pinscreen2.App/bin/Release/<tfm>/<rid>/publish/`.

## Releases and auto-update

The app can download updates from GitHub Releases if you set `UpdateGitHubRepo` (e.g., `"yourname/pinscreen-2"`) in your per-user config.

### Create a release from local

Prereqs: GitHub CLI (`gh auth login`), git remote points to GitHub.

1) Tag a version
```powershell
$ver = "v0.1.0"
git tag -a $ver -m "Pinscreen 2 $ver"
git push origin $ver
```

Or use the helper script:
```powershell
./version.ps1 v0.1.0
```

2) Build and zip artifacts (Windows example; repeat for other platforms as needed)
```powershell
./publish.ps1 win-x64 -Zip
```

3) Create the GitHub Release and upload assets
```powershell
gh release create $ver .\Pinscreen2-win-x64.zip --title "Pinscreen 2 $ver" --notes "Release $ver"
```

Naming tips:
- Use zip filenames containing the target runtime/OS, e.g., `Pinscreen2-win-x64.zip`, `Pinscreen2-osx-arm64.zip`, `Pinscreen2-linux-x64.zip`.
- Each zip should contain the published app AND `Pinscreen2.Updater(.exe)` in the same folder.

Updater behavior:
- The app calls `https://api.github.com/repos/{UpdateGitHubRepo}/releases/latest`.
- It picks a zip asset matching the current OS/architecture, downloads to a temp file, runs `Pinscreen2.Updater` to apply it, and relaunches.

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

## Helper scripts

Prereqs:
- PowerShell execution policy allows running local scripts (recommended):
  - Set for current user: `Set-ExecutionPolicy -Scope CurrentUser -ExecutionPolicy RemoteSigned -Force`
- GitHub CLI installed and authenticated: `gh --version` and `gh auth login`

### version.ps1
Tags the repo, publishes for a single runtime, zips, and creates/updates the GitHub release.

Examples:
```powershell
./version.ps1 v1.2.3 win-x64 "Release v1.2.3"
./version.ps1 v1.2.3 osx-arm64
```

### publish.ps1
Publishes the app for a runtime; optional `-Zip` creates `Pinscreen2-<rid>.zip`.

Examples:
```powershell
./publish.ps1 win-x64
./publish.ps1 win-x64 -Zip
```

### release.ps1
End-to-end release across multiple runtimes by invoking `version.ps1` per RID.

Examples:
```powershell
./release.ps1 v1.2.3 -Runtimes win-x64,osx-arm64,linux-x64 -Notes "Release v1.2.3"
./release.ps1 v1.2.3 -Runtimes win-x64
```

