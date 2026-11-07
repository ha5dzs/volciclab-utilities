using System.Collections;
using System.Collections.Generic;
using UnityEngine;
using System.Net.Sockets;
using System.Net;
using System;
using System.Text;
using System.Net.Cache;
using System.Globalization;
using System.Collections.ObjectModel;
using System.Numerics;
using JetBrains.Annotations;
using System.Net.NetworkInformation;
using System.Reflection;
using System.Threading;

/*
 * StreamRigidBodyFromOptitrack
 *
 * Client for the lab's custom OptiTrack/Motive streaming server (the same plain-ASCII UDP
 * server UnityOptitrackAlignment talks to). Attach ONE instance = track ONE rigid body in
 * Motive: it subscribes to that rigid body's 6-DOF pose (position in metres + orientation as
 * a quaternion) and mirrors it onto this GameObject's transform every frame. It writes only
 * its own transform, so hang the visual (marker mesh, fingertip sphere, ...) under it as a
 * child and it follows.
 *
 * Wire protocol (plain-ASCII, ';' separated; a shared contract with the Matlab client and,
 * historically, verified against the lab Wireshark capture). The origin and invariants -
 * including the "never run with the streaming server down" rule - live in the sibling
 * UnityOptitrackAlignment.cs.md. Kept here only for the cross-reference to resolve.
 *   subscribe (us -> server) : <rigid_body_id>;<listening_port>;<decimation>
 *   stream    (server -> us) : <unix_ms>;<rigid_body_id>;<x,y,z>;<qx,qy,qz,qw>;<name>\n
 *   stop      (us -> server) : <rigid_body_id>;<listening_port>;0     (decimation 0 = stop)
 *
 * Multi-instance rule (why its history is written down): one instance per GameObject, and
 * NO cross-instance static state. The receive buffer is a per-instance field (it used to be
 * `static`, which made N attached objects update round-robin - see the sibling
 * StreamRigidBodyFromOptitrack.cs.md). Each instance owns its own UdpClient and its own
 * listening port.
 *
 * Full design notes (axis/rotation conventions, the 3-4-rigid-body port footgun, threading
 * notes) are in the sibling StreamRigidBodyFromOptitrack.cs.md - read it before touching this.
 */
public class StreamRigidBodyFromOptitrack : MonoBehaviour
{

    /*
     * Public variables.
     * These can be adjusted from the Unity GUI as well.
     */
    public string streamerServerAddress = "192.168.42.5";
    public int streamerServerPort = 64923;
    public int listeningPort = 54622;
    public bool useRandomListeningPortForEachInstanceOfThisScript = true;
    public int decimation = 3;
    public bool swapYandZCoordinates = true;
    public int rigidBodyIDYouWantToTrack = 2501;
    public bool useRigidBodyNameAsGameObjectName = false;


    // One UdpClient per instance (NOT shared). Each instance binds its own listening port
    // (Start()), subscribes its own rigid body to it, and re-arms its own BeginReceive chain
    // inside receive_message(). The name is a leftover from before the multi-instance fix;
    // what matters now is the invariant - per-instance state, no statics.
    private UdpClient udp_client_you_may_rename_this = new UdpClient();


    private System.Random rng = new System.Random();
    private bool game_object_name_updated = false; //so we don't have to rename the game object every frame.

    // This will be written to using receive_message(). But, in case nothing comes in, this is
    // a placeholder so parsing has a well-formed shape before the first packet.
    // (Was `static`: ONE shared receive buffer for ALL attached instances, so with several
    // objects attached, interleaved streams corrupted each other (the "1st object every frame,
    // 2nd every other frame..." round-robin choppiness). receive_message() is an instance
    // method, so a per-instance field is exactly right.)
    private string received_payload_as_string;

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {
        // set culture info. This is required for the decimal formatting.
        Thread.CurrentThread.CurrentCulture = new CultureInfo("en-US");
        Thread.CurrentThread.CurrentUICulture = new CultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentCulture = new CultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo("en-US");

        if (useRandomListeningPortForEachInstanceOfThisScript)
        {
            // If we got here, assign a new random number for the listening port.
            listeningPort = rng.Next(listeningPort, listeningPort + 200);
        }

        udp_client_you_may_rename_this = new UdpClient(listeningPort); // Change where we listen
        //udp_client.Client.ReceiveTimeout = 5000;


        /*
         * Send the request to the server
         */

        // <rigid_body_id_in_motive>;<udp_port_to_stream_to>;<decimation>
        string request_to_send = string.Format("{0};{1};{2}", rigidBodyIDYouWantToTrack, listeningPort, decimation);
        //Debug.Log("Sending request to {0}:{1} payload: {2}", streamerServerAddress, streamerServerPort, request_to_send);

        Byte[] payload_to_send = Encoding.ASCII.GetBytes(request_to_send);

        // No try-catch here, I want this to fail
        udp_client_you_may_rename_this.Send(payload_to_send, payload_to_send.Length, streamerServerAddress, streamerServerPort);

        // Create the asyncronous call to receive_message()
        udp_client_you_may_rename_this.BeginReceive(new AsyncCallback(receive_message), udp_client_you_may_rename_this);

        /*
         * I know you are lazy when it somes to reading docs, so here is a tl;dr for you:
         * - 17361661920000 is the unix time stamp, with millisecond precision. Not used here.
         * - 2501 is the rigid body ID. Valid values are 1 to 65535.
         * - 0, 0, 0, are the XYZ coordinates of the rigid body, in metres.
         * - 0, 0, 0, 1 are rotation, in QxQyQzQw quaternion format
         * - NoDataReceivedYet is the name of the object.
         * Fields are separated with a semicolon (;), and the end is terminated with a newline (0x0a, or \n)
         * 
         * This variable is overwritten by receive_message() whenever a packet comes in, and
         * the contents of this string are being parsed in Update(). 
         * 
         */
        received_payload_as_string = "17361661920000;2501;0,0,0;0,0,0,1;NoDataReceivedYet\n";
    }

    // Update is called once per frame
    void Update()
    {
        string[] separated_string = received_payload_as_string.Split(";");

        if (separated_string.Length != 5)
        {
            Debug.LogError("The number of fields when parsing the payload is not 5. Cannot continue.");
            return;
        }

        string rigid_body_name_string = separated_string[4].Replace("\n", string.Empty);

        uint rigid_body_id_extracted_from_separated_string = uint.Parse(separated_string[1]);

        if (rigid_body_id_extracted_from_separated_string == rigidBodyIDYouWantToTrack)
        {
            // If we have the correct one, then:

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
                // (Was a copy-paste of the line above assigning to quaternion_qz.)
                quaternion_qw = float.NaN;
            }

            if (!game_object_name_updated)
            {
                game_object_name_updated = true; // so we only get to this path here only once.

                // If required, update the game object name
                if (useRigidBodyNameAsGameObjectName)
                {
                    if (separated_string[4].Length != 0)
                    {
                        // If we got here, we have a rigid body name. Remove the \n bit
                        transform.name = separated_string[4].Replace("\n", String.Empty);

                    }
                    else
                    {
                        // If we got here, the name was not valid.
                        transform.name = "Unknown rigid body!";
                    }
                }
            }

            // Update the parent object's coordinates.
            if (swapYandZCoordinates)
            {
                // If we got here, we are swapping Y and Z.
                transform.position = new UnityEngine.Vector3(translation_x, translation_z, translation_y);

                // The OptiTrack rotation conventions are different, so are the angle offsets.


                /*
                 * Method 1: Convert to Euler, apply the inversions and rotations, then convert back to Quaternions
                 * We need to invert all rotations, subtract 90 degrees from the X rotation, and add 180 degrees to Z rotation.
                 
                    // Collect the converted floating-point numbers into a UnityEngine.Quaternion
                    UnityEngine.Quaternion quaternion_vector = new UnityEngine.Quaternion(quaternion_qx, quaternion_qz, quaternion_qy, quaternion_qw);
                    // Convert the quaternion into Euler angles. We use Unity's internal convention of rotation order.
                    UnityEngine.Vector3 euler_rotations = quaternion_vector.eulerAngles;
                    // Invert the axes and add rotation offsets where applicable. Units are degrees.
                    euler_rotations[0] = -1 * euler_rotations[0] + (-90); //x
                    euler_rotations[1] = -1 * euler_rotations[1] + 0; //y
                    euler_rotations[2] = -1 * euler_rotations[2] + 180; //z
                    // Assign the changed rotation to the quaternion
                    transform.rotation = UnityEngine.Quaternion.Euler(euler_rotations);

                 * This one works, but the rotations 'jump' at corner conditions (when a rotation is 90 degrees, i.e. the 'gimbal lock'.)
                 */

                /* Method 2:
                 * We need to invert all rotations, subtract 90 degrees from X rotation, and add 180 degrees to Z rotation.
                 * ...but for extra spice, done directly on the Quaternion.
                 */

                UnityEngine.Quaternion quaternion_with_axes_inverted = new UnityEngine.Quaternion(-quaternion_qx, -quaternion_qz, -quaternion_qy, quaternion_qw);

                UnityEngine.Quaternion rotations_added = UnityEngine.Quaternion.Euler(-90, 0, 180);
                //UnityEngine.Quaternion rotations_added = UnityEngine.Quaternion.Euler(0, 0, 0);

                // Rotating quaternion is vectorial multiplication
                UnityEngine.Quaternion quaternion_rotated = quaternion_with_axes_inverted * rotations_added;

                transform.rotation = quaternion_rotated;


            }
            else
            {
                // Proceed as normal
                transform.position = new UnityEngine.Vector3(translation_x, translation_y, translation_z);



                // TODO: Verify this with users that have a game-engine-like set-up. We don't have it here, so can't test it.

                UnityEngine.Quaternion quaternion_with_axes_inverted = new UnityEngine.Quaternion(-quaternion_qx, -quaternion_qy, -quaternion_qz, quaternion_qw);

                UnityEngine.Quaternion rotations_added = UnityEngine.Quaternion.Euler(-90, 180, 0);

                // Rotating quaternion is vectorial multiplication
                UnityEngine.Quaternion quaternion_rotated = quaternion_with_axes_inverted * rotations_added;

                transform.rotation = quaternion_rotated;
            }

            

        }

    }

    void OnDestroy()
    {

        // Effectively, this is the same as when we start the stream, but we set the decimation to 0.

        // <rigid_body_id_in_motive>;<udp_port_to_stream_to>;<decimation>
        string request_to_send = string.Format("{0};{1};0", rigidBodyIDYouWantToTrack, listeningPort);
        //Debug.Log("Sending request to {0}:{1} payload: {2}", streamerServerAddress, streamerServerPort, request_to_send);

        Byte[] payload_to_send = Encoding.ASCII.GetBytes(request_to_send);

        // No try-catch here, I want this to fail
        udp_client_you_may_rename_this.Send(payload_to_send, payload_to_send.Length, streamerServerAddress, streamerServerPort);


    }

    /*
     * This function calls OnDestroy(), when the application quits, to tell the server to stop streaming data.
     */
    void OnApplicationQuit()
    {
        // See above what this does.
        OnDestroy();

    }


    // This function gets executed when there is a packet in the buffer.
    // Original code: https://yal.cc/cs-dotnet-asynchronous-udp-example/
    void receive_message(IAsyncResult result)
    {
        UdpClient socket = result.AsyncState as UdpClient; // set the client to be asynchronous maybe?

        IPEndPoint ip_endpoint = new IPEndPoint(IPAddress.Parse(streamerServerAddress), streamerServerPort); // Accept packets only from the server.

        // Apparently there is a bug in Unity, see, from 2010. Hopefully fixed. :)
        // https://discussions.unity.com/t/udp-receive-problems-v2-beginreceive-and-endreceive/410064


        byte[] received_payload = socket.EndReceive(result, ref ip_endpoint);

        received_payload_as_string = Encoding.ASCII.GetString(received_payload);

        // Once the transfer is done, restart the receive process again.
        socket.BeginReceive(new AsyncCallback(receive_message), socket);

    }



}
