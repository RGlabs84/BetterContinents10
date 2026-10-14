#!/usr/bin/env python3
# Added by Wubarrk on 2026-10-13 for lakes and clearing (0.10.5).
#
# The fixtures of tools/lake-tests that numpy makes, so that the bit order of Tarn's ".clear" file is checked against numpy's own packbits and not against
# this suite's reading of it: a mask file by Tarn's definition [T-HANDOFF "File formats"] ("TARNCLR1", int32 size, float32 half-extent, then size x size bits,
# row 0 the north edge, high bit first, numpy packbits), and two lake lists in Tarn's text format. Small (a few hundred bytes): no cap needed.
#   ~/.venvs/numpy/bin/python make_fixtures.py        (writes fixtures/ beside this file)
import os
import struct
import numpy as np

here = os.path.dirname(os.path.abspath(__file__))
out = os.path.join(here, "fixtures")
os.makedirs(out, exist_ok=True)


def pattern(size):
    # The same rule as Tests.Data.cs's MaskPattern: a diagonal, a stripe pattern and a bar along the south edge.
    r = np.arange(size).reshape(-1, 1)
    c = np.arange(size).reshape(1, -1)
    marked = ((r * 7 + c * 3 + (r * c) % 5) % 11 == 0) | (r == c) | ((r == size - 1) & (c > size // 2))
    return marked


def write(name, size, extent):
    bits = np.packbits(pattern(size).reshape(-1))  # bitorder 'big': the high bit is the first pixel
    with open(os.path.join(out, name), "wb") as f:
        f.write(b"TARNCLR1" + struct.pack("<if", size, extent))
        f.write(bits.tobytes())


write("clear37.bin", 37, 123.5)      # 37 x 37 = 1369 bits: not a multiple of 8 (171 bytes, the last holds 1 pixel)
write("clear64.bin", 64, 32.0)       # a multiple of 8
write("clear8.bin", 8, 3.0)          # one byte a row

with open(os.path.join(out, "lakes-tarn.txt"), "w", newline="\n") as f:
    f.write("# Tarn lakes: name;x;z;level;minX;minZ;maxX;maxZ;depth (world metres, absolute heights; x, z = a point in the water)\n")
    f.write("Vikedal1;-4210.5;3190.25;92;-4420;3050;-4010;3330;11.4\n")
    f.write("Vikedal2;-3900;3000.125;88.5;-4100;2900;-3700;3100;6.7\n")
    f.write("\n")
    f.write("# a short line, an extra field, and a bad number follow\n")
    f.write("Tarn;1;2;3\n")
    f.write("Extra;10;20;60;0;0;40;40;5;ignored;fields\n")
    f.write("Bad;10;20;sixty;0;0;40;40;5\n")
    f.write("Low;10;20;29;0;0;40;40;5\n")
    f.write("Flat;10;20;60;0;0;0;40;5\n")
print("fixtures written to", out)
