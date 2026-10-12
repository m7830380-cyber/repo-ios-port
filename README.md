# R.E.P.O. iOS port (TrollStore)

Runs the retail Steam build of R.E.P.O. (Unity 2022.3.67f2) natively on iPhone, singleplayer and offline.
It does not rebuild the game: the game's own assemblies are patched and AOT-compiled into an IL2CPP player
built with the game's exact Unity version, and the game's own data files are converted for Metal and
dropped in.

**This repository contains only our own code.** Nothing from the game (assemblies, patched or not, assets,
converted data, IPAs with compiled game code) is ever committed here or published from here. Those live in
the private repo `repo-ios-private` (patched `Plugins/` + IPA releases).

* `tools/patcher` – Mono.Cecil patcher: P/Invokes (Steam, Discord, Photon Voice, user32) become stubs,
  microphone access is removed, legacy `UnityEngine.Input` reads go through `RepoShim.VInput`, cursor
  lock state is tracked, frame rate goes through the shim, PPv2 compute is reported off.
* `shim/` – `RepoShim.dll`: virtual legacy input + platform stand-ins the patched game calls.
* `mod/` – `RepoIOS.dll`: touch controls (virtual Input System keyboard/mouse/gamepad), controller support,
  log file + log viewer.
* `shell/` – minimal Unity 2022.3.67f2 project; CI builds the unsigned, ldid-fakesigned shell IPA from it.
* `tools/build_plugins.py` – builds shim + mod and runs the patcher into `../repo-ios-private/Plugins`.
* `tools/convert_data.py`, `convert_shader.py`, `dx2metal.py`, `transcode_tex.py` – convert the Windows data
  for iOS (DX11 → Metal shaders via HLSLcc, BC/DXT → ASTC textures, iOS platform tag).
* `tools/package_ipa.py` – merges the CI shell IPA with the converted data into the final IPA.

Logs: `Documents/REPO-iOS.log` (previous run `REPO-iOS-prev.log`); tap the FPS counter for the on-screen log.
