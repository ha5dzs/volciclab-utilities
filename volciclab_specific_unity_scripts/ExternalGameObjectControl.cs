/*
 * ExternalGameObjectControl
 *
 * The manual, network-driven pose controller — the deliberate twin of StreamRigidBodyFromOptitrack.
 * That script MIRRORS an OptiTrack rigid body's 6-DOF pose onto the object every frame (it follows
 * the trackers). HERE you (e.g. Matlab) SET the pose deliberately — one packet, one pose — and can
 * also QUERY it back. Attach the script to the GameObject whose pose you want to drive, and put the
 * visual you care about (the stim mesh, a wall, a spotlight) under it as a child; the children
 * follow this one transform, so you never touch them directly.
 *
 * Units & convention (you do the coordinate-system + concept conversion yourself; this script is a
 * dumb 6-number passthrough and performs NO maths):
 *   - position:    Unity SPACE metres, X right, Y up, Z fwd.
 *                  World space by default; LOCAL space (relative to the parent) if useLocalSpace.
 *   - orientation: Euler angles in DEGREES, Unity's native convention (Quaternion.Euler /
 *                  eulerAngles). The reply reports transform.eulerAngles, i.e. the canonical form
 *                  the object actually holds (values in [0, 360), so a `set(-30)` reads back as
 *                  `330.0000`; gimbal lock at x = ±90° makes y/z degenerate). Accepted as-is —
 *                  this is for simple, mostly single-axis moves, not general rotation algebra.
 *
 * Multi-instance is a first-class requirement and a first-class footgun at once: this script is
 * meant to be attached to SEVERAL GameObjects at once, EACH with its OWN port, set in the editor
 * (e.g. stimulus -> 5001, spotlight -> 5002, wall -> 5003). `controlPort` DEFAULTS to a
 * placeholder (5001) that you are expected to override per instance. If two instances share a port,
 * the second one CANNOT bind; Start() catches that, logs a clear error, and that instance degrades
 * (drops sets, stays silent to '?') instead of throwing, so the rest of the app keeps running.
 *
 * Protocol (plain-ASCII, one packet per call; the read+set query-response family — see README
 * "Network communication protocol"):
 *   - '?'                          -> query: the headset replies to the sender with
 *                                     '<x>,<y>,<z>,<rx>,<ry>,<rz>' (four decimals each; e.g.
 *                                     '0.5000,0.0000,1.0000,0.0000,30.0000,0.0000')
 *   - '<x>,<y>,<z>,<rx>,<ry>,<rz>' -> SET all six (metres + Euler degrees); fire-and-forget, no ack
 *
 * House rules (the whole External* family):
 *   - The socket callback runs off the main thread, so it only records the request (LAST ONE WINS);
 *     the pose is applied and the query answered in Update() on the main thread — touching the scene
 *     from a socket thread is not allowed.
 *   - The reply goes out through THE SAME socket that received it, so the reply's source port is the
 *     control port (the exact 5-tuple reverse of the request) — the 40000 lesson; a second socket
 *     would give the reply an ephemeral source port and the AP-mode path drops it. See
 *     ExternalEyeTrackerControl.cs.md for the full story.
 *   - Payload parsing is strict: it must be exactly six en-US decimals, all finite. Any other shape
 *     (wrong field count, a non-decimal, NaN/Infinity) is discarded whole — never a half-applied pose.
 */

using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

public class ExternalGameObjectControl : MonoBehaviour
{
    // The UDP port THIS instance listens on. A per-instance placeholder (5001 = "5000 is the
    // OptiTrack alignment port, so object-control ports start here"), NOT a protocol allocation.
    // Override per instance in the editor and assign each instance a DIFFERENT port.
    public int controlPort = 5001;

    // World space (transform.position) by default — matches StreamRigidBodyFromOptitrack and is
    // what you want for placing an object at a known place in the scene. Flip to true to drive
    // localPosition/localRotation (relative to the parent) instead. For an unparented root these
    // are identical; they only diverge once the object sits under a parent with its own transform.
    public bool useLocalSpace = false;

    // One UdpClient per instance (NOT shared). Null if the bind failed at startup (duplicate port).
    private UdpClient listener;

    // Request recorded on the callback thread; applied in Update() on the main thread.
    private readonly object pendingStateLock = new object();
    private bool hasPendingRequest;
    private bool pendingIsQuery;
    private float pendingX, pendingY, pendingZ;
    private float pendingRX, pendingRY, pendingRZ;
    private IPEndPoint pendingSenderEndpoint;

    // en-US culture, fetched explicitly at each use: the callbacks run on pool threads whose
    // locale we cannot rely on being the one Start() pinned (the house pattern).
    static CultureInfo EnUs => CultureInfo.GetCultureInfo("en-US");

    void Start()
    {
        // Pin en-US so decimal formatting does not depend on the host's locale. (lab convention)
        Thread.CurrentThread.CurrentCulture = new CultureInfo("en-US");
        Thread.CurrentThread.CurrentUICulture = new CultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentCulture = new CultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo("en-US");

        // Bind the listener. This can FAIL if another instance already owns the port — the
        // multi-instance footgun. We catch it and degrade rather than throw, so a wrongly
        // configured object does not knock the whole app out at startup.
        try
        {
            IPEndPoint endpoint = new IPEndPoint(IPAddress.Any, controlPort);
            listener = new UdpClient(endpoint);
            // The same socket later sends the replies (the 5-tuple-reverse requirement).
            listener.BeginReceive(new AsyncCallback(ListenerHandler), listener);
        }
        catch (Exception bindError)
        {
            listener = null; // mark "no socket" so Update()/ReplyQuery() degrade cleanly
            Debug.LogError("[ExternalGameObjectControl] On object '" + gameObject.name +
                "': could not bind UDP port " + controlPort + " (" + bindError.Message + "). If other " +
                "ExternalGameObjectControl instances exist, this is almost certainly a DUPLICATE PORT -- " +
                "give every instance its OWN port in the editor (e.g. 5001, 5002, 5003, ...). This " +
                "instance will drop sets and stay silent to '?' until the port is freed and the app " +
                "relaunched.");
            return;
        }

        Debug.Log("[ExternalGameObjectControl] On object '" + gameObject.name + "': listening on port " +
            controlPort + " (" + (useLocalSpace ? "local" : "world") + "-space position, Euler degrees).");
    }

    void Update()
    {
        if (listener == null)
        {
            return; // bind failed at startup: no socket -> no pending requests, no reply source.
        }

        // Take the pending request (recorded on a socket thread) and act on it here, on the main thread.
        bool apply;
        bool isQuery;
        float x, y, z, rx, ry, rz;
        IPEndPoint sender;
        lock (pendingStateLock)
        {
            apply = hasPendingRequest;
            hasPendingRequest = false;
            isQuery = pendingIsQuery;
            x = pendingX; y = pendingY; z = pendingZ;
            rx = pendingRX; ry = pendingRY; rz = pendingRZ;
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
            ApplyPose(x, y, z, rx, ry, rz);
        }
    }

    // Runs on the callback thread once a packet is buffered.
    void ListenerHandler(IAsyncResult result)
    {
        UdpClient socket = result.AsyncState as UdpClient; // original pattern: https://yal.cc/cs-dotnet-asynchronous-udp-example/
        IPEndPoint requestIPEndPoint = new IPEndPoint(IPAddress.Any, controlPort); // Accept from any host.
        // EndReceive fills requestIPEndPoint with the SENDER's IP:port — that is where the reply must go.

        byte[] received = socket.EndReceive(result, ref requestIPEndPoint);

        if (received != null && received.Length > 0)
        {
            // Lab convention: plain-ASCII payload.
            string payload = Encoding.ASCII.GetString(received).Trim();

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
            else if (TryParseSetPayload(payload, out float x, out float y, out float z,
                                       out float rx, out float ry, out float rz))
            {
                // Set: applied in Update() on the main thread. (No reply.) Last one wins.
                lock (pendingStateLock)
                {
                    pendingIsQuery = false;
                    pendingX = x; pendingY = y; pendingZ = z;
                    pendingRX = rx; pendingRY = ry; pendingRZ = rz;
                    hasPendingRequest = true;
                }
            }
            else
            {
                Debug.LogWarning("[ExternalGameObjectControl] On object '" + gameObject.name +
                    "': could not parse payload '" + payload + "'. Expected '?' or " +
                    "'<x>,<y>,<z>,<rx>,<ry>,<rz>' (six fields: metres + Euler degrees, e.g. " +
                    "'0.5,0,1,0,30,0'). Discarding -- nothing was applied.");
            }
        }

        // Once the transfer is done, restart the receive process again (keeps the listener alive).
        socket.BeginReceive(new AsyncCallback(ListenerHandler), socket);
    }

    // '<x>,<y>,<z>,<rx>,<ry>,<rz>' -> six finite en-US decimals. Returns false on any problem;
    // the caller logs the discard. Runs on the socket thread: no scene state touched, culture passed
    // explicitly.
    static bool TryParseSetPayload(string payload, out float x, out float y, out float z,
                                   out float rx, out float ry, out float rz)
    {
        x = y = z = rx = ry = rz = 0f;

        string[] parts = payload.Split(',');
        if (parts.Length != 6)
        {
            return false;
        }

        if (!float.TryParse(parts[0].Trim(), NumberStyles.Float, EnUs, out x) ||
            !float.TryParse(parts[1].Trim(), NumberStyles.Float, EnUs, out y) ||
            !float.TryParse(parts[2].Trim(), NumberStyles.Float, EnUs, out z) ||
            !float.TryParse(parts[3].Trim(), NumberStyles.Float, EnUs, out rx) ||
            !float.TryParse(parts[4].Trim(), NumberStyles.Float, EnUs, out ry) ||
            !float.TryParse(parts[5].Trim(), NumberStyles.Float, EnUs, out rz))
        {
            return false;
        }

        // float.TryParse also accepts "NaN"/"Infinity"; a non-finite pose must never be applied.
        return !float.IsNaN(x) && !float.IsInfinity(x) &&
               !float.IsNaN(y) && !float.IsInfinity(y) &&
               !float.IsNaN(z) && !float.IsInfinity(z) &&
               !float.IsNaN(rx) && !float.IsInfinity(rx) &&
               !float.IsNaN(ry) && !float.IsInfinity(ry) &&
               !float.IsNaN(rz) && !float.IsInfinity(rz);
    }

    // Apply the pose to THIS object's transform. (Main thread only.) Deliberately no clamping:
    // the object may legitimately be placed anywhere / anywhere-oriented in the scene; the only
    // guard is the finiteness check at parse time.
    void ApplyPose(float x, float y, float z, float rx, float ry, float rz)
    {
        if (useLocalSpace)
        {
            transform.localPosition = new Vector3(x, y, z);
            transform.localRotation = Quaternion.Euler(rx, ry, rz);
        }
        else
        {
            transform.position = new Vector3(x, y, z);
            transform.rotation = Quaternion.Euler(rx, ry, rz);
        }

        string summary = string.Format(EnUs,
            "pos=({0:0.0000},{1:0.0000},{2:0.0000}) rot=({3:0.0000},{4:0.0000},{5:0.0000})",
            x, y, z, rx, ry, rz);
        Debug.Log("[ExternalGameObjectControl] On object '" + gameObject.name +
            "': applied " + summary + ".");
    }

    // Answer a '?' with the current pose: '<x>,<y>,<z>,<rx>,<ry>,<rz>'. (Main thread only.)
    // Sent from the SAME socket that received the query, so the reply's source port is the control
    // port — the exact reverse of the request.
    void ReplyQuery(IPEndPoint sender)
    {
        if (sender == null)
        {
            Debug.LogWarning("[ExternalGameObjectControl] Query received, but no sender to answer it to; staying silent.");
            return;
        }
        if (listener == null)
        {
            Debug.LogWarning("[ExternalGameObjectControl] Query received, but this instance has no socket " +
                "(bind failed -- a duplicate port?); staying silent.");
            return;
        }

        Vector3 p = useLocalSpace ? transform.localPosition : transform.position;
        Vector3 e = useLocalSpace ? transform.localEulerAngles : transform.eulerAngles;
        string reply = string.Format(EnUs,
            "{0:0.0000},{1:0.0000},{2:0.0000},{3:0.0000},{4:0.0000},{5:0.0000}",
            p.x, p.y, p.z, e.x, e.y, e.z);

        byte[] bytes = Encoding.ASCII.GetBytes(reply);

        try
        {
            listener.Send(bytes, bytes.Length, sender);
            Debug.Log("[ExternalGameObjectControl] On object '" + gameObject.name +
                "': replied to " + sender + " with '" + reply + "'.");
        }
        catch (Exception sendError)
        {
            Debug.LogError("[ExternalGameObjectControl] On object '" + gameObject.name +
                "': failed to send the query reply: " + sendError.Message);
        }
    }

    void OnDestroy()
    {
        if (listener != null)
        {
            listener.Close();
        }
    }
}
