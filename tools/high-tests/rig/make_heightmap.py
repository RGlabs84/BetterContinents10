#!/home/rohan/upy/bin/python
# Added by Wubarrk on 2026-10-06 for 16k worlds (0.10.3).
#
# The synthetic heightmap of the high-terrain rig test (tools/high-tests/README in the report): a 16-bit grey PNG, north up, for a
# world of World Size 8000 + Edge Size 500 (17,000 m across) read at Heightmap Amount 81 and Sea Level Adjustment 0.5, so that
#   metres = (pixel / 65535 * 81 - 0.15) * 200        and        pixel = (metres / 200 + 0.15) / 81 * 65535.
# A stepped mountain (a ziggurat) centred at (2200, 0) with flat plateaus at 1200, 4000, 8000, 15000 and 16100 m joined by cliffs, a
# gentle dome of 760 m at (-3000, 1500) for moderate slopes, and flat land of 60 m (30 m over the sea) everywhere else.
# Usage: make_heightmap.py <out.png> [size=2048]     (run under ~/valheim-testbed/heavy.sh: numpy over a 2048 px map is about 100 MB)
import sys
import numpy as np
from PIL import Image

TOTAL = 17000.0
AMOUNT = 81.0
SEA = 0.5  # Sea Level Adjustment 0.5 is an offset of 0

out = sys.argv[1]
n = int(sys.argv[2]) if len(sys.argv) > 2 else 2048

# pixel (column c, row r) -> world: x = (c / (n - 1) - 0.5) * TOTAL, z = (0.5 - r / (n - 1)) * TOTAL (north up)
c = np.arange(n, dtype=np.float64)
x = (c / (n - 1) - 0.5) * TOTAL
z = (0.5 - c / (n - 1)) * TOTAL
X, Z = np.meshgrid(x, z)

# the ziggurat: (radius from the centre, height), outside in, linear between
CX, CZ = 2200.0, 0.0
profile = [(4200, 60), (3700, 1200), (3000, 1200), (2600, 4000), (2000, 4000), (1700, 8000), (1100, 8000), (900, 15000), (500, 15000), (300, 16100), (0, 16100)]
r = np.hypot(X - CX, Z - CZ)
rs = np.array([p[0] for p in profile][::-1], dtype=np.float64)
hs = np.array([p[1] for p in profile][::-1], dtype=np.float64)
metres = np.interp(r, rs, hs)

# the dome: 60 m + 700 m * (1 - (d / 2500)^2) inside 2500 m of (-3000, 1500)
d = np.hypot(X + 3000.0, Z - 1500.0)
dome = 60.0 + 700.0 * np.clip(1.0 - (d / 2500.0) ** 2, 0.0, None)
metres = np.maximum(metres, np.where(d < 2500.0, dome, 0.0))

value = np.clip((metres / 200.0 + 0.15) / AMOUNT, 0.0, 1.0)
pixels = np.rint(value * 65535.0).astype(np.uint16)
Image.fromarray(pixels, mode="I;16").save(out)
back = (pixels.astype(np.float64) / 65535.0 * AMOUNT - 0.15) * 200.0
print(f"{out}: {n} px, {TOTAL / (n - 1):.2f} m/px, heights {back.min():.1f} .. {back.max():.1f} m, max quantisation error {np.abs(back - metres).max():.3f} m")
