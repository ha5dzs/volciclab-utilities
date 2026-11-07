/*
 * This script reports the headset's current "user IPD" over the network on demand:
 *   - The Quest Pro's IPD adjustment is manual (the lenses are NOT motorised), and the value
 *     the participant ends up with is only visible to the person wearing the headset — not
 *     through Cast / Quest Developer Hub.
 *   - The experiment requires knowing that the participant set the optics to their measured
 *     IPD. So: ask, and the headset answers with the value it is rendering with, in metres.
 *
 * Protocol: one UDP packet per call to the control port, plain-ASCII payload.
 *   - '?'  -> query: the headset replies to the sender with the current IPD in metres,
 *             at 3 decimals (1 mm resolution), e.g. '0.063'.
 *   - This port is READ-ONLY by design: a numeric payload is not a legal command and gets
 *     a warning (there is nothing to set — software writes cannot move the optics).
 * (matching the other lab scripts; see README.md - "Network communication protocol")
 *
 * The value is read fresh when the query is answered, so a '?' sent after the participant
 * adjusts the IPD reports the new value, not the one from app start.
 *
 * Note: the headset reports this value only while it is being worn (observed on-device; see
 * the .cs.md). With the headset off, queries time out — that is the expected, correct state,
 * not a fault (and disabling the proximity sensor in Quest Developer Hub changes nothing).
 *
 * The socket callback runs off the main thread, so it only records the request (last one
 * wins); answering the query happens in Update() (the main thread) — touching the scene
 * from a callback thread is not allowed.
 *
 * The reply goes out through THE SAME socket that received the query: the answer's source
 * port is therefore 40005 (the exact reverse of the request's 5-tuple), which is what the
 * headset's AP-mode network path expects for "a reply to a packet we got".
 */

using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using UnityEngine;

public class ExternalInterpupillaryDistanceReporter : MonoBehaviour
{
    // Stuff to meddle with in the editor.
    public int ipdReporterPort = 40005;

    // The single socket: incoming control packets AND query replies both use it (see the
    // file header for why the reply must come from the listening port).
    private UdpClient ipdReporterListener;

    // Request arriving on the callback thread; applied in Update() on the main thread.
    private readonly object pendingStateLock = new object();
    private bool hasPendingQuery;
    private IPEndPoint pendingSenderEndpoint;

    void Start()
    {
        // en-US culture info. Required for consistent decimal formatting. (lab convention)
        Thread.CurrentThread.CurrentCulture = new CultureInfo("en-US");
        Thread.CurrentThread.CurrentUICulture = new CultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentCulture = new CultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo("en-US");

        // Log the value on app start, so the headset log also records what the participant
        // left the IPD set to when the session began.
        float initialMetres = ReadIpdMetres();
        if (initialMetres > 0f)
        {
            Debug.Log("[ExternalInterpupillaryDistanceReporter] IPD at app start: " +
                FormatIpdMetres(initialMetres) + " m.");
        }
        else
        {
            Debug.LogWarning("[ExternalInterpupillaryDistanceReporter] No valid IPD available at app start (the headset reports one " +
                "only while it is being worn); '?' queries will go unanswered until it does.");
        }

        // Prepare the async listener. Accept packets from any host.
        IPEndPoint ipdReporterIPEndPoint = new IPEndPoint(IPAddress.Any, ipdReporterPort);
        ipdReporterListener = new UdpClient(ipdReporterIPEndPoint);
        ipdReporterListener.BeginReceive(new AsyncCallback(IpdReporterListenerHandler), ipdReporterListener);

        Debug.Log("[ExternalInterpupillaryDistanceReporter] Listening for IPD queries on port " + ipdReporterPort + ".");
    }

    void Update()
    {
        // Take a pending request (which arrived off the main thread) and act on it on the main thread.
        bool answer;
        IPEndPoint sender;
        lock (pendingStateLock)
        {
            answer = hasPendingQuery;
            hasPendingQuery = false;
            sender = pendingSenderEndpoint;
        }
        if (answer)
        {
            ReplyQuery(sender);
        }
    }

    // Runs on the callback thread once a packet is buffered.
    void IpdReporterListenerHandler(IAsyncResult result)
    {
        UdpClient socket = result.AsyncState as UdpClient; // original code: https://yal.cc/cs-dotnet-asynchronous-udp-example/
        IPEndPoint requestIPEndPoint = new IPEndPoint(IPAddress.Any, ipdReporterPort); // Accept from any host.
        // EndReceive fills requestIPEndPoint with the SENDER's IP:port — that is where a reply goes.

        byte[] receivedControlWord = socket.EndReceive(result, ref requestIPEndPoint);

        if (receivedControlWord != null && receivedControlWord.Length > 0)
        {
            // Lab convention: plain-ASCII payload.
            string payload = Encoding.ASCII.GetString(receivedControlWord).Trim();

            if (payload == "?")
            {
                // Query: the answer is built and sent in Update() on the main thread.
                lock (pendingStateLock)
                {
                    pendingSenderEndpoint = requestIPEndPoint;
                    hasPendingQuery = true;   // last one wins
                }
            }
            else
            {
                // This port is read-only: there is no set to apply. (Never half-apply.)
                Debug.LogWarning("[ExternalInterpupillaryDistanceReporter] Could not parse payload '" + payload +
                    "'. This port is read-only — send '?' to query the current IPD (metres, e.g. '0.063').");
            }
        }

        // Once the transfer is done, restart the receive process again.
        socket.BeginReceive(new AsyncCallback(IpdReporterListenerHandler), socket);
    }

    // The headset's current "user IPD" in metres (Unity units), or 0f if none is available.
    // Primary source: OVRPlugin.ipd (Meta XR SDK, ovrp_GetUserIPD under the hood) — the
    // system value the OS uses as the user's eye separation for rendering, and the one the
    // IPD slider updates. Units are metres (see FovSimulator.cs, which uses OVRPlugin.ipd
    // as the metres value and multiplies by 1000 for mm, and OVRDebugInfo.cs, which prints
    // OVRManager.profile.ipd * 1000.0 as "IPD (mm)").
    // Fallback: OVRManager.profile.ipd (distance between the render eye poses).
    static float ReadIpdMetres()
    {
        if (OVRPlugin.initialized)
        {
            float metres = OVRPlugin.ipd;
            if (!float.IsNaN(metres) && !float.IsInfinity(metres) && metres > 0f)
            {
                return metres;
            }
        }

        float profileMetres = OVRManager.profile.ipd;
        if (!float.IsNaN(profileMetres) && profileMetres > 0f)
        {
            return profileMetres;
        }

        return 0f;
    }

    // Answer a '?' with the current IPD: '<metres>' at 3 decimals (1 mm resolution). (Main thread only.)
    // Sent from the SAME socket that received the query, so the reply's source port is 40005 —
    // the exact reverse of the request. (Sending from a different socket would give the reply
    // a fresh ephemeral source port, and the network path drops that as "not a reply".)
    void ReplyQuery(IPEndPoint sender)
    {
        if (sender == null)
        {
            Debug.LogWarning("[ExternalInterpupillaryDistanceReporter] Query received, but no sender to answer; staying silent.");
            return;
        }

        float metres = ReadIpdMetres();
        if (metres <= 0f)
        {
            // No value to report: stay silent (a timeout from PythonUdpBlaster is the honest
            // "the headset has no IPD value to give" signal — this is the normal state while
            // the headset is NOT being worn).
            Debug.LogWarning("[ExternalInterpupillaryDistanceReporter] Query received, but no valid IPD was available " +
                "(the headset reports one only while it is being worn); staying silent.");
            return;
        }

        string reply = FormatIpdMetres(metres);
        byte[] payloadToSend = Encoding.ASCII.GetBytes(reply);

        try
        {
            ipdReporterListener.Send(payloadToSend, payloadToSend.Length, sender);
            Debug.Log("[ExternalInterpupillaryDistanceReporter] Replied to " + sender + " with '" + reply + "'.");
        }
        catch (Exception e)
        {
            Debug.LogError("[ExternalInterpupillaryDistanceReporter] Failed to send the query reply: " + e.Message);
        }
    }

    // '<metres>' with three decimals (1 mm resolution), always en-US formatted. (Main thread only.)
    static string FormatIpdMetres(float metres)
    {
        System.Globalization.CultureInfo enUs = System.Globalization.CultureInfo.GetCultureInfo("en-US");
        return string.Format(enUs, "{0:0.000}", metres);
    }

    void OnDestroy()
    {
        if (ipdReporterListener != null)
        {
            ipdReporterListener.Close();
        }
    }
}