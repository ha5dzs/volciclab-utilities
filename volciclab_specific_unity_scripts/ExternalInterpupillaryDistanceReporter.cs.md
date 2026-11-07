# ExternalInterpupillaryDistanceReporter.cs documentation for version 1.0

This script reads back the headset's current "user IPD" over the local network, on demand. The Quest Pro's IPD adjustment is **physical** (a manual slider — the lenses are not motorised), and the resulting value is only visible to the person wearing the headset: it is **not visible through Cast / Quest Developer Hub**. In the experiment the participant is expected to set the slider to their own measured IPD; if it is wrong, all disparities and visual angles are wrong, because the planar lenses introduce a lot of distortion on top of the geometry. This script makes the set value checkable from the lab PC, in the middle of a session.

It is one of the `External*` family on the `External Control Scripts` GameObject — same bloodline as `ExternalEyeTrackerControl` and `ExternalScotomaDistanceAdjuster` — with one genuinely new twist: it is the lab's first **read-only** port. The Meta SDK *does* expose a setter (`OVRPlugin.ipd` is a get- and set-property, backed by `ovrp_SetUserIPD()`), but a software write would only change the system's *render model* of the eye separation — it cannot move the optics — so this script deliberately never uses it, and any payload other than `?` is dropped with a warning rather than half-applied.

## Where the "setting" was found (the Meta XR SDK, package cache)

All under `Library/PackageCache/com.meta.xr.sdk.core@3c8a98bf2e9a/Scripts/` (regenerable, do not edit):

* **`OVRPlugin.cs:4369`** — `public static float ipd` (get/set property). The getter calls `OVRP_1_1_0.ovrp_GetUserIPD()` (`:4376`); the native binding is declared at `OVRPlugin.cs:14603` — `public static extern float ovrp_GetUserIPD();`. **This is the value this script reports.**
* **`OVRPlugin.cs:4384`** — the setter's `OVRP_1_1_0.ovrp_SetUserIPD(value)` (extern at `:14606`). Present, **unused and intentionally ignored** (see above).
* **`OVRProfile.cs:58`** — `public float ipd`, a *derived* value: `Vector3.Distance` of the `EyeLeft`/`EyeRight` node poses at the **Render** step. Used here only as a fallback read.
* **`OVRManager.cs:273`** — `public static OVRProfile profile` (the accessor for the above; guaranteed non-null — lazily created).
* **Unit evidence (the value is in metres):**
  * `Util/FovSimulator/FovSimulator.cs:535` — `float ipdMeters = _autoIpd ? OVRPlugin.ipd : _manualIpdMillimeters / 1000f;` (the SDK itself treats `OVRPlugin.ipd` as the *metres* value), and `:161` — the millimetre-facing `Ipd` property is `ResolveIpdMeters() * 1000f`.
  * `Util/OVRDebugInfo.cs:354` — prints `OVRManager.profile.ipd * 1000.0` labelled `"IPD (mm)"`.
* **Permissions: none required.** This value is an OS *user-profile* setting (like `OVRProfile.userName` or `OVRPlugin.systemVolume`), not biometric telemetry, and it is absent from the SDK's whole permission universe — `OVRPermissionsRequester.Permission` only offers `FaceTracking`, `BodyTracking`, `EyeTracking`, `Scene`, `RecordAudio`, `PassthroughCameraAccess` (the biometric ones are what gate the *eye tracker*, and are already requested in the scene).
* **Name-collision watch-out** (do not confuse): `OVRManager.useIPDInPositionTracking` (`OVRManager.cs:1919`, `OVRPlugin.cs:3804`) controls whether IPD affects the *position of the render cameras* — a different feature from the value reported here.

What this number is and is not: it is the **system "user IPD"** — the eye separation the Quest OS uses for rendering, updated when the participant moves the physical IPD slider — not a per-frame physical measurement of the optics. On-device it should track the slider; a one-time sanity check (query, nudge the slider, query again) is worth doing on first set-up with a given headset.

## Behaviour quirk: the value is only available while the headset is worn

Observed on-device (cause unknown and not worth chasing for the protocol): the headset reports a value **only while it is being worn**. Headset off / on the desk → a `?` times out, and that is the correct, expected state (this script stays silent and logs it) — not a fault. Disabling the proximity sensor in Meta Quest Developer Hub does not change this. Practically this costs the experiment nothing, because the IPD is adjusted *while wearing* anyway: check it at set-up with the participant in the headset (that already happens), and if a later `?` suddenly times out, the first thing to suspect is that the headset came off.

## How it is used?

* Attach to any GameObject — it renders nothing and has no scene dependencies of its own (the value comes straight from the Meta XR SDK, so there is no `FindAnyObjectByType` step). In the scene it lives on the `External Control Scripts` GameObject (README step 38), alongside the other control scripts.
* From the controlling computer, send **one UDP packet** to the control port:
  * `?` → query → the headset replies to the sender with `<metres>` at three decimals (1 mm resolution), e.g. `0.063`
  * anything else → dropped with a warning (this port is read-only; there is no set)
* Exercise it without writing ad-hoc code:

```
python3 PythonUdpBlaster 192.168.42.125 40005 "?" 1   # -> prints e.g. 0.063, exit 0
```

A timeout is the deliberate, honest "the headset has no IPD value to give right now" signal — the script stays silent in that case (and logs why) rather than answering a meaningless `0.000`.

## Externally available variables

The public field, for meddling in the Inspector:

```C#
    // The UDP port on the headset that listens for IPD query packets.
    public int ipdReporterPort = 40005;
```

The network-facing surface — what the outside world actually talks to:

| Direction | UDP port | Meaning | Payload |
| --------- | -------- | ------- | ------- |
| PC → headset | `40005` | Query current IPD | `?` → reply `<metres>` at 3 decimals, e.g. `0.063` (reply goes to the sender:port) |

See `README.md` § "Network communication protocol" — this is the `40005` row.

## Method of operation

The socket plumbing is the same async-UDP pattern as the other `External*Control` scripts (`BeginReceive`/`EndReceive` from the [yal.cc sample](https://yal.cc/cs-dotnet-asynchronous-udp-example/), callback records, `Update()` applies).

At startup (`Start()`):

* Pin the culture to `en-US` so decimal formatting does not depend on the host's locale (lab convention).
* Read the current IPD once and log it — so the headset log records what the participant left the slider at *when the session began* (a `Warning` instead if the headset has not reported a value yet).
* Bind a `UdpClient` to `IPAddress.Any:40005` and start an async `BeginReceive` into the callback, so the render thread is never blocked.

When a packet arrives (callback, off the main thread):

* `EndReceive` the bytes — this also **fills the `IPEndPoint` with the sender's actual IP:port**, which is where the reply must go — then immediately re-`BeginReceive` to keep the listener alive.
* Decode the payload as ASCII, trim it.
* `?` → record "pending query, answer this endpoint" under the lock (last one wins).
* Anything else → a loud `Debug.LogWarning` (read-only port); **no state touched, nothing sent**.

Every frame (`Update()`, main thread):

* If a pending query is queued, take it out (under the lock) and `ReplyQuery`: **read the IPD fresh** (so a slider adjustment made earlier in the session is reflected), format `<metres>` with the fixed `0.000` pattern under en-US, and `Send` it **from the 40005 listening socket** (the reply's source port is therefore `40005`, the exact 5-tuple reverse of the request — the AP-mode network-path lesson learned on port 40000; see `ExternalEyeTrackerControl.cs.md` "history").

Read sources, in order:

1. `OVRPlugin.ipd` (only when `OVRPlugin.initialized`) — the system "user IPD".
2. `OVRManager.profile.ipd` — the distance between the render-eye poses.
3. Neither sane (NaN/inf/`0`) → `0f`, which makes the reply path go silent with a warning (never reply `0.000` — that is not a real IPD).

On teardown (`OnDestroy()`): `Close()` the socket.

## What compromises were made?

* **Read-only, by design.** The SDK setter exists but is meaningless here (software cannot move the optics); rejecting non-`?` payloads loudly keeps the protocol honest (a `PythonUdpBlaster` sending a value gets a timeout, and the headset log says why).
* **The reply goes out through the same socket that received the query** — identical to `ExternalEyeTrackerControl` (source-port identity in the AP-mode flow path). One socket, one `OnDestroy()`.
* **The value is read at query time, not cached** — the whole point is checking *the current* slider value, so a stale cache would defeat the purpose.
* **Silence rather than a fake value.** No valid IPD → no reply, warning in the log, `PythonUdpBlaster` times out — the same honest-signal choice the 40000 script makes when no eye tracker exists. (In practice this happens when the headset isn't worn — see above.)
* **3-decimal format (`0.000`) = 1 mm resolution** — matches the README table example and `ExternalScotomaDistanceAdjuster`'s depth formatting. (The 40000 query uses 4 decimals because its values are unitless weights; IPD here is a physical length in metres.)

## Expected log markers (grep `[ExternalInterpupillaryDistanceReporter]`)

* `... Listening for IPD queries on port 40005.` — started up, socket live.
* `... IPD at app start: 0.063 m.` — the participant's slider value at session start.
* `... Replied to 192.168.42.79:<port> with '0.063'.` — a `?` was answered; this is the byte-exact payload the lab PC should be seeing.
* `... Could not parse payload '...'. This port is read-only — send '?' ...` — a non-`?` packet was dropped (state untouched).
* `... No valid IPD available at app start (the headset reports one only while it is being worn); ...` / `... no valid IPD was available (the headset reports one only while it is being worn); staying silent.` — the headset has no value yet; queries will time out while this persists (expected off-device, in the editor, or with the headset off).
