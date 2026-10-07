#!/usr/bin/env python3
# Added by Wubarrk on 2026-10-06 for 16k worlds (0.10.3).
# Synthetic 16,384 px map sets for "dotnet run -c Release -- measure16k <folder> <kind> <0|1>" (Program.Measure16k.cs) and for a
# world made From Config on a dedicated server (Directory = the folder). Band by band, with a streaming PNG writer: peak RAM
# stays near 1 GB whatever the map size (needs numpy). Run it under ~/valheim-testbed/heavy.sh (4G), never beside another job.
#   gen16k.py <outdir> <size> <kind> [<kind> ...]
# kinds: height forest rough heat lava moss biome location spawn vegetation paint paintnoisy terrain altbiome (paintnoisy: every tile has odd pixels)
# Every map is deterministic (fixed seeds), so a set can be rebuilt. Legends are written beside the pictures.
import sys, os, zlib, struct, time
import numpy as np

OUT = sys.argv[1]
N = int(sys.argv[2])
KINDS = sys.argv[3:]
os.makedirs(OUT, exist_ok=True)
BAND = 128


def chunk(tag, data):
    c = struct.pack('>I', len(data)) + tag + data
    return c + struct.pack('>I', zlib.crc32(tag + data) & 0xFFFFFFFF)


def write_png(path, n, color_type, depth, band_fn, level=3):
    """band_fn(y0, y1) -> uint8/uint16 array (rows, n[, channels]); rows from the NORTH as in a file (y0 = top)."""
    channels = {0: 1, 2: 3, 4: 2, 6: 4}[color_type]
    bpp = channels * depth // 8
    z = zlib.compressobj(level)
    t0 = time.time()
    with open(path + '.tmp', 'wb') as f:
        f.write(b'\x89PNG\r\n\x1a\n')
        f.write(chunk(b'IHDR', struct.pack('>IIBBBBB', n, n, depth, color_type, 0, 0, 0)))
        pending = bytearray()
        for y0 in range(0, n, BAND):
            y1 = min(n, y0 + BAND)
            a = band_fn(y0, y1)
            if depth == 16:
                raw = a.astype('>u2').view(np.uint8).reshape(y1 - y0, -1)
            else:
                raw = a.astype(np.uint8).reshape(y1 - y0, -1)
            out = np.empty((y1 - y0, raw.shape[1] + 1), np.uint8)
            out[:, 0] = 1  # Sub
            out[:, 1:1 + bpp] = raw[:, :bpp]
            out[:, 1 + bpp:] = raw[:, bpp:] - raw[:, :-bpp]
            pending += z.compress(out.tobytes())
            if len(pending) > (1 << 20):
                f.write(chunk(b'IDAT', bytes(pending)))
                pending = bytearray()
        pending += z.flush()
        if pending:
            f.write(chunk(b'IDAT', bytes(pending)))
        f.write(chunk(b'IEND', b''))
    os.replace(path + '.tmp', path)
    print(f'  {os.path.basename(path)}: {os.path.getsize(path):,} bytes in {time.time() - t0:.0f} s', flush=True)


class Noise:
    """Value noise on a few octaves, evaluated band by band over the whole map (cells in pixels)."""

    def __init__(self, seed, cells):
        rng = np.random.default_rng(seed)
        self.cells = cells
        self.grids = [rng.random((N // c + 3, N // c + 3), dtype=np.float32) for c in cells]
        xs = np.arange(N, dtype=np.float32)
        self.fx = []
        for c in cells:
            f = xs / c
            i = np.floor(f).astype(np.int32)
            t = f - i
            self.fx.append((i, (t * t * (3 - 2 * t)).astype(np.float32)))

    def band(self, y0, y1, weights):
        ys = np.arange(y0, y1, dtype=np.float32)
        acc = np.zeros((y1 - y0, N), np.float32)
        tot = 0.0
        for g, c, (ix, tx), w in zip(self.grids, self.cells, self.fx, weights):
            f = ys / c
            iy = np.floor(f).astype(np.int32)
            ty = f - iy
            ty = (ty * ty * (3 - 2 * ty)).astype(np.float32)[:, None]
            r0 = g[iy]
            r1 = g[iy + 1]
            a = r0[:, ix] * (1 - tx) + r0[:, ix + 1] * tx
            b = r1[:, ix] * (1 - tx) + r1[:, ix + 1] * tx
            acc += w * (a * (1 - ty) + b * ty)
            tot += w
        return acc / tot


def flip_north(a):
    """Maps are written from the north; the bands here run from y = 0 (the top row of the file)."""
    return a


CELLS_ALL = [max(2, N // (4 << i)) for i in range(11)]
CELLS_SMOOTH = CELLS_ALL[:7]


def height_band(noise, y0, y1):
    n = noise.band(y0, y1, [1.0, 0.7, 0.45, 0.3, 0.2, 0.12, 0.07, 0.04, 0.02, 0.01, 0.005])
    land = np.clip((n - 0.47) * 5.0, 0, 1)  # about half the map is sea, a constant
    ys = np.arange(y0, y1, dtype=np.float32)[:, None]
    xs = np.arange(N, dtype=np.float32)[None, :]
    # an island rim: the map's edge is sea
    edge = np.minimum(np.minimum(ys, N - 1 - ys), np.minimum(xs, N - 1 - xs)) / (N * 0.12)
    land *= np.clip(edge, 0, 1)
    v = 14000 + land * 36000
    v = np.where(land > 0, v, 14000)  # sea at one value
    return v.astype(np.uint16)


def smooth16(noise, y0, y1, lo=0.0, hi=65535.0, weights=None, step=1):
    """step 257 = an 8-bit map saved as 16-bit (256 levels), which compresses like the real ones do."""
    n = noise.band(y0, y1, weights or [1.0, 0.8, 0.6, 0.5, 0.4, 0.3, 0.2])
    v = lo + np.clip((n - 0.3) / 0.4, 0, 1) * (hi - lo)
    if step > 1:
        v = np.round(v / step) * step
    return v.astype(np.uint16)


def regions(noise, y0, y1, k):
    """k region classes from a smooth field (blocky, no blur between them)."""
    n = noise.band(y0, y1, [1.0, 0.6, 0.35, 0.2, 0.1])
    return np.minimum((np.clip((n - 0.25) / 0.5, 0, 0.9999) * k).astype(np.int32), k - 1)


BIOME_COLOURS = np.array([
    [0, 255, 0], [0, 127, 0], [127, 127, 0], [255, 255, 255], [255, 255, 0], [127, 127, 127], [255, 0, 0], [0, 255, 255], [0, 0, 255],
], np.uint8)
BIOME_LEGEND = "None: 000000|Meadows: 00FF00|BlackForest: 007F00|Swamp: 7F7F00|Mountain: FFFFFF|Plains: FFFF00|Mistlands: 7F7F7F|AshLands: FF0000|DeepNorth: 00FFFF|Ocean: 0000FF"


def blobs(n, count, rmin, rmax, seed):
    rng = np.random.default_rng(seed)
    cx = rng.integers(rmax, n - rmax, count)
    cy = rng.integers(rmax, n - rmax, count)
    r = rng.integers(rmin, rmax, count)
    k = rng.integers(0, 10 ** 6, count)
    return cx, cy, r, k


def main():
    for kind in KINDS:
        print(kind, flush=True)
        if kind == 'height':
            noise = Noise(11, CELLS_ALL)
            write_png(os.path.join(OUT, 'heightmap.png'), N, 0, 16, lambda a, b: height_band(noise, a, b))
        elif kind in ('forest', 'rough', 'heat', 'lava', 'moss'):
            seeds = {'forest': 21, 'rough': 22, 'heat': 23, 'lava': 24, 'moss': 25}
            noise = Noise(seeds[kind], CELLS_SMOOTH if kind != 'rough' else CELLS_ALL[:4])
            if kind == 'rough':
                fn = lambda a, b: smooth16(noise, a, b, 20000, 30000, [1.0, 0.7, 0.4, 0.2])
            else:
                fn = lambda a, b: smooth16(noise, a, b, 0, 65535, None, 257)
            write_png(os.path.join(OUT, kind + 'map.png'), N, 0, 16, fn)
        elif kind == 'biome':
            noise = Noise(31, CELLS_SMOOTH[:6])

            def band(a, b):
                c = regions(noise, a, b, 9)
                return BIOME_COLOURS[c]
            write_png(os.path.join(OUT, 'biomemap.png'), N, 2, 8, band)
            open(os.path.join(OUT, 'biomemap.txt'), 'w').write(BIOME_LEGEND.replace('|', '\n') + '\n')
        elif kind == 'location':
            # black, with a few hundred small coloured blobs (the default legend's colours)
            cols = [(255, 0, 0), (255, 153, 0), (0, 255, 0), (255, 255, 0), (0, 255, 255), (74, 134, 232), (0, 0, 255), (230, 184, 175), (201, 218, 248), (255, 242, 204)]
            cx, cy, r, k = blobs(N, 400, 3, 40, 41)

            def band(a, b):
                img = np.zeros((b - a, N, 3), np.uint8)
                for i in range(len(cx)):
                    if cy[i] + r[i] < a or cy[i] - r[i] >= b:
                        continue
                    ys = np.arange(max(a, cy[i] - r[i]), min(b, cy[i] + r[i] + 1))[:, None]
                    xs = np.arange(cx[i] - r[i], cx[i] + r[i] + 1)[None, :]
                    m = (ys - cy[i]) ** 2 + (xs - cx[i]) ** 2 <= r[i] ** 2
                    sub = img[ys[:, 0] - a][:, cx[i] - r[i]:cx[i] + r[i] + 1]
                    sub[m] = cols[k[i] % len(cols)]
                    img[ys[:, 0] - a, cx[i] - r[i]:cx[i] + r[i] + 1] = sub
                return img
            write_png(os.path.join(OUT, 'locationmap.png'), N, 2, 8, band)
        elif kind in ('spawn', 'vegetation'):
            noise = Noise(51 if kind == 'spawn' else 52, CELLS_SMOOTH[:6])
            cols = np.array([[255, 255, 255, 255], [255, 0, 0, 255], [0, 255, 0, 255], [0, 0, 255, 255], [0, 0, 0, 255]], np.uint8)

            def band(a, b):
                c = regions(noise, a, b, 7)
                c = np.where(c >= 4, 4, c)  # most of the map is black: nothing
                return cols[c]
            write_png(os.path.join(OUT, kind + 'map.png'), N, 6, 8, band)
            open(os.path.join(OUT, kind + 'map.txt'), 'w').write('255,0,0,255: -Boar\n0,255,0,255: +Neck\n0,0,255,255: Boar,Deer\n')
        elif kind in ('paint', 'terrain'):
            noise = Noise(61 if kind == 'paint' else 62, CELLS_SMOOTH[:6])
            cols = np.array([[0, 0, 0, 0], [255, 0, 0, 255], [0, 255, 0, 255], [0, 0, 255, 255], [255, 255, 0, 255], [200, 100, 50, 255]], np.uint8)

            def band(a, b):
                c = regions(noise, a, b, 6)
                return cols[c]
            write_png(os.path.join(OUT, kind + 'map.png'), N, 6, 8, band)
            if kind == 'paint':
                open(os.path.join(OUT, 'paintmap.txt'), 'w').write('FF0000: FF0000FF\n00FF00: 00FF00FF\n0000FF: 0000FFFF\nFFFF00: FFFF00FF\nC86432: C86432FF\n')
            else:
                open(os.path.join(OUT, 'terrainmap.txt'), 'w').write('Meadows: FF0000\nMountain: 00FF00\nSwamp: 0000FF\nPlains: FFFF00\nBlackForest: C86432\n')
        elif kind == 'paintnoisy':
            # worst case for a decoded colour grid: every tile has a few odd pixels, so none is uniform
            noise = Noise(63, CELLS_SMOOTH[:6])
            rng = np.random.default_rng(64)
            cols = np.array([[0, 0, 0, 0], [255, 0, 0, 255], [0, 255, 0, 255], [0, 0, 255, 255], [255, 255, 0, 255], [200, 100, 50, 255]], np.uint8)

            def band(a, b):
                c = regions(noise, a, b, 6)
                flip = rng.random((b - a, N)) < 0.02
                c = np.where(flip, rng.integers(0, 6, (b - a, N)), c)
                return cols[c]
            write_png(os.path.join(OUT, 'paintnoisy.png'), N, 6, 8, band)
            open(os.path.join(OUT, 'paintnoisy.txt'), 'w').write('FF0000: FF0000FF\n00FF00: 00FF00FF\n0000FF: 0000FFFF\nFFFF00: FFFF00FF\nC86432: C86432FF\n')
        elif kind == 'altbiome':
            noise = Noise(71, CELLS_SMOOTH[:6])
            cols = np.array([[0, 0, 0, 255], [45, 70, 19, 255], [229, 197, 75, 255], [194, 24, 91, 255], [110, 192, 61, 255]], np.uint8)

            def band(a, b):
                c = regions(noise, a, b, 9)
                c = np.where(c >= 5, 0, c)
                return cols[c]
            write_png(os.path.join(OUT, 'altbiomemap.png'), N, 6, 8, band)
        else:
            print('unknown kind', kind)


main()
