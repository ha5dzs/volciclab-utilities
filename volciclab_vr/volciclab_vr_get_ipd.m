function [ipd] = volciclab_vr_get_ipd(udp_port_object)
%VOLCICLAB_VR_GET_IPD This function reads back the current IPD from the headset.
%   IPD CANNOT be set via the headset: the lenses are not motorised and must
%   be adjusted manually by the person wearing it. This function is read-only.
%   Dependency: volciclab_vr_config.m, which must be in the path, and correctly set up.
%   Input argument:
%   - a pre-initialised udpport object
%   Returns:
%   - the headset's current IPD value (mm)

    % Sanity checks.

    % Correct number of arguments?
    if nargin ~= 1
        error('This function must have exactly one input argument: a valid udpport object.')
    end

    % Do we get a udpport?
    if ~(isvalid(udp_port_object))
        error('The first argument for this function must be a valid udpport object.')
    end

    % Now load the headset structure.
    volciclab_vr_config;

    % Configure timeout for reception: if this triggers, you'll get a low-level error.
    udp_port_object.Timeout = headset.comm_timeout;

    % Do we have an IP address?
    if ~(isstring(headset.ip_address) || ischar(headset.ip_address))
        error('volciclab_vr_config.m: the IP address must be a string.')
    end

    % Query the headset. The protocol is simple: send a question mark, wait a
    % little, then read what's in the receive buffer.

    % Clear the input buffer before querying, to avoid stale data.
    flush(udp_port_object, 'input');
    write(udp_port_object, "?", "string", headset.ip_address, headset.vr_port_ipd_reporter);

    % Give the headset a head start.
    pause(headset.reasonable_response_time / 2);

    % Still waiting: warn once when the reasonable time is exceeded, error on timeout.
    tic;
    warning_thrown = false;
    while ~(udp_port_object.NumBytesAvailable)
        if (toc > headset.reasonable_response_time) && (warning_thrown == false)
            warning('The headset failed to respond within a reasonable time.');
            warning_thrown = true;
        end
        if (toc > headset.comm_timeout)
            error('No response from the headset when queried. This is a problem, cannot continue.')
        end
    end

    % Parse the receive buffer. The payload is an array of characters with no
    % string terminator; the response is a floating-point number.
    received_payload = read(udp_port_object, udp_port_object.NumBytesAvailable, "char");


    % If str2double fails to get the value out, then it leaves it as NaN.
    ipd = str2double(received_payload)*1000;
    

    if isnan(ipd)
        % If it is NaN, then we have a problem.
        fprintf('\tvolciclab_vr_get_ipd(): headset responded to ''?'' with: %s\n', received_payload);
        error('The headset responded with garbage.')
    end

    % Optional sanity check: if the headset reports a physically implausible
    % IPD, warn (but don't fail) — the value is still returned as reported.
    if (ipd < 54.0) || (ipd > 72.4)
        warning('The IPD reported by the headset (%0.1f mm) is outside the plausible range 54.0-72.4 mm.')
    end

    % Flush buffers.
    flush(udp_port_object, "output");
    flush(udp_port_object, "input");
end
