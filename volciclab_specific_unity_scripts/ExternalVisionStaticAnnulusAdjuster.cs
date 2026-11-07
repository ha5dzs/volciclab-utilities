/*
 * This script exposes the two VisionStaticAnnulus instances that are already in the
 * scene to external control over the local network — one per control port, per the lab
 * convention — on the two next-free ports:
 *   - 40006 -> 'Scotoma' (child of GazeContingentGameObject): the GAZE-CONTINGENT annulus.
 *              Its center (azimuth/elevation) is retinotopic: relative to the fovea (the
 *              gaze), not to the head.
 *   - 40007 -> 'EyeTrackerCalibrator' (child of HeadContingentGameObject): the HEAD-
 *              CONTINGENT annulus. Its center is head-centered: locked to the head frame.
 *              This is the target the eye-tracker calibration loop (README § "Scotoma
 *              and the calibration stimulus...") measures against.
 *
 * One script, two sockets. Deliberate: the two annuli are one control surface over two
 * different frames, and the calibration loop needs both in a few-packet cycle (the
 * headset's eye tracker is a character-animation sensor, not a scientific instrument —
 * recalibrations are frequent). ONE instance of this MonoBehaviour owns both ports.
 *
 * HARD RULE that design comes with: each socket gets its OWN pending-request slot (lock +
 * fields + sender endpoint) — see AnnulusSocket below. The two UdpClient callbacks run on
 * pool threads and interleave arbitrarily; a shared "last one wins" slot would let a
 * 40006 packet overwrite a 40007 one between receive and apply, and the reply would leave
 * out the WRONG socket (source-port mismatch → the packet is dropped on the AP-mode path).
 * That is the same family of bug as StreamRigidBodyFromOptitrack's shared `static` receive
 * buffer — the per-instance-state rule, extended here to per-socket state.
 *
 * Protocol (plain-ASCII, one packet per call, same family as 40000/40001):
 *   - '?'
 *       -> query: the headset replies to the sender with the stored state of THAT port's
 *          annulus, in the same six-field order as a set:
 *          '<toggle>,<azimuth>,<elevation>,<inner dia>,<outer dia>,<colour>'
 *          e.g. '1,0.00,-10.00,6.00,10.00,#006400FF'
 *          (toggle 0/1; the four degree values at two decimals, en-US; the colour in
 *          canonical '#RRGGBBAA' with 8-bit channels — byte-exact form in the .cs.md)
 *   - '<toggle>,<az>,<el>,<inner>,<outer>,<colour>'
 *       -> set ALL SIX of that port's annulus. The order is fixed. The values are written
 *          into the VisionStaticAnnulus's own public fields (its Update() picks them up
 *          and re-derives pose and scale from the live anchor depth, so the change is in
 *          force on the next frame — the same zero-coupling design as port 40004).
 *          Fire-and-forget, no ack (matching the other lab scripts).
 *
 * Rejections (the whole payload is discarded: a warning is logged, nothing is applied and
 * nothing is sent — a five-field packet never applies its first five):
 *   * not exactly six comma-separated fields;
 *   * azimuth/elevation/inner/outer not parseable as en-US decimals, or non-finite
 *     (Float.TryParse also accepts 'NaN'/'Infinity' — rejected, same reason as 40001);
 *   * inner diameter LARGER than outer diameter — rejected HERE deliberately instead of
 *     left to the stimulus's clamp: a transposed pair ('...6,10' vs '...10,6') is a typo,
 *     and VisionStaticAnnulus would clamp it into a hairline near-zero-thickness ring,
 *     which reads as "worked but wrong". The discard is in the log, and '?' still
 *     returns the last good state, so it is apparent on the next query too;
 *   * colour not parseable as HTML hex ('#RGB'/'#RRGGBB'/'#RRGGBBAA'; the '#' is
 *     optional to the parser, but the documented payloads keep it).
 *   Toggle field: '0' disables, anything else enables — the 40002/40003 lenient-toggle
 *   convention, extended from one byte to one field.
 *
 * The socket callback runs off the main thread, so it only records the request (last one
 * wins, PER SOCKET); applying to the annulus's fields and answering a query both happen in
 * Update() (the main thread) — touching the scene from a callback thread is not allowed.
 *
 * Replies go out through THE SAME socket that received the query: the answer's source
 * port is therefore 40006 or 40007 (the exact reverse of the request's 5-tuple) — which
 * is what the headset's AP-mode network path expects for "a reply to a packet we got"
 * (the 40000 lesson; see ExternalEyeTrackerControl.cs.md).
 *
 * What this script deliberately does NOT do:
 *   - never write the anchor pivots (GazeContingentGameObject / HeadContingentGameObject):
 *     their transforms are owned by GazeContingentTransform2, and their depth (and thus
 *     the binocular disparity) by port 40004;
 *   - not touch renderQueue: the two instances are set in the editor to unique queues so
 *     the draw order is defined when they overlap (both are ZTest Always);
 *   - not write the material directly: the colour goes into `annulusColor`, and
 *     VisionStaticAnnulus.ApplyVisualState() is the single place that keeps the
 *     Unlit/Crosshair x2 contract (stores C/2).
 *
 * Scene needs: the two named GameObjects with VisionStaticAnnulus attached (README steps
 * 31/41 and 42/43). This script is attached to the 'External Control Scripts' GameObject
 * like the other control scripts — ONE instance in the scene.
 */
using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

public class ExternalVisionStaticAnnulusAdjuster : MonoBehaviour
{
    // The two ports (the two next free after the 40000–40005 block; README port table).
    public int scotomaControlPort = 40006;         // -> the 'Scotoma' annulus (gaze-contingent, retinotopic center)
    public int calibratorControlPort = 40007;      // -> the 'EyeTrackerCalibrator' annulus (head-contingent, head-centered center)

    // The scene's two annulus instances (by name — README steps 31 and 41).
    private VisionStaticAnnulus scotomaAnnulus;
    private VisionStaticAnnulus calibratorAnnulus;

    /*
     * One self-contained state block PER socket: the listening UdpClient plus its pending
     * request slot. The two sockets must not share a pending slot — see the file header
     * (interleaved callbacks = corrupted state + the reply leaving from the wrong socket).
     */
    private sealed class AnnulusSocket
    {
        public readonly int port;
        public readonly VisionStaticAnnulus target;   // null if not found: sets are dropped, queries stay silent
        public readonly UdpClient listener;

        public readonly object pendingLock = new object();
        public bool hasPending;
        public bool pendingIsQuery;
        public bool pendingEnabled;
        public float pendingAzimuth;
        public float pendingElevation;
        public float pendingInnerDiameter;
        public float pendingOuterDiameter;
        public Color pendingColor;
        public IPEndPoint pendingSender;

        // All readonly fields must be set HERE: C# error CS0191 forbids assigning to them
        // from an object initializer (that is a post-construction assignment, not a
        // constructor or declaration-initializer site), so construction goes through this
        // constructor alone.
        public AnnulusSocket(int port, VisionStaticAnnulus target)
        {
            this.port = port;
            this.target = target;
            this.listener = new UdpClient(new IPEndPoint(IPAddress.Any, port));
        }
    }

    private AnnulusSocket scotomaSocket;
    private AnnulusSocket calibratorSocket;

    // en-US culture, fetched explicitly at each use: the callbacks run on pool threads
    // whose locale we cannot rely on being the one Start() pinned (the house pattern —
    // see the sibling control scripts).
    static CultureInfo EnUs => CultureInfo.GetCultureInfo("en-US");

    void Start()
    {
        // en-US culture info. Required for consistent decimal formatting. (lab convention)
        Thread.CurrentThread.CurrentCulture = new CultureInfo("en-US");
        Thread.CurrentThread.CurrentUICulture = new CultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentCulture = new CultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo("en-US");

        // The two named children carry the instances (README steps 31 and 41). Found by
        // name because FindAnyObjectByType<VisionStaticAnnulus> would find only ONE of
        // the two (and which one is not guaranteed) — the same approach
        // ExternalScotomaDistanceAdjuster uses for 'HeadContingentGameObject'.
        GameObject scotomaObject = GameObject.Find("Scotoma");
        scotomaAnnulus = (scotomaObject != null) ? scotomaObject.GetComponent<VisionStaticAnnulus>() : null;
        if (scotomaAnnulus == null)
        {
            Debug.LogError("[ExternalVisionStaticAnnulusAdjuster] No VisionStaticAnnulus on the GameObject named " +
                "'Scotoma' (README step 31, child of GazeContingentGameObject). Port 40006 sets will be dropped " +
                "and '?' will stay unanswered until it exists.");
        }

        GameObject calibratorObject = GameObject.Find("EyeTrackerCalibrator");
        calibratorAnnulus = (calibratorObject != null) ? calibratorObject.GetComponent<VisionStaticAnnulus>() : null;
        if (calibratorAnnulus == null)
        {
            Debug.LogError("[ExternalVisionStaticAnnulusAdjuster] No VisionStaticAnnulus on the GameObject named " +
                "'EyeTrackerCalibrator' (README step 41, child of HeadContingentGameObject). Port 40007 sets will " +
                "be dropped and '?' will stay unanswered until it exists.");
        }

        // One socket per port, each with its own state block. (ONE handler method serves
        // both — result.AsyncState tells the callback which socket, and hence which
        // pending slot, the packet belongs to.)
        scotomaSocket = Listen(scotomaControlPort, scotomaAnnulus);
        calibratorSocket = Listen(calibratorControlPort, calibratorAnnulus);
    }

    // Create the per-socket state block, bind the listener, and start the async receive
    // loop. Accept packets from any host. (The same socket also sends the replies — see
    // the header for why the reply must leave from the listening port.)
    AnnulusSocket Listen(int port, VisionStaticAnnulus target)
    {
        AnnulusSocket state = new AnnulusSocket(port, target);
        state.listener.BeginReceive(new AsyncCallback(ListenerHandler), state);

        Debug.Log("[ExternalVisionStaticAnnulusAdjuster] Listening on port " + port + " for " +
            (target != null
                ? "the annulus '" + target.gameObject.name + "'."
                : "an annulus (TARGET MISSING — see the error above)."));
        return state;
    }

    void Update()
    {
        // Each socket applies its OWN pending request (taken out under its own lock).
        ApplyPending(scotomaSocket);
        ApplyPending(calibratorSocket);
    }

    // Take this socket's pending request (recorded on a callback thread) and act on it
    // here, on the main thread.
    void ApplyPending(AnnulusSocket state)
    {
        bool apply;
        bool isQuery;
        bool enabled;
        float azimuth;
        float elevation;
        float inner;
        float outer;
        Color color;
        IPEndPoint sender;
        lock (state.pendingLock)
        {
            apply = state.hasPending;
            state.hasPending = false;
            isQuery = state.pendingIsQuery;
            enabled = state.pendingEnabled;
            azimuth = state.pendingAzimuth;
            elevation = state.pendingElevation;
            inner = state.pendingInnerDiameter;
            outer = state.pendingOuterDiameter;
            color = state.pendingColor;
            sender = state.pendingSender;
        }
        if (!apply)
        {
            return;
        }

        if (isQuery)
        {
            ReplyQuery(state, sender);
        }
        else
        {
            ApplyAnnulus(state, enabled, azimuth, elevation, inner, outer, color);
        }
    }

    // Runs on the callback thread once a packet is buffered. One handler serves both
    // sockets: result.AsyncState is the AnnulusSocket (UdpClient + pending slot) whose
    // listener received the packet. (Family standard: the yal.cc async-UDP pattern —
    // https://yal.cc/cs-dotnet-asynchronous-udp-example/)
    void ListenerHandler(IAsyncResult result)
    {
        AnnulusSocket state = result.AsyncState as AnnulusSocket;
        UdpClient socket = state.listener;
        IPEndPoint requestIPEndPoint = new IPEndPoint(IPAddress.Any, state.port); // Accept from any host.
        // EndReceive fills requestIPEndPoint with the SENDER's IP:port — that is where the reply goes.

        byte[] receivedControlWord = socket.EndReceive(result, ref requestIPEndPoint);

        if (receivedControlWord != null && receivedControlWord.Length > 0)
        {
            // Lab convention: plain-ASCII payload.
            string payload = Encoding.ASCII.GetString(receivedControlWord).Trim();

            if (payload == "?")
            {
                // Query: the reply is built and sent in Update() on the main thread.
                lock (state.pendingLock)
                {
                    state.pendingIsQuery = true;
                    state.pendingSender = requestIPEndPoint;
                    state.hasPending = true;
                }
            }
            else if (TryParseSetPayload(state, payload,
                out bool enabled, out float azimuth, out float elevation,
                out float inner, out float outer, out Color color))
            {
                // Set: applied in Update() on the main thread. (No reply.) Last one wins —
                // within THIS socket's slot only.
                lock (state.pendingLock)
                {
                    state.pendingIsQuery = false;
                    state.pendingEnabled = enabled;
                    state.pendingAzimuth = azimuth;
                    state.pendingElevation = elevation;
                    state.pendingInnerDiameter = inner;
                    state.pendingOuterDiameter = outer;
                    state.pendingColor = color;
                    state.hasPending = true;
                }
            }
            // A parse failure already logged its warning inside TryParseSetPayload — the
            // whole payload was discarded: state untouched, nothing sent.
        }

        // Once the transfer is done, restart the receive process again.
        socket.BeginReceive(new AsyncCallback(ListenerHandler), state);
    }

    // '<toggle>,<az>,<el>,<inner>,<outer>,<colour>' -> the six fields, fully validated.
    // Toggle: '0' disables, anything else enables (the 40002/40003 lenient-toggle
    // convention, from one byte to one field).
    // Returns false (after a warning) for: != 6 fields; non-en-US or non-finite numerics;
    // inner > outer; a colour that is not HTML hex. Runs on the callback thread: no scene
    // state is touched at all, and the culture is passed explicitly.
    static bool TryParseSetPayload(AnnulusSocket state, string payload, out bool enabled,
        out float azimuth, out float elevation, out float inner, out float outer, out Color color)
    {
        enabled = false;
        azimuth = 0f;
        elevation = 0f;
        inner = 0f;
        outer = 0f;
        color = Color.clear;

        string who = "[ExternalVisionStaticAnnulusAdjuster] (port " + state.port + ")";

        string[] parts = payload.Split(',');
        if (parts.Length != 6)
        {
            Debug.LogWarning(who + " Could not parse payload '" + payload + "'. Expected '?', or six " +
                "comma-separated fields '<toggle>,<az>,<el>,<inner>,<outer>,<colour>' (e.g. " +
                "'1,0,0,1,5,#FF0000FF'); discarding the payload.");
            return false;
        }

        enabled = parts[0].Trim() != "0";

        if (!float.TryParse(parts[1].Trim(), NumberStyles.Float, EnUs, out azimuth) ||
            !float.TryParse(parts[2].Trim(), NumberStyles.Float, EnUs, out elevation) ||
            !float.TryParse(parts[3].Trim(), NumberStyles.Float, EnUs, out inner) ||
            !float.TryParse(parts[4].Trim(), NumberStyles.Float, EnUs, out outer))
        {
            Debug.LogWarning(who + " Could not parse the numeric fields of payload '" + payload +
                "' (en-US decimals required); discarding the payload.");
            return false;
        }

        // Float.TryParse also accepts "NaN"/"Infinity"; reject those — a non-finite angle
        // or diameter would poison the stimulus on the next frame (the 40001 reason).
        if (float.IsNaN(azimuth) || float.IsInfinity(azimuth) ||
            float.IsNaN(elevation) || float.IsInfinity(elevation) ||
            float.IsNaN(inner) || float.IsInfinity(inner) ||
            float.IsNaN(outer) || float.IsInfinity(outer))
        {
            Debug.LogWarning(who + " Rejected payload '" + payload + "': a numeric field is NaN/Infinity. " +
                "Discarding the payload.");
            return false;
        }

        // A stimulus cannot have a bigger hole than itself. Rejected HERE (not left to the
        // stimulus's clamp, which would silently render a hairline ring): a transposed pair
        // is a typo and should be apparent, not decorative. The previous state stands;
        // a '?' at the next opportunity will show it.
        if (inner > outer)
        {
            Debug.LogWarning(who + " Rejected payload '" + payload + "': inner diameter " +
                inner.ToString("0.00", EnUs) + "° is larger than the outer diameter " +
                outer.ToString("0.00", EnUs) + "°. Discarding the payload — nothing was applied; " +
                "the last good state is unchanged (a '?' will show it).");
            return false;
        }

        if (!VisionStaticAnnulus.TryParseHexColor(parts[5], out color))
        {
            Debug.LogWarning(who + " Could not parse colour '" + parts[5].Trim() + "' as HTML hex " +
                "(#RGB / #RRGGBB / #RRGGBBAA — e.g. '#FF0000FF'); discarding the payload.");
            return false;
        }

        return true;
    }

    // Apply the six fields to THIS socket's annulus instance. (Main thread only.)
    // Only the per-instance PUBLIC FIELDS are written; VisionStaticAnnulus.Update() picks
    // them up (pose/scale re-derived from the live anchor depth, colour through the x2
    // contract) — "write the fields, it converges next frame", the same coupling style as
    // port 40004. Deliberately no clamping here for az/el and the diameters beyond the
    // parse-time finiteness/inner<=outer checks: out-of-±89.99° geometry is the stimulus's
    // documented clamp-and-warn territory (and the stored field — what a '?' reports —
    // stays exactly what was set).
    void ApplyAnnulus(AnnulusSocket state, bool enabled, float azimuth, float elevation, float inner, float outer, Color color)
    {
        VisionStaticAnnulus a = state.target;
        if (a == null)
        {
            Debug.LogWarning("[ExternalVisionStaticAnnulusAdjuster] (port " + state.port + ") A set was received, " +
                "but the annulus instance is missing (see the startup error); ignoring.");
            return;
        }

        a.renderEnabled = enabled;
        a.centerAngles = new Vector2(azimuth, elevation);
        a.innerDiameter = inner;
        a.outerDiameter = outer;
        a.annulusColor = color;

        string summary = string.Format(EnUs,
            "enabled={0} az={1:0.00} el={2:0.00} inner={3:0.00} outer={4:0.00} colour={5}",
            enabled ? 1 : 0, azimuth, elevation, inner, outer, FormatHex(color));
        Debug.Log("[ExternalVisionStaticAnnulusAdjuster] (port " + state.port + ", '" + a.gameObject.name +
            "') Applied: " + summary + ".");
    }

    // Answer a '?' with the stored state of THIS socket's annulus. (Main thread only.)
    // Byte-exact reply form: '<0|1>,<az:0.00>,<el:0.00>,<inner:0.00>,<outer:0.00>,#<RRGGBBAA>'
    // e.g. '1,0.00,-10.00,6.00,10.00,#006400FF' — the README's payload examples are the
    // same values in shortened form (the 40001 example '1.5,-6' is the same convention).
    // Sent from the SAME socket that received the query, so the reply's source port is
    // 40006/40007 — the exact 5-tuple reverse of the request.
    void ReplyQuery(AnnulusSocket state, IPEndPoint sender)
    {
        if (sender == null)
        {
            Debug.LogWarning("[ExternalVisionStaticAnnulusAdjuster] (port " + state.port +
                ") Query received, but no sender to answer it to; staying silent.");
            return;
        }

        VisionStaticAnnulus a = state.target;
        if (a == null)
        {
            // Nothing to report: a PythonUdpBlaster timeout is the honest "the instance
            // is not there" signal — never a fabricated state.
            Debug.LogWarning("[ExternalVisionStaticAnnulusAdjuster] (port " + state.port +
                ") Query received, but the annulus instance is missing (see the startup error); staying silent.");
            return;
        }

        string reply = string.Format(EnUs,
            "{0},{1:0.00},{2:0.00},{3:0.00},{4:0.00},{5}",
            a.renderEnabled ? 1 : 0,
            a.centerAngles.x, a.centerAngles.y, a.innerDiameter, a.outerDiameter,
            FormatHex(a.annulusColor));

        byte[] payloadToSend = Encoding.ASCII.GetBytes(reply);

        try
        {
            state.listener.Send(payloadToSend, payloadToSend.Length, sender);
            Debug.Log("[ExternalVisionStaticAnnulusAdjuster] (port " + state.port + ", '" + a.gameObject.name +
                "') Replied to " + sender + " with '" + reply + "'.");
        }
        catch (Exception e)
        {
            Debug.LogError("[ExternalVisionStaticAnnulusAdjuster] (port " + state.port +
                ") Failed to send the query reply: " + e.Message);
        }
    }

    // A Unity Color back to canonical '#RRGGBBAA' (8-bit channels, upper-case hex — the
    // form the README payloads use). Round-trips with TryParseHexColor at 8-bit precision.
    static string FormatHex(Color c)
    {
        int r = Mathf.Clamp(Mathf.RoundToInt(c.r * 255f), 0, 255);
        int g = Mathf.Clamp(Mathf.RoundToInt(c.g * 255f), 0, 255);
        int b = Mathf.Clamp(Mathf.RoundToInt(c.b * 255f), 0, 255);
        int a = Mathf.Clamp(Mathf.RoundToInt(c.a * 255f), 0, 255);
        return "#" + r.ToString("X2", CultureInfo.InvariantCulture) +
               g.ToString("X2", CultureInfo.InvariantCulture) +
               b.ToString("X2", CultureInfo.InvariantCulture) +
               a.ToString("X2", CultureInfo.InvariantCulture);
    }

    void OnDestroy()
    {
        if (scotomaSocket != null && scotomaSocket.listener != null)
        {
            scotomaSocket.listener.Close();
        }
        if (calibratorSocket != null && calibratorSocket.listener != null)
        {
            calibratorSocket.listener.Close();
        }
    }
}
