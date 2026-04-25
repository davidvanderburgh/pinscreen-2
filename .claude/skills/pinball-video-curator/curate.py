#!/usr/bin/env python3
"""Curate pinball-extracted videos into D:\\Pinball\\videos\\<Game>\\.

See SKILL.md for usage.
"""
from __future__ import annotations

import argparse
import json
import shutil
import subprocess
import sys
import time
from dataclasses import dataclass
from pathlib import Path

VIDEO_EXTS = {".mp4", ".mov", ".avi", ".mkv", ".webm", ".m4v", ".wmv", ".flv", ".ogv"}
DEFAULT_DESK = Path(r"C:\Users\david\OneDrive\Desktop")
DEFAULT_DEST = Path(r"D:\Pinball\videos")
DEFAULT_BLACKLIST = ["test", "debug", "placeholder", "_aux", "blank", "scores_looping"]
DEFAULT_MIN_BYTES = 50_000


@dataclass
class Probe:
    duration: float | None
    width: int | None
    height: int | None
    error: str | None = None


def ffprobe(path: Path) -> Probe:
    try:
        out = subprocess.run(
            [
                "ffprobe", "-v", "error",
                "-select_streams", "v:0",
                "-show_entries", "stream=width,height:format=duration",
                "-of", "json",
                str(path),
            ],
            capture_output=True, text=True, timeout=30,
        )
        if out.returncode != 0:
            return Probe(None, None, None, out.stderr.strip()[:200] or "ffprobe-nonzero")
        data = json.loads(out.stdout or "{}")
        streams = data.get("streams") or [{}]
        s = streams[0] if streams else {}
        fmt = data.get("format") or {}
        dur = fmt.get("duration")
        return Probe(
            duration=float(dur) if dur else None,
            width=int(s["width"]) if s.get("width") else None,
            height=int(s["height"]) if s.get("height") else None,
        )
    except subprocess.TimeoutExpired:
        return Probe(None, None, None, "timeout")
    except Exception as e:
        return Probe(None, None, None, f"exception: {e}")


def iter_videos(root: Path):
    for p in root.rglob("*"):
        if p.is_file() and p.suffix.lower() in VIDEO_EXTS:
            yield p


def safe_name(name: str) -> str:
    bad = '<>:"/\\|?*'
    return "".join("_" if c in bad else c for c in name).strip()


def aspect_clusters(aspects: list[float], tolerance: float, min_pct: float,
                    aspect_min: float, aspect_max: float) -> list[tuple[float, float]]:
    """Return list of (lo, hi) aspect bands considered 'primary' for this game.

    Greedy clustering: repeatedly pick the aspect maximizing in-band count, mark
    those files as claimed, continue until no remaining cluster meets min_pct.
    Clusters whose center falls outside [aspect_min, aspect_max] are rejected.
    """
    if not aspects:
        return []
    total = len(aspects)
    threshold = max(1, int(round(total * min_pct / 100.0)))
    remaining_idx = set(range(total))
    bands: list[tuple[float, float]] = []
    while remaining_idx:
        best_count = 0
        best_center = None
        best_members: set[int] = set()
        for i in remaining_idx:
            c = aspects[i]
            lo, hi = c * (1 - tolerance), c * (1 + tolerance)
            members = {j for j in remaining_idx if lo <= aspects[j] <= hi}
            if len(members) > best_count:
                best_count = len(members)
                best_center = c
                best_members = members
        if best_count < threshold or best_center is None:
            break
        if aspect_min <= best_center <= aspect_max:
            bands.append((best_center * (1 - tolerance), best_center * (1 + tolerance)))
        remaining_idx -= best_members
    return bands


def in_any_band(aspect: float, bands: list[tuple[float, float]]) -> bool:
    return any(lo <= aspect <= hi for lo, hi in bands)


def build_sources(args, mapping: dict) -> list[tuple[Path, str]]:
    pairs: list[tuple[Path, str]] = []
    if args.source:
        for src in args.source:
            p = Path(src).resolve()
            if not p.is_dir():
                print(f"!! source not a directory: {p}", file=sys.stderr)
                continue
            key = p.name.lower()
            if args.game and len(args.source) == 1:
                game = args.game
            elif key in mapping:
                game = mapping[key]
            else:
                print(f"!! no mapping for folder '{p.name}' — use --game or update mapping.json", file=sys.stderr)
                continue
            pairs.append((p, game))
        return pairs
    if not DEFAULT_DESK.is_dir():
        print(f"!! default desktop not found: {DEFAULT_DESK}", file=sys.stderr)
        return pairs
    for child in sorted(DEFAULT_DESK.iterdir()):
        if not child.is_dir():
            continue
        key = child.name.lower()
        if key == "3d models":
            continue
        if key in mapping:
            pairs.append((child, mapping[key]))
    return pairs


def empty_stats() -> dict:
    return {"scanned": 0, "kept": 0, "copied": 0,
            "skipped_short": 0, "skipped_aspect": 0, "skipped_probe": 0,
            "skipped_exists": 0, "skipped_blacklist": 0, "skipped_tiny": 0,
            "pruned": 0, "pruned_bytes": 0,
            "bytes": 0, "errors": 0}


def main():
    ap = argparse.ArgumentParser(description="Curate pinball video assets into pinscreen-2 library.")
    ap.add_argument("--source", action="append", help="Source folder (repeatable). If omitted, scans Desktop + mapping.json.")
    ap.add_argument("--dest", default=str(DEFAULT_DEST))
    ap.add_argument("--min-duration", type=float, default=2.0)
    ap.add_argument("--min-bytes", type=int, default=DEFAULT_MIN_BYTES)
    ap.add_argument("--blacklist", action="append", default=None,
                    help=f"Case-insensitive filename substring to skip (repeatable). Default: {DEFAULT_BLACKLIST}")
    ap.add_argument("--aspect-tolerance", type=float, default=0.15,
                    help="Relative tolerance around each cluster center (default 0.15 = ±15%%).")
    ap.add_argument("--min-cluster-pct", type=float, default=10.0,
                    help="An aspect cluster must contain >= this %% of a game's files to be 'primary' (default 10).")
    ap.add_argument("--aspect-min", type=float, default=0.4,
                    help="Reject cluster centers below this aspect (default 0.4).")
    ap.add_argument("--aspect-max", type=float, default=4.5,
                    help="Reject cluster centers above this aspect (default 4.5).")
    ap.add_argument("--no-adaptive", action="store_true",
                    help="Disable per-game aspect-cluster filtering.")
    ap.add_argument("--prune-dest", action="store_true",
                    help="After computing survivors, delete any files in the destination game folder that are not survivors. Idempotent re-curation.")
    ap.add_argument("--mapping", default=str(Path(__file__).with_name("mapping.json")))
    ap.add_argument("--game", help="Override game name (requires single --source)")
    ap.add_argument("--dry-run", action="store_true")
    ap.add_argument("--move", action="store_true")
    ap.add_argument("--overwrite", action="store_true")
    ap.add_argument("--reset-game", action="store_true",
                    help="Delete the destination game folder before copying.")
    args = ap.parse_args()

    mapping_path = Path(args.mapping)
    mapping = json.loads(mapping_path.read_text(encoding="utf-8")) if mapping_path.exists() else {}

    sources = build_sources(args, mapping)
    if not sources:
        print("No source folders resolved. Nothing to do.")
        return 1

    dest_root = Path(args.dest)
    min_dur = args.min_duration
    min_bytes = args.min_bytes
    tol = args.aspect_tolerance
    blacklist = [b.lower() for b in (args.blacklist if args.blacklist is not None else DEFAULT_BLACKLIST)]

    totals = empty_stats()
    per_game: dict[str, dict] = {}
    start = time.time()

    for src, game in sources:
        g = per_game.setdefault(game, empty_stats())
        game_dir = dest_root / safe_name(game)
        print(f"\n== {src}  ->  {game_dir} ==")

        if args.reset_game and not args.dry_run and game_dir.exists():
            print(f"  -- resetting {game_dir}")
            shutil.rmtree(game_dir)

        # Pass 1: enumerate + cheap filters + probe.
        candidates = []  # list of (path, probe)
        aspects = []
        for video in iter_videos(src):
            totals["scanned"] += 1
            g["scanned"] += 1
            lower_name = video.name.lower()
            if any(b in lower_name for b in blacklist):
                totals["skipped_blacklist"] += 1
                g["skipped_blacklist"] += 1
                continue
            try:
                size_bytes = video.stat().st_size
            except OSError:
                size_bytes = 0
            if size_bytes < min_bytes:
                totals["skipped_tiny"] += 1
                g["skipped_tiny"] += 1
                continue
            probe = ffprobe(video)
            if probe.error or probe.duration is None or not probe.width or not probe.height:
                totals["skipped_probe"] += 1
                g["skipped_probe"] += 1
                print(f"  ?? probe-fail {video.name}: {probe.error or 'missing-metadata'}")
                continue
            if probe.duration < min_dur:
                totals["skipped_short"] += 1
                g["skipped_short"] += 1
                continue
            candidates.append((video, probe))
            aspects.append(probe.width / probe.height)

        # Compute aspect clusters for this game.
        bands: list[tuple[float, float]] = []
        if not args.no_adaptive:
            bands = aspect_clusters(aspects, tol, args.min_cluster_pct,
                                    args.aspect_min, args.aspect_max)
            if bands:
                shown = ", ".join(f"{lo:.2f}-{hi:.2f}" for lo, hi in bands)
                print(f"  -- primary aspect bands: {shown}")
            else:
                print(f"  -- no primary aspect bands met threshold; keeping all by aspect")

        # Pass 2: aspect filter + collect survivor names; copy.
        survivors: set[str] = set()
        for video, probe in candidates:
            if bands:
                ar = probe.width / probe.height
                if not in_any_band(ar, bands):
                    totals["skipped_aspect"] += 1
                    g["skipped_aspect"] += 1
                    continue
            survivors.add(video.name)

            target = game_dir / video.name
            if not args.overwrite and target.exists():
                totals["skipped_exists"] += 1
                g["skipped_exists"] += 1
                continue

            totals["kept"] += 1
            g["kept"] += 1

            if args.dry_run:
                print(f"  + KEEP {probe.width}x{probe.height} {probe.duration:.1f}s  {video.relative_to(src)}")
                continue

            game_dir.mkdir(parents=True, exist_ok=True)
            try:
                if args.move:
                    shutil.move(str(video), str(target))
                else:
                    shutil.copy2(str(video), str(target))
                size = target.stat().st_size
                totals["copied"] += 1
                totals["bytes"] += size
                g["copied"] += 1
                g["bytes"] += size
                print(f"  -> {target.name}  ({probe.width}x{probe.height}, {probe.duration:.1f}s, {size/1_000_000:.1f} MB)")
            except Exception as e:
                totals["errors"] += 1
                g["errors"] += 1
                print(f"  !! copy-failed {video}: {e}")

        # Pass 3: prune dest files not in survivor set.
        if args.prune_dest and game_dir.exists():
            for existing in game_dir.iterdir():
                if existing.is_file() and existing.name not in survivors:
                    try:
                        sz = existing.stat().st_size
                    except OSError:
                        sz = 0
                    if args.dry_run:
                        print(f"  - PRUNE {existing.name} ({sz/1_000_000:.1f} MB)")
                    else:
                        try:
                            existing.unlink()
                            print(f"  -x pruned {existing.name} ({sz/1_000_000:.1f} MB)")
                        except Exception as e:
                            totals["errors"] += 1
                            g["errors"] += 1
                            print(f"  !! prune-failed {existing}: {e}")
                            continue
                    totals["pruned"] += 1
                    totals["pruned_bytes"] += sz
                    g["pruned"] += 1
                    g["pruned_bytes"] += sz

    elapsed = time.time() - start
    print("\n==== Summary ====")
    for game, g in sorted(per_game.items()):
        print(f"  {game}: scanned={g['scanned']} kept={g['kept']} copied={g['copied']} pruned={g['pruned']} "
              f"short={g['skipped_short']} aspect={g['skipped_aspect']} probe={g['skipped_probe']} "
              f"exists={g['skipped_exists']} blacklist={g['skipped_blacklist']} tiny={g['skipped_tiny']} "
              f"err={g['errors']} size={g['bytes']/1_000_000:.1f}MB freed={g['pruned_bytes']/1_000_000:.1f}MB")
    print(f"\nTOTAL: scanned={totals['scanned']} kept={totals['kept']} copied={totals['copied']} pruned={totals['pruned']} "
          f"short={totals['skipped_short']} aspect={totals['skipped_aspect']} probe={totals['skipped_probe']} "
          f"exists={totals['skipped_exists']} blacklist={totals['skipped_blacklist']} tiny={totals['skipped_tiny']} "
          f"err={totals['errors']} size={totals['bytes']/1_000_000_000:.2f}GB freed={totals['pruned_bytes']/1_000_000_000:.2f}GB elapsed={elapsed:.1f}s")
    return 0


if __name__ == "__main__":
    sys.exit(main())
