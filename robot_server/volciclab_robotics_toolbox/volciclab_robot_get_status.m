function reply_string = volciclab_robot_get_status(robot_config_struct)
%VOLCICLAB_GET_ROBOT_STATUS
%   This function gets the latest robot status string.
%   I don't think I need to explain this any further, but this is the same
%   string you see in the top left corner of the server window. There you
%   have it! :D
%   Input argument is:
%   -robot_config_struct, which has the connection details.
%   Returns:
%   ...a string with the robot status in it.
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
    write(robot_config_struct.udp_object, 'What is the status of the robot?', robot_config_struct.ip_address, robot_config_struct.port);
    % Wait for the reply
    pause(robot_config_struct.udp_timeout)
    % Get the reply
    reply_string = read(robot_config_struct.udp_object, robot_config_struct.udp_object.NumBytesAvailable, 'string');


end

