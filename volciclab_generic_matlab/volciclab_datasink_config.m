function  volciclab_datasink_config(udp_port_object, file_path)
%VOLCICLAB_DATASINK_CONFIG Tell the lab UniversalDataSink where to dump the next recording.
%   The lab UniversalDataSink (a separate program, outside this directory) listens
%   on two ports *on the OptiTrack computer* (a fixed, permanent lab address —
%   `192.168.42.5`):
%
%     - UDP `3430` (control): receives a *file path* (a string) telling the sink
%       where to start writing the next recording. **This helper sends to this
%       port.** Each call reconfigures the sink's *destination*; the headset's
%       `DataTransmitter` (a C# script on the headset, see
%       `unity_docs/DataTransmitter.cs.md`) continues to stream to the data
%       port regardless of which file it is currently being written to.
%     - UDP `3431` (data): the sink receives the headset's telemetry records
%       here (plain-ASCII JSON, one record per packet). The sink appends its
%       own arrival timestamp and a trailing newline to each record. The
%       headset's `DataTransmitter` is configured in the Unity editor to send
%       to `192.168.42.5:3431`.
%
%   This is the "set the destination" half of the datasink protocol; the
%   "start/stop the stream" half is `volciclab_vr_telemetry_decimation` (control
%   port `40008`, on the headset). Use them together:
%
%       volciclab_datasink_config(udp_sink, 'C:/exp/p123/trial01.csv')
%       volciclab_vr_telemetry_decimation(udp_headset, 1)   % start (every frame)
%       ... run the trial ...
%       volciclab_vr_telemetry_decimation(udp_headset, 0)   % stop
%
%   Fire-and-forget: the sink does not reply to a control packet. The change is
%   observable as the next batch of telemetry records landing in the new path.
%
%   Dependency: none — the datasink's IP and port are **lab-fixed constants**
%   (the OptiTrack computer's address, which does not change between
%   experiments). They are deliberately NOT pulled from `volciclab_vr_config.m`
%   (because they are not part of the headset's configuration, and the headset's
%   IP — *which does* change per setup — is a different value on a different
%   host). This is also why the helper's name breaks the `volciclab_vr_*`
%   naming convention used by the other helpers in this directory: it talks to
%   the OptiTrack computer, not to the VR headset.
%
%   Input arguments:
%     - a pre-initialised udpport object (the *datasink* interface —
%       `network_interface_datasink` per `create_network_interfaces.m` —
%       because this talks to the OptiTrack computer, not to the headset).
%     - file_path: a non-empty string or char array — the destination (relative
%       or absolute, as the sink interprets it) where the sink should write the
%       next recording. The sink owns the file-naming and the
%       newline-termination convention; this helper's only job is to carry the
%       string to the right port.
%
%   Returns:
%     - nothing.

    % Lab-fixed constants (the OptiTrack computer's address). Deliberately
    % hard-coded — the OptiTrack computer is a permanent part of the lab
    % infrastructure and does not change between experiments.
    DatasinkIp   = '192.168.42.5';
    DatasinkPort = 3430;

    % Sanity checks.
    if(nargin ~= 2)
        error('This function must have exactly two input arguments.')
    end

    if~(isvalid(udp_port_object))
        error('The first argument for this function must be a valid udpport object.')
    end

    if~(isstring(file_path) || ischar(file_path))
        error('The file path must be a string or a char array.')
    end

    if(numel(file_path) == 0)
        error('The file path must not be empty.')
    end

    % Send. The datasink's control-port payload is a single string (the file
    % path) — no other shape, and no reply.
    write(udp_port_object, file_path, "string", DatasinkIp, DatasinkPort);
    % Clear the output buffer (housekeeping, per the AGENTS.md R2026 quirk —
    % input buffer not involved on a fire-and-forget send).
    flush(udp_port_object, "output");

end
