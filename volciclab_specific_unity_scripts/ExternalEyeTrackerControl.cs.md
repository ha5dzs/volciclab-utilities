# ExternalEyeTrackerControl.cs documentation for version 1.0

This script exposes the eye tracker's two sensitivity thresholds to external control over the local network: `GazeContingentTransform2.EyeTrackingConfidenceThreshold` and `GazeContingentTransform2.EyesAreClosedThreshold`. In practice this is the thing you meddle with *in the middle of a trial* when the gaze-contingent stimulus misbehaves: if the retinal frame is too **jerky**, raise the confidence requirement (more samples get skipped, motion smooths out); if the scotoma **slips while blinking** or lingers when the eyes close, move the eyelid-closed threshold. It is one of the `External*Control` family — same bloodline as `ExternalPassthroughControl` and `ExternalHandRenderingControl` — with one new twist: it is the project's first port that is **both readable and settable**. Send a `?`, and the headset answers with the current pair, so you can confirm which settings are actually in force on the running device instead of trusting memory or an old build.

## A brief history of this code

The first version of this file was a stub — the culture-pinning in `Start()` and an empty `Update()`, with the port `40000` already reserved in the protocol table in `README.md`. The design here follows the same socket plumbing the whole lab standardizes on (`BeginReceive` / `EndReceive` from the [yal.cc sample](https://yal.cc/cs-dotnet-asynchronous-udp-example/), callback records, `Update()` applies — identical to the other two control scripts), so the only genuinely new decision worth writing down is the **reply path**.

A reply must be addressed to *whoever asked* — the IP:port the `?` came from, which is any machine on the LAN, and it is exactly how `PythonUdpBlaster` captures the reply (it reserves its source port before sending, and the headset replies there). First version shipped the reply through a **second, send-only socket**: `sendto()` returned fine, the log line fired, and the packet still never reached the lab PC — not on the wire (Wireshark on the PC saw the request but never the reply). The fix was to send the reply **from the socket that received the query**. The difference is the source port: the second socket made the reply come from a random ephemeral port, so the headset's AP-mode network path — which treats the flow as `PC:<srcport> → headset:40000` and only lets the *reverse* `headset:40000 → PC:<srcport>` through as the reply — classified the ephemeral-source packet as a "new" flow and silently dropped it while `sendto()` still succeeded. Sent from the listening socket, the reply is the exact 5-tuple reverse of the request and goes through. (Thread hygiene is preserved: the reply is still built and sent in `Update()` on the main thread; send and async-receive use separate kernel queues on the socket.)

## How it is used?

* Attach this script to any GameObject. It does not render and does not care where it lives. In the scene, it lives on the `External Control Scripts` GameObject (README step 36), enabled alongside the other two control scripts.
* The scene needs the `GazeContingentTransform2` component (README step 30) — those are the exact two fields this script writes to and reads from. There is no `new GazeContingentTransform2()`: it is a `MonoBehaviour`, found via `FindAnyObjectByType<GazeContingentTransform2>(FindObjectsInactive.Include)`.
* From the controlling computer, send **one UDP packet** to the control port:
  * `?` → query → the headset replies to the sender with `<confidence>,<eyesClosed>` at four decimals, e.g. `0.9000,0.5000`
  * `<confidence>,<eyesClosed>` → set both thresholds, e.g. `0.95,0.43`

That is the whole interface. **Set is fire-and-forget** — no handshake, no acknowledgement, no reply. The `?` is the only thing that gets an answer. To exercise it without writing ad-hoc C#, the lab's `PythonUdpBlaster` is the standardised test:

```
python3 PythonUdpBlaster 192.168.42.125 40000 "0.95,0.43" 0   # set (write side)
python3 PythonUdpBlaster 192.168.42.125 40000 "?" 1          # query -> prints e.g. 0.9500,0.4300, exit 0
```

Note the field order is fixed: `EyeTrackingConfidenceThreshold` **first**, `EyesAreClosedThreshold` **second** — both in the reply and in the set payload.

## What compromises were made?

* **The reply goes out through the same socket that received the query.** See "history" above for the why (source-port identity in the AP-mode flow path): the reply's source port is `40000`, the exact reverse of the request, and that is what gets delivered. One socket, one `OnDestroy()` to close.

* **The mutation still happens on the main thread.** The `BeginReceive` callback executes on a socket-pool thread, and touching a `MonoBehaviour`'s fields from there is not allowed. So the callback only records the request under a lock — last one wins, which is fine at this traffic rate (and the real workflow is "set a known-good pair once, before the session starts, and leave it alone"; the `?` is there for the occasional sanity check). `Update()` takes the recorded request and acts on it a frame later.

* **Parsing is lenient but explicit.** The payload is trimmed of whitespace/newlines; `?` is the query; anything else must split into exactly two halves that parse as **en-US** decimals. The culture is passed *explicitly* to `float.TryParse` rather than relying on the thread's locale — the callback's pool thread may predate the `Start()` culture pinning, and an explicit `CultureInfo` is the airtight way. Garbage gets a loud `Debug.LogWarning` and the state is left untouched — never a half-applied set (e.g. a three-field payload does not apply its first two fields).

* **Values are clamped to \[0, 1\].** Both thresholds are weights, and the `[Range(0f, 1f)]` attribute on the `GazeContingentTransform2` fields is an Inspector-only decoration — a plain field assignment bypasses it. `Mathf.Clamp01` in `ApplyThresholds` is what actually enforces the range for UDP-sourced writes.

* **Missing component = silent degradation, not a crash.** If there is no `GazeContingentTransform2` in the scene, the log says so at startup; sets are warned about and dropped; queries get no answer (the `PythonUdpBlaster` will then report a timeout, which is the correct, honest signal that "there is no eye tracker to query").

## Externally available variables

The public field, for meddling in the Inspector:

```C#
    // The UDP port on the headset that listens for control packets.
    public int eyeTrackerControlPort = 40000;
```

The fields actually controlled — they live on `GazeContingentTransform2`, which is what the packet mutates, not this script:

```C#
    [Range(0f, 1f)]
    public float EyeTrackingConfidenceThreshold = 0.9f;
    [Range(0f, 1f)]
    public float EyesAreClosedThreshold = 0.5f;
```

The network-facing surface — what the outside world actually talks to:

| Direction | UDP port | Meaning | Payload |
| --------- | -------- | ------- | ------- |
| PC → headset | `40000` | Query both thresholds | `?` → reply `<confidence>,<eyesClosed>`, e.g. `0.9000,0.5000` (reply goes to the sender:port) |
| PC → headset | `40000` | Set both thresholds | `<confidence>,<eyesClosed>`, e.g. `0.95,0.43` — no reply |

See `README.md` § "Network communication protocol" — this is the `40000` row.

## Method of operation

The socket plumbing is the same async-UDP pattern used by the other `External*Control` scripts (and the `universe_relocator` / `StreamRigidBodyFromOptitrack` scripts; `https://yal.cc/cs-dotnet-asynchronous-udp-example/`).

At startup (`Start()`):

* Pin the culture to `en-US` so decimal formatting does not depend on the host's locale (lab convention).
* `FindAnyObjectByType<GazeContingentTransform2>(FindObjectsInactive.Include)` to find the scene's real eye tracker — `Include` so we still find (and can drive) it even if it started disabled.
* If not found, log a loud error pointing at README step 30, and keep going: the listener still binds, so the port answers "no eye tracker here" (by not answering at all) rather than never existing.
* Bind a `UdpClient` to `IPAddress.Any:eyeTrackerControlPort` and start an async `BeginReceive` into the callback, so the render thread is never blocked. (This same socket later answers queries too — see "history" for why the reply must come from port `40000`.)

When a packet arrives (callback, off the main thread):

* `EndReceive` the bytes — this also **fills the `IPEndPoint` with the sender's actual IP:port**, which is where the reply must go — then immediately re-`BeginReceive` to keep the listener alive (this is the loop that keeps it listening).
* Decode the payload as ASCII, trim it.
* `?` → record "pending query, answer this endpoint" under the lock.
* `<a>,<b>` that parses under en-US → record "pending set: a, b" under the lock.
* Otherwise log a warning. **Do not touch the scene yet, do not send anything.**

Every frame (`Update()`, main thread):

* If a pending request is queued, take it out and clear the flag (under the lock).
* Query → `ReplyQuery`: read both fields (fresh values, whatever `ApplyThresholds` or the Inspector last made them), format `<confidence>,<eyesClosed>` with the fixed four-decimal `0.0000` pattern under en-US, and `Send` it **from the control-port socket** (source port `40000`) to the stored sender endpoint.
* Set → `ApplyThresholds`: clamp both values to \[0, 1\], write both fields on the `GazeContingentTransform2`, log the resulting pair.

Both thresholds are read and written **atomically as a pair** (one packet → both fields, both fields always in `confidence`-then-`eyesClosed` order), so there is no window in which one is new and the other is stale.

On teardown (`OnDestroy()`):

* `Close()` the socket so we do not leave a dangling listener.

What actually makes the knobs bite (the "why" they matter): `GazeContingentTransform2.Update()` reads `EyeTrackingConfidenceThreshold` as the cutoff on `EyeGazeState.Confidence` for both eyes (below it → the retinal frame is not updated for that sample), and `EyesAreClosedThreshold` as the cutoff on the `EyesClosedL`/`EyesClosedR` face-expression weights (above it → the frame is held, because the eyes are closed). Both are ordinary public fields, so writing them from `Update()` on the main thread takes effect on the very next sample the tracker delivers — which is why adjusting them mid-session changes the stimulus behaviour immediately, with no restart and no re-permission.

Expected log markers when something is off (grep `[ExternalEyeTrackerControl]`):

* `... Listening for control on port 40000.` — started up, socket live.
* `... GazeContingentTransform2 found. Thresholds: confidence=0.9000, eyesClosed=0.5000.` — the component was located (and what it currently holds).
* `... No GazeContingentTransform2 found in the scene. ...` — nothing to control; the `?` will go unanswered while this persists.
* `... Thresholds updated to 0.9500,0.4300.` — a set packet actually took effect.
* `... Replied to 192.168.42.79:<port> with '0.9500,0.4300'.` — a `?` was answered, and this is the byte-exact payload the lab PC should be seeing.
* `... Could not parse payload '...'. Expected '?' or '<confidence>,<eyesClosed>' ...` — a malformed packet was dropped (state untouched).
