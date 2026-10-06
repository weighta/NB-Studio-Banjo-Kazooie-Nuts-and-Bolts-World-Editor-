// NB fixes for game races that reNut's timing exposes (config/nb_fixes.toml).
#include "nb_fixes.h"

#include <rex/audio/audio_system.h>
#include <rex/ppc.h>
#include <rex/runtime.h>
#include <rex/logging.h>

#include <atomic>
#include <chrono>
#include <thread>

// 0x823642D8 lwz r3,0x24(r31) with r31 = 0 (the slot is not in use yet): skip to the unlock at 0x82364340.
bool nbfix_empty_slot(PPCRegister& r31)
{
    return r31.u32 == 0;
}

namespace {

rex::Runtime* g_runtime = nullptr;

// The audio system's client table is protected; a pointer to the member, named through a derived class, reads it.
struct AudioClients : rex::audio::AudioSystem {
    static auto Table() { return &AudioClients::clients_; }
    static constexpr size_t kCount = kMaximumClientCount;
};

}  // namespace

namespace nb {
void InstallFixes(rex::Runtime* runtime) { g_runtime = runtime; }
}  // namespace nb

// XAudio's render callback (XAUDIO::CEngine::Process, 0x82664B70) and its render client
// (config/nb_fixes.toml). The SDK's audio worker runs the callback outside its lock; when the game unregisters the
// client meanwhile (XAudio shuts down as the intro movie ends, while the title screen loads), the callback's
// XAudioSubmitRenderDriverFrame found the client's slot empty and AudioSystem::SubmitFrame called a null driver
// (crash). On the console an unregister waits for a running callback: the callbacks are counted while they run, and
// the unregister calls wait (at most 1 s) until none is running. A frame that still finds no driver is dropped.
namespace {
std::atomic<int> g_audio_callbacks{0};

void WaitForAudioCallbacks()
{
    const auto until = std::chrono::steady_clock::now() + std::chrono::seconds(1);
    while (g_audio_callbacks.load(std::memory_order_acquire) > 0) {
        if (std::chrono::steady_clock::now() > until) {
            REXLOG_WARN("NB: audio client unregistered while its callback still runs (waited 1 s)");
            return;
        }
        std::this_thread::sleep_for(std::chrono::microseconds(200));
    }
}
}  // namespace

void nbfix_audio_enter() { g_audio_callbacks.fetch_add(1, std::memory_order_acq_rel); }
void nbfix_audio_leave() { g_audio_callbacks.fetch_sub(1, std::memory_order_acq_rel); }
void nbfix_audio_unregister() { WaitForAudioCallbacks(); }
void nbfix_audio_unregister2() { WaitForAudioCallbacks(); }

// 0x826676D4 bl XAudioSubmitRenderDriverFrame(r3 = driver handle 0x4155xxxx, r4 = samples): the original call while
// the client has a driver; otherwise the frame is dropped (true: the call is skipped, r3 = 0 as it returns).
bool nbfix_audio_submit(PPCRegister& r3, PPCRegister& r4)
{
    (void)r4;
    auto* audio = g_runtime ? dynamic_cast<rex::audio::AudioSystem*>(g_runtime->audio_system()) : nullptr;
    const size_t index = r3.u32 & 0xFFFF;
    if (!audio || index >= AudioClients::kCount) return false;   // not ours to judge: the original call
    if (((*audio).*AudioClients::Table())[index].driver) return false;   // the original call
    static int logged = 0;
    if (logged < 20) { ++logged; REXLOG_INFO("NB: audio frame for client {} dropped (no driver)", index); }
    r3.u64 = 0;   // what the call returns
    return true;
}
