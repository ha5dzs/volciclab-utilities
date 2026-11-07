# DataTransmitter.cs documentation

`DataTransmitter` is the rig's **outbound telemetry path**. Every N rendered frames (N = the decimation the lab sets over the network) it packs the headset's live state — head pose, calibrated gaze, eyelid state, and scotoma state — into one compact PascalCase JSON record and sends it to the lab's `UniversalDataSink` over UDP. It is the "data out" half that pairs with the control scripts' "data in", and it is the one script that **sends** (fire-and-forget) rather than replies.

## What it does — two independent UDP channels

**1. Decimation control (we receive; non-blocking).**
Binds to `decimationControlPort` (default **`40008`**) and reads a single unsigned-integer payload:

- `0`      → stop streaming
- `N > 0`  → send one record every `N` rendered frames

It is read inside `Update()` with `controlWordReceiver.Available > 0` guarding the `Receive()`, so the render loop **never blocks** on the network. This is the former port-`1410` control channel, renumbered into the standard `40000`+ block (the next free port after the `40006`/`40007` colour control landed). There is **no reply** on this port — it is a one-way control input, so the 5-tuple reverse rule does not apply to it.

**2. Telemetry (we send; fire-and-forget).**
When decimation fires, it builds one JSON record from the cached live scene references and sends it to `dataSinkIpAddress:dataSinkPort` (default **`192.168.42.5:3431`**, the lab `UniversalDataSink`). No ack, no reply socket. The sink appends its own arrival timestamp and a newline; `DataTransmitter` sends the record **verbatim, with no trailing newline**.

## Attach + pre-requisites

- **Attach to `[BuildingBlock] Camera Rig`** — the root the `CenterEyeAnchor` subtree hangs from. Every object lookup is relative to this object (there is no `GameObject.Find` by absolute scene path — the stale `OVRCameraRig/…/EyeTrackedGameObject/…` path the old code used is gone).
- The standard scene tree must be present: `TrackingSpace/CenterEyeAnchor`, a `GazeContingentGameObject` carrying `GazeContingentTransform2`, and — under it — a `Scotoma` child carrying `VisionStaticAnnulus`. If any of these are missing, `Start()` logs exactly which reference it could not resolve **and** a single "readiness … => READY / NOT READY" line. The script does not crash; it simply omits the fields that depend on whatever is missing.
- **`NtpTimeSync`** (README setup step 48) should be attached to a permanently active object so `TimestampMs` is NTP-corrected. If it is absent or not yet synced, `TimestampMs` falls back to the **raw** system clock (uncorrected) and `DataTransmitter` logs a **one-time** warning on the first record, so an uncorrected clock is not silently mistaken for a good timestamp.

## Timestamp — and the two-clock trap

The in-record timestamp is `NtpTimeSync.UnixTimeNowInMs()` — the lab's NTP-corrected, millisecond-precision UTC clock (the whole reason `NtpTimeSync` landed before this rewrite; the old `unix_time_now_in_ms()` / `DateTimeOffset.UtcNow` path is deleted).

**Important: two different clocks coexist by design, and they are not expected to be equal.**

- `DataTransmitter.TimestampMs` is the **capture time** at the headset (the rendered frame), NTP-corrected.
- The sink's own leading column is the **arrival time** at the recording PC.

The gap between them is network + OS-stack latency (single-digit ms once the clocks are disciplined) — it is the measurement, not an error. (The raw, pre-discipline samples show a steady ≈ +1480 ms offset, which is headset-vs-PC **clock skew**, not latency — see `NtpTimeSync.cs.md` for the clock side of this.)

## Record format

PascalCase keys (human-readable, fixed order), hand-formed string (not `JsonUtility`), culture-invariant decimals (the thread is also pinned to `en-US`). One line per record.

| Key | Source | Unit | Precision |
| --- | --- | --- | --- |
| `TimestampMs` | `NtpTimeSync.UnixTimeNowInMs()` | Unix ms (UTC) | integer |
| `HeadsetPosition` | `CenterEyeAnchor.transform.position` | metres (world) | 3 decimals (mm) |
| `HeadsetRotation` | `CenterEyeAnchor.transform.rotation` | quaternion (world) | 4 decimals |
| `GazeVectorAzimuth` | `GazeContingentTransform2.CurrentGazeVector.x` | degrees | 1 decimal |
| `GazeVectorElevation` | `GazeContingentTransform2.CurrentGazeVector.y` | degrees | 1 decimal |
| `EyelidPosition` | `GazeContingentTransform2.EyelidMean` | 0 = open … 1 = closed | 4 decimals |
| `ScotomaEnabled` | `VisionStaticAnnulus.renderEnabled` | bool | `true`/`false` |
| `ScotomaInnerAngle` | `VisionStaticAnnulus.innerDiameter` | degrees | 1 decimal |
| `ScotomaOuterAngle` | `VisionStaticAnnulus.outerDiameter` | degrees | 1 decimal |
| `ScotomaDistance` | `Scotoma` anchor pivot `.localPosition.magnitude` | metres | 3 decimals (mm) |
| `ScotomaPosition` | `Scotoma.transform.position` | metres (world) | 3 decimals (mm) |
| `ScotomaRotation` | `Scotoma.transform.rotation` | quaternion (world) | 4 decimals |

**Size budget** — the whole record must fit in one Ethernet packet so it arrives unfragmented (MTU 1500 bytes *including* overhead ⇒ keep the payload < ~1400 bytes). The precision caps above keep a typical record well under ~450 bytes, leaving generous headroom. If more fields are added later, keep the total under ~1400 bytes.

**Example record** (shape only — values are illustrative):

```json
{"TimestampMs":1764904800123,"HeadsetPosition":[0.050,0.180,0.320],"HeadsetRotation":[0.7071,0.0000,0.7071,0.0000],"GazeVectorAzimuth":-12.5,"GazeVectorElevation":3.7,"EyelidPosition":0.1204,"ScotomaEnabled":true,"ScotomaInnerAngle":10.0,"ScotomaOuterAngle":40.0,"ScotomaDistance":0.500,"ScotomaPosition":[0.098,0.010,-0.567],"ScotomaRotation":[0.6934,-0.0204,0.7195,0.0141]}
```

## Behaviour + gotchas

- **Decimation adapts mid-stream.** The cadence is `frameCounter % decimation == 0`, so a decimation change takes effect from the very next frame — no reset bookkeeping, `0` stops cleanly, and no divide-by-zero (guarded by `decimation > 0`).
- **A blink is a reading; an invalid frame holds the last value.** `EyelidPosition` comes from `GazeContingentTransform2.EyelidMean`, which is recorded *before* the eyes-closed gate (so a closed eyelid ≈ 1 is captured) but *after* the validity gate — while the gaze sample is invalid the public field simply keeps its previous value. A dropped-tracking streak therefore reads as a flat run, never a spurious jump to 0/NaN. See `GazeContingentTransform2.cs.md` for the gate ordering.
- **`ScotomaDistance` is the live depth.** Read as the scotoma anchor pivot's `.localPosition.magnitude` — the exact `D` that `VisionStaticAnnulus` itself uses for its pose. So the streamed distance is the *actually rendered* distance and picks up `40004` depth changes automatically, with zero coupling.
- **One record per packet, no trailing newline.** The sink owns the line terminator and writes `<sink_ts>,<payload>\n`. `DataTransmitter` transmits only the payload bytes.
- **First record is logged.** The first successful send logs the full record and its byte count — a handy on-device smoke check that the field set, the precision, and the clock are all wired up before trusting a long capture.
- **Non-blocking by construction.** The only `Receive()` is guarded by `Available > 0`, and telemetry is a plain `UdpClient.Send` — the render loop is never held up by the network.

## Ports + protocol house rules

- Control **in**: **UDP `40008`**, a single unsigned integer, `0` = stop, `N` = every N frames. No reply.
- Telemetry **out**: **`192.168.42.5:3431`** (lab `UniversalDataSink`), plain-ASCII JSON, one record per packet.
- The thread pins `en-US` for decimal formatting, and the number helpers use `CultureInfo.InvariantCulture`, so decimal points are always `.`.
- Test the control channel from the lab with the project's tool (note: no reply, so this just *sends*): `python3 PythonUdpBlaster <headset_ip> 40008 "5"` to stream every 5 frames, then `python3 PythonUdpBlaster <headset_ip> 40008 "0"` to stop.

## Verification (on-device, 2026-11-06)

**Complete and verified** on the Quest Pro against the lab `UniversalDataSink` (capture `datasink_test.csv`: 1,338 records over 14.9 s):

- **Every one of the 1,338 lines is valid JSON** with exactly the 12 keys above — zero parse failures, zero key/shape mismatches.
- **Frame rate:** capture deltas (the inner `TimestampMs`) are a steady ~11 ms (median 11.0 ms → **90.9 records/s at decimation 1** on the 90 Hz panel) — the render clock is smooth even where the arrival clock is clustered (see below).
- **Zero packet loss:** the sink received 1,337/1,338 intervals → 89.7 records/s, matching the send rate.
- **End-to-end latency (sink arrival − `TimestampMs`):** **35 ms median, 38.9 ms mean** (min 33 / max 120 ms) over Wi-Fi through two repeater hops; no drift across the capture (first half 38.2 ms, second half 39.7 ms).
- **Clock:** the `NtpTimeSync` offset read at this launch was ≈ **500 ms** (vs ≈ 1.9 s on 2026-11-04) — the headset clock is slow and drifts day to day; the correction is applied either way (see `NtpTimeSync.cs.md`).

**Transport shape (expected of UDP — do not "fix"):** the Wi-Fi link occasionally crowds packets — one observed burst was 7 records landing within 5 sink ms after an 87 ms gap, and 13 of the 1,338 consecutive pairs share one sink millisecond (RTC ms-resolution aliasing). This is a wireless compromise: the headset stays wireless, and the crowding is worst when far from the main AP (more repeater hops); it should ease as the headset gets closer to the AP. The sink's leading column is therefore an **arrival time, ms-resolution, bursty** — it is fine as "when we got it," but any ordering, dedup or differentiation must use the inner `TimestampMs`, which is strictly monotonic across the whole capture (reordering on this link would show up as a non-increasing `TimestampMs` delta — it never did).

## Relations

- Reads `GazeContingentTransform2`'s public surface — `CurrentGazeVector`, `EyelidMean` (see `GazeContingentTransform2.cs.md`).
- Reads `VisionStaticAnnulus`'s public surface — `renderEnabled`, `innerDiameter`, `outerDiameter` (see `VisionStaticAnnulus.cs.md`).
- Timestamps from `NtpTimeSync.UnixTimeNowInMs()` (see `NtpTimeSync.cs.md`).
- Sends to the lab `UniversalDataSink` (outside this repo).
