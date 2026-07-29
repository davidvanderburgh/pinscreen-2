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

## Remote library (sync)

Instead of scanning local folders, the app can pull videos from a `Pinscreen2.Server` instance running on another machine on your LAN. Files are downloaded into a local cache via an explicit **Sync Now** button (no streaming during playback), so the device works offline once synced.

### Run the server

The server (`Pinscreen2.Server`) exposes the manifest, file downloads, a push
channel for connected screens, and a management dashboard.

Install (publishes a self-contained single-file exe to
`%LOCALAPPDATA%\Pinscreen2.Server`, writes `server-config.json`, installs the
watchdog launcher and Startup shortcut, and opens the firewall port):

```powershell
./scripts/install-server.ps1
```

`server-config.json` next to the exe drives everything:

```json
{
  "Root": "D:\\Pinball\\videos",
  "Port": 8088,
  "AutoPushOnChange": true,
  "RefreshMinutes": 5
}
```

- `AutoPushOnChange` — when a rescan finds the library changed, tell every
  connected screen to sync. This is what makes curating on the server enough.
- `RefreshMinutes` — how often to rescan `Root`.

Sanity check from another machine: `http://<hostname>:8088/manifest.json` returns
a JSON list of every video, and `http://<hostname>:8088/` is the dashboard.

#### Keeping it running

The server previously ran from a plain Startup shortcut that launched it exactly
once at login. It died on 2026-06-01 and stayed down for two months — nothing
restarted it and nothing reported it. Two supervision options now exist:

| | Watchdog (default) | Scheduled task (`-AsService`) |
|---|---|---|
| Elevation to install | no | yes |
| Restarts on crash | yes, within ~10s | yes, within 5 min |
| Runs before login | no | yes |
| Survives logout | no | yes |

```powershell
./scripts/install-server.ps1              # watchdog
./scripts/install-server.ps1 -AsService   # SYSTEM task, from an elevated shell
```

The watchdog is `scripts/start-server.vbs`: a hidden `wscript` loop that
relaunches the exe whenever it exits. The server writes its own rolling
`server.log` (5 MB, one prior generation kept) next to the exe.

### Management dashboard

Browse to `http://<hostname>:8088/` on any machine that can reach the server.

- **Library** — every game folder with video counts and sizes; click a row to
  list its files. **Rescan now** rebuilds the manifest immediately instead of
  waiting for the timer.
- **Pinscreens** — every screen that has ever connected, with online state,
  cached video count, free disk space, app version, live sync progress, and when
  it last finished a sync. **Sync** pushes to one screen; **Push sync to all**
  pushes to every connected screen. Offline screens can be forgotten.

Device records persist in `devices.json` next to the exe, so a powered-off
screen still appears in the list.

### How screens receive updates

Each screen holds a Server-Sent Events connection to `/events`. The server
pushes a `sync` command down it and the screen syncs on its own — nobody has to
touch a pinscreen. The connection reconnects forever with backoff, so a screen
that was asleep, offline, or behind a dropped Tailscale relay picks straight up
when it comes back.

A sync is pushed when:

- you press **Sync** or **Push sync to all** on the dashboard,
- a timed rescan finds the library changed (with `AutoPushOnChange`),
- you press **Rescan now** and it finds changes.

There is no authentication on any endpoint — the same as the file endpoints have
always been. Keep it on your LAN or Tailscale, not the open internet.

### Configure the client

In the app overlay, set **Remote library URL** to `http://<hostname>:8088`, hit
**Apply**, then **Sync Now**. Sync diffs the manifest against the local cache,
checks free disk space (with 1 GB headroom), and downloads anything missing —
files that won't fit are skipped and reported. Future syncs only pull new files.

Applying the URL also registers the screen with the server, after which pushed
syncs work with no further setup. Relevant config keys:

- `DeviceName` — name shown on the dashboard. Defaults to the machine name.
- `DeviceId` — stable id, generated once so the dashboard tracks the screen
  across restarts. Don't copy it between machines.
- `AutoSyncOnPush` — set false to register and report status but ignore pushed
  syncs.

The cache directory defaults to `%LOCALAPPDATA%/Pinscreen2/cache` on Windows (and the equivalent on macOS/Linux); override via `RemoteCacheDir` in config.

## Populating the library (Claude Code skill)

A Claude Code skill at `.claude/skills/pinball-video-curator/` automates curating videos extracted from pinball machines into `D:\Pinball\videos\<Game Name>\`. It probes each source file with `ffprobe`, applies an adaptive per-game aspect-ratio cluster filter (so multi-display games keep their main + sub displays and DMD-only games keep their narrow strip), drops short/blacklisted/junk files, and copies survivors into the library.

Symlink (or copy) the folder into your Claude config to use it:

```powershell
New-Item -ItemType SymbolicLink -Path "$env:USERPROFILE\.claude\skills\pinball-video-curator" -Target "$(Resolve-Path .claude\skills\pinball-video-curator)"
```

Then invoke via natural-language prompts like *"curate pinball videos"* — see `.claude/skills/pinball-video-curator/SKILL.md` for thresholds, action modes (`--dry-run`, `--prune-dest`, etc.), and the folder→game `mapping.json`.

## Kiosk debloat (Windows pinscreen)

`scripts/debloat-kiosk.ps1` disables/uninstalls Windows components a kiosk display device doesn't need (telemetry, Search indexer, SysMain, Xbox, Print Spooler, Cortana, preinstalled UWP bloat, etc). Each section is opt-in via switches, with safer defaults on and riskier ones (Defender, OneDrive uninstall) gated behind explicit flags.

```powershell
# Dry-run first (admin PowerShell on the pinscreen)
pwsh -File .\scripts\debloat-kiosk.ps1 -DryRun

# Apply the safe defaults
pwsh -File .\scripts\debloat-kiosk.ps1

# Aggressive: also disable Defender real-time + remove OneDrive
pwsh -File .\scripts\debloat-kiosk.ps1 -All
```

`-DisableDefender` requires Tamper Protection to already be off in Windows Security or the changes silently revert.

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

## Releases and one-click update

**Check for Updates…** in the overlay does the whole update: it asks GitHub for
the latest release, downloads `Pinscreen2_Setup_v<version>_win-x64.exe`, verifies
it against the sha256 GitHub publishes for the asset, runs it silently over the
top, and the installer relaunches the app.

The only prompt is the single UAC consent the installer needs to write to
Program Files. Downloading the file in-app (rather than handing a URL to a
browser) is deliberate: a browser-saved file carries the Mark-of-the-Web, so
every release — a fresh unsigned binary with no reputation — would put a
SmartScreen warning in front of a wall-mounted kiosk that usually has no
keyboard.

Requirements for this to work, all handled by the current build:

- The release must carry the Inno Setup installer asset. `UpdateService` returns
  "no installer" when it is missing, which also covers the window where the
  release row exists but CI is still uploading — the button must never point at
  a download that isn't there.
- The install directory must stay stable across versions (`{autopf}\Pinscreen2`),
  since the silent install lands over the existing one.
- `pinscreen2.iss` must keep the `/RELAUNCH=1` handling; `[Run]` is
  `skipifsilent`, so without it a silent update leaves a black screen.

If a screen was ever updated by unzipping `Pinscreen2-win-x64.zip` over the
install directory, its registry version will disagree with what it is actually
running. The first installer-based update corrects that.

### Create a release via GitHub Actions (preferred)

`.github/workflows/release.yml` triggers on any pushed `v*` tag and publishes the
win-x64 app zip, the Windows installer, and the server zip to the matching
release. The release must already exist when the workflow runs (it uploads with
`gh release upload --clobber`), so use:

```bash
gh release create vX.Y.Z --target main --title "vX.Y.Z" --notes "release notes here"
```

That single command creates the tag, the release, and triggers the multi-platform build.

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

### Release assets

| Asset | What it is | When you want it |
|---|---|---|
| `Pinscreen2_Setup_v<ver>_win-x64.exe` | Inno Setup installer — installs to `C:\Program Files\Pinscreen2`, creates shortcuts, registers an uninstaller | **Normal path.** This is what in-app update downloads and runs. |
| `Pinscreen2-win-x64.zip` | The same published app folder, zipped. No installer, no shortcuts, no uninstall entry. | Portable use, or inspecting the build. Extracting it over an installed copy leaves the registry version stale. |
| `Pinscreen2-Server-win-x64.zip` | Published `Pinscreen2.Server` | Deploying the server somewhere you can't build from source (otherwise use `scripts/install-server.ps1`). |

Both app assets carry identical payloads; the installer is smaller only because
Inno uses lzma2/ultra64 solid compression where the zip uses Deflate.

Builds are **Windows only** — the pinscreens are Windows kiosk boxes and nothing
else consumes these artifacts.

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

