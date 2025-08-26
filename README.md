# Pinscreen 2

Kiosk-style video loop player with a clock overlay.

- UI: Avalonia (.NET 9)
- Playback: LibVLCSharp (VLC engine)
- Platforms: macOS, Windows, Linux

## Prerequisites

- .NET SDK 9: `brew install --cask dotnet-sdk` (macOS) or from Microsoft.
- VLC: `brew install --cask vlc` (macOS) or install from videolan.org.

LibVLCSharp loads VLC's native libraries from your system install.

## Quick start

```bash
cd /Users/dvanderburgh/development/pinscreen-2
# (Optional) edit config first
open Pinscreen2.App/config.json
# Run
dotnet run --project Pinscreen2.App
```

Place videos in any folders listed in `Pinscreen2.App/config.json`.

## Configuration

File: `Pinscreen2.App/config.json`

```json
{
  "MediaFolders": [
    "/Users/dvanderburgh/Movies"
  ],
  "ClockFormat": "HH:mm:ss",
  "BalanceQueueByGame": true
}
```

- MediaFolders: Array of folders to scan (recursively) for videos.
- ClockFormat: .NET time format string.
- BalanceQueueByGame: Round-robins files by parent folder name to avoid over-represented folders.

Supported extensions: .mp4, .mov, .m4v, .mkv, .avi

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

- If playback fails with "libvlc" not found, ensure VLC is installed and on the default path (e.g., `/Applications/VLC.app` on macOS). Installing via Homebrew cask usually resolves it.
- The queue rebuilds automatically when it reaches the end.

