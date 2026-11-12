/*
 * This script is loosely based on the OVREyeGaze.cs script.
 * I properly butchered it.
 *
 * Just in case:
 * Copyright (c) Zoltan Derzsi - zd8 <at> nyu <dot> edu
 * Copyright (c) Meta Platforms, Inc. and affiliates.
 */

using System;
using UnityEngine;

public class GazeContingentTransform2 : MonoBehaviour
{
    // <summary>
    /// Too high: will skip tracking. Too low: will be noisy.
    /// </summary>
    [Range(0f, 1f)]
    public float EyeTrackingConfidenceThreshold = 0.95f;

    // <summary>
    /// Adjust this value to see when the system interprets the eyes being closed.
    /// </summary>
    [Range(0f, 1f)]
    public float EyesAreClosedThreshold = 0.3f;

    // <summary>
    /// This script updates this public variable. [Azimuth, Elevation] in degrees.
    /// </summary>
    public Vector2 CurrentGazeVector = new Vector2(0f, 0f);

    // <summary>
    /// If the eye tracker is off, adjust it here. [Azimuth, Elevation] in degrees.
    /// Applied to the data AND to the visuals, so the rendered scotoma always sits
    /// at the true (calibrated) fovea, not the raw measured one.
    /// </summary>
    public Vector2 GazeVectorOffset = new Vector2(0f, 0f);

    // <summary>
    /// Latest eyelid state, one per eye. 0 = fully open, 1 = fully closed (the
    /// OVRFaceExpressions EyesClosedL / EyesClosedR weights). Refreshed on every frame
    /// where face-expression data is valid; while the eye tracker is returning invalid
    /// data the fields keep the previous frame's value (the data sink reads the last
    /// good state - this is intended behaviour, not a bug). These two values feed
    /// the EyelidMean property below.
    /// </summary>
    public float LeftEyelidWeight = 0f;
    public float RightEyelidWeight = 0f;

    // <summary>
    /// Mean eyelid position across both eyes (0 = open, 1 = closed): the single scalar
    /// the data transmitter streams for blink state. Computed (not stored), so it can
    /// never drift out of sync with the two per-eye values.
    /// </summary>
    public float EyelidMean => (LeftEyelidWeight + RightEyelidWeight) * 0.5f;

    private OVRFaceExpressions faceExpressions;
    private OVRPlugin.EyeGazesState currentEyeGazesState;

    // The scotoma's "resting" offset from CenterEyeAnchor (e.g. (0, 0, 0.5) = ~50 cm ahead).
    // This is the radius that Update() sweeps around the eye each frame, so it is the single
    // knob for viewing distance / binocular disparity. Public and live (NOT a one-shot Awake
    // cache) so ExternalScotomaDistanceAdjuster (port 40004) and the Inspector can change it at
    // runtime without the value in use going stale.
    public Vector3 scotomaLocalOffset = new Vector3(0f, 0f, 0.5f);

    /*
     * Permissions are also requested on startup by the OVR Manager in Unity Editor, so we check them here too.
     * Note: when the OVR Manager already requested the permissions, the PermissionGranted event is not
     * guaranteed to fire (see OVRPermissionsRequester remarks), so we also start tracking directly in Start().
     * When the component is disabled we stop the eye tracker so it won't stay active.
     */
    private const OVRPermissionsRequester.Permission EyeTrackingPermission = OVRPermissionsRequester.Permission.EyeTracking;
    private const OVRPermissionsRequester.Permission FaceTrackingPermission = OVRPermissionsRequester.Permission.FaceTracking;
    private Action<string> _onEyeTrackingPermissionGranted;
    private Action<string> _onFaceTrackingPermissionGranted;
    private static int _trackingInstanceCount;
    private static bool _eyeTrackingStarted;

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        Debug.Log("[GazeContingentTransform2] Script loaded.");

        // Face expression component
        faceExpressions = FindAnyObjectByType<OVRFaceExpressions>();
        if (faceExpressions == null)
        {
            Debug.LogWarning("[GazeContingentTransform2] No OVRFaceExpressions component found in scene. Eye tracking will work, but blink detection is disabled.");
        }
        else
        {
            Debug.Log("[GazeContingentTransform2] OVR FaceExpression component found. Face tracking enabled: " + faceExpressions.FaceTrackingEnabled);
        }

        // Check each permission separately so the log tells us exactly what state we are in.
        bool eyeGranted = OVRPermissionsRequester.IsPermissionGranted(EyeTrackingPermission);
        bool faceGranted = OVRPermissionsRequester.IsPermissionGranted(FaceTrackingPermission);
        Debug.Log("[GazeContingentTransform2] Permissions: Eye Tracking = " + (eyeGranted ? "granted" : "NOT granted") + ", Face Tracking = " + (faceGranted ? "granted" : "NOT granted") + ".");

        // Request only the permissions that are still missing.
        RequestEyeTrackingPermission();
        RequestFaceTrackingPermission();

        // Start the trackers now as well: if the OVR Manager already handled the request, the
        // PermissionGranted event may never reach us (see the note above).
        TryStartEyeTracking();
        if (faceExpressions != null && faceExpressions.FaceTrackingEnabled)
        {
            Debug.Log("[GazeContingentTransform2] Face tracking is running (managed by OVRFaceExpressions).");
        }
    }

    // Awake is called when the headset is actually on.
    private void Awake()
    {
        // Seed the live offset from the authored position (e.g. (0, 0, 0.5)) before Update()
        // starts driving the transform. This vector is swept around CenterEyeAnchor by the
        // gaze rotation every frame, so the subtree orbits the eye at constant radius.
        // scotomaLocalOffset stays public/live and externally adjustable (port 40004).
        scotomaLocalOffset = transform.localPosition;

        _onEyeTrackingPermissionGranted = OnEyeTrackingPermissionGranted;
        _onFaceTrackingPermissionGranted = OnFaceTrackingPermissionGranted;
    }

    // Request the Eye Tracking permission if it is not granted yet
    private void RequestEyeTrackingPermission()
    {
        if (OVRPermissionsRequester.IsPermissionGranted(EyeTrackingPermission))
        {
            return;
        }

        Debug.Log("[GazeContingentTransform2] Requesting Eye Tracking permission...");
        OVRPermissionsRequester.PermissionGranted += _onEyeTrackingPermissionGranted;
        OVRPermissionsRequester.Request(new[] { EyeTrackingPermission });
    }

    // Request the Face Tracking permission if it is not granted yet
    private void RequestFaceTrackingPermission()
    {
        if (OVRPermissionsRequester.IsPermissionGranted(FaceTrackingPermission))
        {
            return;
        }

        Debug.Log("[GazeContingentTransform2] Requesting Face Tracking permission...");
        OVRPermissionsRequester.PermissionGranted += _onFaceTrackingPermissionGranted;
        OVRPermissionsRequester.Request(new[] { FaceTrackingPermission });
    }

    // Start the eye tracker if we are allowed to, without waiting for the PermissionGranted event.
    // Face tracking is started by the OVRFaceExpressions component itself, so it is left alone here.
    private void TryStartEyeTracking()
    {
        if (_eyeTrackingStarted)
        {
            return;
        }

        if (!OVRPlugin.initialized)
        {
            Debug.Log("[GazeContingentTransform2] Eye tracking not started: OVR Plugin not initialized (editor with no device?).");
            return;
        }

        if (!OVRPermissionsRequester.IsPermissionGranted(EyeTrackingPermission))
        {
            Debug.Log("[GazeContingentTransform2] Eye tracking not started yet: permission not granted, waiting for PermissionGranted.");
            return;
        }

        if (OVRPlugin.StartEyeTracking())
        {
            _eyeTrackingStarted = true;
            Debug.Log("[GazeContingentTransform2] Eye tracking started.");
        }
        else
        {
            Debug.LogWarning("[GazeContingentTransform2] Failed to start eye tracking.");
        }
    }

    // When eye tracking permission is granted, start eye tracking
    private void OnEyeTrackingPermissionGranted(string permissionId)
    {
        if (permissionId == OVRPermissionsRequester.GetPermissionId(EyeTrackingPermission))
        {
            Debug.Log("[GazeContingentTransform2] Eye tracking permission granted.");
            OVRPermissionsRequester.PermissionGranted -= _onEyeTrackingPermissionGranted;

            // Start eye tracking (no-op if already started in Start)
            TryStartEyeTracking();
        }
    }

    // When face tracking permission is granted, confirm it so the OVRFaceExpressions is not blocked.
    // The OVRFaceExpressions component starts face tracking itself (see OVRFaceExpressions.OnEnable)
    // and only needs the permission to be granted - so this handler has nothing to start here.
    private void OnFaceTrackingPermissionGranted(string permissionId)
    {
        if (permissionId == OVRPermissionsRequester.GetPermissionId(FaceTrackingPermission))
        {
            Debug.Log("[GazeContingentTransform2] Face tracking permission granted.");
            OVRPermissionsRequester.PermissionGranted -= _onFaceTrackingPermissionGranted;
        }
    }

    // Update is called once per frame
    void Update()
    {
        // Fetch eye gaze data for both eyes with head space pose information
        // Note: GetEyeGazesState takes (step, frameIndex, ref state) - returns bool for success
        if (!OVRPlugin.GetEyeGazesState(OVRPlugin.Step.Render, -1, ref currentEyeGazesState))
        {
            return;
        }

        // Sanity check: do we have valid eye gaze?
        var eyeGazeLeft = currentEyeGazesState.EyeGazes[(int)OVRPlugin.Eye.Left];
        var eyeGazeRight = currentEyeGazesState.EyeGazes[(int)OVRPlugin.Eye.Right];

        if (!eyeGazeLeft.IsValid || !eyeGazeRight.IsValid)
        {
            return;
        }

        // Check confidence threshold
        if (eyeGazeLeft.Confidence < EyeTrackingConfidenceThreshold ||
            eyeGazeRight.Confidence < EyeTrackingConfidenceThreshold)
        {
            return;
        }

        // Sanity check: do we have valid face expressions?
        if (faceExpressions == null || !faceExpressions.ValidExpressions)
        {
            return;
        }

        // Check if eyes are actually open using the correct enum values (EyesClosedL/EyesClosedR)
        var leftEyelidWeight = faceExpressions.GetWeight(OVRFaceExpressions.FaceExpression.EyesClosedL);
        var rightEyelidWeight = faceExpressions.GetWeight(OVRFaceExpressions.FaceExpression.EyesClosedR);

        // Expose the per-eye weights so the data transmitter can stream them (and their
        // mean). Assigned here - after the validity checks, before the eyes-closed gate -
        // so a blink IS recorded, but while the gaze sample is invalid the public fields
        // simply keep the previous frame's value (intended - the sink sees last good state).
        LeftEyelidWeight = leftEyelidWeight;
        RightEyelidWeight = rightEyelidWeight;

        // If either eye is above the threshold, consider it closed (weight > threshold means closed)
        if (leftEyelidWeight > EyesAreClosedThreshold || rightEyelidWeight > EyesAreClosedThreshold)
        {
            return;
        }

        // Convert to head space pose and get rotations
        // EyeGazeState.Pose is a Posef, convert it to OVRPose first
        var leftOvrPose = eyeGazeLeft.Pose.ToOVRPose();
        var rightOvrPose = eyeGazeRight.Pose.ToOVRPose();

        // Convert to head space pose
        var leftHeadSpace = leftOvrPose.ToHeadSpacePose();
        var rightHeadSpace = rightOvrPose.ToHeadSpacePose();

        // Create mean rotation by slerping the two quaternions with factor 0.5
        UnityEngine.Quaternion leftRotation = leftHeadSpace.orientation;
        UnityEngine.Quaternion rightRotation = rightHeadSpace.orientation;

        UnityEngine.Quaternion gazeRotation = UnityEngine.Quaternion.Slerp(leftRotation, rightRotation, 0.5f);

        // Convert the mean gaze orientation into head-space AZIMUTH / ELEVATION.
        // The eye pose points its +Z axis along the gaze (same convention as the
        // Meta reference OVREyeGaze: lookDirection = rotation * Vector3.forward),
        // so in head space the gaze direction is: +X right, +Y up, +Z straight ahead.
        // atan2'ing that direction vector (not eulerAngles, which returns [0, 360)
        // and is roll-ambiguous) yields the documented convention directly, already
        // in [-180, 180]: azimuth positive = looking right, elevation positive = looking up.
        Vector3 gazeDirection = gazeRotation * Vector3.forward;
        float azimuth = Mathf.Atan2(gazeDirection.x, gazeDirection.z) * Mathf.Rad2Deg;
        float elevation = Mathf.Atan2(gazeDirection.y,
            Mathf.Sqrt(gazeDirection.x * gazeDirection.x + gazeDirection.z * gazeDirection.z)) * Mathf.Rad2Deg;

        // Add the per-trial calibration offset: the calibrated pair below is what the
        // participant is truly looking at, and what we stream out as data.
        float calibratedAzimuth = azimuth + GazeVectorOffset.x;
        float calibratedElevation = elevation + GazeVectorOffset.y;

        // Normalize calibrated azimuth to [-180, 180] range
        if (calibratedAzimuth > 180f)
        {
            calibratedAzimuth -= 360f;
        }
        else if (calibratedAzimuth < -180f)
        {
            calibratedAzimuth += 360f;
        }

        // Assign the calibrated [Azimuth, Elevation] to CurrentGazeVector
        CurrentGazeVector = new Vector2(calibratedAzimuth, calibratedElevation);

        // Drive the visuals from the SAME calibrated angles the data reports, so the
        // rendered scotoma always sits on the true (calibrated) fovea. Rebuilt as pure
        // yaw+pitch (no roll): head-space 2-DOF retinal frame, upright by construction,
        // and any per-eye torsion/roll noise from the tracker is deliberately dropped.
        // Signs: a positive Y rotation swings +Z towards +X (right), and a positive X
        // rotation tips +Z towards -Y (down), so yaw = +azimuth, pitch = -elevation
        // is what makes calibratedGaze * Vector3.forward land on the (azimuth,
        // elevation) point. This also means an offset of +AZ deg moves the rendered
        // scotoma AZ deg to the right, matching the reported-data convention.
        Quaternion calibratedGaze = Quaternion.Euler(-calibratedElevation, calibratedAzimuth, 0f);

        // Rotate around CenterEyeAnchor (the eye), not around our own origin: the same
        // rotation that points local +Z along the gaze also sweeps the authored offset
        // (scotomaLocalOffset, e.g. (0, 0, 0.5)) around the eye, carrying the children.
        transform.localRotation = calibratedGaze;
        transform.localPosition = calibratedGaze * scotomaLocalOffset;
    }

    private void OnEnable()
    {
        _trackingInstanceCount++;
        TryStartEyeTracking();
    }

    private void OnDisable()
    {
        if (--_trackingInstanceCount <= 0)
        {
            _trackingInstanceCount = 0;
            _eyeTrackingStarted = false;
            if (OVRPlugin.initialized)
            {
                OVRPlugin.StopEyeTracking();
                Debug.Log("[GazeContingentTransform2] Eye tracking stopped.");
            }
        }
    }

    private void OnDestroy()
    {
        // Clean up permission handlers
        OVRPermissionsRequester.PermissionGranted -= _onEyeTrackingPermissionGranted;
        OVRPermissionsRequester.PermissionGranted -= _onFaceTrackingPermissionGranted;
    }

    // Additional method to apply gaze vector offset for calibration
    public void ApplyGazeOffset(Vector2 offset)
    {
        GazeVectorOffset = offset;
        Debug.Log($"[GazeContingentTransform2] Applied gaze offset: [{offset.x:F2}, {offset.y:F2}]");
    }

    // Method to reset offset (for initial calibration)
    public void ResetGazeOffset()
    {
        GazeVectorOffset = Vector2.zero;
        Debug.Log("[GazeContingentTransform2] Gaze offset reset to zero.");
    }
}
