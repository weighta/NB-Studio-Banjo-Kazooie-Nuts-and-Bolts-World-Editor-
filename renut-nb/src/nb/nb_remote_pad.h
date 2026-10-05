#pragma once
// NB virtual controller (UDP, same protocol as NB's Xenia build). See nb_remote_pad.cpp.
namespace rex { class Runtime; }
namespace rex::ui { class Window; }

namespace nb {
// Adds the virtual controller to the runtime's input system when --nb_remote_input_port is set.
void InstallRemotePad(rex::Runtime* runtime, rex::ui::Window* window);
}
