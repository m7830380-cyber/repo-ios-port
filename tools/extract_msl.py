"""Dump the unique Metal sources from converted shaders (work/cache/shaders) for offline compile checks."""
import os, pickle, sys, hashlib, tarfile, io
sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
from convert_shader import platform_blob, entries_of, get_seg
from ukblob import read_sub

cache = sys.argv[1] if len(sys.argv) > 1 else 'work/cache/shaders'
out = sys.argv[2] if len(sys.argv) > 2 else 'work/msl.tar.gz'
seen = set()
with tarfile.open(out, 'w:gz') as tar:
    for f in sorted(os.listdir(cache)):
        nt, stats = pickle.load(open(os.path.join(cache, f), 'rb'))
        name = nt['m_ParsedForm']['m_Name'].replace('/', '_').replace(' ', '_')
        chunks = platform_blob(nt, nt['compressedBlob'], 0)
        for k, e in enumerate(entries_of(chunks)):
            seg = get_seg(chunks, e)
            try:
                s = read_sub(seg)
            except Exception:
                continue
            if s['type'] not in (23, 24):
                continue
            src = s['code'][60:]
            h = hashlib.sha1(src).hexdigest()
            if h in seen:
                continue
            seen.add(h)
            ti = tarfile.TarInfo(f'msl/{name}/{k}_{"vs" if s["type"] == 23 else "fs"}.metal')
            ti.size = len(src)
            tar.addfile(ti, io.BytesIO(src))
print('unique sources', len(seen))
