#!/usr/bin/env python3
# Added by Wubarrk on 2026-10-06 for 16k worlds (0.10.3).
"""The export's fine bytes (C#) against fine_ref.py's own writer rule, on the same heights.

  BCEXPORT_ONLY=fine BCEXPORT_FINE_KEEP=<folder> dotnet run -c Release       (in tools/export-tests; TMPDIR on disk)
  python3 compare_fine_writer.py <folder>/fine-auto-81 2100 4                  (numpy and Pillow, fine_ref.py next to tools/ or in $FINE_REF)

FineTest keeps, with that export, x.f64 (the heights the exporter worked from, in grey steps, as little-endian doubles, file row 0 first) and
wet.u8 (1 where a pixel is under water by the height the world has there). Here fine_ref.encode and the steep-ground rule of
fine_ref.apply_policy are run on v = x / 65535, and the bytes must be the exporter's wherever the grey value is the same (the exporter picks
the grey value in float32 as it always has, fine_ref in double, so a few pixels differ by one step there; N = 256 c + f must still agree
to within the byte running out at the top of its range: 16 units with 4 bits).
"""
import os
import sys

import numpy as np

here = os.path.dirname(os.path.abspath(__file__))
for candidate in (os.environ.get('FINE_REF'), os.path.join(here, '..'), os.path.join(here, '..', '..'), '/home/rohan/valheim-testbed/bc-16k/fine'):
    if candidate and os.path.exists(os.path.join(candidate, 'fine_ref.py')):
        sys.path.insert(0, candidate)
        break
import fine_ref as fr


def main():
    folder, n, bits = sys.argv[1], int(sys.argv[2]), int(sys.argv[3])
    x = np.fromfile(f'{folder}/x.f64', np.float64).reshape(n, n)
    wet = np.fromfile(f'{folder}/wet.u8', np.uint8).reshape(n, n).astype(bool)
    hp = f'{folder}/heightmap.png'
    c_png = fr.read_grey(hp, fr.Png(hp))
    f_png = fr.signed(fr.read_grey(fr.fine_path(hp), fr.Png(fr.fine_path(hp))))
    v = x / 65535.0
    c_ref, f_ref = fr.encode(v, bits)
    fr.apply_policy(v, f_ref, None, 0.0)      # the steep-ground rule only: no Heightmap Amount, so no water test of its own
    f_ref[wet] = 0                            # under water, by the exporter's own height
    same_c = c_ref == c_png
    print(f'{n} x {n}, {bits} bits: the grey value differs at {int((~same_c).sum())} pixels')
    mismatch = int(((f_png != f_ref) & same_c).sum())
    print(f'f differs from fine_ref.py where the grey value is the same: {mismatch} of {int(same_c.sum())} pixels; f not 0 on {int((f_png != 0).sum())} (reference {int((f_ref != 0).sum())})')
    both = (f_png != 0) & (f_ref != 0)
    d = np.abs(fr.heights24(c_png, f_png) - fr.heights24(c_ref, f_ref))
    worst = int(d[both].max()) if both.any() else 0
    print(f'N = 256 c + f where both have a byte: {int(both.sum())} pixels, largest difference {worst} units')
    one_sided = int(((f_png != 0) != (f_ref != 0)).sum())
    print(f'a byte on one side only: {one_sided} pixels')
    return 0 if mismatch == 0 and one_sided == 0 and worst <= (1 << (8 - bits)) else 1


if __name__ == '__main__':
    sys.exit(main())
