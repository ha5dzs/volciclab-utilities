/*
 * This script exposes the two sensitivity thresholds of GazeContingentTransform2
 * to external control over the network:
 *   - EyeTrackingConfidenceThreshold: too low -> the retinal frame is noisy/jerky; too high -> tracking gets dropped.
 *   - EyesAreClosedThreshold: the eyelid weight above which the script treats the eyes as closed (blink gate).
 * These are the knobs to turn when the gaze-contingent stimulus is too jerky in the scene, or
 * slips / sticks around with the eyes blinking.
 *
 * Protocol: one UDP packet per call to the control port, plain-ASCII payload.
 *   - '?'                          -> query: the headset replies to the sender with
 *                                     '<confidence>,<eyesClosed>' (4 decimals, e.g. '0.9000,0.5000')
 *   - '<confidence>,<eyesClosed>'  -> set BOTH thresholds (e.g. '0.95,0.43'); fire-and-forget, no ack
 * (matching the other lab scripts; see README.md - "Network communication protocol")
 *
 * The socket callback runs off the main thread, so it only records the request (last one wins);
 * applying the thresholds and answering a query happen in Update() (the main thread) — touching
 * the scene from a callback thread is not allowed.
 *
 * The reply goes out through THE SAME socket that received the query: the answer's
 * source port is therefore 40000 (the exact reverse of the request's 5-tuple), which is
 * what the headset's AP-mode network path expects for "a reply to a packet we got".
 */

using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

public class ExternalEyeTrackerControl : MonoBehaviour
{
    // Stuff to meddle with in the editor.
    public int eyeTrackerControlPort = 40000;

    // The component whose thresholds we actually control (the scene's eye tracker core).
    private GazeContingentTransform2 eyeTracker;

    // The socket, created in Start(). Both the incoming control packets AND the query
    // replies use it: a reply sent from source port 40000 is the exact reverse of the
    // request's 5-tuple, which is what gets through the AP-mode network path.
    // (Sending from the main thread and receiving via the async callback use separate
    // kernel queues — there is no shared state to corrupt.)
    private UdpClient eyeTrackerControlListener;

    // Request arriving on the callback thread; applied in Update() on the main thread.
    private readonly object pendingStateLock = new object();
    private bool hasPendingRequest;
    private bool pendingIsQuery;
    private float pendingConfidenceThreshold;
    private float pendingEyesAreClosedThreshold;
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
            Debug.LogError("[ExternalEyeTrackerControl] No GazeContingentTransform2 found in the scene. " +
                "Put it on the Gaze Contingent GameObject under CenterEyeAnchor (see README step 30).");
        }
        else
        {
            Debug.Log("[ExternalEyeTrackerControl] GazeContingentTransform2 found. " +
                "Thresholds: confidence=" + eyeTracker.EyeTrackingConfidenceThreshold.ToString("0.0000") +
                ", eyesClosed=" + eyeTracker.EyesAreClosedThreshold.ToString("0.0000") + ".");
        }

        // Prepare the async listener. Accept packets from any host.
        IPEndPoint eyeTrackerControlIPEndPoint = new IPEndPoint(IPAddress.Any, eyeTrackerControlPort);
        eyeTrackerControlListener = new UdpClient(eyeTrackerControlIPEndPoint);
        eyeTrackerControlListener.BeginReceive(new AsyncCallback(EyeTrackerControlListenerHandler), eyeTrackerControlListener);

        Debug.Log("[ExternalEyeTrackerControl] Listening for control on port " + eyeTrackerControlPort + ".");
    }

    void Update()
    {
        // Take a pending request (which arrived off the main thread) and act on it on the main thread.
        bool apply;
        bool isQuery;
        float confidence;
        float eyesClosed;
        IPEndPoint sender;
        lock (pendingStateLock)
        {
            apply = hasPendingRequest;
            hasPendingRequest = false;
            isQuery = pendingIsQuery;
            confidence = pendingConfidenceThreshold;
            eyesClosed = pendingEyesAreClosedThreshold;
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
            ApplyThresholds(confidence, eyesClosed);
        }
    }

    // Runs on the callback thread once a packet is buffered.
    void EyeTrackerControlListenerHandler(IAsyncResult result)
    {
        UdpClient socket = result.AsyncState as UdpClient; // original code: https://yal.cc/cs-dotnet-asynchronous-udp-example/
        IPEndPoint requestIPEndPoint = new IPEndPoint(IPAddress.Any, eyeTrackerControlPort); // Accept from any host.
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
            else if (TryParseSetPayload(payload, out float confidence, out float eyesClosed))
            {
                // Set: both thresholds are applied in Update() on the main thread. (No reply.)
                lock (pendingStateLock)
                {
                    pendingIsQuery = false;
                    pendingConfidenceThreshold = confidence;
                    pendingEyesAreClosedThreshold = eyesClosed;
                    hasPendingRequest = true;
                }
            }
            else
            {
                Debug.LogWarning("[ExternalEyeTrackerControl] Could not parse payload '" + payload +
                    "'. Expected '?' or '<confidence>,<eyesClosed>' (e.g. '0.95,0.43').");
            }
        }

        // Once the transfer is done, restart the receive process again.
        socket.BeginReceive(new AsyncCallback(EyeTrackerControlListenerHandler), socket);
    }

    // '<confidence>,<eyesClosed>' -> two floats, en-US decimals, whitespace-tolerant.
    static bool TryParseSetPayload(string payload, out float confidence, out float eyesClosed)
    {
        confidence = 0f;
        eyesClosed = 0f;

        string[] parts = payload.Split(',');
        if (parts.Length != 2)
        {
            return false;
        }

        // Pass the culture explicitly: the callback runs on a pool thread, whose locale we
        // cannot rely on being en-US even though Start() pins it for new threads.
        System.Globalization.CultureInfo enUs = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        return float.TryParse(parts[0].Trim(), NumberStyles.Float, enUs, out confidence) &&
               float.TryParse(parts[1].Trim(), NumberStyles.Float, enUs, out eyesClosed);
    }

    // Apply the requested thresholds to the eye tracker. (Main thread only.)
    void ApplyThresholds(float confidence, float eyesClosed)
    {
        if (eyeTracker == null)
        {
            Debug.LogWarning("[ExternalEyeTrackerControl] Asked to update the thresholds, but no GazeContingentTransform2 was found; ignoring.");
            return;
        }

        // Both are weights in [0..1]. The [Range(0f, 1f)] attribute on the fields is an
        // Inspector-only decoration — a write straight into the field bypasses it, so clamp here.
        confidence = Mathf.Clamp01(confidence);
        eyesClosed = Mathf.Clamp01(eyesClosed);

        eyeTracker.EyeTrackingConfidenceThreshold = confidence;
        eyeTracker.EyesAreClosedThreshold = eyesClosed;

        Debug.Log("[ExternalEyeTrackerControl] Thresholds updated to " + FormatThresholds() + ".");
    }

    // Answer a '?' with the current thresholds: '<confidence>,<eyesClosed>'. (Main thread only.)
    // Sent from the SAME socket that received the query, so the reply's source port is 40000 —
    // the exact reverse of the request. (Sending from a different socket would give the reply
    // a fresh ephemeral source port, and the network path dropped that as "not a reply".)
    void ReplyQuery(IPEndPoint sender)
    {
        if (eyeTracker == null || sender == null)
        {
            Debug.LogWarning("[ExternalEyeTrackerControl] Query received, but nothing to answer it with; staying silent.");
            return;
        }

        string reply = FormatThresholds();
        byte[] payloadToSend = Encoding.ASCII.GetBytes(reply);

        try
        {
            eyeTrackerControlListener.Send(payloadToSend, payloadToSend.Length, sender);
            Debug.Log("[ExternalEyeTrackerControl] Replied to " + sender + " with '" + reply + "'.");
        }
        catch (Exception e)
        {
            Debug.LogError("[ExternalEyeTrackerControl] Failed to send the query reply: " + e.Message);
        }
    }

    // '<confidence>,<eyesClosed>' with four decimals, always en-US formatted. (Main thread only.)
    string FormatThresholds()
    {
        if (eyeTracker == null)
        {
            return "n/a";
        }

        System.Globalization.CultureInfo enUs = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        return string.Format(enUs, "{0:0.0000},{1:0.0000}",
            eyeTracker.EyeTrackingConfidenceThreshold,
            eyeTracker.EyesAreClosedThreshold);
    }

    void OnDestroy()
    {
        if (eyeTrackerControlListener != null)
        {
            eyeTrackerControlListener.Close();
        }
    }
}
