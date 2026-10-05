#pragma once
// NB exe-mod layer for reNut.
//
// NB Studio / NB Multiplayer change the game's code by patching 32-bit words of default.xex (NB.Core ExePatches: a
// replaced instruction at a "site", often a branch into new code placed in zero padding, a "cave"). Under Xenia the
// patched words simply run. reNut compiles the original code ahead of time, so here every known site gets a check in
// the generated code (tools/nb_patch_layer.py): when the word in guest memory differs from the original one (the
// edition's default.xex was patched, or a live patch wrote it), the patched code is run by the small interpreter below
// until it reaches original code again; the generated code then jumps there (or returns, when the patched code returned
// from the function). With unmodified words nothing changes. So one reNut build runs every edition, mods on or off.
#include <cstdint>

struct PPCContext;

namespace nbpatch {

constexpr uint32_t kReturn = 0xFFFFFFFFu;    // the host function returns
constexpr uint32_t kFallback = 0xFFFFFFFEu;  // could not run the patched code: run the original instruction

// Runs the patched code starting at `site` (whose word differs from the original). `exits` are the addresses of the
// host function the generated code can jump to. Returns one of them, kReturn or kFallback.
uint32_t Run(PPCContext& ctx, uint8_t* base, uint32_t site, const uint32_t* exits, int exit_count);

// Entry for mod code the game calls through a pointer (registered per cave entry at startup).
void RunFunction(PPCContext& ctx, uint8_t* base, uint32_t entry);

}  // namespace nbpatch
