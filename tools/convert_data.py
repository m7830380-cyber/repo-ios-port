"""Convert retail R.E.P.O. (Windows) data so the iOS shell player can load it.

  python tools/convert_data.py [--game "C:/.../REPO"] [--out ../repo-work/data] [--jobs 14]

Produces <out>/Data/ (goes into the app's Data folder, see package_ipa.py). For every serialized file:
  * the target-platform tag changes from StandaloneWindows64 (19) to iOS (9), and the Unity version string
    to the shell's (2022.3.62f3; the game is 2022.3.67f2, an Enterprise-only release of the same LTS line)
  * every Shader gets its DX11 programs translated to Metal (convert_shader.py, HLSLcc)
  * every BCn/DXT (and crunched) texture is re-encoded as ASTC (transcode_tex.py). Streamed textures go
    into a rebuilt <file>.resS together with the unchanged streamed mesh data (mesh offsets re-pointed).
Addressables bundles (StreamingAssets/aa, localization tables) get the same treatment and, with the
localization .tsv files, go to Data/Raw (iOS StreamingAssets). Audio (.resource) and videos (VP8, which
Unity decodes in software on every platform) stay as they are.
Nothing produced here may be committed to the public repo - it is derived from the game.
"""
import argparse, hashlib, json, os, pickle, shutil, struct, sys, time, traceback
from concurrent.futures import ProcessPoolExecutor

sys.path.insert(0, os.path.dirname(os.path.abspath(__file__)))
import UnityPy
from convert_shader import convert_shader
import transcode_tex as tex

IOS_PLATFORM = 9
TARGET_UNITY = '2022.3.62f3'
PLAYER_FILES = ['globalgamemanagers', 'globalgamemanagers.assets', 'level0', 'level1', 'level2',
                'sharedassets0.assets', 'sharedassets1.assets', 'sharedassets2.assets', 'resources.assets',
                'Resources/unity_builtin_extra']
DEFAULT_GAME = r'C:\Program Files (x86)\Steam\steamapps\common\REPO'
CONVERTER_VERSION = '1'   # bump to invalidate the shader cache
STREAMED = ('Texture2D', 'Cubemap', 'Texture2DArray', 'Texture3D', 'Mesh')
CHUNK = 160               # textures in flight at once (memory bound)


def log(*a):
    print(time.strftime('%H:%M:%S'), *a, flush=True)


# ------------------------------------------------------------------------------------------------- shaders
def convert_shaders(objects, cache_dir, work_dir, report):
    n = 0
    for obj in objects:
        if obj.type.name != 'Shader':
            continue
        tt = obj.read_typetree()
        blob = bytes(tt['compressedBlob'])
        name = tt['m_ParsedForm']['m_Name']
        h = hashlib.sha1((CONVERTER_VERSION + name).encode())
        h.update(blob)
        cp = os.path.join(cache_dir, h.hexdigest() + '.pkl')
        if os.path.exists(cp):
            nt, stats = pickle.load(open(cp, 'rb'))
        else:
            tt['compressedBlob'] = b''
            msgs = []
            wd = os.path.join(work_dir, 'hlslcc', h.hexdigest())
            res = convert_shader(tt, blob, wd, log=msgs.append)
            shutil.rmtree(wd, ignore_errors=True)
            if res is None:
                report.setdefault('shader_no_dx11', []).append(name)
                continue
            nt, stats = res
            stats = dict(stats, msgs=msgs[:20])
            pickle.dump((nt, stats), open(cp, 'wb'))
        if stats.get('fail'):
            report.setdefault('shader_failures', {})[name] = stats.get('msgs', [])
        obj.save_typetree(nt)
        n += 1
    return n


# ------------------------------------------------------------------------------------------------ textures
def _tex_job(args):
    key, fmt, w, h, mips, images, data = args
    try:
        return key, tex.transcode(fmt, w, h, mips, images, data), None
    except Exception as e:
        return key, None, repr(e)


def _apply_texture(obj, tt, res, stream=None):
    tfmt, data, mips = res
    images = max(1, tt.get('m_ImageCount', 1))
    tt['m_TextureFormat'] = tfmt
    tt['m_MipCount'] = mips
    tt['m_CompleteImageSize'] = len(data) // images
    if 'm_PlatformBlob' in tt:
        tt['m_PlatformBlob'] = []
    if stream is not None:
        tt['image data'] = b''
        tt['m_StreamData'] = stream
    else:
        tt['image data'] = data
        if 'm_StreamData' in tt:
            tt['m_StreamData'] = {'offset': 0, 'size': 0, 'path': ''}
    obj.save_typetree(tt)


def _mesh_repoint(obj, path, old_off, size, new_off):
    """Mesh m_StreamData (offset u64, size u32, path string) is the last field: patch the offset in place."""
    raw = obj.get_raw_data()
    p = path.encode()
    tail_body = struct.pack('<QIi', old_off, size, len(p)) + p
    pad = (-len(tail_body)) % 4
    for tail in (tail_body + b'\0' * pad, tail_body):
        i = raw.rfind(tail)
        if i >= 0 and len(raw) - i - len(tail) <= 4:
            new = raw[:i] + struct.pack('<Q', new_off) + raw[i + 8:]
            obj.set_raw_data(new)
            return True
    return False


def rebuild_streamed(sf, src_path, dst_path, pool, report):
    """Textures (transcoded) + other streamed objects (copied) into a new <file>.resS."""
    name = os.path.basename(src_path) + '.resS'
    src_ress = src_path + '.resS'
    items = []          # (obj, tt or None, offset, size)
    inline = []
    for obj in sf.objects.values():
        if obj.type.name not in STREAMED:
            continue
        tt = obj.read_typetree()
        sd = tt.get('m_StreamData') or {}
        if sd.get('size') and sd.get('path') == name:
            items.append((obj, tt, sd['offset'], sd['size']))
        elif obj.type.name in tex.TEXTURE_CLASSES and tt.get('m_TextureFormat') in tex.CONVERTIBLE and tt.get('image data'):
            inline.append((obj, tt))
        elif sd.get('size'):
            report.setdefault('foreign_stream', []).append((obj.type.name, sd.get('path')))
    n_tex = 0
    # inline textures (small): transcode in the pool too
    jobs = [(i, tt['m_TextureFormat'], tt['m_Width'], tt['m_Height'], tt.get('m_MipCount', 1),
             tt.get('m_ImageCount', 1), bytes(tt['image data'])) for i, (obj, tt) in enumerate(inline)]
    for key, res, err in pool.map(_tex_job, jobs):
        obj, tt = inline[key]
        if err:
            report.setdefault('texture_errors', []).append(f"{tt.get('m_Name')}: {err}")
            continue
        _apply_texture(obj, tt, res)
        n_tex += 1
    if not items:
        return n_tex, False
    items.sort(key=lambda it: it[2])
    out = open(dst_path + '.resS', 'wb')
    src = open(src_ress, 'rb')
    pos = 0
    for c0 in range(0, len(items), CHUNK):
        chunk = items[c0:c0 + CHUNK]
        jobs = []
        raws = {}
        for k, (obj, tt, off, size) in enumerate(chunk):
            src.seek(off)
            raw = src.read(size)
            if obj.type.name in tex.TEXTURE_CLASSES and tt.get('m_TextureFormat') in tex.CONVERTIBLE:
                jobs.append((k, tt['m_TextureFormat'], tt['m_Width'], tt['m_Height'], tt.get('m_MipCount', 1),
                             tt.get('m_ImageCount', 1), raw))
            else:
                raws[k] = raw
        results = {}
        for key, res, err in pool.map(_tex_job, jobs):
            if err:
                obj, tt = chunk[key][0], chunk[key][1]
                report.setdefault('texture_errors', []).append(f"{tt.get('m_Name')}: {err}")
                src.seek(chunk[key][2])
                raws[key] = src.read(chunk[key][3])
            else:
                results[key] = res
        for k, (obj, tt, off, size) in enumerate(chunk):
            pad = (-pos) % 16
            out.write(b'\0' * pad)
            pos += pad
            if k in results:
                res = results[k]
                out.write(res[1])
                _apply_texture(obj, tt, res, {'offset': pos, 'size': len(res[1]), 'path': name})
                pos += len(res[1])
                n_tex += 1
            else:
                out.write(raws[k])
                if obj.type.name == 'Mesh':
                    if not _mesh_repoint(obj, name, off, size, pos):
                        report.setdefault('mesh_repoint_failed', []).append(tt.get('m_Name'))
                else:
                    tt['m_StreamData'] = {'offset': pos, 'size': size, 'path': name}
                    obj.save_typetree(tt)
                pos += size
        log(f'  {os.path.basename(src_path)}: streamed {min(c0 + CHUNK, len(items))}/{len(items)}')
    out.close()
    src.close()
    report['resS_bytes'] = pos
    return n_tex, True


# --------------------------------------------------------------------------------------------- files
def stamp(sf):
    sf._m_target_platform = IOS_PLATFORM
    sf.unity_version = TARGET_UNITY


def process_player_file(src, dst, cache_dir, work_dir, pool):
    report = {}
    env = UnityPy.load(src)
    sf = list(env.files.values())[0]
    report['shaders'] = convert_shaders(sf.objects.values(), cache_dir, work_dir, report)
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    report['textures'], rebuilt = rebuild_streamed(sf, src, dst, pool, report)
    stamp(sf)
    with open(dst, 'wb') as f:
        f.write(sf.save())
    if not rebuilt and os.path.exists(src + '.resS'):
        shutil.copyfile(src + '.resS', dst + '.resS')
    # audio / video: "x.assets" -> "x.resource"
    res = os.path.splitext(src)[0] + '.resource'
    if os.path.exists(res):
        shutil.copyfile(res, os.path.splitext(dst)[0] + '.resource')
    return report


def process_bundle(src, dst, cache_dir, work_dir, pool):
    report = {}
    env = UnityPy.load(src)
    bf = list(env.files.values())[0]
    for name, sf in bf.files.items():
        if not hasattr(sf, 'objects'):
            continue
        report['shaders'] = report.get('shaders', 0) + convert_shaders(sf.objects.values(), cache_dir, work_dir, report)
        inline = [o for o in sf.objects.values() if o.type.name in tex.TEXTURE_CLASSES]
        for obj in inline:
            tt = obj.read_typetree()
            if tt.get('m_TextureFormat') not in tex.CONVERTIBLE:
                continue
            data = bytes(obj.read().get_image_data())
            key, res, err = _tex_job((0, tt['m_TextureFormat'], tt['m_Width'], tt['m_Height'], tt.get('m_MipCount', 1), tt.get('m_ImageCount', 1), data))
            if err:
                report.setdefault('texture_errors', []).append(err)
            else:
                _apply_texture(obj, tt, res)
        stamp(sf)
    if hasattr(bf, 'version_engine'):
        bf.version_engine = TARGET_UNITY
    data = bf.save(packer='lz4')
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    with open(dst, 'wb') as f:
        f.write(data)
    return report


def convert_streaming_assets(game_data, out_raw, cache_dir, work_dir, pool, full_report):
    sa = os.path.join(game_data, 'StreamingAssets')
    for root, _, files in os.walk(sa):
        for f in files:
            src = os.path.join(root, f)
            rel = os.path.relpath(src, sa)
            dst = os.path.join(out_raw, rel)
            os.makedirs(os.path.dirname(dst), exist_ok=True)
            if f.endswith('.bundle'):
                full_report[rel] = process_bundle(src, dst, cache_dir, work_dir, pool)
                log('done', rel)
            else:
                shutil.copyfile(src, dst)
    # Windows-built catalog uses backslash separators (a plain filename character on iOS)
    cat = os.path.join(out_raw, 'aa', 'catalog.json')
    if os.path.exists(cat):
        c = json.load(open(cat, encoding='utf8'))
        c['m_InternalIds'] = [i.replace('\\', '/') for i in c['m_InternalIds']]
        json.dump(c, open(cat, 'w', encoding='utf8'), separators=(',', ':'))
    st = os.path.join(out_raw, 'aa', 'settings.json')
    if os.path.exists(st):
        s = json.load(open(st, encoding='utf8'))
        s['m_buildTarget'] = 'iOS'
        json.dump(s, open(st, 'w', encoding='utf8'), separators=(',', ':'))


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--game', default=DEFAULT_GAME)
    ap.add_argument('--out', default=os.path.join(os.path.dirname(os.path.dirname(os.path.dirname(os.path.abspath(__file__)))), 'repo-work', 'data'))
    ap.add_argument('--only', default=None, help='substring filter on file names (testing)')
    ap.add_argument('--jobs', type=int, default=max(1, (os.cpu_count() or 4) - 2))
    a = ap.parse_args()

    data = os.path.join(a.game, 'REPO_Data')
    out_data = os.path.join(a.out, 'Data')
    cache_dir = os.path.join(a.out, 'cache', 'shaders')
    work_dir = os.path.join(a.out, 'tmp')
    os.makedirs(cache_dir, exist_ok=True)
    files = [f for f in PLAYER_FILES if not a.only or a.only in f]
    # biggest first
    files.sort(key=lambda f: -os.path.getsize(os.path.join(data, f)))
    full_report = {}
    t0 = time.time()
    with ProcessPoolExecutor(max_workers=a.jobs) as pool:
        for f in files:
            src = os.path.join(data, f)
            log('converting', f)
            try:
                full_report[f] = process_player_file(src, os.path.join(out_data, f), cache_dir, work_dir, pool)
            except Exception:
                full_report[f] = {'error': traceback.format_exc()}
            r = full_report[f]
            log('done', f, {k: (v if not isinstance(v, (list, dict)) else len(v)) for k, v in r.items()})
        if not a.only or 'aa' in a.only or 'Stream' in a.only:
            convert_streaming_assets(data, os.path.join(out_data, 'Raw'), cache_dir, work_dir, pool, full_report)
    json.dump(full_report, open(os.path.join(a.out, 'convert_report.json'), 'w'), indent=1, default=str)
    errors = sum(len(r.get('texture_errors', [])) + ('error' in r) + len(r.get('mesh_repoint_failed', [])) for r in full_report.values())
    log('finished in %.0fs, %d problems (see convert_report.json)' % (time.time() - t0, errors))


if __name__ == '__main__':
    main()
