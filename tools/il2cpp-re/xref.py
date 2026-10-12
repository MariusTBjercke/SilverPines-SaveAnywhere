"""Find direct call/jmp sites (E8/E9 rel32) that target the given RVAs.
usage: xref.py <srcdir> <GameAssembly.dll> 0xRVA [0xRVA ...]
<srcdir> is the same decompiled dummy-DLL source folder dis.py uses (its symbol cache is reused).
Only finds direct calls; virtual calls and calls through MethodInfo/delegates won't show up.
"""
import bisect, os, pickle, re, sys
import pefile

src, dll = sys.argv[1], sys.argv[2]
cache = os.path.join(src, 'symcache.pkl')
if not os.path.exists(cache):
    sys.exit('Run dis.py once first to build the symbol cache.')
syms, byname, fields = pickle.load(open(cache, 'rb'))

pe = pefile.PE(dll, fast_load=True)
img = pe.get_memory_mapped_image()
# IL2CPP code lives in the 'il2cpp' section (sometimes '.text'); scan every executable section.
code = [s for s in pe.sections if s.Characteristics & 0x20000000]
starts = sorted(syms)

for t in [int(x, 16) for x in sys.argv[3:]]:
    print('== xrefs to', hex(t), syms.get(t, ('?',))[0])
    for sec in code:
        lo = sec.VirtualAddress
        data = img[lo:lo + sec.Misc_VirtualSize]
        for m in re.finditer(rb'[\xe8\xe9]', data):
            p = lo + m.start()
            rel = int.from_bytes(data[m.start() + 1:m.start() + 5], 'little', signed=True)
            if p + 5 + rel != t:
                continue
            k = bisect.bisect_right(starts, p) - 1
            s = starts[k] if k >= 0 else None
            if s is not None and p < s + syms[s][1]:
                print(f'  0x{p:X} in {syms[s][0]} @0x{s:X}')
            else:
                print(f'  0x{p:X} (unknown function)')
