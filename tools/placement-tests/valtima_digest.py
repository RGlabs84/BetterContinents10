#!/usr/bin/env python3
# Added by Wubarrk on 2026-10-07 for baked placements (0.10.4).
#
# Writes fixtures/valtima-records.tsv: for every zone with records of VALtima's placements.bcp, one line "zx zz count sha256", where the digest
# is over one text line per record, in the block's order:
#   "<palette> <x> <z> <y> <yaw> <sx> <sy> <sz> <id>\n"
# with x, z the world position (zone corner + u16 / 1024), y metres (mm / 1000), yaw degrees, the scales (x1000 -> 1 when the record has none)
# and the id (0 when none), formatted %.6f, %.6f, %.6f, %.6f, %.3f x 3 and %d. Tests.Valtima.cs computes the same text from BC's reader
# and compares: two readers written apart (this one follows the format as the spec lays it out, as ~/valheim-testbed/bc-0104/measure/bcpread.py
# does) agree on every record's place, turn, scale and id.
#
# Usage: valtima_digest.py <placements.bcp> [out.tsv]      (run with: ulimit -v 4000000; nice -n 10 python3 ...)
import hashlib, struct, sys, zlib


def main():
    path = sys.argv[1]
    out = sys.argv[2] if len(sys.argv) > 2 else 'fixtures/valtima-records.tsv'
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

    producer = s()
    for _ in range(npal):
        nc = data[o]
        o += 1
        for _ in range(nc):
            s()
            o += 6
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
    rows = []
    for _ in range(nzones):
        rows.append(struct.unpack_from('<hhIIIHBBff', data, o))
        o += 28
    lines = ['# valtima-records.tsv: zx zz records sha256 of the records\' text (see valtima_digest.py); placements.bcp md5 %s, %d zones, %d records' % (md5, nzones, nplace)]
    total = 0
    for zx, zz, off, ln, cnt, live, zf, pad, ymin, ymax in rows:
        if cnt == 0:
            continue
        raw = zlib.decompressobj(-15).decompress(data[off:off + ln])
        q = 0
        n = struct.unpack_from('<I', raw, q)[0]
        q += 4
        assert n == cnt
        pal = struct.unpack_from('<%dH' % n, raw, q); q += 2 * n
        fl = raw[q:q + n]; q += n
        xs = struct.unpack_from('<%dH' % n, raw, q); q += 2 * n
        zs = struct.unpack_from('<%dH' % n, raw, q); q += 2 * n
        ys = struct.unpack_from('<%di' % n, raw, q); q += 4 * n
        yaws = struct.unpack_from('<%dH' % n, raw, q); q += 2 * n
        assert not any(f & 0x19 for f in fl), 'a rotation, source or seed run: not in VALtima\'s file'
        scales = {}
        for k in range(n):
            if fl[k] & 2:
                scales[k] = struct.unpack_from('<3H', raw, q)
                q += 6
        ids = {}
        for k in range(n):
            if fl[k] & 4:
                ids[k] = struct.unpack_from('<I', raw, q)[0]
                q += 4
        text = []
        for k in range(n):
            sc = scales.get(k, (1000, 1000, 1000))
            text.append('%d %.6f %.6f %.6f %.6f %.3f %.3f %.3f %d\n' % (
                pal[k], zx * 64 - 32 + xs[k] / 1024.0, zz * 64 - 32 + zs[k] / 1024.0, ys[k] / 1000.0, yaws[k] * 360.0 / 65536.0,
                sc[0] / 1000.0, sc[1] / 1000.0, sc[2] / 1000.0, ids.get(k, 0)))
        total += n
        lines.append('%d %d %d %s' % (zx, zz, n, hashlib.sha256(''.join(text).encode('ascii')).hexdigest()))
    assert total == nplace
    open(out, 'w').write('\n'.join(lines) + '\n')
    print('wrote', out, len(lines) - 1, 'zones,', total, 'records; producer', repr(producer))


main()
