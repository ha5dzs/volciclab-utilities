function robot_busy = volciclab_robot_is_busy(robot_config_struct)
%VOLCICLAB_IS_ROBOT_BUSY 
%   This function returns true if the robot is busy, and false if the robot
%   is not busy.
% Input argument:
%   -robot_config_struct, which has the connection details.
% Returns:
%   -True if the robot is busy, false is when the robot is not busy.

     %% Sanity checks.
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
    write(robot_config_struct.udp_object, 'Is the robot busy?', robot_config_struct.ip_address, robot_config_struct.port);
    % Wait for the reply
    pause(robot_config_struct.udp_timeout)
    % Get the reply
    reply_string = read(robot_config_struct.udp_object, robot_config_struct.udp_object.NumBytesAvailable, 'string');
    
    if(strcmp(reply_string, 'The robot is busy.'))
        robot_busy = true;
    else
        robot_busy = false;
    end
end

