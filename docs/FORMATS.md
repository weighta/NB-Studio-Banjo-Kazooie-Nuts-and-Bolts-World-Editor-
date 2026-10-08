# Banjo-Kazooie: Nuts & Bolts (X360) — file formats



Everything below was established against the unmodified game directory

(`Banjo Kazooie Nuts & Bolts (FRESH)`). Each item is marked:



* **[verified]** — checked on every file of that kind (round-trip, checksum or size accounting) or confirmed in Xenia.

* **[observed]** — seen consistently on the samples examined, not yet proven for every file.

* **[unknown]** — field exists, meaning not established. Preserved verbatim by the tool.



All multi-byte values are big-endian (PowerPC) unless stated.



---



## 1. Directory layout



| Path | Contents | Count | Size |

|---|---|---|---|

| `default.xex` | XEX2 executable, AES-encrypted, basic-compressed | 1 | 16 MB |

| `Bundle/4f/<id>` | Resident bundles (one CAFF each; 142 compressed with xcompress, 9 raw) | 151 | 1.9 GB |

| `Bundle/50/<id>` | Streaming bundles (0x438CB47C archive of CAFFs + XACT wave banks) | 151 | 3.1 GB |

| `Debug/11/xx/yy/zz` | Loose English localisation text assets (CAFF, type 0x11) | 107 | 1.2 MB |

| `Debug/36/xx/yy/zz` | Loose videos: ASF/WMV (type 0x36) | 52 | 1.2 GB |

| `loctext/<language>/<id>` | Localised text CAFFs per language (same ids as Debug/11) | 14×107 | 16 MB |

| `xuifontcachefont/*.sbp` | XUI font glyph caches | 6 | 46 MB |

| `xuifontcachemeta/*.ini` | Font cache metadata (text) | 12 | — |

| `$SystemUpdate/` | Console system update, not game data | 1 | — |

| `tile.bmp` | Dashboard tile image | 1 | — |



### IDs and names **[verified]**



A 32-bit id is `typeByte << 24 | hash24`, where

`hash24 = CRC32_noFinalXor(name) & 0xFFFFFF` (standard reflected CRC-32, init 0xFFFFFFFF, **no** final inversion).



* Bundles: name is the bundle name without the `aid_bundle_` prefix.

  `banjox_common` → `0x685374` (file `Bundle/4f/685374`, stream partner `Bundle/50/685374`).

  `banjox_ui_frontend_startscreen` → `0x757c4b`. Type byte `0x4F` = bundle, `0x50` = stream bundle.

* Assets: name is the asset name without `aid_<type>_`, e.g.

  `aid_texture_banjox_grass_showdowntown_shadow51_1` → hash of `banjox_grass_showdowntown_shadow51_1`.

* `Debug/<tt>/<b2>/<b1>/<b0>` is an asset looked up by id (tt = type byte, bytes of hash24). At boot the game

  probes `\debug\db_index.txt` and then opens loctext and videos from here.



Asset type bytes (from matching every manifest): vehicle 00, texture 01, anim 02, model 04, havok 05,

animevents 06, cutscene 08, cutsceneevents 09, misc 0B, actorgoals 0C, marker 0D, callout 0E, aidlist 0F,

loctext 11, avatarhavokdata 14, xcuelist 15, font 16, script 19, fxemitter 1B, fxparticle 1C, fxrumble 1D,

fxcamshake 1E, objparams 1F, animtable 20, scripttable 23, statetable 24, xuipackage 2A, xuicachefile 2B,

xuiloadlist 2F, xlsdata 35, video 36, challenge 3D, vertexshader 41, pixelshader 42, dialog 43, chardata 44,

gpuparticleeffect 45, cutcam 46, blobsdropleteffect 47, explosioneffect 48, garagetutorial 49,

3dgpuparticleeffect 4A, compositeeffect 4B, ddstexture 4D, pathenginepreprocess 4E, bundle 4F, streambundle 50.

(Some pairs share a stem, e.g. model/havok and anim/animevents, so the type byte is what disambiguates.)



### Worlds → resident bundles **[observed]**

Found via the `aid_model_banjox_background_<world>_default` asset each contains:



| World | Bundles |

|---|---|

| Showdown Town | `234cec` (raw CAFF, 201 MB) |

| Nutty Acres | `13b0ff`, `4979db` (terrain split in `defaultchunk1..4`) |

| LOGBOX 720 (`cpu`) | `b4a7c4`, `b837e8` |

| Banjoland | `077143`, `1592bd` |

| World of Sports | `3e5515`, `65ec45` |

| Terrarium of Terror (`terrorium`) | `03f312`, `3af75d` |

| Spiral Mountain | `0d0735`, `757c4b` (title screen), `e3ff02` |

| Car park (Mumbo's Motors test area) | `20abf9`, `4bf033`, `6d0c4a` |

| Banjo's house interior | `36dc87`, `c50bc9` |



Boot load order seen in Xenia: `685374` (common) → `757c4b` (start screen) → `36dc87` → `e00470`.



---



## 2. xcompress `0x0FF512ED` (Bundle/4f) **[verified]**



`XCOMPRESS_FILE_IDENTIFIER_LZXTDECODE`, what `xbdecompress.exe` undoes.



```

0x00 u32 0x0FF512ED   0x04 u16 version 0x0100   0x06 u16 reserved

0x08 u32 hash (not a plain CRC of either buffer; not needed to decode)

0x0C u32 flags: bits0-3 window = 1<<(n+15) (always 128 KB here)

                bits4-5 segment size = 0x8000<<n (always 128 KB)

                bits6-21 segment count

                bits22-23 size-table entry width: 0 → 20 bits, 1 → 32 bits

0x10 bit-packed (MSB first) uncompressed size of each segment, padded to u32

```

Segment *i* starts at file offset `i * segmentSize` (segment 0 right after the table). Each segment is an

independent LZX stream (decoder reset), split into XMem frames: `u16 compressedSize` (→ 32 KB output) or

`0xFF, u16 uncompressedSize, u16 compressedSize`. A segment whose first u16 is `0x0000` is stored: raw bytes follow.

LZX details that matter: the bit reader restarts at every frame; an odd-length uncompressed LZX block is padded

with one byte **inside the frame where the block ends**.



The game also loads uncompressed CAFFs from `Bundle/4f`: 9 bundles ship that way (`20abf9 234cec 3ef9bf 455e80

757c4b 90aff1 9cedca cd43ee e37086`), and replacing the originally *compressed* common bundle `685374` with its

decompressed CAFF boots to the title screen in Xenia **[verified]**. The tool therefore writes modified resident

bundles uncompressed.



---



## 3. CAFF container **[verified: 1756/1756 files round-trip byte-identically]**



```

0x00 "CAFF"  0x04 version "07.08.06.0036" (16 bytes)

0x14 u32 header size (0x78)

0x18 u32 header checksum

0x1C u32 symbol count          0x20 u32 part count

0x24 8 bytes 0

0x2C u32 relocation group count 0x30 u32 relocation offset count

0x34 16 bytes 0

0x44 u32 pool record count

0x48 u8 1, u8 section count, u8 0, u8 pool part count

0x4C u32 section-name bytes

0x50 u32 info size   0x60 u32 info size (copy)

0x64 u32 relocation block size   0x74 u32 relocation block size (copy)

0x78 section records, 0x21 bytes each:

       u32 name offset, u8 align log2, u32 flags [unknown], u32 size, 16 bytes 0 [unknown], u32 size

     section names (NUL separated)

     u32 symbol buffer size, u32 offset[symbols], strings

     u32 n, n bytes (always empty so far)

     parts, 14 bytes each: u32 symbol, u32 offset in section, u32 size, u8 section (1-based), u8 align log2

     pad to 4 → end of info

relocation block:

     groups: (u32 fromPart, u32 toPart, u32 count) × groupCount

     offsets: u32 × offsetCount   (part-relative location of each pointer, grouped in the same order)

     pool table: (u32 part, u32 count) × poolPartCount, then 16-byte records × poolRecordCount [unknown layout]

section data: sections back to back, each part at its alignment, section size padded to 4.

```



* **Checksum** (Mojobojo's `CaffChecksumTest`): over the 0x78-byte header with 0x18 zeroed,

  `h = signext8(b) + (h << 4); t = h & 0xF0000000; if (t) h ^= t | (t >> 24)`.

* **Sections**: `.data` (CPU), `.texturegpu`, `.gpu`, `.gpucached`, `.stream`.

* **Symbols** are asset names (`aid_<type>_banjox_…`, textures as `D:\LocalLibrary\BanjoX\<name>\default.rtx`),

  plus `pool` (shared allocation asset) and `manifest`. Duplicated names get `(1)`, `(2)`… suffixes.

* **Pointers** inside a part are stored as offsets into the target part; the relocation table lists every one.

  This makes resizing a part safe as long as its own relocation entries are regenerated.

* **manifest** asset (`.data`, same magic as stream archives): `0x438CB47C, u32 timestamp, u32 0x20 (entries ptr),

  u32 count, u32 deps ptr, u32 dep count, 8 bytes 0`, entries `(u32 assetId, u32 ordinal)` sorted by id,

  then dependency bundle ids (`0x4Fxxxxxx`).



Rebuild rule used by the tool (matches the originals exactly): parts are placed in table order at

`align(sectionEnd, 1 << alignLog2)`, section size = `align(end, 4)`, all counts/sizes recomputed, checksum last.



---



## 4. Streaming archive `0x438CB47C` (Bundle/50) **[verified: 151/151 round-trip]**



```

0x00 u32 0x438CB47C  0x04 u32 12 (entry size)  0x08 u32 entry count

0x0C u32 build time (unix)  0x10 u32 dependency count

u32 dependencies[] (stream bundle ids 0x50xxxxxx)

entries: u32 asset id, u32 offset, u32 size   (0/0 = not present)

payloads back to back

```

Payloads: 14 330 CAFFs (one streamed asset each, e.g. texture `…top` levels, cutscenes),

420 XACT wave banks (`DNBW` = big-endian `WBND`, i.e. `.xwb`), 79 empty slots.



---



## 5. Textures **[verified: layout matches all 14 049 resident textures; retile byte-identical]**



CPU part (`.data`):

```

0x00 "texture\0"  0x08 "04.05.05.0032"

0x18 u32 XDK D3DFORMAT: bits0-5 GPU format, bits6-7 endian, bit8 tiled

0x1C u32 kind: 0 = 2D, 2 = cube map (6 faces), 4 = multi-frame, 5 = volume

0x24 u16 width, u16 height

0x28 ptr -> .texturegpu (base)   0x2C ptr -> mips, or 0xFFFFFFFF when the blob starts with the base level

0x30 u8 level count              0x34 ptr -> +0x3C (runtime D3D object, zero on disk)

0x38 u32 frame count (kind 4) / depth (kind 5); kind 4 headers are longer and list per-frame GPU pointers

```

Counts: 13 679 2D, 80 cube, 234 volume, 56 multi-frame. Faces/frames/slices are stored back to back at the size

of one level set. Formats in use: DXT1 `0x1A200152` (9382), DXN `0x1A200171` (2141), DXT3 `0x1A200153` (765),

CTX1 `0x1A20017C` (765), DXT5 `0x1A200154` (731), 8-bit `0x04900102` (166, heightmaps, NPOT),

ARGB8888 tiled `0x1A200186` / linear `0x18280086`, 8-bit `0x28000102`.



A texture is usually two assets: `…mip` (resident, levels 1..n) and `…top` (streamed from Bundle/50, level 0).

Tiled data: 32×32-block macro tiles, each level 4 KB aligned, levels below 32 px (and small bases) packed into one

tail — layout and address math follow Xenia (`texture_util.cc`, `texture_address.h`). Linear data (no packing):

rows padded to 256 bytes, no height padding (e.g. 447×503 ARGB = 1792 × 503 bytes).

Endian 1 = swap bytes in 16-bit words (DXT), 2 = in 32-bit words (ARGB). DXN/CTX1 are two-channel normal maps.

Volume textures are shown as stacked 2D slices (true 3D tiling not implemented yet — **[experimental]**).



---



## 6. Models (`aid_model_*`) **[geometry verified visually; geometry import verified in Xenia]**



`.data` starts with `u32 ptr -> chunk table, u32 count`; the table holds `(u32 chunkId, u32 ptr)` pairs.

Chunks seen: 0, 1, 2, 4, 5, 8, 10, 12, 17, 22, 23, 26, 30, 32, 37, 38. Records are often only 2-byte aligned;

the CAFF relocation table is used to follow every pointer.



* **Chunk 2 — nodes**: `u32 n` then n × 68 bytes (`u16 flags, u16 parent (0xFFFF = root)`, float 3×4 row-major with

  translation in column 3, float4). Vertices carry node indices/weights; for background models the nodes are the

  scenery placements (same transforms as chunk 12, different order).

* **Chunk 12 — scenery instances** **[verified in game]**:

  `u32 refModelCount, u32 n, u32 n, u32 n, u32 0, u32 n, ptr refModelIds, ptr records(n × 0x144; name at +0x3C

  like "|REFERENCE_crate1|", u32 refModelIndex at +0), ptr worldMatrices(n × float4x4, row-vector, translation

  in row 3), ptr positions(n × float3), u32 0, ptr indexList`. Reference ids are model asset ids (type 0x04);

  92 of 96 in Showdown Town resolve to models in the same bundle.

* **Render resources header** (found through the GPU buffer table): `+0x24/+0x28/+0x2C` pixel-shader patch list

  (stream locations, pool `.gpu` addresses, count), `+0x30/+0x34/+0x38` vertex-shader patch list (stream

  locations, pool `.gpucached` addresses, count), `+0x48` GPU buffer table (vertex buffers: object ptr, `.gpu` ptr,

  size), `+0x50` vertex buffer count, `+0x54/+0x58` index buffer table (object ptr, `.gpu` ptr, size, 1) and count.

  Vertex buffer record: `u32 stride, ptr D3D object, ptr .gpu data, u32 size`. Indices: u16 big-endian.

* **Command stream** (`.stream` part, starts at the pointer in its first word): commands are

  `(sizeInBytes << 16) | (op << 8)` followed by arguments. `0x16` = 3-way pass switch, `0x19` = jump,

  `0x02` = set pixel shader, `0x2E` = bind vertex buffer (+8 record, +0x14 vertex shader, patched from the pool),

  `0x0F` = sampler, `0x43` = bind textures (`count << 16`, then `(mipIndex, topIndex, slot << 16)` into the

  model's texture table), `0x06` = shader constants, `0x01` / `0x30` = draw indexed (primitive, index count,

  index-buffer object[, n]), `0x0C`/`0x12`/`0x15`/`0x17`/`0x1A`/`0x2A`/`0x05` = state/sub-list commands **[unknown]**,

  `0x1D` = end.

* **Vertex layout** comes from the vfetch instructions of the vertex shader microcode, which is stored in the

  bundle's shared `pool` asset (`.gpucached`). Seen: position float3 or half4 at +0, int16×4 node indices,

  ubyte4 weights/colour, 10:10:10:2 normal/tangent, half2 UVs.

* **Texture table**: 24-byte entries, name pointer at +20, `…mip`/`…top` pairs.

* Collision for world geometry is a separate asset (`aid_havok_banjox_background_<world>_default`) **[not decoded]**.



---



### 6.0 Scenery instances: what the engine reads **[verified in Xenia]**



* Placement comes from **chunk 12**: editing only an instance's world matrix + position moves it in game (Mumbo's

  Motors test). Chunk 2 nodes are not a per-instance table in general (node count ≠ instance count in several

  worlds; in Showdown Town node = n−1−i).

* Record +0x10/+0x14/+0x18 = the instance's own index; the index list (+0x2C) is the identity.

* **Duplicate** (`InstanceEditor.Duplicate`): larger copies of the record/matrix/position/index arrays are appended

  to `.data` and the chunk-12 header repointed and counts (+4/+8/+0xC/+0x14) incremented — the copy renders in

  game (a second Mumbo's Motors in the square). Collision of copies: not verified (the camera clipped into the copy).

* **Delete**: shrinking the instance count (moving the last instance into the freed slot) makes Showdown Town hang

  on the loading screen — other data refers to instances by index. The tool therefore deletes by hiding: zero-scale

  matrix, moved to y = −10000; the world loads normally.



### 6.1 GPU references and geometry import (`NB.Core.Models.ModelImporter`)



Every reference to GPU memory in a model is one of these (for the café parasol: 5 + 5 + 12 = all 22 `.data → .gpu`

relocations; `.stream` never points into `.gpu`):



| Structure | Where | Fields |

|---|---|---|

| VB record | `.data` (often unaligned) | +0 stride, +4 → runtime D3D object (zero-filled), +8 → `.gpu` vertices, +0xC byte size |

| GPU buffer table | R+0x48 → entries, count R+0x50 | 12 bytes: → VB record+4, → `.gpu`, size |

| IB table | R+0x54 → entries, count R+0x58 (directly after the GPU table) | 16 bytes: → IB object, → `.gpu`, byte size, format (1 = 16-bit BE) |

| VB list | R+0x04 area | (→ VB object, stride) pairs |

| Draw count table | R+0x00 / R+0x20 point into it | (→ IB object, index count) pairs — **the engine draws this count**; it must match the draw command |

| Draw commands | `.stream` op 0x30 (0x01) | +4 primitive (4 = triangle list), +8 index count, +12 → IB object, +16 LOD level (0/1/2) |

| VB bind | `.stream` op 0x2E | +4 → `.stream` (runtime slot), +8 → VB record, +0xC 0x00010000, +0x14 → `.data` runtime storage |



Chunk 5 = bounds: radius from origin, AABB centre (3), radius from centre, base point (x, minY, z), height,

max(|x|,|z|), AABB min (3), AABB max (3), then an empty AABB (±10000).



Import: new vertex and index data are appended to the model's `.gpu` part (32-byte aligned) and every structure above is

repointed/resized; pointers are part-relative, so relocations stay valid and the CAFF writer relayouts the bundle.

Vertices are written in the model's own vertex format (e.g. props: half4 position, s16×4 constant (1,0,0,0), RGBA8

colour, 2:10:10:10 normal + tangent, half2 UV); channels an OBJ does not have are copied from the nearest original

vertex. Mapping: one OBJ group per draw when the counts match (a model exported by the tool re-imports exactly, LODs

included), else one group per vertex buffer (all LODs of that buffer draw it) and buffers without a group are hidden.

16-bit indices limit a buffer to 65 535 vertices.



Verified in Xenia (Showdown Town): the parasol re-imported from its own OBJ renders unchanged; scaled ×2 renders twice

as large; a generated 561-vertex UV sphere replaces the parasol and renders lit and smooth. Before the draw count table

was updated, the engine drew the old counts from the new index buffers (black shards) — that table is required.



## 7. Executable **[verified]**



XEX2, title id `4D5307ED`, entry `0x82718798`, image base `0x82000000`, image size `0x10A0000`.

Encryption type 1 (retail AES key `20B185A5…`), compression type 1 (basic). Decoded image is a valid PE

(`.rdata`, `.pdata`, `.text`, 8 × `.embsec_`, `.data`, `.tls`, `.XBMOVIE`, `.idata`, `.XBLD`).

Notable strings: `frontend_debugmenu` / `XuiScene_BanjoX_Frontend_DebugMenu` (a debug menu scene exists),

`d:\DebugOptions.ini` (only configures the Xbox Live networking library's logging), `db_index.txt`.



---



## 8. Other



* **Videos** (`Debug/36`, type 0x36): ASF/WMV (`30 26 B2 75…`), 52 files; playable by Windows Media Foundation.

* **Text** (`aid_loctext_*`, type 0x11; English in `Debug/11`, others in `loctext/<language>/<id>`) **[verified:

  103/107 English and 1442/1498 translated tables round-trip byte-identically]**: `.data` = `u32 0x0C, 0, 0`, then a

  little-endian `LSBL` block: `u32 0x1C, u32 4, u32 offA, u32 offB, u32 0, u32 offC` (offsets from `LSBL`);

  A = `u32 size, u32 count, count × (u16 key, u32 offset in UTF-16 units), (0xFFFF, u32 total units)`, UTF-16LE

  strings, 2-byte pad; B = key → dialog name table; C = key order (absent when offC = 0). Four tables per language

  have an extra prefix before `LSBL` and are read-only in the tool. Strings contain tags such as `{MOODHAPPY}`.

* **Dialogue** (`aid_dialog_*`, type 0x43): conversation records referencing a loctext asset id and a name.

* **Audio** — XACT3 wave banks (`DNBW`, version 43, type 0x12) inside Bundle/50 **[verified: 420/420 round-trip]**:

  8 932 XMA2 entries (effects, voices) and 31 big-endian PCM16 entries (music, 44.1 kHz stereo streaming banks).

  Entry = `u32 flags(4)|duration(28), u32 format (tag:2 ch:3 rate:18 blockAlign:8 bits16:1), u32 offset, u32 length,

  u32 loopStart, u32 loopLength`; seek tables per XMA entry; wave data 2048-aligned. XMA is decoded with

  vgmstream; replacements are stored as PCM16 (the engine's own music format) — **in-game playback not yet tested**.



---



## 9. Scripts and fast testing **[verified in Xenia]**



`aid_script_*` (type 0x19) = header (first u32 = header size) + flat command list `(u32 size, u32 opcode, args)`;

each opcode has a fixed size, no pointers (702/702 scripts parse exactly). Known opcodes: 0x00 end, 0x02 spawn player

(objparams id), 0x48 set game flag (64-byte name + u32 value), 0x4E load marker sets, 0x50 debug print, 0x5A run

sub-script, 0x60 start challenge, 0x86 go to level script. Others are preserved as unknown.



`aid_script_banjox_spiralmountain_startofgame` (common bundle `685374`) drives a new game: animated sequences 1–3,

the "Pointless Collecting" tutorial, flags `SeenStartOfGame` / `ShowdownTown_Intro_00_ShowIntroMasterFlag`, then

`go to aid_script_banjox_showdowntown_midday`. Showdown Town's own intro is a flag state machine

(`ShowdownTown_Intro_01…13`).



**Test mode** (`NB.Cli skip-intro <ws> --no-town-intro --preset-town`, or Tools menu in the studio): removes the

sequences/tutorial, drops the master flag and pre-sets intro steps 01–07. Measured in Xenia: launch → Showdown Town

gameplay in ~43 s (`tools/xenia/quickstart.py`). Boot movies (`Debug/36/a7/b1/d6` MGS logo, `a6/71/9e` boot

sequence) are skippable with A/Start. Workspace-only change; revert `Bundle/4f/685374` to undo.



## 10. Object parameters (`aid_objparams_*`, type 0x1F) **[layouts inferred from all 1810 assets; one field verified in Xenia]**



Fixed-size big-endian C structs, one layout per class. No pointers in most classes, no field-name table in the

executable (only class/tag/message names), so the tool infers layouts by comparing every instance of a class

(`NB.Core.Tags.ObjParamsSchema`, cached in `<workspace>/cache/objschema.json`, CLI `obj-schema`):



| Offset | Content |

|---|---|

| 0x00 | u16 struct size (= asset size, e.g. 1160 for `vehicleBlockEngine`) |

| 0x02 | char[62] object tag, `objTag_…` |

| 0x40 | u16 (0 in all assets seen) |

| 0x42 | char[62] class, `objDefId_…` — 273 classes, sizes fixed per class |

| … | class fields: char[64] identifier strings (`actorBodyStateId_…`, `gameFlag_…`, `colour_…`, animation names), floats, small ints, hashes, and asset references (u32 asset ids: dialog 0x43, model 0x04, anim 0x02, avatarhavokdata 0x14, objparams 0x1F …) |



Inference result over the game: 273 classes, 8440 fields: 2059 strings, 3304 floats, 1363 ints, 763 asset

references, 543 u16, 408 hashes. Asset references resolve to real assets (e.g. an engine's model, havok data, four

animations and its garage description dialog).



Verified meanings (in Xenia):

* `objDefId_vehicleBlockEngine` +0x3FC = engine power (small 40, medium 60, large 80, super 160; AI variants higher).

  With 0x3FC and +0x3D0 (200/300/450) set to 0 the Showdown Town trolley needs 4.3 s instead of 0.27 s to reach the

  warp pad ahead of the spawn (`tools/xenia/drivetest.py`); with 400/2000 it takes 0.16–0.20 s.



Other observations (not verified): `objDefId_entityStateWalk` holds animation bands `char[64] name` + 3 floats

(Banjo: walk 2.5, jog 4.5, run 8.2 — plausibly speeds). Banjo has a `summonvehicle` state

(`aid_objparams_banjox_actorstate_banjo_summonvehicle`).



## 11. Vehicles (`aid_vehicle_*`, type 0x00) **[verified structurally: 275/275 parse exactly]**



Header 0x7C bytes, then one 0x24-byte record per block (`NB.Core.Tags.VehicleAsset`, CLI `vehicles`):



| Offset | Content |

|---|---|

| +0x00 | u16 block count, u16 flags |

| +0x04..+0x1C | floats (unknown; e.g. 40, 80, …) |

| +0x20 | name text (e.g. `trolley7`) |

| block +0 | u8 grid x, y, z, pad |

| block +4 | u8 flag, u8 ?, u8 part category (3 engine, 4 seat, 5 wheels, 6 weapon, 22 horn, 44 fuel …), u8 0 |

| block +8 | u32 objparams id of the part (all 12 455 blocks resolve to `aid_objparams_banjox_vehicleblock_*`) |

| block +0xC | 3 × f32 rotation (radians, multiples of π/2) |

| block +0x18 | u32 paint, RGBA |

| block +0x1C | u32 action-button mask (horn 0x2000, laser 0x4000, spring 0x1000) |

| block +0x20 | u32 unknown |



Across all shipped vehicles: at most 250 blocks per vehicle, grid coordinates 0..18 on each axis. The Showdown Town

trolley is `aid_vehicle_banjox_general_golfcart` (8 blocks: low-loader, 4 standard wheels, seat, small fuel tank,

small engine); upgrade stages are `general_golfcart*` / `test_gm_sdt*`.



## 12. In-game verified edit paths (Showdown Town, source-built Xenia)



| Edit | Tool | Result |

|---|---|---|

| Scenery transform | studio / script `--select --move --save` | café parasol + table float +6; Mumbo's Motors raised/lowered |

| Marker transform | same (markers are scene objects) | Humba's actor spawn moved next to the warp pad: she stands there |

| Texture replace | `NB.Cli tex-replace <ws> <texture> <png|checker>` | Mumbo's Motors painted with the checkerboard |

| Text | `NB.Cli text-set <ws> <string name> <text>` (English tables in `Debug/11`) | pause menu shows the new label |

| Audio | `NB.Cli audio-replace <ws> <bundle> <bank> <index> <wav|tone:Hz>` | MusicPause replaced by a 1 kHz tone (loopback capture: `NB.Cli audio-capture`) |

| Objparams | tag editor (structured) / `NB.Cli obj-set <ws> <asset> <offset> f:<value>` | engine power change measurable in drive test |



## 13. Executable research (default.xex) and executable mods



Tools: `tools/probe/ppc.py` (PowerPC disassembler / cross-references over the decrypted image `work/default.exe`,

memory layout: file offset = VA − 0x82000000; `.text` 0x821E0000–0x82D09400). Executable mods are applied as

**Xenia Canary patch files** (`<xenia>/patches/4D5307ED*.patch.toml`, `[[patch.be32]]` address/value); the XEX is

never modified. Xenia matches the file by module hash — XXH3-64 of the code pages *after* its loader rewrote import

thunks (retail: `C03916823ADAC91B`; the tool maps its own hash of the file image `66DC9721DEB966A8` to it, or reads

"Module Hash:" from xenia.log).



### 13.1 Pause menu (Game Options)

* Item table at 0x82E514E0: 8-byte entries (XUI element name, pause id): 0 assign components, 1 change vehicle,

  2 build vehicle, 3 restart jiggy game, 4 quit jiggy game, 5 return to Showdown Town, 6 return to title,

  then multiplayer leave items. The visible list is built by 0x825A89B0 into the array at 0x82FA32F8

  (count 0x82FA32F4).

* Change Vehicle / Build Vehicle are added when **0x82577DA8** returns true. It returns false when: the current level's

  settings byte `[[[0x82FAD9F0]+0xF4]+8]+0xD4` lacks bit 0x08; game object `[0x82FAC7AC]+0x58 == 1` (Showdown Town);

  a jiggy game is running (`+0x15B0 → +0x182C`); multiplayer conditions; or no current vehicle slot is set.

  "Return to Showdown Town" needs bit 0x80 of the same settings object's byte +0xD3.

* **Mod "Change Vehicle in Showdown Town"**: `0x82577DF8 beq → nop` and `0x82577E04 beq → nop`

  **[verified in Xenia]**: both items appear in Showdown Town; Change Vehicle opens the Load Blueprint screen

  ("Town Blueprints") and delivers the chosen vehicle; Build Vehicle opens Mumbo's garage. Switching verified: with

  the trolley upgrades unlocked (test mode preset of the six *_Act*_1_Beaten flags listed in

  aid_misc_banjox_unlockablelist_golfcarts), the list shows Trolley Mk. 1–7 and choosing Mk. 2 swaps the vehicle in town.



### 13.2 Acts and world bundles

`aid_script_banjox_<world>_act<N>_main` (common bundle) → op 0x01 "load level" = (background model 0x04…, map texture

0x01…, 0x07… (not an asset), path-engine asset 0x4E… whose low 24 bits are the **act bundle**, misc 0x0B…, 6 floats).

The act bundle's stream archive dependencies name the world bundle the act loads (`NB.Cli acts`; 29 acts). Worlds

ship in two copies; acts use one (Nutty Acres 13b0ff, Banjoland 077143, Terrarium 03f312, LOGBOX b4a7c4,

World of Sports 65ec45) — verified for Nutty Acres act 1 in Xenia. Test mode `NB.Cli start-in <ws> <act script>`

retargets the start script's go-to so a new game starts in any act.



## 14. Collision (`aid_havok_*`, type 0x05) **[decoded: Showdown Town 367 474 and Nutty Acres 320 760 triangles]**



`.data` wrapper: +0 → table of (u32 type, u32 → entry), +4 entry count. Entry **type 1** = list of (u32 offset,

u32 count) pairs: the **Havok 5.5.0-r1 binary packfile** (count = byte size; 32-bit big-endian layout 04 00 00 01),

then — for world collision — the **index buffer** (count = number of u16 indices) and **vertex buffer** (count =

number of float3 vertices) that the game plugs into the packfile's extended-mesh triangle subpart, whose

vertexBase/indexBase are null in the file (Showdown Town: 67 576 triangles = 202 728 indices at 0xA0, 41 141 vertices

at 0x63070, packfile at 0xDB8F0). Types 6/7 hold further runtime data (not needed for geometry). 32- and 56-byte

assets are stubs without a packfile (actors, skydomes).



Packfile: header (magic 57E0E057 10C0C010, version 5, layout, section count, contents), 0x30-byte section headers

(`__classnames__`, `__types__`, `__data__`; data start, local/global/virtual fixup offsets, exports, imports, end).

Local fixups (src, dst) and global fixups (src, section, dst) resolve pointers; virtual fixups give each object's

class. `__types__` carries full **hkClass reflection** (name, parent, size, members with type/subtype/offset/class) —

the tool reads objects by member name (`NB.Core.Havok.HkPackfile`), so layouts are not hard-coded.



Shapes in the game (survey of all havok assets): hkpConvexVerticesShape 2609, hkpMoppBvTreeShape 834,

hkpExtendedMeshShape 801, hkpConvexTranslateShape 189, hkpSphereShape 156, hkpListShape 156, hkpBoxShape 67,

hkpCylinderShape 64, hkpConvexTransformShape 20; plus rigid bodies, constraints and hkx scene data. Convex vertices are

stored transposed in blocks of four (x[4], y[4], z[4]); their **plane equations include the convex radius**

(vertices lie at distance −radius), faces are rebuilt from the planes, or from a convex hull when unusable.

Scenery collision = `aid_havok_X` for reference model `aid_model_X`; world collision = `aid_havok_banjox_background_<world>_default`.



Display: View → Collision (terrain cyan, scenery yellow, drawn per instance with its matrix); export:

`NB.Cli collision <ws> <bundle> <havok asset> out.obj`.



**Scenery collision placement (verified in Xenia, 2026-09-27):** the world havok asset's wrapper entry of **type 7** is

{→ table, count, 0, 0}; the table has one (→ list, count) pair per chunk-12 instance, in instance order (entry 0 =

poshwaterplug, the first chunk-12 instance), and each list holds 16-byte records (model asset id 0x04…, havok asset id

0x05…, 0, 0). The game builds each instance's collision from its list and the **chunk-12 matrix**: moving an instance

moves its collision (Mumbo's Motors sunk 40 units: the trolley drives across its old footprint); moving only its chunk-2

node does not (node-only test: no invisible wall). A duplicate appended beyond the table had no collision; Duplicate now

re-creates the table one entry longer (copy of the source's pair, new relocations) and the copy blocks the trolley.

Chunk-2 nodes in Showdown Town are the instance table in reverse (node k = instance n-1-k); Duplicate keeps that

consistent by inserting a node at index 0.



**Loader and collision import (static RE, 2026-09-27).** The havok asset resolver 0x82306F78 (type-handler table
0x821A89E8) calls 0x82307C70 for the wrapper's type-1 entry E = {+0 packfile, +4 size, +8 index buffer (u16), +0xC
index count, +0x10 vertex buffer (float3), +0x14 vertex count, +0x18 material index per triangle (u8), +0x1C material
table (12-byte records, most common 00000000 00000000 00000004), +0x20 material count, then mass properties}. It loads the
packfile (hkBinaryPackfileReader, contents = getContents(getContentsClassName())), then unconditionally treats
contents+0x34 (hkpMoppBvTreeShape child) as the hkpExtendedMeshShape and, if it has triangle subparts, plugs vertexBase
= E+0x10, indexBase = E+0x08 and (when E+0x1C != 0) the materials into subpart 0; the EMS finish constructor
0x82CC0DE8 has already copied a single subpart into the embedded one (+0x70) and pointed +0x50 at it. The root must stay a
MOPP. All 390 shipped triangle meshes: materials present, disableWelding 1, welding type TWO_SIDED with one entry per
triangle, triangleOffset 0, chunked MOPP (buildType 0, size multiple of 512). Type-6 entry = **breakable scenery**
(24 of 411 in Showdown Town: cafe tables, parasols, benches, lamp posts, fences, food stands, crates): two more packfiles
(the pieces as a physics system; property 0x2001 makes 0x823075B8 set kind 5, and such instances are built through
0x82307DF0). Their static collision is a shape subpart of the pieces. **Replacing a breakable's collision (cafetable)
froze the game in Xenia after ~40 s** — the tool refuses breakables.
MOPP byte code: the run-time machines are the chunk-capable ones — hkpMoppObbVirtualMachine 0x82948928 (AABB queries;
query set up by 0x82949600: 24-bit bounds truncated -1/+1, fallthrough if min>>16 < a, jump if (max>>16)+1 > b),
hkpMoppLongRayVirtualMachine 0x82949790 (segment clipped to <= a / >= b) and hkpMoppAabbCastVirtualMachine 0x8294EF68;
the other machines (FindAll, Sphere, KDop, ...) have no 0x0C and loop forever on it (their error handler does not
advance ip). Opcodes: 0x10-0x12 a b j axis split, 0x26-0x28 lo hi bounds, 0x01-0x04 rescale, 0x05-0x07 jumps,
0x09-0x0B key offset, 0x0C u16 chunk jump (ip = data + u16*512), 0x0D 5-byte no-op, 0x30-0x53 terminals, 0x60-0x6B
properties. `NB.Cli collision-import <ws> <bundle> <aid_havok_...> <mesh.obj|fbx> [--offset x,y,z] [--scale s] [--box]
[--material 24hex]` (Studio: scenery right-click "Import Collision") writes the shipped form (chunked MOPP, materials,
welding NONE); `--selftest` checks every mesh collision of a bundle (structure invariants, AABB/ray/linear-cast
queries modelled on the three machines; the game's own MOPPs pass with 0 misses).

### 13.3 Debug / developer UI (15.5 — developer menu and developer part list verified)

* Scenes are opened by 0x823E9BA8(mgr = [0x82FAC758], index); descriptors at 0x82E56A90 + i*0x120 (name table

  0x82E5E038: 68 Frontend_MainMenu, 71 Frontend_StartScreen, 73 Frontend_DebugMenu), or by name through the objparams

  class entitySceneControlUI and aid_xuiloadlist_* records. (Earlier note corrected: `li r4,71` at 0x822385DC builds

  message 71 objMsgId_Actor_ActionBegin, it does not open scene 71.)

* aid_misc_banjox_gameassetref_default (common bundle 685374) maps names to asset ids: retail_mainmenu_scene →

  0x1936DC87 (ui_frontend_houseinterior, Bottles' house), debug_mainmenu_scene → 0x195906C3

  (aid_script_banjox_ui_frontend_main, bundle 5906c3 ships; its loadlist opens XuiScene_BanjoX_Frontend_MainMenu).

  The executable only ever looks up the retail entry.

* **Developer main menu — VERIFIED in Xenia:** the title-screen Start handler

  (entitySceneControlBanjoXStartScreenButtonHandler) init 0x824735C0 copies its objparams +0xA4 (0x1936DC87) to

  this+0x70; Start loads that script. Mod "developer-main-menu" stores 0x195906C3 instead (4 words, see ExePatches);

  data-only equivalent: bundle 757c4b aid_objparams_banjox_scenecontrol_ui_startscreenbuttonhandler .data+0xA4.

  Result: MAIN MENU with Enter Garage (verified: loads Mumbo's Motors directly), Start New Game, Resume Save,

  Multiplayer, Play Demo Level / Demo Messing Around (load showdowntown_demo / nuttyacres_demo — bundles a9cb7b /

  4ee58d are not on the disc, do not use), Unlocked / Auto Progression checkboxes (no code reads them; the flag

  gameFlag_Normal_EnableAutoProgression is unused). RB on that menu goes to the retail house.

* DebugMenu (scene 73, "DEBUG OPTIONS", 12 unlabeled checkboxes) has no handler class and nothing opens it.

* Globals: [0x82FAC650] = multiplayer mode (set only by the multiplayer menu init 0x825879C0); [0x82FAC5D0] = demo

  build (from gameassetref_demoflag or file GAME:\isdemobuild; redirects the front end to the E3 Banjoland demo whose

  bundle 901667 is missing). cameraMode_Debug (camera_debug objparams) exists but nothing enters it.




* **Developer part list — VERIFIED in Xenia:** the inventory builder 0x8251CB20 adds unlocked `unlockable_blocksets`
  records. In demo builds ([0x82FAC5D0] != 0) it adds the single list named at 0x8216B7B4 ("garage_demoblocks")
  instead. Mod developer-all-parts renames that string to the retail, otherwise unused gameassetref entry
  `garage_allblocks` (0x0BE788E4) and nops the flag test at 0x8251CC24. Result: 118 inventory entries × 200 at a new
  game (stock 23). docs/research/155_debug.md §5.

### 13.4 Garage limits (15.2 — build area verified)
* Build area: 0x8264AA58 (after every cursor step) requires the box of vehicle + held part to span < 19 cells per axis
  (relative: the vehicle can grow in any direction); otherwise the cursor is clamped and hint 29 "no room" is shown.
  Mod garage-build-area-31 raises all eight constants to 31 — verified with the editor cursor in guest memory
  (editor object: +0x1A8 cursor cell, +0x2A0 held-part cell, packed u32 = A bits 31..21, B 20..11, C 10..0, signed):
  Mk.7 + cube stops at extent 19 stock, 31 patched. Blueprint cells are bytes (≤ 256); the blueprint picture only
  draws cells < 19.
* Part limit 250: vehicle merge 0x826041C0 / can-attach 0x82604054 / garage message 0x82648150 (`cmpwi 250`). Vehicle
  block lists and blueprint counts are growable/u16. Fixed 250-sized storage exists in these places:
  * the vehicle spawner 0x825697C8 (u32[250] on the stack);
  * the break-up routine 0x825F15B0 (u32[250] + memset 1000);
  * the challenge restriction stripper 0x8252FD50 ([250] arrays);
  * the blueprint preview object (0xC40 bytes, three u32[250] arrays at +0x84/+0x46C/+0x854), filled by the loader
    0x824BA700 (count read once at 0x824BA7FC).

  Mod vehicle-part-limit-400 raises the three limits to 400, enlarges the two stack frames, skips the stripper above 250
  parts and clamps the preview count to 250. Keep 0x824BA810..81C intact: it is the loader's shared exit. Verified in
  Xenia with a 295-part blueprint (docs/research/152_garage.md §6.2).
* Garage hints: 0x82512D80(garage, player, instr), instr = row of aid_misc_banjox_garageinstructions_default (29 cursor,
  33 invalid, 34 max components). Test mode: preset the 7 gameFlag_Normal_Garage_Tutorial_* flags to skip the tutorial.
  Details: docs/research/152_garage.md.

## 15. Paths

Path nodes are marker records of type 22 (104 bytes): +0x14 position, +0x24 heading, +0x34 float (width?), and

**+8 u16 = index of the next path node** in the same marker asset (indices are numbered per record type).

Showdown Town (aid_marker_banjox_showdowntown_main): 331 nodes forming one route along the roads (1 start, 330 links,

mean spacing 20.8 units). Nutty Acres act 1 has a single node. Navigation meshes (aid_pathenginepreprocess, type 0x4E,

PathEngine library) are not decoded.



### 13.5 World bounds (15.1 — verified)

* Mechanism: the game's Havok broadphase border (callback 0x823C1680) queues objects that leave the world box; the flush

  0x823C17B8 sends objMsgId_Avatar_EscapedBackground (22); Banjo's strategy handler 0x82257D58 kills/resets him

  (0x8225802C, single player) or leaves the session "Player left the broadphase" (0x8225801C, multiplayer).

* The box: 0x822EB698 (at level load) = AABB of the level collision body [G+0x7C0] (+ other bodies) ± [G+0xAC4/AC8]

  ± 100.0 (literal 0x82000AE0), written to W+0x610/0x620 (W = [[[[0x82FAB0FC]+4]+0x15B0]+0x1D0+0x15B4], flag W+0x561)

  and used for hkpWorldCinfo → hkpWorld+0x2D0/0x2E0. Showdown Town: (-556.26,-116.23,-622.45)..(525.42,314.11,757.22).

* Mod world-bounds-2048 (5 words at 0x822EB8A0..0x822EB92C): X/Z margin 2048, Y margin 100 — verified live:

  (-2504.26,-116.23,-2570.45)..(2473.42,314.11,2705.22). Inverse test (max Z − 360 so the spawn is outside): level

  reload loop. Memory: the spatial grid 0x823C8E20 uses 50×1000×50 cells over the box (~0.8 MB at ±2048; keep Y small).

* 0x8253DA70 (level command 0x26) is a second writer of the same fields; it does not build Showdown Town's box.

  tools/xenia/worldbox.py prints the live box. Details: docs/research/151_bounds.md.

## 16. Animations and skeletons (aid_anim_*, type 0x02) **[skeleton + skinning verified; keyframe codec decoded]**

`.data` starts with "animation " + version text "19.12.06.0036"; +0x1C f32 duration (s); +0x20 u16 track count (= joint

count of the character, 175 for Banjo/Kazooie); +0x22 u16 frame count at 30 fps; +0x24.. offsets of the streams. The

rotation stream is tagged "QUAT_BITSTREAM" (at +0x64 in every anim), other streams "BITSTREAM"; each anim also embeds a

"pose" object (the skeleton without names).

**Keyframe codec (decoded from the game's decoder; tools/probe/animdecode.py, docs/research/16_anim_codec.md):** codec
table 0x82D868A0 (QUAT_BITSTREAM, BITSTREAM, *_UNCOMPRESSED, *32, *_VARIABLE). Per track 9 channels (rotation xyz,
translation xyz, scale xyz); flag bytes say stored/static per channel; animated channels = signed base + width (4-bit,
+1) and per key `width` bits of unsigned delta, value = (bits + base) × scale; bits are read LSB-first from big-endian
32-bit words. Keys every 1/rate frames (rate at +0x5C), last key = last frame; w = sqrt(1 − x² − y² − z²); runtime slerp.
Decodes all 5,911 anims with exact stream sizes; consecutive anims join (169/175 tracks within 1°). Translation is an
offset added to the bind-pose local translation (verified: `anim-fbx` of Banjo `run` plays as a correct run cycle in
Blender — feet in antiphase, planted at constant height during stance, loop closes). Unknown: the extra BITSTREAM float
channels (+0x28 stream, 1–54 channels). NB.Core.Models.AnimAsset is the C# decoder.
* aid_animtable_* (type 0x20): u32 header size (0xA8), then 168-byte records starting with a char[64] action name
  (Banjo: 166 actions — challenge_lose, climbpole_climb, jump_start, kazooiein …) mapping actions to animations.
  Character models also carry chunks 26/27/4 (small tables) and chunk 32 (named effect points, e.g. "underWaterAirBubbles").



**Skeleton ("pose" object, decoded + verified):** in a character model it sits inside the rendergraph (chunk 30, tag

"rendergraph" 26.09.07.0039). Layout: "pose\0\0\0\0" + version text; +0x18 u32 joint count; +0x20 offset of the name

table {u32 count, u32 offset of 12-byte records (u32 name offset, 0, u32 ordinal), 0, offset of a sorted hash table};

+0x2C offset of the joint array, 52 bytes per joint: f32[3] translation relative to the parent, f32[3] bind-pose model

translation, f32[4] quaternion (x,y,z,w), u16 parent, first child, next sibling, index, mirror joint (LF_* <-> RT_*), pad

(0xFFFF = none). All offsets are plain .data offsets. Banjo: 175 joints (BASE, BACK, LF_H ... Kazooie's *_K joints,

WRENCH*), Mumbo 127, Grunty 119, Bottles 77, Jinjo 66. `NB.Cli skeleton <caff> <model>` prints the hierarchy and

`model-fbx` now writes the joints as FBX LimbNodes — Blender 3.2 imports one armature with 175 bones, correct parents

and bind positions (verified).



**Skin weights (decoded + verified):** skinned vertex layouts have an integer k_16_16_16_16 element (shader r5) = 4 joint

indices into the model skeleton (global indices, no per-draw palette) and a normalized k_8_8_8_8 element (r0) = weights

in reverse byte order (byte 3 = weight of index 0; the four bytes sum to 255). `model-fbx` writes a Skin deformer with one

Cluster per joint. Blender 3.2: all 210 Banjo meshes get an armature modifier; rotating NECK moves 31,415 vertices,

LF_K 7,602, and an 80 degree knee bend renders as a clean bend of the lower leg (work/skel/r_cmp.png).



### 13.6 Camera tool range (15.4 — verified)

* Photo camera: per-frame controller 0x8229A8D0 (photo object [[0x82FAC7AC]+0x15B0]+0x40: +0x60 position, +0x130 origin,

  +0x124 state, 1 = free camera) clamps |pos − origin| to 30.0 (shared literal 0x82127D30 — do not edit it); origin =

  camera start, or Banjo/vehicle if within 30 (enter function 0x82535A58). Mod photo-camera-unlimited: 0x8229AB98

  ble → b. Verified: stock stops at 30.00, patched reached 57.7 (collision 0x822EFF20 still applies).

* Pause tabs switch only on left-stick left/right keystrokes (hidden leftButton/rightButton, VK_PAD_LTHUMB_LEFT/RIGHT);

  helper mod pause-opens-photos (0x825A6B1C) opens the pause menu on Photos & Videos.

* Correction: cameraModeEditor's factory is 0x822F2090 (garage orbit camera); 0x82320568 belongs to

  entityAvatarMiscAnimDelete. Details: docs/research/154_camera.md, pause_tabs.md.

### 13.7 Vehicle destruction in Showdown Town (15.3 — partial)

* aid_misc_banjox_vehicleblockdamagelevels_default (common bundle): 4 × 80-byte levels (char[64] name, f32 health %,

  RGBA colour, f32, f32): Undamaged 75, Minor 50, Modest 25, Major 0 — display thresholds, not the town rule.

* No game flag/counter controls it; the rule is in code. Candidate: the level-settings byte +0xD4 bit 0x08 (clear in

  Showdown Town; also gates the vehicle pause items) — extra vehicle handling only when set in 0x824A9A18 (beq at

  0x824A9B6C) and 0x82431550 (beq at 0x824317E0). Experiment done (boosted trolley reversed at full speed into walls/canal, with and without both checks forced): the

  trolley stayed intact both times, so this bit is not the destruction switch (or crash damage alone is not enough).

* Messages: (name ptr, payload size) table at 0x8212A988, 338 entries; the message ID IS the table index (ctor

  0x82241888 stores r4 = id and reads size from table[id*8+4]). Vehicle_Damaged = 303, Scene_Vehicle_BreakUp = 270,

  Actor_OccupiedBlockDetaching = 174, WeaponFire = 309, VehicleBlock_WeaponFire = 315. tools/probe/msgsites.py lists

  where a message is built (Damaged: 0x825F0B04, BreakUp: 0x8260517C).

* The town rule (found 2026-09-27): the player-vehicle spawn 0x825697C8 tests owner +0x58 == 1 (Showdown Town) or

  [0x82FAC5D0] != 0 at 0x82569B1C. Town branch: vehicle +0x18D0 |= 0x410, +0xEB4 = +0xEB8 = 1, and the spawn timer

  +0xD00 is NOT set. Normal branch: +0x18D0 |= 0x10 and +0xD00 = spawn-protection time from the player settings; the

  update 0x825F1F78 counts +0xD00 down and clears 0x10 at zero. Bit 0x10 = damage immunity: checked first by the

  damage entry 0x825F0A70, the hit/explosion path 0x825F17E8 (called from explosion radius code 0x822709D0) and the

  hazard-zone path 0x82608D58. So in town the immunity bit is set and never expires. Bit 0x400 (with byte +0xDF7)

  disables Banjo's wrench interaction with the vehicle (0x82460D30) and a collision flag (0x82236CE8).

  +0xEB4/+0xEB8 hide the fuel/ammo gauges (0x82611960 -> 0x825EEB50/0x825EEBB8).

* Vehicle fields: +0x4C player object, +0xD0C max health, +0xD10 health, +0xD18 health fraction, +0xE90..+0xE98

  capacities (fuel, ammo, third), +0xE9C..+0xEA4 current values, +0xCE4 damage-receiver list (empty on trolleys, so

  damage lands on +0xD10 directly).

* Mod "town-vehicles-normal-rules" (nop 0x82569B1C) — EXPERIMENTAL. Verified in Xenia with guest-memory reads

  (tools/xenia/xmem.py, xvehicle.py): town vehicles end with flags 0x800 (no 0x10), gauges show, and the grenade egg gun

  fires in town (ammo 200 -> 190 -> 180 -> 170, eggs explode in the square). With only the 0x10 bit removed (ori 0x410

  -> 0x400 at 0x82569B60) the flag clears but weapons stay disabled.

* Not verified: a player vehicle breaking apart in town. Findings: wall/house crashes at boosted speed never change

  vehicle health (town, with and without both patches), and point-blank egg explosions from the player's own gun do

  not change it either (392.87 before/after three blasts). A gun rotated -pi/2 makes the Mk.7 fall into two pieces

  on spawn — that is a disconnected build, not damage. Next: find a damage source that can hit the player in town

  (e.g. place an explosive/hostile actor with the marker tools) and compare with the same test in Nutty Acres.

## Grass layers, dialog assets, path nodes, collision (NB Studio 1.7, 2026-10-05)

### Grass layers
* Grass layers = chunk 17 (u32 count + 0xC4-byte records) of the background model **or of a reference model placed in the
  world**: Spiral Mountain has none in its background — its 28 layers are in `…spiralmountain_grassboxes` (instance 90 at
  the origin). Showdown Town 78 (background), Terrarium 1 (+1 in `references_mushroomhall`). Nutty Acres has **no chunk 17**:
  its grass is the scenery model `nuttyacres_references_trees_fabricgrass` (see "Nutty Acres" below).
* Record: +0 grass model id, +4 shadow texture id (name without the lighting suffix), +8 box min, +0x14 box max,
  +0x20 tile spacing (6; underwater 2), +0x24/+0x28/+0x2C ?, +0x30 draw range in tiles (20 → 120 units = last LOD distance),
  +0x34 position jitter (0.2 of the spacing, either way), +0x38 height texture id, +0x3C/+0x40 height scale min/max
  (1 / 1.5), +0x44 shadow texture name (stale bytes after it).
* Tiles (13,279 tile records read from guest memory on the Spiral Mountain title screen, 0x70 bytes: 3 rotation rows,
  position, bbox min, max, flags): **one tile per spacing×spacing cell that has any non-zero shadow-texture alpha**
  (1035/1035 cells of layer 23 match); position = cell centre + jitter; y = box min y; turned by a random multiple of 90°;
  y scaled by a random factor in [+0x3C, +0x40].
* Grass vertex shader: index = vertex × **30** + tile slot (c132 = 1/30, −30; 30 tiles per draw — the generic K detector
  in ModelAsset picks 16/32 for these, wrong); world xz → uv = (xz − min.xz)/size.xz (no flip, checked against tile
  bboxes); y += height texture red × (max.y − min.y); colour = vertex colour × shadow rgb; **a blade is killed when its
  vertex alpha > shadow alpha** (o63.z = saturate(vcol.a − shadow.a)): the shadow texture's alpha is the density.
  Pixel shader: diffuse × colour × (sun colour × sun height + c49).
* Shadow textures come in lighting variants `_0, _1, _2, _4` held by the time-of-day/act bundles (Showdown Town: _0 midday
  e00470, _1 morning 01d1b6, _2 afternoon 6bd4a7, _4 night 3f0052; Spiral Mountain _0 title/startofgame, _1 endofgame/
  trickyrace, _2 twistyrace). The viewer picks the variant held by the current light's bundle, else the scene's bundles,
  else _0, and re-lays the tiles when the light changes.
* LOD: the grass model's LOD table (meadow 0:[0,1,2,3] 60:[1,2,3] 90:[2,3] 120:[3]) = density nodes by tile distance.

### Dialog assets and character lines
* `aid_dialog_*`: +8 = loctext table id. A dialog node name such as "mrfit_running" is the table entry
  "dialog__mrfit_running"; numbered variants ("dialog__mrfit_cantafford1".."4") belong to the same node.
* A character's lines: marker -> actor objparams -> its own strategy/script/dialog assets -> the dialog's table, plus entries
  named after the character in the world's and act's tables (e.g. `showdowntown__mrfit_menutitle`, hit lines in
  `actor_hitdialogs`). Tables exist per language (`loctext/<lang>/<id>`).

### Path nodes
* Marker type 6 (actor): +8 = the start node of its path. Path nodes keep a per-node value in the scale field
  (0.5, 1.59, 6.6 ...): it is data, not a size (only types 5, 8, 14, 22 have values other than 1).
* The town markers exist only once (234cec); `aid_pathenginepreprocess_*` is the walking navmesh and holds no node
  coordinates. Moved nodes are followed in game as long as the next node is reachable on that navmesh.

### Collision
* Each model has an `aid_havok_<same name>` asset with its collision. Character (actor) havok assets are empty 32-byte
  placeholders.

### Texture names
* Imported texture names must not end in "top" (and probably "mip"): the game names texture entries `<name>top` / `<name>mip`; a "top"
  name (WOOD/OFFDESKTOP) hung Showdown Town's loading. NB's Source map importer adds `_m` to such names.


## 11a. Vehicle saves and blueprints (Xbox 360 packages, content files, aid_vehicle assets) **[verified: 54/54 readable samples round-trip byte-identical; Xenia: editor-made packages load and drive]**

(To be merged into docs/FORMATS.md after §11 "Vehicles". Code: `NB.Core/Vehicles/*`, CLI `vehicle-*`, NB Studio > Vehicle Editor.)

### Three containers, one blueprint

| Kind | How to tell | Layout |
|---|---|---|
| **Package** (what the console stores, files `0x0000000N`) | starts `CON ` | Xbox 360 STFS package (§ photo packages): title 4D5307ED, content type 1 (saved game), header size 0x971A, display name `VEHICLE: <name>` (UTF-16BE at 0x411, case as typed), thumbnail = the game icon (9573-byte PNG at 0x171A, same bytes in all 75 samples, also the title thumbnail 0x571A), media id 0x3E567DFF, transfer flags 0x40 (0x1711). **One file inside named like the package without `0x`** (package 0x00000082 holds `00000082`). |
| **Content file** (what a package holds; what Xenia keeps in `content\<xuid>\4D5307ED\00000001\0x0000000N\0000000N`) | 8-byte prefix `3F9AE148 40547AE1` (f32 1.21, f32 3.32) | prefix + blueprint. Photo content files start f32 1.21, **f32 9.73** (never confused). |
| **Blueprint** (`aid_vehicle_*` .data in the bundles, no pointers) | size = 0x7C + count × 0x24, part ids type 0x1F | the bare blueprint |

The console names packages 0x00000001, 0x00000002, … (lowest free index) next to the save slots 0x0b0a5c5c / 0x0b0d6cca.
**Xenia** shows a blueprint only when the file inside the content folder is named like the folder (a package installed
under another number listed as "CORRUPT VEHICLE!" — `VehicleFile.InstallToXenia` renames). Its header file
`Headers\00000001\0x0000000N.header` = package bytes 0..0x971A, then the folder name at 0x971A (0xA000 bytes).
Xenia reads its content list at boot only.

### Package integrity (console-written samples)

* Block separation 0 (two level-0 hash tables; byte 0x37B bit 1 picks the active one: samples have both 0 and 2).
  Rewrites leave freed blocks: hash status 0x00 unused, 0x40 freed (stale hash, skip), 0x80/0xC0 in use.
* Checked per package (`StfsIntegrity.Check`): header hash 0x32C = SHA-1(0x344 .. first table), top hash (volume
  descriptor +8) = SHA-1(active table), SHA-1 of every in-use block.
* Writing (`StfsBuilder`): file table at block 0, files consecutive, two identical tables (sep 0), status 0x80, next
  links, counts, top hash, header hash. The console signature (0x1AC, RSA over the header hash with the console's key)
  cannot be redone: **Xenia does not check it; a real Xbox 360 needs the package rehashed and resigned (Horizon /
  Velocity / Le Fluffie "Rehash & Resign")**. Saving over a package keeps its header (certificate, console id
  0x36C, profile id 0x371, thumbnails); a brand-new package gets a neutral header (profile 0) or the header of a package
  of the player's own (template).
* **Sample folder `ROOT\vehicle saves`**: 53 of 75 packages are sound; **22 are damaged copies** (0x03, 0x18, 0x1e, 0x28,
  0x4a–0x4e, 0x59, 0x5a, 0x5c–0x66): byte for byte equal to a sound package up to 0x4000, then other files' data
  (an XDBF profile file, PNG pictures, UTF-16 text) — the first 16 KB FATX cluster is right, the cluster chain after it is
  not (copied from a damaged drive / recovery tool). Header hash and top hash fail; the vehicle is not in the file.
  0x75 "MY DEATH CAR." is hand-edited (file entry 435 bytes, declares 166 parts, records shifted by one byte); 0x85 has 28
  bytes after its 250 parts. Both are read with warnings and round-trip unchanged.

### Blueprint header (0x7C) — written by the game's serializer 0x8260FF20

| Offset | Content |
|---|---|
| +0x00 | u16 part count (no fixed limit in the format: 65535; stock game 250, see §13.4) |
| +0x02 | u8 1 = the vehicle is one piece ([veh+0xCDC] == 1) |
| +0x03 | u8 not written by the game (dev assets carry 0x40..0xD0) |
| +0x04 | f32 power = max([veh+0x1910], [veh+0x18F4]) — the "speed" bar of the blueprint lists |
| +0x08, +0x0C | f32 stat bars ([veh+0xE90], [veh+0xE94]) |
| +0x10 | f32 **weight = Σ objparams +0x190 of the parts** (exact on every sample and game asset) |
| +0x14 | f32 stat bar ([veh+0xD08]) |
| +0x18 | u32 OR of the parts' runtime ability masks ([block+0x478]; depends on the built vehicle, not per part type; game assets often hold garbage pointers — unused for spawning) |
| +0x1C | u32 1 = some part has [block+0x334] != 0 |
| +0x20 | name: saves UTF-16BE (≤ 31 chars + NUL, 0x40 bytes); the game's own assets an ASCII creator tag ("SalvyBob", "log_…") |
| +0x60/+0x64/+0x68 | u32 part type (objparams id) shown for action buttons A / B / X (flags [veh+0x138C] bits 0x1000/0x2000/0x4000) |
| +0x6C/+0x70/+0x74 | u32 per button |
| +0x78 | u8 1 = named by the player (0: the game wrote "-- gamertag --") |

The blueprint lists copy +4..+0x14 for their stat bars (0x82568170) and +0x18 (0x825DD2E0); spawning a vehicle does
not need them. The editor recomputes count, weight and the button part types and keeps the rest.

### Part record (0x24)

| Offset | Content |
|---|---|
| +0x00 | u8 cell x, y, z (0..255; the stock garage edits 19 cells per axis, the build-area mod 31; the spawner takes any) |
| +0x03 | u8 group: **the spawner skips parts with a non-zero group** unless the spawn asks for them (0x825698C8); the game's serializer writes 0 (one sample record has 4) |
| +0x04 | u8 **painted**: 1 = use the paint at +0x18; 0 = the part's default colour (objparams +0x130 = a `paint_colours` entry). Set by the game when paint ≠ default (0x8256C1B8) |
| +0x05 | u8 **setting** = index into the garage's list ([block+0x240], set by 0x82646AB8): wheels 0 Automatic, 1 Driven, 2 Steering, 3 Driven & Steering, 4 Freewheeling (verified by driving in Xenia: 1 and 3 drive, 2 and 4 do not); propellers 0 Automatic, 1 Push, 2 Pull (tables 0x821242A0 / 0x82124598: char[32] text + u32) |
| +0x06 | u8 category = objparams +0x98 + 1 (the game's own early assets: 0) |
| +0x07 | u8 0 |
| +0x08 | u32 objparams id of the part (`aid_objparams_banjox_vehicleblock_*`; unknown ids: Disc Read Error in the blueprint lists) |
| +0x0C/+0x10/+0x14 | f32 rotation X, Y, Z (rad). The game keeps one of 24 orientations per part (quaternion table 0x82F0FCD0) and writes R = Ry·Rx·Rz decomposed with atan2; System.Numerics: `CreateFromYawPitchRoll(Y, X, Z)`. Its float bits are fixed per orientation (90° about X = 0x3FC90FD6, about Y/Z = 0x3FC90FE0, …: `Orientations.Euler`). |
| +0x18 | u32 paint RGBA |
| +0x1C | u32 action buttons: bit 12 = A, 13 = B, 14 = X (combinations allowed) |
| +0x20 | u32 buttons of the part's second action ([block+0x3D8]) |

### Paint as the game draws it

`paint_colours` = `aid_misc_banjox_paintcolours_startpack` (13 records of 0x28: char[32] name, u32 hash, u32 RGBA: blue
4E7BC7, purple AF68CA, magenta E4CBB1, orange E09A07, greenblue 008049, pink CD65A0, black 303030, silver EBEBEB,
greenlime 78E317, aqua 59AFB7, yellow FAFF1A, red D10903, brown 804000); the garage offers these, the format takes any
RGB. Part materials that can be painted carry an `…_editable` texture (R = paint mask, G = hue variation). The paint
reaches the pixel shader as **HSL in c42**; the shader tints the diffuse colour by
`mix(1, hsl2rgb(clamp(H + 0.1·G − 0.05), S, L), R)` and the specular by half of that (literals c79 = (0.1, −0.05),
c104..c106 = the HSL→RGB constants; read from model_banjox_vehicleparts_wheel_standard).

### Part models

Part models are drawn at their cell (one cell = one unit), rotated by the orientation, as authored (+Z = front).
Their rendergraph (chunk 30) lists Maya node paths: `…SWITCH<n>_<k>` = option k of switch n. On a vehicle the game shows
level-0 LOD nodes and one option per switch: the lowest option of the model unless the part sets it (the trolley tray's
SWITCH1_1 junk — books, magazine, frog — shows in the garage and in town); **wheels show option 1** (suspension struts
and springs instead of the boxy Parts Store frame, verified in Showdown Town); the shared seat model
(model_banjox_vehicleparts_seats_all) turns switches 1–6 on from objparams +0x3DC..+0x3F0.

Footprints (avatarhavokdata bounds) can share cells in valid vehicles (the trolley's wheels, bounds y −1..0, hang into
the corners of its tray).

Materials: vehicle part sections with blend nibble 2 are mostly opaque in the game (tyres, Banjo's seat, the trolley
tray mesh are skinned meshes carrying blend mode 2 and render states 0x48=6 / 0x4C=7 like real glass). Treat them as
blended only when the material opacity is < 1 or the texture has real partial alpha (> 5 % of texels between 8 and 247);
a texture with hard alpha (> 1 % of texels < 128, e.g. the propeller's flow arrows) is alpha-tested.

### Attach faces and connectivity (garage hazard)

avatarhavokdata face record, 0x28 bytes: s32 cell x, y, z; u32 direction (0 +Y, 1 +Z, 2 −Y, 3 −Z, 4 −X, 5 +X);
f32 face centre x, y, z (cell units, one coordinate on a half); u32 attachable (1/0); s32 neighbour record (−1 none);
f32 2.0. The direction code is needed for non-box footprints (the L-shaped large engine's inner corner faces).

Two parts are joined where both have an **attachable** face at the same half-cell position with opposite normals (after
the part's orientation, `Orientations.All` = yaw·pitch·roll Y, X, Z). Two tow bars facing each other (their ends are
not attachable) form a hitch. Parts not connected (through other parts) to the piece holding the driver's seat (a
`seats_*` part, or an AI seat variant `*ai` on the game's racers) fall off when the vehicle is built; the garage shows its
hazard triangle (verified in Xenia, Mumbo's garage: a propeller turned so its attachable face points away → hazard; the
same vehicle correct → none; save 0x82 → hazard, 7 parts loose in the editor). With this rule all 274 of the game's
blueprints are one piece. The trolley tray's top (cargo bed) is not attachable: parts set on it float.

### The game's own vehicles

AI vehicles are marker records of type 21 (+0x38 blueprint id, +0x3C driver objparams, +0x40 strategy, +0x54 vehicle
requirements). World of Sports Act 2 (act bundle 3112a5) "Burnin' Rubber": `worldofsport_burninrubber_racer1` = Mr. Fit
(marker #42), `racer2` = Blubber (#43), `racer3` = Thomas (#8; also resident in e66c1c). Every bundle holding a blueprint
must be written. **AI drivers sit in AI seats**: the racers carry `secondaryseats_large` / `secondaryseats_small`
(variants "passengerlargeai" / "passengersmallai"), not a driver seat. A player vehicle put in their place gets that
AI seat instead of its driver seat (moved up out of other parts: with the large AI seat sunk into the vehicle, Mr. Fit's
car stayed on the start line in Xenia); then the race runs with all three replaced vehicles driving (verified: live
positions of all three blueprints change during the race; one loose part fell off Mr. Fit's car).

### Parts Store and game vehicle places

* Parts Store contents = the parts listed in any `aid_misc_banjox_blockset_*` (big-endian u32 pairs count, part objparams
  id; crates, Humba / log crates, keys, Jinjo bingo, start pack, demo pack, backup, blockset_all; resident in 20abf9 / 4bf033).
  118 of the 153 shipped parts are listed. The other 35 are AI / internal variants: `*_ai_*` engines and jets, the AI taxi
  seats (variants passengerlargeai / passengersmallai), cutscene seat, Grunty / Pikelet seats, gameplay-creator parts,
  attach / leak points, autopilot, blower, sucker, remote control, piddles turret, fixed egg turret, heavy high-grip wheel,
  spring ai / trolley, spotlight always-on, shield variant, Grunty floater, egg-'n'-spoon tray, light pole connector.
* Store categories = objparams +0x228 group: seat, wheel, engine (Power), fuel, storage, ammo, body, gadget, protection,
  flyandfloat, weapon, accessory; names in loctext `garage__grouping_*` / `block__group_*`.
* Challenge names: loctext `challenge__<world><act>game<n>` (e.g. challenge__worldofsportact2game1 = "Burnin' Rubber"),
  live challenges `challenge__<world>live<name>`. Vehicle assets carry the challenge in their name
  (worldofsport_burninrubber_racer1); the markers (type 21) of `aid_marker_banjox_<world>_<act>_main` place them.

## 17. Cameras: fixed camera points, warp-pad cameras, cut-scene cameras (NB Studio 1.18, 2026-10-07) **[verified in Xenia]**


### 17.1. Fixed cameras are marker points
A fixed camera is a type-1 marker record ("point"):
- **Position:** +0x14.
- **Pitch:** rotation X at +0x20, positive = looking down.
- **Yaw:** rotation Y at +0x24.
- The view direction is the record's local +Z: (sin y·cos p, −sin p, cos y·cos p).
- No field of view and no roll are stored.

The object that uses a camera points at it with the **u16 at its record's +8** (the same field path nodes use for their next node).

Verified in Xenia (in-game camera at 0x82FADC30):
- The Town Square pad's WARP TO menu camera is exactly point #1083, with its pitch and yaw.
- **Moving the point moved the menu view** (`shots/r12w4_pair.png`).

| Owner of the camera (+8 →) | Marker type | Showdown Town | Note |
|---|---|---|---|
| Warp pad (`props_showdowntown_warppad`, pad number u32 at +0x38) | 37 | 6 | WARP TO menu view of that pad |
| Bolt heads (`props_boltheads_*`) | 33 | 25 (78 in all worlds) | close-up cameras |
| Info points (Banjoland, Spiral Mountain), Jiggy Bank | 14 | 1 (42 in all) | close-ups |
| unidentified | 40 | 0 (2 in other worlds) | – |

- World doors (type 28) and type 19 also point at points, but those points are level (pitch 0): they are places, not cameras.
- In all 102 resident marker sets there are 134 pitched points. NB Studio lists 33 cameras in Showdown Town: 25 bolt-head, 6 warp, the Jiggy Bank and 1 unowned camera point.

### 17.2. Warp pads
- **Pad numbers.** The menu lists pads in number order, and pad numbers are verified by warping: 0 Town Square (Mumbo's Motors), 1 Theater District, 2 Seaside, 3 Lakeshore, 4 Docks, 5 Uptown.
- **Arrival.** Arriving at a pad puts the vehicle on the pad and uses the normal camera behind it, so the arrival view follows the pad by itself. Traced in Xenia: arrival at the moved Lakeshore pad was at the new spot.
- **What caused the user's report.** Only the WARP TO menu view uses the pad's camera point. Studio did not move that point with the pad, so the menu still showed the old surroundings.

### 17.3. Cut-scene cameras (intros, act fly-bys, …)
The camera of an `aid_cutscene_*` main record (the part that is not an "animation") is **sampled per frame (30 fps), uncompressed**:

- **Header:**
  - +0x00: u16 frame count.
  - +0x03: u8 entity count n.
  - +0x04: f32 duration.
  - +0x08: offset of the camera block (= 0x10 + 0x30·n).
  - +0x0C: offset of the entity list (= 0x10).
- **Camera block:** u32 offset of the camera's name ("camera_newShape", "Camera_Shape1", …), then 12 channels of (u16 sample count, u16, f32 duration, u32 offset of f32[count]):
  - 0–2: position.
  - 3–6: rotation quaternion. The camera looks down local −Z (Maya convention).
  - 7: vertical field of view in degrees.
  - 8–11: four more curves, not identified (not edited).
  - A channel with one sample is constant.
- **Where they are stored:** most cut-scenes are streamed (Bundle/50); some are resident.
- **Act intro fly-bys** are the same records (`<world>_act<N>_game<M>_intro`).

**Verified in Xenia:**
- **Matched to the data.** I traced the camera through Showdown Town's new-game intro (quick test with the intro kept). Every traced position lies on `showdowntown_animatedsequence1_shot3` (streamed, 992 frames) within 0.01–0.02 units, and the view direction equals the quaternion's −Z.
- **Edit reaches the game.** I moved its key 15 (frame 450) up 15 units in Studio and saved. The game's camera now rises to Y 20.7 at that moment (original 6.5), then returns. Screenshots: `shots/r12_cutscene_before_after.png`, original left, edited right (same trigger point, the edited camera is higher and shows the roof).

**Not edited:**
- `aid_cutcam_*` (13 small "award jiggy" assets of 60-byte keys, likely relative to Banjo).
- Camera volumes / triggers: no camera-specific volume type was found; type 7 volumes link to each other.


## 18. Joint scale and the outer collision shell (NB Studio 1.19, 2026-10-07) **[verified in Xenia]**

### 18.1. Joint scale in animations
- Animations scale joints (Thomas 1.25, Mr. Fit 1.42, Klungo up to 1.24, Boggy 0.86–1.15). The scale follows Maya segment scale compensation: `local = S(joint) · R · S(parent)⁻¹ · T`.
- A parent's scale moves its children (their offsets grow) but does not scale their geometry again. FBX `InheritType 2` is the same rule.
- Scaling each joint's geometry while keeping the bind-pose bone lengths gives oversized hands and props, and rigid props (glasses, medals) sink into the body.

### 18.2. The outer collision shell (Nutty Acres)
- The terrain collision has an invisible outer box: 12 triangles of a ±982-unit box in `aid_havok_…_nuttyacres_default`, plus the outer-wall triangles in `…_defaultchunk2`.
- Deleting those triangles makes the game draw black sky with stars in those directions; the black area turns with the camera.
- Deleting an ordinary collision triangle does not cause this.
- The engine mechanism is not traced; the likely explanation is that the outer box sets the draw distance or the visibility bounds.
