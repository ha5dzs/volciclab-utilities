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
using System.Globalization;
using System.Net.Sockets;
using System.Threading;
using System.Text;
using System.Net;
using System;
using UnityEngine;
using Meta.XR.Util;
using static OVRPlugin;


/// <summary>
/// The eye tracker doesn't support vergence.
/// This class SLERPS the two eye direction data
/// and calculates azimuth/elevation with respect to the head.
/// Then, it transmits it over the network
/// in a nicely formatted UDP packet.
/// </summary>

public class gazeVectorTransmitter : MonoBehaviour
{
    /// <summary>
    /// True if eye tracking is enabled, otherwise false.
    /// </summary>
    public bool EyeTrackingEnabled => OVRPlugin.eyeTrackingEnabled;

    /// <summary>
    /// IP Address on local network
    /// </summary>
    public string whereToTransmit = "192.168.54.1";

    /// <summary>
    /// UDP Port on local network
    /// </summary>
    public int portToTransmit = 35644;

    /// <summary>
    /// Send data every N frames
    /// </summary>
    public int decimation = 15;


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


    // Create the socket interface
    UdpClient udpClient;

    string formatted_angles;
    private Byte[] payload_to_send;

    // Eye tracking transform to be modified.
    private Quaternion _initialRotationOffset;
    private Transform _viewTransform;

    private void Awake()
    {
        _onPermissionGranted = OnPermissionGranted;
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


    private void PrepareHeadDirection()
    {
        string transformName = "HeadLookAtDirection";

        _viewTransform = new GameObject(transformName).transform;

        _viewTransform.parent = transform.parent;

        _initialRotationOffset = Quaternion.Inverse(_viewTransform.rotation) * transform.rotation;
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


    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        PrepareHeadDirection();
        StartEyeTracking();

        // Change the culture info to generic ASCII. Decimal point is dot.
        Thread.CurrentThread.CurrentCulture = new CultureInfo("en-US");
        Thread.CurrentThread.CurrentUICulture = new CultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentCulture = new CultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo("en-US");

        // Open the UDP client at the selected port
        udpClient = new UdpClient(portToTransmit);


    }

    // Update is called once per frame
    void Update()
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



        // Now,extract Azimuth and Elevation from the look directions. These are in radians.

        var azimuth = lookDirection[0]; // left-right rotation along the Y axis (up)
        var elevation = lookDirection[1]; // up-down rotation along the X axis (right)


        if ((Time.frameCount % decimation) == 0)
        {
            //Debug.Log("gazeVectorTransmitter.Update(): It is time to send data.");
            // If it's time to send the packet, create it

            Debug.Log(Confidence);

            //if (Confidence > EyeTrackingConfidenceThreshold) // This doesn't work for some reason.
            if(true)
            {
                formatted_angles = string.Format("{0:000} deg / {1:000} deg", azimuth * Mathf.Rad2Deg, elevation * Mathf.Rad2Deg);
                Debug.Log(formatted_angles);
                payload_to_send = Encoding.ASCII.GetBytes(formatted_angles);
                // We know that we are statically formatted. So, we can manually insert characters.
              
            }
            else
            {
                formatted_angles = string.Format("No eyes detected.");
                payload_to_send = Encoding.ASCII.GetBytes(formatted_angles);
            }
            
            udpClient.Send(payload_to_send, payload_to_send.Length, whereToTransmit, portToTransmit);

        }

        //transform.position = pose.position;

        // This is the actual rotation
        //transform.rotation = targetRotation * _initialRotationOffset;


        // Added this to see how it impacts performance.
        string timestamp = unix_time_now_in_ms();

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

}
