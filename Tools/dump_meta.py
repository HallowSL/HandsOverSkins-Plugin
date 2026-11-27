import struct, sys, json

d = open(sys.argv[1], 'rb').read()
out = sys.argv[2]
sec = {}
names = ('stringLiterals stringLiteralData strings events properties methods paramDefaults fieldDefaults '
         'fieldParamDefaultData fieldMarshaledSizes parameters fields genericParameters genericParamConstraints '
         'genericContainers nestedTypes interfaces vtableMethods interfaceOffsets typeDefinitions images assemblies').split()
off = 8
for n in names:
    sec[n] = struct.unpack_from('<3I', d, off); off += 12

def s(idx):
    b = sec['strings'][0] + idx
    e = d.index(b'\0', b)
    return d[b:e].decode('utf8', 'replace')

# images (36 bytes) - try layout: name, assembly, typeStart, typeCount, exportedStart, exportedCount, entry, token, caStart? (one fewer)
imgs = []
o, sz, c = sec['images']
for i in range(c):
    p = o + i * 36
    n, a = struct.unpack_from('<2I', d, p)
    ts, = struct.unpack_from('<H', d, p + 8)
    tc, = struct.unpack_from('<I', d, p + 10)
    imgs.append(dict(name=s(n), typeStart=ts, typeCount=tc))

# type defs (82 bytes)
tds = []
o, sz, c = sec['typeDefinitions']
for i in range(c):
    p = o + i * 82
    nameI, nsI, byval, decl, parent = struct.unpack_from('<5I', d, p); p += 20
    gc, = struct.unpack_from('<H', d, p); p += 2
    flags, fieldStart, methodStart, eventStart, propStart, nestedStart, ifStart, vtStart, ioStart = struct.unpack_from('<9I', d, p); p += 36
    mc, pc, fc, ec, nc, vc, ic, ioc = struct.unpack_from('<8H', d, p); p += 16
    bitfield, token = struct.unpack_from('<2I', d, p)
    tds.append(dict(name=s(nameI), ns=s(nsI), parent=parent, decl=decl, flags=flags, fieldStart=fieldStart, fc=fc,
                    methodStart=methodStart, mc=mc, propStart=propStart, pc=pc, nestedStart=nestedStart, nc=nc, byval=byval))

fo = sec['fields'][0]
def field(i):
    n, t, tok = struct.unpack_from('<3I', d, fo + i * 12)
    return s(n), t
mo = sec['methods'][0]
def method(i):
    p = mo + i * 32
    n, = struct.unpack_from('<I', d, p)
    decl, = struct.unpack_from('<H', d, p + 4)
    ret, rtok, pstart = struct.unpack_from('<3I', d, p + 6)
    gc, = struct.unpack_from('<H', d, p + 18)
    tok, = struct.unpack_from('<I', d, p + 20)
    fl, ifl, slot, pcnt = struct.unpack_from('<4H', d, p + 24)
    return s(n), pstart, pcnt, fl, ret
po = sec['parameters'][0]
def param(i):
    n, tok, t = struct.unpack_from('<3I', d, po + i * 12)
    return s(n), t
pro = sec['properties'][0]
def prop(i):
    n, g, st, a, tok = struct.unpack_from('<5I', d, pro + i * 20)
    return s(n)

res = []
for im in imgs:
    for ti in range(im['typeStart'], im['typeStart'] + im['typeCount']):
        t = tds[ti]
        e = dict(asm=im['name'], idx=ti, ns=t['ns'], name=t['name'], decl=t['decl'], byval=t['byval'],
                 fields=[field(t['fieldStart'] + k)[0] for k in range(t['fc'])] if t['fieldStart'] != 0xFFFFFFFF else [],
                 props=[prop(t['propStart'] + k) for k in range(t['pc'])] if t['propStart'] != 0xFFFFFFFF else [],
                 methods=[])
        if t['methodStart'] != 0xFFFFFFFF:
            for k in range(t['mc']):
                n, ps, pc_, fl, ret = method(t['methodStart'] + k)
                e['methods'].append(n + '(' + ', '.join(param(ps + j)[0] for j in range(pc_)) + ')')
        res.append(e)
json.dump(dict(images=imgs, types=res), open(out, 'w'))
print(len(imgs), len(res))

# field defaults: fieldIndex, typeIndex, dataIndex
fdo, fdsz, fdc = sec['fieldDefaults']
dataBase = sec['fieldParamDefaultData'][0]
fdefs = {}
for i in range(fdc):
    fi, ti, di = struct.unpack_from('<3I', d, fdo + i * 12)
    fdefs[fi] = (ti, di)
# string literals
slo = sec['stringLiterals'][0]; sldo = sec['stringLiteralData'][0]
lits = []
for i in range(sec['stringLiterals'][2]):
    di, = struct.unpack_from('<I', d, slo + i * 4)
    nxt = struct.unpack_from('<I', d, slo + (i + 1) * 4)[0] if i + 1 < sec['stringLiterals'][2] else sec['stringLiteralData'][1]
    lits.append(d[sldo + di: sldo + nxt].decode('utf8', 'replace'))
json.dump(lits, open(out.replace('.json', '_lits.json'), 'w'))
# per-type field default raw bytes (first 64 bytes)
fd = {}
for t in tds:
    pass
import base64
res2 = {}
for e in res:
    t = tds[e['idx']]
    if t['fieldStart'] == 0xFFFFFFFF: continue
    for k in range(t['fc']):
        fi = t['fieldStart'] + k
        if fi in fdefs:
            ti, di = fdefs[fi]
            res2.setdefault(e['ns'] + '.' + e['name'], {})[field(fi)[0]] = d[dataBase + di: dataBase + di + 48].hex()
json.dump(res2, open(out.replace('.json', '_fdefs.json'), 'w'))
