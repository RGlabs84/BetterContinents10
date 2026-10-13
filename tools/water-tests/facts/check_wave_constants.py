#!/usr/bin/env python3
# Added by Wubarrk on 2026-10-13 for BC's own water (0.10.5).
"""Offline check, run by tools/build-water.sh: the wave constants of unity/Sea/SeaWaves.hlsl against the game's WaterVolume.CalcWave (the decompile in
libs-Tools: speed, length, height, sharpness and direction index of each of the ten waves; the ten directions normalised in float32 as Vector2.normalized
makes them) and against the shader's literals in the game's compiled water (WATER-SHADER-FACTS.md). Exits 1 on a difference.

    check_wave_constants.py [WaterVolume.cs]       (default: ~/WubarrkCODING/libs-Tools/Decompiled_1.0.17/assembly_valheim/WaterVolume.cs)
Skips with a note when the decompile is not there. Plain Python (float32 through struct).
"""
import os, re, struct, sys

HERE = os.path.dirname(os.path.abspath(__file__))
HLSL = os.path.join(HERE, "..", "..", "..", "unity", "Sea", "SeaWaves.hlsl")
CS = sys.argv[1] if len(sys.argv) > 1 else os.path.expanduser("~/WubarrkCODING/libs-Tools/Decompiled_1.0.17/assembly_valheim/WaterVolume.cs")


def f32(x):
    return struct.unpack("f", struct.pack("f", x))[0]


def main():
    hl = open(HLSL).read()
    if not os.path.exists(CS):
        print(f"check_wave_constants: {CS} is not there; skipped")
        return 0
    cs = open(CS).read()
    calls = re.findall(r"CreateWave\(worldPos, time, ([\d.]+)f, ([\d.]+)f, ([\d.]+)f \* (\w+), s_createWaveDirections\[(\d)\], s_createWaveTangents\[\d\], ([\d.]+)f\)", cs)
    hcalls = re.findall(r"BCSeaCreateWave\(xz, t, ([\d.]+), ([\d.]+), ([\d.]+) \* (\w+)(?: \* waveFactor)?, dir(\d), tan\d, ([\d.]+)\)", hl)
    bad = []
    if len(calls) != 10 or len(hcalls) != 10:
        bad.append(f"expected 10 CreateWave calls in each, found {len(calls)} and {len(hcalls)}")
    for i, (a, b) in enumerate(zip(calls, hcalls)):
        for k, name in ((0, "speed"), (1, "length"), (2, "height"), (5, "sharpness")):
            if float(a[k]) != float(b[k]):
                bad.append(f"wave {i + 1}: {name} {a[k]} in the game, {b[k]} in the HLSL")
        if a[4] != b[4]:
            bad.append(f"wave {i + 1}: direction index {a[4]} in the game, {b[4]} in the HLSL")
    # the directions: s_createWaveDirections, normalised in float32
    dirs = re.findall(r"new Vector2\((-?[\d.]+)f, (-?[\d.]+)f\)\.normalized", cs)
    for i in range(1, 10):
        x, y = f32(float(dirs[i][0])), f32(float(dirs[i][1]))
        mag = f32(f32(x * x + y * y) ** 0.5)
        nx, ny = f32(x / mag), f32(y / mag)
        m = re.search(rf"dir{i} = float2\((-?[\d.]+), (-?[\d.]+)\), tan{i} = float2\((-?[\d.]+), (-?[\d.]+)\)", hl)
        if not m:
            bad.append(f"direction {i}: not found in the HLSL")
            continue
        hx, hy, tx, ty = (float(g) for g in m.groups())
        # one float32 step of difference is fine (the normalisation's rounding)
        if abs(hx - nx) > 2e-7 or abs(hy - ny) > 2e-7 or abs(tx + ny) > 2e-7 or abs(ty - nx) > 2e-7:
            bad.append(f"direction {i}: ({nx:.9g}, {ny:.9g}) from the game's vector, ({hx}, {hy}) / ({tx}, {ty}) in the HLSL")
    if "num * 0.123 + v.x * 0.13123 * waveLength" not in hl or "num * 0.123f + vector.x * 0.13123f * waveLength" not in cs:
        bad.append("the second sine's argument differs (num * 0.123 + vector.x * 0.13123 * waveLength)")
    if "- 0.2) * waveHeight" not in hl or "- 0.2f) * waveHeight" not in cs:
        bad.append("the wave's offset or height factor differs (- 0.2) * waveHeight)")
    if bad:
        print("check_wave_constants FAILED:\n  " + "\n  ".join(bad))
        return 1
    print("check_wave_constants: the HLSL's ten waves have the game's constants (speed, length, height, sharpness, direction, the 0.123 / 0.13123 / 0.2 terms)")
    return 0


if __name__ == "__main__":
    sys.exit(main())
