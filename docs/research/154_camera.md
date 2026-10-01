# 15.4 Photo camera range limit (static RE, 2026-09-27)

Static analysis of `work/default.exe`. Nothing was run in Xenia, so the patch below is **not verified in-game**.

## Result

The photo camera (used by pause menu → Photos & Videos → Take Photo, and by the replay "Photo Mode" button) is
kept inside a **sphere of radius 30.0** around an origin that is saved when photo mode starts.

| What | Address | Value |
|---|---|---|
| Per-frame photo-camera controller | `0x8229A8D0` (325 instr.), called from `0x8224E450` (game update, `r3 = [app+0x15B0]+0x40`) | runs only when state `+0x124 == 1` |
| Radius constant load | `0x8229AB50  C01EED08  lfs f0,-0x12F8(r30)` with r30 = `0x82129028` | reads `0x82127D30` |
| Radius constant | `0x82127D30` (.rdata) | `0x41F00000` = **30.0** |
| Clamp branch | `0x8229AB98  40990044  ble cr6,0x8229ABDC` | skips the clamp while the distance is ≤ 30 |
| Origin set on entry | `0x82535A58` (photo-mode enter), `0x82535B4C..0x82535C14` | see below |

**Do not change the constant at 0x82127D30.** It is a shared, compiler-pooled literal. `tools/probe/constref.py 0x82127D30`
finds 49 load sites in about 45 functions (camera code 0x822F0B20 and 0x822F9B58, vehicles, UI and more).

## Patch proposal

Recommended: remove the clamp. It then never runs, so the range is unlimited.

```toml
[[patch]]
    name = "Unlimited photo camera range"
    desc = "Photo mode clamps the free camera to 30 units from its start origin (0x8229A8D0). The ble that skips the clamp is made unconditional."
    author = "NB Mod Tool"
    is_enabled = false

    [[patch.be32]]
        address = 0x8229AB98
        value = 0x48000044 # was 0x40990044: ble cr6,0x8229ABDC -> b 0x8229ABDC (never clamp to the 30.0 radius)
```

Alternative with a large but finite range: point the `lfs` at an existing 10000.0 literal in .rdata. The reach from
r30 = 0x82129028 is ±32 KB.

```
0x8229AB50: C01EED08 (lfs f0,-0x12F8(r30) -> 0x82127D30 = 30.0)
         -> C01EBA0C (lfs f0,-0x45F4(r30) -> 0x82124A34 = 10000.0)
```

Other literals within reach: 1000.0 at 0x821243D8 (`C01EB3B0`), 500.0 at 0x8212487C (`C01EB854`), 100.0 at 0x82124BC8
(`C01EBBA0`), 2000.0 at 0x82127104 (`C01EE0DC`). The clamp block uses the same `f0` for the rescale, so one word changes
both the compare and the new radius.

## How it works (evidence)

### Controller 0x8229A8D0 (r27 = photo object)

- It reads the pad at `[r27+0x120]`: two sticks with a 0.1 dead zone (0x82124CA8) rescaled by 1/0.9 (0x8219F464), plus the triggers.
  The frame time comes from `[0x82FAC644]`.
- `0x82536160(r3=r27+0x50, r4=input, r7=out, f1=dt, f2=speed scale)` rotates the input by the camera angles at
  `r27+0xA0` (via 0x82266198). The per-axis speeds are 20 / 5 / 10 units/s (literals 0x82127D34, 0x8212A29C, 0x8212DD9C;
  all shared). It then adds the result to the current camera position `r27+0x60`, giving the proposed position in `r1+0x80`.
- **Clamp to the sphere around the origin `r27+0x130`:**

```
8229AB3C: addi r11,r1,0x80
8229AB40: li r10,304                     ; 0x130
8229AB50: lfs f0,-0x12f8(r30)            ; 0x82127D30 = 30.0
8229AB54: lvx128 v2,r0,r11               ; v2  = proposed position
8229AB5C: lvx128 v12,r27,r10             ; v12 = origin (+0x130)
8229AB60: vsubfp v0,v2,v12               ; d = p - origin
8229AB68: vmsum3fp128 v10,v0,v0          ; |d|^2
8229AB6C: vrsqrtefp v13,v10  ...         ; Newton step -> |d|
8229AB90: lfs f13,0x80(r1)               ; f13 = |d|
8229AB94: fcmpu cr6,f13,f0
8229AB98: ble cr6,0x8229abdc             ; |d| <= 30 -> keep
8229AB9C: vmsum3fp128 v11,v0,v0          ; else normalise d ...
8229ABA4: stfs f0,0x68(r1)               ; radius (30)
8229ABD8: vmaddfp v2,v0,v9,v12           ; p = origin + normalize(d) * 30
8229ABDC: addi r28,r27,0x60
8229ABFC: bl 0x822eff20                  ; sphere-cast collision, radius 0.75 (0x82125D24)
8229AC24: stvx128 v0,r0,r28              ; store camera position (+0x60)
```

- `0x822EFF20` is world-collision resolution only. It does up to 10 sphere casts through 0x823C8118, with the old position
  in v1 and the new one in r5, and pushes the camera out of geometry. It has no range limit.
- After that come the right-stick angle updates at `r27+0xA0..0xA8` (rate −2.0944 = −120°/s, 0x8219F290) and a view-ray
  query (0x8226B9C8 / 0x82273B60) that sets the depth-of-field/focus values `+0x158`, `+0x70..+0x7C`. These are not limits.

### Photo-mode enter 0x82535A58 (sets the origin)

It is called from the pause-menu handler `0x825AA0D8` (in 0x825A9A78, which uses the `pause_inputreplaytitle` strings) and
from the replay scene's Photo Mode handler `0x8253C838` (in 0x8253C580, "ReplayPhotoMode"). Both pass the photo object
`[app+0x15B0]+0x40`.

```
82535AB4: bl 0x82536238                  ; set state (+0x124) = 1
82535B4C: addi r9,r31,0x130
82535B50: lvx128 v0,r0,r25               ; r25 = r31+0x60: camera start position
82535B58: stvx128 v0,r0,r9               ; origin = camera start position
   ... r11 = focus actor (player vehicle via [owner+0xA44+idx*8]+0xC3C, else the avatar)
82535BD4: lvx128 v12,r11,r10             ; actor position (+0x50)
82535BD8: vsubfp v0,v12,v0
82535BDC: lfs f0,0x20(r8)                ; 0x82127D30 = 30.0 (same literal)
82535C0C: fcmpu cr6,f13,f0
82535C10: bge cr6,0x82535c18
82535C14: stvx128 v12,r0,r9              ; if actor is within 30 of the camera, origin = actor position
```

So the origin is Banjo or his vehicle if the camera starts within 30 units of it, otherwise the camera's start position.
The range is 30 units from that point. After the recommended patch this choice no longer matters.

### Photo object layout (base = `[app+0x15B0]+0x40`; constructor 0x825355B8)

| Offset | Meaning |
|---|---|
| +0x00 (u8) | camera activated (0x822ECD68) |
| +0x50 | camera struct named "Photo_Camera_Name" (0x822EDDF0) |
| +0x60 | camera position |
| +0x80 | camera matrix (built by 0x8224FE80) |
| +0xA0..+0xA8 | camera angles |
| +0x120 | pad |
| +0x124 | state: 0 idle, 1 free camera (the controller runs), 4 capture/render (0x8229ADE8), 7..11 save/UI states (state setter 0x82536238) |
| +0x130 | origin (vec4) |
| +0x148 | owner |
| +0x158 | smoothed focus distance |
| +0x180 / +0x184 | timestamps |

The constructor also loads the "photo_viewfinder" and "photo_polaroid" UI assets.

## Risks of unlimited range

- **Streaming / LOD / culling:** world sections, LOD and object activation are driven by the player/level, not the photo
  camera. Far from the player the camera will show unloaded or low-LOD scenery, missing objects and the skybox edge.
  Nothing suggests a crash, but it is not verified.
- **Collision:** the 0.75 sphere cast (0x822EFF20) still stops the camera at walls and ground, which is intended. Outside
  the Havok broadphase or loaded collision, casts should simply return no hit, so the camera flies freely. This is
  unverified; the broadphase border behaviour is set in code (see §13.5).
- **Speed:** movement is only 20/5/10 units/s, so long trips are slow. Those literals are shared; to change speed, patch the
  `lfs` in 0x82536160 (0x825361A0 / 0x825361AC / 0x825361B8) to point at other literals, not the literals themselves.
- **Scope:** the change affects both pause-menu photos and replay photo mode, since they share the same controller.
  Photos taken far away still save normally (the capture path 0x8229ADE8 has no distance check).
- **Other users of 30.0 are untouched** by the branch patch. The `lfs` alternative only changes this one load.

## Correction to FORMATS §13.6

Entries in the class registration table at 0x8212B480.. are `(name ptr, type, factory)`. So the factory for
**cameraModeEditor** is **0x822F2090** (descriptor 0x82FB3BD4, update callback 0x822F2120). 0x82320568 is the factory of
the preceding entry, entityAvatarMiscAnimDelete. Its callbacks 0x8245AF80/0x823205F0/0x82320750/0x82320688 are not camera
code.

The cameraModeEditor update (0x822F2120) is a snapped orbit camera: zoom level +0x40 is 0..3, elevation +0x44 is 0..2 and
a 3-way cycle is at +0x48. It is most likely the garage/workshop editor camera, not the photo camera.

Other findings:
- The camera-mode enum names are at 0x82E919E0 (Null=0, String, Chase, Flight, Editor=4, … Replay=20).
- `aid_misc_banjox_camera_statetable` maps each mode name to an objparams asset id.
- `aid_misc_banjox_camera_globalsettings` (60 bytes: 0.15, 1.28, 10, 20, 1, 1.4) is used for the replay view camera
  "Replay_Play_View_Replay_Camera" and has no photo-range field.

## Confidence

- High: 0x8229A8D0 clamps a camera position to 30.0 around `+0x130`.
- High: 0x8229A8D0 is the photo free camera (the object is built with "Photo_Camera_Name" and "photo_viewfinder", and the
  enter function is called from the pause-menu photo path and from replay Photo Mode).
- Medium: in-game behaviour of the patch. It is not tested, and the streaming/culling appearance far from the player is
  unknown.

## Helper scripts added (tools/probe)

- `vmx.py`: VMX/VMX128 decoder, now used by `ppc.py`. `ppc.py` also decodes fctid, mffs and mtfsf.
- `dumpw.py <va> [n]`: dumps words as floats and string pointers.
- `floatscan.py <start> <end> [--consts]`: for each function in the range, counts sqrt ops and compares and lists float
  literals.
- `immfind.py <imm> [start end]`: finds uses of a 16-bit displacement or immediate.
- `constref.py <va>...`: finds lfs/lwz/lfd loads that resolve to a data address, tracking lis and lis+addi bases.
