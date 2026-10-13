"""Build the final REPO.ipa from the CI shell IPA plus the converted game data.

  python tools/package_ipa.py [--work ../repo-work] [--shell path/to/REPO-shell.ipa] [--upload]

Without --shell the latest `shell-latest` release of the private repo is downloaded with the GitHub CLI.
The shell's own Data files (IL2CPP metadata, "unity default resources", ScriptingAssemblies.json, ...)
stay; the game's scenes/assets come from convert_data.py's output. The staging folder hardlinks the
converted files (no extra disk space) and is deleted afterwards.
--upload publishes the IPA as the `ipa-latest` release of the PRIVATE repo (it contains game code + data).
"""
import argparse, json, os, shutil, subprocess, zipfile

PRIVATE_REPO = 'm7830380-cyber/repo-ios-private'
ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))
DEFAULT_WORK = os.path.join(os.path.dirname(ROOT), 'repo-work')
GAME = r'C:\Program Files (x86)\Steam\steamapps\common\REPO'
# shell files replaced by the game's (converted) ones
SHELL_GAME_FILES = ('level', 'sharedassets', 'resources.assets', 'resources.resource', 'globalgamemanagers')
STORE_EXT = ('.resS', '.resource', '.assets', '.bundle')


def find_app(payload):
    for n in os.listdir(payload):
        if n.endswith('.app'):
            return os.path.join(payload, n)
    raise SystemExit('no .app in shell ipa')


def merge_rtiol(app_data):
    """Union of RuntimeInitializeOnLoads.json from the retail game and the shell (which adds RepoIOS)."""
    p_shell = os.path.join(app_data, 'RuntimeInitializeOnLoads.json')
    shell = json.load(open(p_shell)) if os.path.exists(p_shell) else {'root': []}
    retail = json.load(open(os.path.join(GAME, 'REPO_Data', 'RuntimeInitializeOnLoads.json')))
    key = lambda e: (e['assemblyName'], e['nameSpace'], e['className'], e['methodName'])
    seen = {key(e) for e in shell['root']}
    added = [e for e in retail['root'] if key(e) not in seen]
    shell['root'] += added
    json.dump(shell, open(p_shell, 'w'))
    mods = [e['methodName'] for e in shell['root'] if e['assemblyName'] == 'RepoIOS']
    print(f'RuntimeInitializeOnLoads: +{len(added)} retail entries, RepoIOS hooks: {mods}')
    if not mods:
        raise SystemExit('the shell has no RepoIOS init hooks - mod missing from the shell build?')


def link_or_copy(src, dst):
    os.makedirs(os.path.dirname(dst), exist_ok=True)
    if os.path.exists(dst):
        os.remove(dst)
    try:
        os.link(src, dst)
    except OSError:
        shutil.copyfile(src, dst)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--work', default=DEFAULT_WORK)
    ap.add_argument('--shell')
    ap.add_argument('--upload', action='store_true')
    a = ap.parse_args()

    shell = a.shell
    if not shell:
        shell = os.path.join(a.work, 'REPO-shell.ipa')
        if os.path.exists(shell):
            os.remove(shell)
        subprocess.check_call(['gh', 'release', 'download', 'shell-latest', '--repo', PRIVATE_REPO,
                               '-p', 'REPO-shell.ipa', '-D', a.work])

    stage = os.path.join(a.work, 'ipa_stage')
    shutil.rmtree(stage, ignore_errors=True)
    try:
        with zipfile.ZipFile(shell) as z:
            z.extractall(stage)
        app = find_app(os.path.join(stage, 'Payload'))
        data = os.path.join(app, 'Data')
        print('shell app:', app)
        for n in os.listdir(data):
            if n.startswith(SHELL_GAME_FILES):
                os.remove(os.path.join(data, n))
        conv = os.path.join(a.work, 'data', 'Data')
        n = 0
        for root, _, files in os.walk(conv):
            for f in files:
                src = os.path.join(root, f)
                link_or_copy(src, os.path.join(data, os.path.relpath(src, conv)))
                n += 1
        print(f'{n} converted data files')
        merge_rtiol(data)

        out = os.path.join(a.work, 'REPO.ipa')
        if os.path.exists(out):
            os.remove(out)
        with zipfile.ZipFile(out, 'w', zipfile.ZIP_DEFLATED, allowZip64=True, compresslevel=6) as z:
            for root, dirs, files in os.walk(os.path.join(stage, 'Payload')):
                for f in files:
                    p = os.path.join(root, f)
                    arc = os.path.relpath(p, stage).replace('\\', '/')
                    ct = zipfile.ZIP_STORED if f.endswith(STORE_EXT) else zipfile.ZIP_DEFLATED
                    z.write(p, arc, compress_type=ct)
        print('wrote', out, os.path.getsize(out) >> 20, 'MB')
    finally:
        shutil.rmtree(stage, ignore_errors=True)

    if a.upload:
        tag = 'ipa-latest'
        if subprocess.call(['gh', 'release', 'view', tag, '--repo', PRIVATE_REPO], stdout=subprocess.DEVNULL, stderr=subprocess.DEVNULL) != 0:
            subprocess.check_call(['gh', 'release', 'create', tag, '--repo', PRIVATE_REPO, '--prerelease',
                                   '--title', 'R.E.P.O. iOS (TrollStore)', '--notes', 'Install with TrollStore.'])
        subprocess.check_call(['gh', 'release', 'upload', tag, out, '--clobber', '--repo', PRIVATE_REPO])
        print('uploaded to', PRIVATE_REPO, tag)


if __name__ == '__main__':
    main()
