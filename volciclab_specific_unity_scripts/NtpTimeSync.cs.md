# NtpTimeSync.cs documentation for version 1.0

This script gives the headset a **corrected UTC clock**. At start it asks a real NTP server for the time (a couple of samples), works out the offset between the Quest's own system clock and that reference, and then exposes the corrected time to the rest of the project as one function call: `NtpTimeSync.UnixTimeNowInMs()`.

## Why it exists

`DataTransmitter.cs` needs **unix timestamps in milliseconds** at experiment runtime so that the headset's clock and the lab computers' clocks can be correlated precisely (that is how network latency and OptiTrack streaming latency get measured against a single time base).

But the Quest keeps *its own* system clock, synced to the internet NTP only **loosely** — the app-layer clock is the wrong thing to trust as the frame of reference, and re-syncing it from ADB/Matlab at experiment time is not possible (wireless debugging will be off, and forcing a clock update from the shell is not on the table). The only workable option is what this script does: **measure the offset once from the application layer** (an NTP round trip the app itself performs) and then apply it.

This is deliberately a *convenience feature with a correctness payoff*: it makes every lab clock sit in the same frame so the millisecond timestamps are comparable across machines. It is not a substitute for the lab's real time infrastructure — the lab intends to stand up a **Stratum-2** NTP server later (the building gets no GPS — jammed — and a Cesium clock is overkill for a psychology lab). Until then, the public pool is the reference.

## How it is used?

* Attach to a **permanently active GameObject — the `[BuildingBlock] Camera Rig`** (README step 48). It renders nothing and has no scene dependencies beyond the HMD being on (so it needs network, not a specific Unity object).
* Call the static accessor from anywhere, including other `MonoBehaviour`s:
  * `long nowMs = NtpTimeSync.UnixTimeNowInMs();` — corrected UTC, **millisecond** precision.
  * `string now = NtpTimeSync.UnixTimeNowInMsString();` — same value, as a string.
  * `if (!NtpTimeSync.IsSynced)` — false until the first adequate sample; treat the timestamp as **uncorrected**.
* That is the whole interface. `DataTransmitter` (rewritten 2026-11-06) sources its `TimestampMs` field from `NtpTimeSync.UnixTimeNowInMs()`.

**Silent fallback (convenience).** Before the first good sample has landed, `IsSynced` is `false` and `UnixTimeNowInMs()` returns the **raw** system clock — so no caller ever blocks, crashes, or has to special-case startup. Once a good sample applies, the offset is in effect for the session.

**Verification.** The outcome markers are **`Debug.LogWarning` on purpose** (the lab's MQDH hides Info-level lines — same contract as `CrankFrameRateUp`). In the MQDH log (filter on `[NtpTimeSync]`) you should see exactly one of:

* `[NtpTimeSync] offset applied: <N> ms vs ae.pool.ntp.org (round-trip <M> ms). IsSynced is now true.`
* `[NtpTimeSync] no adequate sample (best delay ... ms >= sane ceiling 3000 ms). Timestamps fall back to UNCORRECTED system clock.`

(**On-device verified 2026-11-04 (three runs):** the `offset applied` line appears at every start, and the offset was a steady **−1939 / −1937 / −1943 ms** (≈ 1.9 s) across the runs with NTP round-trips of **46–153 ms** — i.e. the headset's wall clock runs ~1.9 s SLOW versus `ae.pool.ntp.org`; a two-second offset is the *correct* answer here (not hours/days), `IsSynced` was `true` in all three. **Re-read 2026-11-06 during the `DataTransmitter` test: offset ≈ 500 ms.** Conclusion: the skew is real, but **drifts day to day** — the headset clock is unregulated, so expect a *different* offset on each launch; never assume a constant offset between days, and do not treat any particular reading as an error.)

## What compromises were made?

* **One-shot at start, not a live PTP-grade sync.** The app can't (and shouldn't) do sub-microsecond time transfer. A single NTP exchange at startup is the pragmatic ceiling for an application-layer clock, and it is what we need: the headset clock is then *in the same frame* as the lab clocks, which is the whole point (precise latency measurement), not millisecond-per-millisecond tracking. The offset is re-derived only by restarting the app or calling `NtpTimeSync.RequestResync()`.
* **Symmetric-latency assumption.** The offset for a sample is computed from **both** server timestamps (receive **and** transmit): `offset = ((t1 − t0) + (t2 − t3)) / 2`, `delay = (t3 − t0) − (t2 − t1)`. That is the standard NTP offset under the symmetric-wire assumption; it uses both server fields so the result does not depend on which one a particular server fills in. The error term is roughly `(one-way error) − (the other)`, and —
* **Min-delay sample selection.** We take `sampleCount` samples and **keep the one with the smallest round-trip delay** (the classic NTP "closest sample" rule). A smaller round trip means less asymmetric-wire skew, so the surviving sample is the one least contaminated by the latency assumption. We do *not* average samples (averaging would keep the bias).
* **A sane ceiling is enforced.** A sample whose round-trip delay exceeds `saneMaxDelayMs` (default `3000` ms) is discarded, and if *no* sample is within the ceiling the offset is **not** applied and `IsSynced` stays false. This stops a pathologically delayed reply — or a server in a different time frame — from silently corrupting every timestamp for the session.
* **Nothing blocks the render thread.** DNS + UDP run `async`; the offset is written under a lock and read as an atomic `long` (the codebase's "off-thread record, main-thread read" pattern). No `BeginReceive`/`EndReceive` thread writes Unity state.
* **No UDP control port.** Although it is `UdpClient`-based, this is a NTP **client** to `123` with an ephemeral local port — it is *not* a lab control socket, it does not listen on a fixed port, and it is **not** part of the `40000`–`40007` experiment-control block. It consumes no protocol and no port number.
* **Default server is the public pool, not the lab.** `ae.pool.ntp.org` is in the same time frame the lab machines will sync to, which is what keeps the clocks comparable. If the lab stands up its own Stratum-2 NTP on the OptiTrack PC / file server, set `ntpServerHost` to that (an IP literal works too, to remove DNS at experiment time).
* **Editor/desktop-safe.** No HMD needed; if DNS or the network fails it simply logs the `no adequate sample / DNS failed` warning and leaves the clock uncorrected. No crash, no spam.

## Externally available variables (for meddling in the Inspector)

```C#
    public string ntpServerHost = "ae.pool.ntp.org";  // the time reference; IP literal allowed
    public int ntpServerPort = 123;

    public int sampleCount = 2;            // samples to take, keep the min-delay one
    public int perRequestTimeoutMs = 750;  // per-request timeout
    public int saneMaxDelayMs = 3000;       // discard a sample slower than this
    public int resyncPeriodSeconds = 0;    // 0 = one-shot at start (the norm); >0 = re-sample

    public bool verboseDiagnostics = false;
```

The programmatic surface the rest of the project uses:

```C#
    NtpTimeSync.UnixTimeNowInMs()        // -> long, corrected UTC ms (millisecond precision)
    NtpTimeSync.UnixTimeNowInMsString()  // -> string, same value
    NtpTimeSync.IsSynced                 // -> bool, have we got a good offset yet?
    NtpTimeSync.OffsetMs                 // -> long, the applied offset (server = local + this)
    NtpTimeSync.LastDelayMs              // -> long, the round-trip delay of the chosen sample
    NtpTimeSync.RequestResync()          // re-run the NTP pass on demand
```

## Scene needs

The HMD, network connectivity, and a reachable NTP server. No other scene object, rendered or otherwise. Attach to `[BuildingBlock] Camera Rig` (README step 48).
