/*
 * This script toggles the scene's [BuildingBlock] Passthrough over the network.
 *
 * It targets the OVRPassthroughLayer that the [BuildingBlock] Passthrough building block
 * already placed on its own GameObject in the scene, and activates / deactivates that
 * GameObject. Toggling it runs OVRPassthroughLayer.OnEnable/OnDisable, which is what
 * creates / destroys the OVROverlay proxy that actually renders the passthrough.
 *
 * IMPORTANT: we do NOT `new OVRPassthroughLayer()`. That would create a component that is
 * not attached to any GameObject, and toggling it has no effect on the rendered
 * passthrough at all. We must find and toggle the instance that is already in the scene.
 *
 * Protocol: a single UDP packet to the control port.
 *   - first byte is '0' (0x30 / 48)  ->  passthrough OFF
 *   - anything else                 ->  passthrough ON
 * (lenient, matching the other lab scripts)
 *
 * The socket callback runs off the main thread, so it only records the requested state;
 * the actual component toggle happens in Update() (the main thread) — mutating a Unity
 * component from a callback thread is not allowed.
 */

using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

public class ExternalPassthroughControl : MonoBehaviour
{
    // Stuff to meddle with in the editor.
    // (kept under its original field name so any saved Inspector value is preserved)
    public bool enablePassthrougAtStartup = true;
    public int passthroughControlPort = 40002;

    // The scene's actual passthrough layer (from the [BuildingBlock] Passthrough).
    private OVRPassthroughLayer passthroughLayer;

    // The socket, created in Start().
    private UdpClient passthroughControlListener;

    // Requested state arriving on the callback thread; applied in Update() on the main thread.
    private readonly object pendingStateLock = new object();
    private bool pendingPassthroughEnabled;
    private bool hasPendingPassthroughState;

    void Start()
    {
        // en-US culture info. Required for consistent decimal formatting. (lab convention)
        Thread.CurrentThread.CurrentCulture = new CultureInfo("en-US");
        Thread.CurrentThread.CurrentUICulture = new CultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentCulture = new CultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo("en-US");

        // Find the real [BuildingBlock] Passthrough layer that is already in the scene.
        // Include inactive ones too, so we can still find it — and turn it on — even if it
        // starts disabled.
        passthroughLayer = FindAnyObjectByType<OVRPassthroughLayer>(FindObjectsInactive.Include);

        if (passthroughLayer == null)
        {
            Debug.LogError("[ExternalPassthroughControl] No OVRPassthroughLayer found in the scene. " +
                "Add a [BuildingBlock] Passthrough (or attach an OVRPassthroughLayer component).");
        }
        else
        {
            // Apply the initial state.
            ApplyPassthroughState(enablePassthrougAtStartup);
        }

        // Prepare the async listener. Accept packets from any host.
        IPEndPoint passthroughControlIPEndPoint = new IPEndPoint(IPAddress.Any, passthroughControlPort);
        passthroughControlListener = new UdpClient(passthroughControlIPEndPoint);
        passthroughControlListener.BeginReceive(new AsyncCallback(PassthroughControlListenerHandler), passthroughControlListener);

        Debug.Log("[ExternalPassthroughControl] Listening for passthrough control on port " + passthroughControlPort + ".");
    }

    void Update()
    {
        // Apply any requested state (which arrived off the main thread) on the main thread.
        bool apply;
        bool enable;
        lock (pendingStateLock)
        {
            apply = hasPendingPassthroughState;
            hasPendingPassthroughState = false;
            enable = pendingPassthroughEnabled;
        }
        if (apply)
        {
            ApplyPassthroughState(enable);
        }
    }

    // Runs on the callback thread once a packet is buffered.
    void PassthroughControlListenerHandler(IAsyncResult result)
    {
        UdpClient socket = result.AsyncState as UdpClient; // original code: https://yal.cc/cs-dotnet-asynchronous-udp-example/
        IPEndPoint requestIPEndPoint = new IPEndPoint(IPAddress.Any, passthroughControlPort); // Accept from any host.

        byte[] receivedControlWord = socket.EndReceive(result, ref requestIPEndPoint);

        // Lenient parse: first byte '0' (48) -> off, anything else -> on.
        if (receivedControlWord != null && receivedControlWord.Length > 0)
        {
            bool enable = (receivedControlWord[0] != 48);

            // Record the request; it is applied in Update() on the main thread.
            lock (pendingStateLock)
            {
                pendingPassthroughEnabled = enable;
                hasPendingPassthroughState = true;
            }
        }

        // Once the transfer is done, restart the receive process again.
        socket.BeginReceive(new AsyncCallback(PassthroughControlListenerHandler), socket);
    }

    // Apply the desired passthrough state to the scene's layer. (Main thread only.)
    void ApplyPassthroughState(bool enable)
    {
        if (passthroughLayer == null)
        {
            Debug.LogWarning("[ExternalPassthroughControl] Asked to set passthrough " + (enable ? "on" : "off") + " but no OVRPassthroughLayer was found; ignoring.");
            return;
        }

        if (passthroughLayer.gameObject.activeSelf != enable)
        {
            // Toggling the block's GameObject drives OVRPassthroughLayer.OnEnable/OnDisable,
            // creating / destroying the OVROverlay proxy -> passthrough shown / hidden.
            passthroughLayer.gameObject.SetActive(enable);
            Debug.Log("[ExternalPassthroughControl] Passthrough " + (enable ? "enabled" : "disabled") + ".");
        }
    }

    void OnDestroy()
    {
        if (passthroughControlListener != null)
        {
            passthroughControlListener.Close();
        }
    }
}
