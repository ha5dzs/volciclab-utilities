function  volciclab_vr_hands(udp_port_object, hands_state)
%VOLCICLAB_VR_HANDS Controls the hand rendering on the VR headset
%   Dependency: volciclab_vr_config.m, which must be in the path, and have it correctly set up.
%   Input argument:
%       - a pre-initialised udpport network interface
%       -'0', or 'false', for turning hand rendering off, everything else is to turn it on.
%   Returns:
%       -<nothing>, it's a fire-and-forget type communication


    % Sanity checks.

    % Correct number of arguments?
    if(nargin ~= 2)
        error('This function must have two input arguments.')
    end
    
    % Do we get a udpport?
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

    if( (hands_state == false) || hands_state == 0 || strcmp(hands_state, '0') )
        % If any of these are true, then disable hand rendering.
        write(udp_port_object, "0", "string", headset.ip_address, headset.vr_port_hand_rendering_control);
        % Clear the output buffer
        flush(udp_port_object, "output");
    else
        % Enable hand rendering
        write(udp_port_object, "Open sesame", "string", headset.ip_address, headset.vr_port_hand_rendering_control);
        % Clear the output buffer
        flush(udp_port_object, "output");

    end


end
