# VisionStaticAnnulus.cs documentation for version 1.0

This is the visual-field stimulus object: a flat annulus (or a filled disc, when the inner diameter is zero) whose position and size are defined in **degrees of visual angle at the eye**, rendered unlit in a solid color, always facing the eye and always in front of everything else. It is the "programmable scotoma": the *same* script is attached to two different empty GameObjects, and each instance behaves fully independently — this is the textbook case of re-using one MonoBehaviour across several GameObjects, and the rules that make it safe are documented below.

## A brief history of this code

The lab's previous attempt at "one script, many objects" bit us for a long time, so the root cause is worth writing down before the lesson fades.

`StreamRigidBodyFromOptitrack` (the OptiTrack rigid-body client, now active) used to have exactly this shape: attach it to a GameObject, toss a mesh on, track something. Attach a second one for a second rigid body, and the *second* object only updated every second frame, the third every third, and so on. The old file even carried the comment "I suspect this happens because of Unity's execution model. Not sure, proceed with caution." The suspicion was wrong, and the real cause is one line in that file:

```C#
private static string received_payload_as_string;
```

Every instance's socket had its own `BeginReceive` chain writing into that **one shared `static` buffer** (last packet wins), and every instance's `Update()` read the same string and applied it to its own transform — but only when the packet's rigid-body ID matched what *that* instance asked for. N interleaved streams into one shared slot: object *k* only "sees" its own data on the frames where its packet happened to be the most recent write. Round-robin. The old workaround — copying the script and renaming the class — "fixed" it only because a distinct class has a distinct home for the `static`.

The rule this project now follows, and that `VisionStaticAnnulus` is built around:

- **Per-object data lives in the component instance.** Plain instance fields on the `MonoBehaviour` are, per attached copy, exactly the "unique per-GameObject variable" one wants. No registry, no singleton, no `FindObjectOfType` coordination.
- **Callbacks that touch data are instance methods.** (`StreamRigidBodyFromOptitrack`'s fix was one line: drop the `static` — `receive_message` is an instance method, so it already knows *which* instance it belongs to; now the documented house rule, on-device verified 2026-11-04.)

`SilverAnnulus.cs`, the earlier throwaway annulus (kept as a starting point from an older revision), is the anti-pattern: its `public static float innerRadius / outerRadius` are shared by every attached copy — do not carry that pattern anywhere.

A second, device-side lesson was written into this script the hard way: the first iteration of this session rendered through a **project-written** shader with the same intended pass state. On the Quest it silently failed to appear — two device runs, no useful error in the logs, shader and setup verified in the editor. The lab's own field record (Oculus Go era) is the same shape: **Meta's own shaders are the most reliable baseline on this hardware class**; custom shaders occasionally just do not make it onto the device. So the base material now points at the Meta XR SDK's own **`Unlit/Crosshair`** (`com.meta.xr.sdk.core`, `Shaders/Unlit Crosshair.shader`) — which already declares exactly the pass state this object needs. The project-written shader remains at `Assets\Shaders\VisionStaticAnnulus.shader` as a documented alternative (e.g. for non-Meta targets), but it is not the default. See the "Rendering" section for the color contract that comes with the choice.

(Also: an unimplemented placeholder, `RetinoStaticAnnulus.cs`, existed for a while; it was deleted in this session to avoid two "the intended script" — `VisionStaticAnnulus.cs` is the one.)

## How it is used?

* Attached (in the editor, by hand — different settings per instance are expected and fine) to **two empty child GameObjects** under the two anchor pivots that are children of `CenterEyeAnchor`:
  * **`Scotoma`**, under `GazeContingentGameObject` — *gaze-contingent*: it rides the retino-centric frame, so it follows the gaze.
  * **`EyeTrackerCalibrator`**, under `HeadContingentGameObject` — *head-contingent*: fixed to the head.
* Everything else is per-instance state: each copy has its own `renderEnabled`, `centerAngles`, diameters, color, queue. Moving or recoloring one changes nothing about the other.
* The script is self-bootstrapping: on an empty GameObject it adds its own `MeshFilter` / `MeshRenderer` and builds its own per-instance mesh and material. There is nothing to pre-place.
* Runtime parameter control is by *writing the public fields* (from the Inspector, or from another script holding a component reference, e.g. `GameObject.Find("Scotoma").GetComponent<VisionStaticAnnulus>()` and then `.outerDiameter = 5f;`). The script converges to the written values on the next frame. There is no port on this script; the external control lives in `ExternalVisionStaticAnnulusAdjuster` (ports `40006`/`40007`), which does exactly this field-writing (the colour field goes through this same hex API — `TryParseHexColor`, then `annulusColor = …`).

## The geometry (the "why")

Tangent-plane convention, perimetry-standard. Let `D` be the eye-to-anchor-pivot distance, `(az, el)` the stimulus center in degrees of visual angle:

```
x     = D · tan(az)            local x of this GameObject
y     = D · tan(el)            local y of this GameObject   (local z stays 0)
r_out = D · tan(outerDiameter / 2)   → local x/y scale
r_in  = D · tan(innerDiameter  / 2)  → baked into the mesh as the ratio r_in / r_out
```

Three decisions in there that would be easy to get wrong from a distance:

- **`D` is the parent's `localPosition.magnitude`, not `.z`.** The gaze pivot's *position* is re-written every frame by `GazeContingentTransform2` as `calibratedGaze * scotomaLocalOffset` — i.e. the `(0,0,D)` offset is *rotated*. Rotations preserve length, so the magnitude stays exactly the authored depth, while the `.z` component shrinks by `cos(gaze)` (a 13 % error at 30° of gaze, which would silently modulate the apparent size). This holds for the head anchor as well, where the position is a plain `(0,0,D)`.
- **The facing falls out of the retino frame — and the cull side is easy to get wrong.** In an anchor pivot's local frame the eye (`CenterEyeAnchor`) sits exactly at `(0, 0, −D)`. So a stimulus placed at local `(x, y, 0)` automatically lies in a plane perpendicular to the eye→stimulus line: no per-frame `LookAt()` needed. But it also means the eye is on the **−Z side** of the plane, so a front-culled, single-sided annulus would face the eye the wrong way. The shader pass is `Cull Off` (double-sided) for exactly this reason.
- **`r_in` is baked as a TANGENT ratio, not a linear one — `tan(inner/2) / tan(outer/2)`, never `inner/outer`.** The whole mesh is scaled by `D · tan(outer/2)`, so a linear `inner/outer` places the inner edge at `(inner/outer)·D·tan(outer/2)` instead of the intended `D·tan(inner/2)`. Both read the same while the angles are tiny (tan ≈ linear — the old 0.1/0.5 defaults), but at `outerDiameter` of 80–120° the foveal **hole grows with the outer** (measured 2026-11-02: `inner=1°` lands ≈1.65× too big at `outer=120°` and ≈1.20× at `outer=80°`). `tan()` puts both edges at their true visual angles, so the hole is independent of the outer. This is the formula the header and line 45 above always stated (`r_in/r_out`); the code had drifted to a linear ratio and this session re-aligned it.

The mesh itself is a **unit outer-radius** ring in the local XY plane. The inner/outer *ratio* is baked into it, and runtime size comes from the transform scale — so when the depth changes (see below), only a cheap re-scale happens. The mesh is rebuilt *only* when the inner/outer diameters (or the segment count) actually change; there are no per-frame allocations once built. `innerDiameter = 0` gives a filled disc — rendered as a **hairline ring** (the ratio is floored at `0.0001`, a sub-pixel hole), deliberately **not** the fan branch: a fan (a center vertex shared by all N triangles) corrupts the shared `mesh` on a ring→disc transition (the vertex array shrinks while the old, larger triangle array is still attached → stale indices → the "smaller disc + a vertex poking out" artifact, reproduced and cleared on-device 2026-11-02). Flooring keeps every state in the clean ring branch; the fan `else`-path in `RebuildAnnulus` is now unreachable and kept only as the reference shape.

Angles use the eye-tracker convention: **azimuth +x = right, elevation +y = up, dead-center-ahead = (0, 0)** — the same convention as `GazeContingentTransform2.CurrentGazeVector`.

## Inverse-scotoma / tunnel-vision — disc radius beyond the FOV (added 2026-11-02)

A lab use case that drove this session's three changes: the **inverse scotoma** (a.k.a. tunnel vision) — the *entire* visual field is a solid colour **except a round foveal disc** that stays clear. The opposite of the small scotoma-with-clear-surround this object also supports. Because it rides the gaze pivot, the solid "field" must extend **beyond the headset's FOV** in every direction (look hard left and still see no scene leaking in on the right), while the fovea stays a round window at the centre.

- **The "90° limit" is split — and only one half is genuine.** `maxCenterAngle = 89.99°` caps the disc **centre** (azimuth/elevation): a point past 90° is behind the eye, a real ceiling. `maxDiameter = 170°` caps the disc **outer edge**, i.e. a **radius of 85°**: big enough to sit past the ~±55° half-FOV, so the field's boundary is never seen. (`maxDiameter` was `89.99°` before this session, capping the radius at ~45° — *inside* the FOV, which is exactly why the field used to leak.)
- **The inner must stay a tangent ratio** (see "The geometry"): `inner=5°` is a 5° round foveal disc whether the field is 80° or 170° wide.
- **`innerDiameter = 0` (filled disc) is a hairline ring, not a fan** (see "The geometry").
- **Flat-plane ceiling, and the next step.** A *flat tangent* disc's radius is geometrically capped at ~89.9° (diameter ~179.8°) — `maxDiameter` can be raised to that. **Beyond radius ~89.9° a flat plane physically cannot reach the field**; the correct next surface is a **sphere** centred on the eye (keeps the foveal disc round *and* supports all angles). A **cylinder** is the wrong shape for a round fovea: its inner edge is a vertical channel, so it renders the foveal "window" as a tall rectangle, not a disc (a cylinder only suits a tunnel whose exit is an arch).

On-device (headset, 2026-11-02): `inner=0` filled disc renders clean (the floor fix); a 150° field (radius 75°) covered the *glasses* case but leaked a little bare-eye periphery; 170° (radius 85°) covers it; the foveal disc size stayed independent of the field width (the tangent-ratio fix).

## Rendering (always visible, no lights, no GI, no reflections)

The base material (`Assets\Resources\Stimulus\VisionStaticAnnulus.mat`) uses the **Meta XR SDK's own `Unlit/Crosshair` shader** — Meta's own shaders are the proven-reliable baseline on Quest hardware (see history above), and that pass declares exactly what this object needs:

* **`ZTest Always`** — never occluded by depth. This is what makes "always visible against anything else" true; a high render queue alone does *not* (a closer hand would still win the depth test).
* **`ZWrite Off`** — the stimulus never pollutes the depth buffer for anything else.
* **`Cull Off`** — both sides render (the eye is on the −Z side of the retino frame; see above).
* Unlit: the fragment is `texture * vertexColor * _Color * 2.0` — no lighting response of any kind, no GI, no reflections. The texture defaults to the built-in `white`.

**COLOR CONTRACT (calibration-critical).** Because of that fixed `* 2.0`, the on-screen color is exactly `C` **iff** the script stores `_Color = C / 2` *and* the mesh vertex colors are white. Both are enforced in the script (it divides before writing; the mesh builder pins `Color.white`). The requested color — the public field, the hex string — is always the *display* color C; the stored value is C/2. Do not "fix" a display color by editing the stored one, and do not remove the division without re-reading this paragraph. (Alpha works out consistently: `1 · 1 · 0.5 · 2 = 1`, opaque under the pass's `SrcAlpha` blend.) **VERIFIED ON DEVICE 2026-10-28** — the requested colors matched the lab's calibrated references.

One nuance to keep when adding instances: objects with `ZTest Always` and the *same* render queue have an undefined draw order between them. `renderQueue` (a public field, default `3001`) is what orders instances against each other — if two are visible at once and overlap, give them different queues.

Two per-object-state / packaging rules:

* **Each instance creates its own `Material`.** A shared material would be a second copy of the shared-state bug: one instance's color write would leak into the others.
* **The base material stays in the project `Resources` folder.** Resources content is always included in the build, and a material drags the shader it references along with it — that is the mechanism that gets `Unlit/Crosshair` into a device build without `Shader.Find` or Graphics-settings registration. Moving the asset elsewhere breaks that, and the failure mode is the same silent one described in the history. (The first iteration — `Shader.Find` of a project-written shader — failed on-device twice; that is exactly why this design exists.)

## Externally available variables

The public fields, for meddling with in the Inspector (one set per attached GameObject):

```C#
    public bool renderEnabled = true;            // on/off; hiding also stops the per-frame pose work
    public Vector2 centerAngles = new Vector2(0, 0); // azimuth (x), elevation (y), degrees
    public float innerDiameter = 0.1f;            // degrees of visual angle; 0 => filled disc (a sub-pixel ring, not a fan)
    public float outerDiameter = 0.5f;            // degrees of visual angle; kept >= innerDiameter
    public Color annulusColor = new Color(0.5f, 0.5f, 0.5f, 1f);  // "#808080" = 50% grey
    public int renderQueue = 3001;                // keep unique between simultaneously-visible instances
    public int segments = 96;                     // circle resolution (min 3)

    // Diagnostics (default OFF — enable on the instance while debugging on device; see "On-device troubleshooting"):
    public bool verboseDiagnostics = false;          // 4-line state log: first frame + every diagnosticsPeriod frames
    public int diagnosticsPeriod = 100;
    public string debugMaterialResourcesPath = "";   // "" = base material (Unlit/Crosshair); "Stimulus/DebugSolid" = known-good opaque fallback
```

The programmatic color entry points (the future external-control face):

```C#
    public bool SetColorHex(string hex);                 // sets annulusColor from "#808080" etc.
    public static bool TryParseHexColor(string hex, out Color color);
```

Hex is the **canonical form**: the lab's calibration colors are the headset's calibrated inner-screen colors, and those are written down as hex. Unity's Inspector color slots accept hex natively, so both entry points agree. Accepted forms: `#RGB`, `#RRGGBB`, `#RRGGBBAA` (leading `#` optional).

## Interplay with the rest of the scene (and what is deliberately NOT touched)

- **Port 40004 / `ExternalScotomaDistanceAdjuster`:** that script sets the *anchor pivots'* depth (the gaze one through `GazeContingentTransform2.scotomaLocalOffset`, the head one through `localPosition.z`). Because `VisionStaticAnnulus` re-reads the live parent depth every frame, a 40004 change re-poses and re-scales the stimulus on the next frame with **zero coupling** — no change to either script, no event, no reference. That is the whole of requirement "sizes always recalculated when the Z-depth is externally adjusted".
- **The anchor pivots' transforms are owned by others** (`GazeContingentTransform2` re-writes the gaze pivot's local position/rotation every frame). This script only ever *reads* them. Do not start writing to the pivots from here, and do not reparent the stimulus off the pivots.
- **`test_cube`** (under the gaze anchor, disabled) is the lab's eye-tracking verification aid ("the cube should always follow my gaze and should face me"); it is independent of this script.
- No UDP port, no sockets, no callbacks. There is no off-thread code in this script.

## On-device troubleshooting (RESOLVED 2026-10-28 — kept as the decode for any future invisibility)

**Symptom (then).** The stimulus did not appear on the Quest Pro over several builds, while in the *same builds* `test_cube` (URP/Lit) and the Meta hand meshes rendered. A single, non-reproduced artifact flash at app start was reported (it was not this object — see the root cause below).

**Established by reading the code (so placement is NOT the suspect).** `GazeContingentTransform2.cs:293–294` writes the gaze pivot as `localRotation = calibratedGaze` and `localPosition = calibratedGaze * scotomaLocalOffset` — i.e. the pivot sits exactly `D = |scotomaLocalOffset|` ahead of the eye, **on** the eye→pivot line, with its local +Z along that line. Therefore its local XY is the tangent plane perpendicular to the eye axis, and the `Scotoma` child (local `(0,0,0)`) is at the fixation point, in front of the eye. `test_cube` (pivot-local `(0,0,0.1)`) renders where expected, corroborating the frame. ⇒ **The annulus is verifiably in front of the eye; the failure is on the rendering side** (the material / pass chain, or the material never resolving on device).

**Diagnostics (standing lab tool — default OFF; per-instance, see the `Diagnostics` block in the script):**

| Inspector field (per instance) | Default | Set to, when debugging on device |
| --- | --- | --- |
| `verboseDiagnostics` | `false` | `true` |
| `debugMaterialResourcesPath` | `""` (the base material, `Unlit/Crosshair`) | `"Stimulus/DebugSolid"` |

* **`DebugSolid.mat`** (`Assets\Resources\Stimulus\`): **URP/Lit** — the same shader `test_cube` uses, i.e. *proven to render in this exact build* — with `_Cull = 0` (Cull Off, so the test does not depend on our winding-vs-eye analysis), **opaque** (no blending), constant color. It differs from `Unlit/Crosshair` on exactly the suspect axes: opaque pass instead of the transparent band, no `ZTest Always`, no texture sampling, no vertex-color multiply, no ×2, URP shader instead of a built-in CG pass.
* **`[VisionStaticAnnulus][dbg]` lines** every ~100 frames + first frame; four lines: (1) pivot, D, and our own local pose/scale; (2) **eye-relative world position — `inFront` > 0 means in front of the eye** (it is the signed distance ahead on the gaze axis); (3) renderer enabled, **`filterMesh` (the actually-attached mesh — the 2026-10-28 failure was a NULL here)**, live material name + shader, mesh tris + extents; (4) requested color vs the color actually stored in the material, + queue.

**Reading the diagnostics build:**

| Observation in the log / on the headset | Conclusion | Next step |
| --- | --- | --- |
| loud `base material not found` / `no valid shader reference` | the material never resolves on device (asset moved, packaging) | fix the asset/path; no further guessing |
| `inFront ≤ 0` or `distToEye ≫ D` | our frame assumption is wrong in practice | the log lines give the real numbers — re-derive placement |
| `renderer.enabled = False` | `renderEnabled` got flipped | find the flip (it is logged nowhere else — add one if it recurs) |
| `filterMesh = NULL` on a `[dbg]` line | the mesh is **not attached to the MeshFilter** — the renderer draws nothing, silently (the actual 2026-10-28 failure) | `meshFilter.sharedMesh = annulusMesh` must exist in `Start()` |
| `tris = 0` or tiny `meshExtents` | mesh never built / degenerate | check `Update()` early returns in the log |
| **DebugSolid visible, crosshair invisible** | geometry is fine; the defect is the crosshair pass: transparent band / `ZTest Always` under URP-XR, or the `tex × vcolor × _Color × 2.0` chain | isolate one axis at a time (e.g. crosshair with `_Color.a = 1`; crosshair with vertex colors removed) — the stored `_Color` line tells you what the script actually wrote |
| **Both invisible, log state all healthy** | culling / batching / XR-pass issue, or the material silently absent | point `debugMaterialResourcesPath` at any other known-good Resources material; check URP asset; grep the MQDH log for `Exception` (an uncaught `Update()` exception kills the pose write — and it would appear in the log) |

**RESOLVED (2026-10-28).** The confirmation build showed both discs. With the `DebugSolid` override they came out in the asset's `_BaseColor` (an IBL-lit brick red — URP/Lit ignores the script's `_Color` write, as documented above); after clearing the override back to `Unlit/Crosshair`, both discs took on their **requested** colors, matching the lab's calibrated references. Passed in the course of the resolution: visibility + per-instance parameters + colors (items 1, 3, 7), **hand occlusion — the annulus rendered *above* the hand (item 4, proof of `ZTest Always`)**, and the disc/ring states (item 6). Items 2 and 5 remain regression checks for when those parameters change.

**ROOT CAUSE (the lesson, for when invisibility recurs):** `annulusMesh` was never assigned to `meshFilter.sharedMesh`. A `MeshRenderer` with a material but **no mesh on its filter draws nothing, with no log and no error** — and the `[dbg]` lines were reporting the mesh *object* (tris, extents), everything except the hookup. The 20:46 device log proved the rest was perfect: material resolved, transform right (`inFront = 0.5`, scales matching hand calculation), renderer enabled. The fix is one line in `Start()` (`meshFilter.sharedMesh = annulusMesh;`), and `LogDiagnostics` reports `filterMesh=… | NULL` so the hookup itself is auditable. Note for the archive: the two earlier "shader" symptoms were real but separate — the first iteration's project shader genuinely failed to reach the device build (two rounds of loud `shader not found`); the fix was the Resources base material + the SDK's `Unlit/Crosshair`. The mesh hookup bug was stacked underneath both all along.

## Method of operation

* `Start()` — pins the culture to `en-US` (lab convention); adds `MeshFilter` / `MeshRenderer` if missing; **assigns the per-instance `Mesh` to the `MeshFilter` (`meshFilter.sharedMesh`) — the render hookup; silently a no-draw if ever removed** (this was the 2026-10-28 on-device failure); loads the base material from `Resources/<debugMaterialResourcesPath or "Stimulus/VisionStaticAnnulus">` (loud error if the asset — or its shader reference — is missing; on success it logs `base material '…' OK; shader = '…'`, which is the definitive "what is this instance actually using" line); instantiates a per-instance copy; caches `CenterEyeAnchor` for the diagnostics' eye-relative math.
* `Update()` — every frame, on the main thread:
  * applies `renderEnabled` (toggles the renderer), color and render queue (only when changed);
  * sanitizes the diameters / angles (non-finite input collapses to a finite value, one warning per changed input — never per frame);
  * rebuilds the mesh only when the inner/outer ratio or segment count changed;
  * computes `D = parent.localPosition.magnitude`, then writes `localPosition = (D·tan(az), D·tan(el), 0)` and `localScale = (D·tan(outerDiameter/2), D·tan(outerDiameter/2), 1)` — no allocations;
  * when `verboseDiagnostics` is on: emits the 4-line `[VisionStaticAnnulus][dbg]` block (on the first frame, then every `diagnosticsPeriod` frames) — see "On-device troubleshooting" for the decode.
* `OnDestroy()` — destroys the per-instance `Material` and `Mesh` (no leaks, no stray shared resources).

## Test checklist (on-headset, per instance, with distinct colors)

The two instances should be set to *different colors* so any shared-state leak becomes visible immediately:

* **Verified on device 2026-10-28 (during the on-device resolution):** both instances visible with their distinct parameters and requested colors (items 1, 3, 7), **item 4 hand-occlusion passed — the annulus rendered *above* the hand** (proof of `ZTest Always`). Items 2 and 5 remain the regression checks when the angle or depth parameters change.

1. Both annuli visible at fixation, grey-ish defaults; toggle each's `renderEnabled` independently.
2. Set `centerAngles` on one (e.g. az 10°, el 5°) — it moves; the other does not. The gaze one follows the gaze; the head one stays head-fixed.
3. Set distinct colors (e.g. `#808080` and a calibration blue) — each keeps its own.
4. With hand rendering on and a hand between eye and stimulus, the annulus still renders (ZTest Off) — and hands are not affected by the stimulus (ZWrite Off).
5. From the PC: `python3 PythonUdpBlaster <headset_ip> 40004 "0.7" 2`, then `… 40004 "0.4" 2` — both annuli should stay the *same size in degrees* (they rebase; the angular size is invariant), which is the requirement-4 check.
6. `innerDiameter = 0` → filled disc (a sub-pixel ring, **not** a fan — confirm it renders as a clean round disc, no "vertex poking out"); restore → annulus again. (Mesh rebuild on change.)
7. **Color accuracy**: set one instance to a known calibration hex (e.g. `#00FF00`, then `#808080`) and compare the rendered color against the lab's calibrated reference. A color that looks *doubled* / washed out means the `× 2.0` contract (Rendering section) or the white vertex colors were touched.

## Expected log markers when something is off (grep `[VisionStaticAnnulus]`, note the instance name)

* `… ('Scotoma') base material '…' OK; shader = '…' …` — the good line: confirms, per instance, exactly which material + shader resolved on device. If you do NOT see it, look for the two error lines below.
* `… ('Scotoma') base material not found at Resources path '…' …` — the material asset is missing, or was moved out of `Assets\Resources\`; nothing will render (see the packaging rule in "Rendering").
* `… has no valid shader reference …` — the material is there but its shader reference dangles (package gone or incompatible version); nothing will render.
* `[VisionStaticAnnulus][dbg] (…)` — the 4-line diagnostics block (first frame + every `diagnosticsPeriod` frames); decode in "On-device troubleshooting": line 1 pivot/D/local pose, line 2 eye-relative (`inFront` ≤ 0 = behind the eye), line 3 renderer/mesh/live material, line 4 requested vs stored `_Color` + queue.
* `… must be a child of one of the anchor GameObjects …` — the component was attached somewhere it cannot read a depth from.
* `… anchor depth is ~0 …` — port 40004 was set to `0`, a valid-but-degenerate state: the stimulus collapses into a point at the eye.
* `… diameters out of the valid range …` / `… centerAngles out of the valid range …` — input clamped (valid: diameter `0..170°`, i.e. radius ≤ 85°, `outer >= inner`; center azimuth/elevation `±89.99°`); logged once per changed bad value, not per frame.
* `… renderQueue … clamped to …` — queue outside Unity's `1000..5000`.
* Any `Exception` with a `VisionStaticAnnulus.Update` frame on the stack — the per-frame work died mid-way (the pose write after the exception never happens); the log says exactly where.
