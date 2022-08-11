function [natnet_object] = volciclab_optitrack_init(your_ip_address, session_name)
%VOLCICLAB_OPTITRACK_INIT This function initialises the OptiTrack system
%   Input argumens are:
%       -Your IP address, as a string, such as:
%        '192.168.42.8' or so.
%       -session_name is for Motive to create a directory to store your
%       takes in.
%   Returns
%       -natnet_object, which is the streaming client

    %% Sanity checks
    if(~nargin)
        error('Please specify your computer''s IP address on the local network, as a string.')
    end
    
    if(~ischar(your_ip_address))
        error('Your computer''s IP address must be a string.')
    end
    
    if(~ischar(session_name))
        error('The session name must be a string.')
    end
    
    
    %% Initialise hardware.
    % The stuff is hard-coded, because it is meant to be used locally only.
    
    natnet_object = natnet;
    natnet_object.IsReporting = 0; % keep quiet
    natnet_object.HostIP = '192.168.42.5'; % The Volciclab OptiTrack computer's IP address
    natnet_object.ClientIP = your_ip_address;
    natnet_object.ConnectionType = 'Multicast';
    natnet_object.connect;
   
    %% Check connection
    if(natnet_object.IsConnected)
        pause(1); % I saw that if you are too quick here, you can't immediately fetch stuff
        natnet_object.liveMode; % Put Motive to Live mode, to stream coordinates.
        fprintf('[OptiTrack]: We are good to go.\n')
    else
        error('Couldn''t connect to the streaming server.')
    end
 
    %% Add session name.
    
    natnet_object.sendMessageAndWait(sprintf('SetCurrentSession,%s', session_name));
end

