% Pull my finger demo.
% Robot assumes a pose, human pulls the robot, robot switches to freedrive,
% then, rinse and repeat.

clear
instrreset;
clc

volciclab_robot_config; % Initial config structure.
% Just in case, if I left the robot in freedrive mode
volciclab_robot_freedrive(volciclab_robot_config_struct, 0);
% Again, just in case, 
volciclab_robot_set_gripper(volciclab_robot_config_struct, 100);

pull_my_finger_pose = [0.293335,-0.0832708,0.209628,1.21489,1.15469,0.234843];
finger_pulled_pose = [0.312461,-0.101182,0.216425,1.20019,1.14285,0.23504];
move_away_pose1 = [0.242364,-0.0376852,0.3037,0.354478,0.351376,0.259191];
move_away_pose2 = [0.448552,0.136638,0.382161,-1.24751,1.39311,-1.54593];
%move_away_pose3 = [0.0948335,0.310698,0.65105,-1.18436,-0.118871,-0.640755];


force_threshold = 6; % the force threshold

while volciclab_robot_is_connected(volciclab_robot_config_struct)
    % Move robot to position
    volciclab_robot_move_tcp(volciclab_robot_config_struct, pull_my_finger_pose);
    
    % Wait until the robot is ready.
    while volciclab_robot_is_busy(volciclab_robot_config_struct)
    end
    
    % Stay in this loop until 
    current_force = 0; % We will update this.
    clc;
    fprintf('Pull my finger.\n')
    while current_force < force_threshold
        % Stay in this loop until the force exceeds the force
        current_force = volciclab_robot_get_force(volciclab_robot_config_struct);
    end
    fprintf('Woohoo, you got me!\n')
    current_force = 0;
    volciclab_robot_move_tcp(volciclab_robot_config_struct, finger_pulled_pose);
    
    % Set the robot to freedrive
    %volciclab_robot_freedrive(volciclab_robot_config_struct, 1);
    
    %pause(0.5); %Wait a little bit
    
    % Stop freedrive so we can move the robot
    %volciclab_robot_freedrive(volciclab_robot_config_struct, 0);

    % move robot to the away position
    %volciclab_robot_move_tcp(volciclab_robot_config_struct, move_away_pose1);
    volciclab_robot_move_tcp(volciclab_robot_config_struct, move_away_pose2);
    %volciclab_robot_move_tcp(volciclab_robot_config_struct, move_away_pose3);
end
