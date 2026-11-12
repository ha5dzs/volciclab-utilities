# VR Headset UDP Helpers

Matlab-side helpers for communicating with a VR headset running a Unity game-engine implementation. All communication is over **UDP**, asynchronous, and in most cases **fire-and-forget** — there is no persistent "connection" to maintain.

These files were developed for a VR grasping experiment and are intended to be moved to the central `volciclab-utilities` repository. They form the *Matlab half* of a wire contract whose *headset half* is implemented as C# scripts in the Unity project — the **docs in `unity_docs/` are the source of truth** for the headset-side protocol. When a protocol needs to change, **both halves must be updated together** (the grammar is also observable directly in Wireshark, so a silent change on one side will break the other).

---

## Two UDP interfaces

The experiment communicates with **two different machines** on the lab network:

| Interface | Target machine | Address | Purpose |
|---|---|---|---|
| `network_interface_unity` | **The VR headset** | Per-experiment, in `volciclab_vr_config.m` (`headset.ip_address`) | All `volciclab_vr_*` helpers |
| `network_interface_datasink` | **The lab UniversalDataSink** (on the OptiTrack computer) | `192.168.42.5` (fixed, permanent lab IP) | `volciclab_datasink_config` only |

They are **separate endpoints**, not a single shared pipe: each helper in this directory uses *one* of the two, chosen by *which machine it is talking to*.

Both `udpport` objects are created in `create_network_interfaces.m`; the helpers **do not create sockets themselves** — they accept a pre-initialised `udpport` object as their first argument, guard it with `isvalid()`, and **flush both input and output buffers before returning** so stale bytes don't leak into the next call (an R2026a quirk).

### Two-stage wait convention (query helpers only)

All query helpers follow the same pattern:

1. Send the `?` and wait.
2. **Warn** if no reply arrives within `headset.reasonable_response_time` (default `0.2 s`).
3. **Error** if no reply arrives within `headset.comm_timeout` (default `1 s`).

The two values are read from `volciclab_vr_config.m`. Fire-and-forget helpers have no wait.

---

## Functions

### `volciclab_vr_config.m` — shared config script

Not a function; a plain script that defines the `headset` struct. Every other helper depends on it being run (and in the path) first.

Key fields:

| Field | Description |
|---|---|
| `headset.ip_address` | The headset's LAN IP. **Change this per-experiment.** |
| `headset.reasonable_response_time` | Warning threshold (s) for blocking queries. |
| `headset.comm_timeout` | Error threshold (s) for blocking queries. |
| `headset.calibration_scotoma` | Struct: the *calibration* scotoma (visual check reference). |
| `headset.calibration_spot` | Struct: the *calibration spot* (fixate-on stimulus). `azimuth`/`elevation` are in **degrees of visual field, head-frame** (the "target" in the calibration formula). |
| `headset.eye_tracking_confidence_threshold` | Lab default: `0.95`. |
| `headset.eyes_are_closed_threshold` | Lab default: `0.30`. |
| `headset.vr_port_*` | Port definitions (see table below). |

> **Note:** `expt_settings.m` (in the same directory) is the *experiment-specific* config. It holds the *experiment* scotoma shape, the per-rigid-body structures for `volciclab_vr_object_control`, and the OptiTrack rigid-body IDs. It is a different category from `volciclab_vr_config.m` (the *shared* headset config) and is **not** included in this README.

---

### `volciclab_vr_scotoma(udp, [struct])` — port 40006

**`?` / set triad.** Query or set the *experiment* scotoma running on the headset.

- **Query** (1 arg): returns the live scotoma struct as currently on the headset.
- **Set** (2 args): sends the struct to the headset (fire-and-forget).

**Scotoma struct** (canonical shape, both directions):

```matlab
struct( ...
    'is_rendered',      1,        % boolean
    'azimuth',          0,        % deg, with respect to the retina
    'elevation',        0,        % deg, with respect to the retina
    'inner_diameter',   0,        % deg; 0 = solid disk
    'outer_diameter',   5,        % deg
    'colour',           '#AAAAAAFF')  % RGBA HTML
```

The set path is **not verified** on the headset side; the experimenter must confirm the change visually via the headset's display.

---

### `volciclab_vr_calibration_spot(udp, [struct])` — port 40007

**`?` / set triad.** Query or set the *calibration spot* on the headset.

- **Query** (1 arg): returns the live calibration-spot struct.
- **Set** (2 args): sends the struct to the headset (fire-and-forget).

**Calibration-spot struct** (same canonical shape as the scotoma):

```matlab
struct( ...
    'is_rendered',      1,
    'azimuth',          0,        % deg, **head-frame** (centre of the headset)
    'elevation',       -15,       % deg, head-frame (slightly below the fovea)
    'inner_diameter',   6,        % deg; >0 = annulus
    'outer_diameter',  12,        % deg
    'colour',           '#006400FF')
```

The *azimuth/elevation* here are the **spot's head-frame position** — a *stimulus location*, not a gaze-frame target.

---

### `volciclab_vr_eye_tracker_offset(udp, [az, el])` — port 40001

**`?` / set triad.** Query or set the *per-trial gaze calibration offset*.

- **Query** (1 arg): returns the *calibrated* gaze `[azimuth, elevation]` — the 10-sample moving average on the Unity side, with the current offset already added in. Units: **degrees**.
- **Set** (3 args): sets the offset (fire-and-forget, no reply). Convention: **+azimuth = right, +elevation = up**. Deliberately *not* clamped (a calibration correction may legitimately sit outside any "reasonable" box).

**Use in the calibration loop:**

```matlab
% Read the raw uncorrected gaze after resetting the offset:
volciclab_vr_eye_tracker_offset(udp, 0, 0);          % reset to (0,0)
[raw_az, raw_el] = volciclab_vr_eye_tracker_offset(udp);  % calibrated == raw (offset is 0)
% Apply the calibration:
volciclab_vr_eye_tracker_offset(udp, target_az - raw_az, target_el - raw_el);
```

---

### `volciclab_vr_eye_tracker_settings(udp, [threshold, threshold])` — port 40000

**`?` / set triad.** Query or set the two eye-tracker *threshold* settings.

- **Query** (1 arg): returns `[confidence_threshold, eyes_closed_threshold]`.
- **Set** (3 args): sets both (fire-and-forget). Both values are **clamped to [0, 1]** on the headset side.

These are *sensitivity* settings, not per-trial calibration fields. They are lab defaults in `volciclab_vr_config.m`; an experiment can override them.

---

### `volciclab_vr_calibrate_eye_tracker(udp)` — loop helper

**The full per-trial calibration loop.** One call, N iterations. This is the *only* thing the experiment code needs to call to keep the eye tracker accurate for the current trial.

**What it owns:**
- The loop structure and termination semantics.
- The two manual touches: the `fprintf`+`pause` fixture prompt in step 5, and the `questdlg` "Good to go?" gate in step 8.
- The offset arithmetic: `offset = (spot.azimuth - raw_az, spot.elevation - raw_el)` where the *spot's head-frame position* (from `headset.calibration_spot`) is the *target* and the *gaze reading* (after the reset in step 3) is the *measured* value. This matches the tested-and-working `eye_tracker_calibration_demo.py`.

**What it does NOT do:**
- It does **not** record the offset (a data concern for the experiment code — the headset holds it; read it back with `volciclab_vr_eye_tracker_offset(udp)` after the call).
- It does **not** add a retry counter (the `questdlg` is the retry mechanism).
- It does **not** assert any `is_rendered` on the experiment scotoma (it's *restored* exactly as found in step 1 — "leave it as you found it").

**Loop shape (steps 1–9):**

| Step | Action |
|---|---|
| 1 | Save the live experiment scotoma (query port 40006). |
| 2 | Hide the visual disturbance (set port 40006, `is_rendered=0`). |
| 3 | Reset the offset to (0, 0) (set port 40001). |
| 4 | Show the calibration spot (set port 40007, `is_rendered=1`). |
| 5 | Prompt the experimenter: "ask the participant to look at the spot and hold fixation; press ENTER to continue." (`fprintf` + `pause`) |
| 6 | Measure the raw gaze (query port 40001 — at this point offset is 0, so calibrated = raw). |
| 7a | Apply the inverse offset (set port 40001: `target - raw`). |
| 7b | Re-show the *calibration* scotoma (set port 40006, `is_rendered=1`) for the visual check. |
| 8 | `questdlg` gate. **No** → back to step 2 (fresh iteration). **Yes** → exit path. |
| 9 | Hide the calibration spot (set port 40007, `is_rendered=0`) and restore the experiment scotoma **exactly** as found in step 1 (set port 40006 with the saved struct). |

**Returns:** nothing. The function's side effects are: the eye-tracker offset is set to the calibrated value (readable via `volciclab_vr_eye_tracker_offset(udp)`), and the *visual state* of the headset is restored to exactly what the experiment left it in.

**Read-only dependency on `volciclab_vr_config.m`:** the function does not use `headset.ip_address` (it only talks to ports), but it *does* read `headset.calibration_scotoma`, `headset.calibration_spot`, and validates that `headset.calibration_spot.azimuth`/`.elevation` are present and finite numbers.

---

### `volciclab_vr_passthrough(udp, state)` — port 40002

**Fire-and-forget.** Toggle the headset's passthrough mode.

- `state` = `'0'` (or `'false'`): off.
- `state` = anything else: on.

No query path; the set path sends and does not wait for a reply.

---

### `volciclab_vr_hands(udp, state)` — port 40003

**Fire-and-forget.** Toggle hand rendering on the headset.

- `state` = `'0'` (or `'false'`): off.
- `state` = anything else: on.

No query path; the set path sends and does not wait for a reply.

---

### `volciclab_vr_get_ipd(udp)` — port 40005

**Read-only.** Returns the headset's current IPD in **millimetres**.

- The game engine reports IPD in *metres* on the wire; this helper multiplies by 1000 for mm consistency.
- **IPD cannot be set** from Matlab — the lenses are not motorised; they are adjusted manually by the wearer.

---

### `volciclab_vr_scotoma_distance(udp, [distance])` — port 40004

**`?` / set triad** (the query path is the *template for the other helpers' failure paths*).

- **Query** (1 arg): sends `?` to port 40004. **Intentionally no reply is expected** from the headset (this function is designed to demonstrate the warn-then-error sequence; see the function's docstring for the exact wording). When the query path of a helper is not implemented in the headset, this is the precedent to copy.
- **Set** (2 args): sends the scotoma distance (fire-and-forget).

---

### `volciclab_vr_align_to_optitrack(udp, [delta_or_query])` — port 5000

**Three call shapes** (disambiguated by the 2nd argument's type):

| Shape | Args | Mode |
|---|---|---|
| Plain trigger | `volciclab_vr_align_to_optitrack(udp)` | Fire-and-forget: trigger the alignment and don't wait. |
| Query | `volciclab_vr_align_to_optitrack(udp, '?')` | Blocking: wait for the 6-field reply. |
| Fudge-delta + trigger | `volciclab_vr_align_to_optitrack(udp, [dx, dy, dz])` | Fire-and-forget: apply a 3-element delta then trigger. |

**Query reply** (6 semi-colon-separated fields):

```
OK;STATE;fx,fy,fz;errMm;errDeg;attempts
```

- `STATE`: `"OK"` or `"FAILED"`. The helper derives `alignment_status = ~strcmpi(STATE, 'FAILED')`.
- `fx,fy,fz`: the alignment transform (metres).
- `errMm`: **already in millimetres on the wire** (the field name is the unit cue, not a conversion). Do **not** multiply by 1000.
- `errDeg`: the angular residual, in degrees.
- `attempts`: the number of OptiTrack frames processed.

**Port 5000 is the lab-specific `UnityOptitrackAlignment` port**, deliberately *outside* the core `40000`–`40008` block so the same APK is findable on any lab box.

---

### `volciclab_vr_object_control(udp, port_number_or_struct)` — ports 5001+

**Per-instance `ExternalGameObjectControl`** (the user picks a port in the Unity editor and notes it down; two instances must not share a port).

| Shape | Args | Mode |
|---|---|---|
| Query | `volciclab_vr_object_control(udp, 5001)` (2nd arg is a *number*) | Blocking: wait for the canonical object struct. |
| Set | `volciclab_vr_object_control(udp, obj_struct)` (2nd arg is a *struct*) | Fire-and-forget: send the struct. |

The **type** of the 2nd argument disambiguates the two shapes. The canonical shape is the **same in both directions** so a query result is round-trippable straight back into the set path.

**Object struct** (canonical shape):

```matlab
struct( ...
    'port',        5001,    % the per-instance port number
    'translation', [x, y, z],  % 1×3, metres
    'rotation',    [rx, ry, rz]) % 1×3, Euler degrees
```

---

### `volciclab_vr_telemetry_decimation(udp, decimation)` — port 40008

**Fire-and-forget.** Set the `DataTransmitter`'s decimation on the headset (the cadence at which telemetry records are sent to the lab datasink).

| Value | Meaning |
|---|---|
| `0` | Stop streaming entirely. |
| `1` | Send one record every frame (the densest cadence; ~90/s at 90 Hz). |
| `N` (N > 1) | Send one record every Nth frame. |

No query path; the change is observable as the next batch of telemetry records landing in the datasink's current file.

---

### `volciclab_datasink_config(udp, file_path)` — port 3430, `192.168.42.5`

**Fire-and-forget.** Tell the **lab UniversalDataSink** (on the OptiTrack computer) where to dump the next recording.

> **Naming note:** this helper deliberately breaks the `volciclab_vr_*` convention because it talks to **lab infrastructure**, not to the VR headset. It is written for the central `volciclab-utilities` repository from day one and does **not** get moved out of there.

- **Sends to:** `192.168.42.5:3430` (fixed, hard-coded — not pulled from config).
- **Payload:** a single file-path string.
- **The datasink's data port** is `3431` on the same machine: the headset's `DataTransmitter` streams records here; the sink appends its own arrival timestamp and a trailing newline to each record. This is the "data" half of the datasink protocol; `volciclab_vr_telemetry_decimation` on the headset controls the *cadence* of that stream.

---

## Port allocation summary

| Port | Function | Helper |
|---|---|---|
| `40000` | Eye-tracker threshold settings | `volciclab_vr_eye_tracker_settings` |
| `40001` | Per-trial gaze offset (calibration) | `volciclab_vr_eye_tracker_offset` |
| `40002` | Passthrough toggle | `volciclab_vr_passthrough` |
| `40003` | Hand rendering toggle | `volciclab_vr_hands` |
| `40004` | Scotoma distance adjuster | `volciclab_vr_scotoma_distance` |
| `40005` | IPD reporter | `volciclab_vr_get_ipd` |
| `40006` | Experiment scotoma | `volciclab_vr_scotoma` |
| `40007` | Calibration spot | `volciclab_vr_calibration_spot` |
| `40008` | Telemetry decimation | `volciclab_vr_telemetry_decimation` |
| `5000` | OptiTrack alignment (lab-specific) | `volciclab_vr_align_to_optitrack` |
| `5001, 5002, …` | Per-instance `ExternalGameObjectControl` | `volciclab_vr_object_control` |
| `3430` (datasink) | UniversalDataSink control | `volciclab_datasink_config` |
| `3431` (datasink) | UniversalDataSink data | *(driven by the headset's `DataTransmitter`)* |

---

## Conventions

### Socket guard and buffer flushing

Every helper:

1. Guards the socket with `isvalid(udp_port_object)` — **not** `isa(…, 'udpport')` (which doesn't reliably recognise the socket object on R2026a).
2. **Flushes both input and output buffers** before returning, so stale bytes don't leak into the next call (R2026a quirk).
3. Accepts the socket as its **first argument** — the helper does not create or own the socket.

### Units

The game engine reports quantities in **metres**. Exceptions on the wire:

| Quantity | Unit on the wire | Matlab-side handling |
|---|---|---|
| IPD | **metres** | `* 1000` → mm (see `volciclab_vr_get_ipd`) |
| `errMm` (alignment residual) | **already millimetres** (the field name is the cue) | **No rescaling** (see `volciclab_vr_align_to_optitrack`) |

Angles are in **degrees** throughout (azimuth/elevation, Euler rotations).

### `?` / set / fire-and-forget triad

This is the canonical interaction pattern. New helpers should pick one pattern and follow it:

- **`?` / set** (query + fire-and-forget): `volciclab_vr_scotoma`, `volciclab_vr_calibration_spot`, `volciclab_vr_eye_tracker_offset`, `volciclab_vr_eye_tracker_settings`, `volciclab_vr_object_control`.
- **Set only** (fire-and-forget): `volciclab_vr_passthrough`, `volciclab_vr_hands`, `volciclab_vr_telemetry_decimation`, `volciclab_datasink_config`.
- **Query only** (read-only): `volciclab_vr_get_ipd`.
- **Loop helper** (multi-step, human-gated): `volciclab_vr_calibrate_eye_tracker`.

### Deliberately-failing template

`volciclab_vr_scotoma_distance.m` is the *template for the other helpers' failure paths*: its query path sends `?` and intentionally receives no reply, causing the warn-then-error sequence. When the query path of a helper is not implemented in the headset, its comment block is the precedent to copy.

---

## Compatibility notes

- **MATLAB R2026a** (the target host). `unixtime` is not available in this installation (use `posixtime` instead); `datetime`/`duration` are available but `tNow - epoch` (the `minus` method) is **not** in the Octave separable `datatypes` package build present in the dev sandbox, so that idiom is avoided in favour of `datenum`-based arithmetic where a date delta is needed.
- **Octave** (dev sandbox) is for pure-computation sanity-checks only (string parsing, array arithmetic, control flow). **Do not run** the `udpport`-based helpers under Octave — the `udpport` class does not exist there, and Octave is a reference implementation, not a drop-in MATLAB replacement. A passing or failing Octave run is **not** proof that a MATLAB-specific API works (or breaks) on the R2026a host.
- `.asv` files in this directory are Matlab autosave backups — safe to ignore, **do not commit or copy them**.

---

## File manifest

| File | Category |
|---|---|
| `volciclab_vr_config.m` | Shared config (run before any helper) |
| `volciclab_vr_scotoma.m` | Core-block helper (port 40006) |
| `volciclab_vr_calibration_spot.m` | Core-block helper (port 40007) |
| `volciclab_vr_eye_tracker_offset.m` | Core-block helper (port 40001) |
| `volciclab_vr_eye_tracker_settings.m` | Core-block helper (port 40000) |
| `volciclab_vr_calibrate_eye_tracker.m` | Loop helper (uses 40001, 40006, 40007) |
| `volciclab_vr_passthrough.m` | Core-block helper (port 40002) |
| `volciclab_vr_hands.m` | Core-block helper (port 40003) |
| `volciclab_vr_get_ipd.m` | Core-block helper (port 40005) |
| `volciclab_vr_scotoma_distance.m` | Core-block helper (port 40004) — deliberately-failing template |
| `volciclab_vr_align_to_optitrack.m` | Lab-specific helper (port 5000) |
| `volciclab_vr_object_control.m` | Per-instance helper (ports 5001+) |
| `volciclab_vr_telemetry_decimation.m` | Core-block helper (port 40008) |

