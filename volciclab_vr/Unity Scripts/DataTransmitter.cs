using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

/*
 * DataTransmitter
 * ---------------
 * Streams a per-frame JSON telemetry record from the headset to the lab
 * UniversalDataSink over UDP. One record per (decimated) frame; the whole
 * payload is sized to fit a single Ethernet packet, so it is delivered
 * unfragmented.
 *
 * TWO CHANNELS
 * ------------
 * 1. Decimation control (WE RECEIVE, non-blocking): a single unsigned-integer
 *    payload on [decimationControlPort]:
 *        0     = stop streaming
 *        N > 0 = send one record every N rendered frames
 *    Read inside Update() with an Available guard, so the render loop never
 *    blocks on the network. This is the former port-1410 control channel,
 *    renumbered into the standard 40000+ block as 40008 (the next free port).
 *
 * 2. Telemetry (WE SEND, fire-and-forget): when decimation fires, build one
 *    PascalCase JSON record from live scene state and send it to
 *    [dataSinkIpAddress]:[dataSinkPort] (default 192.168.42.5:3431). The sink
 *    appends its own arrival timestamp and a newline; we send the record
 *    verbatim (NO trailing newline). No ack / no reply socket.
 *
 * TIMESTAMP
 * ---------
 * The in-payload capture time is NtpTimeSync.UnixTimeNowInMs() - the lab's
 * NTP-corrected, millisecond-precision UTC clock. If NtpTimeSync is absent or
 * not yet synced the value falls back to the RAW system clock (uncorrected);
 * we log a one-time warning when we notice that so it is not mistaken.
 * The sink's own leading column is a SEPARATE arrival/lab clock - the two are
 * deliberately different (capture time at the headset vs. arrival time at the
 * sink). Do not expect them to be equal.
 *
 * FIELD SOURCE MAP (the record, in order)
 * --------------------------------------
 *   TimestampMs          NtpTimeSync.UnixTimeNowInMs()
 *   HeadsetPosition      CenterEyeAnchor          .transform.position  (world, m)
 *   HeadsetRotation      CenterEyeAnchor          .transform.rotation  (world, quat)
 *   GazeVectorAzimuth    GazeContingentTransform2.CurrentGazeVector.x (deg)
 *   GazeVectorElevation  GazeContingentTransform2.CurrentGazeVector.y (deg)
 *   EyelidLeft           GazeContingentTransform2.LeftEyelidWeight    (0 open .. 1 closed)
 *   EyelidRight          GazeContingentTransform2.RightEyelidWeight   (0 open .. 1 closed)
 *   ScotomaEnabled       VisionStaticAnnulus.renderEnabled
 *   ScotomaInnerAngle    VisionStaticAnnulus.innerDiameter            (deg)
 *   ScotomaOuterAngle    VisionStaticAnnulus.outerDiameter            (deg)
 *   ScotomaDistance      Scotoma anchor pivot .localPosition.magnitude (m) == the D VisionStaticAnnulus uses
 *   ScotomaPosition      Scotoma                  .transform.position  (world, m)
 *   ScotomaRotation      Scotoma                  .transform.rotation  (world, quat)
 *
 * SIZE / PRECISION BUDGET (target: one Ethernet packet, keep the record < ~1400 bytes)
 * ------------------------------------------------------------------------------------
 *   coordinates (m)        3 decimals   (millimetre)
 *   viewing angles (deg)   1 decimal    (0.1 deg)
 *   quaternions            4 decimals   (1e-4)
 *   eyelids (2 x 0..1)     4 decimals each
 *   timestamp              integer Unix ms (13 digits)
 * A typical record is well under 450 bytes - ample headroom under the ~1400-byte
 * single-packet cap (MTU 1500 including overhead).
 *
 * ATTACH
 * ------
 * Attach to [BuildingBlock] Camera Rig (the root that the CenterEyeAnchor
 * subtree hangs from). See README § setup and AGENTS.md § scene tree.
 *
 * Sibling doc: DataTransmitter.cs.md
 */
public class DataTransmitter : MonoBehaviour
{
    [Header("Data sink (where the streamed records go)")]
    public string dataSinkIpAddress = "192.168.42.5"; // The lab UniversalDataSink host.
    public UInt16 dataSinkPort = 3431;

    [Header("Decimation control (the packet we receive)")]
    // Next free port of the lab block: 40000-40007 are taken by the control
    // scripts, so this data-path control channel takes 40008 (was 1410 before).
    public UInt16 decimationControlPort = 40008;

    // 0 = stop; N = send one record every N frames. Driven by the control packet.
    private UInt16 decimation = 0;
    private UInt64 frameCounter = 0;

    // Network handles.
    private UdpClient controlWordReceiver;
    private IPEndPoint controlWordEndPoint;
    private UdpClient dataSender;
    private IPEndPoint dataSinkEndPoint;

    // Cached scene references (all resolved once in Start; relative to this Camera Rig).
    private Transform centerEyeTransform;
    private Transform scotomaTransform;
    private GazeContingentTransform2 gaze;
    private VisionStaticAnnulus scotomaVision;

    // One-time warnings.
    private bool ntpNotSyncedWarned = false;
    private bool firstRecordLogged = false;

    void Start()
    {
        // Pin culture (en-US) for consistent decimal formatting in the payload.
        Thread.CurrentThread.CurrentCulture = new CultureInfo("en-US");
        Thread.CurrentThread.CurrentUICulture = new CultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentCulture = new CultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo("en-US");

        // Decimation-control socket: bind to our control port, readable from anywhere.
        // Bind failures throw; catch them so a port clash is a clean log, not a crash.
        try
        {
            controlWordReceiver = new UdpClient(decimationControlPort);
        }
        catch (Exception e)
        {
            Debug.LogError("[DataTransmitter] Could not bind UDP control port " + decimationControlPort + ": " + e.Message);
            controlWordReceiver = null;
        }
        if (controlWordReceiver != null)
        {
            controlWordEndPoint = new IPEndPoint(IPAddress.Any, decimationControlPort);
        }

        // Data-sink socket: ephemeral local port (the old code bound the sink PORT
        // locally too - pointless), explicit remote endpoint.
        dataSender = new UdpClient();
        try
        {
            dataSinkEndPoint = new IPEndPoint(IPAddress.Parse(dataSinkIpAddress), dataSinkPort);
        }
        catch (Exception e)
        {
            Debug.LogError("[DataTransmitter] Invalid dataSinkIpAddress '" + dataSinkIpAddress + "': " + e.Message);
            dataSinkEndPoint = null;
        }

        // Cache the scene references we read every frame.
        centerEyeTransform = transform.Find("TrackingSpace/CenterEyeAnchor");
        if (centerEyeTransform != null)
        {
            scotomaTransform = centerEyeTransform.Find("GazeContingentGameObject/Scotoma");
            if (scotomaTransform != null)
            {
                scotomaVision = scotomaTransform.GetComponent<VisionStaticAnnulus>();
                if (scotomaVision == null)
                {
                    Debug.LogWarning("[DataTransmitter] 'Scotoma' found but has no VisionStaticAnnulus component - the Scotoma* fields will be absent.");
                }
            }
            else
            {
                Debug.LogWarning("[DataTransmitter] Could not find 'TrackingSpace/CenterEyeAnchor/GazeContingentGameObject/Scotoma' under '" + transform.name + "'.");
            }
        }
        else
        {
            Debug.LogError("[DataTransmitter] Could not find 'TrackingSpace/CenterEyeAnchor' under '" + transform.name +
                "' - is DataTransmitter attached to the [BuildingBlock] Camera Rig?");
        }

        gaze = FindAnyObjectByType<GazeContingentTransform2>();
        if (gaze == null)
        {
            Debug.LogWarning("[DataTransmitter] No GazeContingentTransform2 component found in the scene - the gaze/eyelid fields will be absent.");
        }

        Debug.Log("[DataTransmitter] Listening for decimation packets on UDP " + decimationControlPort + " (0 = stop, N = send every N frames).");
        Debug.Log("[DataTransmitter] Will stream JSON records to " + dataSinkIpAddress + ":" + dataSinkPort + ".");
        LogReadiness();
    }

    // A single clear "are we ready to send?" line so a miswired scene is obvious.
    void LogReadiness()
    {
        bool ok = centerEyeTransform != null
                 && scotomaTransform != null && scotomaVision != null
                 && gaze != null
                 && dataSinkEndPoint != null;
        Debug.Log("[DataTransmitter] readiness: centerEye=" + (centerEyeTransform != null) +
            "  scotoma=" + (scotomaTransform != null) +
            "  scotomaVision=" + (scotomaVision != null) +
            "  gaze=" + (gaze != null) +
            "  sink=" + (dataSinkEndPoint != null) +
            (ok ? "  => READY (set a decimation > 0 to start streaming)." : "  => NOT READY (see the lines above)."));
    }

    void Update()
    {
        // 1. Decimation control packet (non-blocking: Available guards the Receive).
        if (controlWordReceiver != null && controlWordReceiver.Available > 0)
        {
            HandleControlPacket();
        }

        // 2. If streaming is enabled and this frame is due, send a record.
        //    Modulo does the cadence: a decimation change takes effect from the
        //    very next frame, no reset bookkeeping needed.
        if (decimation > 0 && frameCounter % decimation == 0)
        {
            TrySendPayload();
        }

        frameCounter++;
    }

    void HandleControlPacket()
    {
        string payload;
        try
        {
            byte[] receivedBytes = controlWordReceiver.Receive(ref controlWordEndPoint);
            payload = Encoding.UTF8.GetString(receivedBytes).Trim();
        }
        catch (Exception e)
        {
            Debug.LogError("[DataTransmitter] Failed to read the control packet: " + e.Message);
            return;
        }

        Debug.Log("[DataTransmitter] Received decimation control packet: '" + payload + "'.");
        if (UInt16.TryParse(payload, NumberStyles.AllowLeadingWhite | NumberStyles.AllowTrailingWhite, CultureInfo.InvariantCulture, out UInt16 newDecimation))
        {
            decimation = newDecimation;
            Debug.Log("[DataTransmitter] Decimation is now " + decimation +
                (decimation == 0 ? " => STREAMING STOPPED." : " => send one record every " + decimation + " frame(s)."));
        }
        else
        {
            Debug.LogError("[DataTransmitter] Could not parse decimation (expected a non-negative integer; 0 stops): '" + payload + "'.");
        }
    }

    void TrySendPayload()
    {
        if (dataSinkEndPoint == null)
        {
            return; // invalid sink address; already logged in Start.
        }
        if (centerEyeTransform == null || gaze == null || scotomaTransform == null || scotomaVision == null)
        {
            return; // a required reference is missing; already logged in Start (stay quiet per-frame).
        }

        try
        {
            long timestampMs = NtpTimeSync.UnixTimeNowInMs();
            if (!NtpTimeSync.IsSynced && !ntpNotSyncedWarned)
            {
                ntpNotSyncedWarned = true;
                Debug.LogWarning("[DataTransmitter] NtpTimeSync is not synced - TimestampMs is the UNCORRECTED system clock. " +
                    "Attach NtpTimeSync to a permanently active object (README setup step 48) and verify the 'offset applied' log.");
            }

            // Sample all fields in one tight block so a single record is self-consistent.
            Vector3 headPos = centerEyeTransform.position;
            Quaternion headRot = centerEyeTransform.rotation;
            float gazeAz = gaze.CurrentGazeVector.x;
            float gazeEl = gaze.CurrentGazeVector.y;
            // Both eyelids separately (not the mean): a participant closing ONE eye
            // (to look "behind" / through the scotoma) would read ~0.5 as a mean —
            // indistinguishable from a half-blink — but with per-eye fields the closed
            // eye is unambiguously ~1 on exactly one side.
            float eyelidL = gaze.LeftEyelidWeight;
            float eyelidR = gaze.RightEyelidWeight;
            bool scotomaEnabled = scotomaVision.renderEnabled;
            float innerAngle = scotomaVision.innerDiameter;
            float outerAngle = scotomaVision.outerDiameter;
            float distance = (scotomaTransform.parent != null) ? scotomaTransform.parent.localPosition.magnitude : 0f;
            Vector3 scotomaPos = scotomaTransform.position;
            Quaternion scotomaRot = scotomaTransform.rotation;

            string json = BuildJson(
                timestampMs,
                headPos, headRot,
                gazeAz, gazeEl, eyelidL, eyelidR,
                scotomaEnabled, innerAngle, outerAngle, distance,
                scotomaPos, scotomaRot);

            byte[] bytes = Encoding.ASCII.GetBytes(json); // plain-ASCII payload (lab convention)
            dataSender.Send(bytes, bytes.Length, dataSinkEndPoint);

            if (!firstRecordLogged)
            {
                firstRecordLogged = true;
                Debug.Log("[DataTransmitter] First record sent (" + bytes.Length + " bytes): " + json);
            }
        }
        catch (Exception e)
        {
            Debug.LogError("[DataTransmitter] Failed to build/send the telemetry record: " + e.Message);
        }
    }

    /*
     * Build the PascalCase JSON record with the fixed-precision, culture-
     * invariant number formatting (see the header budget table). Hand-formed
     * (rather than JsonUtility) so we control precision and key names exactly,
     * and the record stays a single, compact, one-line object.
     */
    string BuildJson(long timestampMs,
                     Vector3 headPos, Quaternion headRot,
                     float gazeAz, float gazeEl, float eyelidL, float eyelidR,
                     bool scotomaEnabled, float innerAngle, float outerAngle, float distance,
                     Vector3 scotomaPos, Quaternion scotomaRot)
    {
        string enabledStr = scotomaEnabled ? "true" : "false";

        return "{" +
               "\"TimestampMs\":" + timestampMs.ToString(CultureInfo.InvariantCulture) + "," +
               "\"HeadsetPosition\":[" + F3(headPos.x) + "," + F3(headPos.y) + "," + F3(headPos.z) + "]," +
               "\"HeadsetRotation\":[" + F4(headRot.x) + "," + F4(headRot.y) + "," + F4(headRot.z) + "," + F4(headRot.w) + "]," +
               "\"GazeVectorAzimuth\":" + F1(gazeAz) + "," +
               "\"GazeVectorElevation\":" + F1(gazeEl) + "," +
               "\"EyelidLeft\":" + F4(eyelidL) + "," +
               "\"EyelidRight\":" + F4(eyelidR) + "," +
               "\"ScotomaEnabled\":" + enabledStr + "," +
               "\"ScotomaInnerAngle\":" + F1(innerAngle) + "," +
               "\"ScotomaOuterAngle\":" + F1(outerAngle) + "," +
               "\"ScotomaDistance\":" + F3(distance) + "," +
               "\"ScotomaPosition\":[" + F3(scotomaPos.x) + "," + F3(scotomaPos.y) + "," + F3(scotomaPos.z) + "]," +
               "\"ScotomaRotation\":[" + F4(scotomaRot.x) + "," + F4(scotomaRot.y) + "," + F4(scotomaRot.z) + "," + F4(scotomaRot.w) + "]" +
               "}";
    }

    // Fixed-precision, culture-invariant decimal formatting (lab uses en-US / invariant '.' decimals).
    static string F1(float v) { return v.ToString("0.0", CultureInfo.InvariantCulture); }   // 0.1   (degrees)
    static string F3(float v) { return v.ToString("0.000", CultureInfo.InvariantCulture); }  // 0.001 (millimetre)
    static string F4(float v) { return v.ToString("0.0000", CultureInfo.InvariantCulture); }  // 0.0001 (quaternion / 0..1)

    void OnDestroy()
    {
        if (controlWordReceiver != null)
        {
            controlWordReceiver.Close();
            controlWordReceiver = null;
        }
        if (dataSender != null)
        {
            dataSender.Close();
            dataSender = null;
        }
    }
}
