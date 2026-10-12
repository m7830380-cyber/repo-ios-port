"""Convert a Unity 2022.3 Shader typetree (with DX11 programs) to a Metal-only Shader typetree."""
import os, re, json, struct, subprocess, tempfile, hashlib, copy
import lz4.block
from ukblob import *
from dx2metal import merge_params, prep_dxbc

HERE = os.path.dirname(os.path.abspath(__file__))
HLSLCC = os.environ.get('HLSLCC_CLI') or os.path.join(HERE, 'hlslcc_cli', 'build', 'Release', 'hlslcc_cli.exe')
BLOB_VERSION = 202012090
DX_TYPES = set(range(15, 23))
DX_VS = {15, 16}
DX_PS = {17, 18}
METAL_VS, METAL_FS = 23, 24
HLSLCC_FLAGS = 0x20000 | 0x80000   # TRANSLATE_MATRICES | METAL_SHADOW_SAMPLER_LINEAR

SEM_SRC = {'POSITION': 0, 'NORMAL': 1, 'TANGENT': 2, 'COLOR': 3, 'BLENDWEIGHTS': 12, 'BLENDWEIGHT': 12, 'BLENDINDICES': 13}


def metal_code(msl):
    name = b'xlatMtlMain\0'
    hdr = struct.pack('<IIIIIIIIIiiI', 0xF00DCAFE, 48, 6, 48 + len(name), 0, 0, 0, 0, 0, 37, -1, 0)
    return hdr + name + msl


class ConvertError(Exception):
    pass


# ---------------- platform blob io ----------------
def platform_blob(tt, blob, pi):
    """Return list of decompressed chunks for platform index pi."""
    chunks = []
    for j in range(len(tt['offsets'][pi])):
        off = tt['offsets'][pi][j]
        cl = tt['compressedLengths'][pi][j]
        dl = tt['decompressedLengths'][pi][j]
        chunks.append(lz4.block.decompress(blob[off:off + cl], uncompressed_size=dl))
    return chunks


def entries_of(chunks):
    d = chunks[0]
    n = struct.unpack_from('<i', d, 0)[0]
    return [struct.unpack_from('<iii', d, 4 + 12 * k) for k in range(n)]


def get_seg(chunks, ent):
    o, l, s = ent
    return chunks[s][o:o + l]


def pack_platform(segs, chunk_limit=48 << 20):
    """Pack segments into chunked blob with entry table in chunk 0. Returns list of raw chunks."""
    n = len(segs)
    table_size = 4 + 12 * n
    chunks = [bytearray(table_size)]
    ents = []
    for s in segs:
        if len(chunks[-1]) + len(s) > chunk_limit and len(chunks[-1]) > (table_size if len(chunks) == 1 else 0):
            chunks.append(bytearray())
        ents.append((len(chunks[-1]), len(s), len(chunks) - 1))
        chunks[-1] += s
    struct.pack_into('<i', chunks[0], 0, n)
    for k, e in enumerate(ents):
        struct.pack_into('<iii', chunks[0], 4 + 12 * k, *e)
    return [bytes(c) for c in chunks]


# ---------------- reflection -> metal params ----------------
def metal_layout(c):
    """size, align, stride for a reflected constant."""
    n = max(1, c['arraySize'])
    if c['isMatrix']:
        cnt = c['cols'] * n
        return 16 * cnt, 16
    cols = c['cols']
    if cols == 1:
        return 4 * n, 4
    if cols == 2:
        return 8 * n, 8
    return 16 * n, 16


def parse_refl(path):
    s = open(path, 'rb').read().decode('utf8', 'replace')
    return json.loads(s)


def build_metal_params(refl, msl, P, cbname_map):
    """Build a Metal param segment from HLSLcc reflection + original DX params P."""
    orig_vars = {}
    for cn, cb in P['cbs'].items():
        for v in cb['vars']:
            orig_vars[(cn, v['name'])] = v
    cbs = [dict(name='', size=0, params=[], structs=[])]
    binds = []
    cur = None
    off = 0
    for r in refl['refl']:
        k = r['k']
        if k == 'cb':
            cur = dict(name=r['name'], size=0, params=[], structs=[])
            cbs.append(cur)
            off = 0
        elif k == 'const':
            if cur is None:
                raise ConvertError('const outside cb')
            size, align = metal_layout(r)
            off = (off + align - 1) // align * align
            ocb = cbname_map.get(cur['name'], cur['name'])
            ov = orig_vars.get((ocb, r['name']))
            if ov is None:
                raise ConvertError('unknown const %s.%s' % (ocb, r['name']))
            cur['params'].append(dict(name=r['name'], type=ov['type'], rows=ov['rows'], cols=ov['cols'],
                                      isMatrix=ov['isMatrix'], arraySize=ov['arraySize'], index=off))
            off += size
            cur['size'] = off
        elif k == 'cbbind':
            binds.append(dict(kind='cb', name=r['name'], index=r['index'], arraySize=1))
        elif k == 'tex':
            ot = next((t for t in P['tex'] if t['name'] == r['name']), None)
            if r['uav']:
                ou = next((u for u in P['uav'] if u['name'] == r['name']), None)
                binds.append(dict(kind='uav', name=r['name'], index=r['index'], orig=ou['orig'] if ou else r['index']))
                continue
            if ot is None:
                raise ConvertError('unknown texture %s' % r['name'])
            samp = r['sampler'] if r['sampler'] >= 0 else -1
            binds.append(dict(kind='tex', name=r['name'], index=r['index'], sampler=samp,
                              extra=(ot['dim'] << 1) | (ot['ms'] & 1)))
        elif k == 'buf':
            if r['uav']:
                ou = next((u for u in P['uav'] if u['name'] == r['name']), None)
                binds.append(dict(kind='uav', name=r['name'], index=r['index'], orig=ou['orig'] if ou else r['index']))
            else:
                ob = next((b for b in P['buf'] if b['name'] == r['name']), None)
                binds.append(dict(kind='buf', name=r['name'], index=r['index'], arraySize=ob['arraySize'] if ob else 0))
    # inline samplers
    for m in re.finditer(rb'sampler sampler_inline(\d+) \[\[ sampler \((\d+)\) \]\]', msl):
        dxslot = int(m.group(1))
        s = next((x for x in P['samplers'] if x['bind'] == dxslot), None)
        if s is not None:
            binds.append(dict(kind='sampler', name='', bind=int(m.group(2)), sampler=s['sampler']))
    # texture sampler slots that HLSLcc renamed (sampler_unknownN) are fine - bound by texture sampler index
    return write_params(dict(version=BLOB_VERSION, cbs=cbs, binds=binds, tail=b''))


def vertex_channels(refl):
    ch = []
    smap = 0
    for r in refl['refl']:
        if r['k'] != 'input':
            continue
        m = re.match(r'([A-Z_]+?)(\d*)$', r['name'])
        sem, idx = m.group(1), int(m.group(2) or 0)
        if sem == 'TEXCOORD':
            src = 4 + idx
        elif sem in SEM_SRC:
            src = SEM_SRC[sem]
        else:
            raise ConvertError('unknown vertex semantic %s' % r['name'])
        ch.append((src, 13 + r['index']))
        smap |= 1 << src
    return smap, ch


def fix_msl(msl, is_vs):
    if is_vs:
        msl = msl.replace(b'[[ position ]]', b'[[ position, invariant ]]')
    return msl


# ---------------- main conversion ----------------
PROGS = ('progVertex', 'progFragment', 'progGeometry', 'progHull', 'progDomain')


class HlslccBatch:
    def __init__(self, workdir):
        self.dir = workdir
        os.makedirs(workdir, exist_ok=True)
        self.jobs = []

    def add(self, key, dxbc):
        p = os.path.join(self.dir, key)
        open(p + '.dxbc', 'wb').write(dxbc)
        self.jobs.append(key)

    def run(self, nproc=None):
        nproc = nproc or max(1, os.cpu_count() - 1)
        jobs = self.jobs
        if not jobs:
            return
        parts = [jobs[i::nproc] for i in range(nproc)]
        procs = []
        for i, part in enumerate(parts):
            if not part:
                continue
            lf = os.path.join(self.dir, 'list%d.txt' % i)
            with open(lf, 'w') as f:
                for key in part:
                    p = os.path.join(self.dir, key)
                    f.write(f'{p}.dxbc\t{p}.metal\t{p}.json\t{HLSLCC_FLAGS}\n')
            procs.append(subprocess.Popen([HLSLCC, lf], stderr=subprocess.DEVNULL))
        for p in procs:
            p.wait()
        # isolate crashers: rerun any job without output individually
        for key in jobs:
            p = os.path.join(self.dir, key)
            if os.path.exists(p + '.json'):
                continue
            lf = os.path.join(self.dir, 'single.txt')
            with open(lf, 'w') as f:
                f.write(f'{p}.dxbc\t{p}.metal\t{p}.json\t{HLSLCC_FLAGS}\n')
            subprocess.run([HLSLCC, lf], stderr=subprocess.DEVNULL)

    def result(self, key):
        p = os.path.join(self.dir, key)
        if not os.path.exists(p + '.json'):
            return None, None
        refl = parse_refl(p + '.json')
        if not refl['ok']:
            return None, refl
        return open(p + '.metal', 'rb').read(), refl


def convert_shader(tt, blob, workdir, log=print):
    """tt: Shader typetree dict (compressedBlob removed), blob: raw compressed blob bytes.
    Returns (new_tt_without_blob, new_blob_bytes) or None if no DX11 program."""
    if 4 not in tt['platforms']:
        return None
    pi = tt['platforms'].index(4)
    chunks = platform_blob(tt, blob, pi)
    ents = entries_of(chunks)
    name = tt['m_ParsedForm']['m_Name']
    batch = HlslccBatch(workdir)
    plan = []   # (pass, prog, slot, entry, key)
    keymap = {}
    stats = dict(ok=0, fail=0, dropped=0)
    for si, ss in enumerate(tt['m_ParsedForm']['m_SubShaders']):
        for pa, p in enumerate(ss['m_Passes']):
            names = {v: k for k, v in p['m_NameIndices']}
            for prog in PROGS:
                pr = p[prog]
                for slot, lst in enumerate(pr['m_PlayerSubPrograms']):
                    for ei, e in enumerate(lst):
                        if e['m_GpuProgramType'] not in DX_TYPES:
                            continue
                        if e['m_GpuProgramType'] not in DX_VS | DX_PS:
                            stats['dropped'] += 1
                            continue
                        pbi = pr['m_ParameterBlobIndices'][slot][ei] if pr['m_ParameterBlobIndices'][slot] else -1
                        ck = (si, pa, prog, e['m_BlobIndex'], pbi)
                        if ck not in keymap:
                            key = 'k%d' % len(keymap)
                            keymap[ck] = key
                            try:
                                bp = read_params(get_seg(chunks, ents[pbi])) if pbi >= 0 else None
                                P = merge_params(pr['m_CommonParameters'], names, bp)
                                sub = read_sub(get_seg(chunks, ents[e['m_BlobIndex']]))
                                dx, ptype = prep_dxbc(sub['code'], P)
                                batch.add(key, dx)
                                plan.append((key, P, sub, e['m_GpuProgramType'] in DX_VS))
                            except (ConvertError, NotImplementedError, ValueError) as ex:
                                keymap[ck] = None
                                log('  [%s] prep failed: %s' % (name, ex))
    batch.run()
    # build segments
    segs = []
    results = {}
    cbmap_vs = {'VGlobals': '$Globals'}
    cbmap_fs = {'FGlobals': '$Globals'}
    for key, P, sub, is_vs in plan:
        msl, refl = batch.result(key)
        if msl is None:
            stats['fail'] += 1
            results[key] = None
            continue
        try:
            msl = fix_msl(msl, is_vs)
            pseg = build_metal_params(refl, msl, P, cbmap_vs if is_vs else cbmap_fs)
            if is_vs:
                smap, ch = vertex_channels(refl)
            else:
                smap, ch = 0, []
            msub = dict(version=BLOB_VERSION, type=METAL_VS if is_vs else METAL_FS, stats=[0, 0, 0, 0],
                        keywords=sub['keywords'], code=metal_code(msl), sourceMap=smap, channels=ch, tail=b'')
            segs.append(pseg)
            pidx = len(segs) - 1
            segs.append(write_sub(msub))
            results[key] = (len(segs) - 1, pidx)
            stats['ok'] += 1
        except Exception as ex:
            stats['fail'] += 1
            results[key] = None
            log('  [%s] build failed: %s' % (name, ex))
    # rewrite typetree
    nt = copy.deepcopy(tt)
    for si, ss in enumerate(nt['m_ParsedForm']['m_SubShaders']):
        for pa, p in enumerate(ss['m_Passes']):
            if p['m_Platforms']:
                p['m_Platforms'] = [14] if 4 in p['m_Platforms'] else []
            for prog in PROGS:
                pr = p[prog]
                newsub = []
                newpar = []
                for slot, lst in enumerate(pr['m_PlayerSubPrograms']):
                    nl, npl = [], []
                    for ei, e in enumerate(lst):
                        if e['m_GpuProgramType'] not in DX_VS | DX_PS:
                            continue
                        pbi = pr['m_ParameterBlobIndices'][slot][ei] if pr['m_ParameterBlobIndices'][slot] else -1
                        key = keymap.get((si, pa, prog, e['m_BlobIndex'], pbi))
                        r = results.get(key) if key else None
                        if r is None:
                            continue
                        ne = dict(e)
                        ne['m_BlobIndex'] = r[0]
                        ne['m_GpuProgramType'] = METAL_VS if e['m_GpuProgramType'] in DX_VS else METAL_FS
                        nl.append(ne)
                        npl.append(r[1])
                    newsub.append(nl)
                    newpar.append(npl)
                # iOS players look programs up per graphics tier (slots 0..2); Windows stores them tier-independent in slot 3.
                allsub = [e for l in newsub for e in l]
                allpar = [x for l in newpar for x in l]
                if allsub:
                    pr['m_PlayerSubPrograms'] = [list(allsub), list(allsub), list(allsub), []]
                    pr['m_ParameterBlobIndices'] = [list(allpar), list(allpar), list(allpar), []]
                else:
                    pr['m_PlayerSubPrograms'] = [[] for _ in newsub] if newsub else []
                    pr['m_ParameterBlobIndices'] = [[] for _ in newpar] if newpar else []
                cp = pr['m_CommonParameters']
                for k in cp:
                    cp[k] = []
            p['m_HasInstancingVariant'] = p['m_HasInstancingVariant']
    raw_chunks = pack_platform(segs)
    comp = [lz4.block.compress(c, store_size=False, mode='high_compression') for c in raw_chunks]
    newblob = b''.join(comp)
    offs = []
    o = 0
    for c in comp:
        offs.append(o)
        o += len(c)
    nt['platforms'] = [14]
    nt['offsets'] = [offs]
    nt['compressedLengths'] = [[len(c) for c in comp]]
    nt['decompressedLengths'] = [[len(c) for c in raw_chunks]]
    nt['stageCounts'] = [tt['stageCounts'][pi]] if tt.get('stageCounts') else []
    nt['compressedBlob'] = newblob
    log('  [%s] variants ok=%d fail=%d dropped(non vs/ps)=%d' % (name, stats['ok'], stats['fail'], stats['dropped']))
    return nt, stats
