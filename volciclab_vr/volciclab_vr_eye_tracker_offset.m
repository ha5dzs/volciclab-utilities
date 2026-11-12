function [gaze_vector_azimuth, gaze_vector_elevation] = volciclab_vr_eye_tracker_offset(udp_port_object, varargin)
%VOLCICLAB_VR_EYE_TRACKER_OFFSET Query or set the per-trial gaze calibration offset on the headset.
%   The headset (port 40001, 'ExternalEyeTrackerAzimuthElevationAdjuster' in the Unity project)
%   is the read/write half of the eye-tracker calibration loop:
%
%       - QUERY: the headset returns the CALIBRATED gaze — a running average of the last
%         ~10 samples (see the Unity-side doc), the offset already added in, i.e. what the
%         participant sees rendered. In DEGREES of visual field.
%       - SET: writes GazeVectorOffset on GazeContingentTransform2. In DEGREES.
%         Convention: +azimuth = right, +elevation = up (same as the gaze vector).
%         Deliberately NOT clamped (a calibration correction may legitimately sit outside
%         any "reasonable" box — clamping would break the loop).
%
%   The intended calibration loop this helper serves:
%       1. present a stimulus at some (az, el),
%       2. ask the participant to look at it,
%       3. QUERY  -> averaged calibrated gaze,
%       4. compute the offset = (desired - returned),
%       5. SET    -> send the computed offset,
%       6. QUERY  -> should now match the stimulus (a 1:1 shift is the sanity check).
%
%   Dependency: volciclab_vr_config.m, which must be in the path, and have them correctly set up.
%
%   Input arguments:
%       - a pre-initialised udpport network interface
%       - [OPTIONAL] two doubles: gaze_vector_azimuth (deg), gaze_vector_elevation (deg).
%         If both are given, the function sets them on the headset; if neither is given, it
%         queries them instead. (It is an error to give exactly one.)
%   Returns:
%       - [gaze_vector_azimuth, gaze_vector_elevation]:
%           * if called with one argument: the calibrated gaze vector currently in force on the
%             headset (queried; the running average of the last ~10 tracker samples),
%           * if called with two arguments: the two input values, echoed back unchanged.
%             This is a fire-and-forget operation: it is NOT verified that the headset actually
%             received and applied them (same policy as volciclab_vr_eye_tracker_settings.m's
%             set path; verify via a follow-up query if it matters).

    %% Sanity checks.

    % Correct number of arguments? (0 or 2 optional args, not exactly 1)
    if(nargin == 1)
        gaze_vector_azimuth = NaN;
        gaze_vector_elevation = NaN;
        query = true;
    elseif(nargin == 2)
        error('This function requires either 0 or 2 values for the two angles, not exactly 1.')
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
        % Query: read back the running-average calibrated gaze from the headset.
        % Configure timeout for reception: if this triggers, you'll get a low-level error.
        udp_port_object.Timeout = headset.comm_timeout;

        % Clear the input buffer before querying, to avoid stale data.
        flush(udp_port_object, 'input');
        write(udp_port_object, "?", "string", headset.ip_address, headset.vr_port_eye_tracker_gaze_offset);
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

        % Parse the receive buffer: payload is '<azimuth>,<elevation>' (fixed field order,
        % four decimals, per ExternalEyeTrackerAzimuthElevationAdjuster).
        received_payload = read(udp_port_object, udp_port_object.NumBytesAvailable, "char")

        payload_fields = strsplit(received_payload, ',');
        if numel(payload_fields) ~= 2
            sprintf('\tvolciclab_vr_eye_tracker_offset(): Headset sent: %s\n', received_payload)
            error('Gaze vector payload has %d fields, not 2 (got "%s")', numel(payload_fields), received_payload);
        end

        gaze_vector_azimuth = str2double(payload_fields{1});
        gaze_vector_elevation = str2double(payload_fields{2});

        if isnan(gaze_vector_azimuth) || isnan(gaze_vector_elevation)
            error('The headset responded with garbage (not parseable numbers). Received: %s', received_payload)
        end

        % Flush buffers.
        flush(udp_port_object, "output");
        flush(udp_port_object, "input");

    else
        % Set: send a new (azimuth, elevation) calibration offset to the headset.
        % Fire-and-forget, no reply, no read-back.

        % Local sanity check: both values must be finite numbers (degrees).
        % Deliberately NOT clamped: a calibration correction may legitimately be big (the
        % Unity side documents this), so range-checking here would break the loop.
        gaze_vector_azimuth = varargin{1};
        gaze_vector_elevation = varargin{2};
        if( ~isfinite(gaze_vector_azimuth) || ~isfinite(gaze_vector_elevation) )
            error('Both gaze offset values must be finite numbers (degrees).')
        end

        % Assemble the payload: '<azimuth>,<elevation>' — fixed field order, matching the
        % Unity-side convention (azimuth first), formatted at four decimals like the reply.
        payload_to_send = sprintf('%0.4f,%0.4f', ...
            gaze_vector_azimuth, ...
            gaze_vector_elevation);

        % Send it.
        write(udp_port_object, payload_to_send, "string", headset.ip_address, headset.vr_port_eye_tracker_gaze_offset);
        % Clear the output buffer
        flush(udp_port_object, "output");

    end


end
