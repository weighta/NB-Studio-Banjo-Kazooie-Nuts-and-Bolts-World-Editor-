#pragma once
// NB fixes for game races that reNut's timing exposes (config/nb_fixes.toml, nb_fixes.cpp).
namespace rex { class Runtime; }

namespace nb {
// After the runtime is set up (ReXApp::OnPostSetup): the fixes that need the runtime's subsystems.
void InstallFixes(rex::Runtime* runtime);
}  // namespace nb
