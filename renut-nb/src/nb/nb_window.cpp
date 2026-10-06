// NB: windowed start + full-screen toggle for reNut.
// The SDK's cvar "fullscreen" opens the window full screen (ReXApp::SetupPresentation). reNut now starts in a window
// unless the player asked for full screen (command line, renut.toml or environment), and F11 / Alt+Enter toggle.
#include "nb_window.h"

#include <rex/cvar.h>
#include <rex/ui/flags.h>
#include <rex/ui/ui_event.h>
#include <rex/ui/virtual_key.h>
#include <rex/ui/window.h>

namespace nb {

void PreferWindowedStart()
{
    if (rex::cvar::GetFlagSource("fullscreen") == rex::cvar::Source::kDefault)
        REXCVAR_SET(fullscreen, false);   // keeps the flag's source "default": nothing is written to renut.toml
}

bool HandleWindowKeys(rex::ui::Window* window, rex::ui::KeyEvent& e)
{
    if (!window) return false;
    const auto vk = e.virtual_key();
    const bool f11 = vk == rex::ui::VirtualKey::kF11 && !e.is_alt_pressed() && !e.is_ctrl_pressed();
    const bool alt_enter = vk == rex::ui::VirtualKey::kReturn && e.is_alt_pressed() && !e.is_ctrl_pressed();
    if (!f11 && !alt_enter) return false;
    e.set_handled(true);
    if (e.prev_state()) return true;                  // key repeat while held: toggle once per press
    window->SetFullscreen(!window->IsFullscreen());
    return true;
}

}  // namespace nb
