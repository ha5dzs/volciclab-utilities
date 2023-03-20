function  volciclab_robot_move_tcp_rpy(new_pose)
%VOLCICLAB_ROBOT_MOVE_TCP Instructs the robot to move the tool contact point.
% This moves the robot's end to the desired position
%   Input arugments are:
%   -new_pose, which is a vector with 6 values. These are:
%           -X, Y, Z, which is the tool contact point's coordinates
%           -R, P, Y, which is the tool contact point's rotation in
%            roll-pitch-yaw, and in radians
%   There are no return values, you should see either the robot move, or
%   the instruction being stored on the server.


    %% Sanity checks.
    
    % Check the input argument
    if(length(new_pose) ~= 6)
        error('The new pose must be a 6 element vector.')
    end
    volciclab_robot_config; % Load the connection config structure
    
    %% Assemble the string for the server
    % 4 and 5 are intentionally replaced, because we use RPY, but the robot
    % uses PRY. Nobody is wrong, but it's different standards.
    move_tcp_string = sprintf('move_tcp_rpy;(%0.8f, %0.8f, %0.8f, %0.8f, %0.8f, %0.8f)', ...
        new_pose(1), new_pose(2), new_pose(3), new_pose(5), new_pose(4), new_pose(6) );
    
    %% Send UDP packet.
    
    
    write(volciclab_robot_config_struct.udp_object, move_tcp_string, volciclab_robot_config_struct.ip_address, volciclab_robot_config_struct.port)

end

