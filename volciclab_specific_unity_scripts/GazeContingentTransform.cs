/*
 * This is a modification of the OVREyeGaze script.
 * It modifies the transform of the parent object so that
 * it would move on a spherical surface, instead of a plane.
 * I had a couple of things simplified to suit our needs.
 * 
 * Just in case:
 * Copyright (c) Zoltan Derzsi - zd8 <at> nyu <dot> edu
 * Copyright (c) Meta Platforms, Inc. and affiliates.
 * 
 * ...and to track who wrote what:
 * function_name() is written by me
 * FunctionName() is from Meta.
 */


using UnityEngine;
using System;
using Meta.XR.Util;
using static OVRPlugin;

/// <summary>
/// This class modifies the game object's transform and rotation so it would always be
/// in the same distance from the eye.
/// </summary>
/// <remarks>
/// See <see cref="OVRPlugin.EyeGazeState"/> structure for list of eye state parameters.
/// </remarks>
[HelpURL("https://developer.oculus.com/documentation/unity/move-eye-tracking/")]
public class GazeContingentTransform : MonoBehaviour
{

    /// <summary>
    /// True if eye tracking is enabled, otherwise false.
    /// </summary>
    public bool EyeTrackingEnabled => OVRPlugin.eyeTrackingEnabled;


    /// <summary>
    /// A confidence value ranging from 0..1 indicating the reliability of the eye tracking data.
    /// </summary>
    public float Confidence { get; private set; }
    // <summary>
    /// Too high: will skip tracking. Too low: will be noisy.
    /// </summary>
    [Range(0f, 1f)]
    public float EyeTrackingConfidenceThreshold = 0.5f;


    private OVRPlugin.EyeGazesState _currentEyeGazesState;



    // This is for requesting eye tracking permission request.
    private const OVRPermissionsRequester.Permission EyeTrackingPermission = OVRPermissionsRequester.Permission.EyeTracking;
    private Action<string> _onPermissionGranted;
    private static int _trackingInstanceCount;


    // Eye tracking transform to be modified.
    private Quaternion _initialRotationOffset;
    private Transform _viewTransform;




    private void Awake()
    {
        _onPermissionGranted = OnPermissionGranted;
    }


    // Start is called once before the first execution of Update after the MonoBehaviour is created
    private void Start()
    {
        PrepareHeadDirection();
    }


    private void OnEnable()
    {
        _trackingInstanceCount++;

        if (!StartEyeTracking())
        {
            enabled = false;
        }
    }



    // Update is called once per frame
    private void Update()
    {
        /* Sanity checks. */

        // Is the eye tracker working?
        if (!OVRPlugin.GetEyeGazesState(OVRPlugin.Step.Render, -1, ref _currentEyeGazesState))
            return;


        /*
         * Not sure what Meta is doing here, but this tracker does not track vergence
         * So, there is no point in separating the two eye's data.
         * But, to reduce noise, I am taking both data into account, and work from there.
         */
        
        var eye_gaze_left = _currentEyeGazesState.EyeGazes[(int)OVRPlugin.Eye.Left];
        var eye_gaze_right = _currentEyeGazesState.EyeGazes[(int)OVRPlugin.Eye.Right];

        // Is the eye tracker returning valid information? If not, then just don't do anything.
        if (!eye_gaze_left.IsValid || !eye_gaze_right.IsValid)
            return;

        // Is the eye tracking data above the confindence threshold that is required by the user?
        Confidence = (eye_gaze_left.Confidence + eye_gaze_right.Confidence) / 2;
        if (Confidence < EyeTrackingConfidenceThreshold)
            return;

        // This is one of the 'simplifications'. We track relative to the head.
        var eye_pose_left = eye_gaze_left.Pose.ToOVRPose();
        eye_pose_left = eye_pose_left.ToHeadSpacePose();
        var eye_pose_right = eye_gaze_right.Pose.ToOVRPose();
        eye_pose_right = eye_pose_right.ToHeadSpacePose();

        /*
         * Apply transform and rotation.
         * I moved the contents of CalculateEyeRotation() here,
         * so I can save the bits to a file later-on.
         */

        var eye_in_world_space_left = _viewTransform.rotation * eye_pose_left.orientation;
        var eye_in_world_space_right = _viewTransform.rotation * eye_pose_right.orientation;

       
        var eye_in_world_space_cyclopean = Quaternion.Slerp(eye_in_world_space_left, eye_in_world_space_right, 0.5f);

        var lookDirection = eye_in_world_space_cyclopean * Vector3.forward;
        var targetRotation = Quaternion.LookRotation(lookDirection, _viewTransform.up);


        //transform.position = pose.position;
        transform.rotation = targetRotation * _initialRotationOffset;

        // Added this to see how it impacts performance.
        string timestamp = unix_time_now_in_ms();

    }

  
    private void OnDisable()
    {
        if (--_trackingInstanceCount == 0)
        {
            OVRPlugin.StopEyeTracking();
        }
    }

    private void OnDestroy()
    {
        OVRPermissionsRequester.PermissionGranted -= _onPermissionGranted;
    }

    private Quaternion CalculateEyeRotation(Quaternion eyeRotation)
    {
        var eyeRotationWorldSpace = _viewTransform.rotation * eyeRotation;
        var lookDirection = eyeRotationWorldSpace * Vector3.forward;
        var targetRotation = Quaternion.LookRotation(lookDirection, _viewTransform.up);

        return targetRotation * _initialRotationOffset;
    }



    private void PrepareHeadDirection()
    {
        string transformName = "HeadLookAtDirection";

        _viewTransform = new GameObject(transformName).transform;

        _viewTransform.parent = transform.parent;

        _initialRotationOffset = Quaternion.Inverse(_viewTransform.rotation) * transform.rotation;
    }


    private void OnPermissionGranted(string permissionId)
    {
        if (permissionId == OVRPermissionsRequester.GetPermissionId(EyeTrackingPermission))
        {
            OVRPermissionsRequester.PermissionGranted -= _onPermissionGranted;
            enabled = true;
        }
    }

    private bool StartEyeTracking()
    {
        if (!OVRPermissionsRequester.IsPermissionGranted(EyeTrackingPermission))
        {
            OVRPermissionsRequester.PermissionGranted -= _onPermissionGranted;
            OVRPermissionsRequester.PermissionGranted += _onPermissionGranted;
            return false;
        }

        if (!OVRPlugin.StartEyeTracking())
        {
            Debug.LogWarning($"[{nameof(OVREyeGaze)}] Failed to start eye tracking.");
            return false;
        }

        return true;
    }

    /// <summary>
    /// Which eye to track?
    /// </summary>
    public enum EyeId
    {
        Left = OVRPlugin.Eye.Left,
        Right = OVRPlugin.Eye.Right
    }

    /// <summary>
    /// Unix time now, in milliseconds.
    /// </summary>
    /// <returns></returns>
    private string unix_time_now_in_ms()
    {
        // Get the UTC time now
        DateTimeOffset time_now = DateTimeOffset.UtcNow;
        // Convert it to milliseconds
        long the_time = time_now.ToUnixTimeMilliseconds();
        // Format it into a string
        return the_time.ToString();
    }


}
