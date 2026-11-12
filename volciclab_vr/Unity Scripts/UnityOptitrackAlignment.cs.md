# UnityOptitrackAlignment.cs documentation for version 1.0

This script **marries the Unity world and the OptiTrack/Motive world**, on demand, over the lab LAN. On a trigger it fetches one pose of the headset's OptiTrack rigid body from the lab streaming server, and moves/rotates its parent chain (the camera rig) so that the cyclopean eye lands exactly on that pose — which is what licenses `StreamRigidBodyFromOptitrack` to write raw tracker coordinates straight into the scene. It is a deliberately cleaner re-implementation of `universe_relocator.cs` (the Christmas-break original, kept for reference): **same wire protocol, same verified correction math, tamed failure modes** (bounded attempts, a receive timeout, NaN-safe parsing, a loud terminal state instead of "retry forever").

## A brief history of this protocol (read before "improving" anything)

Motive/NatNet 4.5 had a bug where rigid body *headers* did not match rigid body *data* — for a given rigid body name or ID you effectively got someone else's rotation and translation. NaturalPoint supported it, replicated it, called it GUI-related, and declined to fix it for a single client. The lab streaming server (plain-ASCII UDP, `;`-separated) is the angry workaround, and it is now **permanent infrastructure**:

* plain text was deliberate, so the packets are visible directly in Wireshark;
* a **Matlab client speaks the same grammar** — the wire format is a shared contract, and this Unity side must stay byte-compatible with it;
* transfers stay on the local LAN (10GBase-T / 6 GHz WiFi), unicast only (no broadcast/multicast) so several workstations stream without interfering;
* up to 200 Hz in current use, with QoS/flood throttling disabled on the managed switch and router;
* and a hard lab invariant: **this must never be used with the streaming server down.** That invariant is what makes the blocking receive in this script defensible (see "What compromises were made").

So: do not rewrite this against NatNet directly (you would re-inherit the bug), and do not change the grammar (Matlab and Wireshark depend on it).

## How it is used?

* Attach this script to a GameObject **above `CenterEyeAnchor`** — in this scene that is the `[BuildingBlock] Camera Rig` (its child `TrackingSpace` is fine too; NOT `CenterEyeAnchor` itself, NOT `GazeContingentGameObject` — see "Scene needs" below).
* The sockets are **always bound** (a listening socket transmits nothing, so an isolated-AP demo is unaffected) but **no alignment runs automatically**: `requestAlignmentOnStart` is `false` in the template.
  * Check **`Request Alignment on Start`** to have one alignment run requested when the app starts (consumed on the first frame — the original `requestUpdateNow` ergonomics; ticking it in the Inspector mid-session also works as a live manual trigger).
  * A remote trigger is available at any time over the control port (below).
* **`swapYAndZCoordinates`** (default `true` = the lab convention): the lab's OptiTrack/Motive data is **Z-up, Y-depth** (as plotted), while Unity is Y-up, Z-depth — so swap. Labs streaming *standard* OptiTrack (Z-depth, Y-up, already Unity order) turn it `off`. This mirrors the original's two branches, which also decide which axis of the OptiTrack quaternion carries the vertical angle (Z when swapped, Y otherwise).
* **`anchorSampleCount`** (default `3`): the headset-side anchor read is averaged over the last N frames before the correction is applied (the headset's own pose estimate is the noisy half of the equation — see "What compromises were made"). `1` = a single read, the original's behaviour.
* Set `controlPort` / `rigidBodyID` if your lab uses different numbers (defaults: both `5000`).
* Prerequisite in Motive (per the lab procedure): a rigid body whose local origin corresponds to `CenterEyeAnchor` (created in the cradle, axes aligned), stable tracking — default ID here is `5000`.
* From the experimenter software (Matlab / anything):
  * **Plain trigger:** send one packet of anything to the control port (default `5000`) → one alignment run.
  * **Fudge + trigger:** send `<x>,<y>,<z>` (three floats, metres, Y-up) → that delta is **added** to the running fudge (`CoordinateOffsetsInUnitySpace`), then alignment runs.
  * **Query:** send `?` → reply `OK;<STATE>;<fx,fy,fz>;errMm;errDeg;attempts`.

The experimenter software owns the fudge *at initialisation time* (see the "why the fudge is a relative delta" section below); the Unity side only applies and reports it.

## What compromises were made?

* **The pose receive is a blocking `Receive` on the render thread.** It is bounded by `receiveTimeoutMilliseconds` (default 5 s) and by `maxAttempts` (default 3), so the worst case is ~15 s of frozen frames, not infinity — but the *design* relies on the invariant above: this is a deliberate, occasional lab action (cradled-headset boot, or a participant reset), not a per-frame data path. With the server up and the LAN good, a run is a single exchange, well under a second.
* **The fudge stays in memory.** It is *not* persisted across app launches, on purpose: the workflow is "experimenter software initialises the hardware, verifies, and re-sends whatever is needed before the first participant arrives." Persistence would invite a stale value that is hard to trace across sessions and machines. The query reply (`?`) lets the lab read back the current accumulated value at any time.
* **The fudge is a translation only** (metres, Y-up). Orientation is *never* fudged: the yaw-only correction has never shown measurable drift in this lab (see "The verified part"), and the rotation conventions differ between OptiTrack and Unity by more than axis order — an orientation delta you cannot verify by eye is a delta you must not send.
* **Only the vertical axis is corrected, by construction:** the experimental table is at zero height, the tracker is anchored to it, and the floors are parallel — so the general 3D transform between the 'origin' and 'survey' coordinate systems (R. E. Deakin, *3D Coordinate Transforms*, 1999) specialises to **one translation + one rotation about the vertical axis.** Full 3D correction would be over-engineering here, and pitching/rolling the virtual room under a live participant is disqualifying to begin with.
* **The angular gate is wrap-safe but measures the *applied* correction** (|Δyaw| between the two systems), matching the original's semantics: a successful second attempt measures ~0, so the gate really checks "was the first correction already small enough."
* **NaNs are an attempt failure, not a value.** The original script wrote `float.NaN` into the math when a field failed to parse; `NaN > x` is `false`, so a garbage packet could "pass" the gate and leave the world at NaN. Here a parse failure simply fails that attempt.
* **The headset-side read is temporally averaged (default 3 frames), instead of one read as in the original.** The headset's OWN pose estimate (the anchor read out of the rig hierarchy) is noisier than the OptiTrack side when the headset is cradled — observed on device as the origin marker drifting a little, randomly, between otherwise-identical triggers. Averages are taken over *frames* (the pose only updates once per rendered frame, so "read 3 times inside the alignment" would read the same value), with a deliberately small window: nothing guarantees the headset is stationary during an alignment. This targets exactly the noisy input; re-running the whole server exchange N times (the other option) would triple the protocol traffic and average the already-stable OptiTrack side for no gain. `anchorSampleCount = 1` restores the original single-read behaviour.

## The verified part — do not "improve" without re-verifying on the cradle

The OptiTrack rotation **conventions** differ from Unity's (not just the axis order — the quaternion/convention layer itself), and these lines were hand-verified against the lab hardware:

* **position:** Y/Z swap (`(x, y, z)` → `(x, z, y)`) — OptiTrack is Z-up, Unity is Y-up;
* **yaw:** `verticalRotationFromOptitrack = 360 - eulerAngles.z` (the raw quaternion's Z-up yaw, inverted because OptiTrack's vertical rotation runs the other way — verified, *not* a handedness derivation), then the difference is applied as `Euler(0, yawOt, 0) * Inverse(Euler(0, yawHead, 0))`.

If you change either constant and the alignment suddenly misbehaves, you changed a *verified convention*, not a typo. The one *deliberate* deviation from the original is the **gate metric** only: `Mathf.Abs(Mathf.DeltaAngle(yawOt, yawHead))` instead of the raw subtraction, which could misread the 0°/360° boundary as a ~360° error (and let a real 3° correction through as "-357°").

**The *order* of the operations is also verified behaviour** (this bit the first revision of this script on device): the rig is reset to identity rotation **before** the headset yaw is read — the after-reset read is then just the local chain, i.e. the headset's OWN pose, independent of the rig's previous state, so the final rig yaw is the *absolute* correction (yawOt − headsetYaw) and repeat triggers are idempotent. Reading the headset yaw *first* would fold in the rig's previous correction, and every second trigger would flip the world (distances looked fine; the angle oscillated). The position is likewise applied under the rig's *previous* state (the anchor is read before any change is made), exactly as in the original. Both headset-side reads are the same physical, rig-independent quantity, which is also what makes the temporal averaging (above) replace the single samples without changing the formula.

## Why the fudge is a relative delta, and why it exists at all

This is the *why* section for `CoordinateOffsetsInUnitySpace`:

* After any alignment, the experimenter (the primary author, or more likely an undergraduate/colleague who has no game-engine expertise) must **visually verify** the marriage before the first participant. The lab procedure: place a physical object at a known location, stream the position and orientation of the **twin of that object** into the scene (via `StreamRigidBodyFromOptitrack`), and look through the headset's optics (there is a gap between the nose and the headset) comparing the real object and its virtual twin. It is crude and it works very well.
* If the twin looks, say, 1 cm lower than the real thing, the fix is one delta: `0,0.01,0` (Y is up) — added to the fudge — and a re-alignment snaps everything by exactly that amount, twin included (it lives in the aligned world). Because the operator *accumulates* corrections interactively, the payload is **relative**, not an absolute set.
* History backs the shape of the interface: in the old workflow the translation was twiddled either in the Inspector before compile, or by editing the rigid body's local coordinate system in Motive. The **fudge is the live, per-session equivalent of both** — same degrees of freedom, no re-creating the rigid body, readable back with `?`.
* Angles: never a problem in this lab. Translation: always eventually needs twiddling (e.g. after the marker-holder assembly slips relative to the headset — exactly the case the fudge exists to absorb).

## Scene needs (and what must NOT happen)

* Attached to an **ancestor of `CenterEyeAnchor`** whose parent chain is identity — the `[BuildingBlock] Camera Rig` in the current scene (`Assets/Scenes/ARScene.unity`). The script writes only to **its own** `transform.position` / `transform.rotation`; everything between it and the eye anchor (including the gaze-/head-contingent pivots) moves with it, which is intended.
* **Never write to the anchor pivots.** `CenterEyeAnchor` is owned by the rig; `GazeContingentGameObject`'s local position is re-written every frame by `GazeContingentTransform2`. Touching either destroys retino-centric rendering.
* The scene's **`Origin Tester Object`** (scene root, identity transform, child prefab = `Assets/Objects/AxisOrientationTester.obj`) is the visual marker for the Unity/scene origin. It is deliberately **outside** the camera rig, so alignment moves the *user* and leaves the marker (the 'fixed stage') alone. The alignment test: with a good alignment + the right fudge, that marker should appear to sit exactly on the IRL tracker origin; any apparent offset is the residual error to take up with a fudge delta.
* `RecenterProbe.cs` (sibling file, diagnostic, throwaway by design) exists to verify — with evidence — whether *triggering* a recenter works on this project's **Eye Level** tracking origin (it may be a no-op: recentering is documented as a floor-based-origin feature). The *detection* half (`RecenteredPose` event) is SDK 207 API and already wired into `autoRealignOnRecenter`.

## Externally available variables

Public fields, for meddling in the Inspector:

```C#
    public bool requestAlignmentOnStart = false;      // one alignment run on start (consumed once); also a live Inspector trigger
    public bool swapYAndZCoordinates = true;          // lab convention: OptiTrack Z-up / Y-depth -> Unity Y-up / Z-depth
    public int controlPort = 5000;                    // lab -> headset trigger / query (outside the 40000-40007 block)
    public int rigidBodyID = 5000;                    // headset rigid body in Motive
    public string streamerServerAddress = "192.168.42.5";
    public int streamerServerPort = 64923;
    public int dataReturnPort = 54622;                // server -> headset pose stream (base)
    public bool randomizeDataReturnPort = true;       // + random 0-199 per session
    public int decimation = 1;                        // 1 = every sample; we take one and stop
    public Vector3 CoordinateOffsetsInUnitySpace;     // the running fudge (metres, Y-up); deltas over the control port
    public int maxAttempts = 3;
    public float maxCoordinateErrorMetres = 0.005f;
    public float maxAngularErrorDegrees = 0.5f;
    public int receiveTimeoutMilliseconds = 5000;
    public int anchorSampleCount = 3;               // temporal window for the (noisy) headset-side read; 1 = original single read
    public bool autoRealignOnRecenter = true;         // react to RecenteredPose after a successful alignment
    public GameObject centerEyeAnchor;                // optional direct reference; else GameObject.Find
```

The network-facing surface:

| Direction | UDP port | Meaning | Payload |
| --------- | -------- | ------- | ------- |
| PC → headset | `5000` (default) | Plain alignment trigger | anything that isn't `?` or three floats |
| PC → headset | `5000` (default) | Fudge delta + trigger | `<x>,<y>,<z>` — added to the running fudge |
| PC → headset | `5000` (default) | Query state | `?` → `OK;<STATE>;<fx,fy,fz>;errMm;errDeg;attempts` |
| PC → server / server → PC | `64923` / `54622`+ | start / pose / stop (see header comment of the .cs) | the lab grammar |

Note the reply on `5000`: this is a read+set-ish port, so it follows the house rule — **the reply goes out through the same socket that received the request** (5-tuple reverse), never a separate send socket.

## Method of operation

* **Startup (`Start()`, unconditional):** pin the culture to `en-US` (lab convention — the numbers must round-trip), randomise the session's data return port, open the data socket with its `ReceiveTimeout`, open the control socket and start the async `BeginReceive` loop. Listening sockets are harmless on an isolated AP — they transmit nothing.
* **Control callback (socket thread):** `EndReceive` the packet, record payload + sender endpoint under the lock, re-arm `BeginReceive`. It stores strings only — it never touches Unity (house rule).
* **Every frame (`Update()`, main thread):** (0a) one sample of the rig-relative headset-side anchor pose goes into the rolling buffer (the averaging input; one frame = one new headset pose); (0b) if `requestAlignmentOnStart` is set (on start, or ticked in the Inspector) AND the sample window is filled (or the anchor is unresolvable — then run and fail loudly), consume the flag and run the alignment; (1) pick up a pending port request and dispatch — `?` replies immediately; a three-float payload is added to the fudge and the alignment starts; (2) otherwise, check the recenter hook: if the SDK reported a recenter, at least one frame after the successful-alignment state, **and the sample window has refilled with fresh reads**, re-align (that is the "a frame or so later" behaviour — the SDK itself notes poses settle on the next frame).
* **The alignment (`BeginAlignment`), per attempt:** send start → **blocking** receive (bounded) → send stop → parse (5 fields; ID checked) → apply the verified correction (the headset-side position and yaw feeds averaged over `anchorSampleCount` frames, circular mean for the yaw) → gate → retry up to `maxAttempts`. Terminal state is `Success` or `Failed` with a reason — always logged, always queryable.
* **Recenter hook:** `OVRManager.display.RecenteredPose` (SDK 207; driven by the native recenter counter, Quest-standalone) raises a flag and **flushes the sample buffer** (the recenter moved the headset pose — averaging stale reads would pull the correction part-way back); `Update()` acts on the flag once fresh reads have refilled the window, only after a previous `Success`. The event also fires after our own programmatic recenters (the SDK's one-frame latch), which is exactly the probe+realign use case.
* **Teardown (`OnDisable` / `OnApplicationQuit`):** best-effort stop packet to the server, close both sockets, unsubscribe the event. Idempotent.

## Old → new mapping (`universe_relocator.cs` → this)

| Aspect | Original | This |
| --- | --- | --- |
| Trigger port / default | `7510` | **`5000`** |
| Rigid body ID default | `9998` | **`5000`** |
| Fudge | Inspector-only field | Inspector **and** control-port relative deltas + `?` read-back |
| Receive | blocking, **no timeout** (could hang forever) | blocking, `ReceiveTimeout` (5 s) |
| Failure | unbounded retry loop | bounded (`maxAttempts` = 3), terminal `Failed` + reason |
| Parse failure | NaN carried into math; **passed the gate** (`NaN > x == false`) | attempt failure |
| `qw` parse fallback | assigned to `quaternion_qz` (copy-paste) | fixed (and in the async client) |
| Angular gate | raw subtraction (wrap-unsafe) | `Mathf.Abs(Mathf.DeltaAngle(...))` |
| Recenter awareness | none | `RecenteredPose` hook + `RecenterProbe.cs` |
| Template safety | always alive | sockets always bound (listening only, transmits nothing); **no auto-run** (`requestAlignmentOnStart = false`) |
| Y/Z swap | `swapYAndZCoordinates` option (2 branches) | same option, same two branches, now the only branch path (no duplicated code) |
| Math / protocol / ports | — | **byte-for-byte kept** (shared with Matlab + Wireshark); the *order* of the operations mirrors the original (see "The verified part") |

## Expected log markers (grep `[UnityOptitrackAlignment]`)

* `Listening: control port ..., swapY/Z on, align-on-start off, ...` — started; the full configuration is on that line (no auto-run in the template).
* `Alignment requested (on-start / Inspector flag) — running now.` — the auto/manual trigger fired.
* `Attempt 1: sent start request '5000;54687;1'...` — an alignment run started.
* `Attempt N: streaming server did not answer within 5000 ms — retrying.` — server down/late; bounded, then…
* `ALIGNED on attempt N: applied correction X mm / Y deg; fudge (...); headset read averaged over 3 frame(s).` — success; the applied-correction magnitudes are the alignment quality of that run, and the sample count is the window actually used (1 = the buffer was empty / `anchorSampleCount` = 1).
* `Alignment FAILED after N attempt(s): <reason> — the worlds are NOT married; do not collect data` — the loud terminal state.
* `RecenteredPose event received...` — the tracker was reset (by anyone); re-alignment follows if previously aligned.
* `Shutdown: sockets closed...` — clean teardown.
