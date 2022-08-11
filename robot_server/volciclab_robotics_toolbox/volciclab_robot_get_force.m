function robot_force_in_newtons = volciclab_robot_get_force(robot_config_struct)
%VOLCICLAB_GET_ROBOT_FORCE
%   This function gets the current force applied to the robot's tool in
%   Newtons.
%   Input argument is:
%   -robot_config_struct, which has the connection details.
%   Returns:
%   -If the robot is connected and getting data, then the appropriate force
%   is returned.
%   -If the robot is not connected, then it returns a NaN

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
    
    %% Communication.
    
    % Send the query
    write(robot_config_struct.udp_object, 'What is the current force on the robot?', robot_config_struct.ip_address, robot_config_struct.port);
    % Wait for the reply
    pause(robot_config_struct.udp_timeout)
    % Get the reply
    reply_string = read(robot_config_struct.udp_object, robot_config_struct.udp_object.NumBytesAvailable, 'string');
    
    % Convert the reply to a number.
    robot_force_in_newtons = str2double(reply_string);

end

