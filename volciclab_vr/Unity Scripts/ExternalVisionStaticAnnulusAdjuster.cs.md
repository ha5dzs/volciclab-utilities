# ExternalVisionStaticAnnulusAdjuster.cs documentation for version 1.0

This script is the external (LAN) control surface for the two `VisionStaticAnnulus` instances in the scene. It owns **two ports** — the two next free after the `40000`–`40005` block — and drives two different annuli with the **same six-field payload**:

* **`40006` → the scotoma** (the instance on `Scotoma`, child of `GazeContingentGameObject`): **gaze-contingent** — its center azimuth/elevation is *retinotopic*, i.e. relative to the fovea (the calibrated gaze), so `(0, 0)` sits on the fovea.
* **`40007` → the calibration stimulus** (the instance on `EyeTrackerCalibrator`, child of `HeadContingentGameObject`): **head-contingent** — its center is *head-centered*, locked to the head frame. This is the target the eye-tracker calibration loop (README § "Scotoma and the calibration stimulus...") measures against — and the loop works *because* it is head-contingent: a gaze-contingent target would be self-referential (it sits where the gaze is by construction), so you could not measure the tracker's error against it.

The six fields, in fixed order: `toggle, azimuth, elevation, inner diameter, outer diameter, colour` — toggle `0` disables / anything else enables (the `40002`/`40003` lenient-toggle convention); the angles and diameters in **degrees of visual angle** (the units `VisionStaticAnnulus` uses); the colour an HTML hex. `?` returns the stored state in the same order.

## A brief history of this code

Port `40006` was reserved in the ninth session as a "future colour port" for the stimulus (see `AGENTS.md` "Declared next tasks" of that era and the ninth-session record). It became this — a six-field control surface for **both** annuli — because the calibration loop needs stimulus control and gaze-control (port `40001`) in a few-packet cycle, and frequent recalibration is the norm: the participant *wears* the headset, the headset moves on the head, and Meta built the eye tracker for character animation, not as a scientific instrument.

Everything socket-ish is copied from the family standard (the `BeginReceive`/`EndReceive` loop from the [yal.cc sample](https://yal.cc/cs-dotnet-asynchronous-udp-example/), callback records under a lock, `Update()` applies, reply from the listening socket — including the 5-tuple-reverse lesson, `ExternalEyeTrackerControl.cs.md` "history"). The one deliberate structural deviation, which makes this the only multi-socket control script in the project:

* **two sockets in one MonoBehaviour, with strictly per-socket pending slots.** The two callback threads interleave arbitrarily; a *shared* "last one wins" slot would let a `40006` packet overwrite a `40007` one between receive and apply — and the reply would leave from the *wrong* socket (wrong source port → dropped on the AP-mode path). That is the same bug family as `StreamRigidBodyFromOptitrack`'s shared `static` receive buffer (the documented round-robin anti-pattern); the per-socket slot is the per-socket generalization of the per-instance rule. **Keep it that way if you ever add a third socket.**

## How it is used?

* Attach this script **once** to the `External Control Scripts` GameObject (like the other control scripts). It does not render and does not care where else it lives.
* The scene needs the two named children with their components (README steps 31/41 and 42/43): `Scotoma` with `VisionStaticAnnulus`, `EyeTrackerCalibrator` with `VisionStaticAnnulus`. If one is missing: a loud error at startup; that port's sets are dropped (warned) and its `?` stays unanswered (the `PythonUdpBlaster` timeout is the honest "the instance is not there" signal — never a fabricated state). The *other* socket keeps working.
* From the controlling computer, one plain-ASCII UDP packet per call:
  * `?` → query — the reply is the stored state, byte-exact form `<0|1>,<az:0.00>,<el:0.00>,<inner:0.00>,<outer:0.00>,#<RRGGBBAA>`, e.g. `1,0.00,-10.00,6.00,10.00,#006400FF`
  * `<toggle>,<az>,<el>,<inner>,<outer>,<colour>` → set all six, fire-and-forget (no ack; verify with `?`)
    Send packets on the *same* port **at least one headset frame apart** (≈ 11 ms at 90 Hz — any human- or script-paced gap, e.g. `PythonUdpBlaster` process launches or the demo's 100 ms settle). Each port holds one pending packet, last one wins: a second same-port packet within the same frame *replaces* the first before `Update()` applies it, so the first is silently dropped (a back-to-back `set → ?` can answer with the pre-set state). Different ports are unaffected; queries are self-synchronizing (the reply arrives after processing).
* To exercise it without writing ad-hoc code:

```
python3 PythonUdpBlaster 192.168.42.125 40006 "1,0,0,1,5,#FF0000FF" 0     # scotoma: on, on the fovea, inner 1 / outer 5 deg, red
python3 PythonUdpBlaster 192.168.42.125 40006 "?" 1                        # -> 1,0.00,0.00,1.00,5.00,#FF0000FF
python3 PythonUdpBlaster 192.168.42.125 40007 "1,0,-10,6,10,#006400FF" 0  # calibration ring: on, 10 deg below, inner 6 / outer 10, dark green
python3 PythonUdpBlaster 192.168.42.125 40007 "?" 1                        # -> 1,0.00,-10.00,6.00,10.00,#006400FF
```

* The whole calibration loop as a single hard-coded, demo-grade script (run from another computer; **not for production**): `eye_tracker_calibration_demo.py` at the repo root — `python3 eye_tracker_calibration_demo.py 192.168.42.125` walks the README's steps and prints every packet.

End-to-end sanity check: set a known state, then query and compare bytes — the round-trip is exact at 8-bit colour and ±0.01° because the reply is the stored field itself.

## Externally available variables

The public fields, for meddling in the Inspector:

```C#
    public int scotomaControlPort = 40006;      // -> the 'Scotoma' annulus (gaze-contingent)
    public int calibratorControlPort = 40007;   // -> the 'EyeTrackerCalibrator' annulus (head-contingent)
```

The fields actually controlled — they live on the two `VisionStaticAnnulus` instances, which is what the packets mutate, not this script:

```C#
    public bool    renderEnabled;    // field 1 (toggle)
    public Vector2 centerAngles;     // fields 2-3 (azimuth, elevation, degrees)
    public float   innerDiameter;    // field 4 (degrees; 0 = filled disk)
    public float   outerDiameter;    // field 5 (degrees)
    public Color   annulusColor;     // field 6 (hex entry point: SetColorHex / TryParseHexColor)
```

The network-facing surface — what the outside world actually talks to:

| Direction | UDP port | Meaning | Payload |
| --------- | -------- | ------- | ------- |
| PC → headset | `40006` | Query the scotoma state | `?` → reply `1,0.00,0.00,1.00,5.00,#FF0000FF` (reply goes to the sender:port) |
| PC → headset | `40006` | Set the scotoma state (all six) | `1,0,0,1,5,#FF0000FF` — no reply |
| PC → headset | `40007` | Query the calibration stimulus state | `?` → same six-field shape |
| PC → headset | `40007` | Set the calibration stimulus state (all six) | `1,0,-10,6,10,#006400FF` — no reply |

Rejections (whole payload discarded + warning logged; nothing applied, nothing sent):

| Rejected because | Example |
| ---------------- | ------- |
| not exactly six comma-separated fields | `1,0,0,1,5` |
| numeric field not an en-US decimal, or `NaN`/`Infinity` | `1,x,0,1,5,#FF0000FF` |
| inner diameter larger than outer diameter (a typo must not silently become a hairline ring) | `1,0,0,10,5,#FF0000FF` |
| colour not HTML hex | `1,0,0,1,5,red` |

## Method of operation

At startup (`Start()`):

* Pin the culture to `en-US` (lab convention), then re-fetch it *explicitly* in every formatting path that may run on a callback (pool) thread — the house pattern, same as the sibling control scripts.
* Find the two instances **by name** (`GameObject.Find("Scotoma")` / `GameObject.Find("EyeTrackerCalibrator")` + `GetComponent<VisionStaticAnnulus>()`). `FindAnyObjectByType<VisionStaticAnnulus>` would return only one of the two (and which one is not guaranteed); name-finding is the approach `ExternalScotomaDistanceAdjuster` already uses for `HeadContingentGameObject`. Missing instance → loud error (README steps in the message); that socket still binds.
* Bind one `UdpClient` per port to `IPAddress.Any`, and start each async `BeginReceive` with `result.AsyncState` = that socket's state block, so the single shared handler knows which socket (and which pending slot) a packet belongs to.

When a packet arrives (callback, off the main thread):

* `EndReceive` the bytes — which fills the `IPEndPoint` with the **sender's** IP:port (where the reply must go) — then immediately re-`BeginReceive` on that socket.
* Decode ASCII, trim. `?` → record "pending query + sender" in *that socket's* pending slot under *its* lock (last one wins, per socket). Six-field payload → validate (field count; en-US finite numerics; `inner ≤ outer`; parseable hex) → record the set in that socket's slot.
* Anything invalid → `Debug.LogWarning` with the port and payload; **no state touched, nothing sent**.

Every frame (`Update()`, main thread): take each socket's pending request under its lock and act — set → write the six public fields on that socket's instance (its `Update()` converges the render next frame); query → format the stored state and `Send` it **from that socket's listener** to the recorded sender (source port = the port → exact 5-tuple reverse).

On teardown (`OnDestroy()`): `Close()` both sockets.

What this deliberately does **not** touch: the anchor pivots (owned by `GazeContingentTransform2` and port `40004`), `renderQueue` (set per-instance in the editor to unique values so the draw order is defined when the two annuli overlap — both are `ZTest Always`), and the material directly (colour goes through `annulusColor`; `VisionStaticAnnulus.ApplyVisualState()` is the single place that stores `C/2` for the `Unlit/Crosshair` ×2 contract).

## What compromises were made?

* **One handler method serves two sockets** (dispatched via `AsyncState`) instead of two near-duplicate handlers. Code sharing was bought with a *hard* per-socket state separation — if a third socket is ever added, it gets its own `AnnulusSocket` block, never a shared pending slot.
* **The reply reports the *stored* fields, not the rendered geometry.** `VisionStaticAnnulus` clamps out-of-range geometry at render time (±89.99° center, `outer ≥ inner`, with a one-shot warning) but keeps the fields. So a set of azimuth `200` will *render* at `89.99°` while `?` answers `200.00` — both facts are logged in the device console; the round-trip `set → ?` is still exact for the stored value. (Contrast: `inner > outer` is rejected *here*, at the network edge, because a transposed pair is a typo and should be obvious; `200°` is not.)
* **The toggle is as lenient as `40002`/`40003`** ("`0`" off, anything else on) — extended from one byte to one field. A non-numeric toggle is therefore *not* rejected: `"x,0,0,1,5,#FF0000FF"` enables. Deliberate — the toggle is the field least likely to carry a typo that matters, and leniency matches the family.
* **The reply is at two decimals, canonical `#RRGGBBAA`** (decided with the lab: two decimals is enough for degrees; the README's payload examples show the same values in shortened form, the same convention as the `40001` example `1.5,-6` standing in for `1.5000,-6.0000`).
* **Alpha is accepted but only verified on-device for `FF` (opaque).** The parser and the ×2 contract both support it (`a` is stored as `a/2`, multiplied back to `a`), and the lab's on-device color verification of 2026-10-28 covered opaque colors — if an experiment relies on `< FF` alpha, verify it on device first.
* **The calibration loop's timing lives on the `40001` side.** The `40001` average window (10 samples) holds *calibrated* samples — the offset is baked in upstream — so after **any** offset change (`0,0`, or the correction) the window must refill (≥ 10 frames ≈ 111 ms at 90 Hz, ≈ 139 ms at 72 Hz) before the next read is a clean average of the *new* offset. Wait well over a full window (≈ 300 ms, or read twice and use the second — which is what `eye_tracker_calibration_demo.py` does). This is documented here because the loop spans both scripts, even though the window itself is `ExternalEyeTrackerAzimuthElevationAdjuster`'s to own.
* **No clamping of the numerics beyond the parse-time checks** (finiteness, `inner ≤ outer`): legitimate stimulus geometry can sit anywhere the participant needs it, and the stimulus's own clamp-and-warn is the second net — the same reasoning, in reverse, as "the offset is a correction by design" in the `40001` script.

## Expected log markers (grep `[ExternalVisionStaticAnnulusAdjuster]`)

* `... Listening on port 40006 for the annulus 'Scotoma'.` / `... port 40007 ... 'EyeTrackerCalibrator'.` — started up, both sockets live.
* `... No VisionStaticAnnulus on the GameObject named 'Scotoma' (README step 31, ...)` — that port is dead until the instance exists (sets dropped, `?` silent).
* `... (port 40006, 'Scotoma') Applied: enabled=1 az=0.00 el=0.00 inner=1.00 outer=5.00 colour=#FF0000FF.` — a set landed; the byte-exact state the next `?` will echo.
* `... (port 40007, 'EyeTrackerCalibrator') Replied to 192.168.42.79:xxxxx with '1,0.00,-10.00,6.00,10.00,#006400FF'.` — a `?` was answered; this is the byte-exact payload the lab PC should be seeing.
* `... (port 40006) Rejected payload '1,0,0,10,5,#FF0000FF': inner diameter 10.00° is larger than the outer diameter 5.00°. ...` — a transposed pair was dropped; the previous state stands.
* `... (port 40007) Could not parse the numeric fields of payload '...' ...` / `... Could not parse colour '...' as HTML hex ...` / `... Expected '?', or six comma-separated fields ...` — other malformed packets, same discard-and-warn.
* `... (port 40006) Query received, but no sender to answer it to; staying silent.` / `... the annulus instance is missing ...; staying silent.` — honest timeouts.
* Out-of-range geometry that was *accepted* is warned by the stimulus itself: `[VisionStaticAnnulus] (Scotoma) centerAngles out of the valid range ... — clamped to ...`
