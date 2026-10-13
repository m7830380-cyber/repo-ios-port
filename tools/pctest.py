"""PC test bed: runs the patched assemblies + the iOS mod in the Windows (Mono) player, so the patcher, the
Steam-less boot and the input layer can be tested without a phone round trip.

  python tools/pctest.py setup   [--game ...] [--plugins ../repo-ios-private/Plugins] [--out ../repo-work/pctest]
  python tools/pctest.py run     [--touchsim] [--seconds N]
  python tools/pctest.py lan     [--seconds N]   # two windows: one hosts a local game, the other joins it
  python tools/pctest.py restore            # put the PC game's real saves/settings back

The copy hardlinks every game file (no extra disk space) except the managed assemblies and the two JSON
manifests. The Windows player keeps its data in AppData/LocalLow/semiwork/Repo, shared with the real game,
so `setup` backs that folder up once and `restore` puts it back.
"""
import argparse, json, os, shutil, subprocess, sys, time

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
WORK = os.path.join(os.path.dirname(ROOT), 'repo-work')
LOCALLOW = os.path.join(os.environ.get('USERPROFILE', ''), 'AppData', 'LocalLow', 'semiwork', 'Repo')
BACKUP = os.path.join(WORK, 'pc-save-backup')

# RuntimeInitializeLoadType: AfterSceneLoad=0 BeforeSceneLoad=1 AfterAssembliesLoaded=2 BeforeSplash=3 SubsystemRegistration=4
MOD_INITS = [('RepoIOS', 'RepoIOS', 'Bootstrap', 'EarlyInit', 4), ('RepoIOS', 'RepoIOS', 'Bootstrap', 'Init', 1)]


def setup(a):
    data_src = os.path.join(a.game, 'REPO_Data')
    out = a.out
    if os.path.isdir(out):
        shutil.rmtree(out)
    for root, dirs, files in os.walk(a.game):
        rel = os.path.relpath(root, a.game)
        os.makedirs(os.path.join(out, rel), exist_ok=True)
        for f in files:
            os.link(os.path.join(root, f), os.path.join(out, rel, f))
    managed = os.path.join(out, 'REPO_Data', 'Managed')
    names = []
    for f in sorted(os.listdir(a.plugins)):
        if not f.endswith('.dll'):
            continue
        dst = os.path.join(managed, f)
        if os.path.exists(dst):
            os.remove(dst)        # break the hardlink before writing
        shutil.copyfile(os.path.join(a.plugins, f), dst)
        names.append(f)
    sa = os.path.join(out, 'REPO_Data', 'ScriptingAssemblies.json')
    d = json.load(open(os.path.join(data_src, 'ScriptingAssemblies.json')))
    os.remove(sa)
    for f in ('RepoShim.dll', 'RepoIOS.dll'):
        if f not in d['names']:
            d['names'].append(f)
            d['types'].append(16)   # user assembly
    json.dump(d, open(sa, 'w'))
    ri = os.path.join(out, 'REPO_Data', 'RuntimeInitializeOnLoads.json')
    r = json.load(open(os.path.join(data_src, 'RuntimeInitializeOnLoads.json')))
    os.remove(ri)
    for asm, ns, cls, meth, lt in MOD_INITS:
        r['root'].append({'assemblyName': asm, 'nameSpace': ns, 'className': cls, 'methodName': meth, 'loadTypes': lt, 'isUnityClass': False})
    json.dump(r, open(ri, 'w'))
    if os.path.isdir(LOCALLOW) and not os.path.isdir(BACKUP):
        shutil.copytree(LOCALLOW, BACKUP)
        print('backed up', LOCALLOW, '->', BACKUP)
    print(f'pctest ready in {out} ({len(names)} patched assemblies)')


def run(a):
    exe = os.path.join(a.out, 'REPO.exe')
    flag = os.path.join(LOCALLOW, 'touchsim')
    if a.touchsim:
        os.makedirs(LOCALLOW, exist_ok=True)
        open(flag, 'w').close()
    elif os.path.exists(flag):
        os.remove(flag)
    args = [exe, '-screen-fullscreen', '0', '-screen-width', '1280', '-screen-height', '590']
    p = subprocess.Popen(args, cwd=a.out)
    if a.seconds:
        time.sleep(a.seconds)
        p.kill()
    else:
        p.wait()
    log = os.path.join(LOCALLOW, 'REPO-iOS.log')
    print('log:', log)


def lan(a):
    """Local multiplayer test: two copies with their own Documents (log, player ID, autotest script)."""
    exe = os.path.join(a.out, 'REPO.exe')
    procs = []
    for i, (name, mode) in enumerate((('lan-host', 'lan-host'), ('lan-join', 'lan-join 127.0.0.1'))):
        docs = os.path.join(WORK, name)
        shutil.rmtree(docs, ignore_errors=True)
        os.makedirs(docs)
        open(os.path.join(docs, 'touchsim'), 'w').close()
        open(os.path.join(docs, 'autotest'), 'w').write(mode)
        open(os.path.join(docs, 'PlayerName.txt'), 'w').write(('Hosty' if i == 0 else 'Joiny'))
        args = [exe, '-screen-fullscreen', '0', '-screen-width', '960', '-screen-height', '540',
                '-repo-docs', docs, '-lan-verbose', '-logFile', os.path.join(docs, 'player.log')]
        procs.append(subprocess.Popen(args, cwd=a.out))
        time.sleep(8)
    t0 = time.time()
    try:
        while time.time() - t0 < (a.seconds or 600):
            done = 0
            for name in ('lan-host', 'lan-join'):
                try:
                    if 'AUTOTEST DONE' in open(os.path.join(WORK, name, 'REPO-iOS.log'), encoding='utf-8', errors='replace').read():
                        done += 1
                except OSError:
                    pass
            if done == 2 or any(p.poll() is not None for p in procs):
                break
            time.sleep(5)
    finally:
        time.sleep(3)
        for p in procs:
            p.kill()
    for name in ('lan-host', 'lan-join'):
        print('log:', os.path.join(WORK, name, 'REPO-iOS.log'))


def restore(a):
    if not os.path.isdir(BACKUP):
        sys.exit('no backup at ' + BACKUP)
    shutil.rmtree(LOCALLOW, ignore_errors=True)
    shutil.copytree(BACKUP, LOCALLOW)
    print('restored', LOCALLOW)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('cmd', choices=['setup', 'run', 'lan', 'restore'])
    ap.add_argument('--game', default=r'C:\Program Files (x86)\Steam\steamapps\common\REPO')
    ap.add_argument('--plugins', default=os.path.join(os.path.dirname(ROOT), 'repo-ios-private', 'Plugins'))
    ap.add_argument('--out', default=os.path.join(WORK, 'pctest'))
    ap.add_argument('--touchsim', action='store_true')
    ap.add_argument('--seconds', type=int, default=0)
    a = ap.parse_args()
    {'setup': setup, 'run': run, 'lan': lan, 'restore': restore}[a.cmd](a)


if __name__ == '__main__':
    main()
