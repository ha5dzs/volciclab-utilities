using System;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Threading;
using UnityEngine;

/*
 * This script controls the passthrough over the network.
 */

public class passthroughRemoteControllerScript : MonoBehaviour
{
    // Stuff to meddle with in the editor
    public bool enablePassthrougAtStartup = true;
    public int passthroughControlPort = 7511;

    // Stuff multiple functions need access to
    private UdpClient passthrough_control_listener = new UdpClient();
    private OVRPassthroughLayer passtrhough_layer = new OVRPassthroughLayer();

    // Start is called once before the first execution of Update after the MonoBehaviour is created
    void Start()
    {

        // set culture info. This is required for the decimal formatting.
        Thread.CurrentThread.CurrentCulture = new CultureInfo("en-US");
        Thread.CurrentThread.CurrentUICulture = new CultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentCulture = new CultureInfo("en-US");
        CultureInfo.DefaultThreadCurrentUICulture = new CultureInfo("en-US");

        // Prepare the async listener.
        IPEndPoint passthrough_control_ip_endpoint = new IPEndPoint(IPAddress.Any, passthroughControlPort);
        passthrough_control_listener = new UdpClient(passthrough_control_ip_endpoint);
        passthrough_control_listener.BeginReceive(new AsyncCallback(passtrhough_control_listener_handler), passthrough_control_listener);

        // Set the passtrhough layer to whatever is in the script
        passtrhough_layer.enabled = enablePassthrougAtStartup;
       
    }

    // Update is called once per frame
    void Update()
    {
        // Nothing to do here, this script is event-driven.
    }


    // This function gets executed when there is a packet in the buffer.
    // Original code: https://yal.cc/cs-dotnet-asynchronous-udp-example/
    void passtrhough_control_listener_handler(IAsyncResult result)
    {
        Debug.Log("receive_request_control function called.\n");

        UdpClient socket = result.AsyncState as UdpClient; // set the client to be asynchronous maybe?

        IPEndPoint request_ip_endpoint = new IPEndPoint(IPAddress.Any, passthroughControlPort); // Accept packets only from the server.

        // Apparently there is a bug in Unity, see, from 2010. Hopefully fixed. :)
        // https://discussions.unity.com/t/udp-receive-problems-v2-beginreceive-and-endreceive/410064



        byte[] received_control_word = socket.EndReceive(result, ref request_ip_endpoint);

        /*
         * We can afford to be galavant with this.
         * If the first byte is '0', i.e. 0x30 or 48 decimal
         * then disable the passthrough.
         * Anything else enables it.
         */

        if (received_control_word[0] == 48)
        {
            passtrhough_layer.enabled = false;
        }
        else
        {
            passtrhough_layer.enabled = true;
        }


        // Once the transfer is done, restart the receive process again.
        socket.BeginReceive(new AsyncCallback(passtrhough_control_listener_handler), socket);

    }
}
