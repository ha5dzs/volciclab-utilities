# ExternalEyeTrackerAzimuthElevationAdjuster.cs documentation for version 1.0

This script is the read/write half of the calibration loop, over the local network. It **reads** `GazeContingentTransform2.CurrentGazeVector` — as a **running average of its last `windowFrames` samples** (default 10, ≈ 111 ms at 90 Hz) rather than as one raw frame, because a single frame of the Quest Pro eye tracker carries microsaccades plus sensor noise and is a poor estimate of "where the participant actually looks" while holding fixation. And it **sets** `GazeContingentTransform2.GazeVectorOffset` — the per-trial calibration offset (azimuth, elevation, in degrees, same convention as the gaze: +azimuth = right, +elevation = up).

The intended loop (the full-set calibration methodology is a later concern — this script is only the plumbing):

1. present a stimulus at (Az;El);
2. ask the participant to look at it;
3. send `?` — the headset answers with the averaged gaze;
4. compute the difference and send it back here as the offset;
5. send `?` again — the answer should now match the stimulus.

It additionally has a **log-only guard**: while the (calibrated) gaze sustains outside a "reasonable" box (default |azimuth| ≤ 40°, |elevation| ≤ 20°), it logs one warning on entering and one on leaving that state. The rationale for the numbers: the human visual field in this convention is about ±80°/±30°, the headset's rendered field is about ±53°/±48°, and in practice (glasses, or the planar lenses further from the eyes) the *usable* field is substantially smaller still. A participant holding fixation on a stimulus should land comfortably inside ±40°/±20° — a sustained read outside that box means the calibration is suspect (offset never applied, miscalibration, or the tracker lost lock), and the averages being reported to the lab PC are suspect too.

## A brief history of this code

The first version of this file was a stub with the port `40001` already reserved in the README's port table. Everything socket-ish is copied from the family standard (`BeginReceive`/`EndReceive` loop from the [yal.cc sample](https://yal.cc/cs-dotnet-asynchronous-udp-example/), callback records under a lock, `Update()` applies, reply from the listening socket) — for the *why* of that last one, see "history" in `ExternalEyeTrackerControl.cs.md` (the 5-tuple-reverse lesson). The two genuinely new parts here are:

* **the rolling sample window** (a `Queue<Vector2>` deque), which is what turns the read into an *average* instead of an instantaneous value;
* **the out-of-range log guard** (enter/exit logging, never per-frame).

## How it is used?

* Attach this script to any GameObject. It does not render and does not care where it lives. In the scene it lives on the `External Control Scripts` GameObject (README step 39), enabled alongside the other control scripts.
* The scene needs the `GazeContingentTransform2` component (README step 30) — this script only reads its `CurrentGazeVector` and writes through its `ApplyGazeOffset(...)`. There is no `new GazeContingentTransform2()`.
* From the controlling computer, send **one UDP packet** to the control port:
  * `?` → query → the headset replies to the sender with `<azimuth>,<elevation>` at four decimals — the mean of the last 10 samples (default), e.g. `2.3450,-1.0025`
  * `<az>,<el>` → set the per-trial offset, e.g. `0.5,-0.1` — fire-and-forget, no acknowledgement
* To exercise it without writing ad-hoc code:

```
python3 PythonUdpBlaster 192.168.42.125 40001 "?" 1           # query -> prints e.g. 2.3450,-1.0025, exit 0
python3 PythonUdpBlaster 192.168.42.125 40001 "0.5,-0.1" 0    # set offset (write side; no reply expected)
```

End-to-end sanity check: with the participant still fixating the same point, set an offset of `0.5,-0.1`; the next `?` answer should shift by ≈ `+0.5 / −0.1` — because the answer is the *calibrated* gaze (offset already added), a 1:1 shift is exactly what "the calibration bit" should look like.

## Externally available variables

The public fields, for meddling in the Inspector:

```C#
    public int eyeTrackerAzimuthElevationPort = 40001;
    public int windowFrames = 10;            // samples to average (values < 1 are treated as 1)
    public float maxAbsAzimuth = 40f;        // degrees, log-only out-of-range guard
    public float maxAbsElevation = 20f;      // degrees, log-only out-of-range guard
```

The fields actually controlled — they live on `GazeContingentTransform2`, which is what the packets mutate, not this script:

```C#
    public Vector2 CurrentGazeVector;   // read (and averaged) on query
    public Vector2 GazeVectorOffset;    // written via ApplyGazeOffset(...) on set
```

The network-facing surface — what the outside world actually talks to:

| Direction | UDP port | Meaning | Payload |
| --------- | -------- | ------- | ------- |
| PC → headset | `40001` | Query averaged gaze | `?` → reply `<azimuth>,<elevation>`, e.g. `2.3450,-1.0025` (reply goes to the sender:port) |
| PC → headset | `40001` | Set per-trial offset | `<azimuth>,<elevation>`, e.g. `0.5,-0.1` — no reply |

See `README.md` § "Network communication protocol" — this is the `40001` row.

## Method of operation

The socket plumbing is the same async-UDP pattern used by the other `External*Control` scripts.

At startup (`Start()`):

* Pin the culture to `en-US` so decimal formatting does not depend on the host's locale (lab convention).
* `FindAnyObjectByType<GazeContingentTransform2>(FindObjectsInactive.Include)` to find the scene's real eye tracker — `Include` so it is still found (and can be driven) even if it started disabled. If not found: a loud error pointing at README step 30, and the listener STILL binds — sets then warn and are dropped, queries stay unanswered (the `PythonUdpBlaster` timeout is the honest "there is no eye tracker to query" signal, never a fake `0.0000,0.0000`).
* Bind a `UdpClient` to `IPAddress.Any:40001` and start an async `BeginReceive` into the callback, so the render thread is never blocked. (This same socket answers queries too — the source-port lesson from 40000.)

When a packet arrives (callback, off the main thread):

* `EndReceive` the bytes — this also **fills the `IPEndPoint` with the sender's actual IP:port**, which is where the reply must go — then immediately re-`BeginReceive` to keep the listener alive.
* Decode the payload as ASCII, trim it.
* `?` → record "pending query, answer this endpoint" under the lock (last one wins).
* `<az>,<el>` that splits into exactly two **en-US** decimals (culture passed *explicitly* to `float.TryParse`, since the callback's pool thread may predate the culture pinning) and are both finite (`NaN`/`Infinity` are rejected — a non-finite offset would poison the gaze for the rest of the trial) → record "pending set: az, el" under the lock.
* Anything else → a loud `Debug.LogWarning`. **No state touched, nothing sent** — a three-field packet never applies its first two.

Every frame (`Update()`, main thread):

1. **Sample**: enqueue the current `CurrentGazeVector`, dequeue the oldest while the queue holds more than `windowFrames` samples, and run the out-of-range guard on the sample (log-only — see below).
2. **Apply** the pending request (taken out under the lock):
   * **Set** → `eyeTracker.ApplyGazeOffset(new Vector2(az, el))`. Deliberately *no clamp*: the offset is a correction by design and can legitimately sit outside any "reasonable" box — a silent clamp would break the calibration loop. The helper logs the applied pair on its side (`[GazeContingentTransform2] Applied gaze offset: ...`).
   * **Query** → if the window is full: mean of the full window, axis by axis, formatted `0.0000,0.0000` under en-US, `Send` it **from the 40001 listening socket** to the stored sender endpoint (reply's source port 40001 = exact 5-tuple reverse of the request). If the window is not full yet: stay silent + warning (honest timeout). If there is no eye tracker: stay silent + warning.

On teardown (`OnDestroy()`): `Close()` the socket.

## What compromises were made?

* **The average is over samples, not wall-clock time.** "Last 10 samples" is ≈ 111 ms at 90 Hz but 222 ms at 45 Hz. Accepted explicitly: the headset renders at a fixed display rate and *sample count* is the meaningful unit here, so the window is defined in frames, not milliseconds.
* **Stale samples can duplicate in the window.** `GazeContingentTransform2` only overwrites `CurrentGazeVector` when *all* of its gates pass (both eyes valid, confidence above threshold, face expressions valid, eyes open); otherwise the field keeps its last valid value. So across a blink or a tracking dropout, the window fills with *repeats* of that last value. That is acceptable for the intended use (fixated reads, both eyes open) and is documented rather than papered over — the obvious "skip a sample if the value did not change" dedupe was rejected as brittle (a genuinely stationary gaze can legitimately repeat bit-for-bit).
* **The newest sample is at most one frame stale.** Two MonoBehaviours' `Update()` calls on different GameObjects have no guaranteed relative order, so the freshest sample in the window is this frame's or last frame's value — ≤ 11 ms at 90 Hz, negligible against a ≈ 111 ms window.
* **Silent while the window fills in** (the first ≈ 10 frames after startup). A sub-full average would be a different animal, and would also still contain the field's `(0,0)` seed from before the first valid tracker sample. `PythonUdpBlaster` simply times out during that window — the honest "not ready yet" signal. In practice the first query lands seconds after startup, long after the window is full.
* **The out-of-range guard is log-only, once per excursion.** One warning on entering, one on leaving — never per frame (at 90 Hz that would be thousands of lines for one bad state). The bounds deliberately do *not* touch the data, the average, or the reply; they are the reason the *set* path also never clamps (see above). The two are the same design choice stated from each side: a legitimate correction may cross the lines; only the *reading* side needs a human-sized sanity check.
* **The reply reports the *calibrated* gaze** (raw + offset — what `GazeContingentTransform2` stores and what the data-collection scripts stream out), not the raw tracker number. That is what step 5 of the calibration loop needs ("set the offset, read back, it should match the stimulus"), and it means an unchanged fixation shifts 1:1 with the offset you set.
* **The reply goes out through the same socket that received the query** — the 40000 lesson (source-port identity in the headset's AP-mode flow path). One socket, one `OnDestroy()`.

## Expected log markers (grep `[ExternalEyeTrackerAzimuthElevationAdjuster]`)

* `... GazeContingentTransform2 found. Current gaze: 0.0000,0.0000, offset: 0.0000,0.0000.` — the component was located (and what it holds at startup).
* `... No GazeContingentTransform2 found in the scene. ...` — nothing to read or set; `?` will stay unanswered while this persists.
* `... Listening for gaze control on port 40001 (window = 10 samples, reasonable range = ±40° az / ±20° el).` — started up, socket live.
* `... Gaze 55.1234,3.0000 is outside the reasonable range (±40° azimuth, ±20° elevation). ...` — entered the out-of-range state (calibration suspect / tracker dropped); averaged replies are suspect while it persists.
* `... Gaze 12.0000,-2.0000 is back inside the reasonable range.` — left it again.
* `... Replied to 192.168.42.79:<port> with '2.3450,-1.0025' (mean of 10 samples).` — a `?` was answered; this is the byte-exact payload the lab PC should be seeing.
* `... Query received, but the average window is not full yet (3/10 samples); staying silent.` — queried too early (first ~10 frames); the timeout is expected.
* `... Asked to set the gaze offset to <az>,<el>, but no GazeContingentTransform2 was found; ignoring.` — a set was dropped (no eye tracker in the scene).
* `... Could not parse payload '...'. Expected '?' or '<azimuth>,<elevation>' ...` — a malformed packet was dropped (state untouched).
* A successful set is additionally confirmed by the `[GazeContingentTransform2] Applied gaze offset: [...]` line, logged by the component it acts on.
