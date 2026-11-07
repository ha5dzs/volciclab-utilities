/*
 * UnityOptitrackAlignment.cs
 *
 * Marries the Unity world and the OptiTrack/Motive world, on demand, over the LAN.
 *
 * WHAT IT DOES
 *   On trigger it fetches ONE pose of the headset's OptiTrack rigid body from the lab
 *   streaming server, and moves/rotates THIS GameObject (attach on an ancestor of
 *   CenterEyeAnchor, e.g. the [BuildingBlock] Camera Rig) so that the cyclopean eye
 *   lands exactly on that pose (plus the running fudge below). That makes the
 *   Unity coordinate system coincide with the tracker's, which is what licenses
 *   StreamRigidBodyFromOptitrack to write raw tracker coordinates straight into the
 *   scene.
 *
 * SUPERSEDES (as a reference): universe_relocator.cs (the Christmas-break original).
 * Same wire protocol, same verified correction math; the failure modes are tamed:
 * bounded attempts, a receive timeout, NaN-safe parsing, and a loud terminal state
 * instead of an unbounded "retry forever" loop.
 *
 * AUTO-ALIGN ON START (off by default)
 *   'requestAlignmentOnStart' is false in a fresh template: the script binds its
 *   sockets (a listening socket transmits nothing, so an isolated-AP demo is
 *   unaffected) but runs NO alignment until one is explicitly requested — via the
 *   control port, by ticking the flag in the Inspector (it fires on the next frame
 *   and consumes itself, the original 'requestUpdateNow' ergonomics), by code, or
 *   the recenter hook after a successful alignment.
 *
 * PROTOCOL (plain ASCII, en-US, ';'-separated — the lab streaming server's grammar)
 *   Headset -> server (start):  <rigid_body_id>;<data_port>;<decimation>
 *   Server  -> headset (pose):  <unix_ms>;<rb_id>;<x,y,z>;<qx,qy,qz,qw>;<name>\n
 *   Headset -> server (stop):   <rigid_body_id>;<data_port>;0      (decimation 0 = off)
 *
 *   Lab -> headset, port 'controlPort' (default 5000; deliberately OUTSIDE the
 *   40000-40007 experiment-control block, so the same APK is findable on any box):
 *     '?'                 -> query: replies 'OK;<STATE>;<fx,fy,fz>;errMm;errDeg;attempts'
 *     '<x>,<y>,<z>'       -> ADD that translation delta (metres, Y-up) to the running
 *                            fudge, then run the alignment
 *     anything else       -> plain alignment trigger (fudge unchanged)
 *
 *   This is a READ+SET-ish port (it can reply), so the reply goes out through the
 *   SAME socket that received the request — the established 5-tuple-reverse house rule.
 *
 * THREADING (house rule): the socket callback runs off the main thread and only
 * RECORDS the request under a lock; Update() applies it on the main thread, a frame
 * later. The alignment itself intentionally BLOCKS the render thread for up to
 * 'maxAttempts' * receive-timeout: the lab invariant is that this script is only ever
 * used while the streaming server is guaranteed to be up, and the trigger is a
 * deliberate lab action, not a per-frame data path.
 *
 * HEADSET NOISE (why the headset-side read is averaged over a few frames)
 *   The headset's OWN pose estimate (the anchor read from the rig hierarchy) is
 *   noisier than the OptiTrack side when the headset is cradled: observed on device,
 *   repeat triggers left the origin marker drifting a little, randomly each time.
 *   The headset pose updates once per rendered frame, so "read 3 times inside the
 *   alignment" would read the same value 3 times — instead, Update() samples the
 *   rig-relative headset pose once per frame into a small ring, and the alignment
 *   averages the last 'anchorSampleCount' of them (default 3; 1 = one read = the
 *   original's single read). The window is small on purpose: nothing guarantees the
 *   headset is stationary during an alignment.
 *
 * DO NOT write to the anchor pivots: CenterEyeAnchor / GazeContingentGameObject
 * transforms are owned by the rig and by GazeContingentTransform2. This script only
 * reads the eye anchor's live pose, and only writes to its own transform.
 */

using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

public class UnityOptitrackAlignment : MonoBehaviour
{
    // ------------------------------------------------------------------ public ---

    // If checked, one alignment run is requested on start (consumed on the first
    // Update — the original 'requestUpdateNow' pattern). It also doubles as a live
    // manual trigger when ticked in the Inspector mid-session.
    // OFF in the template: the script listens and waits for an explicit trigger.
    public bool requestAlignmentOnStart = false;

    // Swap Y and Z between the OptiTrack frame and Unity — the original's option,
    // kept for the labs that stream standard OptiTrack (Z-depth, Y-up, already Unity
    // order) by turning it OFF. It also selects which axis of the OptiTrack
    // quaternion carries the vertical angle (Z when swapped, Y otherwise), exactly as
    // the original's two branches do.
    public bool swapYAndZCoordinates = true;

    // Lab-side trigger / query port. 5000 = easy to remember; outside the 40000-40007 block.
    public int controlPort = 5000;

    // The rigid body on the headset in Motive (origin = CenterEyeAnchor, created in the cradle).
    public int rigidBodyID = 5000;

    // The lab streaming server (the custom plain-ASCII front end to Motive).
    public string streamerServerAddress = "192.168.42.5";
    public int streamerServerPort = 64923;

    // Where the server streams the pose back to. Randomised per session by default,
    // so several instances / workstations never collide.
    public int dataReturnPort = 54622;
    public bool randomizeDataReturnPort = true;

    // 1 = every sample (we take the first one and stop immediately); 0 = stop-streaming value.
    public int decimation = 1;

    // The running fudge: a translation (metres, Y-up) added to the alignment.
    // The experimenter software (Matlab) adjusts it over 'controlPort' as RELATIVE
    // deltas while visually verifying — it is deliberately in-memory only: on every
    // app start the lab re-verifies and re-sends what it needs.
    public Vector3 CoordinateOffsetsInUnitySpace = Vector3.zero;

    // Bounded failure (the original script looped FOREVER when alignment never
    // converged; this one gives up loudly instead).
    public int maxAttempts = 3;
    public float maxCoordinateErrorMetres = 0.005f;   // 5 mm
    public float maxAngularErrorDegrees = 0.5f;
    public int receiveTimeoutMilliseconds = 5000;

    // Temporal window for averaging the HEADETT-side anchor read (the headset's own
    // pose estimate is the noisy input; see the header note). 3 = the default (a few
    // frames at 70-90 Hz keeps drift from motion negligible); 1 = the original's
    // single read. Capped internally at MaxAnchorSamples.
    public int anchorSampleCount = 10;

    // When the tracker is recentred (e.g. the participant resets it), re-run the
    // alignment one frame later, so the worlds stay married without the lab PC.
    // Only acts after at least one SUCCESSFUL alignment (no boot noise).
    public bool autoRealignOnRecenter = true;

    // Optional direct reference. If null, falls back to GameObject.Find("CenterEyeAnchor")
    // (the same convention as the original script).
    public GameObject centerEyeAnchor;

    // ------------------------------------------------------------------- state ---

    public enum AlignmentState
    {
        Idle,
        Aligning,
        Success,
        Failed,
    }

    public AlignmentState alignmentState = AlignmentState.Idle;
    public float lastCoordinateError;   // metres; magnitude of the applied correction
    public float lastAngularError;      // degrees; magnitude of the applied yaw correction
    public int lastAttemptCount;

    // --------------------------------------------------------------- internals ---

    private UdpClient controlClient;
    private UdpClient dataClient;

    private readonly object pendingRequestLock = new object();
    private bool hasPendingRequest;
    private string pendingRequestPayload;
    private IPEndPoint pendingRequestSource;

    private bool recenterPending;
    private int recenterPendingFrame = -1;
    private bool recenterHookAttached;

    private bool shutdownDone;
    private System.Random rng = new System.Random();
    // Culture for consistent decimal formatting (lab convention: en-US).
    private CultureInfo enUs = CultureInfo.GetCultureInfo("en-US");

    // Rolling buffer of the headset-side (RIG-RELATIVE) anchor pose — the physical,
    // rig-independent headset pose, so the buffer stays valid whatever the rig's state
    // is when the samples were taken. One entry per frame (Update runs after the
    // runtime's per-frame pose update, so one frame = one new headset pose).
    private const int MaxAnchorSamples = 16;
    private readonly Vector3[] anchorLocalSamples = new Vector3[MaxAnchorSamples];
    private readonly float[] anchorYawSamples = new float[MaxAnchorSamples];
    private int anchorSampleHead;
    private int anchorSampleFilled;
    private Transform cachedAnchor;
    private int lastSamplesUsed;

    // ---------------------------------------------------------------- lifecycle --

    void Start()
    {
        // en-US culture info. Required for consistent decimal formatting. (lab convention)
        enUs = new CultureInfo("en-US");
        Thread.CurrentThread.CurrentCulture = enUs;
        Thread.CurrentThread.CurrentUICulture = enUs;
        CultureInfo.DefaultThreadCurrentCulture = enUs;
        CultureInfo.DefaultThreadCurrentUICulture = enUs;

        // The sockets are always bound (a listening socket transmits nothing — an
        // isolated-AP demo is unaffected); whether any alignment RUNS is gated by
        // 'requestAlignmentOnStart' and the explicit triggers below.
        // Pick this session's data return port.
        if (randomizeDataReturnPort)
        {
            dataReturnPort = rng.Next(dataReturnPort, dataReturnPort + 200);
        }

        // Data socket: the server streams the pose here. Bounded receive (the original
        // blocked HERE forever when the server was down; the lab invariant protects us,
        // the timeout is insurance for the rare broken-invariant moment).
        dataClient = new UdpClient(dataReturnPort);
        dataClient.Client.ReceiveTimeout = receiveTimeoutMilliseconds;

        // Control socket: lab -> headset trigger / query. (IPEndPoint constructor —
        // Unity's BCL lacks the UdpClient(IPAddress, int) overload; this matches the
        // working sibling scripts.)
        controlClient = new UdpClient(new IPEndPoint(IPAddress.Any, controlPort));
        controlClient.BeginReceive(new AsyncCallback(ControlListenerHandler), controlClient);

        Debug.Log(string.Format(enUs, "[UnityOptitrackAlignment] Listening: control port {0}, data return port {1}, server {2}:{3}, rigid body {4}, swapY/Z {5}, align-on-start {6}, fudge ({7},{8},{9}), up to {10} attempts.",
            controlPort, dataReturnPort, streamerServerAddress, streamerServerPort, rigidBodyID,
            swapYAndZCoordinates ? "on" : "off",
            requestAlignmentOnStart ? "on" : "off",
            CoordinateOffsetsInUnitySpace.x, CoordinateOffsetsInUnitySpace.y, CoordinateOffsetsInUnitySpace.z, maxAttempts));
    }

    void Update()
    {
        // 0a. One headset-side sample per frame (non-blocking; see the header note
        //     on headset noise). Update runs after the runtime pose update, so this
        //     captures each new headset pose exactly once.
        SampleAnchorPose();

        // 0b. One-shot auto-alignment (the original 'requestUpdateNow' consumption):
        //     fires when checked for start, or on whatever frame it was ticked in the
        //     Inspector. Waits for the requested sample window to fill (a few frames —
        //     the headset pose needs a moment to settle after launch too); if the
        //     anchor itself is unresolvable, don't wait — run it and fail loudly.
        if (requestAlignmentOnStart && (AnchorReadsAreFresh() || ResolveCenterEyeAnchor() == null))
        {
            requestAlignmentOnStart = false;
            Debug.Log("[UnityOptitrackAlignment] Alignment requested (on-start / Inspector flag) — running now.");
            BeginAlignment();
            return;
        }

        // Attach the tracker-recentered hook lazily: OVRManager.display may not exist
        // when our Start() runs (script execution order), so latch it on first sight.
        if (!recenterHookAttached && OVRManager.display != null)
        {
            recenterHookAttached = true;
            OVRManager.display.RecenteredPose += OnTrackerRecentered;
        }

        // 1. Apply a control-port request (recorded off the main thread; applied here).
        bool applyRequest;
        string payload;
        IPEndPoint source;
        lock (pendingRequestLock)
        {
            applyRequest = hasPendingRequest;
            hasPendingRequest = false;
            payload = pendingRequestPayload;
            source = pendingRequestSource;
            pendingRequestPayload = null;
            pendingRequestSource = null;
        }

        if (applyRequest)
        {
            string trimmed = (payload ?? string.Empty).Trim();
            if (trimmed == "?")
            {
                // Query: reply through the same socket (house rule), then do nothing else.
                SendQueryReply(source);
            }
            else
            {
                Vector3 delta;
                if (TryParseFudgeDelta(trimmed, out delta))
                {
                    CoordinateOffsetsInUnitySpace += delta;
                    Debug.Log(string.Format(enUs, "[UnityOptitrackAlignment] Fudge delta ({0},{1},{2}); fudge now ({3},{4},{5}).",
                        delta.x, delta.y, delta.z,
                        CoordinateOffsetsInUnitySpace.x, CoordinateOffsetsInUnitySpace.y, CoordinateOffsetsInUnitySpace.z));
                }
                BeginAlignment();
            }
            return;
        }

        // 2. Tracker was recentred: re-align once FRESH headset-side samples have
        //    refilled the window. The recenter moved the headset's pose and the
        //    buffer was flushed with the event — averaging stale samples would pull
        //    the correction part-way back. Only after a successful alignment, so a
        //    pre-boot recenter event does not fire it.
        if (autoRealignOnRecenter && recenterPending &&
            alignmentState == AlignmentState.Success &&
            Time.frameCount > recenterPendingFrame &&
            AnchorReadsAreFresh())
        {
            recenterPending = false;
            Debug.Log("[UnityOptitrackAlignment] Tracker recenter event detected — re-aligning to keep the worlds married.");
            BeginAlignment();
        }
    }

    void OnApplicationQuit()
    {
        Shutdown();
    }

    void OnDisable()
    {
        // Covers both scene unload and application quit (whichever fires first).
        Shutdown();
    }

    void OnEnable()
    {
        // Re-armed after disable/enable in the editor: start clean.
        shutdownDone = false;
        recenterHookAttached = false;
        FlushAnchorReads();
        cachedAnchor = null;
    }

    // ------------------------------------------------------------- control port --

    // Runs on the socket (callback) thread: RECORD the request, never touch Unity.
    void ControlListenerHandler(IAsyncResult result)
    {
        UdpClient socket = result.AsyncState as UdpClient;
        if (socket == null)
        {
            return;
        }

        IPEndPoint source = new IPEndPoint(IPAddress.Any, controlPort);
        try
        {
            byte[] received = socket.EndReceive(result, ref source);
            string payload = Encoding.ASCII.GetString(received);
            lock (pendingRequestLock)
            {
                pendingRequestPayload = payload;
                pendingRequestSource = source;
                hasPendingRequest = true;
            }
        }
        catch (Exception e)
        {
            // The socket was closed underneath us (teardown) — stop re-arming.
            Debug.LogWarning("[UnityOptitrackAlignment] Control listener stopped: " + e.Message);
            return;
        }

        // Restart the receive loop.
        socket.BeginReceive(new AsyncCallback(ControlListenerHandler), socket);
    }

    // Reads back the running state. Format (documented, ';'-seposed, en-US):
    //   OK;<STATE>;<fx,fy,fz>;errMm;errDeg;attempts
    void SendQueryReply(IPEndPoint source)
    {
        if (controlClient == null || source == null)
        {
            return;
        }

        string reply = string.Format(enUs, "OK;{0};{1:0.###},{2:0.###},{3:0.###};{4:0.0};{5:0.00};{6}",
            alignmentState.ToString().ToUpperInvariant(),
            CoordinateOffsetsInUnitySpace.x, CoordinateOffsetsInUnitySpace.y, CoordinateOffsetsInUnitySpace.z,
            lastCoordinateError * 1000f, lastAngularError, lastAttemptCount);

        try
        {
            byte[] replyBytes = Encoding.ASCII.GetBytes(reply);
            controlClient.Send(replyBytes, replyBytes.Length, source);
            Debug.Log("[UnityOptitrackAlignment] Queried; replied '" + reply + "'.");
        }
        catch (Exception e)
        {
            Debug.LogWarning("[UnityOptitrackAlignment] Could not send the query reply: " + e.Message);
        }
    }

    // '<x>,<y>,<z>' — exactly three floats, comma-separated (the lab's translation
    // convention), and no ';' (a ';'-separated payload is a different message).
    bool TryParseFudgeDelta(string payload, out Vector3 delta)
    {
        delta = Vector3.zero;
        if (string.IsNullOrEmpty(payload) || payload.IndexOf(';') >= 0)
        {
            return false;
        }

        string[] parts = payload.Split(',');
        if (parts.Length != 3)
        {
            return false;
        }

        float x, y, z;
        if (!float.TryParse(parts[0].Trim(), NumberStyles.Float, enUs, out x)) return false;
        if (!float.TryParse(parts[1].Trim(), NumberStyles.Float, enUs, out y)) return false;
        if (!float.TryParse(parts[2].Trim(), NumberStyles.Float, enUs, out z)) return false;

        delta = new Vector3(x, y, z);
        return true;
    }

    // --------------------------------------------------------------- alignment --

    // The alignment proper. Main thread, intentionally blocking (see header).
    public void BeginAlignment()
    {
        if (dataClient == null)
        {
            Debug.LogError("[UnityOptitrackAlignment] Alignment requested but no data socket — the component may still be starting.");
            return;
        }

        alignmentState = AlignmentState.Aligning;
        string failureReason = "";
        bool success = false;

        for (int attempt = 1; attempt <= maxAttempts && !success; attempt++)
        {
            lastAttemptCount = attempt;

            // 1. Start: ask the server for the rigid body's pose, streamed to our port.
            try
            {
                string request = string.Format("{0};{1};{2}", rigidBodyID, dataReturnPort, decimation);
                byte[] requestBytes = Encoding.ASCII.GetBytes(request);
                dataClient.Send(requestBytes, requestBytes.Length, streamerServerAddress, streamerServerPort);
                Debug.Log(string.Format(enUs, "[UnityOptitrackAlignment] Attempt {0}: sent start request '{1}' to {2}:{3}.",
                    attempt, request, streamerServerAddress, streamerServerPort));
            }
            catch (Exception e)
            {
                failureReason = "could not send the start request: " + e.Message;
                break;
            }

            // 2. Blocking receive — bounded by ReceiveTimeout.
            byte[] received;
            try
            {
                IPEndPoint remote = new IPEndPoint(IPAddress.Parse(streamerServerAddress), dataReturnPort);
                received = dataClient.Receive(ref remote);
            }
            catch (SocketException e) when (e.SocketErrorCode == SocketError.TimedOut)
            {
                failureReason = string.Format(enUs, "streaming server did not answer within {0} ms", receiveTimeoutMilliseconds);
                Debug.LogWarning("[UnityOptitrackAlignment] Attempt " + attempt + ": " + failureReason + " — retrying.");
                continue;
            }
            catch (Exception e)
            {
                failureReason = "receive failed: " + e.Message;
                break;
            }

            // 3. Stop the stream immediately: we wanted exactly one pose.
            try
            {
                string stop = string.Format("{0};{1};0", rigidBodyID, dataReturnPort);
                byte[] stopBytes = Encoding.ASCII.GetBytes(stop);
                dataClient.Send(stopBytes, stopBytes.Length, streamerServerAddress, streamerServerPort);
            }
            catch (Exception e)
            {
                Debug.LogWarning("[UnityOptitrackAlignment] Could not send the stop packet: " + e.Message);
            }

            // 4. Parse. NaNs are an attempt FAILURE here (the original carried NaNs
            //    into the math, and NaN > x is false, so it "passed" at NaN — not here).
            string text = Encoding.ASCII.GetString(received);
            string[] fields = text.Split(';');
            if (fields.Length != 5)
            {
                failureReason = string.Format(enUs, "expected 5 ';' fields in the pose packet, got {0}", fields.Length);
                Debug.LogWarning("[UnityOptitrackAlignment] " + failureReason + " — retrying.");
                continue;
            }

            uint receivedID;
            if (!uint.TryParse(fields[1], out receivedID) || receivedID != rigidBodyID)
            {
                failureReason = string.Format(enUs, "wrong rigid body in the payload (got '{0}', expected {1})", fields[1], rigidBodyID);
                Debug.LogWarning("[UnityOptitrackAlignment] " + failureReason + " — retrying.");
                continue;
            }

            float tx, ty, tz;
            if (!TryParseThree(fields[2], out tx, out ty, out tz))
            {
                failureReason = "malformed translation field: '" + fields[2] + "'";
                Debug.LogWarning("[UnityOptitrackAlignment] " + failureReason + " — retrying.");
                continue;
            }

            float qx, qy, qz, qw;
            if (!TryParseFour(fields[3], out qx, out qy, out qz, out qw))
            {
                failureReason = "malformed quaternion field: '" + fields[3] + "'";
                Debug.LogWarning("[UnityOptitrackAlignment] " + failureReason + " — retrying.");
                continue;
            }

            // 5. Apply the (lab-verified) correction. See the .cs.md "The verified part".
            Transform anchor = ResolveCenterEyeAnchor();
            if (anchor == null)
            {
                failureReason = "no CenterEyeAnchor found — is the Camera Rig in the scene?";
                break;
            }

            ApplyCorrection(tx, ty, tz, qx, qy, qz, qw, anchor);

            // 6. The gate: was the applied correction small enough?
            if (lastCoordinateError <= maxCoordinateErrorMetres && lastAngularError <= maxAngularErrorDegrees)
            {
                success = true;
                Debug.Log(string.Format(enUs, "[UnityOptitrackAlignment] ALIGNED on attempt {0}: applied correction {1:0.0} mm / {2:0.00} deg; fudge ({3},{4},{5}); headset read averaged over {6} frame(s).",
                    attempt, lastCoordinateError * 1000f, lastAngularError,
                    CoordinateOffsetsInUnitySpace.x, CoordinateOffsetsInUnitySpace.y, CoordinateOffsetsInUnitySpace.z, lastSamplesUsed));
            }
            else
            {
                Debug.LogWarning(string.Format(enUs, "[UnityOptitrackAlignment] Attempt {0}: applied correction {1:0.0} mm / {2:0.00} deg exceeds tolerance ({3:0.0} mm / {4:0.00} deg) — retrying.",
                    attempt, lastCoordinateError * 1000f, lastAngularError,
                    maxCoordinateErrorMetres * 1000f, maxAngularErrorDegrees));
            }
        }

        alignmentState = success ? AlignmentState.Success : AlignmentState.Failed;
        if (!success)
        {
            Debug.LogError("[UnityOptitrackAlignment] Alignment FAILED after " + lastAttemptCount +
                " attempt(s): " + failureReason + " — the worlds are NOT married; do not collect data until fixed.");
        }
    }

    // The verified correction. MIRRORS universe_relocator.cs (the known-good reference,
    // verified on the hardware) line-for-line — in expression AND in the ORDER the
    // operations happen, because the order is part of the verified behaviour:
    //
    //   (a) position is applied while the rig's PREVIOUS rotation is still in effect
    //       (the anchor position is read before any rotation reset);
    //   (b) the rig rotation is reset to identity BEFORE the headset yaw is read, so
    //       with the (identity) anchor chain the read is 0 and the final rig yaw is
    //       the ABSOLUTE OptiTrack yaw — repeat triggers are idempotent. Reading the
    //       headset yaw first would subtract the previous correction, and each extra
    //       trigger would flip the world the other way (the on-device "jumps").
    //
    // DO NOT "improve" without re-verifying on the cradle.
    void ApplyCorrection(float tx, float ty, float tz, float qx, float qy, float qz, float qw, Transform anchor)
    {
        // HEADSET-SIDE read, temporally stabilised (the headset's own pose estimate is
        // the noisy half of the equation; the OptiTrack body is stable when cradled).
        // The buffer stores exactly the rig-relative quantity the original read in
        // both spots below, so averaging it removes noise, not behaviour.
        Vector3 anchorLocalMean;
        float verticalRotationFromHeadset;
        int samplesUsed;
        AverageAnchorReads(anchor, out anchorLocalMean, out verticalRotationFromHeadset, out samplesUsed);
        lastSamplesUsed = samplesUsed;

        // POSITION — the original's expression, applied under the rig's CURRENT state
        // (point a): the anchor's world pose is rigPos + rigRot * (rig-relative read),
        // and the rig is unchanged between runs, so this is exactly the original's
        // single read — except temporally averaged. Y/Z swap = lab convention
        // (OptiTrack data is Z-up / Y-depth, as plotted; Unity is Y-up / Z-depth).
        Vector3 positionFromOptitrack = swapYAndZCoordinates
            ? new Vector3(tx, tz, ty)
            : new Vector3(tx, ty, tz);   // standard OptiTrack (Z-depth, Y-up) already matches Unity
        Vector3 eyeAnchorPosition = transform.position + transform.rotation * anchorLocalMean;   // (point a)
        // Move this GameObject so the eye anchor lands exactly on the (swapped)
        // OptiTrack position, plus the running fudge. Everything between this object
        // and the anchor moves with it.
        transform.position = positionFromOptitrack + transform.position + CoordinateOffsetsInUnitySpace - eyeAnchorPosition;
        // Magnitude of the applied correction (what the gate judges).
        lastCoordinateError = Vector3.Distance(positionFromOptitrack, eyeAnchorPosition);

        // ROTATION. Yaw only: the floors are parallel and the subject is upright, so
        // the full 3D transform reduces to a vertical-axis rotation (the Deakin 1999
        // general case, specialised for this lab — see the .cs.md).
        // Reset FIRST (the original's order, point b):
        transform.rotation = Quaternion.identity;

        // The vertical axis of the OptiTrack quaternion: Z when swapped (Z-up lab
        // data), Y otherwise (standard OptiTrack) — the original's two branches.
        Quaternion quaternionFromOptitrack = new Quaternion(qx, qy, qz, qw);
        float verticalRotationFromOptitrack = swapYAndZCoordinates
            ? quaternionFromOptitrack.eulerAngles.z
            : quaternionFromOptitrack.eulerAngles.y;
        // OptiTrack's vertical rotation runs the other way (verified on the cradle).
        verticalRotationFromOptitrack = 360f - verticalRotationFromOptitrack;

        // Headset yaw = the original's AFTER-reset read (point b), temporally
        // averaged: it is rig-relative (the local chain = the headset's own pose),
        // exactly the quantity read post-identity-reset in the original.
        transform.rotation = Quaternion.Euler(0f, verticalRotationFromOptitrack, 0f) *
                            Quaternion.Inverse(Quaternion.Euler(0f, verticalRotationFromHeadset, 0f));
        // The applied yaw correction, wrap-safe (the original's raw subtraction could
        // misread the 0/360 boundary as a ~360 deg error).
        lastAngularError = Mathf.Abs(Mathf.DeltaAngle(verticalRotationFromOptitrack, verticalRotationFromHeadset));
    }

    Transform ResolveCenterEyeAnchor()
    {
        if (cachedAnchor != null)
        {
            return cachedAnchor;
        }

        if (centerEyeAnchor != null && centerEyeAnchor.transform != null)
        {
            cachedAnchor = centerEyeAnchor.transform;
        }
        else
        {
            GameObject found = GameObject.Find("CenterEyeAnchor");
            if (found != null)
            {
                cachedAnchor = found.transform;
            }
        }

        return cachedAnchor;
    }

    // One headset-side sample per frame (see the header note). Rig-relative (the
    // physical headset pose), so the buffer stays valid whatever rig state the
    // samples were taken under — the rig itself is constant between runs.
    void SampleAnchorPose()
    {
        Transform anchor = ResolveCenterEyeAnchor();
        if (anchor == null)
        {
            return;
        }

        anchorLocalSamples[anchorSampleHead] = transform.InverseTransformPoint(anchor.position);
        anchorYawSamples[anchorSampleHead] = Mathf.DeltaAngle(transform.eulerAngles.y, anchor.eulerAngles.y);
        anchorSampleHead = (anchorSampleHead + 1) % MaxAnchorSamples;
        if (anchorSampleFilled < MaxAnchorSamples)
        {
            anchorSampleFilled++;
        }
    }

    // Temporal mean of the buffered headset-side reads: position by arithmetic mean,
    // yaw by circular mean (wrap-safe). Falls back to one direct read when the buffer
    // is empty (e.g. an external trigger on the very first frame) — the original's
    // behaviour, so a trigger can never starve.
    void AverageAnchorReads(Transform anchor, out Vector3 meanLocalPosition, out float meanLocalYaw, out int used)
    {
        used = Mathf.Min(anchorSampleCount, anchorSampleFilled);
        if (used <= 0)
        {
            used = 1;
            meanLocalPosition = transform.InverseTransformPoint(anchor.position);
            meanLocalYaw = Mathf.DeltaAngle(transform.eulerAngles.y, anchor.eulerAngles.y);
            if (meanLocalYaw < 0f)
            {
                meanLocalYaw += 360f;
            }
            return;
        }

        Vector3 sumLocal = Vector3.zero;
        float sumCos = 0f, sumSin = 0f;
        int start = (anchorSampleHead - used + MaxAnchorSamples) % MaxAnchorSamples;
        for (int i = 0; i < used; i++)
        {
            int index = (start + i) % MaxAnchorSamples;
            sumLocal += anchorLocalSamples[index];
            float radians = anchorYawSamples[index] * Mathf.Deg2Rad;
            sumCos += Mathf.Cos(radians);
            sumSin += Mathf.Sin(radians);
        }

        meanLocalPosition = sumLocal / used;
        meanLocalYaw = Mathf.Atan2(sumSin, sumCos) * Mathf.Rad2Deg;
        if (meanLocalYaw < 0f)
        {
            meanLocalYaw += 360f;   // [0, 360), matching eulerAngles conventions
        }
    }

    bool AnchorReadsAreFresh()
    {
        return anchorSampleFilled >= Mathf.Min(anchorSampleCount, MaxAnchorSamples);
    }

    void FlushAnchorReads()
    {
        anchorSampleHead = 0;
        anchorSampleFilled = 0;
    }

    bool TryParseThree(string field, out float a, out float b, out float c)
    {
        a = b = c = 0f;
        string[] parts = field.Split(',');
        if (parts.Length != 3)
        {
            return false;
        }
        return float.TryParse(parts[0].Trim(), NumberStyles.Float, enUs, out a) &&
               float.TryParse(parts[1].Trim(), NumberStyles.Float, enUs, out b) &&
               float.TryParse(parts[2].Trim(), NumberStyles.Float, enUs, out c);
    }

    bool TryParseFour(string field, out float a, out float b, out float c, out float d)
    {
        a = b = c = d = 0f;
        string[] parts = field.Split(',');
        if (parts.Length != 4)
        {
            return false;
        }
        return float.TryParse(parts[0].Trim(), NumberStyles.Float, enUs, out a) &&
               float.TryParse(parts[1].Trim(), NumberStyles.Float, enUs, out b) &&
               float.TryParse(parts[2].Trim(), NumberStyles.Float, enUs, out c) &&
               float.TryParse(parts[3].Trim(), NumberStyles.Float, enUs, out d);
    }

    // ------------------------------------------------------ tracker recentering --

    // Fired by OVRManager.display (Meta XR SDK 207) whenever the local tracking space
    // is recentred — by the OS, by the participant, or by app code. Runs on the main
    // thread (from OVRDisplay.Update), so the flag handoff below is safe.
    void OnTrackerRecentered()
    {
        recenterPending = true;
        recenterPendingFrame = Time.frameCount;
        // The recenter changed the headset's pose: the buffered reads are stale, so
        // flush them and only re-align once fresh ones have refilled (see Update step 2).
        FlushAnchorReads();
        Debug.Log("[UnityOptitrackAlignment] RecenteredPose event received (re-alignment follows once fresh headset samples refill the window, if previously aligned).");
    }

    // --------------------------------------------------------------- teardown ---

    void Shutdown()
    {
        if (shutdownDone)
        {
            return;
        }
        shutdownDone = true;

        if (recenterHookAttached && OVRManager.display != null)
        {
            recenterHookAttached = false;
            OVRManager.display.RecenteredPose -= OnTrackerRecentered;
        }

        if (controlClient != null)
        {
            try { controlClient.Close(); } catch { /* best effort */ }
            controlClient = null;
        }

        if (dataClient != null)
        {
            // Best effort: tell the server to stop streaming to this socket.
            try
            {
                string stop = string.Format("{0};{1};0", rigidBodyID, dataReturnPort);
                byte[] stopBytes = Encoding.ASCII.GetBytes(stop);
                dataClient.Send(stopBytes, stopBytes.Length, streamerServerAddress, streamerServerPort);
            }
            catch { /* best effort */ }
            try { dataClient.Close(); } catch { /* best effort */ }
            dataClient = null;
        }

        Debug.Log("[UnityOptitrackAlignment] Shutdown: sockets closed, streaming stopped (best effort).");
    }
}
