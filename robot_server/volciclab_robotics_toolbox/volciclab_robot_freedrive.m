function volciclab_robot_freedrive(robot_config_struct,on_or_off)
%VOLCICLAB_ROBOT_FREEDRIVE Switches the robot's freedrive mode on or off.
%   Input arugments are:
%   -robot_config_struct, which tells how the robot is connected.
%   -on_or_off, which could be a boolean, a number, or a string:
%       true, 1, 'ON', 'On', 'on' turns on freedrive mode
%       literally anything else turns it off

    %% Sanity check.
    if(~isstruct(robot_config_struct))
        if(~exist('robot_config_struct.ip_address', 'var') && ...
           ~exist('robot_config_struct.port', 'var') && ...
           ~exist('robot_config_struct.udp_object', 'var') ...
           )
            error('The robot config structure must be properly initialised.')
        end
        error('The robot_config_struct must be a pre-defined structure.')
    end

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
    write(robot_config_struct.udp_object, string_to_send, robot_config_struct.ip_address, robot_config_struct.port)
end

