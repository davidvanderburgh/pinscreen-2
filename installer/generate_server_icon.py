"""Generate the Pinscreen 2 server/dashboard icon (Pinscreen2.Server/favicon.ico).

Pure Python, no Pillow dependency -- same approach as generate_icon.py.

Design: deliberately the same family as the app icon (deep blue-to-purple
gradient, light rounded-rectangle "screen" frame, chrome pinball) so the two
read as one product, but with cyan sonar rings pinging outward from the ball.
The rings are the whole point of the server: it pushes sync commands out to
every screen. At 16px the rings collapse into a bright halo, which still reads
as distinct from the plain app icon.
"""

import math
import struct
import zlib
from pathlib import Path

# Matches --accent in the dashboard stylesheet.
ACCENT = (74, 163, 255)


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


def mix(base, over, a):
    return clamp(base * (1 - a) + over * a)


def render(size):
    s = size
    px = bytearray(s * s * 4)
    cx = cy = (s - 1) / 2.0
    outer_r = s * 0.46
    frame_thick = max(2, s * 0.06)
    ball_r = s * 0.155

    # Sonar rings pinging outward from the ball, fading as they travel. Ring
    # count drops at small sizes -- three rings inside 16px is an indistinct
    # blue smear, where one bold ring still reads as a ping.
    if s <= 20:
        rings = [(ball_r + s * 0.150, 1.00)]
        ring_thick = max(1.0, s * 0.055)
    elif s <= 32:
        rings = [(ball_r + s * 0.110, 1.00),
                 (ball_r + s * 0.235, 0.45)]
        ring_thick = max(1.1, s * 0.040)
    else:
        rings = [(ball_r + s * 0.085, 1.00),
                 (ball_r + s * 0.165, 0.62),
                 (ball_r + s * 0.245, 0.32)]
        ring_thick = max(1.1, s * 0.028)

    for y in range(s):
        for x in range(s):
            t = y / (s - 1)
            r = clamp(20 + 30 * t)
            g = clamp(28 + 8 * t)
            b = clamp(60 + 80 * t)

            dx = x - cx
            dy = y - cy

            half = outer_r
            corner = outer_r * 0.30
            qx = abs(dx) - (half - corner)
            qy = abs(dy) - (half - corner)
            outside = math.hypot(max(qx, 0), max(qy, 0)) - corner
            inside = min(max(qx, qy), 0)
            sd = outside + inside

            frame_a = smoothstep(frame_thick / 2 + 1, frame_thick / 2 - 1, abs(sd))
            if frame_a > 0:
                shade = 200 + 30 * (1 - y / s)
                r = mix(r, clamp(shade), frame_a)
                g = mix(g, clamp(shade), frame_a)
                b = mix(b, clamp(shade + 5), frame_a)

            interior_a = smoothstep(-frame_thick / 2 - 1, -frame_thick / 2 + 1, -sd)
            if interior_a > 0:
                r = mix(r, 12, interior_a)
                g = mix(g, 36, interior_a)
                b = mix(b, 48, interior_a)

            dist = math.hypot(dx, dy)

            # Rings, clipped to the screen interior so they never cross the frame.
            for radius, strength in rings:
                ring_a = smoothstep(ring_thick, 0.0, abs(dist - radius)) * interior_a * strength
                if ring_a > 0:
                    r = mix(r, ACCENT[0], ring_a)
                    g = mix(g, ACCENT[1], ring_a)
                    b = mix(b, ACCENT[2], ring_a)

            # Chrome ball, drawn last so it sits on top of the innermost ring.
            ball_a = smoothstep(1, -1, dist - ball_r) * interior_a
            if ball_a > 0:
                shade_t = max(0.0, min(1.0, 0.5 - (dx + dy) / (4 * ball_r)))
                base = 110 + 130 * shade_t
                hx = -ball_r * 0.35
                hy = -ball_r * 0.45
                spec = math.exp(-((dx - hx) ** 2 + (dy - hy) ** 2) / (2 * (ball_r * 0.18) ** 2))
                base = min(255, base + 80 * spec)
                r = mix(r, clamp(base), ball_a)
                g = mix(g, clamp(base + 4), ball_a)
                b = mix(b, clamp(base + 10), ball_a)

            i = (y * s + x) * 4
            px[i] = r
            px[i + 1] = g
            px[i + 2] = b
            px[i + 3] = 255
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
    server_dir = here.parent / "Pinscreen2.Server"
    server_dir.mkdir(parents=True, exist_ok=True)
    make_ico([16, 32, 48, 64, 128, 256], server_dir / "favicon.ico")
    (server_dir / "favicon.png").write_bytes(make_png(256, 256, render(256)))
    print(f"Wrote {server_dir / 'favicon.png'}")


if __name__ == "__main__":
    main()
