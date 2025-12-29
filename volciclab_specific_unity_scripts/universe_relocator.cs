using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;
using UnityEngine.UIElements;
using UnityEngine.XR;

public class universeRelocatorScript : MonoBehaviour
{
    public bool requestUpdateNow = false;
    public Vector3 CoordinateOffsetsInUnitySpace = new Vector3(0f, 0f, 0f);
    public int requestControlPort = 7510;
    public string streamerServerAddress = "192.168.42.5";
    public int streamerServerPort = 64923;
    public int clientListeningPort = 54622;
    public bool useRandomListeningPortForEachInstanceOfThisScript = true;
    public int decimation = 1;
    public bool swapYAndZCoordinates = true;
    public int rigidBodyIDYouWantToTrack = 9998;



    /*
     * READ THIS PLEASE:
     * 
     * This script is to update the postion/orientation of a gameObject on a request basis.
     * While this has a request control port, the process can also be initiated with the
     * the global variable requestUpdateNow.
     * 
     * Then, the code sends a request to the streaming server, receives a packet from it,
     * and updates the coordinates accordingly.
     * 
     * WARNING:
     * 
     * THIS CODE BLOCKS EXECUTION. YOU MUST MAKE SURE THAT:
     *  - The 'streaming server thingy' is running
     *      - (and that it accepts packets, and say the firewall doesn't block it)
     *  - The rigid body you are requesting exists in Motive
     *  - Blocking execution would not cause user discomfort
     *      - (particularly in a VR application)
     */

    // requestor's network interface
    private UdpClient requestor_udp_client = new UdpClient();
    // This will be where the streamer sends data to
    private UdpClient streaming_udp_client = new UdpClient();
    // For future implementation, when there will be a command packet.
    private string received_control_word_as_string;


    // Shuffle random number generator for random listening port
    private System.Random rng = new System.Random();

    
    // This is for the tracker.
    private XRInputSubsystem xr_input;

   
    
    // We need these for the final sanity check
    private float final_coordinate_error; // Cartesian offset from reality
    private float final_angular_error; // Vertical rotation difference from relaity
    private UInt64 alignment_attempt_counter;

    void Start()
    {

        // set culture info. This is required for the decimal formatting.
        Thread.CurrentThread.CurrentCulture = new CultureInfo("en-US");
        Thread.CurrentThread.CurrentUICulture = new CultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentCulture = new CultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo("en-US");

        // Get the active XR Input Subsystem
        List<XRInputSubsystem> subsystems = new List<XRInputSubsystem>();
        SubsystemManager.GetSubsystems(subsystems);
        if (subsystems.Count > 0)
        {
            xr_input = subsystems[0];
        }

        /*
         * OpenXR input has a tracking mode setting.
         * https://docs.unity3d.com/6000.3/Documentation/ScriptReference/XR.TrackingOriginModeFlags.html
         * 
         * Unknown: either has no tracking or origin is not set
         * Unbounded: tracking is for some sort of a world anchor.
         */

        // Set the appropriate tracking mode.
        //xr_input.TrySetTrackingOriginMode(TrackingOriginModeFlags.Unknown);
        //xr_input.TrySetTrackingOriginMode(TrackingOriginModeFlags.TrackingReference);

        if (useRandomListeningPortForEachInstanceOfThisScript)
        {
            // If we got here, assign a new random number for the listening port.
            clientListeningPort = rng.Next(clientListeningPort, clientListeningPort + 200);
        }


        // Prepare async listener for the control port.

        IPEndPoint requestor_ip_endpoint = new IPEndPoint(IPAddress.Any, requestControlPort);
        requestor_udp_client = new UdpClient(requestor_ip_endpoint);

        // Start this as async callback.
        requestor_udp_client.BeginReceive(new AsyncCallback(receive_request_control), requestor_udp_client);

        // Start the task. No dice with this.
        //_ = receive_ansync_task(); // Start the listener task.


        
        Debug.Log("Control packet listener created.");

        streaming_udp_client = new UdpClient(clientListeningPort);

        // Added placeholder, so I would remember the message format.
        //received_streaming_data_as_string = "17361661920000;2501;0,0,0;0,0,0,1;NoDataReceivedYet\n";
    }

    // Update is called once per frame
    void Update()
    {
        if (requestUpdateNow)
        {
            /*
             * If we got here, we need to send a message to the streaming server,
             * update the parent coordinate system, and then request to stop streaming.
             */

        // Update the boolean. This will prevent this part from running again unless the async function sets it to true.
        requestUpdateNow = false;

            /*
             * Send the request to the server
             */

            // <rigid_body_id_in_motive>;<udp_port_to_stream_to>;<decimation>
            string request_to_send = string.Format("{0};{1};{2}", rigidBodyIDYouWantToTrack, clientListeningPort, decimation);
            Debug.LogFormat("Sending _request_ to {0}:{1} payload: {2}", streamerServerAddress, streamerServerPort, request_to_send);

            Byte[] payload_to_send = Encoding.ASCII.GetBytes(request_to_send);


            // No try-catch here, I want this to fail
            streaming_udp_client.Send(payload_to_send, payload_to_send.Length, streamerServerAddress, streamerServerPort);


            // And now we do a blocking receive.

            // I am not sure if I have a choice here, not sure how much will this hit performance.
            IPEndPoint streamer_ip_endpoint = new IPEndPoint(IPAddress.Parse(streamerServerAddress), clientListeningPort);

            // If the render loop stalls, it will stall because of this:
            Byte[] received_payload = streaming_udp_client.Receive(ref streamer_ip_endpoint);

            string received_payload_as_string = Encoding.ASCII.GetString(received_payload);

            string[] separated_string = received_payload_as_string.Split(";");

            if (separated_string.Length != 5)
            {
                Debug.LogError("The number of fields when parsing the payload is not 5. Cannot continue.");
                return;
            }

            // If we made it this far, then we can dissect the string.

            uint rigid_body_id_extracted_from_separated_string = uint.Parse(separated_string[1]);

            if (rigid_body_id_extracted_from_separated_string == rigidBodyIDYouWantToTrack)
            {
                // If we have the correct one, then
                // Assemble request to stop streaming, and send it.
                request_to_send = string.Format("{0};{1};0", rigidBodyIDYouWantToTrack, clientListeningPort); // Decimation is 0
                payload_to_send = Encoding.ASCII.GetBytes(request_to_send);
                streaming_udp_client.Send(payload_to_send, payload_to_send.Length, streamerServerAddress, streamerServerPort);



                // Extract the translation coordinates
                string[] translation_as_string = separated_string[2].Split(",");
                if (translation_as_string.Length != 3)
                {
                    Console.WriteLine("Something is wrong with the formatting of the translation coordinates, could not split it into numbers.");
                    return;
                }

                if (!float.TryParse(translation_as_string[0], out float translation_x))
                {
                    translation_x = float.NaN;
                }

                if (!float.TryParse(translation_as_string[1], out float translation_y))
                {
                    translation_y = float.NaN;
                }

                if (!float.TryParse(translation_as_string[2], out float translation_z))
                {
                    translation_z = float.NaN;
                }


                // Extract the orientation
                string[] quaternion_as_string = separated_string[3].Split(",");
                if (quaternion_as_string.Length != 4)
                {
                    Console.WriteLine("Something is wrong with the formatting of the quaternion, could not split it into numbers.");
                    return;
                }


                if (!float.TryParse(quaternion_as_string[0], out float quaternion_qx))
                {
                    quaternion_qx = float.NaN;
                }

                if (!float.TryParse(quaternion_as_string[1], out float quaternion_qy))
                {
                    quaternion_qy = float.NaN;
                }

                if (!float.TryParse(quaternion_as_string[2], out float quaternion_qz))
                {
                    quaternion_qz = float.NaN;
                }

                if (!float.TryParse(quaternion_as_string[3], out float quaternion_qw))
                {
                    quaternion_qz = float.NaN;
                }


                /*
                 * Just a bit of thinking here.
                 * - We know that the floor planes of the OptiTrack and the Quest are parallel.
                 * - The coordinate systems can therefore be offset in the cartesian coordinates, and
                 * - it may be rotated via the vertial axis.
                 * 
                 * The Camera rig is used for the cartesian alignment, and
                 * it also needs to be rotated as per the OptiTrack rigid body,
                 * around the origin.
                 * 
                 * Does the rotation must be independent from the transforms of TrackingSpace?
                 * (by calling TryRecenter(), the current position will be the origin)
                 */

                //transform.SetParent(null); // Go as high as possible.

                // Update the parent object's coordinates.
                if (swapYAndZCoordinates)
                {

                    // If we got here, we are swapping Y and Z.
                    Debug.LogFormat("Alignment: [POSITION] Inverting Y and Z axis from OptiTrack data source.");
                    // This is the rigod body, which physically corresponds with the headset position.
                    Vector3 position_from_optitrack = new UnityEngine.Vector3(translation_x, translation_z, translation_y);




                    Debug.LogFormat("Alignment: OptiTrack's cyclopean perspective: {0}", position_from_optitrack);

                    /*
                    // Try resetting the tracker.
                    if (!xr_input.TryRecenter())
                    {
                        Debug.Log("Alignment: Attempt to recenter unsuccessful.");
                    }
                    /*
                     * Got these from:
                     * https://discussions.unity.com/t/how-to-recenter-in-openxr/935209/9
                     * but they don't seem to work.
                     * 
                     * 
                    xr_input.TrySetTrackingOriginMode(TrackingOriginModeFlags.Floor);
                    xr_input.TrySetTrackingOriginMode(TrackingOriginModeFlags.Unknown);
                    */

                    // We need to eliminate the alignment error, which we do so in here by adjusting to the offset.


                    // From: https://developers.meta.com/horizon/documentation/unity/unity-ovrcamerarig/
                    //OVRManager.display.RecenterPose(); // Doesn't seem to do anything.


                    // This in the camera rig, with must be the child gameobject this script is running from.
                    GameObject center_eye_anchor = GameObject.Find("CenterEyeAnchor");



                    // This is where the headset is with respect to the tracker's origin
                    Vector3 eye_anchor_position = center_eye_anchor.transform.position;
                    Debug.LogFormat("Alignment: Eye anchor is at: {0}", eye_anchor_position);

                    
                    // This is where the tracker's orign is, with respect to Unity's coordinates.
                    Vector3 position_from_unity = transform.position;
                    Debug.LogFormat("Alignment: GameObject is currently at: {0}", position_from_unity);

                    // This is the difference between what the OptiTrack shows and what the eye anchor shows.
                    Vector3 offset_between_physically_corresponding_positions = position_from_optitrack + position_from_unity + CoordinateOffsetsInUnitySpace - eye_anchor_position;
                    Debug.LogFormat("Alignment: Difference from reality is: {0}", offset_between_physically_corresponding_positions);

                    // Move the GameObject, so that the eye anchor positon would correspond with the OptiTrack position.
                    transform.position = offset_between_physically_corresponding_positions;

                    Vector3 current_position_from_unity = transform.position;
                    Debug.LogFormat("Alignment: GameObject is now at: {0}", current_position_from_unity);

                    Debug.LogFormat("Alignment: Eye anchor is now at: {0}", center_eye_anchor.transform.position);

                    Vector3 final_offset_between_physically_corresponding_positions = current_position_from_unity - center_eye_anchor.transform.position;
                    Debug.LogFormat("Alignment: GameObject - Center Eye anchor offset: {0}", final_offset_between_physically_corresponding_positions);

                    Vector3 final_optitrack_eye_anchor_offset = position_from_optitrack - eye_anchor_position;
                    Debug.LogFormat("Alignment: OptiTrack - Cyclopean offset: {0}", final_optitrack_eye_anchor_offset);

                    final_coordinate_error = Vector3.Distance(position_from_optitrack, eye_anchor_position);
                    Debug.LogFormat("Alignment: OptiTrack - Cyclopean distance: {0}mm", final_coordinate_error * 1000f);


                    /*
                     * Coordinate system convention in Unity:
                     * https://docs.unity3d.com/6000.3/Documentation/Manual/QuaternionAndEulerRotationsInUnity.html
                     * 
                     * We use quaternions.
                     * 
                     */

                    Debug.LogFormat("Alignment: [ROTATION]");

                    // Get rotation from OptiTrack.
                    UnityEngine.Quaternion quaternion_from_optitrack = new UnityEngine.Quaternion(quaternion_qx, quaternion_qy, quaternion_qz, quaternion_qw); // Z is up.

                    // Set the rotation of the GameObject to 0, wherever it may be.
                    transform.rotation = new UnityEngine.Quaternion(0, 0, 0, 1); // Y is up.

                    // Get rotation from the headset itself
                    //GameObject headset_anchor = GameObject.Find("CenterEyeAnchor");
                    //Camera headset_camera = Camera.main;

                    UnityEngine.Quaternion quaternion_of_headset = center_eye_anchor.transform.rotation; // Y is up.
                    //UnityEngine.Quaternion quaternion_of_headset = headset_camera.transform.rotation; // Y is up.


                    //Debug.LogFormat("Alignment: OptiTrack quaternion: {0}", quaternion_from_optitrack);
                    //Debug.LogFormat("Alignment: GameObject quaternion: {0}", quaternion_of_gameobject);
                    //Debug.LogFormat("Alignment: Headset quaternion: {0}", quaternion_of_headset);



                    // We have the luxury of only worrying about the vertical axes.
                    // We also assume that the user is not doing anything silly, such as lying down or dangling from the truss.
                    float vertical_rotation_from_optitrack = quaternion_from_optitrack.eulerAngles.z;
                    // The OptiTrack rotation direction is opposite to what's in Unity.
                    vertical_rotation_from_optitrack = 360 - vertical_rotation_from_optitrack;
                    Debug.LogFormat("Alignment: Rotation from OptiTrack: {0}°.", vertical_rotation_from_optitrack);
                    
                    float vertical_rotation_from_headset = quaternion_of_headset.eulerAngles.y;
                    Debug.LogFormat("Alignment: Rotation from headset: {0}°.", vertical_rotation_from_headset);

                    

                    /*
                     * Because the angles can jump from 0° to/from 360°, or -180° to/from 180°, using the angles would not work.
                     * So, we have to calculate the rotations from quaternions, but only on the vertical axis.
                     * We know that the axes are rotating to the same direction, and only the vertical one matters.
                     */

                    UnityEngine.Quaternion optitrack_vertical_quaternion = UnityEngine.Quaternion.Euler(0, vertical_rotation_from_optitrack, 0);
                    UnityEngine.Quaternion headset_vertical_quaternion = UnityEngine.Quaternion.Euler(0, vertical_rotation_from_headset, 0);

                    UnityEngine.Quaternion difference_vertical_quaternion = optitrack_vertical_quaternion * UnityEngine.Quaternion.Inverse(headset_vertical_quaternion);

                    float difference_in_vertical_orientation = difference_vertical_quaternion.eulerAngles.y;
                    Debug.LogFormat("Alignment: Rotation difference: {0}°.", difference_in_vertical_orientation);

                    // Rotate the gameobject by the appropriate difference. With quaternions.

                    transform.rotation = difference_vertical_quaternion;

                    // Re-calculate all the positions.
                    UnityEngine.Quaternion quaternion_of_gameobject = transform.rotation;
                    quaternion_of_headset = center_eye_anchor.transform.rotation;
                    float vertical_rotation_from_gameobject = quaternion_of_gameobject.eulerAngles.y;
                    vertical_rotation_from_headset = quaternion_of_headset.eulerAngles.y;

                    Debug.LogFormat("Alignment: Updated GameObject rotation: {0}°.", vertical_rotation_from_gameobject);
                    Debug.LogFormat("Alignment: Updated headset rotation: {0}°.", vertical_rotation_from_headset);


                    final_angular_error = vertical_rotation_from_optitrack - vertical_rotation_from_headset;
                    Debug.LogFormat("Alignment: Final angular error: {0}°.", final_angular_error);

                    /* 
                     * Need to rotate the camera rig around the origin to align the two coordinate systems.
                     * https://docs.unity3d.com/2021.2/Documentation/ScriptReference/Transform.RotateAround.html
                     */

                    // The rotation angles are opposite.
                    //transform.RotateAround(Vector3.zero, Vector3.up, -1* vertical_rotation_from_optitrack);
                    //transform.RotateAround(transform.position, Vector3.up, -1*vertical_rotation_difference);


                    //Debug.LogFormat("angular offset was {0}", vertical_rotation_difference);

                    //UnityEngine.Quaternion vertical_rotation_added = UnityEngine.Quaternion.Euler(0, vertical_rotation_difference, 0);
                    //UnityEngine.Quaternion current_headset_orientation = transform.rotation;

                    // Now we update the rotation.
                    //UnityEngine.Quaternion quaternion_rotated = current_headset_orientation * vertical_rotation_added;

                    // ...and finally, we rotate around the origin.
                    //transform.rotation = quaternion_rotated;

                    //transform.rotation = vertical_rotation_added;




                    //Debug.LogFormat("Alignment complete: coordinates were off by {0}m, Coordinate offset is {1};{2};{3}; and the rotation difference was {4}°, {5}°, {6}°.", alignment_error, position_difference[0], position_difference[1], position_difference[2], rotation_differences[0], rotation_differences[1], rotation_differences[2]);

                    /*
                    // Do this with the rotations too
                    UnityEngine.Quaternion quaternion_with_axes_inverted = new UnityEngine.Quaternion(quaternion_qx, quaternion_qz, -quaternion_qy, quaternion_qw);

                    UnityEngine.Quaternion rotations_added = UnityEngine.Quaternion.Euler(-90, 0, 180);
                    //UnityEngine.Quaternion rotations_added = UnityEngine.Quaternion.Euler(0, 0, 0);

                    // Rotating quaternion is vectorial multiplication
                    UnityEngine.Quaternion quaternion_rotated = quaternion_with_axes_inverted * rotations_added;

                    // We need to rotate around the origin
                    //transform.rotation = quaternion_rotated;
                    */

                    // Change tracking mode, so we can manually set it.
                    //xr_input.TrySetTrackingOriginMode(TrackingOriginModeFlags.Unbounded);


                    // We reset the tracker.
                    //xr_input.TryRecenter();

                    // Increase the alignment counter
                    alignment_attempt_counter++;
                }
                else
                {

                    /*
                     * This is the same stuff as above, but without the agony of finding out how it worked.
                     * See the previous part of the if statement for comments.
                     * 
                     * The only difference is that the Y and the Z axes are not swapped.
                     * We don't use this in the lab, it's here out of courtesy.
                     */


                    // If we got here, we are swapping Y and Z.
                    Debug.LogFormat("Alignment: [POSITION] Inverting Y and Z axis from OptiTrack data source.");
                    // This is the rigod body, which physically corresponds with the headset position.
                    Vector3 position_from_optitrack = new UnityEngine.Vector3(translation_x, translation_y, translation_z);




                    Debug.LogFormat("Alignment: OptiTrack's cyclopean perspective: {0}", position_from_optitrack);

                    /*
                    // Try resetting the tracker.
                    if (!xr_input.TryRecenter())
                    {
                        Debug.Log("Alignment: Attempt to recenter unsuccessful.");
                    }
                    /*
                     * Got these from:
                     * https://discussions.unity.com/t/how-to-recenter-in-openxr/935209/9
                     * but they don't seem to work.
                     * 
                     * 
                    xr_input.TrySetTrackingOriginMode(TrackingOriginModeFlags.Floor);
                    xr_input.TrySetTrackingOriginMode(TrackingOriginModeFlags.Unknown);
                    */

                    // We need to eliminate the alignment error, which we do so in here by adjusting to the offset.


                    // From: https://developers.meta.com/horizon/documentation/unity/unity-ovrcamerarig/
                    //OVRManager.display.RecenterPose(); // Doesn't seem to do anything.


                    // This in the camera rig, with must be the child gameobject this script is running from.
                    GameObject center_eye_anchor = GameObject.Find("CenterEyeAnchor");



                    // This is where the headset is with respect to the tracker's origin
                    Vector3 eye_anchor_position = center_eye_anchor.transform.position;
                    Debug.LogFormat("Alignment: Eye anchor is at: {0}", eye_anchor_position);


                    // This is where the tracker's orign is, with respect to Unity's coordinates.
                    Vector3 position_from_unity = transform.position;
                    Debug.LogFormat("Alignment: GameObject is currently at: {0}", position_from_unity);

                    // This is the difference between what the OptiTrack shows and what the eye anchor shows.
                    Vector3 offset_between_physically_corresponding_positions = position_from_optitrack + position_from_unity + CoordinateOffsetsInUnitySpace - eye_anchor_position;
                    Debug.LogFormat("Alignment: Difference from reality is: {0}", offset_between_physically_corresponding_positions);

                    // Move the GameObject, so that the eye anchor positon would correspond with the OptiTrack position.
                    transform.position = offset_between_physically_corresponding_positions;

                    Vector3 current_position_from_unity = transform.position;
                    Debug.LogFormat("Alignment: GameObject is now at: {0}", current_position_from_unity);

                    Debug.LogFormat("Alignment: Eye anchor is now at: {0}", center_eye_anchor.transform.position);

                    Vector3 final_offset_between_physically_corresponding_positions = current_position_from_unity - center_eye_anchor.transform.position;
                    Debug.LogFormat("Alignment: GameObject - Center Eye anchor offset: {0}", final_offset_between_physically_corresponding_positions);

                    Vector3 final_optitrack_eye_anchor_offset = position_from_optitrack - eye_anchor_position;
                    Debug.LogFormat("Alignment: OptiTrack - Cyclopean offset: {0}", final_optitrack_eye_anchor_offset);

                    final_coordinate_error = Vector3.Distance(position_from_optitrack, eye_anchor_position);
                    Debug.LogFormat("Alignment: OptiTrack - Cyclopean distance: {0}mm", final_coordinate_error * 1000f);


                    /*
                     * Coordinate system convention in Unity:
                     * https://docs.unity3d.com/6000.3/Documentation/Manual/QuaternionAndEulerRotationsInUnity.html
                     * 
                     * We use quaternions.
                     * 
                     */

                    Debug.LogFormat("Alignment: [ROTATION]");

                    // Get rotation from OptiTrack.
                    UnityEngine.Quaternion quaternion_from_optitrack = new UnityEngine.Quaternion(quaternion_qx, quaternion_qy, quaternion_qz, quaternion_qw); // Y is up.

                    // Set the rotation of the GameObject to 0, wherever it may be.
                    transform.rotation = new UnityEngine.Quaternion(0, 0, 0, 1); // Y is up.

                    // Get rotation from the headset itself
                    //GameObject headset_anchor = GameObject.Find("CenterEyeAnchor");
                    //Camera headset_camera = Camera.main;

                    UnityEngine.Quaternion quaternion_of_headset = center_eye_anchor.transform.rotation; // Y is up.
                    //UnityEngine.Quaternion quaternion_of_headset = headset_camera.transform.rotation; // Y is up.


                    //Debug.LogFormat("Alignment: OptiTrack quaternion: {0}", quaternion_from_optitrack);
                    //Debug.LogFormat("Alignment: GameObject quaternion: {0}", quaternion_of_gameobject);
                    //Debug.LogFormat("Alignment: Headset quaternion: {0}", quaternion_of_headset);



                    // We have the luxury of only worrying about the vertical axes.
                    // We also assume that the user is not doing anything silly, such as lying down or dangling from the truss.
                    float vertical_rotation_from_optitrack = quaternion_from_optitrack.eulerAngles.y;
                    // The OptiTrack rotation direction is opposite to what's in Unity.
                    vertical_rotation_from_optitrack = 360 - vertical_rotation_from_optitrack;
                    Debug.LogFormat("Alignment: Rotation from OptiTrack: {0}°.", vertical_rotation_from_optitrack);

                    float vertical_rotation_from_headset = quaternion_of_headset.eulerAngles.y;
                    Debug.LogFormat("Alignment: Rotation from headset: {0}°.", vertical_rotation_from_headset);



                    /*
                     * Because the angles can jump from 0° to/from 360°, or -180° to/from 180°, using the angles would not work.
                     * So, we have to calculate the rotations from quaternions, but only on the vertical axis.
                     * We know that the axes are rotating to the same direction, and only the vertical one matters.
                     */

                    UnityEngine.Quaternion optitrack_vertical_quaternion = UnityEngine.Quaternion.Euler(0, vertical_rotation_from_optitrack, 0);
                    UnityEngine.Quaternion headset_vertical_quaternion = UnityEngine.Quaternion.Euler(0, vertical_rotation_from_headset, 0);

                    UnityEngine.Quaternion difference_vertical_quaternion = optitrack_vertical_quaternion * UnityEngine.Quaternion.Inverse(headset_vertical_quaternion);

                    float difference_in_vertical_orientation = difference_vertical_quaternion.eulerAngles.y;
                    Debug.LogFormat("Alignment: Rotation difference: {0}°.", difference_in_vertical_orientation);

                    // Rotate the gameobject by the appropriate difference. With quaternions.

                    transform.rotation = difference_vertical_quaternion;

                    // Re-calculate all the positions.
                    UnityEngine.Quaternion quaternion_of_gameobject = transform.rotation;
                    quaternion_of_headset = center_eye_anchor.transform.rotation;
                    float vertical_rotation_from_gameobject = quaternion_of_gameobject.eulerAngles.y;
                    vertical_rotation_from_headset = quaternion_of_headset.eulerAngles.y;

                    Debug.LogFormat("Alignment: Updated GameObject rotation: {0}°.", vertical_rotation_from_gameobject);
                    Debug.LogFormat("Alignment: Updated headset rotation: {0}°.", vertical_rotation_from_headset);


                    final_angular_error = vertical_rotation_from_optitrack - vertical_rotation_from_headset;
                    Debug.LogFormat("Alignment: Final angular error: {0}°.", final_angular_error);

                    
                    // Increase the alignment counter
                    alignment_attempt_counter++;

                }


                
                


            }



            /*
             * Sanity checks, to make sure we are aligned.
             * 
             * Check if the coordinates are more than 5 mm off.
             *
             * -and-
             *
             * Check whether the angles correspond (i.e. more than half a degree)
             * 
             * If any of these fail, repeat the entire process.
             * 
             */

            if (final_coordinate_error > 0.005f || final_angular_error > 0.5)
            {
                Debug.LogFormat("Alignment: Final alignment error is too large ({0}mm, {1}°), repeating the whole process.", final_coordinate_error * 1000f, final_angular_error);
                requestUpdateNow = true;
                Debug.LogFormat("Alignment: This was attempt #{0}.", alignment_attempt_counter);
            } else
            {
                Debug.LogFormat("Alignment: Number of alignment interations: {0}", alignment_attempt_counter);
                // It was successful, so reset the counter.
                alignment_attempt_counter = 0;
            }

            

            

        }

    }

    private void OnApplicationQuit()
    {
        /*
         *If we got here, all the update is done.Stop streaming and clean up.
         */

        // Assemble request to stop streaming, and send it.
        string request_to_send = string.Format("{0};{1};0", rigidBodyIDYouWantToTrack, clientListeningPort); // Decimation is 0
        byte[] payload_to_send = Encoding.ASCII.GetBytes(request_to_send);
        streaming_udp_client.Send(payload_to_send, payload_to_send.Length, streamerServerAddress, streamerServerPort);
        requestor_udp_client.Close();
    }

    private void OnDestroy()
    {
        /*
         *If we got here, all the update is done.Stop streaming and clean up.
         */

        // Assemble request to stop streaming, and send it.
        string request_to_send = string.Format("{0};{1};0", rigidBodyIDYouWantToTrack, clientListeningPort); // Decimation is 0
        byte[] payload_to_send = Encoding.ASCII.GetBytes(request_to_send);
        streaming_udp_client.Send(payload_to_send, payload_to_send.Length, streamerServerAddress, streamerServerPort);
        requestor_udp_client.Close();
    }


    // This function gets executed when there is a packet in the buffer.
    // Original code: https://yal.cc/cs-dotnet-asynchronous-udp-example/
    void receive_request_control(IAsyncResult result)
    {
        Debug.Log("receive_request_control function called.\n");

        UdpClient socket = result.AsyncState as UdpClient; // set the client to be asynchronous maybe?

        IPEndPoint request_ip_endpoint = new IPEndPoint(IPAddress.Any, requestControlPort); // Accept packets only from the server.

        // Apparently there is a bug in Unity, see, from 2010. Hopefully fixed. :)
        // https://discussions.unity.com/t/udp-receive-problems-v2-beginreceive-and-endreceive/410064

        

        byte[] received_control_word = socket.EndReceive(result, ref request_ip_endpoint);

        //socket.EndReceive(result, ref request_ip_endpoint);

        received_control_word_as_string = Encoding.ASCII.GetString(received_control_word);

        requestUpdateNow = true;

        // Once the transfer is done, restart the receive process again.
        socket.BeginReceive(new AsyncCallback(receive_request_control), socket);

    }

}
