# ExternalHandRenderingControl.cs documentation for version 1.0

This script toggles the *visual rendering* of the built-in hand models (the `[BuildingBlock] Hand Tracking` left/right blocks) over the local network, while leaving the underlying hand tracking **running**. In practice this is what lets an experiment swap out the built-in hand for the lab's own, more precise fingertip render: hide Meta's hand model so it does not double up with the streamed fingertip spheres, and flip the two back and forth on the fly. It is deliberately the smallest possible control, in the same family as `ExternalPassthroughControl` and the other `External*Control` scripts.

## A brief history of this code

The obvious first attempt is to just hide the hand mesh: `SkinnedMeshRenderer.enabled = false`. But the hand visual is not a plain mesh you toggle by hand. Each `[BuildingBlock] Hand Tracking` GameObject carries an `OVRMeshRenderer` component that, in the scene's `ConfidenceBehavior.ToggleRenderer` mode, writes `SkinnedMeshRenderer.enabled = (hand data valid AND high confidence)` on **every** `Update`. So the moment you hide the `SkinnedMeshRenderer`, the SDK flips it back on the next frame. A perfectly polite, perfectly predictable fight you lose every time.

The other obvious attempt is to `SetActive(false)` the whole hand-block GameObject — which is exactly what you *do* do for the passthrough. But that would also deactivate the `OVRHand` component, which is the *data* engine (pinch, hand pose, finger state). We want the hand *invisible* but the hand *tracked*, so the rest of the experiment — gestures, and a future `DataTransmitter` that streams the tracker's data — keeps working.

So the correct switch is to stop the *visual driver*, hide the *pixels*, and leave the *data* alone.

## How it is used?

- Attach this to any GameObject. It does not render and does not care where it lives.
- The scene needs the `[BuildingBlock] Hand Tracking` blocks (one per hand) — that is where the `OVRMeshRenderer` / `SkinnedMeshRenderer` you are toggling come from.
- From the controlling computer, send **one UDP packet** to the control port:
  - first byte is `0` (ASCII `48`) → hand rendering **off**
  - any other byte → hand rendering **on**

Fire and forget: no handshake, no acknowledgement.

## What compromises were made?

* **Visual vs. data is the whole game here.** The hand is three things on one GameObject: `OVRHand` (the *data* — pinch, pose, fingers), `OVRSkeleton` + `OVRMesh` (the bones + the mesh), and the `OVRMeshRenderer` + `SkinnedMeshRenderer` (the *pixels*). We touch only the last two. `OVRHand` and friends keep running, so `GetFingerIsPinching`, hand pose, and everything the rest of the app reads stay alive whether the hand is shown or not. This is the "does not interfere with anything else" guarantee — including the streamed fingertip objects, which are separate GameObjects with their own stock renderers.

* **You must stop the driver, not just hide the mesh.** `OVRMeshRenderer` re-asserts `SkinnedMeshRenderer.enabled` from the tracking confidence every frame. Disabling the `OVRMeshRenderer` first stops it re-showing the mesh; *then* we hide the `SkinnedMeshRenderer`. Hiding the pixels alone loses to the driver. This is the exact "interference" that would otherwise surface.

* **Turning "on" respects tracking.** When you re-enable, the `OVRMeshRenderer` resumes its confidence logic, so a hand only reappears when the tracker actually has valid, high-confidence data. There is no "ghost hand" frozen in its last-seen pose — that is a feature, not a bug.

* **One hand per `OVRMeshRenderer`.** We find them by type (`FindObjectsByType<OVRMeshRenderer>`), so this handles both hands and scales if another one is added. And we never `new` a component — a `new`-ed `MonoBehaviour` has no GameObject and does nothing.

## Externally available variables

The public fields, for meddling in the Inspector:

```C#
    // Whether the hands start visible or hidden.
    public bool enableHandsAtStartup = true;

    // The UDP port on the headset that listens for control packets.
    public int handRenderingControlPort = 40003;
```

(Yes, `enableHandsAtStartup` is spelled the way the original had it; the field name was kept stable so any saved Inspector value survives.)

The network-facing surface — what the outside world actually talks to:

| Direction | UDP port | Meaning | Payload |
| --------- | -------- | ------- | ------- |
| PC → headset | `40003` | Toggle hand rendering | first byte `0` = off, anything else = on |

See `README.md` § "Network communication protocol" — this is the `40003` row. Like the passthrough port, it is **write-only**: there is no `?` and nothing to read back.

## Method of operation

The socket plumbing is the same async-UDP pattern used by the `universe_relocator` / `StreamRigidBodyFromOptitrack` scripts (`https://yal.cc/cs-dotnet-asynchronous-udp-example/`).

At startup (`Start()`):

* Pin the culture to `en-US` so decimal formatting does not depend on the host's locale (lab convention).
* `FindObjectsByType<OVRMeshRenderer>(FindObjectsInactive.Include)` to find the hand visuals — `Include` so we still find and can switch on a hand that started disabled. Grab each one's `SkinnedMeshRenderer`.
* If none are found, log a loud error telling us to add a `[BuildingBlock] Hand Tracking`. If some are, apply `enableHandsAtStartup`.
* Bind a `UdpClient` to `IPAddress.Any:handRenderingControlPort` and start an async `BeginReceive` into the callback, so the render thread is never blocked.

When a packet arrives (callback, off the main thread):

* `EndReceive` the bytes, then immediately re-`BeginReceive` to keep the listener alive (this is the loop that keeps it listening).
* Guard that the packet actually has at least one byte.
* Read the first byte: `48` → off, otherwise → on.
* Store the requested state under the lock. **Do not touch the scene yet.**

Every frame (`Update()`, main thread):

* If a pending request is queued, take it out and clear the flag (both under the lock).
* Call `ApplyHandsState`, which per hand sets `OVRMeshRenderer.enabled` (the driver) and then `SkinnedMeshRenderer.enabled` (the pixels). `OVRHand` / `OVRSkeleton` / `OVRMesh` are left alone.

On teardown (`OnDestroy()`):

* `Close()` the socket so we do not leave a dangling listener.

What actually makes the hand show (the "why" behind all of the above): `OVRHand` produces the hand data and exposes it via `OVRMeshRenderer.IOVRMeshRendererDataProvider`; `OVRMeshRenderer` combines that with `OVRMesh` (the mesh) and `OVRSkeleton` (the bones) and drives the `SkinnedMeshRenderer` — its mesh, its bones, and its visibility. We toggle the `OVRMeshRenderer` and the `SkinnedMeshRenderer`, which is the render side of that chain, and leave the data side (`OVRHand`) running. That is the entire trick, and it is exactly why we toggle the *components* rather than the *GameObject*.

Expected log markers when something is off (grep `[ExternalHandRenderingControl]`):

* `... Listening for hand-rendering control on port 40003 (N hand visual(s) found).` — started up, socket live, hand count (should be 2).
* `... Hand rendering enabled. (hand tracking kept running).` / `... disabled. ...` — a toggle actually took effect.
* `... No OVRMeshRenderer (hand visual) found in the scene. ...` — the hand blocks were not found; there is nothing to toggle.
