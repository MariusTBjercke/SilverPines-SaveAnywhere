"""List identifier-like strings in a binary (e.g. global-metadata.dat) matching a regex, case-insensitive.
usage: strs.py <file> <regex>
Much faster than grep -a on the 19 MB metadata file.
"""
import re, sys

data = open(sys.argv[1], 'rb').read()
pat = re.compile(sys.argv[2].encode(), re.I)
found = {m.group().decode('latin1')
         for m in re.finditer(rb'[A-Za-z_][A-Za-z0-9_ .:<>/]{3,80}', data)
         if pat.search(m.group())}
for s in sorted(found):
    print(s)
