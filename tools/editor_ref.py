#!/usr/bin/env python3
# Added by Wubarrk on 2026-10-26 for the editor pack (0.10.7).
"""The editor pack's independent reference: reads and writes the pack's files with the standard library only (struct, zlib), so that
the C# writers and readers are checked against code that shares nothing with them. Run it as `python3 -I editor_ref.py ...`.

    editor_ref.py tiff FILE            parse a classic or Big TIFF: tags, strips and pixels, as JSON
    editor_ref.py r32 FILE             size and pixels of a headerless 32-bit float file, as JSON
    editor_ref.py png FILE             parse a grey PNG (8 or 16 bit): size, depth, text chunks, pixels, as JSON
    editor_ref.py slope TIF PNG --amount A --sla S --pitch P
                                       the slope mask the pack should hold for that TIFF, against the PNG: worst difference in steps
    editor_ref.py mktiff OUT RAW SIZE [--compression none|deflate] [--predictor 1|3] [--tiles T] [--rows-per-strip R]
                                       [--big] [--be] [--bits 32|16|8]
                                       a TIFF of a SIZE x SIZE picture from a raw file of little-endian float32 values
                                       (16 and 8 bit: the values times 65535 / 255, rounded)

JSON goes to stdout; a failing check exits with 1.
"""
import json
import struct
import sys
import zlib


def die(msg):
    sys.stderr.write(msg + "\n")
    sys.exit(1)


# ---- TIFF reading --------------------------------------------------------------------------------------------------------

TYPE_SIZE = {1: 1, 2: 1, 3: 2, 4: 4, 5: 8, 6: 1, 7: 1, 8: 2, 9: 4, 10: 8, 11: 4, 12: 8, 16: 8, 17: 8, 18: 8}
TYPE_FMT = {1: "B", 2: "B", 3: "H", 4: "I", 6: "b", 7: "B", 8: "h", 9: "i", 16: "Q", 17: "q", 18: "Q"}


def read_tiff(path):
    data = open(path, "rb").read()
    order = {b"II": "<", b"MM": ">"}.get(data[:2])
    if order is None:
        die("not a TIFF")
    magic = struct.unpack(order + "H", data[2:4])[0]
    big = magic == 43
    if magic not in (42, 43):
        die("bad magic")
    if big:
        ifd = struct.unpack(order + "Q", data[8:16])[0]
        count = struct.unpack(order + "Q", data[ifd:ifd + 8])[0]
        entry_size, at, vsize, cfmt = 20, ifd + 8, 8, "Q"
    else:
        ifd = struct.unpack(order + "I", data[4:8])[0]
        count = struct.unpack(order + "H", data[ifd:ifd + 2])[0]
        entry_size, at, vsize, cfmt = 12, ifd + 2, 4, "I"
    tags = {}
    entries = []
    for i in range(count):
        e = data[at + i * entry_size: at + (i + 1) * entry_size]
        tag, typ = struct.unpack(order + "HH", e[:4])
        n = struct.unpack(order + cfmt, e[4:4 + vsize])[0]
        raw = e[4 + vsize:4 + 2 * vsize]
        size = TYPE_SIZE[typ] * n
        if size > vsize:
            off = struct.unpack(order + cfmt, raw)[0]
            raw = data[off:off + size]
        else:
            raw = raw[:size]
        if typ == 2:
            val = raw.rstrip(b"\0").decode("latin-1")
        elif typ in (5, 10):
            val = list(struct.unpack(order + ("II" if typ == 5 else "ii") * n, raw))
        else:
            val = list(struct.unpack(order + TYPE_FMT[typ] * n, raw))
        tags[tag] = val
        entries.append({"tag": tag, "type": typ, "count": n, "value": val if not isinstance(val, list) or len(val) <= 4 else "[%d values]" % len(val)})
    return data, order, tags, entries, big, ifd


def tiff_command(path):
    data, order, tags, entries, big, ifd = read_tiff(path)
    w, h = tags[256][0], tags[257][0]
    bits = tags[258][0]
    spp = tags.get(277, [1])[0]
    comp = tags.get(259, [1])[0]
    fmt = tags.get(339, [1])[0]
    rows = tags.get(278, [h])[0]
    offsets, counts = tags[273], tags[279]
    pixels = b""
    for o, c in zip(offsets, counts):
        pixels += data[o:o + c]
    if comp != 1:
        die("compression %d is not read by the reference" % comp)
    if len(pixels) != w * h * spp * bits // 8:
        die("strip bytes %d != %d" % (len(pixels), w * h * spp * bits // 8))
    asc = [e["tag"] for e in entries]
    out = {
        "order": order, "big": big, "ifd": ifd, "width": w, "height": h, "bits": bits, "samples": spp, "compression": comp, "sampleformat": fmt,
        "photometric": tags.get(262, [None])[0], "orientation": tags.get(274, [None])[0], "rowsPerStrip": rows, "planar": tags.get(284, [None])[0],
        "description": tags.get(270), "software": tags.get(305), "entries": len(entries), "tagsAscending": asc == sorted(asc), "tags": asc,
        "stripOffsets": offsets, "stripCounts": counts, "fileLength": len(data), "firstStrip": min(offsets),
        "stripsContiguous": all(offsets[i] + counts[i] == offsets[i + 1] for i in range(len(offsets) - 1)),
        "stripsEnd": offsets[-1] + counts[-1], "pixels": pixels.hex(),
    }
    print(json.dumps(out))


def r32_command(path):
    data = open(path, "rb").read()
    n = len(data) // 4
    side = int(round(n ** 0.5))
    print(json.dumps({"length": len(data), "side": side, "square": side * side == n and len(data) % 4 == 0, "pixels": data.hex()}))


# ---- PNG reading ---------------------------------------------------------------------------------------------------------------

def read_png(path):
    data = open(path, "rb").read()
    if data[:8] != b"\x89PNG\r\n\x1a\n":
        die("not a PNG")
    at = 8
    idat = b""
    text = []
    w = h = depth = ctype = None
    ended = False
    while at < len(data):
        length = struct.unpack(">I", data[at:at + 4])[0]
        typ = data[at + 4:at + 8]
        body = data[at + 8:at + 8 + length]
        crc = struct.unpack(">I", data[at + 8 + length:at + 12 + length])[0]
        if zlib.crc32(typ + body) & 0xFFFFFFFF != crc:
            die("bad crc in %r" % typ)
        if typ == b"IHDR":
            w, h, depth, ctype = struct.unpack(">IIBB", body[:10])
        elif typ == b"IDAT":
            idat += body
        elif typ == b"tEXt":
            k, _, v = body.partition(b"\0")
            text.append((k.decode("latin-1"), v.decode("latin-1")))
        elif typ == b"IEND":
            ended = True
        at += 12 + length
    if not ended or ctype != 0 or depth not in (8, 16):
        die("only grey 8/16-bit PNGs with IEND are read (ctype %s depth %s)" % (ctype, depth))
    raw = zlib.decompress(idat)
    bpp = depth // 8
    stride = w * bpp
    if len(raw) != h * (stride + 1):
        die("pixel data is %d bytes, expected %d" % (len(raw), h * (stride + 1)))
    rows = []
    prev = bytearray(stride)
    pos = 0
    for _ in range(h):
        f = raw[pos]
        cur = bytearray(raw[pos + 1:pos + 1 + stride])
        pos += 1 + stride
        for i in range(stride):
            a = cur[i - bpp] if i >= bpp else 0
            b = prev[i]
            c = prev[i - bpp] if i >= bpp else 0
            if f == 1:
                cur[i] = (cur[i] + a) & 255
            elif f == 2:
                cur[i] = (cur[i] + b) & 255
            elif f == 3:
                cur[i] = (cur[i] + ((a + b) >> 1)) & 255
            elif f == 4:
                p = a + b - c
                pa, pb, pc = abs(p - a), abs(p - b), abs(p - c)
                pr = a if pa <= pb and pa <= pc else (b if pb <= pc else c)
                cur[i] = (cur[i] + pr) & 255
            elif f != 0:
                die("bad filter")
        rows.append(bytes(cur))
        prev = cur
    flat = b"".join(rows)
    values = list(struct.unpack(">%d%s" % (w * h, "H" if depth == 16 else "B"), flat))
    return w, h, depth, text, values


def png_command(path):
    w, h, depth, text, values = read_png(path)
    print(json.dumps({"width": w, "height": h, "depth": depth, "text": text, "pixels": values}))


# ---- the slope mask -----------------------------------------------------------------------------------------------------------

def slope_command(tif, png, amount, sla, pitch):
    import math
    data, order, tags, entries, big, ifd = read_tiff(tif)
    w, h = tags[256][0], tags[257][0]
    pixels = b"".join(data[o:o + c] for o, c in zip(tags[273], tags[279]))
    v = struct.unpack(order + "%df" % (w * h), pixels)
    base = struct.unpack("<f", struct.pack("<f", 0.15))[0]    # the float 0.15f the game's formula holds

    def metres(x):
        return (x * amount - base + sla) * 200.0

    m = [metres(x) for x in v]
    pw, ph, depth, _, got = read_png(png)
    if (pw, ph, depth) != (w, h, 16):
        die("the mask is %dx%d %d-bit" % (pw, ph, depth))
    worst = 0
    bad = 0
    for r in range(h):
        for c in range(w):
            here = m[r * w + c]
            if c == 0:
                gx = (m[r * w + 1] - here) / pitch
            elif c == w - 1:
                gx = (here - m[r * w + c - 1]) / pitch
            else:
                gx = (m[r * w + c + 1] - m[r * w + c - 1]) / (2.0 * pitch)
            if r == 0:
                gz = (m[w + c] - here) / pitch
            elif r == h - 1:
                gz = (here - m[(r - 1) * w + c]) / pitch
            else:
                gz = (m[(r + 1) * w + c] - m[(r - 1) * w + c]) / (2.0 * pitch)
            deg = math.degrees(math.atan(math.hypot(gx, gz)))
            want = int(deg / 90.0 * 65535.0 + 0.5)
            d = abs(want - got[r * w + c])
            worst = max(worst, d)
            if d > 1:
                bad += 1
    print(json.dumps({"worst": worst, "bad": bad, "max": max(got)}))


# ---- writing TIFFs of every layout the reader must take ---------------------------------------------------------------------

def predict_float_rows(rowbytes_list, order):
    """TIFF Technical Note 3 on rows of float words given as raw bytes in the file's byte order."""
    out = []
    for row in rowbytes_list:
        n = len(row) // 4
        words = [row[4 * i:4 * i + 4] for i in range(n)]
        # most significant byte first: in a big-endian word that is byte 0, in a little-endian one byte 3
        if order == "<":
            words = [w[::-1] for w in words]
        planes = bytearray(len(row))
        for i, w in enumerate(words):
            for b in range(4):
                planes[b * n + i] = w[b]
        enc = bytearray(planes)
        for i in range(len(enc) - 1, 0, -1):
            enc[i] = (enc[i] - enc[i - 1]) & 255
        out.append(bytes(enc))
    return out


def mktiff(args):
    out, raw, size = args[0], args[1], int(args[2])
    opt = {"--compression": "none", "--predictor": "1", "--tiles": "0", "--rows-per-strip": "0", "--bits": "32"}
    flags = set()
    i = 3
    while i < len(args):
        if args[i] in ("--big", "--be"):
            flags.add(args[i])
            i += 1
        else:
            opt[args[i]] = args[i + 1]
            i += 2
    order = ">" if "--be" in flags else "<"
    big = "--big" in flags
    bits = int(opt["--bits"])
    pred = int(opt["--predictor"])
    deflate = opt["--compression"] == "deflate"
    values = struct.unpack("<%df" % (size * size), open(raw, "rb").read())
    if bits == 32:
        rows = [struct.pack(order + "%df" % size, *values[r * size:(r + 1) * size]) for r in range(size)]
    elif bits == 16:
        rows = [struct.pack(order + "%dH" % size, *[max(0, min(65535, int(v * 65535 + 0.5))) for v in values[r * size:(r + 1) * size]]) for r in range(size)]
    else:
        rows = [bytes(max(0, min(255, int(v * 255 + 0.5))) for v in values[r * size:(r + 1) * size]) for r in range(size)]
    tile = int(opt["--tiles"])
    rps = int(opt["--rows-per-strip"]) or size
    blocks = []
    if tile:
        bw = bits // 8
        per = (size + tile - 1) // tile
        for tr in range(per):
            for tc in range(per):
                trows = []
                for r in range(tr * tile, tr * tile + tile):
                    src = rows[r] if r < size else bytes(size * bw)
                    seg = src[tc * tile * bw:(tc + 1) * tile * bw]
                    trows.append(seg + bytes(tile * bw - len(seg)))
                blocks.append(trows)
    else:
        for s in range(0, size, rps):
            blocks.append(rows[s:s + rps])
    packed = []
    for rs in blocks:
        if pred == 3:
            rs = predict_float_rows(rs, order)
        elif pred == 2:
            enc = []
            for row in rs:
                r2 = bytearray(row)
                if bits == 8:
                    for k in range(len(r2) - 1, 0, -1):
                        r2[k] = (r2[k] - r2[k - 1]) & 255
                else:
                    w = [struct.unpack(order + "H", row[2 * k:2 * k + 2])[0] for k in range(len(row) // 2)]
                    w2 = [w[0]] + [(w[k] - w[k - 1]) & 0xFFFF for k in range(1, len(w))]
                    r2 = bytearray(b"".join(struct.pack(order + "H", x) for x in w2))
                enc.append(bytes(r2))
            rs = enc
        body = b"".join(rs)
        packed.append(zlib.compress(body) if deflate else body)

    entries = []   # (tag, type, count, values)
    entries.append((256, 4, 1, [size]))
    entries.append((257, 4, 1, [size]))
    entries.append((258, 3, 1, [bits]))
    entries.append((259, 3, 1, [8 if deflate else 1]))
    entries.append((262, 3, 1, [1]))
    if tile:
        entries.append((322, 4, 1, [tile]))
        entries.append((323, 4, 1, [tile]))
    else:
        entries.append((273, 4, len(packed), [0] * len(packed)))      # filled below
        entries.append((278, 4, 1, [rps]))
        entries.append((279, 4, len(packed), [len(p) for p in packed]))
    if tile:
        entries.append((324, 16 if big else 4, len(packed), [0] * len(packed)))
        entries.append((325, 16 if big else 4, len(packed), [len(p) for p in packed]))
    entries.append((277, 3, 1, [1]))
    entries.append((284, 3, 1, [1]))
    if pred != 1:
        entries.append((317, 3, 1, [pred]))
    entries.append((339, 3, 1, [3 if bits == 32 else 1]))
    entries.sort(key=lambda e: e[0])

    vsize = 8 if big else 4
    esize = 20 if big else 12
    head = 16 if big else 8
    ifd_len = (8 if big else 2) + len(entries) * esize + vsize
    fmt = {3: "H", 4: "I", 16: "Q"}
    extra_at = head + ifd_len
    # lay out the arrays that do not fit an entry, then the blocks
    layout = []
    pos = extra_at
    for e in entries:
        tag, typ, n, vals = e
        if n * {3: 2, 4: 4, 16: 8}[typ] > vsize:
            layout.append(pos)
            pos += n * {3: 2, 4: 4, 16: 8}[typ]
        else:
            layout.append(None)
    pos = (pos + 3) & ~3
    offsets = []
    for p in packed:
        offsets.append(pos)
        pos += len(p)
    final = []
    for e in entries:
        tag, typ, n, vals = e
        if tag in (273, 324):
            vals = offsets
        final.append((tag, typ, n, vals))
    buf = bytearray()
    if big:
        buf += (b"MM" if order == ">" else b"II") + struct.pack(order + "HHHQ", 43, 8, 0, 16)
        buf += struct.pack(order + "Q", len(final))
    else:
        buf += (b"MM" if order == ">" else b"II") + struct.pack(order + "HI", 42, 8)
        buf += struct.pack(order + "H", len(final))
    extra = bytearray()
    for (tag, typ, n, vals), where in zip(final, layout):
        cell = struct.pack(order + fmt[typ] * n, *vals)
        if where is None:
            cell = cell.ljust(vsize, b"\0")
            buf += struct.pack(order + "HH", tag, typ) + struct.pack(order + ("Q" if big else "I"), n) + cell
        else:
            buf += struct.pack(order + "HH", tag, typ) + struct.pack(order + ("Q" if big else "I"), n) + struct.pack(order + ("Q" if big else "I"), where)
            extra += cell
    buf += b"\0" * vsize
    buf += extra
    while len(buf) < offsets[0]:
        buf += b"\0"
    for p in packed:
        buf += p
    open(out, "wb").write(bytes(buf))


def main():
    a = sys.argv[1:]
    if not a:
        die(__doc__)
    if a[0] == "tiff":
        tiff_command(a[1])
    elif a[0] == "r32":
        r32_command(a[1])
    elif a[0] == "png":
        png_command(a[1])
    elif a[0] == "slope":
        opts = dict(zip(a[3::2], a[4::2]))
        slope_command(a[1], a[2], float(opts["--amount"]), float(opts["--sla"]), float(opts["--pitch"]))
    elif a[0] == "mktiff":
        mktiff(a[1:])
    else:
        die("unknown command " + a[0])


if __name__ == "__main__":
    main()
