# ExternalGameObjectControl.cs documentation for version 1.0

This script exposes the **transform of the GameObject it is attached to** — its position in the scene and its orientation — to external control over the local network. In practice it is the deliberate, manual twin of `StreamRigidBodyFromOptitrack`: that script *mirrors* an OptiTrack rigid body's 6-DOF pose onto the object every frame (the object follows the trackers), whereas **here you drive the pose yourself** from the experiment code (e.g. Matlab) — one packet, one pose — and you can also *query* it back. Attach the script to the object whose pose you want and hang the visual you care about (the stimulus mesh, a wall, a spotlight…) under it as a child; the children follow this one transform, so you never have to touch them directly.

It is a **generic, optional utility** — a *template* the forked experiments are expected to adopt only where they need manual pose control. It is **not** one of the core headset protocol ports: the `40000`–`40008` block is fixed and allocated, but this script takes whatever **per-instance port you pick in the editor** (5001, 5002, 5003, …). Because of that it deliberately does *not* follow the deterministic core-block convention, and an implementation is free to not use it at all.

## How it is used?

* **One instance per GameObject, each with its own port.** Attach `ExternalGameObjectControl` to the GameObject whose transform you want to drive. In the Inspector set `controlPort` to a value you choose and **note it down** (e.g. stimulus → `5001`, spotlight → `5002`, wall → `5003`). Put the visual under that GameObject as a child. The default port is `5001` only as a placeholder — **`5000` is already taken by `UnityOptitrackAlignment`**, so a fresh object-control port should start at `5001`.
* There is no "find the target" step and no `FindAnyObjectByType`: this script controls the transform of **the GameObject it lives on** (that is the whole point of attaching it there). It does not render and does not care where it sits, but it MUST sit on the object it is meant to move.
* From the controlling computer, send **one plain-ASCII UDP packet** to that port:
  * `?` → query → the headset replies to the sender with `<x>,<y>,<z>,<rx>,<ry>,<rz>` at four decimals, e.g. `0.5000,0.0000,1.0000,0.0000,30.0000,0.0000`
  * `<x>,<y>,<z>,<rx>,<ry>,<rz>` → set all six (position in metres, orientation as Euler degrees), e.g. `0.5,0,1,0,30,0` — **fire-and-forget, no reply**
* To exercise it without writing ad-hoc C#, the lab's `PythonUdpBlaster` is the standard test:

  ```
  python3 PythonUdpBlaster 192.168.42.125 5001 "?" 1                 # query  -> prints e.g. 0.5000,0.0000,1.0000,0.0000,30.0000,0.0000
  python3 PythonUdpBlaster 192.168.42.125 5001 "0.5,0,1,0,30,0" 0   # set pose (metres + Euler degrees); no reply
  python3 PythonUdpBlaster 192.168.42.125 5002 "0,1,0.5,90,0,0" 1   # a SECOND object on its own port: set, then you can '?' it back
  ```

The field order is fixed: position `<x>,<y>,<z>` **first** (Unity SPACE metres, X right / Y up / Z fwd), then orientation `<rx>,<ry>,<rz>` (Euler, degrees) — in both the `?` reply and the set payload.

### Units, coordinates, and the local/world toggle

* **Position** is in Unity metres. By default it is **world space** (`transform.position`) — the same convention as `StreamRigidBodyFromOptitrack`, and what you want for putting an object at a known place in the scene.
* **`useLocalSpace`** flips the script to **local space** (`transform.localPosition` / `localRotation`), i.e. relative to the parent object. For an **unparented GameObject (a scene-root object) local and world space are identical**, so at the scene root you get the same world coordinates with the flag on or off; the flag only starts to matter once the object sits under a parent that has its own non-identity transform. Keep it `false` unless you specifically want parent-relative movement.
* **Orientation is Euler angles in degrees**, the closest thing to "the angles you type in". Two honest caveats (intentionally **not** handled — this is for simple, mostly single-axis moves, not general rotation algebra):
  * Unity reports `eulerAngles` in `[0, 360)`, so a `set(-30)` reads back as `330.0000` on the next `?`. That is Unity normalising, not a bug.
  * At a pitch of ±90° the yaw/roll are degenerate (gimbal lock). This script is a dumb passthrough and won't warn about it; for the "turn to a set rotation about the vertical" use case it is a healthy, documented trade-off.
* **No conversion happens in the script.** If you are placing objects in an external frame (a Matlab frame, a lab frame, an OptiTrack frame), do the math *there*. This script only says "these six numbers live in Unity's (world or local) space, orientation as degrees".

## Externally available variables

The two public fields you meddle with in the Inspector:

```C#
    // The UDP port THIS instance listens on. Per-instance placeholder, NOT a protocol port.
    // Override per instance and give every instance a DIFFERENT port (5001, 5002, ...).
    public int controlPort = 5001;

    // false (default) = world space (transform.position / rotation).
    // true            = local space (transform.localPosition / localRotation), i.e. relative to the parent.
    public bool useLocalSpace = false;
```

There are no "state" fields to read or set on the script: the thing being controlled is the object's own `transform`, and after a set the pose is exactly what you sent (modulo Unity's `[0,360)` Euler normalisation).

The network-facing surface:

| Direction | UDP port (per instance) | Meaning | Payload |
| --------- | ----------------------- | ------- | ------- |
| PC → headset | `<controlPort>` | Query pose | `?` → reply `<x>,<y>,<z>,<rx>,<ry>,<rz>` (metres + Euler°, 4 decimals), to the sender:port |
| PC → headset | `<controlPort>` | Set pose | `<x>,<y>,<z>,<rx>,<ry>,<rz>` (e.g. `0.5,0,1,0,30,0`) — no reply |

## What design decisions were made?

* **One port per object, assigned by you, and a loud failure if you don't.** Multi-instance is the whole use case (stimulus, spotlight, wall all driven at once), so two instances sharing a port must be caught, not silently broken. `Start()` wraps the socket bind in a `try/catch`; if the port is already owned by another live instance the bind throws, the script logs a clear `…DUPLICATE PORT…` error, and that instance **degrades cleanly** (it drops sets and stays silent to `?`) instead of throwing and killing the app. The *other* instances keep working.
* **The reply goes out through the same socket that received the query** (source port = the control port = the exact 5-tuple reverse of the request), which is what the headset's AP-mode network path demands — see `ExternalEyeTrackerControl.cs.md` for the full "learned the hard way" story. One socket per instance, one `OnDestroy()` to close.
* **The mutation is still on the main thread.** The `BeginReceive` callback runs on a socket-pool thread and must not touch the scene, so it only records the request under a lock (last one wins — fine at this traffic rate). `Update()` takes the recorded request and applies the pose / answers the query a frame later.
* **The set is atomic — all six at once.** One packet → all six fields (position + orientation), in fixed order. There is deliberately *no* "position only" or "orientation only" packet: to move only the position, `?` first, read the current rotation, and send the new position with that rotation. This keeps the protocol trivially simple and always leaves the object in a fully-specified pose (never half-written).
* **Parsing is strict.** The payload must split into **exactly six** fields that parse as **en-US** decimals, all **finite** (a `NaN`/`Infinity` pose is rejected). Any other shape — wrong field count, a non-number, a non-finite value — is discarded **whole** with a `Debug.LogWarning`; state is never half-applied.
* **No clamping** of the position or angles: an object may legitimately be placed anywhere / anywhere-oriented in the scene; the only guard is finiteness.

## Method of operation

The socket plumbing is the same async-UDP pattern used by the `External*Control` family (https://yal.cc/cs-dotnet-asynchronous-udp-example/).

At startup (`Start()`):

* Pin the culture to `en-US` so decimal formatting does not depend on the host's locale (lab convention).
* Bind a `UdpClient` to `IPAddress.Any:controlPort` and start an async `BeginReceive` into the callback — the render thread is never blocked. (This same socket later answers queries too, so the reply leaves from the control port.)
* If the bind **fails** (duplicate port), log a clear `…DUPLICATE PORT…` error, set `listener = null`, and `return` — the object keeps running, this instance just does not answer its port until the port is freed and the app is relaunched.

When a packet arrives (callback, off the main thread):

* `EndReceive` the bytes — which **also fills the `IPEndPoint` with the sender's actual IP:port** (where the reply must go) — then immediately re-`BeginReceive` to keep the listener alive.
* Decode the payload as ASCII, trim it.
* `?` → record "pending query, answer this endpoint" under the lock.
* A 6-field en-US payload that is all-finite → record "pending set: x,y,z,rx,ry,rz" under the lock.
* Otherwise → `Debug.LogWarning` and discard. **Do not touch the scene yet, do not send anything.**

Every frame (`Update()`, main thread):

* If the socket bound, take the pending request out (under the lock) and clear the flag. (If the socket did **not** bind, return immediately — nothing is pending and there is no reply source.)
* Query → `ReplyQuery`: read the live `transform.position`/`eulerAngles` (or the `local*` pair if `useLocalSpace`), format `<x>,<y>,<z>,<rx>,<ry>,<rz>` with four decimals under en-US, and `Send` it **from the control-port socket** to the stored sender endpoint.
* Set → `ApplyPose`: write `transform.position`/`rotation` (or the `local*` pair) from the six numbers, then log the applied pose.

On teardown (`OnDestroy()`): `Close()` the socket so no dangling listener is left behind.

## Expected log markers (grep `[ExternalGameObjectControl]`)

* `On object '<name>': listening on port 5001 (world-space position, Euler degrees).` — started up, socket live, this is the object you'll be driving.
* `On object '<name>': could not bind UDP port … DUPLICATE PORT …` — two instances are fighting over a port; fix the other one's `controlPort`.
* `On object '<name>': applied pos=(…) rot=(…).` — a set packet actually took effect, and the exact pose you'd expect.
* `On object '<name>': replied to 192.168.42.79:<port> with '…'.` — a `?` was answered; this is the byte-exact payload the lab PC should be seeing.
* `On object '<name>': could not parse payload '…' …` — a malformed packet was dropped (state untouched).
