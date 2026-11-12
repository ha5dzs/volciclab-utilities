function [eye_tracking_confidence_threshold, eyes_are_closed_threshold] = volciclab_vr_eye_tracker_settings(udp_port_object, varargin)
%VOLCICLAB_VR_EYE_TRACKER_SETTINGS Query or set the eye tracker threshold settings on the headset.
%   The headset (port 40000, 'ExternalEyeTrackerControl' in the Unity project) exposes two
%   values, which are always transferred together, as a pair:
%
%       - eye_tracking_confidence_threshold (0...1): the cutoff on the eye tracker's per-sample
%         confidence; below it, the retinal frame is not updated. Higher = less jerky retinal
%         tracking, lower = more dropped samples.
%       - eyes_are_closed_threshold (0...1): the cutoff on the eyelid-closed weight; above it
%         the frame is held. Lower = scotoma released sooner after a blink; higher = held longer.
%
%   BOTH values are clamped to [0, 1] on the headset side.
%
%   Dependency: volciclab_vr_config.m, which must be in the path, and have them correctly set up.
%
%   Input arguments:
%       - a pre-initialised udpport network interface
%       - [OPTIONAL] two doubles: eye_tracking_confidence_threshold, eyes_are_closed_threshold.
%         If both are given, the function sets them on the headset; if neither is given, it
%         queries them instead. (It is an error to give exactly one.)
%   Returns:
%       - [eye_tracking_confidence_threshold, eyes_are_closed_threshold]:
%           * if called with one argument: the values currently in force on the headset (queried)
%           * if called with two arguments: the two input values, echoed back unchanged.
%             This is a fire-and-forget operation: it is NOT verified that the headset actually
%             received and applied them (same policy as volciclab_vr_scotoma.m's set path;
%             verify visually via MQDH if it matters).

    %% Sanity checks.

    % Correct number of arguments? (0 or 2 optional args, not exactly 1)
    if(nargin == 1)
        eye_tracking_confidence_threshold = NaN;
        eyes_are_closed_threshold = NaN;
        query = true;
    elseif(nargin == 2)
        error('This function requires either 0 or 2 values for the two thresholds, not exactly 1.')
    elseif(nargin == 3)
        query = false;
    else
        error('This function must have either 1 or 3 input arguments.')
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



    %% Do the actual work

    if(query)
        % Query: read back the current pair of thresholds from the headset.
        % Configure timeout for reception: if this triggers, you'll get a low-level error.
        udp_port_object.Timeout = headset.comm_timeout;

        % Clear the input buffer before querying, to avoid stale data.
        flush(udp_port_object, 'input');
        write(udp_port_object, "?", "string", headset.ip_address, headset.vr_port_eye_tracker_settings);
        % Give the headset a head start.
        pause(headset.reasonable_response_time / 2);
        % Still waiting: warn once when the reasonable time is exceeded, error on timeout.
        tic;
        warning_thrown = false;
        while~(udp_port_object.NumBytesAvailable)
            if( (toc > headset.reasonable_response_time) && (warning_thrown == false) )
                warning('The headset failed to respond within a reasonable time.');
                warning_thrown = true;
            end
            if(toc > headset.comm_timeout)
                error('No response from the headset when queried. This is a problem, cannot continue.')
            end
        end

        % Parse the receive buffer: payload is '<confidence>,<eyesClosed>' (fixed field order,
        % four decimals, per ExternalEyeTrackerControl).
        received_payload = read(udp_port_object, udp_port_object.NumBytesAvailable, "char");

        payload_fields = strsplit(received_payload, ',');
        if numel(payload_fields) ~= 2
            sprintf('\tvolciclab_vr_eye_tracker_settings(): Headset sent: %s\n', received_payload)
            error('Eye tracker settings payload has %d fields, not 2 (got "%s")', numel(payload_fields), received_payload);
        end

        eye_tracking_confidence_threshold = str2double(payload_fields{1});
        eyes_are_closed_threshold = str2double(payload_fields{2});

        if isnan(eye_tracking_confidence_threshold) || isnan(eyes_are_closed_threshold)
            error('The headset responded with garbage (not parseable numbers). Received: %s', received_payload)
        end

        % Flush buffers.
        flush(udp_port_object, "output");
        flush(udp_port_object, "input");

    else
        % Set: send a new pair of thresholds to the headset. Fire-and-forget, no reply, no read-back.

        % Local sanity check: both values must be finite numbers within [0, 1].
        eye_tracking_confidence_threshold = varargin{1};
        eyes_are_closed_threshold = varargin{2};
        if( ~isfinite(eye_tracking_confidence_threshold) || ~isfinite(eyes_are_closed_threshold) || ...
             ~((eye_tracking_confidence_threshold >= 0) && (eye_tracking_confidence_threshold <= 1)) || ...
             ~((eyes_are_closed_threshold >= 0) && (eyes_are_closed_threshold <= 1)) )
            error('Both eye tracker thresholds must be finite numbers within [0, 1].')
        end

        % Assemble the payload: '<confidence>,<eyesClosed>' — fixed field order, matching the
        % Unity-side convention (confidence first), formatted at four decimals like the reply.
        payload_to_send = sprintf('%0.4f,%0.4f', ...
            eye_tracking_confidence_threshold, ...
            eyes_are_closed_threshold);

        % Send it.
        write(udp_port_object, payload_to_send, "string", headset.ip_address, headset.vr_port_eye_tracker_settings);
        % Clear the output buffer
        flush(udp_port_object, "output");

    end


end
