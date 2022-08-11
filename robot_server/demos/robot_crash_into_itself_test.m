% Robot crash into itself test
clear
instrreset;
clc

volciclab_robot_config; % Initial config structure.
fprintf('Checking connection to the robot.\n')
tic
while(~volciclab_robot_is_connected(volciclab_robot_config_struct))
    if (mod(toc, 3) == 0)
        fprintf('.')
    end
end
fprintf('\n')

% Just in case, if I left the robot in freedrive mode
volciclab_robot_freedrive(volciclab_robot_config_struct, 0);
% Again, just in case, 
volciclab_robot_set_gripper(volciclab_robot_config_struct, 100);

start_pose = [-0.0799881,0.12426,0.650046,0.0322665,-0.014128,-2.51363];
end_pose = [-0.0976709,0.136665,0.33242,-0.0143164,-0.130097,-2.59161];

volciclab_robot_move_tcp(volciclab_robot_config_struct, start_pose);
volciclab_robot_move_tcp(volciclab_robot_config_struct, end_pose);