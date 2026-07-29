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

### System tray

The server puts an icon in the notification area of the machine it runs on.
Present means serving; gone means down (the watchdog relaunches within ~10s, so
a permanently missing icon is a real fault). Hovering shows the video count and
how many screens are online.

Double-click opens the dashboard. Right-click gives: open dashboard, rescan
library now, push sync to all screens, open the server log, open the library
folder, restart, and stop. **Stop** writes a `stop.flag` sentinel that the
watchdog checks after each run — without it the watchdog would faithfully undo
every deliberate stop.

Set `ShowTrayIcon: false` or pass `--no-tray` to disable. It has no effect under
`-AsService`, where session 0 isolation means there is no desktop to attach to;
that is the tradeoff for a server that starts before login.

For a taskbar-pinned dashboard, `scripts/install-server.ps1` also creates a
"Pinscreen Library" shortcut (Start Menu and desktop) that opens the dashboard
in a browser app window. Windows 11 blocks programmatic taskbar pinning, so
pinning that one is a manual right-click.

### Management dashboard

Browse to `http://<hostname>:8088/` on any machine that can reach the server.

- **Library** — every game folder with video counts and sizes; click a row to
  list its files. **Rescan now** rebuilds the manifest immediately instead of
  waiting for the timer.
- **Pinscreens** — every screen that has ever connected, with online state,
  cached video count, free disk space, app version, and when it last finished a
  sync. **Sync** pushes to one screen; **Push sync to all** pushes to every
  connected screen. Offline screens can be forgotten.

  While a screen is syncing it reports the game and file it is downloading, plus
  an **Incoming** list of every game the sync intends to fetch with counts and
  sizes — so you can eyeball what a screen is about to receive rather than
  inferring it from one filename at a time.

A file that exhausts its retries no longer aborts the sync; the screen finishes
the rest and reports as `error` with a count of what failed. A single dropped
connection used to strand every remaining file until the next push.

### Knowing whether a screen needs a sync

Each screen diffs itself against the server manifest without downloading
anything, and reports the result: **✓ up to date** or **N videos behind** with
the games listed. Comparing cached counts alone can't answer this — two
libraries of equal size are not necessarily the same library.

The diff runs on connect, whenever the library changes, after every sync, and on
a 15-minute backstop. **Re-check all** forces it. Each screen also keeps a sync
history (last 25) of what it pulled and when.

Screens older than v1.8.3 don't report it and show "up-to-date state unknown".

### Clock placement after a display wake

The clock lives in a `Popup` because it has to paint above the native LibVLC
`VideoView`. On Windows a Popup is its own top-level native window, and Avalonia
places it when it *opens* — it does not follow the owner afterwards. A monitor
sleep/wake usually drops and re-adds the display, resizing the main window; the
`Canvas` inside the popup re-lays-out correctly (it is bound to
`PlacementTarget.Bounds`) but the popup's own window keeps its old size and
offset, so the clock sits at correct coordinates inside a stale window.

That is why adjusting the positioning maths never fixed it — the maths was never
wrong.

The trigger is a **resolution change**: turning a monitor off usually drops it
from the display topology, Windows falls back to a low mode, and on wake it
returns to the real one. `EnsureClockPlacement` samples a `DisplayGeometry` on
the existing 1-second clock timer — display mode, scaling, client size, root
size, window state — and when it changes and then holds for one tick it:

1. re-asserts fullscreen if the mode changed and the window is still sized to
   the old one (Windows leaves windows flagged fullscreen at a stale size, and
   anchoring the popup to those bounds is exactly how the clock ends up
   off-centre), then
2. closes and reopens the popup so Avalonia re-places it against current bounds.

Sampling rather than subscribing is deliberate: display events arrive in bursts
during a wake, and reacting to each is what caused the hangs that moved clock
updates onto a timer in the first place.

The decision rules live in `DisplayGeometry` — separate from the window so they
can be tested directly against the sequences a real sleep/wake produces (steady
state, fallback mode, stale window after wake, DPI-only change, unknown screen).

The app is a `WinExe` with no console, so each re-anchor is reported to the
dashboard as a counter next to the screen's display geometry (`🖵 1920x1080 ·3`).
Sleep and wake the monitor and watch that number increment to confirm the fix is
firing on a given screen.

Device records persist in `devices.json` next to the exe, so a powered-off
screen still appears in the list.

Click a screen's name to rename it — `WIN-G47M6NUFE63` says nothing about which
cabinet it is. The new name is pushed to the screen, which stores it in its own
`DeviceName` config, and the server keeps an override so a heartbeat still
reporting the machine name cannot revert it (and so renaming an offline screen
sticks).

### Pushing app updates from the dashboard

The server polls GitHub for the latest release and flags screens running an
older version. **Update** on a screen (or **Update all**) pushes an `update`
command down the same SSE channel; the screen then checks, downloads the
installer, verifies its sha256, runs it silently and restarts itself. Progress
is reported back, so a push update is watchable from the dashboard.

A screen already on the latest version does nothing. A screen mid-sync refuses
and reports why — replacing the app's files mid-sync would abort it and leave
half-written videos behind. Push again once it finishes.

**Elevation is the catch.** The installer writes to Program Files and needs
admin, but the app runs as `asInvoker`. Whether that is silent depends on the
screen's UAC policy:

- `ConsentPromptBehaviorAdmin = 0` (elevate without prompting) — silent, works
  unattended.
- Anything else — Windows raises a consent dialog, and a wall-mounted screen has
  nobody to click it.

Each screen reports whether it is elevated, and the dashboard marks an
`⚠ not elevated` screen while it installs. If the installer has not taken effect
two minutes after launch, the screen reports an error naming UAC as the likely
cause, rather than sitting on "installing" forever.

Check a screen's policy with:

```powershell
Get-ItemProperty 'HKLM:\SOFTWARE\Microsoft\Windows\CurrentVersion\Policies\System' ConsentPromptBehaviorAdmin
```

### Tailscale watchdog

Screens that reach the server over Tailscale are stranded when it dies —
recovering one meant remoting in and starting it by hand.

**The dashboard cannot fix this on its own, and it is worth being clear why.**
A screen whose Tailscale is down has no path back to the server: it simply shows
as offline, and a "restart Tailscale" command has no transport to arrive on. The
transport *is* what broke. Remote control only helps a screen that is degraded
but still reachable, or one on the LAN.

So the recovery runs on the screen. Every 60 seconds the app checks:

- `tailscale status --json` — backend state and whether this node is online
- whether `tailscaled` and `tailscale-ipn` are running

Status queries need no elevation. When it finds Tailscale down it climbs a
recovery ladder, cheapest first, and reports which rung worked:

1. **Tray gone, daemon alive** → relaunch `tailscale-ipn.exe`. No admin needed.
2. **Daemon down** → `sc start Tailscale`. Needs admin; reports "needs admin" on
   access denied rather than failing silently.
3. **Daemon up but backend stopped** → `tailscale up`.

Attempts are rate-limited to one per 5 minutes so a persistent failure retries
rather than spins. Disable with `WatchTailscale: false`.

The dashboard shows `TS ok` or `TS <state>` per screen, with a restart count, and
a **Restart TS** button for screens still reachable. While a screen is offline
those values are its *last known* state — which is itself the clue.

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

