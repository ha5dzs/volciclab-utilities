# ExternalPassthroughControl.cs documentation for version 1.0

This script is one of the external-control scripts: it lets the computer *outside* the headset switch the headset's passthrough (the AR camera feed) on and off over the local network. In practice this is what lets an experiment flip between **AR** and **VR** on the fly — for example, have the participant sit down and orient themselves with the real room visible, then cut to pure VR for the scotoma part of the trial, or back to AR again for an orientation check. It is deliberately the smallest possible "string-bashing" control, in the same family as the other `External*Control` scripts.

## A brief history of this code

The first version of this was embarrassingly broken, especially since the new `[BuildingBlock]`-types have been introduced. It looked something like:

```C#
    private OVRPassthroughLayer passthroughLayer = new OVRPassthroughLayer();
    ...
    passthroughLayer.enabled = enablePassthrougAtStartup;
```

On a C#-literate day, `new` a component and flip `.enabled` looks perfectly reasonable. But in Unity a component does not exist as a free-floating object. `new OVRPassthroughLayer()` produces a `MonoBehaviour` that is **not attached to any GameObject**: it has no hierarchy, its `OnEnable` / `OnDisable` never run, and it never builds the internal `OVROverlay` that actually gets submitted to the renderer. So a UDP packet would arrive, get parsed, the code would "work" without any complaint — and the screen would not change at all. A perfectly silent no-op.

So this version was rewritten to find the real `OVRPassthroughLayer` that the `[BuildingBlock] Passthrough` has already placed in the scene (see `README.md` - if you are the tl;dr type, then just toss it into the scene and attach this script to that prefab, or your favourite GameObject), and to toggle *that* — the one whose `OnEnable` / `OnDisable` genuinely create and destroy the passthrough overlay.

## How it is used?

* Attach this script to any GameObject. It does not render and does not care where it lives.
* The scene needs a `[BuildingBlock] Passthrough` (README step 13) — that is the `OVRPassthroughLayer` this script talks to.
* From the controlling computer, send **one UDP packet** to the control port:
  * first byte is `0` (ASCII `48`) → passthrough **off** (VR)
  * any other byte → passthrough **on** (AR)

That is the whole interface. There is no handshake and no acknowledgement: fire and forget. The experimenter should monitor what is happening in the VR headset via casting though the Meta Quest Developer Hub (MQDH), so verifying this working should be fairly simple.

## What compromises were made?

* **A component is not a free-floating object.**  `OVRPassthroughLayer` is a `MonoBehaviour`; it only does anything while it belongs to a live GameObject in the scene. Its `OnEnable` builds the internal `OVROverlay` proxy, and its `OnDisable` tears it down. The correct way to hide the passthrough is therefore to **deactivate that GameObject** (`SetActive(false)`), not to `new` our own copy and flip `.enabled` on it. The two lines look nearly identical, and one of them silently does nothing.

* **The toggle must run on the main thread.** The `BeginReceive` callback executes on a socket thread, not Unity's render thread, and mutating a Unity component from there is not allowed. So the callback only records the requested state under a lock, and `Update()` applies it on the main thread a (as in, one) frame later. Probably switching latency here is not an issue, but for the curious, at 90 Hz and at around 4 ms WiFi network latency, the total delay will be around 15 milliseconds. Note that if the frame rate drops in the headset (due to scene complexity, thermal throttling, or just from abuse), this delay will increase significantly. So this is **NOT** for production use in an experiment, think of it as a convenience feature.

* **It is a lenient, single-byte protocol.** We only ever look at the first byte: `48` means off, anything else means on. Trailing newlines, extra spaces, `0x0a` — none of it matters. That matches the lab's "plain ASCII over UDP" KISS principle.

* **The real master switch is owned by the SDK, not by this script.** `OVRPassthroughLayer` only submits its overlay when the underlying flag `OVRManager.instance.isInsightPassthroughEnabled` is true *and* passthrough has actually initialized. This project ships with passthrough on (the README enables it by default), so toggling the building block works here. But if that master flag were turned off, this script's "on" would have no effect: we control the *layer*, the SDK owns the *pipeline*. That is by design, not a bug — but it is worth knowing before someone wonders why "on" did not come up.

## Externally available variables

The public fields, for meddling in the Inspector:

```C#
    // The initial passthrough state applied in Start().
    public bool enablePassthrougAtStartup = true;

    // The UDP port on the headset that listens for control packets.
    public int passthroughControlPort = 40002;
```

(Yes, `enablePassthrougAtStartup` is spelled the way the original had it; the field name was kept stable so any saved Inspector value survives.)

The network-facing surface — what the outside world actually talks to:

| Direction | UDP port | Meaning | Payload |
| --------- | -------- | ------- | ------- |
| PC → headset | `40002` | Toggle passthrough | first byte `0` = off, anything else = on |

See `README.md` § "Network communication protocol" — this is the `40002` row. Note the difference from `40000` / `40001`: those are readable *and* settable, and a `?` gets a normalized answer; this port is write-only, because there is no value to read back.

## Method of operation

The socket plumbing is the same async-UDP pattern used by the `universe_relocator` / `StreamRigidBodyFromOptitrack` scripts (`https://yal.cc/cs-dotnet-asynchronous-udp-example/`).

At startup (`Start()`):

* Pin the culture to `en-US` so decimal formatting does not depend on the host's locale (lab convention).
* `FindAnyObjectByType<OVRPassthroughLayer>(FindObjectsInactive.Include)` to find the scene's real layer. The `Include` matters: it means we still find it — and can switch it back on — even if it started deactivated.
* If we found it, apply `enablePassthrougAtStartup`. If not, log a loud error telling us to add a `[BuildingBlock] Passthrough`, because without it there is nothing to toggle.
* Bind a `UdpClient` to `IPAddress.Any:passthroughControlPort` and start an async `BeginReceive` into the callback, so the render thread is never blocked.

When a packet arrives (callback, off the main thread):

* `EndReceive` the bytes, then immediately re-`BeginReceive` to keep the listener alive (this is the loop that keeps it listening).
* Guard that the packet actually has at least one byte.
* Read the first byte: `48` → off, otherwise → on.
* Store the requested state under the lock. **Do not touch the scene yet.**

Every frame (`Update()`, main thread):

* If a pending request is queued, take it out and clear the flag (both under the lock).
* Call `ApplyPassthroughState`, which — only if the state actually changed — runs `passthroughLayer.gameObject.SetActive(enable)`. That single call is what builds or tears down the passthrough overlay.

On teardown (`OnDestroy()`):

* `Close()` the socket so we do not leave a dangling listener.

What actually makes it show (the "why" behind all of the above): `OVRPassthroughLayer.OnEnable` creates an auxiliary GameObject carrying an `OVROverlay` with `OverlayShape.ReconstructionPassthrough` — *that* is the object the compositor actually renders. `OnDisable` destroys it. So `SetActive(false)` removes the overlay and the real world disappears, leaving whatever VR content is behind it; `SetActive(true)` rebuilds the overlay and the world comes back. That is the entire trick, and it is exactly why we toggle the *GameObject* rather than a detached component.

Expected log markers when something is off (grep `[ExternalPassthroughControl]`):

* `... Listening for passthrough control on port 40002.` — started up, socket live.
* `... No OVRPassthroughLayer found in the scene. Add a [BuildingBlock] Passthrough ...` — the real layer was not found; nothing can be toggled.
* `... Passthrough enabled.` / `... Passthrough disabled.` — a toggle actually took effect.
