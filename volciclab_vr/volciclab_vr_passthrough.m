function  volciclab_vr_passthrough(udp_port_object, passthrough_state)
%VOLCICLAB_VR_PASSTHROUGH Controls the passthrough on the VR headset
%   Dependency: volciclab_vr_config.m, which must be in the path, and have them correctly set up.
%   Input argument:
%       - a pre-initialised udpport network interface
%       -'0', or 'false', for turning the passthrough off, everything else is to turn it on.
%   Returns:
%       -<nothing>, it's a fire-and-forget type communication


    % Sanity checks.

    % Correct number of arguments?
    if(nargin ~= 2)
        error('This function must have two input arguments.')
    end
    
    % Do we get an uddport?
    if~(isvalid(udp_port_object))
        error('The first argument for this function must be a valid udpport object.')
    end



    % Now load the headset structure, and see if we get the correct fields.
    volciclab_vr_config;

    % Do we have an IP address?
    if~(isstring(headset.ip_address))
        error("volciclab_vr_config.m: the IP address must be a string.")
    end


    % Do the actual work

    if( (passthrough_state == false) || passthrough_state == 0 || strcmp(passthrough_state, '0') )
        % If any of these are true, then disable passthrough.
        write(udp_port_object, "0", "string", headset.ip_address, headset.vr_port_passthrough_control);
        % Clear the output buffer
        flush(udp_port_object, "output");
    else
        % Enable passthrough
        write(udp_port_object, "Open sesame", "string", headset.ip_address, headset.vr_port_passthrough_control);
        % Clear the output buffer
        flush(udp_port_object, "output");

    end


end