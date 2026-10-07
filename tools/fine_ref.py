#!/usr/bin/env python3
# Added by Wubarrk on 2026-10-06 for 16k worlds (0.10.3).
"""Fine heights, format 1: the reference for heightmap-fine.png, the file that gives a heightmap 8 more bits.

A heightmap.png holds 16-bit heights, so one step of it is 200 * Heightmap Amount / 65535 metres: 1.5 cm at Amount 5,
25 cm at Amount 81. Ground that climbs by less than a step per pixel climbs in terraces of that height. A
heightmap-fine.png beside it (same folder, '-fine' before the extension, same size, 8-bit grey) holds a signed offset
in 1/256 of a step for every pixel. heightmap.png itself is exactly what it was, so a world made without the fine file,
or by a Better Continents that does not read one, has the same heights as before.

    c       the heightmap's value, 0..65535 (16-bit grey, or the grey of 16-bit grey and alpha)
    f       the fine file's byte read as a signed byte: 0..127 are 0..127, 128..255 are -128..-1
    N       256 * c + f, the height in 1/256 steps
    v       N / 16776960 (= 65535 * 256); f = 0 reads c / 65535, as a heightmap alone always has
    metres  (v * Heightmap Amount - 0.15 + Sea Level Adjustment) * 200, with the water at 30 m: the heightmap's own
            height, as a world export writes it

The writer: x = v * 65535 for the float height v (0..1); c = floor(x + 0.5), exactly what a plain 16-bit writer makes;
f = round((x - c) * 256) (to even), kept in -128..127; with fewer bits, rounded to a multiple of 2^(8 - bits) instead (4
bits: a multiple of 16, at most 112). A pixel is then within half a unit of x (4 bits: 8 units), or within 1 unit (4
bits: 16) when x - c is in the last 1/512 (4 bits: 1/32) of a step below +1/2, where the top of the range cuts it off.
The policy recommended to writers: 4 bits; f = 0 under water; f = 0 where the ground rises by a step or more to any of
the four neighbouring pixels (a step is less than a pixel away there, and the extra bits would only cost bytes).

The record (optional, recommended): a text chunk in heightmap-fine.png, keyword 'BetterContinents', text
'Fine Format = 1; Fine Bits = 4; Heightmap CRC-32 = 89ABCDEF' (parts in any order, unknown ones ignored), anywhere
between IHDR and IEND. The CRC-32 is zlib's, of the whole heightmap.png file the fine file was made for: when it names
another, the fine file is not used. Fine Format other than 1: not used. Fine Bits is for people.

    fine_ref.py check <heightmap.png | folder> [--amount A] [--sea-level S]     what Better Continents makes of the pair
    fine_ref.py sample <heightmap.png | folder> (--at X,Y ... | --points FILE)  heights as Better Continents samples them
    fine_ref.py write <folder> --from-heightmap PNG --amount A [--bits 4]       a pair at another Heightmap Amount
    fine_ref.py fixture <folder> [--size N] [--amount A] [--bits B] [--alpha]   a small test pair and its expected samples
    fine_ref.py selftest                                                        the format's properties, checked

A 16384 px pair takes about 1.5 GB of memory to check or write. Needs numpy and Pillow.
"""
import argparse
import os
import re
import struct
import sys
import zlib

import numpy as np

SCALE = 65535 * 256              # N = v * SCALE
WATER = 30.0                     # the game's water level, metres
MAX_MAP = 16384                  # the largest map Better Continents reads
TILE = 128                       # the side of Better Continents' map tiles (MapTiles.cs)
KEYWORD = 'BetterContinents'
SIGNATURE = b'\x89PNG\r\n\x1a\n'
COLOURS = {0: 'grey', 2: 'RGB', 3: 'palette', 4: 'grey and alpha', 6: 'RGBA'}
CHANNELS = {0: 1, 2: 3, 3: 1, 4: 2, 6: 4}
BAND = 512                       # rows handled at a time


# ---- the format ------------------------------------------------------------------------------------------------------

def fine_path(heightmap):
    """Where Better Continents looks for a heightmap's fine file: '-fine' before the name's last extension, in the same
    folder; '.png' when the name has none."""
    folder, name = os.path.split(heightmap)
    dot = name.rfind('.')
    if dot < 0 or dot == len(name) - 1:
        stem, ext = (name[:dot] if dot >= 0 else name), '.png'
    else:
        stem, ext = name[:dot], name[dot:]
    return os.path.join(folder, stem + '-fine' + ext)


def encode(v, bits=8):
    """Float heights v (0..1) as heightmap values c (uint16) and fine offsets f (int8). c = floor(x + 0.5) for
    x = v * 65535, what a plain 16-bit writer makes; f = the rest in 1/256 step, rounded (to even) to a multiple of
    2^(8 - bits) and kept in -128 .. 128 - 2^(8 - bits). bits 0: f = 0."""
    x = np.clip(np.asarray(v, np.float64), 0.0, 1.0) * 65535.0
    c = np.floor(x + 0.5)
    if bits <= 0:
        return c.astype(np.uint16), np.zeros(c.shape, np.int8)
    q = 1 << (8 - bits)
    f = np.clip(np.rint((x - c) * (256.0 / q)) * q, -128, 128 - q)
    return c.astype(np.uint16), f.astype(np.int8)


def signed(f):
    """Fine bytes as stored (uint8) or already signed, as int8."""
    return np.asarray(f).astype(np.uint8, copy=False).view(np.int8)


def heights24(c, f=None):
    """N = 256 c + f (int64), the height in 1/256 steps."""
    n = np.asarray(c).astype(np.int64) << 8
    if f is not None:
        n += signed(f)
    return n


def sea_adjustment(sea_level):
    """The Sea Level setting (0..1, 0.5 by default) as the adjustment Better Continents adds: Mathf.Lerp(1, -1, s)."""
    return 1.0 - 2.0 * min(max(sea_level, 0.0), 1.0)


def to_metres(v, amount, sla=0.0):
    return (v * amount - 0.15 + sla) * 200.0


def to_v(metres, amount, sla=0.0):
    return (metres / 200.0 + 0.15 - sla) / amount


def steepest(n):
    """For every pixel the largest |difference of N| to its four neighbours; a neighbour past the edge does not count."""
    n = np.asarray(n, np.int64)
    s = np.zeros(n.shape, np.int64)
    d = np.abs(n[:, 1:] - n[:, :-1])
    np.maximum(s[:, 1:], d, out=s[:, 1:])
    np.maximum(s[:, :-1], d, out=s[:, :-1])
    d = np.abs(n[1:] - n[:-1])
    np.maximum(s[1:], d, out=s[1:])
    np.maximum(s[:-1], d, out=s[:-1])
    return s


def apply_policy(v, f, amount=None, sla=0.0, steep=True):
    """The recommended policy, in place on f made from heights v (same shape): f = 0 where the true heights rise by a
    step (256 units) or more to a neighbour, and f = 0 under water (when the Amount is known). Returns f."""
    if steep:
        f[steepest(np.rint(np.clip(v, 0.0, 1.0) * SCALE)) >= 256] = 0
    if amount is not None:
        f[to_metres(np.asarray(v, np.float64), amount, sla) < WATER] = 0
    return f


def from_provisional(c_old, f_byte):
    """A pair made by the provisional rule (c floored, f 0..255 the low byte of N) in format 1: the fine byte stays the
    same and c gains 1 where the byte is 128 or more."""
    return (np.asarray(c_old, np.int64) + (np.asarray(f_byte) >= 128)).astype(np.uint16), np.asarray(f_byte, np.uint8)


def bits_used(or_of_bytes):
    """How many bits the fine bytes use, from the OR of all of them: 8 less the trailing zero bits; 0 when all are 0."""
    if or_of_bytes == 0:
        return 0
    return 8 - ((or_of_bytes & -or_of_bytes).bit_length() - 1)


def fine_record_text(bits, crc):
    return f'Fine Format = 1; Fine Bits = {bits}; Heightmap CRC-32 = {crc:08X}'


def record_parts(texts):
    """The first BetterContinents text with fine parts, as a dict: 'format' (int), 'bits' (int), 'crc' (int). Parts are
    'key = value' split at ';'; keys must match exactly (spaces around them aside); a value that does not parse is skipped,
    as in the heightmap's own record."""
    for keyword, text in texts:
        if keyword != KEYWORD:
            continue
        parts = {}
        for part in text.split(';'):
            kv = part.split('=')
            if len(kv) != 2:
                continue
            key, value = kv[0].strip(), kv[1].strip()
            if key in ('Fine Format', 'Fine Bits') and re.fullmatch(r'[+-]?[0-9]+', value):
                parts['format' if key == 'Fine Format' else 'bits'] = int(value)
            elif key == 'Heightmap CRC-32' and re.fullmatch(r'[0-9A-Fa-f]+', value) and int(value, 16) <= 0xFFFFFFFF:
                parts['crc'] = int(value, 16)
        if parts:
            return parts
    return None


def heightmap_record(texts):
    """The heightmap's own record (a world export's): (Heightmap Amount, the Sea Level Adjustment SETTING, 0..1), or None.
    Better Continents writes the config value under that key (0.5 at the default), not the adjustment it makes of it
    (sea_adjustment: 0 at 0.5)."""
    for keyword, text in texts:
        if keyword != KEYWORD:
            continue
        amount = sla = None
        for part in text.split(';'):
            kv = part.split('=')
            if len(kv) != 2:
                continue
            try:
                value = float(kv[1].strip())
            except ValueError:
                continue
            if kv[0].strip() == 'Heightmap Amount':
                amount = value
            elif kv[0].strip() == 'Sea Level Adjustment':
                sla = value
        if amount is not None and sla is not None:
            return amount, sla
    return None


def float_text(x):
    """A float as C# reads it back (InvariantCulture) and as short as float32 allows."""
    return str(np.float32(x))


# ---- PNG files -------------------------------------------------------------------------------------------------------

class PngError(Exception):
    pass


class Png:
    """A PNG's header and text chunks, every chunk's CRC checked; the image data kept only when asked."""

    def __init__(self, path, keep_data=False):
        self.path = path
        with open(path, 'rb') as fh:
            raw = fh.read()
        self.file_crc = zlib.crc32(raw) & 0xFFFFFFFF
        self.file_size = len(raw)
        if raw[:8] != SIGNATURE:
            raise PngError('it is not a PNG file')
        at, self.texts, data, self.width = 8, [], [], None
        idats = after = 0
        while True:
            if at + 12 > len(raw):
                raise PngError('it is cut short (no IEND)')
            size, kind = struct.unpack('>I4s', raw[at:at + 8])
            name = kind.decode('latin-1')
            if at + 12 + size > len(raw):
                raise PngError(f'it is cut short (inside its {name} chunk)')
            body = raw[at + 8:at + 8 + size]
            if zlib.crc32(raw[at + 4:at + 8 + size]) & 0xFFFFFFFF != struct.unpack('>I', raw[at + 8 + size:at + 12 + size])[0]:
                raise PngError(f'its {name} chunk at byte {at} is damaged (the CRC does not match)')
            if self.width is None and kind != b'IHDR':
                raise PngError('it does not start with IHDR')
            if kind == b'IHDR':
                if size != 13 or self.width is not None:
                    raise PngError('its IHDR is not valid')
                (self.width, self.height, self.depth, self.colour, compression, filtering,
                 self.interlace) = struct.unpack('>IIBBBBB', body)
                if compression or filtering or self.interlace > 1 or self.colour not in COLOURS:
                    raise PngError('its IHDR is not valid')
            elif kind == b'IDAT':
                idats += 1
                if keep_data:
                    data.append(body)
            elif kind in (b'tEXt', b'zTXt', b'iTXt'):
                text = _text_chunk(kind, body)
                if text:
                    self.texts.append(text)
                    after += idats > 0
            elif kind == b'IEND':
                break
            at += 12 + size
        if not idats:
            raise PngError('it holds no image data')
        self.texts_after_data = after
        self.data = b''.join(data) if keep_data else None

    def describe(self):
        return f'{self.depth}-bit {COLOURS[self.colour]}' + (', interlaced' if self.interlace else '')


def _text_chunk(kind, body):
    """(keyword, text) of a tEXt, zTXt or iTXt chunk; None when it cannot be read."""
    try:
        keyword, rest = body.split(b'\0', 1)
        keyword = keyword.decode('latin-1')
        if kind == b'tEXt':
            return keyword, rest.decode('latin-1')
        if kind == b'zTXt':
            return keyword, zlib.decompress(rest[1:]).decode('latin-1')
        compressed, rest = rest[0], rest[2:]
        _language, rest = rest.split(b'\0', 1)
        _translated, rest = rest.split(b'\0', 1)
        return keyword, (zlib.decompress(rest) if compressed else rest).decode('utf-8')
    except (ValueError, IndexError, zlib.error, UnicodeDecodeError):
        return None


def read_grey(path, png):
    """The grey of a PNG, file row 0 (north) first: uint16 for 16 bits, uint8 for 8. Pillow reads grey; grey and alpha
    is decoded here (not interlaced)."""
    if png.colour == 0 and png.depth in (8, 16):
        try:
            from PIL import Image
            Image.MAX_IMAGE_PIXELS = None
            with Image.open(path) as im:
                im.load()
                a = np.asarray(im)
        except Exception as e:  # Pillow's errors for broken image data are of several types
            raise PngError(f'its image data cannot be read ({e})') from None
        a = a.astype(np.uint16 if png.depth == 16 else np.uint8, copy=False)
        if a.shape != (png.height, png.width):
            raise PngError(f'it read as {a.shape}, not {png.height} x {png.width}')
        return a
    if png.colour == 4 and png.depth in (8, 16) and not png.interlace:
        return decode(path)[..., 0]
    raise PngError(f'fine_ref.py reads grey PNGs, and grey and alpha ones that are not interlaced; this is {png.describe()}')


def decode(path):
    """Every sample of a non-interlaced 8 or 16-bit PNG without a palette, decoded here without Pillow: (H, W, channels),
    uint8 or uint16. Slow for big files (the Average and Paeth filters run a pixel at a time)."""
    png = Png(path, keep_data=True)
    if png.interlace or png.depth not in (8, 16) or png.colour == 3:
        raise PngError(f'decode() reads 8 and 16-bit PNGs without a palette or interlacing; this is {png.describe()}')
    bpp = CHANNELS[png.colour] * png.depth // 8
    stride = png.width * bpp
    try:
        raw = zlib.decompress(png.data)
    except zlib.error as e:
        raise PngError(f'its image data cannot be read ({e})') from None
    if len(raw) < png.height * (stride + 1):
        raise PngError('its image data is cut short')
    out = np.empty((png.height, stride), np.uint8)
    prior = np.zeros(stride, np.int32)
    for y in range(png.height):
        at = y * (stride + 1)
        kind = raw[at]
        row = np.frombuffer(raw, np.uint8, stride, at + 1).astype(np.int32)
        if kind == 0:
            cur = row
        elif kind == 1:
            cur = np.empty_like(row)
            for j in range(bpp):
                cur[j::bpp] = np.cumsum(row[j::bpp]) & 255
        elif kind == 2:
            cur = (row + prior) & 255
        elif kind in (3, 4):
            r, p = row.tolist(), prior.tolist()
            for i in range(stride):
                a = r[i - bpp] if i >= bpp else 0
                if kind == 3:
                    r[i] = (r[i] + ((a + p[i]) >> 1)) & 255
                else:
                    b, c = p[i], (p[i - bpp] if i >= bpp else 0)
                    pa, pb, pc = abs(b - c), abs(a - c), abs(a + b - 2 * c)
                    r[i] = (r[i] + (a if pa <= pb and pa <= pc else b if pb <= pc else c)) & 255
            cur = np.array(r, np.int32)
        else:
            raise PngError(f'row {y} has filter type {kind}')
        out[y] = cur
        prior = cur
    channels = CHANNELS[png.colour]
    if png.depth == 16:
        return out.view('>u2').astype(np.uint16).reshape(png.height, png.width, channels)
    return out.reshape(png.height, png.width, channels)


def filter_row(kind, cur, prior, bpp):
    """A row of bytes (int32) filtered with PNG filter type kind."""
    if kind == 0:
        return cur
    a = np.zeros_like(cur)
    a[bpp:] = cur[:-bpp]
    if kind == 1:
        return (cur - a) & 255
    if kind == 2:
        return (cur - prior) & 255
    if kind == 3:
        return (cur - ((a + prior) >> 1)) & 255
    c = np.zeros_like(cur)
    c[bpp:] = prior[:-bpp]
    pa, pb, pc = np.abs(prior - c), np.abs(a - c), np.abs(a + prior - 2 * c)
    return (cur - np.where((pa <= pb) & (pa <= pc), a, np.where(pb <= pc, prior, c))) & 255


class PngWriter:
    """A PNG written a band of rows at a time to <path>.tmp and renamed when closed: grey (1 channel) or grey and alpha
    (2), 8 or 16 bits. Row y is filtered with filters[y % len(filters)]; texts go before the image data, or after it when
    given to close()."""

    def __init__(self, path, width, height, depth, channels=1, filters=(2,), texts=(), level=6):
        self.path, self.tmp = path, path + '.tmp'
        self.width, self.height, self.depth, self.channels = width, height, depth, channels
        self.bpp = channels * depth // 8
        self.filters, self.row = filters, 0
        self.prior = np.zeros(width * self.bpp, np.int32)
        self.z = zlib.compressobj(level)
        self.fh = open(self.tmp, 'wb')
        self.fh.write(SIGNATURE)
        self._chunk(b'IHDR', struct.pack('>IIBBBBB', width, height, depth, 0 if channels == 1 else 4, 0, 0, 0))
        for keyword, text in texts:
            self._chunk(b'tEXt', keyword.encode('latin-1') + b'\0' + text.encode('latin-1'))

    def _chunk(self, kind, body):
        self.fh.write(struct.pack('>I', len(body)) + kind + body + struct.pack('>I', zlib.crc32(kind + body) & 0xFFFFFFFF))

    def write(self, block):
        a = np.asarray(block)
        b = a.astype('>u2').view(np.uint8) if self.depth == 16 else a.astype(np.uint8)
        b = b.reshape(a.shape[0], self.width * self.bpp).astype(np.int32)
        out = bytearray()
        for cur in b:
            kind = self.filters[self.row % len(self.filters)]
            out.append(kind)
            out += filter_row(kind, cur, self.prior, self.bpp).astype(np.uint8).tobytes()
            self.prior = cur
            self.row += 1
        data = self.z.compress(bytes(out))
        if data:
            self._chunk(b'IDAT', data)

    def close(self, texts=()):
        if self.row != self.height:
            raise ValueError(f'{self.path}: {self.row} rows written of {self.height}')
        self._chunk(b'IDAT', self.z.flush())
        for keyword, text in texts:
            self._chunk(b'tEXt', keyword.encode('latin-1') + b'\0' + text.encode('latin-1'))
        self._chunk(b'IEND', b'')
        self.fh.close()
        os.replace(self.tmp, self.path)


def file_crc(path):
    crc = 0
    with open(path, 'rb') as fh:
        while chunk := fh.read(1 << 22):
            crc = zlib.crc32(chunk, crc)
    return crc & 0xFFFFFFFF


# ---- what Better Continents does with a pair ---------------------------------------------------------------------------

class Verdict:
    """used: whether Better Continents refines the heightmap with the fine file; level: none (no file), info, warning,
    error; message: its log line; c, f: the pixels when they were read (uint16, uint8); record: the fine record."""

    def __init__(self, used, level, message, c=None, f=None, record=None, bits=0, coarse=None, fine=None):
        self.used, self.level, self.message = used, level, message
        self.c, self.f, self.record, self.bits, self.coarse, self.fine = c, f, record, bits, coarse, fine


def judge(heightmap, alpha='none', version=12, read_pixels=True):
    """What Better Continents does with heightmap.png and the fine file beside it, in the order it checks."""
    fine = fine_path(heightmap)
    hn, fn = os.path.basename(heightmap), os.path.basename(fine)
    if not os.path.exists(fine):
        return Verdict(False, 'none', f'Fine heights: no {fn} beside {hn}; the heights come from {hn} alone (nothing is logged).')
    if version < 12:
        return Verdict(False, 'warning', f'Fine heights: {fn} is not read: the world is saved in settings version {version}, which has no fine heights.')
    if alpha == 'legacy':
        return Verdict(False, 'warning', f'Fine heights: {fn} is not read: Heightmap Alpha as it was before (Legacy) reads {hn} as 8-bit grey.')
    try:
        coarse = Png(heightmap)
    except (OSError, PngError) as e:
        return Verdict(False, 'error', f'Fine heights: {fn} is not read: {hn} cannot be read: {e}.')
    if coarse.depth != 16 or coarse.colour not in (0, 4):
        return Verdict(False, 'warning', f'Fine heights: {fn} is not read: {hn} is {coarse.describe()}; fine heights refine a 16-bit grey heightmap (grey, or grey and alpha).', coarse=coarse)
    try:
        png = Png(fine)
    except (OSError, PngError) as e:
        return Verdict(False, 'error', f'Fine heights: {fn} cannot be read: {e}.', coarse=coarse)
    if png.width > MAX_MAP or png.height > MAX_MAP:
        return Verdict(False, 'error', f'Fine heights: {fn} is {png.width} x {png.height} pixels, and the largest map Better Continents reads is {MAX_MAP} x {MAX_MAP}.', coarse=coarse, fine=png)
    if png.depth != 8 or png.colour != 0:
        return Verdict(False, 'error', f'Fine heights: {fn} is {png.describe()}; it must be 8-bit grey (no alpha, no palette).', coarse=coarse, fine=png)
    if (png.width, png.height) != (coarse.width, coarse.height):
        return Verdict(False, 'error', f'Fine heights: {fn} is {png.width} x {png.height} pixels and {hn} is {coarse.width} x {coarse.height}; they must be the same size.', coarse=coarse, fine=png)
    record = record_parts(png.texts)
    if record and record.get('format', 1) != 1:
        return Verdict(False, 'warning', f'Fine heights: {fn} is in fine format {record["format"]}, which this Better Continents cannot read.', record=record, coarse=coarse, fine=png)
    if record and 'crc' in record and record['crc'] != coarse.file_crc:
        return Verdict(False, 'warning', f'Fine heights: {fn} was made for another {hn} (its record names CRC-32 {record["crc"]:08X}; this one is {coarse.file_crc:08X}). '
                       f'If {hn} was edited, make {fn} again or delete it.', record=record, coarse=coarse, fine=png)
    if not read_pixels:
        return Verdict(True, 'info', 'Fine heights: the headers and the record are in order (pixels not read).', record=record, coarse=coarse, fine=png)
    try:
        f = read_grey(fine, png)
    except PngError as e:
        return Verdict(False, 'error', f'Fine heights: {fn} cannot be read: {e}.', record=record, coarse=coarse, fine=png)
    total = 0
    for y in range(0, f.shape[0], BAND):
        total |= int(np.bitwise_or.reduce(f[y:y + BAND], axis=None))
    bits = bits_used(total)
    if bits == 0:
        return Verdict(False, 'info', f'Fine heights: every byte of {fn} is 0, so it changes nothing: the world is the one {hn} makes alone.', f=f, record=record, coarse=coarse, fine=png)
    try:
        c = read_grey(heightmap, coarse)
    except PngError as e:
        return Verdict(False, 'error', f'Fine heights: {fn} is not read: {hn} cannot be read: {e}.', f=f, record=record, coarse=coarse, fine=png)
    return Verdict(True, 'info', f'Fine heights: {fn} refines {hn} with {bits} bits a pixel.', c=c, f=f, record=record, bits=bits, coarse=coarse, fine=png)


def tile_cost(f, compress=True):
    """The fine bytes as Better Continents stores them (TileBlock, MapTiles.cs): 128 px tiles counted from the image's
    last row (the map's row 0 is south); a tile of one value costs 2 bytes, any other 5 bytes and the smaller of its bytes
    deflated as they are or as median-predictor residuals. Returns (uniform tiles, other tiles, block bytes); the bytes
    are an estimate (zlib's level 6 for .NET's DeflateStream)."""
    g = f[::-1]
    size_y, size_x = g.shape
    uniform = mixed = 0
    total = 13
    for ty in range(0, size_y, TILE):
        band = np.ascontiguousarray(g[ty:ty + TILE]).astype(np.int32)
        for tx in range(0, size_x, TILE):
            t = band[:, tx:tx + TILE]
            if (t == t.flat[0]).all():
                uniform += 1
                total += 2
                continue
            mixed += 1
            if not compress:
                continue
            a = np.zeros_like(t)
            a[:, 1:] = t[:, :-1]
            b = np.zeros_like(t)
            b[1:] = t[:-1]
            cc = np.zeros_like(t)
            cc[1:, 1:] = t[:-1, :-1]
            hi, lo = np.maximum(a, b), np.minimum(a, b)
            pred = np.where(cc >= hi, lo, np.where(cc <= lo, hi, a + b - cc))
            pred[0] = a[0]
            pred[1:, 0] = t[:-1, 0]
            d = (((t - pred) + 128) & 255) - 128
            residual = ((d << 1) ^ (d >> 7)) & 255
            sizes = []
            for raw in (t, residual):
                z = zlib.compressobj(6, zlib.DEFLATED, -15)
                sizes.append(len(z.compress(raw.astype(np.uint8).tobytes()) + z.flush()))
            total += 5 + min(sizes)
    return uniform, mixed, total


def stats(c, f, amount=None, sla=0.0):
    """Counts over the pair, a band of rows at a time."""
    h = c.shape[0]
    s = dict(pixels=c.size, nonzero=0, fmin=127, fmax=-128, water=None, water_nonzero=0, step1=0, step2=0,
             gentle=0, gentle_nonzero=0, over=0, under=0)
    if amount is not None:
        s['water'] = 0
    for y0 in range(0, h, BAND):
        y1 = min(h, y0 + BAND)
        e0, e1 = max(0, y0 - 1), min(h, y1 + 1)
        n = heights24(c[e0:e1], f[e0:e1])
        steep = steepest(n)[y0 - e0:y1 - e0]
        n = n[y0 - e0:y1 - e0]
        fs = signed(f[y0:y1])
        cb = c[y0:y1]
        nz = fs != 0
        s['nonzero'] += int(nz.sum())
        if nz.any():
            s['fmin'] = min(s['fmin'], int(fs.min()))
            s['fmax'] = max(s['fmax'], int(fs.max()))
        s['over'] += int(((cb == 65535) & (fs > 0)).sum())
        s['under'] += int(((cb == 0) & (fs < 0)).sum())
        s['step1'] += int((nz & (steep >= 256) & (steep < 512)).sum())
        s['step2'] += int((nz & (steep >= 512)).sum())
        dry = np.ones(n.shape, bool)
        if amount is not None:
            wet = to_metres(n / SCALE, amount, sla) < WATER
            s['water'] += int(wet.sum())
            s['water_nonzero'] += int((nz & wet).sum())
            dry = ~wet
        gentle = dry & (steep < 256)
        s['gentle'] += int(gentle.sum())
        s['gentle_nonzero'] += int((gentle & nz).sum())
    return s


def resolve(path):
    return os.path.join(path, 'heightmap.png') if os.path.isdir(path) else path


def amount_and_sla(args, coarse):
    record = heightmap_record(coarse.texts) if coarse is not None else None
    amount = args.amount if args.amount is not None else (record[0] if record else None)
    if args.sea_adjust is not None:
        sla = args.sea_adjust
    elif args.sea_level is not None:
        sla = sea_adjustment(args.sea_level)
    else:
        sla = sea_adjustment(record[1]) if record else 0.0
    return amount, sla, record


def cmd_check(args):
    heightmap = resolve(args.path)
    v = judge(heightmap, alpha=args.alpha, version=args.settings_version)
    print(v.message)
    hn, fn = os.path.basename(heightmap), os.path.basename(fine_path(heightmap))
    if v.coarse is not None:
        print(f'  {hn}: {v.coarse.width} x {v.coarse.height}, {v.coarse.describe()}, {v.coarse.file_size / 1e6:.2f} MB, CRC-32 {v.coarse.file_crc:08X}')
    if v.fine is not None:
        where = 'after the image data' if v.fine.texts_after_data else 'before the image data'
        rec = 'no record' if not v.record else 'record "' + '; '.join(
            f'{k} = {v.record[key]:08X}' if key == 'crc' else f'{k} = {v.record[key]}'
            for key, k in (('format', 'Fine Format'), ('bits', 'Fine Bits'), ('crc', 'Heightmap CRC-32')) if key in v.record) + f'" ({where})'
        print(f'  {fn}: {v.fine.width} x {v.fine.height}, {v.fine.describe()}, {v.fine.file_size / 1e6:.2f} MB, {rec}')
        if v.record and 'crc' not in v.record:
            print(f'  the record names no CRC-32, so the pair is trusted as it is')
    if v.f is None:
        return 0 if v.used else 1
    amount, sla, record = amount_and_sla(args, v.coarse)
    if v.record and 'bits' in v.record and v.record['bits'] != v.bits:
        print(f'  the record says {v.record["bits"]} bits; the bytes use {v.bits}')
    if v.c is None:
        return 1
    s = stats(v.c, v.f, amount, sla)
    px = s['pixels']
    print(f'  f is not 0 on {s["nonzero"]:,} of {px:,} pixels ({s["nonzero"] / px:.1%}), from {s["fmin"]} to {s["fmax"]}; '
          f'{v.bits} bits used (the bytes are multiples of {1 << (8 - v.bits)})')
    if amount is None:
        print('  water: not checked (no --amount, and heightmap.png has no record)')
    else:
        step = 200 * amount / 65535
        src = 'given' if args.amount is not None else "heightmap.png's record"
        print(f'  Heightmap Amount {amount:g} ({src}), Sea Level Adjustment {sla:g}: a step of {hn} is {step * 100:.2f} cm; '
              f'with {v.bits} bits, {step / (1 << v.bits) * 1000:.2f} mm')
        print(f'  under water (below {WATER:g} m): {s["water"]:,} pixels, {s["water_nonzero"]:,} of them with f not 0 (the policy keeps 0)')
    print(f'  f not 0 where the ground rises 2 steps or more to a neighbour: {s["step2"]:,} pixels (the policy keeps 0); '
          f'1 to 2 steps: {s["step1"]:,} (a writer measures its own heights, so a few are expected)')
    print(f'  gentle {"dry " if amount is not None else ""}ground (under a step to every neighbour): {s["gentle"]:,} pixels, '
          f'f not 0 on {s["gentle_nonzero"]:,} ({s["gentle_nonzero"] / max(s["gentle"], 1):.1%})')
    if s['over'] or s['under']:
        print(f'  {s["over"]:,} pixels go past the top (c 65535, f above 0) and {s["under"]:,} below the bottom (c 0, f below 0)')
    if not args.no_cost:
        uniform, mixed, total = tile_cost(v.f)
        print(f'  in a world: {uniform:,} tiles of one value, {mixed:,} others; about {total / 1e6:.1f} MB more in the world file and '
              f'in every join ({total * 8 / px:.2f} bits a pixel), {mixed * TILE * TILE / 1e6:.0f} MB more when every tile is decoded')
    return 0


def map_coords(xs, ys, size, coords, total_size=None):
    """Points as Better Continents' map position (x, y from 0 to 1, y = 0 at the image's last row), as float32 like the
    game: 'map' as given; 'pixel' = (column, row) with row 0 the image's first; 'world' = metres from the centre, east
    and north, over --total-size (2 x (World Size + Edge))."""
    xs = np.asarray(xs, np.float64)
    ys = np.asarray(ys, np.float64)
    if coords == 'pixel':
        return (xs / (size - 1)).astype(np.float32), ((size - 1 - ys) / (size - 1)).astype(np.float32)
    if coords == 'world':
        t = np.float32(total_size)
        norm = lambda w: np.clip(w.astype(np.float32) / t + np.float32(0.5), np.float32(0), np.float32(1))
        return norm(xs), norm(ys)
    return xs.astype(np.float32), ys.astype(np.float32)


def sample(c, f, mx, my):
    """Heights v as Better Continents samples them (ImageMapFloat.Sample): bilinear between the pixel centres of N, the
    position worked out in float32 as the game does, the blend in double (the game's float32 blend differs by about a
    unit, 1/256 step). f None: the heightmap alone."""
    size = c.shape[0]
    xa = mx.astype(np.float32) * np.float32(size - 1)
    ya = my.astype(np.float32) * np.float32(size - 1)
    xi, yi = np.floor(xa), np.floor(ya)
    xd, yd = (xa - xi).astype(np.float64), (ya - yi).astype(np.float64)
    xi, yi = xi.astype(np.int64), yi.astype(np.int64)
    x0, x1 = np.clip(xi, 0, size - 1), np.clip(xi + 1, 0, size - 1)
    y0, y1 = np.clip(yi, 0, size - 1), np.clip(yi + 1, 0, size - 1)

    def at(x, y):
        r = size - 1 - y
        return heights24(c[r, x], None if f is None else f[r, x]).astype(np.float64)

    n00, n10, n01, n11 = at(x0, y0), at(x1, y0), at(x0, y1), at(x1, y1)
    top = n00 + (n10 - n00) * xd
    bottom = n01 + (n11 - n01) * xd
    return (top + (bottom - top) * yd) / SCALE


def cmd_sample(args):
    heightmap = resolve(args.path)
    v = judge(heightmap, alpha=args.alpha, version=args.settings_version)
    print('# ' + v.message)
    coarse = v.coarse or Png(heightmap)
    c = v.c if v.c is not None else read_grey(heightmap, coarse)
    f = v.f if v.used else None
    pts = [tuple(float(t) for t in p.split(',')) for p in args.at or []]
    if args.points:
        with open(args.points) as fh:
            for line in fh:
                line = line.split('#')[0].split()
                if len(line) >= 2:
                    pts.append((float(line[0]), float(line[1])))
    if not pts:
        print('no points: give --at X,Y or --points FILE', file=sys.stderr)
        return 2
    xs, ys = zip(*pts)
    mx, my = map_coords(xs, ys, c.shape[0], args.coords, args.total_size)
    vf = sample(c, f, mx, my)
    vc = sample(c, None, mx, my)
    amount, sla, _ = amount_and_sla(args, coarse)
    head = 'x\ty\tmap_x\tmap_y\tv_fine\tv_alone'
    if amount is not None:
        head += '\tmetres_fine\tmetres_alone'
    print(head)
    for i, (x, y) in enumerate(pts):
        line = f'{x:g}\t{y:g}\t{str(mx[i])}\t{str(my[i])}\t{vf[i]:.12g}\t{vc[i]:.12g}'
        if amount is not None:
            line += f'\t{to_metres(vf[i], amount, sla):.4f}\t{to_metres(vc[i], amount, sla):.4f}'
        print(line)
    return 0


# ---- writing pairs ---------------------------------------------------------------------------------------------------

def write_pair(folder, rows, width, height, bits, amount, sla, policy=True, alpha_rows=None, filters=(2,),
               record_after=True, name='heightmap'):
    """heightmap.png and heightmap-fine.png from float heights. rows(y0, y1) gives v for file rows y0..y1 (float64);
    alpha_rows(y0, y1), when given, a 16-bit alpha (the heightmap is then grey and alpha). Bands carry a row above and
    below for the steep-ground rule. Returns (fine bits used, nonzero fine pixels, clipped pixels, heightmap CRC-32)."""
    os.makedirs(folder, exist_ok=True)
    hp = os.path.join(folder, name + '.png')
    fp = fine_path(hp)
    # The setting, as Better Continents' own record holds it: sla = 1 - 2 x setting.
    record = f'Heightmap Amount = {float_text(amount)}; Sea Level Adjustment = {float_text((1.0 - sla) / 2.0)}'
    hw = PngWriter(hp, width, height, 16, 1 if alpha_rows is None else 2, filters, texts=[(KEYWORD, record)])
    fw = PngWriter(fp, width, height, 8, 1, filters, texts=())
    used = nonzero = clipped = 0
    for y0 in range(0, height, BAND):
        y1 = min(height, y0 + BAND)
        e0, e1 = max(0, y0 - 1), min(height, y1 + 1)
        v = rows(e0, e1)
        clipped += int(((v[y0 - e0:y1 - e0] < 0) | (v[y0 - e0:y1 - e0] > 1)).sum())
        c, f = encode(v, bits)
        if policy:
            apply_policy(v, f, amount, sla)
        c, f = c[y0 - e0:y1 - e0], f[y0 - e0:y1 - e0]
        if alpha_rows is not None:
            hw.write(np.stack([c, alpha_rows(y0, y1).astype(np.uint16)], axis=-1))
        else:
            hw.write(c)
        fw.write(f.view(np.uint8))
        used |= int(np.bitwise_or.reduce(f.view(np.uint8), axis=None))
        nonzero += int(np.count_nonzero(f))
    hw.close()
    crc = file_crc(hp)
    text = (KEYWORD, fine_record_text(bits, crc))
    if record_after:
        fw.close(texts=[text])
    else:
        # The record before the image data needs the CRC first: the fine file is written again with it at the front.
        fw.close()
        f_all = read_grey(fp, Png(fp))
        again = PngWriter(fp, width, height, 8, 1, filters, texts=[text])
        for y0 in range(0, height, BAND):
            again.write(f_all[y0:y0 + BAND])
        again.close()
    return bits_used(used), nonzero, clipped, crc


def cmd_write(args):
    if args.from_heightmap:
        src = Png(args.from_heightmap)
        if src.depth != 16 or src.colour not in (0, 4):
            print(f'{args.from_heightmap} is {src.describe()}; give a 16-bit grey heightmap', file=sys.stderr)
            return 2
        record = heightmap_record(src.texts)
        a0 = args.from_amount if args.from_amount is not None else (record[0] if record else None)
        s0 = args.from_sea_adjust if args.from_sea_adjust is not None else (sea_adjustment(record[1]) if record else 0.0)
        if a0 is None:
            print('give --from-amount: the heightmap has no record', file=sys.stderr)
            return 2
        c0 = read_grey(args.from_heightmap, src)
        height, width = c0.shape
        sla = args.sea_adjust if args.sea_adjust is not None else s0
        rows = lambda e0, e1: (c0[e0:e1] / 65535.0 * a0 + s0 - sla) / args.amount
        what = f'{args.from_heightmap} (Heightmap Amount {a0:g}, Sea Level Adjustment {s0:g})'
    else:
        data = np.load(args.from_npy, mmap_mode='r')
        height, width = data.shape
        sla = args.sea_adjust if args.sea_adjust is not None else 0.0
        if args.units == 'metres':
            rows = lambda e0, e1: to_v(np.asarray(data[e0:e1], np.float64), args.amount, sla)
        else:
            rows = lambda e0, e1: np.asarray(data[e0:e1], np.float64)
        what = f'{args.from_npy} ({args.units})'
    if width != height or width > MAX_MAP:
        print(f'the heights are {width} x {height}; Better Continents reads square maps up to {MAX_MAP} px', file=sys.stderr)
        return 2
    bits, nonzero, clipped, crc = write_pair(args.folder, rows, width, height, args.bits, args.amount, sla,
                                             policy=not args.everywhere)
    print(f'{args.folder}: heightmap.png (CRC-32 {crc:08X}) and heightmap-fine.png from {what}, at Heightmap Amount '
          f'{args.amount:g}, Sea Level Adjustment {sla:g}; {args.bits} bits asked, {bits} used; f not 0 on {nonzero:,} of '
          f'{width * height:,} pixels' + (f'; {clipped:,} pixels clipped to 0..1' if clipped else ''))
    return 0


def fixture_heights(size, amount, sla=0.0):
    """A small landscape in metres (file order, row 0 north): sea in the west, a plain that rises gently to the east with
    low hills (gentler than a step per pixel at Amount 81), a steep peak, and a flat pad at exactly 45 m."""
    yy, xx = np.mgrid[0:size, 0:size].astype(np.float64)
    u, w = xx / (size - 1), yy / (size - 1)
    m = 20.0 + 40.0 * u + 2.0 * np.sin(2 * np.pi * 3 * u) * np.cos(2 * np.pi * 2 * w)
    r = np.hypot(u - 0.75, w - 0.3)
    m += 400.0 * np.clip(1.0 - r / 0.12, 0.0, None)
    pad = (np.abs(u - 0.45) < 0.07) & (np.abs(w - 0.7) < 0.07)
    m[pad] = 45.0
    return to_v(m, amount, sla)


def cmd_fixture(args):
    size, amount, sla = args.size, args.amount, 0.0
    v = fixture_heights(size, amount, sla)
    alpha = None
    if args.alpha:
        yy, xx = np.mgrid[0:size, 0:size]
        a = np.rint((xx + yy) / (2 * (size - 1)) * 65535).astype(np.uint16)
        alpha = lambda y0, y1: a[y0:y1]
    bits, nonzero, _, crc = write_pair(args.folder, lambda e0, e1: v[e0:e1], size, size, args.bits, amount, sla,
                                       policy=args.policy, alpha_rows=alpha, filters=(0, 1, 2, 3, 4),
                                       record_after=not args.record_before)
    hp = os.path.join(args.folder, 'heightmap.png')
    c = read_grey(hp, Png(hp))
    f = read_grey(fine_path(hp), Png(fine_path(hp)))
    pts = []
    for p in (0, 1, size // 2, size - 2, size - 1, 127, 128, 129):
        if p < size:
            pts += [(p, q) for q in (0, size // 3, size - 1, 127, 128) if q < size]
    pts += [(127.5, 128.25), (0.5, 0.5), (size - 1.5, size / 3 + 0.75), (-3.0, 5.0), (size + 2.0, size / 2)]
    rng = np.random.default_rng(args.seed)
    pts += [tuple(p) for p in rng.uniform(0, size - 1, (200, 2))]
    xs, ys = zip(*pts)
    mx, my = map_coords(xs, ys, size, 'pixel')
    vf, vc = sample(c, f, mx, my), sample(c, None, mx, my)
    with open(os.path.join(args.folder, 'samples.tsv'), 'w') as out:
        out.write(f'# fine_ref.py fixture: {size} px, Heightmap Amount {amount:g}, Sea Level Adjustment {sla:g}, '
                  f'{args.bits} bits asked ({bits} used), policy {"on" if args.policy else "off"}, alpha {"on" if args.alpha else "off"}, '
                  f'heightmap CRC-32 {crc:08X}\n')
        out.write('# map_x and map_y are the float32 values to sample at (Better Continents\' 0..1, y = 0 at the last row)\n')
        out.write('map_x\tmap_y\tv_fine\tv_alone\tmetres_fine\tmetres_alone\n')
        for i in range(len(pts)):
            out.write(f'{str(mx[i])}\t{str(my[i])}\t{vf[i]:.15g}\t{vc[i]:.15g}\t'
                      f'{to_metres(vf[i], amount, sla):.6f}\t{to_metres(vc[i], amount, sla):.6f}\n')
    print(f'{args.folder}: {size} px pair at Heightmap Amount {amount:g}, {bits} bits used, f not 0 on {nonzero:,} pixels; '
          f'samples.tsv has {len(pts)} points')
    return 0


# ---- the properties ----------------------------------------------------------------------------------------------------

def cmd_selftest(args):
    import tempfile
    failures = []

    def expect(ok, what):
        print(('ok    ' if ok else 'FAIL  ') + what)
        if not ok:
            failures.append(what)

    # The worked example: 123.456 m at Heightmap Amount 81, Sea Level 0.5.
    v = to_v(123.456, 81.0)
    c, f = encode(np.array([v]), 8)
    n = heights24(c, f)[0]
    expect((int(c[0]), int(f[0]), int(f.view(np.uint8)[0])) == (621, -55, 201), f'123.456 m at A 81 is c 621, f -55 (byte 201): got {int(c[0])}, {int(f[0])}')
    expect(abs(to_metres(n / SCALE, 81.0) - 123.4557) < 5e-5, f'and reads 123.4557 m: {to_metres(n / SCALE, 81.0):.4f}')
    expect(abs(to_metres(int(c[0]) / 65535, 81.0) - 123.5088) < 5e-5, 'without the fine byte 123.5088 m')
    c4, f4 = encode(np.array([v]), 4)
    expect((int(f4[0]), int(f4.view(np.uint8)[0])) == (-48, 208) and abs(to_metres(heights24(c4, f4)[0] / SCALE, 81.0) - 123.4625) < 5e-5,
           f'4 bits: f -48 (byte 208), 123.4625 m: got {int(f4[0])}')

    # A million random heights: c is the plain writer's; N within half a unit (1 at the top of the range); 4 bits too.
    rng = np.random.default_rng(1)
    v = np.concatenate([rng.random(1_000_000), [0.0, 1.0, 0.5 / 65535, 1 - 0.5 / 65535], (np.arange(65536) + 0.5) / 65535])
    x = np.clip(v, 0, 1) * 65535
    for bits, near, far, edge in ((8, 0.5, 1.0, 127.5), (4, 8.0, 16.0, 120.0), (2, 32.0, 64.0, 96.0), (1, 64.0, 128.0, 64.0)):
        c, f = encode(v, bits)
        expect(np.array_equal(c, np.floor(x + 0.5).astype(np.uint16)), f'{bits} bits: c is exactly floor(v * 65535 + 0.5)')
        err = np.abs(heights24(c, f) - x * 256)
        top = (x - c) * 256 > edge
        expect(err[~top].max() <= near + 1e-6 and err[top].max(initial=0) <= far + 1e-6,
               f'{bits} bits: N within {near:g} units ({far:g} in the top {128 - edge:g} units): {err[~top].max():.4f}, {err[top].max(initial=0):.4f}')
        expect(np.all(f.view(np.uint8) % (1 << (8 - bits)) == 0), f'{bits} bits: the bytes are multiples of {1 << (8 - bits)}')
    c, f = encode(v, 0)
    expect(not f.any() and np.array_equal(c, np.floor(x + 0.5).astype(np.uint16)), '0 bits: f is 0, c the same')
    # Integer form: N = round(v * 16776960), c = (N + 128) >> 8, f = N & 255 gives the same pair except where x is a hair
    # from a half step (c rounds the other way there).
    nn = np.rint(x * 256).astype(np.int64)
    ci, fi = (nn + 128) >> 8, nn & 255
    c8, f8 = encode(v, 8)
    same = (ci == c8) & (fi == f8.view(np.uint8))
    near_half = np.abs((x - np.floor(x)) - 0.5) < 1 / 256
    expect(np.all(same | near_half), f'the integer form gives the same pair except within 1/256 of a half step ({int((~same).sum())} differ)')
    # The provisional rule (c floored, f the low byte of N) converts by keeping the byte.
    cp, fp_ = nn >> 8, (nn & 255).astype(np.uint8)
    cv, fv = from_provisional(cp, fp_)
    expect(np.array_equal(heights24(cv, fv), nn) and np.array_equal(fv, fp_), 'provisional pairs convert: same byte, same N')
    expect([bits_used(b) for b in (0, 1, 16, 48, 128, 255, 8 | 64)] == [0, 8, 4, 4, 1, 8, 5], 'bits_used')

    # Where Better Continents looks.
    cases = {'heightmap.png': 'heightmap-fine.png', os.path.join('a.b', 'c.v2.png'): os.path.join('a.b', 'c.v2-fine.png'),
             'MAP.PNG': 'MAP-fine.PNG', 'iceland': 'iceland-fine.png', 'file.': 'file-fine.png', '.png': '-fine.png'}
    expect(all(fine_path(k) == w for k, w in cases.items()), f'fine_path: {[fine_path(k) for k in cases]}')
    # The record.
    good = [(KEYWORD, 'Heightmap CRC-32 = 0a1b2c3d ;Fine Bits=4; Fine Format = 1; Other = x')]
    expect(record_parts(good) == {'crc': 0x0A1B2C3D, 'bits': 4, 'format': 1}, f'record parts in any order: {record_parts(good)}')
    expect(record_parts([('Other', 'Fine Format = 2'), (KEYWORD, 'Fine Format = 2')]) == {'format': 2}, 'only the BetterContinents keyword')
    expect(record_parts([(KEYWORD, 'Heightmap Amount = 81; Sea Level Adjustment = 0')]) is None, "the heightmap's own record is not a fine record")
    expect(record_parts([(KEYWORD, 'Heightmap CRC-32 = 0x12; Fine Format = one')]) is None, 'values that do not parse are skipped')

    with tempfile.TemporaryDirectory() as tmp:
        # PNG round trips: every filter, 8 and 16 bits, grey and grey + alpha, text before and after the data; read
        # back with Pillow and with decode().
        rng = np.random.default_rng(2)
        for depth, channels, w in ((8, 1, 1), (8, 1, 129), (16, 1, 130), (16, 2, 67), (8, 2, 40)):
            a = rng.integers(0, 1 << depth, (37, w, channels)).astype(np.uint16 if depth == 16 else np.uint8)
            a[5:9] = a[4]  # rows that repeat: Up gives zeros
            p = os.path.join(tmp, f'rt-{depth}-{channels}-{w}.png')
            pw = PngWriter(p, w, 37, depth, channels, filters=(0, 1, 2, 3, 4), texts=[(KEYWORD, 'Fine Format = 1')])
            pw.write(a[:20] if channels > 1 else a[:20, :, 0])
            pw.write(a[20:] if channels > 1 else a[20:, :, 0])
            pw.close(texts=[('Comment', 'after')])
            png = Png(p)
            ok = np.array_equal(decode(p), a) and png.texts == [(KEYWORD, 'Fine Format = 1'), ('Comment', 'after')] and png.texts_after_data == 1
            if channels == 1:
                ok &= np.array_equal(read_grey(p, png), a[..., 0])
            expect(ok, f'PNG round trip: {depth}-bit, {channels} channel(s), {w} px wide, every filter')

        # The pair, and what Better Continents makes of it in each case.
        size, amount = 300, 81.0
        v = fixture_heights(size, amount)
        folder = os.path.join(tmp, 'pair')
        bits, nonzero, _, crc = write_pair(folder, lambda e0, e1: v[e0:e1], size, size, 4, amount, 0.0, filters=(0, 1, 2, 3, 4))
        hp = os.path.join(folder, 'heightmap.png')
        fp = fine_path(hp)
        verdict = judge(hp)
        expect(verdict.used and verdict.bits == 4 and verdict.record == {'format': 1, 'bits': 4, 'crc': crc}, f'the fixture is used, 4 bits: {verdict.message}')
        c_ref, f_ref = encode(v, 4)
        apply_policy(v, f_ref, amount, 0.0)
        expect(np.array_equal(verdict.c, c_ref) and np.array_equal(signed(verdict.f), f_ref), 'the files hold the reference pair')
        expect(np.array_equal(verdict.c, encode(v, 0)[0]), 'heightmap.png is the plain writer\'s')
        wet = to_metres(v, amount) < WATER
        expect(not signed(verdict.f)[wet].any(), 'f is 0 under water')
        steep = steepest(np.rint(v * SCALE)) >= 256
        expect(steep.any() and not signed(verdict.f)[steep].any(), 'f is 0 on steep ground')
        expect(signed(verdict.f)[~wet & ~steep].any(), 'f is not 0 on gentle dry ground')
        # Sampling: pixel centres read N exactly; halfway between two pixels reads their mean; outside clamps.
        cc, ff = verdict.c, verdict.f
        n = heights24(cc, ff)
        mx, my = map_coords([0, 10, 299, 10.5, -5], [0, 20, 299, 20, 0], size, 'pixel')
        got = sample(cc, ff, mx, my) * SCALE
        want = [n[0, 0], n[20, 10], n[299, 299], (n[20, 10] + n[20, 11]) / 2, n[0, 0]]
        expect(np.allclose(got, want, rtol=0, atol=1e-3), f'sampling at pixel centres, between them and outside: {np.round(got - want, 6)}')
        mx, my = map_coords([0.0], [-1.0], size, 'world', total_size=21000.0)
        expect(float(mx[0]) == 0.5 and abs(float(my[0]) - (0.5 - 1 / 21000)) < 1e-7, 'world coordinates as the game normalises them')

        def variant(name, make):
            d = os.path.join(tmp, name)
            os.makedirs(d)
            h = os.path.join(d, 'heightmap.png')
            with open(h, 'wb') as out, open(hp, 'rb') as src:
                out.write(src.read())
            make(h, fine_path(h))
            return judge(h)

        def copy_fine(h, f):
            with open(f, 'wb') as out, open(fp, 'rb') as src:
                out.write(src.read())

        def png_of(f, a, depth, channels=1, texts=(), after=()):
            pw = PngWriter(f, a.shape[1], a.shape[0], depth, channels, texts=texts)
            pw.write(a)
            pw.close(texts=after)

        fine8 = verdict.f
        expect(variant('copy', copy_fine).used, 'a copy beside a copy of the heightmap is used')
        expect(judge(os.path.join(tmp, 'none', 'heightmap.png')).level == 'none', 'no fine file: nothing')
        cases = [
            ('other size', lambda h, f: png_of(f, fine8[:299, :299], 8), 'error', 'same size'),
            ('16-bit', lambda h, f: png_of(f, fine8.astype(np.uint16), 16), 'error', '16-bit grey'),
            ('grey+alpha', lambda h, f: png_of(f, np.stack([fine8, fine8], -1), 8, 2), 'error', 'grey and alpha'),
            ('not a png', lambda h, f: open(f, 'wb').write(b'not a picture'), 'error', 'not a PNG'),
            ('cut short', lambda h, f: open(f, 'wb').write(open(fp, 'rb').read()[:3000]), 'error', 'cut short'),
            ('format 2', lambda h, f: png_of(f, fine8, 8, texts=[(KEYWORD, 'Fine Format = 2')]), 'warning', 'fine format 2'),
            ('other crc', lambda h, f: png_of(f, fine8, 8, after=[(KEYWORD, fine_record_text(4, crc ^ 1))]), 'warning', 'made for another'),
            ('all zero', lambda h, f: png_of(f, np.zeros_like(fine8), 8), 'info', 'changes nothing'),
        ]
        for name, make, level, words in cases:
            r = variant(name, make)
            expect(not r.used and r.level == level and words in r.message, f'{name}: {r.level}: {r.message}')
        r = variant('no record', lambda h, f: png_of(f, fine8, 8))
        expect(r.used and r.record is None, f'no record: trusted and used: {r.message}')
        r = variant('crc only', lambda h, f: png_of(f, fine8, 8, after=[(KEYWORD, f'Heightmap CRC-32 = {crc:x}')]))
        expect(r.used, f'a lower-case CRC and no Fine Format: used: {r.message}')
        r = variant('8-bit heightmap', lambda h, f: (png_of(h, (verdict.c >> 8).astype(np.uint8), 8), copy_fine(h, f)))
        expect(not r.used and r.level == 'warning' and '8-bit grey' in r.message, f'an 8-bit heightmap: {r.message}')
        r = judge(hp, alpha='legacy')
        expect(not r.used and r.level == 'warning', f'Heightmap Alpha Legacy: {r.message}')
        r = judge(hp, version=11)
        expect(not r.used and r.level == 'warning', f'settings version 11: {r.message}')
        # Grey + alpha heightmap (Heightmap Alpha Blend): the fine file refines the grey.
        folder = os.path.join(tmp, 'alpha')
        write_pair(folder, lambda e0, e1: v[e0:e1], size, size, 8, amount, 0.0, alpha_rows=lambda y0, y1: np.full((y1 - y0, size), 30000))
        r = judge(os.path.join(folder, 'heightmap.png'), alpha='blend')
        expect(r.used and r.bits == 8 and np.array_equal(r.c, encode(v, 8)[0]), f'a grey and alpha heightmap: {r.message}')
        # Tile cost: a tile of one value costs 2 bytes.
        u, m, total = tile_cost(np.zeros((300, 300), np.uint8))
        expect((u, m, total) == (9, 0, 13 + 18), f'tile cost of an all-zero 300 px map: {u}, {m}, {total}')

    print(f'{len(failures)} failed' if failures else 'all passed')
    return 1 if failures else 0


def main():
    ap = argparse.ArgumentParser(description=__doc__.split('\n\n')[0], formatter_class=argparse.RawDescriptionHelpFormatter)
    sub = ap.add_subparsers(dest='command', required=True)

    def heights_options(p):
        p.add_argument('--amount', type=float, help='Heightmap Amount (default: the heightmap\'s record)')
        p.add_argument('--sea-level', type=float, help='the Sea Level setting, 0..1 (0.5 is the default)')
        p.add_argument('--sea-adjust', type=float, help='or the Sea Level Adjustment itself (0 at Sea Level 0.5)')
        p.add_argument('--alpha', choices=('none', 'blend', 'legacy'), default='none', help='Heightmap Alpha')
        p.add_argument('--settings-version', type=int, default=12)

    p = sub.add_parser('check', help='what Better Continents makes of a heightmap and its fine file')
    p.add_argument('path', help='heightmap.png, or the folder that holds it')
    heights_options(p)
    p.add_argument('--no-cost', action='store_true', help='skip the estimate of the bytes in a world')
    p.set_defaults(run=cmd_check)

    p = sub.add_parser('sample', help='heights as Better Continents samples them')
    p.add_argument('path')
    heights_options(p)
    p.add_argument('--at', action='append', metavar='X,Y')
    p.add_argument('--points', help='a file of "x y" lines')
    p.add_argument('--coords', choices=('map', 'pixel', 'world'), default='pixel')
    p.add_argument('--total-size', type=float, help='for world coordinates: 2 x (World Size + Edge), in metres')
    p.set_defaults(run=cmd_sample)

    p = sub.add_parser('write', help='a pair from heights, with the recommended policy')
    p.add_argument('folder')
    src = p.add_mutually_exclusive_group(required=True)
    src.add_argument('--from-heightmap', help='a 16-bit heightmap.png to write again at --amount')
    src.add_argument('--from-npy', help='a square float array of heights (file order, row 0 north)')
    p.add_argument('--from-amount', type=float, help='the source heightmap\'s Heightmap Amount (default: its record)')
    p.add_argument('--from-sea-adjust', type=float, help='its Sea Level Adjustment (default: its record, else 0)')
    p.add_argument('--units', choices=('v', 'metres'), default='v', help='what --from-npy holds')
    p.add_argument('--amount', type=float, required=True, help='the Heightmap Amount to write for')
    p.add_argument('--sea-adjust', type=float, help='the Sea Level Adjustment to write for (default: the source\'s)')
    p.add_argument('--bits', type=int, default=4, choices=range(0, 9))
    p.add_argument('--everywhere', action='store_true', help='keep f on steep ground and under water too')
    p.set_defaults(run=cmd_write)

    p = sub.add_parser('fixture', help='a small test pair and its expected samples (samples.tsv)')
    p.add_argument('folder')
    p.add_argument('--size', type=int, default=300)
    p.add_argument('--amount', type=float, default=81.0)
    p.add_argument('--bits', type=int, default=8, choices=range(0, 9))
    p.add_argument('--policy', action='store_true', help='apply the recommended policy')
    p.add_argument('--alpha', action='store_true', help='a 16-bit grey and alpha heightmap (Heightmap Alpha Blend)')
    p.add_argument('--record-before', action='store_true', help='the fine record before the image data')
    p.add_argument('--seed', type=int, default=1)
    p.set_defaults(run=cmd_fixture)

    p = sub.add_parser('selftest', help='check the format\'s properties')
    p.set_defaults(run=cmd_selftest)

    args = ap.parse_args()
    if args.command in ('check', 'sample') and not os.path.exists(resolve(args.path)):
        print(f'{resolve(args.path)} does not exist', file=sys.stderr)
        return 2
    return args.run(args)


if __name__ == '__main__':
    sys.exit(main())
