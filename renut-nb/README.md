# renut-nb: NB's mod layer for reNut

[reNut](https://github.com/masterspike52/reNut) is *Banjo-Kazooie: Nuts & Bolts* recompiled to native PC code with
[ReXGlue](https://github.com/rexglue/rexglue-sdk). This kit adds what NB Multiplayer and NB Studio need to run their
editions with it, so **Settings > Game engine > Launch with reNut** in NB Multiplayer plays the same editions, mods,
Showdown Town co-op and Character Select as Xenia does.

## What it adds

| Part | Files | What it does |
|---|---|---|
| Executable-mod layer | `src/nb/nbpatch.*` + `NB.Cli renut-layer` / `renut-layer-apply` | NB's game-code mods patch words of `default.xex`. reNut compiles the original code ahead of time, so at every word an NB mod replaces (109 sites of 24 mods) the generated code checks memory: when the word differs from the original, a small PowerPC interpreter runs the patched code (and the mod code it branches to) and the function continues where that code returns to original code. One reNut build runs every edition, with mods on or off. |
| Virtual controller | `src/nb/nb_remote_pad.*` | `--nb_remote_input_port N`: the same UDP controller protocol as NB's Xenia build (NB's test tools). Sends button events too, which the menus read. |
| Launch paths | `renut-nb.patch` (path wizard) | `--game_data_root` / `--user_data_root` on the command line skip the path wizard, so every edition starts with its own game folder. |
| Window | `src/nb/nb_window.*`, `renut-nb.patch` (`renut_app.h`) | reNut starts in a window (unless `--fullscreen true` or `fullscreen = true` in renut.toml asks for full screen); **F11** or **Alt+Enter** switch between window and full screen. Alt+F4 closes the game as usual. |
| Game fixes | `src/nb/nb_fixes.*`, `config/nb_fixes.toml` | reNut's timing exposes races in the game's loading code. `0x82364250` read a table slot before it was filled (null read, crash at the title screen): an empty slot now skips to the unlock at the end of the function. When the intro movie ends, its XAudio engine shuts down and unregisters its render client while the SDK's audio worker is still running that client's callback; the frame then went to a client without a driver (crash in `AudioSystem::SubmitFrame`, the most common title-screen crash). The callbacks are now counted while they run and the unregister waits for them, as on the console. |
| Photos | `renut-nb.patch` (`gpu/d3d/hooks`, `gpu/render/textures.*`) | The game's Take Photo resolves the picture into a texture and reads it on the CPU (JPEG and preview). reNut keeps resolves on the GPU, so photos were black: a resolve destination the game then locks is now read back into game memory (tiled, in the game's byte order), once per lock. |
| MSVC compatibility | `src/nb/msvc_compat.cpp` | The SDK's prebuilt libraries need one STL helper that Visual Studio 2022 before 17.14 lacks. |

Known issue: the title screen can still crash now and then (about one start in twelve or fewer in tests, in the game's
heap code, with or without NB's mods). NB Multiplayer restarts reNut automatically when it crashes within half a minute of
starting.

How NB Multiplayer uses it: editions carry their mods in their own `default.xex`, which reNut loads; for your own game
folder and NB Studio projects, NB Multiplayer writes the mod words into the running game. Co-op and Character Select
attach to the reNut process (the game's memory is at the same address as in Xenia). Saves go to
`%LOCALAPPDATA%\NB-Multiplayer\data\renut`. Rooms for the game's own Xbox LIVE modes still use Xenia: reNut has no Xbox
LIVE networking.

## Build

You need: Windows 10/11, Visual Studio 2022 (Desktop development with C++), Python 3 with `pip install xxhash`, your own
copy of the game (US, no title update), and:

1. **reNut's source** (this kit was made against the `renut-rendering` source), with its `thirdparty/plume` filled in:
   `git clone --recursive https://github.com/renderbag/plume thirdparty/plume`, and DXC's `dxc.exe`, `dxcompiler.dll`,
   `dxil.dll` in `thirdparty/reblue-XenosRecomp/thirdparty/dxc-bin/bin/x64` (`dxcompiler.lib` in `.../lib/x64`), from a
   [DirectXShaderCompiler release](https://github.com/microsoft/DirectXShaderCompiler/releases).
2. **The ReXGlue SDK 0.10.0** (`rexglue-sdk-0.10.0.x-win-amd64.zip` from the
   [releases](https://github.com/rexglue/rexglue-sdk/releases)), unzipped.
3. **LLVM/Clang 20 or newer** ([LLVM releases](https://github.com/llvm/llvm-project/releases); the installer can also be
   unpacked with 7-Zip).
4. **NB.Cli.exe** (in NB Multiplayer's `tools` folder or NB Studio's `cli` folder, or built from this repository).

```bat
apply.cmd C:\path\to\renut-source
cd C:\path\to\renut-source
set RENUT_SDK=C:\path\to\rexglue-sdk\win-amd64
set RENUT_LLVM=C:\path\to\llvm
set NB_CLI=C:\path\to\NB.Cli.exe
set NB_GAME=C:\path\to\your\game
build_renut.cmd
```

The result is `out\build\win-amd64-release\renut.exe` (with `rexruntime.dll` next to it). Choose that `renut.exe` in NB
Multiplayer under **Settings > Game engine**.

**Run it once, then build again.** The game compiles some shaders at runtime that no dump of its files contains, so a
first build draws the title screen and menus without their 3D backgrounds. Point NB Multiplayer at the `renut.exe` in
`out\build\win-amd64-release` (or start it from there), play through the title, the menus and into Showdown Town once:
reNut saves the missing shaders in `shader_capture` next to the exe. Run `build_renut.cmd` again and they are compiled in
(about 150 of them in our tests). Repeat whenever reNut's log mentions shaders "not in the shader cache".

When NB adds new executable mods, rebuild: `NB.Cli renut-layer` picks up every mod NB.Cli knows.

## Licenses

`generated/rexglue.cmake` is the build boilerplate the ReXGlue SDK's `rexglue init` writes (paths adapted to reNut's
`generated/` layout); the SDK is under the BSD 3-Clause License (`generated/LICENSE-rexglue.txt`). The rest of this kit is
NB's own code, under this repository's license. reNut and the game itself are not part of it: you build reNut from its
own source with your own copy of the game.
