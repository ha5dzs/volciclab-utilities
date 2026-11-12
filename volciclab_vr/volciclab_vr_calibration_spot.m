function [calibration_spot] = volciclab_vr_calibration_spot(udp_port_object, varargin)
%VOLCICLAB_CALIBRATION_SPOT This function gets/sets the calibration spot running on the headset.
%   Dependency: volciclab_vr_config.m, which must be in the path, and have them correctly set up.
%   IMPORTANT: A visual disturbance (scotoma or calibration spot) must be a structure.
%              See the definition on volciclab_vr_config.m or the code in this file.
%   Input arguments:
%   -  a pre-initialised udp port interface
%   - [OPTIONAL]: a calibration spot structure
%   Returns
%   It returns the current calibration spot structure. If there is no second argument given, then
%   it will query the headset and returns the calibration spot settings on the headset. If there
%   is a second argument supplied, then it will return the input. Note that for speed,
%   the 'sending' part if this function does not actually verify whether the headset
%   (1) actually received it and (2) applied the changes.
%   You, the experimenter, need to personally verify the calibration spot change via MQDH (MVDH).



    %% Calibration spot structure definition
    calibration_spot = struct;
    calibration_spot.is_rendered = 1; % This is a boolean.
    calibration_spot.azimuth = 0; % In degrees of visual field, with respect to the head
    calibration_spot.elevation = -15; % In degrees of visual field. Slightly downwards.
    calibration_spot.inner_diameter = 6; % In degrees of visual field. 0 = solid disk
    calibration_spot.outer_diameter = 12; % in degrees of visual field.
    calibration_spot.colour = '#006400FF'; % RGBA HTML colours


    %% Sanity checks.

    % Correct number of arguments?
    if(nargin > 2)
        error('This function must have no more than two input arguments.')
    end

    % Do we get an uddport?
    if~(isvalid(udp_port_object))
        error('The first argument for this function must be a valid udpport object.')
    end



    % Now load the headset structure, and see if we get the correct fields.
    volciclab_vr_config;

    % Configure timeout for reception: if this triggers, you'll get a low-level error.
    udp_port_object.Timeout = headset.comm_timeout;

    % Do we have an IP address?
    if~(isstring(headset.ip_address))
        error("volciclab_vr_config.m: the IP address must be a string.")
    end

    %% Do the actual work

    if(nargin == 1)
        % If we got here, we just query the headset, and get our current calibration spot details.
        % Clear the input buffer
        flush(udp_port_object, 'input');
        write(udp_port_object, "?", "string", headset.ip_address, headset.vr_port_stimulus_adjustments_calibration);
        % Clear the output buffer
        flush(udp_port_object, "output");
        % Wait a little bit
        pause(headset.reasonable_response_time/2);
        % Still wait, and throw a warning when the reasonable time is exceed.
        tic;
        warning_thrown = false;
        while~(udp_port_object.NumBytesAvailable)
            % This will loop until either something is in the buffer, or there is a timeout
            if( (toc > headset.reasonable_response_time) && (warning_thrown == false) )
                % If we got here, the headset didn't respond. 
                warning('The headset failed to respond within a reasonable time.');
                warning_thrown = true;
            end

            if(toc > headset.comm_timeout)
                error('No response from the headset when queried. This is a problem, cannot continue.')
            end
        end

        % Parse the contents of the receive buffer.
        if(udp_port_object.NumBytesAvailable)
            % We receive the payload as an array of characters. There is no string terminator.
            received_payload = read(udp_port_object, udp_port_object.NumBytesAvailable, "char")

            % We know that the calibration spot structure has 6 fields, which are comma separated. So, validate and process.
            % Step 1: Split by commas -> cell array of char arrays (the form str2double wants).
            payload_fields = strsplit(received_payload, ',');

            % Step 2: Does it actually has 6 fields? If not, throw a tantrum
            if numel(payload_fields) ~= 6
                sprintf('\tvolciclab_calibration_spot(): Headset sent: %s\n', received_payload)
                error('Calibration spot payload has %d fields, not 6 (got "%s")', numel(payload_fields), received_payload);
            end

            % (3) First field: is_rendered boolean -> must be exactly '0' or '1'.
            if ~strcmpi(payload_fields{1}, '0') && ~strcmpi(payload_fields{1}, '1')
                error('Calibration spot field 1 (is_rendered) must be 0 or 1 (got "%s")', payload_fields{1});
            end
            calibration_spot.is_rendered = str2double(payload_fields{1});

            % Step 4: Start with the simple stuff, text-to-text: Valid HTML colour, '#' followed by exactly 8 hex digits.
            colour = payload_fields{6};
            if numel(colour) ~= 9 || colour(1) ~= '#'
                error('Calibration spot field 6 (colour) is not a valid #RGBA colour (got "%s")', colour);
            end

            calibration_spot.colour = colour; % Toss this string.

            % Step 5: Inside fields: azimuth, elevation, inner_diameter, outer_diameter -> double.
            for k = 2:5
                v = str2double(payload_fields{k});
                if isnan(v)
                    error('Calibration spot field %d is not a number (got "%s")', k, payload_fields{k});
                end
            end
            calibration_spot.azimuth          = str2double(payload_fields{2});
            calibration_spot.elevation        = str2double(payload_fields{3});
            calibration_spot.inner_diameter   = str2double(payload_fields{4});
            calibration_spot.outer_diameter   = str2double(payload_fields{5});

            




        end

        % Flush buffers.
        flush(udp_port_object, "output");
        flush(udp_port_object, "input");

    else

        % If we got here, we need to send the new calibration spot details to the headset.

       
        if( ~isequal( fields(calibration_spot), fields(varargin{1}) ) )
            error('The strcuture input did not match with the structure definition. Try fetching a valid data from the headset.')
        end
        % Now we can copy over.
        calibration_spot = varargin{1};

        % If we have a calibration spot structure to send, assemble the string.

        payload_to_send = sprintf('%d,%0.2f,%0.2f,%0.2f,%0.2f,%s', ...
            calibration_spot.is_rendered, ...
            calibration_spot.azimuth, ...
            calibration_spot.elevation, ...
            calibration_spot.inner_diameter, ...
            calibration_spot.outer_diameter, ...
            calibration_spot.colour);

        % Send it.
        write(udp_port_object, payload_to_send, "string", headset.ip_address, headset.vr_port_stimulus_adjustments_calibration);

    end


end
