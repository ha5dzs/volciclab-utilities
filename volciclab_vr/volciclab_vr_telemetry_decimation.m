function  volciclab_vr_telemetry_decimation(udp_port_object, decimation)
%VOLCICLAB_VR_TELEMETRY_DECIMATION Set the DataTransmitter's decimation on the headset.
%   The DataTransmitter (the C# script on the headset, see
%   `unity_docs/DataTransmitter.cs.md`) controls how often the headset's
%   telemetry records are sent to the lab UniversalDataSink:
%
%     - 0: stop streaming (the DataTransmitter will not send any records).
%     - 1: send one record every frame (the densest cadence; on a 90 Hz
%       that's ~90 records per second).
%     - N (N > 1): send one record every Nth frame.
%
%   The cadence change takes effect from the very next rendered frame: the
%   script's guard is `frameCounter % decimation == 0`, so there is no
%   bookkeeping to reset, and `0` stops cleanly (the script guards against
%   divide-by-zero).
%
%   Fire-and-forget: the DataTransmitter's control port (default `40008`) is a
%   *single* unsigned-integer input and it does NOT reply (see the script's own
%   "no ack, no reply socket" note). The change is observable as the next batch
%   of telemetry records landing in the lab datasink's current file.
%
%   Dependency: volciclab_vr_config.m, which must be in the path and set up.
%   This helper uses `headset.ip_address` and
%   `headset.vr_port_telemetry_decimation_control` (port 40008 by default).
%
%   Input arguments:
%     - a pre-initialised udpport object (the *unity* interface —
%       `network_interface_unity` per `create_network_interfaces.m` — because
%       this talks to the headset, not to the datasink).
%     - decimation: a finite non-negative whole number (0 = stop, 1 = every
%       frame, N = every Nth frame).
%
%   Returns:
%     - nothing. The experimenter can watch the lab datasink (see
%       `volciclab_datasink_config`) to confirm records are (or are not)
%       flowing.

    % Correct number of arguments?
    if(nargin ~= 2)
        error('This function must have exactly two input arguments.')
    end

    % Do we get a udpport?
    if~(isvalid(udp_port_object))
        error('The first argument for this function must be a valid udpport object.')
    end

    % Now load the headset structure, and see if we get the correct fields.
    volciclab_vr_config;

    % Do we have an IP address?
    if~(isstring(headset.ip_address) || ischar(headset.ip_address))
        error('volciclab_vr_config.m: the IP address must be a string.')
    end

    % Do we have a port?
    if~(isfield(headset, 'vr_port_telemetry_decimation_control'))
        error('volciclab_vr_config.m: missing field "vr_port_telemetry_decimation_control".')
    end
    telemetry_port = headset.vr_port_telemetry_decimation_control;

    % The decimation must be a finite non-negative whole number.
    % 0 is the STOP token, so we do NOT require decimation > 0.
    if(~(isfinite(decimation) && decimation >= 0 && decimation == floor(decimation)))
        error('The decimation must be a finite non-negative whole number (0 = stop, 1 = every frame, N = every Nth frame).')
    end

    % Assemble the payload: a single unsigned integer, per the script's wire
    % grammar (see `unity_docs/DataTransmitter.cs.md`). No other shape is
    % accepted on this port, and there is no reply.
    payload_to_send = sprintf('%d', decimation);

    % Send it.
    write(udp_port_object, payload_to_send, "string", headset.ip_address, telemetry_port);
    % Clear the output buffer (housekeeping, per the AGENTS.md R2026 quirk —
    % input buffer not involved on a fire-and-forget send).
    flush(udp_port_object, "output");

end
