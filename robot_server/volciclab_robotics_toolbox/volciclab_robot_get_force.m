function robot_force_in_newtons = volciclab_robot_get_force()
%VOLCICLAB_GET_ROBOT_FORCE
%   This function gets the current force applied to the robot's tool in
%   Newtons.
%   No input argument required.
%   Returns:
%   -If the robot is connected and getting data, then the appropriate force
%   is returned.
%   -If the robot is not connected, then it returns a NaN

    %% Sanity checks.
    
    volciclab_robot_config;
    
    
    %% Communication.
    
    % Send the query
    write(volciclab_robot_config_struct.udp_object, 'What is the current force on the robot?', volciclab_robot_config_struct.ip_address, volciclab_robot_config_struct.port);
    % Wait for the reply
    pause(volciclab_robot_config_struct.udp_timeout)
    % Get the reply
    reply_string = read(volciclab_robot_config_struct.udp_object, volciclab_robot_config_struct.udp_object.NumBytesAvailable, 'string');
    
    % Convert the reply to a number.
    robot_force_in_newtons = str2double(reply_string);

end

