"""Build everything that goes into the shell's Assets/Plugins/Game folder.

  python tools/build_plugins.py [--game "C:/.../REPO"] [--out ../repo-ios-private/Plugins]

1. builds RepoShim.dll (shim/) and the patcher (tools/patcher)
2. patches the retail assemblies into --out
3. builds RepoIOS.dll (mod/) against them (game internals via a publicized copy in mod/obj, never shipped)
4. writes Unity .meta files (iOS only, not loaded in the Editor)

--out is the private repo's Plugins folder; CI checks it out into shell/Assets/Plugins/Game.
Nothing produced here may be committed to the public repo - it is derived from the game.
"""
import argparse, hashlib, os, shutil, subprocess, sys

ROOT = os.path.dirname(os.path.dirname(os.path.abspath(__file__)))

META = """fileFormatVersion: 2
guid: {guid}
PluginImporter:
  externalObjects: {{}}
  serializedVersion: 2
  iconMap: {{}}
  executionOrder: {{}}
  defineConstraints: []
  isPreloaded: 0
  isOverridable: 1
  isExplicitlyReferenced: 0
  validateReferences: 0
  platformData:
  - first:
      Any:
    second:
      enabled: 0
      settings: {{}}
  - first:
      Editor: Editor
    second:
      enabled: 0
      settings:
        DefaultValueInitialized: true
  - first:
      iPhone: iOS
    second:
      enabled: 1
      settings:
        AddToEmbeddedBinaries: false
        CPU: AnyCPU
        CompileFlags:
        FrameworkDependencies:
  userData:
  assetBundleName:
  assetBundleVariant:
"""


def run(*cmd):
    print('>', ' '.join(cmd), flush=True)
    subprocess.check_call(cmd)


def main():
    ap = argparse.ArgumentParser()
    ap.add_argument('--game', default=r'C:\Program Files (x86)\Steam\steamapps\common\REPO')
    ap.add_argument('--out', default=os.path.join(os.path.dirname(ROOT), 'repo-ios-private', 'Plugins'))
    a = ap.parse_args()
    managed = os.path.join(a.game, 'REPO_Data', 'Managed')
    out = os.path.abspath(a.out)
    os.makedirs(out, exist_ok=True)
    for f in os.listdir(out):
        if f.endswith(('.dll', '.meta')):
            os.remove(os.path.join(out, f))

    run('dotnet', 'build', os.path.join(ROOT, 'shim'), '-c', 'Release', '-nologo', '-v', 'q', f'-p:ManagedDir={managed}')
    run('dotnet', 'build', os.path.join(ROOT, 'tools', 'patcher'), '-c', 'Release', '-nologo', '-v', 'q')
    shim = os.path.join(ROOT, 'shim', 'bin', 'Release', 'netstandard2.1', 'RepoShim.dll')
    patcher = os.path.join(ROOT, 'tools', 'patcher', 'bin', 'Release', 'net8.0', 'RepoPatcher.dll')
    run('dotnet', patcher, 'patch', managed, shim, out)
    ref = os.path.join(ROOT, 'mod', 'obj', 'gameref')
    run('dotnet', patcher, 'publicize', os.path.join(out, 'Assembly-CSharp.dll'), os.path.join(ref, 'Assembly-CSharp.dll'))
    run('dotnet', 'build', os.path.join(ROOT, 'mod'), '-c', 'Release', '-nologo', '-v', 'q',
        f'-p:ManagedDir={managed}', f'-p:GameAsmDir={out}', f'-p:RefAsmDir={ref}')
    shutil.copyfile(os.path.join(ROOT, 'mod', 'bin', 'Release', 'netstandard2.1', 'RepoIOS.dll'), os.path.join(out, 'RepoIOS.dll'))

    n = 0
    for f in sorted(os.listdir(out)):
        if not f.endswith('.dll'):
            continue
        guid = hashlib.md5(('repo-ios-plugin:' + f).encode()).hexdigest()
        with open(os.path.join(out, f + '.meta'), 'w', newline='\n') as fh:
            fh.write(META.format(guid=guid))
        n += 1
    print(f'{n} plugins in {out}')


if __name__ == '__main__':
    main()
