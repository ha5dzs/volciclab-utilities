function robot_busy = volciclab_robot_is_busy()
%VOLCICLAB_IS_ROBOT_BUSY 
%   This function returns true if the robot is busy, and false if the robot
%   is not busy.
%   No input arguments required.
% Returns:
%   -True if the robot is busy, false is when the robot is not busy.

     %% Sanity checks.
    volciclab_robot_config;
    %% Do the work
    write(volciclab_robot_config_struct.udp_object, 'Is the robot busy?', volciclab_robot_config_struct.ip_address, volciclab_robot_config_struct.port);
    % Wait for the reply
    pause(volciclab_robot_config_struct.udp_timeout)
    % Get the reply
    reply_string = read(volciclab_robot_config_struct.udp_object, volciclab_robot_config_struct.udp_object.NumBytesAvailable, 'string');
    
    if(strcmp(reply_string, 'The robot is busy.'))
        robot_busy = true;
    else
        robot_busy = false;
    end
end

