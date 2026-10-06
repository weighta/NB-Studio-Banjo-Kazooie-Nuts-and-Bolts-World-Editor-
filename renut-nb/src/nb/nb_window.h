#pragma once
// NB: reNut's window starts windowed and F11 / Alt+Enter switch between windowed and full screen.
namespace rex::ui { class Window; class KeyEvent; }

namespace nb {

// Before the window is created: start windowed unless fullscreen was asked for (--fullscreen on the command line,
// renut.toml or a REX_ environment variable).
void PreferWindowedStart();

// F11 or Alt+Enter: toggles full screen and consumes the key (true). Alt+F4 is left to Windows (closes the game).
bool HandleWindowKeys(rex::ui::Window* window, rex::ui::KeyEvent& e);

}  // namespace nb
