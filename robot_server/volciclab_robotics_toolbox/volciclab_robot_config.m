% Simple robot config script
volciclab_robot_config_struct = struct;

% How much time we should wait for a UDP request reply?
volciclab_robot_config_struct.udp_timeout = 0.1; % In seconds.

% Create the UDP object inside the structure. We use the same timeout value
% for the incoming buffer as well.
volciclab_robot_config_struct.udp_object = udpport("IPV4", "Timeout", volciclab_robot_config_struct.udp_timeout);


% How to connect to the robot server?
volciclab_robot_config_struct.ip_address = '127.0.0.1';
volciclab_robot_config_struct.port = 2501;

