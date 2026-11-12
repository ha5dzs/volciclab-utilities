# ExternalScotomaDistanceAdjuster.cs documentation for version 1.0

This is one of the external-control scripts: it lets the computer *outside* the headset rebase the **shared Z-distance** (the "depth" / viewing distance) of the two scotoma anchor GameObjects that are children of `CenterEyeAnchor` — **`GazeContingentGameObject`** (gaze-contingent) and **`HeadContingentGameObject`** (head-contingent) — over the local network. Both are authored at `(0, 0, 0.5)`, i.e. ~50 cm in front of the user. Re-basing their depth in flight is how you change the rendered **binocular disparity**: if a participant reports double vision of the stimulus or the scotoma, moving the anchors closer to or further from the eye is the first thing to try. It is deliberately the smallest possible "string-bashing" control, same family as the other `External*` scripts.

It is a **building block** for larger work: the depth value is the same lab quantity the (future) visual-angle / scotoma-rendering / eye-tracker-calibration math will read, so exposing it as an external knob now keeps those pieces consistent when they are written.

## A brief history of this code — the trap

The obvious implementation — "receive a depth over 40004 and set both GameObjects' `transform.localPosition.z`" — **works for the head object and silently breaks for the gaze object.**

`GazeContingentGameObject` is driven every frame by `GazeContingentTransform2`, which (previously) **cached** its authored offset once in `Awake()` into a private `_initialLocalPosition`, and then re-wrote the object's transform every `Update()`:

```C#
transform.localPosition = calibratedGaze * _initialLocalPosition;
```

So if this script wrote `GazeContingentGameObject.transform.localPosition.z = 0.8`, the eye-tracker would immediately overwrite that transform back to `calibratedGaze * (authored 0.5)` on the next frame. Worse, the object's `localPosition` between frames is a **rotated** position (the gaze already turned it), not a clean `(0, 0, Z)`, so even a `.z` write would not have changed the radius the way you'd expect. A perfectly silent no-op, exactly like the `new OVRPassthroughLayer()` mistake.

The fix was to make that offset a **live, public, externally-writable field** on the core script, and route the depth through it instead of the transform:

* `GazeContingentTransform2` now exposes `public Vector3 scotomaLocalOffset` (still seeded in `Awake` from the authored position, so Inspector authoring is unchanged), and `Update()` sweeps **that live field** around the eye each frame. Because it is live — not a one-shot cache — the radius is always the current value, so a runtime change is honored on the next frame and **never goes stale**.
* So this script writes the shared depth into `gazeContingent.scotomaLocalOffset.z` for the gaze anchor, and into `transform.localPosition.z` for the head anchor (nothing else moves the head object, so the direct write is correct there).

The visible result is exactly what was asked: both anchors jump to the requested depth, a fixed-size test cube stays foveated and keeps facing the observer as you look around, and takes the expected perspective change in apparent size as the distance changes.

## How it is used?

* Attach this script to any GameObject. It does not render and does not care where it lives (the scene puts it on `External Control Scripts`).
* The scene needs the standard anchors under `CenterEyeAnchor`: `GazeContingentGameObject` (carrying `GazeContingentTransform2`) and `HeadContingentGameObject` (bare). See `README.md` steps 28–30 and 40.
* From the controlling computer, send **one UDP packet** to the headset's port **`40004`**:
  * payload is a single plain-ASCII, non-negative decimal in metres (Unity units), e.g. `0.5`, `1`, ` 0.8 ` (leading/trailing whitespace is trimmed; a trailing newline is fine).
  * there is **no handshake and no acknowledgement**: fire and forget (same as `40002`/`40003`), **no reply, no `?` readback**.

That is the whole interface. Monitor the headset via the Meta Quest Developer Hub (MQDH); the log markers below make it clear whether a depth was applied and whether it hit the clamped range.

## What compromises were made?

* **The toggle must run on the main thread.** The `BeginReceive` callback executes on a socket thread, so it only records the requested depth under a lock; `Update()` applies it on the main thread one frame later. At 90 Hz plus ~4 ms WiFi latency this is ~15 ms, so it is a convenience control, not production timing-critical.
* **We drive the *offset field*, not the *transform*, for the gaze object.** Because `GazeContingentTransform2` re-writes that transform every frame, writing the field is the only thing that sticks (see "the trap" above). For the head object the transform *is* the thing, so a direct `.z` write is used.
* **It is a lenient single-value protocol.** One non-negative float, en-US decimals, whitespace-tolerant. Matches the lab's "plain ASCII over UDP" KISS principle.
* **Depth is soft-clamped.** `minDepth` / `maxDepth` (Inspector, defaults `0.0` / `3.0`) guard against a fat-fingered value; if a value is clamped, a `LogWarning` says so. The spec is "0..+ve", so `minDepth` is 0 by default.
* **The head anchor is found by name.** It is a bare GameObject with no distinguishing component, so `GameObject.Find("HeadContingentGameObject")` is used (the gaze anchor is found by its `GazeContingentTransform2` component). `GameObject.Find` cannot see inactive objects, but these anchors are active; if they are ever started inactive, prefer wiring a serialized `Transform` reference instead.
* **The *visual angle* of a fixed-size stimulus is not preserved.** A fixed-size cube naturally changes apparent size as the distance changes (correct perspective). If you instead want to keep the *retinal angle* constant while changing distance, that is a separate (future) concern to the visual renderer (`RetinoStaticAnnulus.cs` warns about exactly this); this script only moves the anchors.

## Externally available variables

The public fields, for meddling in the Inspector:

```C#
    // The UDP port on the headset that listens for control packets.
    public int scotomaDistanceControlPort = 40004;

    // Sanity bounds for the incoming depth, in metres (Unity units). The spec is "0..+ve".
    public float minDepth = 0.0f;
    public float maxDepth = 3.0f;
```

The network-facing surface — what the outside world actually talks to:

| Direction | UDP port | Meaning | Payload |
| --------- | -------- | ------- | ------- |
| PC → headset | `40004` | Set the shared Z-distance (metres); rebase binocular disparity of both anchors | `0.5`, `1`, ` 0.8 ` (single non-negative float) |

See `README.md` § "Network communication protocol" — this is the `40004` row. Write-only / fire-and-forget (like `40002`/`40003`), unlike `40000`/`40001` which also answer a `?`.

## Method of operation

The socket plumbing is the same async-UDP pattern used by the other `External*` scripts (`https://yal.cc/cs-dotnet-asynchronous-udp-example/`).

At startup (`Start()`):

* Pin the culture to `en-US` so decimal parsing/formatting does not depend on the host's locale (lab convention).
* `FindAnyObjectByType<GazeContingentTransform2>(FindObjectsInactive.Include)` to find the gaze anchor's script — the object whose live `scotomaLocalOffset` we will drive. Include inactive ones so it can be switched back on even if it started disabled.
* `GameObject.Find("HeadContingentGameObject")` to find the bare head anchor (no component to search by).
* If either is missing, `LogError` with the README step to add; the depth then applies to whichever *was* found.
* Bind a `UdpClient` to `IPAddress.Any:scotomaDistanceControlPort` and start an async `BeginReceive` into the callback, so the render thread is never blocked.
* Log the port and the initial depth.

When a packet arrives (callback, off the main thread):

* `EndReceive` the bytes, then immediately re-`BeginReceive` to keep the listener alive (this is the loop that keeps it listening).
* Guard that the packet has at least one byte; decode as ASCII and trim.
* Parse one non-negative finite float (en-US; rejects `NaN`/`Infinity`/negatives). On success, store it under the lock (**last one wins**). **Do not touch the scene yet.**
* On parse failure, `LogWarning` with the offending payload and the expected format.

Every frame (`Update()`, main thread):

* If a pending depth is queued, take it out and clear the flag (under the lock), then call `ApplyDepth`, which clamps to `[minDepth, maxDepth]` and writes:
  * `gazeContingent.scotomaLocalOffset.z` (the live resting offset the eye-tracker sweeps), and
  * `headContingent.localPosition.z` (direct write; nothing else moves it — x/y are left as authored).
* Log a `LogInfo` on success, or a `LogWarning` if the value was clamped.

On teardown (`OnDestroy()`):

* `Close()` the socket so we do not leave a dangling listener.

Expected log markers when something is off (grep `[ExternalScotomaDistanceAdjuster]`):

* `... Listening for scotoma-distance control on port 40004. Initial depth = 0.500 m.` — started up, socket live.
* `... No GazeContingentTransform2 found in the scene. ...` / `... No GameObject named 'HeadContingentGameObject' ...` — a target is missing; the depth applies to whatever *was* found.
* `... Scotoma depth set to 0.800 m (gaze + head anchors).` — a depth was applied.
* `... Depth ... m was clamped to ... m (allowed [..., ...]).` — the value hit the guardrail.
* `... Could not parse payload '...'.` — garbage on the wire.
