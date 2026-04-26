---
name: pinball-video-curator
description: Curate video assets extracted from pinball machines and copy them into the pinscreen-2 video library (D:\Pinball\videos\<Game Name>\). Use this when the user has extracted pinball assets (typically with one of the *-asset-decryptor repos) and wants to filter+copy videos meeting duration and resolution criteria to the pinscreen-2 library. Triggers on phrases like "curate pinball videos", "copy pinball videos", "prepare pinscreen videos".
---

# Pinball Video Curator

Curates video files extracted from pinball machines and copies the "good" ones into `D:\Pinball\videos\<Game Name>\` for use with the pinscreen-2 project (https://github.com/davidvanderburgh/pinscreen-2).

## Default criteria

- **Duration**: ≥ 2.0 seconds
- **Min file size**: 50 KB (drops near-empty/junk outputs without running ffprobe)
- **Filename blacklist** (case-insensitive substring): `test`, `debug`, `placeholder`, `_aux`, `blank`, `scores_looping`
- **Adaptive multi-cluster aspect filter (per game)**: probes every video in the source folder, then greedily extracts aspect-ratio clusters. Each cluster is the aspect that maximizes the number of files within ±15%; once chosen, those files are claimed and the next cluster is sought. A cluster is kept as "primary" if it contains ≥ 10% of the game's files AND its center aspect is within `[0.4, 4.5]` (sanity band that drops 5.93:1 banners and 9:1 ribbons). Files are kept if their aspect falls in any primary band. This handles multi-display games (main LCD + sub-LCD + portrait phone) and DMD-only games (Dominoes 4:1, Jetsons 2:1) uniformly.
- **Extensions scanned**: `.mp4 .mov .avi .mkv .webm .m4v .wmv .flv .ogv`
- **Target root**: `D:\Pinball\videos`
- **Conflict policy on copy**: skip if a file with that name already exists in the destination game folder (idempotent re-runs).

## How to invoke

The skill ships a Python script at `<skill-dir>\curate.py`. Invoke with the system Python; ffprobe must be on `PATH` (installed via WinGet at `C:\Users\david\AppData\Local\Microsoft\WinGet\Packages\Gyan.FFmpeg_*\ffmpeg-*\bin\ffprobe.exe`).

```
python curate.py [--source <folder> ...] [options]
```

### Sources & naming
- `--source PATH` — one or more source folders. Repeatable. If omitted, the script scans `C:\Users\david\OneDrive\Desktop` and includes only folders found in `mapping.json` (excluding `3d models`).
- `--dest PATH` — destination root. Default `D:\Pinball\videos`.
- `--mapping FILE` — folder→game name mapping JSON. Default `<skill-dir>\mapping.json`.
- `--game NAME` — override game name. Requires exactly one `--source`.

### Filter thresholds
- `--min-duration SECONDS` — default `2.0`
- `--min-bytes BYTES` — default `50000`
- `--blacklist SUBSTR` — repeatable. **Passing this flag replaces the entire default list**, so re-include any defaults you want kept.
- `--aspect-tolerance FRAC` — default `0.15` (±15% around each cluster center)
- `--min-cluster-pct PCT` — default `10.0`
- `--aspect-min FLOAT` — default `0.4` (reject cluster centers below this)
- `--aspect-max FLOAT` — default `4.5` (reject cluster centers above this)
- `--no-adaptive` — disable per-game cluster filtering entirely (keep all aspects)

### Action modes
- `--dry-run` — print what would be done; touch nothing
- `--move` — move source files instead of copying
- `--overwrite` — replace existing destination files (default is to skip)
- `--reset-game` — wipe each destination game folder before copying. Use when you want a hard reset.
- `--prune-dest` — after computing the per-game survivor set, delete any file in the destination game folder that is **not** a survivor. **Preferred path for re-curating** with stricter filters: it preserves files that are still valid (no needless re-copy) while removing the rejects. Idempotent.

## Workflow to follow when assisting the user

1. Read `mapping.json` and confirm the mapping covers any source folder the user names. If a folder isn't mapped, ask for the game name and add it to `mapping.json` (sorted alphabetically by key).
2. Default to a `--dry-run` first when changing thresholds or running on new sources. Summarize per-game counts (kept/copied/pruned/aspect/short/tiny/blacklist) and the chosen aspect bands. Get confirmation before the destructive run.
3. For re-curation with new criteria over an already-populated `D:\Pinball\videos`: use `--prune-dest` (not `--reset-game`) so unchanged survivors are not re-copied.
4. After every real run, surface errors (ffprobe failures, copy/prune failures) — they are logged but not raised.
5. Report per-game scanned/kept/copied/pruned counts, total bytes copied, total bytes freed, elapsed time.

## Notes for future extensions

- When new decryptor output layouts appear, do not hard-code paths. `iter_videos()` already recurses from the source folder.
- New container types (e.g. `.ts`, `.mxf`): add to `VIDEO_EXTS` in `curate.py`.
- New filename-noise patterns from a new platform: extend `DEFAULT_BLACKLIST` in `curate.py` rather than relying on users to remember `--blacklist`.
- If a game has only one display but the cluster filter rejects everything (look for `no primary aspect bands met threshold` in the log), either lower `--min-cluster-pct` or pass `--no-adaptive` for that source.
- `mapping.json` keys are matched case-insensitively against folder names; values are written verbatim as the destination subfolder name (Title Case With Spaces matches the rest of `D:\Pinball\videos`).
