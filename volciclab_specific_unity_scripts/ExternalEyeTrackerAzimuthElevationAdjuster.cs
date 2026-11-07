/*
 * This script exposes the two gaze-related fields of GazeContingentTransform2
 * to external control over the network:
 *   - Read: the current (calibrated) gaze vector, as a RUNNING AVERAGE over the last
 *     `windowFrames` samples (default 10). A single frame of the Quest Pro eye tracker
 *     carries microsaccades plus sensor noise; while the participant holds fixation on
 *     a stimulus, the short-window average is a much better estimate of "where they
 *     actually look".
 *   - Write: the per-trial calibration offset (azimuth, elevation, in degrees, same
 *     convention as the gaze: +azimuth = right, +elevation = up).
 *
 * That is the read/write half of the calibration loop: present a stimulus at (Az;El),
 * ask the participant to look at it, query the averaged gaze, send the difference back
 * here as the offset — a second query should then match the stimulus. (The full-set
 * calibration methodology is a later concern; this script is the plumbing.)
 *
 * Additionally: every sampled gaze is checked against a "reasonable" box (default
 * |azimuth| <= 40°, |elevation| <= 20° — well inside the human visual field, ~±80°/±30°
 * in this convention, and the headset's ~±53°/±48°, both smaller in practice with
 * glasses or a lens-to-eye gap). A gaze that SUSTAINS outside that box means the
 * calibration is suspect (offset not applied, miscalibration, tracker drop), so the
 * script logs once on entering and once on leaving that state — never per frame (that
 * would spam at 90 Hz). Log-only: the samples, the average, and the replies are not clamped.
 *
 * Protocol: one UDP packet per call to the control port, plain-ASCII payload.
 *   - '?'          -> query: the headset replies to the sender with
 *                     '<azimuth>,<elevation>' (4 decimals, e.g. '2.3450,-1.0025')
 *   - '<az>,<el>'  -> set the gaze offset (e.g. '0.5,-0.1'); fire-and-forget, no ack
 * (matching the other lab scripts; see README.md - "Network communication protocol")
 *
 * The socket callback runs off the main thread, so it only records the request
 * (last one wins); applying the offset and answering a query happen in Update()
 * (the main thread) — touching the scene from a callback thread is not allowed.
 *
 * The reply goes out through THE SAME socket that received the query: the answer's
 * source port is therefore 40001 (the exact reverse of the request's 5-tuple), which
 * is what the headset's AP-mode network path expects for "a reply to a packet we got".
 */

using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

public class ExternalEyeTrackerAzimuthElevationAdjuster : MonoBehaviour
{
    // Stuff to meddle with in the editor.
    public int eyeTrackerAzimuthElevationPort = 40001;

    // How many recent samples the query reply averages (default 10 ~= ~111 ms at 90 Hz).
    // Values below 1 are treated as 1.
    public int windowFrames = 10;

    // The "reasonable gaze" box (degrees) for the log-only warning. The actual visual
    // fields are far larger (human ~±80° az / ±30° el; headset ~±53° / ±48°, and smaller
    // still with glasses or a lens-to-eye gap), so a fixated gaze should land well inside
    // this box; a sustained read outside it means the calibration is suspect.
    public float maxAbsAzimuth = 40f;
    public float maxAbsElevation = 20f;

    // The component whose gaze we read and whose offset we set (the scene's eye tracker core).
    private GazeContingentTransform2 eyeTracker;

    // Rolling window (C#'s deque) of recent CurrentGazeVector samples.
    // Main thread only: enqueued and read in Update().
    private readonly Queue<Vector2> gazeHistory = new Queue<Vector2>();

    // Out-of-range warning state: log once on entry and once on leaving, not per frame
    // (a sustained out-of-range gaze must not spam the log at 90 Hz).
    private bool gazeIsOutOfRange;

    // The socket, created in Start(). Both the incoming control packets AND the query
    // replies use it (see the file header for why the reply must come from the listening port).
    private UdpClient azElListener;

    // Request arriving on the callback thread; applied in Update() on the main thread.
    private readonly object pendingStateLock = new object();
    private bool hasPendingRequest;
    private bool pendingIsQuery;
    private float pendingAzimuth;
    private float pendingElevation;
    private IPEndPoint pendingSenderEndpoint;

    void Start()
    {
        // en-US culture info. Required for consistent decimal formatting. (lab convention)
        Thread.CurrentThread.CurrentCulture = new CultureInfo("en-US");
        Thread.CurrentThread.CurrentUICulture = new CultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentCulture = new CultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo("en-US");

        // Find the actual eye-tracker component that is already in the scene.
        // Include inactive ones too, so we still find it if it starts disabled.
        eyeTracker = FindAnyObjectByType<GazeContingentTransform2>(FindObjectsInactive.Include);

        if (eyeTracker == null)
        {
            Debug.LogError("[ExternalEyeTrackerAzimuthElevationAdjuster] No GazeContingentTransform2 found in the scene. " +
                "Put it on the Gaze Contingent GameObject under CenterEyeAnchor (see README step 30). " +
                "Until then the sample window cannot fill and the offset cannot be set; '?' stays unanswered.");
        }
        else
        {
            Debug.Log("[ExternalEyeTrackerAzimuthElevationAdjuster] GazeContingentTransform2 found. " +
                "Current gaze: " + FormatGaze(eyeTracker.CurrentGazeVector) +
                ", offset: " + FormatGaze(eyeTracker.GazeVectorOffset) + ".");
        }

        // Prepare the async listener. Accept packets from any host.
        IPEndPoint azElIPEndPoint = new IPEndPoint(IPAddress.Any, eyeTrackerAzimuthElevationPort);
        azElListener = new UdpClient(azElIPEndPoint);
        azElListener.BeginReceive(new AsyncCallback(AzElListenerHandler), azElListener);

        Debug.Log("[ExternalEyeTrackerAzimuthElevationAdjuster] Listening for gaze control on port " +
            eyeTrackerAzimuthElevationPort + " (window = " + windowFrames + " samples, " +
            "reasonable range = ±" + FormatDegrees(maxAbsAzimuth) + "° az / ±" + FormatDegrees(maxAbsElevation) + "° el).");
    }

    void Update()
    {
        // 1. Sample the current (calibrated) gaze into the rolling window, and run the
        //    log-only out-of-range guard on it.
        if (eyeTracker != null)
        {
            Vector2 currentGaze = eyeTracker.CurrentGazeVector;
            gazeHistory.Enqueue(currentGaze);

            int maxFrames = Mathf.Max(1, windowFrames);
            while (gazeHistory.Count > maxFrames)
            {
                gazeHistory.Dequeue();
            }

            WarnIfGazeOutOfRange(currentGaze);
        }

        // 2. Take a pending request (which arrived off the main thread) and act on it on the main thread.
        bool apply;
        bool isQuery;
        float azimuth;
        float elevation;
        IPEndPoint sender;
        lock (pendingStateLock)
        {
            apply = hasPendingRequest;
            hasPendingRequest = false;
            isQuery = pendingIsQuery;
            azimuth = pendingAzimuth;
            elevation = pendingElevation;
            sender = pendingSenderEndpoint;
        }
        if (!apply)
        {
            return;
        }

        if (isQuery)
        {
            ReplyQuery(sender);
        }
        else
        {
            ApplyGazeOffset(azimuth, elevation);
        }
    }

    // Runs on the callback thread once a packet is buffered.
    void AzElListenerHandler(IAsyncResult result)
    {
        UdpClient socket = result.AsyncState as UdpClient; // original code: https://yal.cc/cs-dotnet-asynchronous-udp-example/
        IPEndPoint requestIPEndPoint = new IPEndPoint(IPAddress.Any, eyeTrackerAzimuthElevationPort); // Accept from any host.
        // EndReceive fills requestIPEndPoint with the SENDER's IP:port — that is where a reply goes.

        byte[] receivedControlWord = socket.EndReceive(result, ref requestIPEndPoint);

        if (receivedControlWord != null && receivedControlWord.Length > 0)
        {
            // Lab convention: plain-ASCII payload.
            string payload = Encoding.ASCII.GetString(receivedControlWord).Trim();

            if (payload == "?")
            {
                // Query: the reply is built and sent in Update() on the main thread.
                lock (pendingStateLock)
                {
                    pendingIsQuery = true;
                    pendingSenderEndpoint = requestIPEndPoint;
                    hasPendingRequest = true;
                }
            }
            else if (TryParseSetPayload(payload, out float azimuth, out float elevation))
            {
                // Set: the offset is applied in Update() on the main thread. (No reply.)
                lock (pendingStateLock)
                {
                    pendingIsQuery = false;
                    pendingAzimuth = azimuth;
                    pendingElevation = elevation;
                    hasPendingRequest = true;
                }
            }
            else
            {
                Debug.LogWarning("[ExternalEyeTrackerAzimuthElevationAdjuster] Could not parse payload '" + payload +
                    "'. Expected '?' or '<azimuth>,<elevation>' (e.g. '0.5,-0.1').");
            }
        }

        // Once the transfer is done, restart the receive process again.
        socket.BeginReceive(new AsyncCallback(AzElListenerHandler), socket);
    }

    // '<azimuth>,<elevation>' -> two finite floats, en-US decimals, whitespace-tolerant.
    static bool TryParseSetPayload(string payload, out float azimuth, out float elevation)
    {
        azimuth = 0f;
        elevation = 0f;

        string[] parts = payload.Split(',');
        if (parts.Length != 2)
        {
            return false;
        }

        // Pass the culture explicitly: the callback runs on a pool thread, whose locale we
        // cannot rely on being en-US even though Start() pins it for new threads.
        System.Globalization.CultureInfo enUs = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        if (!float.TryParse(parts[0].Trim(), NumberStyles.Float, enUs, out azimuth) ||
            !float.TryParse(parts[1].Trim(), NumberStyles.Float, enUs, out elevation))
        {
            return false;
        }

        // Float.TryParse also accepts "NaN" / "Infinity"; reject those — a non-finite offset
        // would poison the gaze (and its average) for the rest of the trial.
        return !float.IsNaN(azimuth) && !float.IsInfinity(azimuth) &&
               !float.IsNaN(elevation) && !float.IsInfinity(elevation);
    }

    // Apply the requested offset to the eye tracker. (Main thread only.)
    void ApplyGazeOffset(float azimuth, float elevation)
    {
        if (eyeTracker == null)
        {
            Debug.LogWarning("[ExternalEyeTrackerAzimuthElevationAdjuster] Asked to set the gaze offset to " +
                FormatGaze(new Vector2(azimuth, elevation)) + ", but no GazeContingentTransform2 was found; ignoring.");
            return;
        }

        // Deliberately NO clamp: the offset is a *correction* by design, and a legitimate
        // correction can lie outside any "reasonable" box — a silent clamp would break the
        // calibration loop. The only guard is the finiteness check at parse time.
        // (ApplyGazeOffset also logs the applied pair on its side.)
        eyeTracker.ApplyGazeOffset(new Vector2(azimuth, elevation));
    }

    // Log once on entering / once on leaving the state where the (calibrated) gaze is
    // outside the reasonable box; never per frame. Log-only: the samples kept, the
    // average reported, and everything else continues to be the plain data.
    void WarnIfGazeOutOfRange(Vector2 gaze)
    {
        bool outOfRange = Mathf.Abs(gaze.x) > maxAbsAzimuth || Mathf.Abs(gaze.y) > maxAbsElevation;

        if (outOfRange && !gazeIsOutOfRange)
        {
            gazeIsOutOfRange = true;
            Debug.LogWarning("[ExternalEyeTrackerAzimuthElevationAdjuster] Gaze " + FormatGaze(gaze) +
                " is outside the reasonable range (±" + FormatDegrees(maxAbsAzimuth) + "° azimuth, " +
                "±" + FormatDegrees(maxAbsElevation) + "° elevation). Usual culprits: the per-trial offset " +
                "is not set, the calibration is off, or the eye tracker lost lock. " +
                "Average replies while this persists are suspect.");
        }
        else if (!outOfRange && gazeIsOutOfRange)
        {
            gazeIsOutOfRange = false;
            Debug.Log("[ExternalEyeTrackerAzimuthElevationAdjuster] Gaze " + FormatGaze(gaze) +
                " is back inside the reasonable range.");
        }
    }

    // Answer a '?' with the running average of the gaze vector: '<azimuth>,<elevation>'. (Main thread only.)
    // Sent from the SAME socket that received the query, so the reply's source port is 40001 —
    // the exact reverse of the request. (Sending from a different socket would give the reply
    // a fresh ephemeral source port, and the network path drops that as "not a reply".)
    void ReplyQuery(IPEndPoint sender)
    {
        if (sender == null)
        {
            Debug.LogWarning("[ExternalEyeTrackerAzimuthElevationAdjuster] Query received, but no sender to answer it to; staying silent.");
            return;
        }

        if (eyeTracker == null)
        {
            // Nothing to average. A PythonUdpBlaster timeout is the honest
            // "there is no eye tracker to query" signal — never a fake '0.0000,0.0000'.
            Debug.LogWarning("[ExternalEyeTrackerAzimuthElevationAdjuster] Query received, but no GazeContingentTransform2 " +
                "was found in the scene; staying silent.");
            return;
        }

        int maxFrames = Mathf.Max(1, windowFrames);
        if (gazeHistory.Count < maxFrames)
        {
            // Window not full yet (the first few frames after startup). Averaging fewer
            // samples would be a different animal, and would also include the (0,0) seed
            // the field carries before the first valid tracker sample. Stay silent — a
            // query timeout is the honest "not ready yet" signal.
            Debug.LogWarning("[ExternalEyeTrackerAzimuthElevationAdjuster] Query received, but the average window " +
                "is not full yet (" + gazeHistory.Count + "/" + maxFrames + " samples); staying silent.");
            return;
        }

        // Mean of the full window, axis by axis.
        Vector2 sum = Vector2.zero;
        foreach (Vector2 sample in gazeHistory)
        {
            sum += sample;
        }
        Vector2 average = sum / gazeHistory.Count;

        string reply = FormatGaze(average);
        byte[] payloadToSend = Encoding.ASCII.GetBytes(reply);

        try
        {
            azElListener.Send(payloadToSend, payloadToSend.Length, sender);
            Debug.Log("[ExternalEyeTrackerAzimuthElevationAdjuster] Replied to " + sender + " with '" + reply +
                "' (mean of " + gazeHistory.Count + " samples).");
        }
        catch (Exception e)
        {
            Debug.LogError("[ExternalEyeTrackerAzimuthElevationAdjuster] Failed to send the query reply: " + e.Message);
        }
    }

    // '<azimuth>,<elevation>' with four decimals, always en-US formatted.
    static string FormatGaze(Vector2 v)
    {
        System.Globalization.CultureInfo enUs = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        return string.Format(enUs, "{0:0.0000},{1:0.0000}", v.x, v.y);
    }

    // A degree count without trailing zeros, always en-US formatted.
    static string FormatDegrees(float degrees)
    {
        System.Globalization.CultureInfo enUs = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        return degrees.ToString("0.##", enUs);
    }

    void OnDestroy()
    {
        if (azElListener != null)
        {
            azElListener.Close();
        }
    }
}
