function volciclab_robot_set_gripper(robot_config_struct, new_gripper_value)
%VOLCICLAB_ROBOT_SET_GRIPPER This function sets the gripper on the robot.
% IMPORTANT: the gripper must be mounted, connected, and the correct robot
% program must be loaded before this function can be called.
%   Input arugments are:
%   -robot_config_struct, which tells how the robot is connected.
%   -new_gripper_value, which is a number between 0 and 100.
    %% Sanity checks
    if(~isstruct(robot_config_struct))
            if(~exist('robot_config_struct.ip_address', 'var') && ...
               ~exist('robot_config_struct.port', 'var') && ...
               ~exist('robot_config_struct.udp_object', 'var') ...
               )
                error('The robot config structure must be properly initialised.')
            end
            error('The robot_config_struct must be a pre-defined structure.')
    end

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
    write(robot_config_struct.udp_object, set_gripper_string, robot_config_struct.ip_address, robot_config_struct.port)
end

