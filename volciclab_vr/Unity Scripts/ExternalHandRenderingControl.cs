/*
 * This script toggles the *visual* rendering of the built-in hand models (the
 * [BuildingBlock] Hand Tracking left/right blocks) over the network, WITHOUT stopping
 * the underlying hand tracking. It leaves the OVRHand / OVRSkeleton / OVRMesh data
 * components running, so pinch and the other hand-tracking data stay available -- it
 * only switches the "renderer" side off and back on.
 *
 * The hand visual is a SkinnedMeshRenderer driven by an OVRMeshRenderer component on the
 * [BuildingBlock] Hand Tracking GameObject. The catch: OVRMeshRenderer (in
 * ConfidenceBehavior.ToggleRenderer mode, which is what the scene uses) sets
 * SkinnedMeshRenderer.enabled to (hand data valid AND high confidence) on every Update.
 * So just hiding the SkinnedMeshRenderer would be overruled on the next frame. The
 * reliable way to switch the hand visual off cleanly, without touching the tracking, is:
 *   1. disable the OVRMeshRenderer component (stops it re-showing the mesh), and
 *   2. hide the SkinnedMeshRenderer (the actual pixels).
 * ...and the reverse to switch it back on.
 *
 * We do NOT deactivate the whole hand-block GameObject: that would also kill the OVRHand
 * *data* component (pinch, hand pose, finger state), which we want to keep.
 *
 * IMPORTANT: we do NOT `new` any of these components. We find the ones already in the
 * scene (FindObjectsByType<OVRMeshRenderer>) -- a `new`-ed component is not attached to a
 * GameObject and toggling it does nothing.
 *
 * Protocol: a single UDP packet to the control port.
 *   - first byte is '0' (0x30 / 48)  -> hand rendering OFF
 *   - anything else                 -> hand rendering ON
 * (lenient, matching the other lab scripts)
 *
 * The socket callback runs off the main thread, so it only records the requested state;
 * the actual component toggles happen in Update() (the main thread) — mutating Unity
 * components from a callback thread is not allowed.
 */

using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

public class ExternalHandRenderingControl : MonoBehaviour
{
    // Stuff to meddle with in the editor.
    // (kept under the original field names so any saved Inspector values are preserved)
    public bool enableHandsAtStartup = true;
    public int handRenderingControlPort = 40003;

    // The scene's hand visuals: one OVRMeshRenderer (the visual driver) + one
    // SkinnedMeshRenderer (the pixels) per hand block.
    private OVRMeshRenderer[] handVisuals;
    private SkinnedMeshRenderer[] handPixelRenderers;

    // The socket, created in Start().
    private UdpClient handRenderingControlListener;

    // Requested state arriving on the callback thread; applied in Update() on the main thread.
    private readonly object pendingStateLock = new object();
    private bool pendingHandsEnabled;
    private bool hasPendingHandsState;

    void Start()
    {
        // en-US culture info. Required for consistent decimal formatting. (lab convention)
        Thread.CurrentThread.CurrentCulture = new CultureInfo("en-US");
        Thread.CurrentThread.CurrentUICulture = new CultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentCulture = new CultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo("en-US");

        // Find the hand visuals that are already in the scene. Each [BuildingBlock] Hand
        // Tracking GameObject carries exactly one OVRMeshRenderer and one SkinnedMeshRenderer.
        // Include inactive ones too, so we can still find them -- and switch them on -- even
        // if a hand block started disabled.
        OVRMeshRenderer[] found = FindObjectsByType<OVRMeshRenderer>(FindObjectsInactive.Include);
        handVisuals = new OVRMeshRenderer[found.Length];
        handPixelRenderers = new SkinnedMeshRenderer[found.Length];
        for (int i = 0; i < found.Length; i++)
        {
            handVisuals[i] = found[i];
            handPixelRenderers[i] = (found[i] != null) ? found[i].GetComponent<SkinnedMeshRenderer>() : null;
        }

        if (handVisuals.Length == 0)
        {
            Debug.LogError("[ExternalHandRenderingControl] No OVRMeshRenderer (hand visual) found in the scene. " +
                "Add a [BuildingBlock] Hand Tracking (left and/or right) to toggle the hand rendering.");
        }
        else
        {
            // Apply the initial state.
            ApplyHandsState(enableHandsAtStartup);
        }

        // Prepare the async listener. Accept packets from any host.
        IPEndPoint requestIPEndPoint = new IPEndPoint(IPAddress.Any, handRenderingControlPort);
        handRenderingControlListener = new UdpClient(requestIPEndPoint);
        handRenderingControlListener.BeginReceive(new AsyncCallback(HandRenderingControlListenerHandler), handRenderingControlListener);

        Debug.Log("[ExternalHandRenderingControl] Listening for hand-rendering control on port " + handRenderingControlPort + " (" + handVisuals.Length + " hand visual(s) found).");
    }

    void Update()
    {
        // Apply any requested state (which arrived off the main thread) on the main thread.
        bool apply;
        bool enable;
        lock (pendingStateLock)
        {
            apply = hasPendingHandsState;
            hasPendingHandsState = false;
            enable = pendingHandsEnabled;
        }
        if (apply)
        {
            ApplyHandsState(enable);
        }
    }

    // Runs on the callback thread once a packet is buffered.
    void HandRenderingControlListenerHandler(IAsyncResult result)
    {
        UdpClient socket = result.AsyncState as UdpClient; // original code: https://yal.cc/cs-dotnet-asynchronous-udp-example/
        IPEndPoint requestIPEndPoint = new IPEndPoint(IPAddress.Any, handRenderingControlPort); // Accept from any host.

        byte[] receivedControlWord = socket.EndReceive(result, ref requestIPEndPoint);

        // Lenient parse: first byte '0' (48) -> hand rendering off, anything else -> on.
        if (receivedControlWord != null && receivedControlWord.Length > 0)
        {
            bool enable = (receivedControlWord[0] != 48);

            // Record the request; it is applied in Update() on the main thread.
            lock (pendingStateLock)
            {
                pendingHandsEnabled = enable;
                hasPendingHandsState = true;
            }
        }

        // Once the transfer is done, restart the receive process again.
        socket.BeginReceive(new AsyncCallback(HandRenderingControlListenerHandler), socket);
    }

    // Switch all hand visuals on/off. (Main thread only.) OVRHand / OVRSkeleton / OVRMesh are left
    // alone, so hand tracking keeps running regardless of the visual state.
    void ApplyHandsState(bool enable)
    {
        if (handVisuals == null || handVisuals.Length == 0)
        {
            Debug.LogWarning("[ExternalHandRenderingControl] Asked to set hand rendering " + (enable ? "on" : "off") + " but no OVRMeshRenderer was found; ignoring.");
            return;
        }

        for (int i = 0; i < handVisuals.Length; i++)
        {
            if (handVisuals[i] == null)
            {
                continue;
            }

            // 1. The visual driver: toggling this first stops it re-enabling the SkinnedMeshRenderer
            //    (ConfidenceBehavior.ToggleRenderer writes SkinnedMeshRenderer.enabled every frame).
            handVisuals[i].enabled = enable;

            // 2. The actual pixels.
            if (handPixelRenderers[i] != null)
            {
                handPixelRenderers[i].enabled = enable;
            }
        }

        Debug.Log("[ExternalHandRenderingControl] Hand rendering " + (enable ? "enabled" : "disabled") + " (hand tracking kept running).");
    }

    void OnDestroy()
    {
        if (handRenderingControlListener != null)
        {
            handRenderingControlListener.Close();
        }
    }
}
