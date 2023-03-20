function [reply_string] = volciclab_robot_get_status()
%VOLCICLAB_GET_ROBOT_STATUS 
%   This function gets the latest robot status string.
%   I don't think I need to explain this any further, but this is the same
%   string you see in the top left corner of the server window. There you
%   have it! :D
%   No input arguments required.
%   Returns:
%   ...a string with the robot status in it.
%% Sanity checks.
    volciclab_robot_config;
    
    %% Do the work
    write(volciclab_robot_config_struct.udp_object, 'What is the status of the robot?', volciclab_robot_config_struct.ip_address, volciclab_robot_config_struct.port);
    % Wait for the reply
    pause(volciclab_robot_config_struct.udp_timeout)
    % Get the reply
    reply_string = read(volciclab_robot_config_struct.udp_object, volciclab_robot_config_struct.udp_object.NumBytesAvailable, 'string');


end

