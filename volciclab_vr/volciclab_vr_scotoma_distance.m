function [scotoma_distance] = volciclab_vr_scotoma_distance(udp_port_object,varargin)
%VOLCICLAB_VR_SCOTOMA_DISTANCE This function gets or sets the scotoma distance.
%   Dependency: volciclab_vr_config.m, which must be in the path, and have them correctly set up.
%   Input arguments:
%   - a pre-initialised udp port interface
%   - [OPTIONAL]: a scotoma distance specified
%   Returns:
%   If there was no scotoma distance value specified as the second input argument, then
%   it returns the current scotoma distance value from the headset
%   If the second argument is specified, then it sets it to the headset, and returns the same value.

    % Sanity checks.
    
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

    
    % Do the actual work
    if (nargin == 2)
        % If the second argument is specified, then at least throw a warning when the distance is not reasonable.
        if(varargin{1} > 10 || varargin{1} < 0.2)
            fprintf('\tvolciclab_vr_scotoma_diistance(): requested scotoma distance is %0.2f.\n', varargin{1})
            warning('The stomoma distance value seems unreasonable.')
        end

        % Now we can send the new scotoma distance value.

        scotoma_distance = varargin{1};
        string_to_send = sprintf("%0.3f", scotoma_distance);
        write(udp_port_object, string_to_send, "string", headset.ip_address, headset.vr_port_scotoma_distance_adjuster);
    else

        %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%
        % This is just a test implementation to check behaviour.
        % This function is designed to fail in a controlled way.
        % The scotoma distance is NOT designed to be queried.
        % Other features, are.
        % So, there is going to be warning below.
        %%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%%

        warning('The scotoma distance cannot be queried from the headset. This part if the function will fail inevitably.')

        % If no second argument was specified, we just need to query the headset.
        % The protocol is simple: We send a question mark, wait a little bit, and then get what's in the receive buffer.

        
        % Clear the input buffer
        flush(udp_port_object, 'input');
        write(udp_port_object, "?", "string", headset.ip_address, headset.vr_port_scotoma_distance_adjuster);
        % Clear the output buffer
        %flush(udp_port_object, "output");
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

            % Since we know exactly the response format, this is going to be a floating-point number.
            scotoma_distance = str2double(received_payload);
            if(isnan(scotoma_distance))
                % If we got here, we got some garbage and str2double failed.
                fprintf('\tvolciclab_vr_scotoma_distance(): headset responded to ''?'' with: %s\n', received_payload);
                error('The headset responded with garbage.')
            end





    end

    % Flush buffers.
    flush(udp_port_object, "output");
    flush(udp_port_object, "input");
end