// NB virtual controller for reNut: the same UDP protocol as NB's Xenia build (cvar nb_remote_input_port), so NB's tools
// (tools/xenia/vpad.py, the co-op test harness) drive reNut like Xenia. Off unless --nb_remote_input_port is given.
// Packet: "NBPD", user u8, connected u8, lt u8, rt u8, buttons u16, lx ly rx ry s16 (little-endian).
#include "nb_remote_pad.h"

#include <rex/cvar.h>
#include <rex/input/input_driver.h>
#include <rex/input/input_system.h>
#include <rex/runtime.h>
#include <rex/system/xtypes.h>

#include <atomic>
#include <cstring>
#include <mutex>
#include <thread>
#include <cstdio>
#include <deque>

#if defined(_WIN32)
#include <winsock2.h>
#pragma comment(lib, "ws2_32.lib")
#endif

REXCVAR_DEFINE_INT32(nb_remote_input_port, 0, "Nuts&Bolts/NB",
    "UDP port of NB's virtual controller (127.0.0.1); 0 = off. Used by NB Multiplayer and NB's test tools.");

namespace {

void Note(const char* what, long a = 0)
{
    if (FILE* f = std::fopen("logs/nb_input.log", "a")) { std::fprintf(f, "%s %ld\n", what, a); std::fclose(f); }
}

using rex::X_STATUS; using rex::X_RESULT;
using rex::input::X_INPUT_STATE; using rex::input::X_INPUT_CAPABILITIES; using rex::input::X_INPUT_VIBRATION; using rex::input::X_INPUT_KEYSTROKE;

struct PadState { bool connected = false; uint8_t lt = 0, rt = 0; uint16_t buttons = 0; int16_t lx = 0, ly = 0, rx = 0, ry = 0; uint32_t packet = 0; };

class RemotePadDriver final : public rex::input::InputDriver
{
public:
    RemotePadDriver(rex::ui::Window* window, int port) : InputDriver(window, 0), port_(port) {}
    ~RemotePadDriver() override
    {
        stop_ = true;
#if defined(_WIN32)
        if (sock_ != INVALID_SOCKET) closesocket(sock_);
#endif
        if (thread_.joinable()) thread_.join();
    }

    X_STATUS Setup() override
    {
#if defined(_WIN32)
        WSADATA wsa; WSAStartup(MAKEWORD(2, 2), &wsa);
        sock_ = socket(AF_INET, SOCK_DGRAM, IPPROTO_UDP);
        if (sock_ == INVALID_SOCKET) return X_STATUS_UNSUCCESSFUL;
        sockaddr_in a{}; a.sin_family = AF_INET; a.sin_port = htons((u_short)port_); a.sin_addr.s_addr = htonl(INADDR_LOOPBACK);
        if (bind(sock_, (sockaddr*)&a, sizeof a) != 0) return X_STATUS_UNSUCCESSFUL;
        DWORD timeout = 200; setsockopt(sock_, SOL_SOCKET, SO_RCVTIMEO, (const char*)&timeout, sizeof timeout);
        thread_ = std::thread([this] { Receive(); });
        return X_STATUS_SUCCESS;
#else
        return X_STATUS_UNSUCCESSFUL;
#endif
    }

    void EnumerateDevices(std::vector<rex::input::DeviceInfo>& out) override
    {
        rex::input::DeviceInfo d;
        d.id = static_cast<rex::input::DeviceId>(0x4E425044);   // 'NBPD'
        d.name = "NB virtual controller"; d.guid = "nb-remote-pad";
        d.synthetic = true;   // feeds user 0
        out.push_back(d);
    }

    X_RESULT GetDeviceState(rex::input::DeviceId, X_INPUT_STATE* s) override
    {
        PadState p; { std::lock_guard<std::mutex> g(m_); p = state_; }
        if (!p.connected) return X_ERROR_DEVICE_NOT_CONNECTED;
        s->packet_number = p.packet;
        s->gamepad.buttons = p.buttons; s->gamepad.left_trigger = p.lt; s->gamepad.right_trigger = p.rt;
        s->gamepad.thumb_lx = p.lx; s->gamepad.thumb_ly = p.ly; s->gamepad.thumb_rx = p.rx; s->gamepad.thumb_ry = p.ry;
        return X_ERROR_SUCCESS;
    }

    X_RESULT GetDeviceCapabilities(rex::input::DeviceId, uint32_t, X_INPUT_CAPABILITIES* c) override
    {
        std::memset(c, 0, sizeof *c);
        c->type = 0x01; c->sub_type = 0x01;
        c->gamepad.buttons = 0xF3FF; c->gamepad.left_trigger = 0xFF; c->gamepad.right_trigger = 0xFF;
        c->gamepad.thumb_lx = (int16_t)0xFFC0; c->gamepad.thumb_ly = (int16_t)0xFFC0; c->gamepad.thumb_rx = (int16_t)0xFFC0; c->gamepad.thumb_ry = (int16_t)0xFFC0;
        return X_ERROR_SUCCESS;
    }

    X_RESULT SetDeviceVibration(rex::input::DeviceId, X_INPUT_VIBRATION*) override { return X_ERROR_SUCCESS; }
    // Menus read button events (XInputGetKeystroke), not only the state: every change of a button or stick direction
    // becomes a key-down / key-up event with the Xbox 360 pad key codes (VK_PAD_*).
    X_RESULT GetDeviceKeystroke(rex::input::DeviceId, uint32_t, X_INPUT_KEYSTROKE* k) override
    {
        std::lock_guard<std::mutex> g(m_);
        if (keys_.empty()) return X_ERROR_EMPTY;
        auto e = keys_.front(); keys_.pop_front();
        std::memset(k, 0, sizeof *k);
        k->virtual_key = e.first; k->flags = e.second; k->user_index = 0;
        return X_ERROR_SUCCESS;
    }

private:
    void Receive()
    {
#if defined(_WIN32)
        unsigned char buf[64];
        while (!stop_)
        {
            int n = recv(sock_, (char*)buf, sizeof buf, 0);
            if (n < 18 || std::memcmp(buf, "NBPD", 4) != 0) continue;
            if (buf[4] != 0) continue;   // user 0 only
            PadState p;
            p.connected = buf[5] != 0; p.lt = buf[6]; p.rt = buf[7];
            std::memcpy(&p.buttons, buf + 8, 2); std::memcpy(&p.lx, buf + 10, 2); std::memcpy(&p.ly, buf + 12, 2);
            std::memcpy(&p.rx, buf + 14, 2); std::memcpy(&p.ry, buf + 16, 2);
            std::lock_guard<std::mutex> g(m_);
            Keystrokes(state_, p);
            p.packet = state_.packet + 1; state_ = p;
        }
#endif
    }

    // VK_PAD_* for the button bits (A B X Y, shoulders, d-pad, start, back, stick presses) and the stick directions
    static uint32_t Dirs(int16_t x, int16_t y, uint16_t up, uint16_t down, uint16_t right, uint16_t left, uint32_t& bits)
    {
        const int t = 16000; bits = 0;
        if (y > t) bits |= 1; if (y < -t) bits |= 2; if (x > t) bits |= 4; if (x < -t) bits |= 8;
        (void)up; (void)down; (void)right; (void)left; return bits;
    }
    void Keystrokes(const PadState& a, const PadState& b)
    {
        static const std::pair<uint16_t, uint16_t> map[] = {
            { 0x1000, 0x5800 }, { 0x2000, 0x5801 }, { 0x4000, 0x5802 }, { 0x8000, 0x5803 }, { 0x0200, 0x5804 }, { 0x0100, 0x5805 },
            { 0x0001, 0x5810 }, { 0x0002, 0x5811 }, { 0x0004, 0x5812 }, { 0x0008, 0x5813 }, { 0x0010, 0x5814 }, { 0x0020, 0x5815 },
            { 0x0040, 0x5816 }, { 0x0080, 0x5817 } };
        uint16_t ab = a.connected ? a.buttons : 0, bb = b.connected ? b.buttons : 0;
        for (auto& [bit, vk] : map)
            if ((ab ^ bb) & bit) keys_.push_back({ vk, (uint16_t)((bb & bit) ? 1 : 2) });
        auto trig = [&](uint8_t x, uint8_t y, uint16_t vk) { bool p = x > 30, q = y > 30; if (p != q) keys_.push_back({ vk, (uint16_t)(q ? 1 : 2) }); };
        trig(a.lt, b.lt, 0x5806); trig(a.rt, b.rt, 0x5807);
        uint32_t da, db;
        Dirs(a.lx, a.ly, 0, 0, 0, 0, da); Dirs(b.lx, b.ly, 0, 0, 0, 0, db);
        static const uint16_t lvk[4] = { 0x5820, 0x5821, 0x5822, 0x5823 };   // left stick up down right left
        for (int i = 0; i < 4; ++i) if ((da ^ db) & (1u << i)) keys_.push_back({ lvk[i], (uint16_t)((db & (1u << i)) ? 1 : 2) });
        Dirs(a.rx, a.ry, 0, 0, 0, 0, da); Dirs(b.rx, b.ry, 0, 0, 0, 0, db);
        static const uint16_t rvk[4] = { 0x5830, 0x5831, 0x5832, 0x5833 };
        for (int i = 0; i < 4; ++i) if ((da ^ db) & (1u << i)) keys_.push_back({ rvk[i], (uint16_t)((db & (1u << i)) ? 1 : 2) });
        while (keys_.size() > 64) keys_.pop_front();
    }

    int port_;
    std::deque<std::pair<uint16_t, uint16_t>> keys_;
    std::mutex m_;
    PadState state_;
    std::atomic<bool> stop_{false};
    std::thread thread_;
#if defined(_WIN32)
    SOCKET sock_ = INVALID_SOCKET;
#endif
};

}  // namespace

namespace nb {

void InstallRemotePad(rex::Runtime* runtime, rex::ui::Window* window)
{
    int port = REXCVAR_GET(nb_remote_input_port);
    if (port <= 0 || !runtime) return;
    auto* input = dynamic_cast<rex::input::InputSystem*>(runtime->input_system());
    if (!input) return;
    auto driver = std::make_unique<RemotePadDriver>(window, port);
    if (driver->Setup() != X_STATUS_SUCCESS) { Note("NB virtual controller: could not open UDP port", port); return; }
    input->AddDriver(std::move(driver));
}

}  // namespace nb
