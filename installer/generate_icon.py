"""Generate Pinscreen 2 app icon (Pinscreen2.App/Assets/icon.ico).

Pure Python, no Pillow dependency. Emits a multi-size ICO with PNG-encoded
images. Design: a stylized pinball / display-screen mark -- a circular pinball
inside a rounded-rectangle "screen" frame on a deep blue gradient.
"""

import math
import struct
import zlib
from pathlib import Path


def make_png(w, h, rgba):
    def chunk(t, d):
        c = t + d
        return struct.pack(">I", len(d)) + c + struct.pack(">I", zlib.crc32(c) & 0xFFFFFFFF)
    sig = b"\x89PNG\r\n\x1a\n"
    ihdr = struct.pack(">IIBBBBB", w, h, 8, 6, 0, 0, 0)
    raw = b"".join(b"\x00" + bytes(rgba[y * w * 4:(y + 1) * w * 4]) for y in range(h))
    return sig + chunk(b"IHDR", ihdr) + chunk(b"IDAT", zlib.compress(raw, 9)) + chunk(b"IEND", b"")


def clamp(v, lo=0, hi=255):
    return max(lo, min(hi, int(round(v))))


def smoothstep(edge0, edge1, x):
    t = max(0.0, min(1.0, (x - edge0) / (edge1 - edge0)))
    return t * t * (3 - 2 * t)


def render(size):
    s = size
    px = bytearray(s * s * 4)
    cx = cy = (s - 1) / 2.0
    outer_r = s * 0.46          # screen frame outer radius (rounded square)
    frame_thick = max(2, s * 0.06)
    ball_r = s * 0.22

    for y in range(s):
        for x in range(s):
            # Background: vertical gradient deep blue -> purple
            t = y / (s - 1)
            br = clamp(20 + 30 * t)
            bg_g = clamp(28 + 8 * t)
            bb = clamp(60 + 80 * t)
            r, g, b, a = br, bg_g, bb, 255

            dx = x - cx
            dy = y - cy

            # Rounded-square "screen" frame: distance to rounded square of half-size = outer_r, corner radius = outer_r*0.28
            half = outer_r
            corner = outer_r * 0.30
            qx = abs(dx) - (half - corner)
            qy = abs(dy) - (half - corner)
            outside = math.hypot(max(qx, 0), max(qy, 0)) - corner
            inside = min(max(qx, qy), 0)
            sd = outside + inside  # signed distance: <0 inside, >0 outside

            # Frame: ring near sd==0 with thickness frame_thick
            frame_a = smoothstep(frame_thick / 2 + 1, frame_thick / 2 - 1, abs(sd))
            if frame_a > 0:
                # Frame is light gray with subtle highlight on top edge
                shade = 200 + 30 * (1 - y / s)
                fr = clamp(shade)
                fg = clamp(shade)
                fb = clamp(shade + 5)
                r = clamp(r * (1 - frame_a) + fr * frame_a)
                g = clamp(g * (1 - frame_a) + fg * frame_a)
                b = clamp(b * (1 - frame_a) + fb * frame_a)

            # Screen interior fill (sd < -frame_thick/2): dark teal
            interior_a = smoothstep(-frame_thick / 2 - 1, -frame_thick / 2 + 1, -sd)
            if interior_a > 0:
                ir = 12
                ig = 36
                ib = 48
                r = clamp(r * (1 - interior_a) + ir * interior_a)
                g = clamp(g * (1 - interior_a) + ig * interior_a)
                b = clamp(b * (1 - interior_a) + ib * interior_a)

            # Ball: solid disc with chrome highlight
            ball_d = math.hypot(dx, dy) - ball_r
            ball_a = smoothstep(1, -1, ball_d) * interior_a
            if ball_a > 0:
                # Chrome shading: lighter top-left, darker bottom-right
                shade_t = max(0.0, min(1.0, 0.5 - (dx + dy) / (4 * ball_r)))
                base = 110 + 130 * shade_t
                # Tiny specular highlight
                hx = -ball_r * 0.35
                hy = -ball_r * 0.45
                spec = math.exp(-((dx - hx) ** 2 + (dy - hy) ** 2) / (2 * (ball_r * 0.18) ** 2))
                base = min(255, base + 80 * spec)
                cr = clamp(base)
                cg = clamp(base + 4)
                cb = clamp(base + 10)
                r = clamp(r * (1 - ball_a) + cr * ball_a)
                g = clamp(g * (1 - ball_a) + cg * ball_a)
                b = clamp(b * (1 - ball_a) + cb * ball_a)

            i = (y * s + x) * 4
            px[i] = r
            px[i + 1] = g
            px[i + 2] = b
            px[i + 3] = a
    return px


def make_ico(sizes, out_path):
    images = [(sz, make_png(sz, sz, render(sz))) for sz in sizes]
    n = len(images)
    header = struct.pack("<HHH", 0, 1, n)
    entries = b""
    blob = b""
    offset = 6 + n * 16
    for sz, png in images:
        w = 0 if sz >= 256 else sz
        h = 0 if sz >= 256 else sz
        entries += struct.pack("<BBBBHHII", w, h, 0, 0, 1, 32, len(png), offset)
        blob += png
        offset += len(png)
    out_path.write_bytes(header + entries + blob)
    print(f"Wrote {out_path} ({len(header + entries + blob)} bytes, sizes={sizes})")


def main():
    here = Path(__file__).resolve().parent
    project_root = here.parent
    assets = project_root / "Pinscreen2.App" / "Assets"
    assets.mkdir(parents=True, exist_ok=True)
    ico_path = assets / "icon.ico"
    png_path = assets / "icon.png"
    make_ico([16, 32, 48, 64, 128, 256], ico_path)
    png_path.write_bytes(make_png(256, 256, render(256)))
    print(f"Wrote {png_path}")


if __name__ == "__main__":
    main()
