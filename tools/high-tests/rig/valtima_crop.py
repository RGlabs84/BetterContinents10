#!/home/rohan/upy/bin/python
# Added by Wubarrk on 2026-10-06 for 16k worlds (0.10.3).
# Crops a 4096 px square of VALtima's 16,383 px heightmap (4 m per pixel) around its tallest pixel, at the same 4 m per pixel, so that a
# world of World Size 7692 + Edge Size 500 (16,384 m across) made from it has VALtima's real ground at its real slopes around its summit.
# Prints where the tallest pixel is, in the full map and in the crop's world (x east, z north, metres from the centre), and what the numbers say.
# Usage (run under ~/valheim-testbed/heavy.sh 4G): valtima_crop.py <heightmap.png> <out crop.png> [size=4096]
import sys
import numpy as np
from PIL import Image
Image.MAX_IMAGE_PIXELS = None

src, out = sys.argv[1], sys.argv[2]
size = int(sys.argv[3]) if len(sys.argv) > 3 else 4096
AMOUNT = 10.8
im = Image.open(src)
print("mode", im.mode, "size", im.size)
a = np.asarray(im)
n = a.shape[0]
print("pixels", a.shape, a.dtype, "min", int(a.min()), "max", int(a.max()))
r, c = np.unravel_index(int(a.argmax()), a.shape)
v = int(a[r, c])
value = v / 65535.0
absolute = (value * AMOUNT - 0.15) * 200.0
print(f"tallest pixel: row {r}, column {c}, value {value:.5f}; at Amount {AMOUNT}: terrain {absolute:.1f} m (above the sea {absolute - 30:.1f} m); b = {value * AMOUNT - 0.15:.4f} units")
# the map spans World Size + Edge Size on each side of the centre: 4 m per pixel at 16,383 px (BC's Normalize: pixel / (n - 1))
total = 4.0 * n
x = (c / (n - 1) - 0.5) * total
z = (0.5 - r / (n - 1)) * total
print(f"tallest pixel in the full world (about {total:.0f} m across): x {x:.0f}, z {z:.0f} (beyond 16,350 m from the centre: {max(abs(x), abs(z)) > 16350})")
h = size // 2
r0 = min(max(r - h, 0), n - size)
c0 = min(max(c - h, 0), n - size)
crop = np.ascontiguousarray(a[r0:r0 + size, c0:c0 + size])
Image.fromarray(crop.astype("<u2"), mode="I;16").save(out)
cr, cc = r - r0, c - c0
xs = (cc - (size - 1) / 2.0) * 4.0
zs = ((size - 1) / 2.0 - cr) * 4.0
print(f"crop {size} px from row {r0}, column {c0}: the tallest pixel is at row {cr}, column {cc}, which is x {xs:.0f}, z {zs:.0f} in a world of World Size {size * 2 - 500} + Edge Size 500")
cm = crop.astype(np.float64) / 65535.0
print(f"crop: max value {cm.max():.5f} min {cm.min():.5f}, terrain {(cm.min() * AMOUNT - 0.15) * 200:.1f} .. {(cm.max() * AMOUNT - 0.15) * 200:.1f} m")
gy, gx = np.gradient(cm * AMOUNT * 200.0, 4.0)
slope = np.hypot(gx, gy)
print(f"crop: ground slope (rise over run of the heightmap's own pixels): mean {slope.mean():.3f}, p99 {np.percentile(slope, 99):.3f}, max {slope.max():.3f}; (|dx| + |dy|) max {(np.abs(gx) + np.abs(gy)).max():.3f}")
