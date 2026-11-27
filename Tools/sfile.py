"""Minimal Unity SerializedFile (v22) reader - objects table + externals, no typetree decode."""
import struct, sys

class R:
    def __init__(s, d, o=0, be=True): s.d, s.o, s.be = d, o, be
    def u(s, fmt, n):
        v = struct.unpack_from(('>' if s.be else '<') + fmt, s.d, s.o)[0]; s.o += n; return v
    def i32(s): return s.u('i', 4)
    def u32(s): return s.u('I', 4)
    def i64(s): return s.u('q', 8)
    def u64(s): return s.u('Q', 8)
    def i16(s): return s.u('h', 2)
    def u8(s): v = s.d[s.o]; s.o += 1; return v
    def cstr(s):
        e = s.d.index(b'\0', s.o); v = s.d[s.o:e].decode('utf8', 'replace'); s.o = e + 1; return v
    def align(s, n=4): s.o = (s.o + n - 1) // n * n
    def str_(s):
        n = s.i32(); v = s.d[s.o:s.o + n].decode('utf8', 'replace'); s.o += n; s.align(); return v

def load(path):
    d = open(path, 'rb').read()
    r = R(d)
    r.o = 8; ver = r.u32()
    assert ver >= 22, ver
    r.o = 16; endian = r.u8(); r.o += 3
    msize = r.u32(); fsize = r.u64(); dataOff = r.u64(); r.u64()
    r.be = endian != 0
    unity = r.cstr(); platform = r.i32(); typetree = r.u8()
    ntypes = r.i32(); types = []
    for i in range(ntypes):
        cid = r.i32(); stripped = r.u8(); sidx = r.i16()
        sh = None
        if cid == 114: sh = d[r.o:r.o + 16].hex(); r.o += 16
        r.o += 16
        if typetree: raise NotImplementedError('typetree')
        types.append(dict(cid=cid, sidx=sidx, scriptHash=sh))
    nobj = r.i32(); objs = []
    for i in range(nobj):
        r.align(4)
        pid = r.i64(); boff = r.i64(); bsize = r.u32(); tidx = r.i32()
        objs.append(dict(pid=pid, off=dataOff + boff, size=bsize, cid=types[tidx]['cid'], t=tidx))
    nscript = r.i32(); scripts = []
    for i in range(nscript):
        fi = r.i32(); r.align(4); pid = r.i64(); scripts.append((fi, pid))
    next_ = r.i32(); ext = []
    for i in range(next_):
        r.cstr(); r.o += 16; r.i32(); ext.append(r.cstr())
    return dict(data=d, unity=unity, types=types, objs=objs, scripts=scripts, ext=ext)

def mb_header(f, o):
    r = R(f['data'], o['off'], be=False)
    go = (r.i32(), r.i64()); en = r.u8(); r.align()
    sc = (r.i32(), r.i64()); name = r.str_()
    return dict(go=go, script=sc, name=name, body=r.o)

if __name__ == '__main__':
    f = load(sys.argv[1])
    print(f['unity'], len(f['objs']), f['ext'][:10])
