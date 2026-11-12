# CrankFrameRateUp.cs documentation for version 2.0

This script asks the headset, at startup, for a chosen display refresh rate (default **90 Hz**) and requests it from the runtime — then **confirms by read-back** that it actually happened, and afterwards **keeps watching** for the runtime silently changing it back (thermal throttling).

## Why it exists

The Quest Pro ships at **72 Hz**. Per Meta's current table (doc: *Set Display Refresh Rates*), the Pro panel offers **72 / 80 / 90 Hz — 90 Hz is its maximum**. (There is *no* 120 Hz mode on the Pro; that is a Quest 2 / 3 / 3S thing. And the "240 Hz" mode is a **Quest 3-only** extended-rate feature that requires Horizon OS 2.7+ *and* an adb display-scaling property — not something a Pro lab can or should chase.)

For participant sessions (≈1 hour) we want the best rate the device offers. The lab's power policy: **request 90 Hz; if battery life turns out to be insufficient, that is a rebuild decision** (set the `refreshRate` field lower, or 0 = device maximum), not a runtime knob.

It exists as its own script — rather than two `OVRPlugin` calls buried somewhere else — purely so a future reader can find, understand, and fix the refresh-rate handling in one known place. The script is small on purpose.

## How it is used?

* Attach to the **`[BuildingBlock] Camera Rig`** (README step 47) — or any always-active GameObject; it renders nothing and has no scene dependencies beyond the HMD itself.
* That is the whole interface. No UDP port, no payload, no acknowledgement. It runs once at startup, then stands down (except the throttling watch, see below).

**Verification.** All status markers in this script are at **`Debug.LogWarning` level on purpose** (see "Compromises" for why), so they show up under *any* log-level setting in Meta Quest Developer Hub. In the MQDH log (filter on `[CrankFrameRateUp]`) you should see, in order:

* `[CrankFrameRateUp] refreshRate 90Hz requested (was 72Hz). Offered by device: 72, 80, 90.`
* `[CrankFrameRateUp] confirmed: now running at 90Hz.`

(_On-device verified 2026-11-04: both lines appeared on the Quest Pro and the rate held._)

An **independent** check that does not depend on the script's logs at all (from Meta's own doc): in the MQDH, the Metrics Performance HUD with **Average FPS** enabled (OVR Metrics Tool) shows the running frame rate live — that is the ground truth.

**If a rate change happens mid-session** (thermal drop), you will get one line like:

* `[CrankFrameRateUp] refresh rate changed 90Hz -> 72Hz since the last check. Meta's dynamic throttling can silently drop a rate above 72 Hz back to 72 (heat/battery) — …`

## What compromises were made?

* **`OVRManager.isHmdPresent` is an XR *session node state*, and it is not settled at `Awake()` time.** Learned on-device, 2026-11-04: the first version of this script checked it in `Awake()` and got **FALSE on the headset** (it is per-frame node state polled from the runtime — `OVRNodeStateProperties.IsHmdPresent()` in the package cache), so it self-disabled before doing anything. The current version polls it in `Update()` with a bounded budget (`waitBudgetFrames`) instead of assuming; in the editor/PC it simply times out and logs why it is skipping. Do not "simplify" this back to a one-shot check.
* **The log-level trap.** The lab's MQDH view was hiding Info-level logcat lines, so the first version's `Debug.Log` status lines never appeared even though the script executed them (verified by pulling `adb logcat` — the lines were there at `I/Unity`). Because the lab verifies against log markers, *every outcome line in this script is `Debug.LogWarning`*: that is a deliberate contract (visible under any level setting), not an error-style convention. If you copy this script's shape elsewhere, copy the levels too.
* **Confirmation is a read-back poll, not the SDK event.** Meta's docs state the `DisplayRefreshRateChanged` event "is not dispatched" under OpenXR (during dynamic throttling), and this project runs on OpenXR — so the script confirms by reading `OVRPlugin.systemDisplayFrequency` back every frame for ~2 s after the request. If the read-back never matches, it says so explicitly (a *rejection* looks exactly like "still 72 Hz" and must not go unnoticed).
* **The offered list is the ceiling, and it is decided by the runtime, not by Unity.** Per Meta's current doc: the runtime **rejects a rate that is not on the list the device reports**, so this script only ever requests a value taken *from that list* (exact `refreshRate` if offered, else the closest offered value). The one prerequisite on the app side that the doc does require is that the **Android manifest declares a device that supports the rate** — which this project already satisfies (README step 16, `Target Devices: Quest Pro`). (The old Quest 2-era "Developer → Refresh rate = Application" setting is not in the current doc, and the Pro's 2.9 developer menu does not carry it — that is *not* a problem.)
* **Thermal throttling is real and silent.** Meta's doc: an app above 72 Hz that runs hot gets dropped back to 72 Hz by dynamic throttling as a first step (then frame-rate cut). In a 1-hour participant session that is a silent experiment change, so the script re-reads the rate every `watchThrottleEveryFrames` frames after applying and logs **any change** (in either direction). It does not try to re-assert the rate — re-requesting a thermally-limited rate is fighting the runtime — but the log line makes the downgrade visible for the session record.
* **One-shot, not per-frame.** A refresh-rate request is a session-level state change; the script applies exactly once and then only performs the (cheap) watchdog reads. After the confirm phase, per-frame cost is zero except every Nth frame.
* **Editor/PC safe.** No HMD ⇒ the poll budget is spent, one self-explaining warning line, and the script disables itself. No crash, no spam.

## Externally available variables (for meddling in the Inspector)

```C#
    // The refresh rate to run at, in Hz (the way Quest itself names the setting).
    // 0 = the maximum the device offers. Quest Pro offers: 72, 80, 90.
    public float refreshRate = 90f;

    public int waitBudgetFrames = 900;        // polling budget for startup settling (~12 s)
    public int watchThrottleEveryFrames = 900; // throttling-watch period after applying; 0 = off
```

The network-facing surface: **none.** This is one of the few scripts in `Assets/Scripts/` with no UDP port by design; it is not part of the `40000`–`40007` experiment-control block and takes no protocol. (If it ever *did* need external control, the next free port would be `40008`.)

## Scene needs

The HMD, whose session node state must settle (poll budget above). No other scene object, rendered or otherwise.
