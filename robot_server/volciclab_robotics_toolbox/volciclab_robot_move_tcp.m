function volciclab_robot_move_tcp(robot_config_struct, new_pose)
%VOLCICLAB_ROBOT_MOVE_TCP Instructs the robot to move the tool contact point.
% This moves the robot's end to the desired position
%   Input arugments are:
%   -robot_config_struct, which tells how the robot is connected.
%   -new_pose, which is a vector with 6 values. These are:
%           -X, Y, Z, which is the tool contact point's coordinates
%           -Rx, Ry, Rz, which is the tool contact point's rotation IN RADIANS!
%   There are no return values, you should see either the robot move, or
%   the instruction being stored on the server.


    %% Sanity checks.

    % Check the input argument
    if(length(new_pose) ~= 6)
        error('The new pose must be a 6 element vector.')
    end

    if(~isstruct(robot_config_struct))
        if(~exist('robot_config_struct.ip_address', 'var') && ...
           ~exist('robot_config_struct.port', 'var') && ...
           ~exist('robot_config_struct.udp_object', 'var') ...
           )
            error('The robot config structure must be properly initialised.')
        end
        error('The robot_config_struct must be a pre-defined structure.')
    end

    %% Assemble the string for the server
    move_tcp_string = sprintf('move_tcp;(%0.8f, %0.8f, %0.8f, %0.8f, %0.8f, %0.8f)', ...
        new_pose(1), new_pose(2), new_pose(3), new_pose(4), new_pose(5), new_pose(6) );

    %% Send UDP packet.


    write(robot_config_struct.udp_object, move_tcp_string, robot_config_struct.ip_address, robot_config_struct.port)

end

