"""Minimal UnityFS bundle extractor (LZ4/none) in pure python."""
import struct, sys, os

def lz4_block(src, usize):
    dst = bytearray(); i = 0; n = len(src)
    while i < n:
        tok = src[i]; i += 1
        lit = tok >> 4
        if lit == 15:
            while True:
                b = src[i]; i += 1; lit += b
                if b != 255: break
        dst += src[i:i + lit]; i += lit
        if i >= n: break
        off = src[i] | (src[i + 1] << 8); i += 2
        ml = tok & 15
        if ml == 15:
            while True:
                b = src[i]; i += 1; ml += b
                if b != 255: break
        ml += 4
        st = len(dst) - off
        for k in range(ml): dst.append(dst[st + k])
    return bytes(dst)

def dec(buf, flags, usize):
    c = flags & 0x3f
    if c == 0: return buf
    if c in (2, 3): return lz4_block(buf, usize)
    raise NotImplementedError('compression %d' % c)

def extract(path):
    d = open(path, 'rb').read()
    o = d.index(b'\0') + 1
    fmt = struct.unpack_from('>I', d, o)[0]; o += 4
    o = d.index(b'\0', o) + 1; o = d.index(b'\0', o) + 1
    size, csz, usz, flags = struct.unpack_from('>qIII', d, o); o += 20
    if fmt >= 7: o = (o + 15) // 16 * 16
    if flags & 0x80:
        bi = d[len(d) - csz:]
    else:
        bi = d[o:o + csz]; o += csz
    if flags & 0x200: o = (o + 15) // 16 * 16
    bi = dec(bi, flags, usz)
    r = 16
    nb = struct.unpack_from('>i', bi, r)[0]; r += 4
    blocks = []
    for _ in range(nb):
        u, c, f = struct.unpack_from('>IIH', bi, r); r += 10; blocks.append((u, c, f))
    nn = struct.unpack_from('>i', bi, r)[0]; r += 4
    nodes = []
    for _ in range(nn):
        off, sz, fl = struct.unpack_from('>qqI', bi, r); r += 20
        e = bi.index(b'\0', r); name = bi[r:e].decode(); r = e + 1
        nodes.append((off, sz, name))
    data = bytearray()
    for u, c, f in blocks:
        data += dec(d[o:o + c], f, u); o += c
    return {name: bytes(data[off:off + sz]) for off, sz, name in nodes}

if __name__ == '__main__':
    files = extract(sys.argv[1])
    os.makedirs(sys.argv[2], exist_ok=True)
    for n, b in files.items():
        open(os.path.join(sys.argv[2], os.path.basename(n)), 'wb').write(b); print(n, len(b))
