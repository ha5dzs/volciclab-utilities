function volciclab_robot_freedrive(on_or_off)
%VOLCICLAB_ROBOT_FREEDRIVE Switches the robot's freedrive mode on or off.
% This moves the robot's end to the desired position
%   Input arugments are:
%   -on_or_off, which could be a boolean, a number, or a string:
%       true, 1, 'ON', 'On', 'on' turns on freedrive mode
%       literally anything else turns it off
   
    %% Sanity check.
    % Nothing needed.
    volciclab_robot_config; % Load the structure.
    %% Do the work
    if(ischar(on_or_off))
        % String?
        if((strcmp(on_or_off, 'on')) || (strcmp(on_or_off, 'ON')) || (strcmp(on_or_off, 'On')))
            % turn on freedrive mode
            string_to_send = 'freedrive;ON';
        else
            % turn off freedrive mode
            string_to_send = 'freedrive;Off';
        end
    else
        % Not string. Boolean?
        if((on_or_off))
            % turn on freedrive mode
            string_to_send = 'freedrive;ON';
        else
            % Turn off freedrive mode.
            string_to_send = 'freedrive;Off';
        
        end
    end
    
    % Send the packet
    write(volciclab_robot_config_struct.udp_object, string_to_send, volciclab_robot_config_struct.ip_address, volciclab_robot_config_struct.port)
end

