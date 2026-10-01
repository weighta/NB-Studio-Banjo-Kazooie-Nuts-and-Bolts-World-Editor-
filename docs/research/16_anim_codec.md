# 16 — Animation keyframe codec (aid_anim_*, QUAT_BITSTREAM / BITSTREAM)

Status: **decoded.** The bit layout is taken from the game's decoder and checked on all 5 911 cached anims. The
semantics of the translation channels and the runtime interpolation are partly hypotheses; section 9 separates the
two.

Tool: `tools/probe/animdecode.py` (read-only, pure Python):

```
python tools/probe/animdecode.py work/anim_banjo_wrencharoundinto.bin --track 8 --frames   # keys + per-frame quats
python tools/probe/animdecode.py work/anim_banjo_wrencharoundinto.bin --layout             # descriptor + checks
python tools/probe/animdecode.py --bundle 685374 --name wrenchwhirl --track 0              # from the cache CAFF
python tools/probe/animdecode.py --bundle 685374 --all                                    # stats for one bundle
python tools/probe/animdecode.py --bundle all                                             # every cached anim (~1.5 min)
python tools/probe/animdecode.py --bundle 685374 --chain attacks_wrencharoundinto attacks_wrencharound
```

## 1. Anim header (plain .data offsets)

| off | type | meaning |
|---|---|---|
| 0x00 | char[10] | "animation " |
| 0x0A | char[] | version "19.12.06.0036" (all 5 911 anims) |
| 0x1C | f32 | duration (s) = (frames-1)/30 |
| 0x20 | u16 | track count (= joints of the character's skeleton) |
| 0x22 | u16 | frame count (30 fps) |
| 0x24 | u32 | codec block: joint transforms (QUAT_BITSTREAM or QUAT_BITSTREAM32), always 0x64 |
| 0x28 | u32 | codec block: float-channel stream (BITSTREAM / BITSTREAM32) |
| 0x2C | u32 | codec block: third stream (BITSTREAM[32]). Its data pointer was null in every anim |
| 0x34, 0x38, 0x3C | u32 | other tables (not decoded; +0x34 looks like a per-track bitmask) |
| 0x48 | u32 | embedded "pose" object (FORMATS §16) |
| 0x4C | u32 | u16 list (20 entries in the sample, 0..175; purpose unknown) |
| 0x50 | u16,u16 | 0x000A, 0x001E (30 = fps) |
| 0x5C | f32 | **key rate** = keys per frame: 0.5 (4 860 anims) or 1.0 (1 051 anims) |

The sampler 0x822418E8 (vtable 0x821A4C58) loads `anim+0x24/+0x28/+0x2C` in turn and calls the codec's sample
function through the table in section 2 (dispatcher 0x82241818). It reads the key rate `anim+0x5C` in 0x82240DC8.

## 2. Codec blocks and the codec table

Each codec block in the file is 0x1C bytes: `char name[0x16]; u16 typeIndex (0 in the file, set at load by the
name lookup 0x8299F410); u32 data` (the offset of the descriptor D, or 0 when the stream is empty).

The codec table is at **0x82D868A0**. It has 8 entries of 0x24 bytes, each `char name[0x18]` followed by 3 function
pointers: `sample` (called via +0x18 by 0x82241818), `size` (+0x1C, 0x8227B698) and `copy` (+0x20, 0x8227B728):

| idx | name | sample | size / copy |
|---|---|---|---|
| 0 | QUAT_BITSTREAM | 0x8223C238 | 0x8299FF78 / 0x8299FFB0 |
| 1 | BITSTREAM | 0x8223C158 | 0x8299FF78 / 0x8299FF90 |
| 2 | QUAT_UNCOMPRESSED | 0x8299FCD8 | – |
| 3 | UNCOMPRESSED | 0x8299F870 | – |
| 4 | QUAT_BITSTREAM32 | 0x8223C238 (same) | same as 0 |
| 5 | BITSTREAM32 | 0x8223C158 (same) | same as 1 |
| 6 | QUAT_VARIABLE | 0x8299F770 | 0x8299F668 / 0x8299F680 |
| 7 | VARIABLE | 0x8299F6A8 | same as 6 |

Across the 5 911 anims only these combinations occur: QUAT_BITSTREAM32 (4 243) or QUAT_BITSTREAM (1 668) at +0x24,
BITSTREAM[32] at +0x28 (4 603 with data, 1 308 without), and an empty BITSTREAM[32] at +0x2C. The "32" variants
differ only in the descriptor byte `wide` (D+0x26). They share the same functions.

## 3. Descriptor D (the codec block's `data`)

| off | type | meaning |
|---|---|---|
| 0x00 | f32 | 1.0 in every anim (unused by the decoder paths read) |
| 0x04 | u32 → f32[nch] | **scale** = quantisation step for each channel |
| 0x08 | u32 → f32[nch] | **default** value for each channel (used when a channel is not stored) |
| 0x0C | u32 → u8[] | **flag bytes** (section 4) |
| 0x10 | u32 → | start of **bases**. Key data follows the bases and widths |
| 0x14 | u32 → | start of **widths** (= D.0x10 + baseBytes) |
| 0x18 | u16 | baseBytes (4-byte padded) |
| 0x1A | u16 | widthBytes (4-byte padded) |
| 0x1C | u16 | **keyStride**: bytes per key |
| 0x1E | u16 | nch = channels per track (9 for QUAT streams, 1..54 for BITSTREAM) |
| 0x20 | u16 | output floats per track (12 for QUAT, = nch for BITSTREAM) |
| 0x22 | u16 | track count (175 for Banjo, 1 for BITSTREAM) |
| 0x24 | u16 | **nkeys** |
| 0x26 | u8 | **wide**: 0 = s16 bases + 4-bit widths, 1 = s32 bases + 8-bit widths (the *32 variants) |

Key `k` starts at byte `D.0x10 + baseBytes + widthBytes + k*keyStride` (0x829A15F8: `frame*[D+0x1C] + [D+0x1A] +
[D+0x18] + [D+0x10]`; the fast path 0x829A2558 uses the same formula for 4 keys).

Sample (wrencharoundinto, D = 0x80): scales at 0xA8 = {1/32767 ×3, 2⁻¹⁵ ×3, 2⁻¹⁴ ×3}, defaults at 0xCC =
{0,0,0, 0,0,0, 1,1,1}, flags 0xF0, bases 0x204 (0x460 bytes), widths 0x664 (0xD8 bytes), key data 0x73C, stride
0x2C1, 5 keys, so the last key ends at 0x1501 (followed by the 3-byte word pad before BITSTREAM at 0x1504).
Scales vary between anims: rotation 1/32767, 1/16383 or 1/4095; translation 2⁻¹³…2⁻¹⁶; scale 2⁻¹³…2⁻¹⁸. Defaults are
always 0,0,0,0,0,0,1,1,1 for QUAT streams.

## 4. Channels, flag bytes and the per-track walk (0x829A1970, 0x829A16B8)

A track's channels are handled in **groups of 9** (for QUAT: 0-2 = rotation x,y,z, 3-5 = translation x,y,z,
6-8 = scale x,y,z). The per-track channel counter restarts at 0 for each track. Each group reads one flag byte F0 from
the flag stream, plus 0-2 more:

* F0 bits 7,6,5 = channel 0,1,2 **stored**; bits 4,3,2 = channel 0,1,2 **static** (only counted when stored);
  bits 1..0 = **mode** for the two remaining triples:
  * 0: channels 3-8 take the defaults (1 flag byte);
  * 1: 3-5 default, 6-8 described by F1 (2 bytes);
  * 2: 3-5 described by F1, 6-8 default (2 bytes);
  * 3: 3-5 by F1, 6-8 by F2 (3 bytes).
* F1/F2 use the same bits 7..2 for their three channels (their low bits are ignored).

For each channel (0x829A16B8, loop mask 0x80,0x40,0x20):

* **not stored** → `value = default[ch]` (0x829A18C0);
* **stored + static** → read the next base (s16, or s32 if wide) → `value = base * scale[ch]` for every key
  (0x829A173C..0x829A178C). No width and no key bits;
* **stored + animated** → read the next base; read the next width nibble `n = nibble + 1` (low nibble first, then high
  nibble of the same byte, toggle byte ctx+0x35; if wide, `n = byte + 1`); then read `n` bits from **each key's**
  bitstream: `value_k = (bits_k + base) * scale[ch]` (0x829A17CC → 0x829A1538; the default path 0x829A1888 and the
  interpolation callbacks 0x8299FFD0 / 0x829A0018 all compute `(bits + base) * scale`).

Bases and widths are single streams read in channel order over all tracks. Each key has its own bit cursor, and all
cursors advance by the same `n` per animated channel. So a key's bitstream is the concatenation of every animated
channel's delta in track/channel order. The deltas are unsigned (the encoder stores min as `base`; every animated
channel has a key with delta 0).

## 5. Bit reader (0x829A1538)

The cursor is `(word pointer, bit offset)`. A byte address `a` becomes `ptr = a & ~3`, `off = (a & 3) * 8`
(0x829A168C `rlwinm r9,r9,3,27,28`). Reading `n ≤ 32` bits:

```
w0 = BE u32 at ptr ; w1 = BE u32 at ptr+4
v  = ((w0 >> off) | ((w1 << 1) << (31 - off))) & ((1 << n) - 1)
off += n ; ptr += 4 * (off >> 5) ; off &= 31
```

In other words the data is an LSB-first little-endian bitstream that was byte-swapped per 32-bit word for the 360.
The file tail shows this too: the sample's last key ends mid-word, and the pad bytes sit at the *start* of that
word (`00 00 00 07` at 0x1500).

## 6. Key ↔ frame mapping (0x82240DC8, ceil helper 0x82240D58)

`nkeys = ceil((frames-1) * keyRate + 1)`, `step = round(1/keyRate)`, and key `k` sits at frame `min(k*step, frames-1)`.
The last key is always the last frame. Sample: 8 frames, rate 0.5 → keys at frames 0,2,4,6,7. The runtime places a
sample time between keys k and k+1 and computes t, with a special case for the short last interval (0x82240F10).
This formula matches D.0x24 in all 5 911 anims.

## 7. Output and quaternion reconstruction

The sampler writes 12 floats per track: `[qx,qy,qz,qw, tx,ty,tz,-, sx,sy,sz,-]`. The post-callback tables are
0x82D86A80 (nearest) and 0x82D86AB0 (linear). The channel-2 callback rebuilds w and sets the output index to 4; the
channel-5 callback 0x8299FF58 sets it to 8. Disabled tracks get the identity row at 0x82D86AE0.

* **w = sqrt(1 − x² − y² − z²), or 0 if that is ≤ 0.** So w ≥ 0 is canonical: 0x8299F4A0 (fsqrts via 0x8223B6E8).
  The linear variant 0x8299F520 rebuilds w for both keys, then slerps (0x829A0D30: shortest path, falls back to lerp
  when dot ≥ 0.99999). The QUAT_UNCOMPRESSED samplers 0x8299F9F0/0x8299FAF8 use the same xyz-only storage (VMX
  rsqrte).
* Interpolation mode = sampler+0xB8 (0x829F4860), read in 0x8223C238:
  * 0 → key mask 2 (one key, nearest);
  * 1 → key mask 6 + the lerp callback table 0x82D869F0;
  * ≥2 → key mask 15 (4 keys). This uses the VMX fast path 0x829A2848 when not wide, with Catmull-Rom constants
    0.5/1.5/2/2.5; the scalar version is 0x829A0058.

## 8. BITSTREAM[32] stream (+0x28)

This stream uses the same descriptor and the same decoder, with one track, nch = 1..54 channels (Banjo 30), scale 1.0
and default 0 in the samples seen. Its channels are groups of 9 with the same flag/mode rules (0x829A1970 loops
`ch += 9`). In the sample it holds 30 channels, all at their default (no data). Meaning of the channels: see
section 9.

## 9. Validation, and what is verified vs hypothesised

**Verified (code + data):**

* The layout (descriptor fields, flag bits, base/width streams, bit reader, key addressing), from the disassembly
  listed above.
* The stream sizes add up exactly in **all 5 911 anims and all their codec streams** (`--bundle all`: 0 layout
  mismatches):
  * the flag walk ends at the base region;
  * the number of bases equals baseBytes (4-padded);
  * the number of animated channels equals the width nibbles/bytes (4-padded, high nibble of an odd last byte = 0);
  * the sum of widths equals keyStride×8 rounded up.

  Sample: 175 tracks, 560 stored channels (427 animated + 133 static), 1 120 base bytes, 214/216 width bytes, 5 635
  bits/key in a 705-byte stride.
* Every animated channel in the sample has minimum delta 0 and maximum in `[2^(n-1), 2^n)`: the width is exactly
  the minimal one, which random misalignment could not produce.
* No key in any anim has x²+y²+z² > 1 (0 of all keys), so every reconstructed quaternion is unit length.
* **Animation chains meet.** The last key of `attacks_wrencharoundinto` equals the first key of
  `attacks_wrencharound`: 169/175 tracks within 1°, median 0°, translation Δ ≤ 0.011. For example track 8 is
  (0.51317,−0.02149,0.16071,0.84283) vs (0.51320,−0.02152,0.16068,0.84282). whirlinto→whirl gives 164/175 and
  around→outof 159/175. The control pair around-into→whirl gives only 42/175 (median 16°).
* Smoothness: consecutive keys change smoothly, and scale ramps are monotonic (e.g. the sample's joints 57, 69, 80,
  89–99 all scale 0.09→0.38→0.70→0.93→0.99). Large key-to-key steps (up to ~100° in the sample, 180° over all anims) happen on
  single-axis 16-bit tracks, which look like eyelid/jaw joints, and in short bursts. They are real data, because the
  width fits the range exactly.

**Encoder quirks (verified in data, harmless):**

* In 6 anims (e.g. `banjo_gunturret_eggturret_sad`, `drivesad`) the widths add up to more than keyStride×8. The
  excess comes only from trailing width-1 channels (nibble 0 = constant channels that the encoder wrote as 0 bits).
  The runtime reads 1 bit of the next key there, which is at most 1 quantum (3e-5) of error. The tool reproduces this
  bit-exactly.
* Some channels have width one larger than needed, with max = 2^(n-1)−1.

**Hypotheses / not traced:**

* **Translation channels are offsets added to the joint's local bind translation**, not absolute. Evidence: the
  default is 0, only 8/175 sample tracks store translation, and the stored values are small (root ≈ 0…0.08 vs bind
  y = 0.78). The consuming code (pose blend after 0x822418E8) was not traced.
* **Rotation relative to or absolute over bind: undetermined.** The embedded pose has identity quaternions for Banjo
  and for almost all characters, so both readings are the same in practice.
* Scale is presumably multiplicative (default 1).
* The exact runtime interpolation per mode (section 7) is inferred from the callback tables, not traced
  end-to-end. The tool's per-frame output uses slerp between keys (mode 1).
* BITSTREAM float channels (scale 1.0, integer-valued): probably facial-texture / visibility / event channels. Not
  identified.
* D+0x00 (1.0) and anim header +0x34/+0x38/+0x3C/+0x4C tables are not decoded.

## 10. Next steps

* Map BITSTREAM channel indices (the +0x3C table may name them: it holds 0x1E and a pointer).
* Trace the pose-blend code that consumes the 12-float rows, to settle translation and rotation composition.
* Export to FBX/BVH with the skeleton (NB.Core port of animdecode.py). Writing anims is also possible: widths, bases
  and bitstreams are simple enough to re-encode.
