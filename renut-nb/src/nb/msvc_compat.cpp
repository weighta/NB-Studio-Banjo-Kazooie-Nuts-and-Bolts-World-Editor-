// The ReXGlue SDK's prebuilt libraries were compiled with a newer MSVC STL (14.44+) than some installs ship; this is the
// one vectorized helper they reference that older msvcprt.lib lacks. Same contract: position of the first byte of
// haystack that occurs in needle, or SIZE_MAX when there is none.
#include <cstddef>
#include <cstdint>
#if defined(_WIN32)
extern "C" __declspec(noalias) size_t __stdcall __std_find_first_of_trivial_pos_1(
    const void* haystack, size_t haystack_length, const void* needle, size_t needle_length) noexcept
{
    auto h = static_cast<const uint8_t*>(haystack);
    auto n = static_cast<const uint8_t*>(needle);
    bool set[256] = {};
    for (size_t i = 0; i < needle_length; ++i) set[n[i]] = true;
    for (size_t i = 0; i < haystack_length; ++i)
        if (set[h[i]]) return i;
    return static_cast<size_t>(-1);
}
#endif
