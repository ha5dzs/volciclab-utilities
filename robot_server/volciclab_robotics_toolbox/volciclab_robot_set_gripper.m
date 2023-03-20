function  volciclab_robot_set_gripper(new_gripper_value)
%VOLCICLAB_ROBOT_SET_GRIPPER This function sets the gripper on the robot.
% IMPORTANT: the gripper must be mounted, connected, and the correct robot
% program must be loaded before this function can be called.
%   % This moves the robot's end to the desired position
%   Input arugments are:
%   -new_gripper_value, which is a number between 0 (open) and 100 (closed).
    %% Sanity checks
    volciclab_robot_config; % Load the conection config structure
    
    if(~isnumeric(new_gripper_value))
        error('gripper_value must be a number.')
    end
    
    if(length(new_gripper_value) ~= 1)
        error('gripper_value must be a single number.')
    end
    
    if( (new_gripper_value > 100) || (new_gripper_value < 0) )
        error('gripper_value must be between 0 and 100.')
    end
    
    %% Do the actual work.
    
    % Create the opcode and argument tuple
    set_gripper_string = sprintf('set_gripper;(%.0f)', new_gripper_value);
    
    % Send tuple over to the network
    write(volciclab_robot_config_struct.udp_object, set_gripper_string, volciclab_robot_config_struct.ip_address, volciclab_robot_config_struct.port)
end

