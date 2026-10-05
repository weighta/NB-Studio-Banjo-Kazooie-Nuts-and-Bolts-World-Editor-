// NB exe-mod interpreter for reNut (see nbpatch.h). Integer, load/store, branch, call, float and vector load/store
// instructions: everything NB's executable mods use, implemented the way the generated code does them (same CR
// compare helper, same vector byte order). Unknown instructions are logged once and the original code runs instead.
#include "nbpatch.h"
#include "generated/renut_pch.h"

#include <cmath>
#include <cstdio>
#include <cstring>
#include <mutex>
#include <unordered_set>
#include <vector>
#if defined(_WIN32)
#include <windows.h>
#endif

namespace nbpatch {
namespace {

inline uint8_t* Host(uint8_t* base, uint32_t a) { return base + a + REX_PHYS_HOST_OFFSET(a); }
inline uint8_t  L8(uint8_t* b, uint32_t a) { return *(volatile uint8_t*)Host(b, a); }
inline uint16_t L16(uint8_t* b, uint32_t a) { return __builtin_bswap16(*(volatile uint16_t*)Host(b, a)); }
inline uint32_t L32(uint8_t* b, uint32_t a) { return __builtin_bswap32(*(volatile uint32_t*)Host(b, a)); }
inline uint64_t L64(uint8_t* b, uint32_t a) { return __builtin_bswap64(*(volatile uint64_t*)Host(b, a)); }
inline void S8(uint8_t* b, uint32_t a, uint8_t v) { *(volatile uint8_t*)Host(b, a) = v; }
inline void S16(uint8_t* b, uint32_t a, uint16_t v) { *(volatile uint16_t*)Host(b, a) = __builtin_bswap16(v); }
inline void S32(uint8_t* b, uint32_t a, uint32_t v) { *(volatile uint32_t*)Host(b, a) = __builtin_bswap32(v); }
inline void S64(uint8_t* b, uint32_t a, uint64_t v) { *(volatile uint64_t*)Host(b, a) = __builtin_bswap64(v); }

void Log(const char* fmt, uint32_t a, uint32_t w)
{
    char buf[256];
    std::snprintf(buf, sizeof buf, fmt, a, w);
    std::fprintf(stderr, "%s\n", buf);
#if defined(_WIN32)
    OutputDebugStringA(buf);
#endif
    static std::mutex m;
    std::lock_guard<std::mutex> g(m);
    if (FILE* f = std::fopen("nbpatch.log", "a")) { std::fprintf(f, "%s\n", buf); std::fclose(f); }
}

// A compiled function starting exactly at `a`, or null (mid-function address, mod code in padding).
PPCFunc* Compiled(uint8_t* base, uint32_t a)
{
    if ((uint32_t)(a - REX_CODE_BASE) >= REX_CODE_SIZE) return nullptr;
    return REX_LOOKUP_FUNC(base, a);
}

struct Machine
{
    PPCContext& ctx;
    uint8_t* base;
    PPCRegister* r[32];
    PPCRegister* f[32];
    PPCVRegister* v[128];
    PPCCRRegister* cr[8];
    std::vector<uint32_t> returns;   // interpreted calls (mod code calling mod code)

    Machine(PPCContext& c, uint8_t* b) : ctx(c), base(b)
    {
        PPCRegister* gp[32] = { &c.r0, &c.r1, &c.r2, &c.r3, &c.r4, &c.r5, &c.r6, &c.r7, &c.r8, &c.r9, &c.r10, &c.r11, &c.r12, &c.r13,
            &c.r14, &c.r15, &c.r16, &c.r17, &c.r18, &c.r19, &c.r20, &c.r21, &c.r22, &c.r23, &c.r24, &c.r25, &c.r26, &c.r27,
            &c.r28, &c.r29, &c.r30, &c.r31 };
        std::memcpy(r, gp, sizeof r);
        for (int i = 0; i < 32; ++i) f[i] = &c.f0 + i;
        for (int i = 0; i < 128; ++i) v[i] = &c.v0 + i;
        PPCCRRegister* c8[8] = { &c.cr0, &c.cr1, &c.cr2, &c.cr3, &c.cr4, &c.cr5, &c.cr6, &c.cr7 };
        std::memcpy(cr, c8, sizeof cr);
    }

    bool CrBit(int bi) const
    {
        const PPCCRRegister& c = *cr[bi >> 2];
        switch (bi & 3) { case 0: return c.lt; case 1: return c.gt; case 2: return c.eq; default: return c.so; }
    }
    void SetCrBit(int bi, bool val)
    {
        PPCCRRegister& c = *cr[bi >> 2];
        switch (bi & 3) { case 0: c.lt = val; break; case 1: c.gt = val; break; case 2: c.eq = val; break; default: c.so = val; break; }
    }
    void Rc(uint64_t value) { cr[0]->compare<int32_t>((int32_t)(uint32_t)value, 0, ctx.xer); }

    // Branch condition (BO, BI) with the CTR decrement.
    bool Cond(int bo, int bi)
    {
        bool ctr_ok = true;
        if (!(bo & 4)) { ctx.ctr.u64 -= 1; ctr_ok = (bo & 2) ? (ctx.ctr.u32 == 0) : (ctx.ctr.u32 != 0); }
        bool cond_ok = (bo & 16) || (CrBit(bi) == (((bo >> 3) & 1) != 0));
        return ctr_ok && cond_ok;
    }

    static uint32_t Mask(int mb, int me)
    {
        uint32_t m = mb <= me ? ((0xFFFFFFFFu >> mb) & (0xFFFFFFFFu << (31 - me))) : ((0xFFFFFFFFu >> mb) | (0xFFFFFFFFu << (31 - me)));
        return m;
    }
    static uint32_t Rotl(uint32_t x, int n) { n &= 31; return n ? (x << n) | (x >> (32 - n)) : x; }

    void LoadVec(int vd, uint32_t ea)
    {
        uint8_t* src = Host(base, ea & ~0xFu);
        for (int i = 0; i < 16; ++i) v[vd]->u8[i] = src[15 - i];
    }
    void StoreVec(int vs, uint32_t ea)
    {
        uint8_t* dst = Host(base, ea & ~0xFu);
        for (int i = 0; i < 16; ++i) dst[i] = v[vs]->u8[15 - i];
    }
};

constexpr uint32_t kUnknown = 0xFFFFFFFDu;

// Executes one instruction at pc. Returns the next pc, or kUnknown. Branches are left to the caller (returns pc
// unchanged with `branch` set).
struct Branch { bool taken = false; bool link = false; bool to_lr = false; bool to_ctr = false; uint32_t target = 0; };

uint32_t Step(Machine& m, uint32_t pc, uint32_t w, Branch& br)
{
    auto& c = m.ctx;
    auto& r = m.r;
    const int op = w >> 26, rD = (w >> 21) & 31, rA = (w >> 16) & 31, rB = (w >> 11) & 31;
    const uint32_t uimm = w & 0xFFFF;
    const int32_t simm = (int16_t)(w & 0xFFFF);
    auto A0 = [&]() -> uint32_t { return rA ? r[rA]->u32 : 0; };
    switch (op)
    {
    case 7: r[rD]->s64 = (int64_t)r[rA]->s64 * simm; return pc + 4;                               // mulli
    case 8: { uint64_t a = r[rA]->u64; r[rD]->u64 = (uint64_t)(int64_t)simm - a; c.xer.ca = (uint32_t)a <= (uint32_t)simm; return pc + 4; } // subfic
    case 10: if ((w >> 21) & 1) m.cr[rD >> 2]->compare<uint64_t>(r[rA]->u64, uimm, c.xer); else m.cr[rD >> 2]->compare<uint32_t>(r[rA]->u32, uimm, c.xer); return pc + 4;
    case 11: if ((w >> 21) & 1) m.cr[rD >> 2]->compare<int64_t>(r[rA]->s64, simm, c.xer); else m.cr[rD >> 2]->compare<int32_t>(r[rA]->s32, simm, c.xer); return pc + 4;
    case 12: case 13: { uint64_t a = r[rA]->u64; r[rD]->u64 = a + (int64_t)simm; c.xer.ca = (uint32_t)r[rD]->u32 < (uint32_t)a; if (op == 13) m.Rc(r[rD]->u64); return pc + 4; }
    case 14: r[rD]->s64 = (rA ? r[rA]->s64 : 0) + simm; return pc + 4;                                 // addi / li
    case 15: r[rD]->s64 = (rA ? r[rA]->s64 : 0) + ((int64_t)simm << 16); return pc + 4;                // addis / lis
    case 24: r[rA]->u64 = r[rD]->u64 | uimm; return pc + 4;                                            // ori (nop)
    case 25: r[rA]->u64 = r[rD]->u64 | ((uint64_t)uimm << 16); return pc + 4;                          // oris
    case 26: r[rA]->u64 = r[rD]->u64 ^ uimm; return pc + 4;                                            // xori
    case 27: r[rA]->u64 = r[rD]->u64 ^ ((uint64_t)uimm << 16); return pc + 4;                          // xoris
    case 28: r[rA]->u64 = r[rD]->u64 & uimm; m.Rc(r[rA]->u64); return pc + 4;                          // andi.
    case 29: r[rA]->u64 = r[rD]->u64 & ((uint64_t)uimm << 16); m.Rc(r[rA]->u64); return pc + 4;       // andis.
    case 20: { int sh = rB, mb = (w >> 6) & 31, me = (w >> 1) & 31; uint32_t mk = Machine::Mask(mb, me);  // rlwimi
        r[rA]->u64 = (Machine::Rotl(r[rD]->u32, sh) & mk) | (r[rA]->u32 & ~mk); if (w & 1) m.Rc(r[rA]->u64); return pc + 4; }
    case 21: { int sh = rB, mb = (w >> 6) & 31, me = (w >> 1) & 31;                                   // rlwinm
        r[rA]->u64 = Machine::Rotl(r[rD]->u32, sh) & Machine::Mask(mb, me); if (w & 1) m.Rc(r[rA]->u64); return pc + 4; }
    case 23: { int mb = (w >> 6) & 31, me = (w >> 1) & 31;                                            // rlwnm
        r[rA]->u64 = Machine::Rotl(r[rD]->u32, r[rB]->u32 & 31) & Machine::Mask(mb, me); if (w & 1) m.Rc(r[rA]->u64); return pc + 4; }
    case 32: r[rD]->u64 = L32(m.base, A0() + simm); return pc + 4;                                     // lwz
    case 33: { uint32_t ea = r[rA]->u32 + simm; r[rD]->u64 = L32(m.base, ea); r[rA]->u64 = ea; return pc + 4; }
    case 34: r[rD]->u64 = L8(m.base, A0() + simm); return pc + 4;                                      // lbz
    case 35: { uint32_t ea = r[rA]->u32 + simm; r[rD]->u64 = L8(m.base, ea); r[rA]->u64 = ea; return pc + 4; }
    case 36: S32(m.base, A0() + simm, r[rD]->u32); return pc + 4;                                      // stw
    case 37: { uint32_t ea = r[rA]->u32 + simm; S32(m.base, ea, r[rD]->u32); r[rA]->u64 = ea; return pc + 4; }
    case 38: S8(m.base, A0() + simm, r[rD]->u8); return pc + 4;                                        // stb
    case 39: { uint32_t ea = r[rA]->u32 + simm; S8(m.base, ea, r[rD]->u8); r[rA]->u64 = ea; return pc + 4; }
    case 40: r[rD]->u64 = L16(m.base, A0() + simm); return pc + 4;                                     // lhz
    case 41: { uint32_t ea = r[rA]->u32 + simm; r[rD]->u64 = L16(m.base, ea); r[rA]->u64 = ea; return pc + 4; }
    case 42: r[rD]->s64 = (int16_t)L16(m.base, A0() + simm); return pc + 4;                            // lha
    case 44: S16(m.base, A0() + simm, r[rD]->u16); return pc + 4;                                      // sth
    case 45: { uint32_t ea = r[rA]->u32 + simm; S16(m.base, ea, r[rD]->u16); r[rA]->u64 = ea; return pc + 4; }
    case 46: { uint32_t ea = A0() + simm; for (int i = rD; i < 32; ++i, ea += 4) r[i]->u64 = L32(m.base, ea); return pc + 4; }  // lmw
    case 47: { uint32_t ea = A0() + simm; for (int i = rD; i < 32; ++i, ea += 4) S32(m.base, ea, r[i]->u32); return pc + 4; }    // stmw
    case 48: { uint32_t b = L32(m.base, A0() + simm); float x; std::memcpy(&x, &b, 4); m.f[rD]->f64 = x; return pc + 4; }      // lfs
    case 50: m.f[rD]->u64 = L64(m.base, A0() + simm); return pc + 4;                                   // lfd
    case 52: { float x = (float)m.f[rD]->f64; uint32_t b; std::memcpy(&b, &x, 4); S32(m.base, A0() + simm, b); return pc + 4; } // stfs
    case 54: S64(m.base, A0() + simm, m.f[rD]->u64); return pc + 4;                                    // stfd
    case 58: { int32_t ds = (int16_t)(w & 0xFFFC);                                                     // ld / ldu / lwa
        switch (w & 3) {
        case 0: r[rD]->u64 = L64(m.base, A0() + ds); return pc + 4;
        case 1: { uint32_t ea = r[rA]->u32 + ds; r[rD]->u64 = L64(m.base, ea); r[rA]->u64 = ea; return pc + 4; }
        case 2: r[rD]->s64 = (int32_t)L32(m.base, A0() + ds); return pc + 4;
        }
        return kUnknown; }
    case 62: { int32_t ds = (int16_t)(w & 0xFFFC);                                                     // std / stdu
        if ((w & 3) == 0) { S64(m.base, A0() + ds, r[rD]->u64); return pc + 4; }
        if ((w & 3) == 1) { uint32_t ea = r[rA]->u32 + ds; S64(m.base, ea, r[rD]->u64); r[rA]->u64 = ea; return pc + 4; }
        return kUnknown; }
    case 18: {                                                                                         // b / bl / ba
        int32_t li = w & 0x3FFFFFC; if (li & 0x2000000) li -= 0x4000000;
        br.taken = true; br.link = w & 1; br.target = (w & 2) ? (uint32_t)li : pc + li; return pc; }
    case 16: {                                                                                         // bc
        int32_t bd = (int16_t)(w & 0xFFFC);
        if (m.Cond(rD, rA)) { br.taken = true; br.link = w & 1; br.target = (w & 2) ? (uint32_t)bd : pc + bd; return pc; }
        if (w & 1) c.lr = pc + 4;
        return pc + 4; }
    case 19: {
        int xo = (w >> 1) & 0x3FF;
        if (xo == 16) {                                                                                // bclr
            if (m.Cond(rD, rA)) { br.taken = true; br.to_lr = true; br.link = w & 1; br.target = (uint32_t)c.lr; return pc; }
            return pc + 4; }
        if (xo == 528) {                                                                               // bcctr
            bool ok = (rD & 16) || (m.CrBit(rA) == (((rD >> 3) & 1) != 0));
            if (ok) { br.taken = true; br.to_ctr = true; br.link = w & 1; br.target = c.ctr.u32; return pc; }
            return pc + 4; }
        if (xo == 0) { m.cr[rD >> 2]->set_raw(m.cr[rA >> 2]->raw()); return pc + 4; }                // mcrf
        if (xo == 150) return pc + 4;                                                                  // isync
        int ba = rA, bb = rB; bool a = m.CrBit(ba), b = m.CrBit(bb), res;
        switch (xo) {
        case 257: res = a && b; break; case 129: res = a && !b; break; case 289: res = a == b; break;   // crand crandc creqv
        case 225: res = !(a && b); break; case 33: res = !(a || b); break; case 449: res = a || b; break; // crnand crnor cror
        case 417: res = a || !b; break; case 193: res = a != b; break;                                 // crorc crxor
        default: return kUnknown; }
        m.SetCrBit(rD, res); return pc + 4; }
    case 30: {                                                                                         // rldicl / rldicr
        int sh = rB | (((w >> 1) & 1) << 5), mbe = ((w >> 6) & 31) | (((w >> 5) & 1) << 5);
        uint64_t x = r[rD]->u64; uint64_t rot = sh ? (x << sh) | (x >> (64 - sh)) : x;
        switch ((w >> 2) & 7) {
        case 0: r[rA]->u64 = rot & (~0ull >> mbe); break;
        case 1: r[rA]->u64 = rot & (~0ull << (63 - mbe)); break;
        default: return kUnknown; }
        if (w & 1) m.Rc(r[rA]->u64); return pc + 4; }
    case 31: {
        int xo = (w >> 1) & 0x3FF; bool rc = w & 1;
        uint32_t ea = A0() + r[rB]->u32;
        switch (xo) {
        case 0: if ((w >> 21) & 1) m.cr[rD >> 2]->compare<int64_t>(r[rA]->s64, r[rB]->s64, c.xer); else m.cr[rD >> 2]->compare<int32_t>(r[rA]->s32, r[rB]->s32, c.xer); return pc + 4;
        case 32: if ((w >> 21) & 1) m.cr[rD >> 2]->compare<uint64_t>(r[rA]->u64, r[rB]->u64, c.xer); else m.cr[rD >> 2]->compare<uint32_t>(r[rA]->u32, r[rB]->u32, c.xer); return pc + 4;
        case 266: r[rD]->u64 = r[rA]->u64 + r[rB]->u64; if (rc) m.Rc(r[rD]->u64); return pc + 4;      // add
        case 10: { uint64_t a = r[rA]->u64; r[rD]->u64 = a + r[rB]->u64; c.xer.ca = r[rD]->u32 < (uint32_t)a; return pc + 4; } // addc
        case 138: { uint64_t s = (uint64_t)r[rA]->u32 + r[rB]->u32 + c.xer.ca; r[rD]->u64 = r[rA]->u64 + r[rB]->u64 + c.xer.ca; c.xer.ca = s >> 32; return pc + 4; } // adde
        case 40: r[rD]->u64 = r[rB]->u64 - r[rA]->u64; if (rc) m.Rc(r[rD]->u64); return pc + 4;       // subf
        case 8: { r[rD]->u64 = r[rB]->u64 - r[rA]->u64; c.xer.ca = r[rB]->u32 >= r[rA]->u32; return pc + 4; } // subfc
        case 104: r[rD]->s64 = -r[rA]->s64; if (rc) m.Rc(r[rD]->u64); return pc + 4;                   // neg
        case 28: r[rA]->u64 = r[rD]->u64 & r[rB]->u64; if (rc) m.Rc(r[rA]->u64); return pc + 4;       // and
        case 60: r[rA]->u64 = r[rD]->u64 & ~r[rB]->u64; if (rc) m.Rc(r[rA]->u64); return pc + 4;      // andc
        case 444: r[rA]->u64 = r[rD]->u64 | r[rB]->u64; if (rc) m.Rc(r[rA]->u64); return pc + 4;      // or / mr
        case 412: r[rA]->u64 = r[rD]->u64 | ~r[rB]->u64; if (rc) m.Rc(r[rA]->u64); return pc + 4;     // orc
        case 316: r[rA]->u64 = r[rD]->u64 ^ r[rB]->u64; if (rc) m.Rc(r[rA]->u64); return pc + 4;      // xor
        case 124: r[rA]->u64 = ~(r[rD]->u64 | r[rB]->u64); if (rc) m.Rc(r[rA]->u64); return pc + 4;   // nor
        case 476: r[rA]->u64 = ~(r[rD]->u64 & r[rB]->u64); if (rc) m.Rc(r[rA]->u64); return pc + 4;   // nand
        case 235: r[rD]->s64 = (int64_t)r[rA]->s32 * r[rB]->s32; if (rc) m.Rc(r[rD]->u64); return pc + 4; // mullw
        case 75: r[rD]->s64 = ((int64_t)r[rA]->s32 * r[rB]->s32) >> 32; return pc + 4;                // mulhw
        case 11: r[rD]->u64 = ((uint64_t)r[rA]->u32 * r[rB]->u32) >> 32; return pc + 4;              // mulhwu
        case 491: r[rD]->s64 = r[rB]->s32 ? r[rA]->s32 / r[rB]->s32 : 0; if (rc) m.Rc(r[rD]->u64); return pc + 4; // divw
        case 459: r[rD]->u64 = r[rB]->u32 ? r[rA]->u32 / r[rB]->u32 : 0; if (rc) m.Rc(r[rD]->u64); return pc + 4; // divwu
        case 24: { uint32_t n = r[rB]->u32 & 63; r[rA]->u64 = n > 31 ? 0 : (uint32_t)(r[rD]->u32 << n); if (rc) m.Rc(r[rA]->u64); return pc + 4; } // slw
        case 536: { uint32_t n = r[rB]->u32 & 63; r[rA]->u64 = n > 31 ? 0 : (r[rD]->u32 >> n); if (rc) m.Rc(r[rA]->u64); return pc + 4; }           // srw
        case 824: { int n = rB; int32_t x = r[rD]->s32; r[rA]->s64 = x >> n; c.xer.ca = x < 0 && n && (x & ((1u << n) - 1)); if (rc) m.Rc(r[rA]->u64); return pc + 4; } // srawi
        case 792: { uint32_t n = r[rB]->u32 & 63; int32_t x = r[rD]->s32; int32_t res = n > 31 ? (x < 0 ? -1 : 0) : x >> n;
            c.xer.ca = x < 0 && (n > 31 ? x != 0 : (n && (x & ((1u << n) - 1)))); r[rA]->s64 = res; if (rc) m.Rc(r[rA]->u64); return pc + 4; } // sraw
        case 26: r[rA]->u64 = r[rD]->u32 ? __builtin_clz(r[rD]->u32) : 32; if (rc) m.Rc(r[rA]->u64); return pc + 4; // cntlzw
        case 954: r[rA]->s64 = r[rD]->s8; if (rc) m.Rc(r[rA]->u64); return pc + 4;                    // extsb
        case 922: r[rA]->s64 = r[rD]->s16; if (rc) m.Rc(r[rA]->u64); return pc + 4;                   // extsh
        case 986: r[rA]->s64 = r[rD]->s32; if (rc) m.Rc(r[rA]->u64); return pc + 4;                   // extsw
        case 339: { int spr = rA | (rB << 5);                                                          // mfspr
            if (spr == 8) r[rD]->u64 = c.lr; else if (spr == 9) r[rD]->u64 = c.ctr.u64;
            else if (spr == 1) r[rD]->u64 = ((uint64_t)c.xer.so << 31) | ((uint64_t)c.xer.ov << 30) | ((uint64_t)c.xer.ca << 29);
            else return kUnknown; return pc + 4; }
        case 467: { int spr = rA | (rB << 5);                                                          // mtspr
            if (spr == 8) c.lr = r[rD]->u64; else if (spr == 9) c.ctr.u64 = r[rD]->u64;
            else if (spr == 1) { c.xer.so = (r[rD]->u32 >> 31) & 1; c.xer.ov = (r[rD]->u32 >> 30) & 1; c.xer.ca = (r[rD]->u32 >> 29) & 1; }
            else return kUnknown; return pc + 4; }
        case 19: { uint32_t v = 0; for (int i = 0; i < 8; ++i) v = (v << 4) | m.cr[i]->raw(); r[rD]->u64 = v; return pc + 4; } // mfcr
        case 144: { int crm = (w >> 12) & 0xFF; for (int i = 0; i < 8; ++i) if (crm & (0x80 >> i)) m.cr[i]->set_raw((r[rD]->u32 >> (28 - 4 * i)) & 15); return pc + 4; } // mtcrf
        case 23: r[rD]->u64 = L32(m.base, ea); return pc + 4;                                          // lwzx
        case 55: r[rD]->u64 = L32(m.base, ea); r[rA]->u64 = ea; return pc + 4;                         // lwzux
        case 87: r[rD]->u64 = L8(m.base, ea); return pc + 4;                                           // lbzx
        case 279: r[rD]->u64 = L16(m.base, ea); return pc + 4;                                         // lhzx
        case 343: r[rD]->s64 = (int16_t)L16(m.base, ea); return pc + 4;                                // lhax
        case 21: r[rD]->u64 = L64(m.base, ea); return pc + 4;                                          // ldx
        case 151: S32(m.base, ea, r[rD]->u32); return pc + 4;                                          // stwx
        case 183: S32(m.base, ea, r[rD]->u32); r[rA]->u64 = ea; return pc + 4;                         // stwux
        case 215: S8(m.base, ea, r[rD]->u8); return pc + 4;                                            // stbx
        case 407: S16(m.base, ea, r[rD]->u16); return pc + 4;                                          // sthx
        case 149: S64(m.base, ea, r[rD]->u64); return pc + 4;                                          // stdx
        case 535: { uint32_t b = L32(m.base, ea); float x; std::memcpy(&x, &b, 4); m.f[rD]->f64 = x; return pc + 4; } // lfsx
        case 599: m.f[rD]->u64 = L64(m.base, ea); return pc + 4;                                       // lfdx
        case 663: { float x = (float)m.f[rD]->f64; uint32_t b; std::memcpy(&b, &x, 4); S32(m.base, ea, b); return pc + 4; } // stfsx
        case 727: S64(m.base, ea, m.f[rD]->u64); return pc + 4;                                        // stfdx
        case 103: case 359: m.LoadVec(rD, ea); return pc + 4;                                          // lvx / lvxl
        case 231: case 487: m.StoreVec(rD, ea); return pc + 4;                                         // stvx / stvxl
        case 598: case 854: case 86: case 278: case 470: return pc + 4;                                // sync eieio dcbf dcbt dcbi
        }
        return kUnknown; }
    case 4: {                                                                                          // VMX128 lvx128 / stvx128
        uint32_t mk = w & 0x7F3; int vd = rD | (((w >> 2) & 3) << 5); uint32_t ea = A0() + r[rB]->u32;
        if (mk == 0x0C3 || mk == 0x2C3) { m.LoadVec(vd, ea); return pc + 4; }
        if (mk == 0x1C3 || mk == 0x3C3) { m.StoreVec(vd, ea); return pc + 4; }
        return kUnknown; }
    case 59: case 63: {
        auto& F = m.f;
        int xa = (w >> 1) & 31, rC = (w >> 6) & 31; bool s = op == 59;
        auto fin = [&](double x) { F[rD]->f64 = s ? (double)(float)x : x; };
        switch (xa) {
        case 18: fin(F[rA]->f64 / F[rB]->f64); return pc + 4;
        case 20: fin(F[rA]->f64 - F[rB]->f64); return pc + 4;
        case 21: fin(F[rA]->f64 + F[rB]->f64); return pc + 4;
        case 25: fin(F[rA]->f64 * F[rC]->f64); return pc + 4;
        case 28: fin(F[rA]->f64 * F[rC]->f64 - F[rB]->f64); return pc + 4;
        case 29: fin(F[rA]->f64 * F[rC]->f64 + F[rB]->f64); return pc + 4;
        case 30: fin(-(F[rA]->f64 * F[rC]->f64 - F[rB]->f64)); return pc + 4;
        case 31: fin(-(F[rA]->f64 * F[rC]->f64 + F[rB]->f64)); return pc + 4;
        case 23: if (!s) { F[rD]->f64 = F[rA]->f64 >= 0.0 ? F[rC]->f64 : F[rB]->f64; return pc + 4; } break; // fsel
        case 22: fin(std::sqrt(F[rB]->f64)); return pc + 4;
        }
        if (op == 63) {
            int xo = (w >> 1) & 0x3FF;
            switch (xo) {
            case 72: F[rD]->f64 = F[rB]->f64; return pc + 4;                                           // fmr
            case 40: F[rD]->f64 = -F[rB]->f64; return pc + 4;                                          // fneg
            case 264: F[rD]->f64 = std::fabs(F[rB]->f64); return pc + 4;                               // fabs
            case 136: F[rD]->f64 = -std::fabs(F[rB]->f64); return pc + 4;                              // fnabs
            case 12: F[rD]->f64 = (double)(float)F[rB]->f64; return pc + 4;                            // frsp
            case 15: { double x = F[rB]->f64; int32_t i = std::isnan(x) ? INT32_MIN : x >= 2147483647.0 ? INT32_MAX : x <= -2147483648.0 ? INT32_MIN : (int32_t)x;
                F[rD]->u64 = (uint32_t)i; return pc + 4; }                                             // fctiwz
            case 815: { double x = F[rB]->f64; F[rD]->s64 = std::isnan(x) ? INT64_MIN : (int64_t)x; return pc + 4; } // fctidz
            case 846: F[rD]->f64 = (double)F[rB]->s64; return pc + 4;                                  // fcfid
            case 0: case 32: m.cr[rD >> 2]->compare(F[rA]->f64, F[rB]->f64); return pc + 4;            // fcmpu / fcmpo
            }
        }
        return kUnknown; }
    }
    return kUnknown;
}

bool InExits(uint32_t pc, const uint32_t* exits, int n)
{
    for (int i = 0; i < n; ++i) if (exits[i] == pc) return true;
    return false;
}

// The interpreter loop. `function_mode`: entered through a pointer (RunFunction): returning from the entry ends it.
uint32_t Execute(Machine& m, uint32_t pc, uint32_t site, const uint32_t* exits, int exit_count, bool function_mode)
{
    for (int steps = 0; steps < 2000000; ++steps)
    {
        if (!function_mode && m.returns.empty() && pc != site && InExits(pc, exits, exit_count)) return pc;
        uint32_t w = L32(m.base, pc);
        if (w == 0)
        {
            // empty padding: the mod's code is not (fully) in memory yet. Before anything ran, the original instruction
            // runs instead; later, finishing the function is the least harmful way out.
            static std::unordered_set<uint32_t> told0; static std::mutex tm0;
            { std::lock_guard<std::mutex> g(tm0); if (told0.insert(site).second) Log("nbpatch: site %08X reached empty memory at %08X", site, pc); }
            return pc == site ? kFallback : (steps == 0 ? kFallback : kReturn);
        }
        Branch br;
        uint32_t next = Step(m, pc, w, br);
        if (next == kUnknown)
        {
            static std::unordered_set<uint32_t> told; static std::mutex tm;
            { std::lock_guard<std::mutex> g(tm); if (told.insert(pc).second) Log("nbpatch: unsupported instruction at %08X: %08X", pc, w); }
            return pc == site ? kFallback : kReturn;
        }
        if (!br.taken) { pc = next; continue; }

        uint32_t t = br.target;
        if (br.link)
        {
            // a call: compiled game functions run natively, mod code is interpreted
            m.ctx.lr = pc + 4;
            if (PPCFunc* fn = Compiled(m.base, t)) { fn(m.ctx, m.base); pc += 4; continue; }
            if (br.to_ctr || br.to_lr)
            {
                // an indirect call the dispatcher knows (function pointers set by the game)
                PPCFunc* fn2 = rex::runtime::ResolveIndirectFunction(t);
                if (fn2) { fn2(m.ctx, m.base); pc += 4; continue; }
            }
            m.returns.push_back(pc + 4);
            pc = t;
            continue;
        }
        if (br.to_lr && !m.returns.empty() && t == m.returns.back()) { m.returns.pop_back(); pc = t; continue; }
        if (br.to_lr)
        {
            // returning from the function the patched code is running in
            if (m.returns.empty()) return kReturn;
            m.returns.pop_back(); pc = t; continue;
        }
        // a jump: to a label of the host function (exit), or a tail call into another function
        if (!function_mode && m.returns.empty() && InExits(t, exits, exit_count)) return t;
        PPCFunc* fn = Compiled(m.base, t);
        if (!fn && br.to_ctr) fn = rex::runtime::ResolveIndirectFunction(t);
        if (fn)
        {
            fn(m.ctx, m.base);
            // the callee returned to our caller: emulate its blr
            if (m.returns.empty()) return kReturn;
            pc = m.returns.back(); m.returns.pop_back(); continue;
        }
        pc = t;   // mod code, or original code without its own function (e.g. a shared epilogue): interpret
    }
    Log("nbpatch: patched code at %08X did not finish (%08X)", site, pc);
    return kReturn;
}

}  // namespace

uint32_t Run(PPCContext& ctx, uint8_t* base, uint32_t site, const uint32_t* exits, int exit_count)
{
    Machine m(ctx, base);
    return Execute(m, site, site, exits, exit_count, false);
}

void RunFunction(PPCContext& ctx, uint8_t* base, uint32_t entry)
{
    Machine m(ctx, base);
    Execute(m, entry, entry, nullptr, 0, true);
}

}  // namespace nbpatch
