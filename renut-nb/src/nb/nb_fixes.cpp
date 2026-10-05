// NB fixes for game races that reNut's timing exposes (config/nb_fixes.toml).
#include <rex/ppc.h>

// 0x823642D8 lwz r3,0x24(r31) with r31 = 0 (the slot is not in use yet): skip to the unlock at 0x82364340.
bool nbfix_empty_slot(PPCRegister& r31)
{
    return r31.u32 == 0;
}
