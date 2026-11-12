# StreamRigidBodyFromOptitrack.cs documentation for version 1.0

This script is the **continuous rigid-body streamer client** for the lab's custom OptiTrack/Motive streaming server — the twin-object half of the OptiTrack workstream. `UnityOptitrackAlignment` does the one-shot "marry the two worlds" correction on a trigger; this one streams the live per-object pose **every frame** for a rigid body you name. Attach one instance to one GameObject (and hang your visual under it as a child), set `rigidBodyIDYouWantToTrack` to that body's Motive ID, and its 6‑DOF pose follows the real thing.

It speaks the **same plain-ASCII wire protocol** as `UnityOptitrackAlignment` — the lab streaming server is a shared contract: plain text so Wireshark reads it, a Matlab client speaks the same grammar, unicast-only on the local LAN, up to ~200 Hz. Its origin and invariants (the "never run with the streaming server down" rule, and the NatNet‑4.5 header/body bug the plain-text server works around) are in `UnityOptitrackAlignment.cs.md`; this doc points there rather than restating them.

## A brief history of this code (the multi-instance rule)

This is the canonical home for the bug every other sibling doc refers to as "the `static` receive-buffer round-robin," so the story lives here.

The old shape: attach it to a GameObject, toss a mesh on, track something. Attach a second instance for a second rigid body, and the *second* object only updated every second frame, the third every third, and so on. The old file even carried the comment *"I suspect this happens because of Unity's execution model. Not sure, proceed with caution."* That suspicion was wrong. The real cause was one line:

```C#
private static string received_payload_as_string;
```

Every instance had its own socket and its own `BeginReceive` chain, but **all of them wrote into that one shared `static` buffer** (last packet wins), and every instance's `Update()` read the same string and applied it to its own transform — only when the packet's rigid-body ID matched what *that* instance asked for. N interleaved streams into one shared slot: object *k* "sees" its own data only on the frames where its packet happened to be the most recent write. That is the round-robin.

The old workaround — copying the script and renaming the class — "fixed" it only because a distinct class has a distinct home for the `static`. The real fix was one word: drop the `static`. Now `received_payload_as_string` is a per-instance field, and because `receive_message` is an instance method each instance writes to and reads from *its own* buffer. **This is the invariant to keep:** one instance per GameObject, **no static mutable state**, each instance owns its own `UdpClient` and its own listening port.

## How it is used

* Attach to a GameObject to track; parent your visual (marker mesh / fingertip sphere) under it — the script writes only its own `transform`, so children follow.
* Set `rigidBodyIDYouWantToTrack` (the Motive rigid body ID, 1–65535).
* **Multiple instances / 3–4 rigid bodies (the experiment case):** the random-port helper can (rarely) collide at that scale, so the documented workflow is to **turn `useRandomListeningPortForEachInstanceOfThisScript` OFF and assign each instance a distinct `listeningPort`** (e.g. 54622, 54623, 54624) in the Inspector **before** play. Do not rely on the random offset when streaming three or four bodies at once.
* `streamerServerAddress` / `streamerServerPort` (defaults `192.168.42.5` : `64923`) — change only if your lab streams from a different host/port.
* `decimation` — send every Nth sample (the lab throttle); default `3`.
* `swapYandZCoordinates` (default `true` = the lab convention): OptiTrack is Z-up / Y‑depth, Unity is Y-up / Z‑depth, so swap. Labs streaming *already-Unity-ordered* data turn it `off`.
* `useRigidBodyNameAsGameObjectName` — rename the GameObject to the Motive rigid-body name on the first packet (off by default).

## What compromises / gotchas were made

* **The cross-thread read is un-synchronised on purpose.** `receive_message` runs on a pool thread and writes `received_payload_as_string` while `Update()` (main thread) reads it. This is benign: a `string` is immutable and a reference assignment is atomic, so `Update()` sees either the old or the new packet, never a corrupt mix. No lock is needed; do not reach for one.
* **The random listening port is probabilistic, not collision-free.** `rng.Next(listeningPort, listeningPort + 200)` per instance is fine for 1–2 instances, but at 3–4 it is a real (if small) collision risk, and the bind is deliberately **un-caught** (the author wants a collision to fail loudly, not silently). Hence the manual-port workflow above.
* **`OnDestroy`'s stop packet is best-effort and un-caught** (it sends `decimation;0` to tell the server to stop the stream); same "I want this to fail" stance as the start path.
* **The `using` set is legacy slop, kept as-is** so the build stays identical: several (`System.Collections`, `System.Numerics`, `System.Net.Cache`, `System.Net.NetworkInformation`, `System.Reflection`, `System.Collections.Generic`, and the risk-flagged `JetBrains.Annotations`) are unused by the current code. It compiles in this project, so it was left untouched; trim only if you take a dedicated cleanup pass.

## The verified part — do not change without on-device re-verification

The OptiTrack rotation **conventions** differ from Unity's (more than axis order). The `swapYandZCoordinates` branch is the lab's **verified** path:

* **position:** Y/Z swap — `(x, y, z) -> (x, z, y)`.
* **orientation (swapped branch):** invert the x/z/y components of the raw quaternion, then pre-rotate by `Euler(-90, 0, 180)`. The commented "Method 1" in the code (invert → adjust Euler angles → convert back) is the *reference* derivation kept for the "why"; it is not used because the rotations "jump" at the 90° gimbal conditions.
* **orientation (non-swap branch):** invert all rotation components and pre-rotate by `Euler(-90, 180, 0)` — this branch is marked *verify-with-a-game-engine-setup* in the code (not the lab default, lower confidence).

If you change either constant and the tracked object suddenly drifts or spins, you changed a verified convention, not a typo.

## Externally available variables

Public fields, for meddling in the Inspector:

```C#
    public string streamerServerAddress = "192.168.42.5";    // lab streaming server host
    public int streamerServerPort = 64923;                     // server control port (subscribe/stop)
    public int listeningPort = 54622;                          // base for the (per-instance) stream port
    public bool useRandomListeningPortForEachInstanceOfThisScript = true; // + random 0-199 per instance
    public int decimation = 3;                                  // every Nth sample; 0 sends stop
    public bool swapYandZCoordinates = true;                    // lab convention: Z-up -> Unity Y-up
    public int rigidBodyIDYouWantToTrack = 2501;               // Motive rigid body ID (1-65535)
    public bool useRigidBodyNameAsGameObjectName = false;      // rename the GO to the Motive name on first packet
```

## Network-facing surface

| Direction | Port | Meaning | Payload |
| --------- | ---- | ------- | ------- |
| us → server | `64923` | subscribe | `<rigid_body_id>;<listening_port>;<decimation>` |
| server → us | `listening_port` (54622+ by default) | pose stream | `<unix_ms>;<rigid_body_id>;<x,y,z>;<qx,qy,qz,qw>;<name>\n` |
| us → server | `64923` | stop | `<rigid_body_id>;<listening_port>;0` |

These are the **client** ports and are deliberately distinct from the `UnityOptitrackAlignment` control port (`5000`) and the `40000`–`40007` block.

## Method of operation

* **`Start()` (once, on enable in build):** pin culture to `en-US` (the numbers must round-trip); optionally randomise the listening port; bind a private `UdpClient` on it; `Send` the subscribe; arm `BeginReceive(receive_message)`; seed `received_payload_as_string` with a well-formed placeholder so the first `Update()` parse is safe.
* **`receive_message` (pool thread):** `EndReceive` the packet, write it into the instance buffer, re-arm `BeginReceive`. One packet at a time.
* **`Update()` (main thread):** split on `;`; expect exactly 5 fields (else `LogError`); confirm `field[1] == rigidBodyIDYouWantToTrack` (a packet for a different body is simply ignored); parse translation (3) + quaternion (4) with `float.TryParse` (failure → `NaN`); optionally rename the GO once; apply the verified position/rotation.
* **`OnDestroy()` / `OnApplicationQuit()`:** best-effort stop packet (`decimation;0`), un-caught by design.

## Old → new (this file)

| Item | Before | Now |
| --- | --- | --- |
| File / class name | `streamer_client_script_async.cs` / `OptitrackMotiveStreamingClientForUnity` | `StreamRigidBodyFromOptitrack.cs` / `StreamRigidBodyFromOptitrack` |
| Receive buffer | `private static` (N-instance round-robin) | **per-instance** field (the fix) |
| `qw` parse fallback | assigned to `quaternion_qz` (copy-paste) | assigned to `quaternion_qw` |
| Sibling doc | none | `StreamRigidBodyFromOptitrack.cs.md` (this file) |
| Behaviour / protocol / ports | — | **unchanged** |
