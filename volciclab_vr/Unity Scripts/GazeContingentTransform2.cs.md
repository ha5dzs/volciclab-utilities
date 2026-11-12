# GazeContingentTransform2.cs documentation for version 2.0

This script is the core part that turns the Meta Quest Pro into a low-cost research-grade eye tracker for vision science applications.
The original intention for the eye tracker in Meta was principally for character animation. This is really a story of broken dreams - very few people use the eye tracker for its intended purpose, it is not really accurate, especially since the user wears it and the entire headset can move with respect to the head, and it does not track vergence or detect eye blinks directly. This made the built-in eye tracker pretty much useless for developers, and consumers decided not to pay more than double price (with respect to the Meta Quest 3) for a lower-spec headset just for the eye tracker.

## A brief history of this code

Initially, many Meta XR SDK versions and Horizon OS releases ago, I created [`GazeContingentTransform.cs`](https://raw.githubusercontent.com/ha5dzs/volciclab-utilities/refs/heads/main/volciclab_specific_unity_scripts/GazeContingentTransform.cs), which had a lot of extensive workarounds for coordinate conversions. With the new version of the Meta XR SDKs, some of the eye tracker bugs were fixed, which in turn broke my workarounds, so I decided to write a new (this) version from scratch. This time, with the help of our fancy new locally running agentic AI.

## How it is used?

This script is to be applied for a GameObject that is a child of `CenterEyeAnchor` in the `[BuildingBlock] Camera Rig` prefab. The object itself must be placed in the dead centre, ahead of (i.e. 'forward', x=0, y=0, z=(+something_reasonable, say 0.5); all rotations are 0 by default) of the camera rig. This will result will be that any child object of this GameObject will be rotated with the eye gaze, so it will create a retino-centric representation. Say, a cube added to this GameObject will always look like a square to the observer wearing the headset, no matter which way the observer looks. The script drives both `localRotation` and `localPosition` of this GameObject (the authored local position seeds a public, live field — `scotomaLocalOffset` — that is swept around `CenterEyeAnchor` by the gaze rotation, and which can be re-based over the network via port 40004), so the whole subtree orbits the eye on a sphere of radius `Z` — children locked to the fovea always show their face to the observer, never an edge-on view. (The earlier version only rotated in place around the object's own origin, which made children spin on the spot instead of orbiting the eye.)

It is important that the script is only attached to the `CenterEyeAnchor` and the depth is being managed manually, because the rendered binocular disparity must be similar to whatever simulus is being used. If this relationship is not maintained, then the scotoma will appear as double to the observer, as they verge towards the stimulus. It is also important to set the distance accordingly to avoid the accommodation-vergence conflict - as we humans verge towards and object close to us, we also adjust the focal lengths of the eye lenses, despite the VR display not doing the same. This will result in things appearing blurry in VR when they are too close to the observer, and will also result in visual discomfort, early onset of fatigue and possibly feeling sick too.

So, for a VR-based experiment where a stimulus is on the table in front of the participant, set the Z distance of this GameObject to +0.5, and that is a good starting point for testing. Adjust accordingly based on participant anatomy comfort.

## What compromises were made?

* The eye tracker does _not_ track vergence and it is noisy.
In order to reduce noise, the two gaze vectors are 'slerp'-ed (spherical linear interpolation, this is a beauty on its own) to give a mean value of a gaze vector. This also means that the noise is halved, theoretically. In practice, not really.

* Lots of conversions, avoiding gimbal lock, but not eliminating it.
As the eye tracker rotations are intended for character animation, they were prepared to rotate eyeballs in a model. For scientific applications, this is not really useful, because we don't want to know the real-world orientation of the eyeballs, we want to know where the observer looks within the visual field we control with the game engine. So, the rotation quaternion (in radians) returned from the eye tracker is turned into a two-dimensional vector (in degrees) that initially is normal to the `CenterEyeAnchor`. These two dimensions are:
  * `Azimuth`, in degrees, which is for measuring sideways glance
    * Positive is when the observer is looking _right_
    * Zero is when the observer is looking _ahead_
    * Negative is when the observer is looking _left_
  * `Elevation`, in degrees which is for measuring up-down glance
    * Positive is when the observer is looking _up_
    * Zero is when the observer is looking _at the horizon_
    * Negative is when the observer is looking _down_
This 2D vector (after the per-trial calibration offset is added to it) is then converted back to a quaternion — rebuilt as pure yaw + pitch with **no roll component** (in head space there is nothing to preserve for torsion; the 2 DOFs carry everything that matters, and this also drops roll noise from the tracker). This calibrated rotation is applied as *both* the `localRotation` and the `localPosition` (the authored offset, cached in `Awake`, rotated by the same calibrated quaternion) of the GameObject that is child of `CenterEyeAnchor`. The result is that anything added to this GameObject does not spin on its own origin: it **orbits around `CenterEyeAnchor`** on a sphere whose radius is the initial Z coordinate, always square-on to the observer — a flat 2D patch always faces them, so a true impression of a visual deficit works even with a plane.

* Two APIs are being used for blink detection and it is still messy
In the first version of `GazeContingentTransform.cs`, I relied on `OVRPlugin.EyeGazesState.EyeGazes[(int)OVRPlugin.Eye.<Left/Right>].Confidence`, and this was a mistake. It does not seem to work very well when it comes to eye blink detection, because it returns erroneous coordinates before losing the eyes, which resulted in the rendered scotoma 'slipping to the side' for participants in an experiment. This meant that by blinking rapidly, the participants could actually see the stimulus that the scotoma was _supposed_to cover, which pretty much destroyed the quality of the data. Later-on, with the use of agentic AI, it turned out that there is an additional API endpoint where we could detect eye blinks - with face expression tracking too. In `OVRFaceExpressions.GetWeight(OVRFaceExpressions.FaceExpression.EyesClosed<L/R>)`, I can reduce the eyelid state to a floating-point number, and set a threshold above which I deem the eyes being closed and won't update the rotation returned by the eye tracker.

* Calibration is basically impossible so it must be done on the fly
Normally, in an experimental setting, the participant is sat at the experimental table, and the head is placed on a chin rest. If scientific rigor demands it, the participant may even be asked to use a bite bar to make sure the head is steady. In these cases, the eye tracker is external, and a rigorous calibration process is taking place. Here, we do not have this luxury. Here, the participant _wears_ the eye tracker, and it is loosely attached to the head with soft pads. The participant is also freely able to move, shake their head and basically do typical flesh-and-blood human things. This means that the eye tracker moves all over the place with respect to the participant, and it needs continuous adjustment. Luckily, this can be done as simply as adjusting the azimuth and elevation offsets before a trial.

## Externally available variables

There are two sliders:

* One for adjusting the eye tracking confidence threshold

```C#
    // <summary>
    /// Too high: will skip tracking. Too low: will be noisy.
    /// </summary>
    [Range(0f, 1f)]
    public float EyeTrackingConfidenceThreshold = 0.95f;
```

* One for adjusting the threshold where the eyelids are deemed closed.

```C#
    // <summary>
    /// Adjust this value to see when the system interprets the eyes being closed.
    /// </summary>
    [Range(0f, 1f)]
    public float EyesAreClosedThreshold = 0.3f;
```

* A Vector2 `CurrentGazeVector` [`Azimuth` (X), `Elevation` (Y)] for the calculated gaze vector

This is for collecting online data from the headset. Other scripts stream this to a dedicated data collection server, and will be analysed later-on. These angles are in degrees, not radians. The direction convention is the same as above ([0, 0] is dead center ahead; [+, 0] is looking right; [0, -] is looking down and so on)

* A Vector2 `GazeVectorOffset` [`Azimuth` (X), `Elevation` (Y)] for offsetting the gaze vector

Since the observer is actually wearing the eye tracker, it may move with respect to the head at any time.

In a separate set of scripts, to minimise the measurement error from this, the eye tracker location is being 'reset' at the beginning of every trial. In practice, this is a process where we place a small sphere to a known location (calculated from `CenterEyeAnchor`), asking the participant to look at that sphere, and when the participant confirms, then we record the raw (uncalibrated) gaze vector. The `GazeVectorOffset` is the inverse of the difference between the sphere's and the gaze vector's azimuth and elevation coordinates. That external script will set `GazeVectorOffset`, so this variable must be globally accessible and being able to be written to.

The offset is applied to the calibration AND the visuals: `CurrentGazeVector` and the transform's pose both use raw angles + `GazeVectorOffset`, so the rendered scotoma always shows at the true (calibrated) fovea, not the raw measured location. This keeps data and visuals consistent — when calibration updates the offset, the scotoma snaps to the corrected retinal location.

* A `Vector3` `scotomaLocalOffset` [x, y, z] — the scotoma's "resting" offset from `CenterEyeAnchor`, i.e. its **viewing distance** (the binocular-disparity radius). Authored as `(0, 0, 0.5)` (~50 cm ahead) by default, seeded in `Awake` from the authored position, and **public / live**: `Update()` sweeps it around the eye every frame, so the radius in use is always the current value.

The external script `ExternalScotomaDistanceAdjuster` (`Assets/Scripts/ExternalScotomaDistanceAdjuster.cs`, **UDP port 40004**) writes this field's `.z` to re-base the shared depth of both the gaze- and head-contingent anchors in flight (see its own `.cs.md`). Because `scotomaLocalOffset` is live rather than a one-shot `Awake` cache, changing it never leaves the per-frame pose stale.

* Per-eye eyelid state plus the mean — three public floats for the data sink
  * `LeftEyelidWeight` (float) — the current `OVRFaceExpressions` `EyesClosedL` weight: `0` = eye fully open, `1` = fully closed.
  * `RightEyelidWeight` (float) — the `EyesClosedR` weight, same scale.
  * `EyelidMean` (float, computed property) — `(LeftEyelidWeight + RightEyelidWeight) / 2`, the single blink-state scalar the data transmitter streams.

  These are the raw per-frame weights, exposed so the data sink can analyse blink onset/offset offline. They are deliberately independent of the internal `EyesAreClosedThreshold` gate: that gate decides whether this script updates the gaze for the current frame, but the sink wants the *weights* in every case — a closed eye is a meaningful signal, not a dropout, so the weights are recorded even on a closed-eye frame (≈1). While `Update()` bails early on an invalid frame (no valid eye gaze, low confidence, or invalid face data), the two fields **keep their last valid value** rather than collapsing to `0`; an invalid streak therefore reads as a flat line in the stream, not a spurious "eyes wide open" jump.

## Method of operation

This script is loosely based on `Library\PackageCache\com.meta.xr.sdk.core@*\Scripts\Movement\OVREyeGaze.cs`, so feel free to check that file out. I added a lot of simplifications.

At startup, and awake, the script checks the eye and face tracking permissions **separately** (logging the state of each), requests only the ones that are still missing, and then — because the OVR Manager may have already granted them and the `PermissionGranted` event is not guaranteed to reach us (see the `OVRPermissionsRequester` remarks) — it also starts the eye tracker directly. Face tracking is **not** started here: the `OVRFaceExpressions` component starts it itself on enable and only needs the permission to be granted. If a permission is granted later, the `PermissionGranted` callback fires and starts the eye tracker as a fallback (no-op if it already started).

Then, at every frame:

* Fetch the eye gazes for each eye

* We do some sanity check, such as:
  * Do we have a valid eye gaze? (`eye_gaze_right.IsValid`? Is `.Confidence` above `EyeTrackingConfidenceThreshold`?)
    * If not, then don't waste more time in this frame, return without doing anything.
  * Do we have a valid face expression? (`faceExpressions != null && faceExpressions.ValidExpressions`)
    * If not, then don't waste more time in this frame, return without doing anything.
  * Are the eyes actually open in this frame (is `faceExpressions.GetWeight(OVRFaceExpressions.FaceExpression.EyesClosed<L/R>)` above `EyesAreClosedThreshold`)
    * If the eyes are not open in this frame, then return without doing anything — but only after recording the weights (see the note below).

  Note: the two eyelid weights are read and assigned to the public fields `LeftEyelidWeight` / `RightEyelidWeight` immediately **before** this gate, so a closed eye **is** captured in the streamed data; only the gaze-vector update for that frame is skipped.

At this point, we have the eye tracker data, but the numbers are in a weird format - they are for character animation.

* We convert the eye tracker data `ToHeadSpacePose` for both eyes.
* From this data, we get the rotations out. We create a 'gaze rotation' by Slerp-ing the two quaternions, with the interpolation factor of 0.5.
This way, we have a single rotation value that is the 'mean' between the two rotations.
* Get the gaze direction vector in head space — `gazeRotation * Vector3.forward` (same convention as the Meta reference `OVREyeGaze`: the pose's +Z points along the look direction; so +X = right, +Y = up, +Z = straight ahead).
* Derive the raw Azimuth and Elevation values in degrees from that direction vector via `atan2` — azimuth positive = right, elevation positive = up, both already in [-180, 180]. (An earlier version decomposed the rotation with `eulerAngles`, which returns every component in [0, 360) and is roll-ambiguous; that reported sign-inverted azimuths and 0..360 elevations, and its transform signs compensated for that only on the visual path, leaving the reported data — and the offset direction — inverted with respect to the convention above. The direction-vector version fixes the data, the offset, and the visuals in one convention.) Add the offsets from `GazeVectorOffset`, and assign the calibrated pair to `CurrentGazeVector`.
* Rebuild a quaternion from the calibrated [-Elevation, Azimuth] with **zero roll** (2-DOF head-space retinal frame; torsion is not calibratable anyway and roll only adds noise), and apply it as `transform.localRotation`. Then apply the SAME rotation to the live local offset (`scotomaLocalOffset`, seeded in `Awake`, e.g. (0, 0, 0.5)) and assign it to `transform.localPosition` — this sweeps the whole subtree around `CenterEyeAnchor` on a sphere of radius `Z` at the calibrated gaze point, so children stay locked to the true fovea, always facing the observer at constant viewing distance (no accommodation-vergence conflict, no binocular doubling).

When the component is disabled or destroyed:

* `OnDisable` (paired with `OnEnable`) stops the eye tracker (`OVRPlugin.StopEyeTracking`) once the last active instance goes away, so the tracker won't stay on.
* `OnDestroy` detaches the permission callbacks. (Permissions themselves are never revoked; Android remembers the grant - that is the desired behaviour so the next run can start tracking immediately.)
