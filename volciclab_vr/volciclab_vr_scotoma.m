function [scotoma_structure] = volciclab_vr_scotoma(udp_port_object, varargin)
%VOLCICLAB_VR_SCOTOMA This function gets/sets the scotoma running on the headset.
%   Dependency: volciclab_vr_config.m, which must be in the path, and have them correctly set up.
%   IMPORTANT: A visual disturbance (scotoma or calibration target) must be a structure.
%              See the definition on volciclab_vr_config.m or the code in this file.
%   Input arguments:
%   -  a pre-initialised udp port interface
%   - [OPTIONAL]: a scotoma structure
%   Returns
%   It reruns the current scotoma structure. If there is no second argument given, then
%   it will query the headset and returns the scotoma settings on the headset. If there
%   is a second argument supplied, then it will return the input. Note that for speed,
%   the 'sending' part if this function does not actually verify whether the headset
%   (1) actually received it and (2) applied the changes.
%   You, the experimenter, need to personally verify the scotoma change via MQDH (MVDH).



    %% Scotoma structure definition
    scotoma_structure = struct;
    scotoma_structure.is_rendered = 1; % This is a boolean.
    scotoma_structure.azimuth = 0; % In degrees of visual field, with respect to the retina
    scotoma_structure.elevation = 0; % In degrees of visual field, with respect to th retina
    scotoma_structure.inner_diameter = 0; % In degrees of visual field. 0 = solid disk
    scotoma_structure.outer_diameter = 5; % in degrees of visual field.
    scotoma_structure.colour = '#AAAAAAFF'; % RGBA HTML colours


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
        % If we got here, we just query the headset, and get our current scotoma details.
        % Clear the input buffer
        flush(udp_port_object, 'input');
        write(udp_port_object, "?", "string", headset.ip_address, headset.vr_port_stimulus_adjustments_scotoma);
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
            received_payload = read(udp_port_object, udp_port_object.NumBytesAvailable, "char");

            % We know that the scotoma structure has 6 fields, which are comma separated. So, validate and process.
            % Step 1: Split by commas -> cell array of char arrays (the form str2double wants).
            payload_fields = strsplit(received_payload, ',');

            % Step 2: Does it actually has 6 fields? If not, throw a tantrum
            if numel(payload_fields) ~= 6
                sprintf('\tvolciclab_vr_scotoma(): Headset sent: %s\n', received_payload)
                error('Scotoma payload has %d fields, not 6 (got "%s")', numel(payload_fields), received_payload);
            end

            % (3) First field: is_rendered boolean -> must be exactly '0' or '1'.
            if ~strcmpi(payload_fields{1}, '0') && ~strcmpi(payload_fields{1}, '1')
                error('Scotoma field 1 (is_rendered) must be 0 or 1 (got "%s")', payload_fields{1});
            end
            scotoma_structure.is_rendered = str2double(payload_fields{1});

            % Step 4: Start with the simple stuff, text-to-text: Valid HTML colour, '#' followed by exactly 8 hex digits.
            colour = payload_fields{6};
            if numel(colour) ~= 9 || colour(1) ~= '#'
                error('Scotoma field 6 (colour) is not a valid #RGBA colour (got "%s")', colour);
            end

            scotoma_structure.colour = colour; % Toss this string.

            % Step 5: Inside fields: azimuth, elevation, inner_diameter, outer_diameter -> double.
            for k = 2:5
                v = str2double(payload_fields{k});
                if isnan(v)
                    error('Scotoma field %d is not a number (got "%s")', k, payload_fields{k});
                end
            end
            scotoma_structure.azimuth          = str2double(payload_fields{2});
            scotoma_structure.elevation        = str2double(payload_fields{3});
            scotoma_structure.inner_diameter   = str2double(payload_fields{4});
            scotoma_structure.outer_diameter   = str2double(payload_fields{5});

            




        end

        % Flush buffers.
        flush(udp_port_object, "output");
        flush(udp_port_object, "input");

    else

        % If we got here, we need to send the new scotoma details to the headset.

       
        if( ~isequal( fields(scotoma_structure), fields(varargin{1}) ) )
            error('The strcuture input did not match with the structure definition. Try fetching a valid data from the headset.')
        end
        
        % Now we can copy over.
        scotoma_structure = varargin{1};

        % If we have a scotoma structure to send, assemble the string.

        payload_to_send = sprintf('%d,%0.2f,%0.2f,%0.2f,%0.2f,%s', ...
            scotoma_structure.is_rendered, ...
            scotoma_structure.azimuth, ...
            scotoma_structure.elevation, ...
            scotoma_structure.inner_diameter, ...
            scotoma_structure.outer_diameter, ...
            scotoma_structure.colour);

        % Send it.
        write(udp_port_object, payload_to_send, "string", headset.ip_address, headset.vr_port_stimulus_adjustments_scotoma);

    end


end