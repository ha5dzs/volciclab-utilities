/*
 * This script changes the shared Z-distance (the "depth" / binocular-disparity distance)
 * of the two scotoma anchor GameObjects that are children of CenterEyeAnchor:
 *   - GazeContingentGameObject  (gaze-contingent; its pose is driven every frame by GazeContingentTransform2)
 *   - HeadContingentGameObject  (head-contingent; bare, static)
 *
 * Both are authored at (0, 0, 0.5), i.e. ~50 cm in front of the user. Changing their shared
 * depth in flight rebases the rendered binocular disparity if a participant reports double
 * vision (see README "Network communication protocol", port 40004). A fixed-size object under
 * either anchor (e.g. the test cube) then sits at the new depth and takes the expected
 * perspective change in apparent size, while still staying foveated.
 *
 * IMPORTANT: for the gaze object we do NOT write to its transform.localPosition.z. That
 * transform is RE-WRITTEN EVERY frame by GazeContingentTransform2 from its resting offset
 * (GazeContingentTransform2.scotomaLocalOffset), so a plain .z write would be clobbered next
 * frame and would not even change the radius. We therefore write the new depth into
 * scotomaLocalOffset.z; for the head object we write transform.localPosition.z directly
 * (nothing else moves it).
 *
 * Protocol: one UDP packet per call to the control port, plain-ASCII payload.
 *   - '<depth>'   -> set the shared Z-distance in metres (Unity units), e.g. '0.5' or '1'.
 *                    Fire-and-forget: no handshake, no ack (matches 40002 / 40003).
 *
 * The socket callback runs off the main thread, so it only records the requested value
 * (last one wins); the transform writes happen in Update() (the main thread) — mutating
 * Unity state from a callback thread is not allowed.
 */
using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

public class ExternalScotomaDistanceAdjuster : MonoBehaviour
{
    // Stuff to meddle with in the editor.
    public int scotomaDistanceControlPort = 40004;

    // Sanity bounds for the incoming depth (metres / Unity units). The spec is "0..+ve";
    // minDepth=0.0 admits 0, maxDepth is a soft guardrail against a fat-fingered value.
    public float minDepth = 0.0f;
    public float maxDepth = 3.0f;

    private GazeContingentTransform2 gazeContingent;   // owns the live resting offset of the gaze object
    private Transform headContingent;                    // the bare head-locked object (found by name)

    private UdpClient scotomaDistanceListener;

    // Requested value arriving on the callback thread; applied in Update() on the main thread.
    private readonly object pendingStateLock = new object();
    private bool hasPendingDepth;
    private float pendingDepth;

    void Start()
    {
        // en-US culture info. Required for consistent decimal formatting. (lab convention)
        Thread.CurrentThread.CurrentCulture = new CultureInfo("en-US");
        Thread.CurrentThread.CurrentUICulture = new CultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentCulture = new CultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo("en-US");

        // Gaze object: find the scene's real GazeContingentTransform2 (include inactive), so we
        // drive the offset the eye-tracker actually uses — including if it starts disabled.
        gazeContingent = FindAnyObjectByType<GazeContingentTransform2>(FindObjectsInactive.Include);
        if (gazeContingent == null)
        {
            Debug.LogError("[ExternalScotomaDistanceAdjuster] No GazeContingentTransform2 found in the scene. " +
                "Attach it to GazeContingentGameObject under CenterEyeAnchor (README step 30).");
        }

        // Head object: a bare empty GameObject (no distinguishing component), so find it by name.
        GameObject headGameObject = GameObject.Find("HeadContingentGameObject");
        headContingent = (headGameObject != null) ? headGameObject.transform : null;
        if (headContingent == null)
        {
            Debug.LogError("[ExternalScotomaDistanceAdjuster] No GameObject named 'HeadContingentGameObject' under " +
                "CenterEyeAnchor found (README step 38).");
        }

        // Prepare the async listener. Accept packets from any host.
        IPEndPoint scotomaDistanceIPEndPoint = new IPEndPoint(IPAddress.Any, scotomaDistanceControlPort);
        scotomaDistanceListener = new UdpClient(scotomaDistanceIPEndPoint);
        scotomaDistanceListener.BeginReceive(new AsyncCallback(ScotomaDistanceListenerHandler), scotomaDistanceListener);

        Debug.Log("[ExternalScotomaDistanceAdjuster] Listening for scotoma-distance control on port " +
            scotomaDistanceControlPort + ". Initial depth = " + FormatDepth(CurrentDepth()) + " m.");
    }

    void Update()
    {
        // Take a pending request (which arrived off the main thread) and act on it on the main thread.
        bool apply;
        float depth;
        lock (pendingStateLock)
        {
            apply = hasPendingDepth;
            hasPendingDepth = false;
            depth = pendingDepth;
        }
        if (apply)
        {
            ApplyDepth(depth);
        }
    }

    // Runs on the callback thread once a packet is buffered.
    void ScotomaDistanceListenerHandler(IAsyncResult result)
    {
        UdpClient socket = result.AsyncState as UdpClient; // original code: https://yal.cc/cs-dotnet-asynchronous-udp-example/
        IPEndPoint requestIPEndPoint = new IPEndPoint(IPAddress.Any, scotomaDistanceControlPort); // Accept from any host.

        byte[] receivedControlWord = socket.EndReceive(result, ref requestIPEndPoint);

        if (receivedControlWord != null && receivedControlWord.Length > 0)
        {
            // Lab convention: plain-ASCII payload.
            string payload = Encoding.ASCII.GetString(receivedControlWord).Trim();

            if (TryParseDepthPayload(payload, out float depth))
            {
                // Fire-and-forget: record the request; applied in Update() on the main thread. (No reply.)
                lock (pendingStateLock)
                {
                    pendingDepth = depth;   // last one wins
                    hasPendingDepth = true;
                }
            }
            else
            {
                Debug.LogWarning("[ExternalScotomaDistanceAdjuster] Could not parse payload '" + payload +
                    "'. Expected a single non-negative depth in metres (en-US), e.g. '0.5' or '1'.");
            }
        }

        // Once the transfer is done, restart the receive process again.
        socket.BeginReceive(new AsyncCallback(ScotomaDistanceListenerHandler), socket);
    }

    // '<depth>' -> one finite, non-negative float, en-US decimals, whitespace-tolerant.
    static bool TryParseDepthPayload(string payload, out float depth)
    {
        depth = 0f;

        // Pass the culture explicitly: the callback runs on a pool thread, whose locale we cannot
        // rely on being en-US even though Start() pins it for new threads.
        System.Globalization.CultureInfo enUs = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        if (!float.TryParse(payload, NumberStyles.Float, enUs, out depth))
        {
            return false;
        }

        // Float.TryParse also accepts "NaN" / "Infinity"; reject those and negatives (spec: 0..+ve).
        return !float.IsNaN(depth) && !float.IsInfinity(depth) && depth >= 0f;
    }

    // Float the shared depth onto BOTH anchor objects. (Main thread only.)
    void ApplyDepth(float requestedDepth)
    {
        if (gazeContingent == null && headContingent == null)
        {
            Debug.LogWarning("[ExternalScotomaDistanceAdjuster] Asked to set depth to " + FormatDepth(requestedDepth) +
                " m, but neither target object was found; ignoring.");
            return;
        }

        float depth = Mathf.Clamp(requestedDepth, minDepth, maxDepth);

        // Gaze object: drive the LIVE resting offset (NOT its transform, which is re-written every frame).
        if (gazeContingent != null)
        {
            Vector3 offset = gazeContingent.scotomaLocalOffset;
            offset.z = depth;                    // keep the authored x/y (0, 0); z becomes the new depth
            gazeContingent.scotomaLocalOffset = offset;
        }

        // Head object: nothing else moves it, so write the local Z directly.
        if (headContingent != null)
        {
            Vector3 pos = headContingent.localPosition;
            headContingent.localPosition = new Vector3(pos.x, pos.y, depth);
        }

        if (depth != requestedDepth)
        {
            Debug.LogWarning("[ExternalScotomaDistanceAdjuster] Depth " + FormatDepth(requestedDepth) +
                " m was clamped to " + FormatDepth(depth) + " m (allowed [" + minDepth + ", " + maxDepth + "]).");
        }
        else
        {
            Debug.Log("[ExternalScotomaDistanceAdjuster] Scotoma depth set to " + FormatDepth(depth) +
                " m (gaze + head anchors).");
        }
    }

    // Best-effort read of the current shared depth (for the startup log only).
    float CurrentDepth()
    {
        if (gazeContingent != null)
        {
            return gazeContingent.scotomaLocalOffset.z;
        }
        if (headContingent != null)
        {
            return headContingent.localPosition.z;
        }
        return 0f;
    }

    static string FormatDepth(float depth)
    {
        System.Globalization.CultureInfo enUs = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        return depth.ToString("0.000", enUs);
    }

    void OnDestroy()
    {
        if (scotomaDistanceListener != null)
        {
            scotomaDistanceListener.Close();
        }
    }
}
