function volciclab_robot_move_from_via_to(start_pose, via_pose, end_pose, open_grip_size)
%VOLCICLAB_ROBOT_MOVE_FROM_VIA_TO This function picks up an object from
%start_pose, and takes it to end_pose via the via_pose.
%   Input arguments are:
%       -start_pose, [X,Y,Z,Rx,Ry,Rz]
%       -via_pose, [X,Y,Z,Rx,Ry,Rz]
%       -end_pose, [X,Y,Z,Rx,Ry,Rz]
%       -open_grip_size, which is a number
%        between 0 (totally open) and 100 (totally closed)


%% Sanity checks.

    % Check start_pose
    if(length(start_pose) ~= 6)
        error('The new pose must be a 6 element vector.')
    end
    
    % Check safe_pose
    if(length(via_pose) ~= 6)
        error('The new pose must be a 6 element vector.')
    end
   
    % Check end_pose
    if(length(end_pose) ~= 6)
        error('The new pose must be a 6 element vector.')
    end
    
    if(open_grip_size > 100 || open_grip_size < 0 || ~isnumeric(open_grip_size))
        error('The open_grip_size input argument doesn''t seem to be correct.')
    end

    
%% If we survived this long, then we're in business

    volciclab_robot_config; % Load the configuration structure
    
%% Assemble the string for the server
    % This is a long one.
    move_tcp_string = sprintf('move_from_via_to;(%0.8f, %0.8f, %0.8f, %0.8f, %0.8f, %0.8f, %0.8f, %0.8f, %0.8f, %0.8f, %0.8f, %0.8f, %0.8f, %0.8f, %0.8f, %0.8f, %0.8f, %0.8f, %0.8f)', ...
        start_pose(1), start_pose(2), start_pose(3), start_pose(4), start_pose(5), start_pose(6), ... 
        via_pose(1), via_pose(2), via_pose(3), via_pose(4), via_pose(5), via_pose(6), ...
        end_pose(1), end_pose(2), end_pose(3), end_pose(4), end_pose(5), end_pose(6), ...
        open_grip_size);
    
%% Send the string via the network

    write(  volciclab_robot_config_struct.udp_object,...
            move_tcp_string, ...
            volciclab_robot_config_struct.ip_address, ...
            volciclab_robot_config_struct.port);
end

