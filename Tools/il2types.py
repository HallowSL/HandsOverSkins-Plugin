"""Resolve IL2CPP field types for selected classes using GameAssembly.dll + metadata dump."""
import struct, json, sys
d = open('/mnt/user-data/uploads/HANDS OVER/GameAssembly.dll', 'rb').read()
md = open('/mnt/user-data/uploads/HANDS OVER/Hands Over_Data/il2cpp_data/Metadata/global-metadata.dat', 'rb').read()
IB = 0x180000000
pe = struct.unpack_from('<I', d, 0x3c)[0]
nsec = struct.unpack_from('<H', d, pe + 6)[0]; optsz = struct.unpack_from('<H', d, pe + 20)[0]
secs = []
for i in range(nsec):
    o = pe + 24 + optsz + i * 40
    vsz, va, rsz, rptr = struct.unpack_from('<4I', d, o + 8)
    secs.append((va, vsz, rptr, rsz))
def v2o(va):
    rva = va - IB
    for sva, vsz, rptr, rsz in secs:
        if sva <= rva < sva + max(vsz, rsz):
            return rptr + rva - sva
    raise ValueError(hex(va))
def q(off): return struct.unpack_from('<Q', d, off)[0]

reg = 0x48679c0 - 80
typesCount = q(reg + 48); typesPtr = q(reg + 56)
gclsCount = q(reg); gclsPtr = q(reg + 8)
ginstCount = q(reg + 16); ginstPtr = q(reg + 24)
types_arr = v2o(typesPtr)

# metadata sections
sec = {}
names = ('stringLiterals stringLiteralData strings events properties methods paramDefaults fieldDefaults '
         'fieldParamDefaultData fieldMarshaledSizes parameters fields genericParameters genericParamConstraints '
         'genericContainers nestedTypes interfaces vtableMethods interfaceOffsets typeDefinitions images assemblies').split()
off = 8
for n in names:
    sec[n] = struct.unpack_from('<3I', md, off); off += 12
def s(idx):
    b = sec['strings'][0] + idx; e = md.index(b'\0', b); return md[b:e].decode()
def tdname(i):
    p = sec['typeDefinitions'][0] + i * 82
    n, ns, byval, decl = struct.unpack_from('<4I', md, p)
    nm = s(n)
    if decl != 0xFFFFFFFF:
        try: return typeat(q(types_arr + decl * 8)) + '/' + nm
        except Exception: return '?/' + nm
    nsn = s(ns)
    return (nsn + '.' if nsn else '') + nm
def gparam_name(i):
    p = sec['genericParameters'][0] + i * 14
    owner, nm = struct.unpack_from('<2I', md, p)
    return s(nm)

PRIM = {1: 'void', 2: 'bool', 3: 'char', 4: 'sbyte', 5: 'byte', 6: 'short', 7: 'ushort', 8: 'int', 9: 'uint', 10: 'long',
        11: 'ulong', 12: 'float', 13: 'double', 14: 'string', 24: 'IntPtr', 25: 'UIntPtr', 28: 'object'}
def typeat(ptr, depth=0):
    o = v2o(ptr)
    data = q(o); bits = struct.unpack_from('<I', d, o + 8)[0]
    t = (bits >> 16) & 0xff
    if t in PRIM: return PRIM[t]
    if t in (0x11, 0x12):  # valuetype / class -> data = typeDefIndex
        return tdname(data & 0xffffffff)
    if t == 0x1d:  # szarray
        return typeat(data, depth + 1) + '[]'
    if t == 0x0f: return typeat(data, depth + 1) + '*'
    if t == 0x15:  # genericinst: data -> Il2CppGenericClass {typeDefinitionIndex/type ptr, context{class_inst, method_inst}, cached}
        go = v2o(data)
        tptr = q(go)
        base = typeat(tptr, depth + 1)
        cinst = q(go + 8)
        io = v2o(cinst)
        argc = q(io); argv = q(io + 8)
        args = [typeat(q(v2o(argv) + 8 * k), depth + 1) for k in range(argc)]
        return base.split('`')[0] + '<' + ', '.join(args) + '>'
    if t in (0x13, 0x1e):
        return 'T?'
    return 'type%x' % t
def typename(i):
    return tdname(i)

def fieldtypes(tdi):
    p = sec['typeDefinitions'][0] + tdi * 82
    fstart, = struct.unpack_from('<I', md, p + 26)
    fc, = struct.unpack_from('<H', md, p + 62)
    out = []
    for k in range(fc):
        n, ti, tok = struct.unpack_from('<3I', md, sec['fields'][0] + (fstart + k) * 12)
        try:
            ty = typeat(q(types_arr + ti * 8))
        except Exception as e:
            ty = '?' + str(e)
        out.append((s(n), ty))
    return out

if __name__ == '__main__':
    print('typesCount', typesCount)
    for a in sys.argv[1:]:
        i = int(a)
        print('==', tdname(i))
        for n, t in fieldtypes(i):
            print('   ', t, n)

def methods(tdi):
    p = sec['typeDefinitions'][0] + tdi * 82
    mstart, = struct.unpack_from('<I', md, p + 30)
    mc, = struct.unpack_from('<H', md, p + 58)
    out = []
    for k in range(mc):
        mp = sec['methods'][0] + (mstart + k) * 32
        n, = struct.unpack_from('<I', md, mp)
        ret, rtok, pstart = struct.unpack_from('<3I', md, mp + 6)
        fl, ifl, slot, pc = struct.unpack_from('<4H', md, mp + 24)
        def T(ti):
            try: return typeat(q(types_arr + ti * 8))
            except Exception as e: return '?'
        ps = []
        for j in range(pc):
            pn, ptok, pti = struct.unpack_from('<3I', md, sec['parameters'][0] + (pstart + j) * 12)
            bits = struct.unpack_from('<I', d, v2o(q(types_arr + pti * 8)) + 8)[0]
            byref = (bits >> 30) & 1
            ps.append(('ref ' if byref else '') + T(pti) + ' ' + s(pn))
        out.append('%s %s(%s)%s' % (T(ret), s(n), ', '.join(ps), ' static' if fl & 0x10 else ''))
    return out
