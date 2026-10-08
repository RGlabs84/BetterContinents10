#!/usr/bin/env python3
# Added by Wubarrk on 2026-10-07 for baked placements (0.10.4).
#
# The look of a record under instancing, transcribed from section 2.5 of the build spec (~/valheim-testbed/bc-0104/BUILD-SPEC-0104-BAKE.md), not
# from BakedFormat.cs. The spec's words, which this follows:
#   h  = FNV-1a 32 over the record's decoded anchor point in world mm, computed in double precision, as three little-endian i32
#        (round(x * 1000), round(y * 1000), round(z * 1000)).
#   u  = ((h xor (i * 0x9E3779B9)) mod 65,536) / 65,536, in u32 arithmetic (i is the MaterialVariation slot).
#   the seed of a record = h mod 12,345 (when it has none of its own); a seed draws as bucket seed mod 8.
# and, from 2.1: a record's x is (world x - (zx * 64 - 32)) * 1024 rounded, so the decoded point is zone corner + u16 / 1024; y is mm.
# `round` is Python's: halves go to the even number, which is what .NET's Math.Round does too. The spec does not say it in words; the
# vectors below include points whose x * 1000 is exactly a half, so the choice is pinned (BakedFormat.cs must give the same).
#
# Writes two files for Tests.Look.cs (placement-tests), next to the other fixtures:
#   fixtures/look-vectors.tsv    "rec zx zz X Z Y hash seed bucket m0 m1 m2 m3" (a record's columns, then what it derives) and
#                                "pt x y z hash" (three doubles, as repr() writes them)
#   fixtures/look-buckets.tsv    from VALtima's file: per kind of the Piece_grausten_* and Ice_floor family and for every record, the count in
#                                each of the 8 buckets, the sha256 of the seeds (one line a record, in the file's order) and of the variant
#                                words m0 m1 m2 (slots 0 to 2, the integer ((h xor slot * 0x9E3779B9) mod 65536), one line a record)
#
# Usage: look_digest.py <placements.bcp> [vectors.tsv] [buckets.tsv]      (run through ~/valheim-testbed/heavy.sh 4G)
import hashlib, struct, sys, zlib

FNV_BASIS = 2166136261
FNV_PRIME = 16777619
GOLDEN = 0x9E3779B9


def fnv1a32(values):
    h = FNV_BASIS
    for v in values:
        for byte in struct.pack('<i', v):
            h ^= byte
            h = (h * FNV_PRIME) & 0xFFFFFFFF
    return h


def look_hash(x, y, z):
    return fnv1a32([round(x * 1000), round(y * 1000), round(z * 1000)])


def seed_of(h):
    return h % 12345


def word(h, slot):
    return (h ^ ((slot * GOLDEN) & 0xFFFFFFFF)) % 65536


def decode_point(zx, zz, X, Z, Y):
    # 2.1: the zone's south-west corner is (zone * 64 - 32); x and z are u16 of 1/1024 m from it; y is an i32 in mm.
    return zx * 64 - 32 + X / 1024.0, Y / 1000.0, zz * 64 - 32 + Z / 1024.0


class Lcg:
    def __init__(self, seed):
        self.s = (seed * 6364136223846793005 + 1442695040888963407) & 0xFFFFFFFFFFFFFFFF

    def next(self):
        self.s = (self.s * 6364136223846793005 + 1442695040888963407) & 0xFFFFFFFFFFFFFFFF
        return self.s >> 33

    def below(self, n):
        return self.next() % n


def vectors(out):
    rows = []
    fixed = [
        (0, 0, 0, 0, 0),
        (0, 0, 64, 64, 0),
        (0, 0, 192, 320, 1),
        (1, 0, 64, 192, 30000),
        (0, 0, 65535, 65535, -5000),
        (-1, -1, 1, 1, 1),
        (-1024, -1024, 0, 0, -2000000000),
        (1023, 1023, 65535, 65535, 2000000000),
        (-1024, 1023, 12345, 54321, 123456),
        (1023, -1024, 32768, 32768, 31250),
        (5, -3, 64 + 128 * 3, 64 + 128 * 7, 250),
        (-7, 9, 64 + 128 * 100, 64 + 128 * 200, 77000),
    ]
    rng = Lcg(20261007)
    for _ in range(300):
        zx = rng.below(2048) - 1024
        zz = rng.below(2048) - 1024
        fixed.append((zx, zz, rng.below(65536), rng.below(65536), rng.below(400000) - 20000))
    # points whose x * 1000 is exactly a half, in every kind of zone (the corner is a multiple of 32, so X = 64 mod 128 does it)
    for j in range(8):
        fixed.append((j - 4, 3 - j, 64 + 128 * j, 64 + 128 * (7 - j), 1000 * j))
    for zx, zz, X, Z, Y in fixed:
        x, y, z = decode_point(zx, zz, X, Z, Y)
        h = look_hash(x, y, z)
        s = seed_of(h)
        rows.append('rec %d %d %d %d %d %08x %d %d %d %d %d %d' % (zx, zz, X, Z, Y, h, s, s % 8, word(h, 0), word(h, 1), word(h, 2), word(h, 3)))
    # direct doubles, as BakedFormat.LookHash takes them
    for x, y, z in [(0.0, 0.0, 0.0), (1.0, 2.0, 3.0), (-0.0005, 0.0005, 0.0015), (0.0025, 0.0035, -0.0025), (12.5, 30.0, 7.25), (-1234.5678, 987.654, -43.21),
                    (1e5, -1e3, 65536.0009765625), (-65535.9990234375, 0.4999, 0.5001), (2.5e-4, 7.5e-4, -2.5e-4)]:
        rows.append('pt %r %r %r %08x' % (x, y, z, look_hash(x, y, z)))
    with open(out, 'w') as f:
        f.write('# look-vectors.tsv: made by look_digest.py from section 2.5 of the spec (see its header). rec: zone x z, the record\'s u16 X and Z, its i32 Y in mm,\n'
                '# then the look hash (hex), the derived seed, the seed\'s bucket and the variant words m0..m3. pt: three doubles and their look hash.\n')
        f.write('\n'.join(rows) + '\n')
    print('wrote', out, len(rows), 'vectors')


def read_layer(path):
    data = open(path, 'rb').read()
    md5 = hashlib.md5(data).hexdigest()
    assert zlib.crc32(data[:-4]) & 0xffffffff == struct.unpack_from('<I', data, len(data) - 4)[0], 'CRC'
    magic, fmt, hflags, rev, npal, nzones, nplace, resv = struct.unpack_from('<4sHHIIIQI', data, 0)
    assert magic == b'BCPL' and fmt == 1 and resv == 0
    o = 32

    def s():
        nonlocal o
        n = data[o]
        v = data[o + 1:o + 1 + n].decode('ascii')
        o += 1 + n
        return v

    s()
    names = []
    for _ in range(npal):
        nc = data[o]
        o += 1
        first = None
        for _ in range(nc):
            n = s()
            if first is None:
                first = n
            o += 6
        names.append(first)
        role, coll, layer, flags = data[o:o + 4]
        o += 4
        if coll == 1:
            nb = data[o]
            o += 1 + 12 * nb
        tint = s()
        if tint:
            o += 6
            s()
        nt = data[o]
        o += 1
        for _ in range(nt):
            s()
            t = data[o]
            o += 1
            if t == 0:
                o += 1
            elif t in (1, 2):
                o += 4
            else:
                s()
    assert hflags & 8 == 0, 'a registry: not in VALtima\'s file'
    rows = []
    for _ in range(nzones):
        rows.append(struct.unpack_from('<hhIIIHBBff', data, o))
        o += 28
    return data, md5, names, rows, nplace


def buckets(path, out):
    data, md5, names, rows, nplace = read_layer(path)
    groups = {}

    def group(name):
        if name not in groups:
            groups[name] = {'n': 0, 'b': [0] * 8, 'seeds': hashlib.sha256(), 'words': hashlib.sha256()}
        return groups[name]

    total = 0
    for zx, zz, off, ln, cnt, live, zf, pad, ymin, ymax in rows:
        if cnt == 0:
            continue
        raw = zlib.decompressobj(-15).decompress(data[off:off + ln])
        q = 4
        n = cnt
        pal = struct.unpack_from('<%dH' % n, raw, q); q += 2 * n
        q += n          # record flags
        xs = struct.unpack_from('<%dH' % n, raw, q); q += 2 * n
        zs = struct.unpack_from('<%dH' % n, raw, q); q += 2 * n
        ys = struct.unpack_from('<%di' % n, raw, q); q += 4 * n
        for k in range(n):
            x, y, z = decode_point(zx, zz, xs[k], zs[k], ys[k])
            h = look_hash(x, y, z)
            seed = seed_of(h)
            words = '%d %d %d\n' % (word(h, 0), word(h, 1), word(h, 2))
            name = names[pal[k]]
            kinds = [group('ALL')]
            if name.startswith('Piece_grausten') or name.startswith('Ice_floor'):
                kinds.append(group(name))
            for g in kinds:
                g['n'] += 1
                g['b'][seed % 8] += 1
                g['seeds'].update(('%d\n' % seed).encode('ascii'))
                g['words'].update(words.encode('ascii'))
        total += n
    assert total == nplace
    with open(out, 'w') as f:
        f.write('# look-buckets.tsv: made by look_digest.py from VALtima\'s placements.bcp (md5 %s, %d records). kind, records, the 8 buckets (seed mod 8),\n'
                '# sha256 of the seeds (one line a record, in the file\'s order), sha256 of the variant words of slots 0 to 2.\n' % (md5, nplace))
        for name in sorted(groups, key=lambda k: (k != 'ALL', k)):
            g = groups[name]
            f.write('%s %d %s %s %s\n' % (name, g['n'], ' '.join(str(c) for c in g['b']), g['seeds'].hexdigest(), g['words'].hexdigest()))
    print('wrote', out, len(groups), 'groups')
    for name in sorted(groups, key=lambda k: (k != 'ALL', k)):
        g = groups[name]
        print('  %-34s %7d  %s' % (name, g['n'], g['b']))


def main():
    path = sys.argv[1]
    vout = sys.argv[2] if len(sys.argv) > 2 else 'fixtures/look-vectors.tsv'
    bout = sys.argv[3] if len(sys.argv) > 3 else 'fixtures/look-buckets.tsv'
    vectors(vout)
    buckets(path, bout)


main()
