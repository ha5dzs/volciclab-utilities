function robot_connected = volciclab_robot_is_connected()
%VOLCICLAB_ROBOT_IS_CONNECTED
% This function checks if the robot is connected to the server.
%   No input arguments required.
% Returns:
%   -True if the robot is connected, false is when the robot is not connected.


%% Sanity checks.
    volciclab_robot_config;
%% Do the work.
    write(volciclab_robot_config_struct.udp_object, 'Is the robot connected?', volciclab_robot_config_struct.ip_address, volciclab_robot_config_struct.port);
    % Wait for the reply
    pause(volciclab_robot_config_struct.udp_timeout)
    % Get the reply
    reply_string = read(volciclab_robot_config_struct.udp_object, volciclab_robot_config_struct.udp_object.NumBytesAvailable, 'string');
    
    if(strcmp(reply_string, 'Yes.'))
        robot_connected = true;
    else
        robot_connected = false;
    end
end

