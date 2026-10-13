"""Re-encode desktop-only (BCn / DXT, incl. crunched) textures as ASTC, which every iOS GPU samples natively.

The A16 on iOS 16 has no BC texture support: Unity would decompress every BCn texture on the CPU at load
time (slow, doubles memory, fails outright for cubemaps / BC6H). Block size policy (bits per pixel):

  * DXT1 (opaque) >= 512 px  -> ASTC 6x6 (3.56 bpp, a bit smaller than DXT1's 4 bpp)
  * everything else          -> ASTC 4x4 (8 bpp; same size as DXT5/BC7, small DXT1 double)

Channel layout is kept as is (e.g. DXT5nm normal maps stay AG), because the shaders are translated from
the game's own DX11 programs and decode exactly what the PC build stored.

`transcode(fmt, w, h, mips, images, data)` is a pure function so it can run in a process pool.
"""
import astc_encoder
import texture2ddecoder

# Unity TextureFormat ids
DXT1, DXT5, BC6H, BC7, BC4, BC5, DXT1_CRUNCHED, DXT5_CRUNCHED = 10, 12, 24, 25, 26, 27, 28, 29
ASTC_4x4, ASTC_6x6 = 48, 50
ASTC_BLOCK = {ASTC_4x4: 4, ASTC_6x6: 6}

BLOCK_BYTES = {DXT1: 8, DXT5: 16, BC6H: 16, BC7: 16, BC4: 8, BC5: 16}
DECODERS = {
    DXT1: texture2ddecoder.decode_bc1,
    DXT5: texture2ddecoder.decode_bc3,
    BC6H: texture2ddecoder.decode_bc6,   # LDR (clamped) decode; good enough for the one HDR cubemap
    BC7: texture2ddecoder.decode_bc7,
    BC4: texture2ddecoder.decode_bc4,
    BC5: texture2ddecoder.decode_bc5,
}
CONVERTIBLE = set(DECODERS) | {DXT1_CRUNCHED, DXT5_CRUNCHED}
TEXTURE_CLASSES = ('Texture2D', 'Cubemap')
QUALITY = 60

_ctx = {}


def _context(block):
    if block not in _ctx:
        cfg = astc_encoder.ASTCConfig(astc_encoder.ASTCProfile.LDR, block, block, block_z=1, quality=QUALITY,
                                      flags=astc_encoder.ASTCConfigFlags.USE_DECODE_UNORM8)
        _ctx[block] = astc_encoder.ASTCContext(cfg)
    return _ctx[block]


def _blocks(n, b=4):
    return max(1, (n + b - 1) // b)


def mip_sizes(w, h, mips):
    out = []
    for _ in range(mips):
        out.append((w, h))
        w, h = max(1, w >> 1), max(1, h >> 1)
    return out


def _bgra_to_rgba(buf):
    b = bytearray(buf)
    b[0::4], b[2::4] = b[2::4], b[0::4]
    return bytes(b)


def _crop(rgba, pw, w, h):
    """Top-left w x h of a pw-wide RGBA image (the decoders work on whole 4x4 blocks)."""
    if pw == w and len(rgba) == w * h * 4:
        return rgba
    row = w * 4
    return b''.join(rgba[y * pw * 4:y * pw * 4 + row] for y in range(h))


def target_format(fmt, w, h):
    base = {DXT1_CRUNCHED: DXT1, DXT5_CRUNCHED: DXT5}.get(fmt, fmt)
    if base == DXT1 and max(w, h) >= 512:
        return ASTC_6x6
    return ASTC_4x4


def astc_size(fmt, w, h, mips):
    b = ASTC_BLOCK[fmt]
    return sum(_blocks(mw, b) * _blocks(mh, b) * 16 for mw, mh in mip_sizes(w, h, mips))


def transcode(fmt, w, h, mips, images, data):
    """Returns (astc_format, astc_bytes, mip_count) for one texture (all images/faces, all mips)."""
    mips = max(1, mips)
    images = max(1, images)
    if fmt in (DXT1_CRUNCHED, DXT5_CRUNCHED):
        # Unity crunch: unpack to plain DXT. Only the top level is reliably recoverable -> no mips.
        data = texture2ddecoder.unpack_unity_crunch(bytes(data))
        fmt = DXT1 if fmt == DXT1_CRUNCHED else DXT5
        mips = 1
    if fmt not in DECODERS:
        raise ValueError('not a BC format: %d' % fmt)
    tfmt = target_format(fmt, w, h)
    block = ASTC_BLOCK[tfmt]
    sizes = mip_sizes(w, h, mips)
    src_img = sum(_blocks(mw) * _blocks(mh) * BLOCK_BYTES[fmt] for mw, mh in sizes)
    if len(data) < src_img * images:
        raise ValueError(f'{len(data)} bytes < {images} x {src_img}')
    ctx = _context(block)
    swz = astc_encoder.ASTCSwizzle.from_str('RGBA')
    decode = DECODERS[fmt]
    out = bytearray()
    pos = 0
    for _ in range(images):
        for mw, mh in sizes:
            pw, ph = _blocks(mw) * 4, _blocks(mh) * 4
            n = _blocks(mw) * _blocks(mh) * BLOCK_BYTES[fmt]
            rgba = _bgra_to_rgba(decode(bytes(data[pos:pos + n]), pw, ph))
            pos += n
            if block != 4:
                rgba = _crop(rgba, pw, mw, mh)
                iw, ih = mw, mh
            else:
                iw, ih = pw, ph       # 4x4 blocks: block-padded image has exactly the right block count
            out += ctx.compress(astc_encoder.ASTCImage(astc_encoder.ASTCType.U8, iw, ih, 1, rgba), swz)
    expect = astc_size(tfmt, w, h, mips) * images
    if len(out) != expect:
        raise ValueError(f'ASTC size {len(out)} != expected {expect}')
    return tfmt, bytes(out), mips
