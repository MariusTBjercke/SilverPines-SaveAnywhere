"""Disassemble IL2CPP methods by name, annotating direct calls with method names.
usage: dis.py <srcdir> <GameAssembly.dll> <Class::Method|0xRVA> [...]
       dis.py <srcdir> - --fields <Class>      field offsets of a class
       dis.py <srcdir> - --find <regex>        list methods (Class::Method) and their RVAs
<srcdir>: ilspycmd -p output of Cpp2IL dummy DLLs (with [Address]/[FieldOffset] attributes).
A symbol cache (symcache.pkl) is built in <srcdir> on first run; delete it to rebuild.
"""
import re, sys, os, pickle, pefile, capstone

src, dll = sys.argv[1], sys.argv[2]
cache = os.path.join(src, 'symcache.pkl')

def build():
    syms, byname, fields = {}, {}, {}
    addr_re = re.compile(r'\[Address\(RVA = "0x([0-9A-F]+)".*Length = "0x([0-9A-F]+)"')
    off_re = re.compile(r'\[FieldOffset\(Offset = "0x([0-9A-F]+)"\)\]')
    cls_re = re.compile(r'^\s*(?:\[.*\]\s*)?(?:public|private|internal|protected)?\s*(?:sealed |static |abstract |unsafe |partial )*(?:class|struct)\s+([^\s:<]+(?:<[^>]*>)?[^\s:]*)')
    name_re = re.compile(r'([A-Za-z0-9_<>.,\[\] ]+?)\s*\(')
    for root, _, files in os.walk(src):
        for f in files:
            if not f.endswith('.cs'): continue
            stack, depth, pending, pend_off = [], 0, None, None
            for line in open(os.path.join(root, f), encoding='utf-8', errors='replace'):
                m = cls_re.match(line)
                if m and '(' not in line:
                    stack.append((m.group(1), depth))
                m = addr_re.search(line)
                if m: pending = (int(m.group(1), 16), int(m.group(2), 16)); continue
                m = off_re.search(line)
                if m: pend_off = int(m.group(1), 16); continue
                s = line.strip()
                if pending and s and not s.startswith('['):
                    nm = name_re.search(s.split('=')[0] if '(' not in s else s)
                    meth = (nm.group(1).split()[-1] if nm else s[:60])
                    cls = '.'.join(c for c, _ in stack) or f[:-3]
                    full = f'{cls}::{meth}'
                    syms.setdefault(pending[0], (full, pending[1]))
                    byname.setdefault(full, []).append(pending[0])
                    pending = None
                if pend_off is not None and s and not s.startswith('['):
                    cls = '.'.join(c for c, _ in stack) or f[:-3]
                    fname = re.sub(r'[;{].*', '', s).split()[-1]
                    fields.setdefault(cls, {})[pend_off] = fname
                    pend_off = None
                depth += line.count('{') - line.count('}')
                while stack and depth <= stack[-1][1]:
                    stack.pop()
    pickle.dump((syms, byname, fields), open(cache, 'wb'))
    return syms, byname, fields

syms, byname, fields = pickle.load(open(cache, 'rb')) if os.path.exists(cache) else build()
args = sys.argv[3:]
if args and args[0] == '--fields':
    for k, v in fields.items():
        if k.split('.')[-1] == args[1] or k == args[1]:
            print(k); [print(f'  0x{o:X} {n}') for o, n in sorted(v.items())]
    sys.exit()
if args and args[0] == '--find':
    for k in byname:
        if re.search(args[1], k): print(k, ' '.join(hex(a) for a in byname[k]))
    sys.exit()

pe = pefile.PE(dll, fast_load=True)
base = pe.OPTIONAL_HEADER.ImageBase
md = capstone.Cs(capstone.CS_ARCH_X86, capstone.CS_MODE_64)
img = pe.get_memory_mapped_image()
for a in args:
    if a.startswith('0x'): rvas = [int(a, 16)]
    else: rvas = [r for k, v in byname.items() if k == a or k.endswith('.' + a) for r in v]
    for rva in rvas:
        name, ln = syms.get(rva, (a, 0x400))
        print(f'===== {name} @ 0x{rva:X} len 0x{ln:X}')
        for ins in md.disasm(img[rva:rva + ln], base + rva):
            note = ''
            if ins.mnemonic in ('call', 'jmp') and ins.op_str.startswith('0x'):
                t = int(ins.op_str, 16) - base
                if t in syms: note = '  ; ' + syms[t][0]
            m = re.search(r'\[rip ([+-]) (0x[0-9a-f]+)\]', ins.op_str)
            if m:
                tgt = ins.address + ins.size + (1 if m.group(1) == '+' else -1) * int(m.group(2), 16)
                note += f'  ; [0x{tgt - base:X}]'
            print(f'  {ins.address - base:8X}: {ins.mnemonic:6} {ins.op_str}{note}')
