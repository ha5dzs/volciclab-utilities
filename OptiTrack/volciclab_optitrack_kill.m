function volciclab_optitrack_kill(natnet_object)
%VOLCICLAB_OPTITRACK_KILL kill the OptiTrack system, gracefully.
%   Input argument is:
%       -natnet_oject, which is the object volciclab_optitrack_init() has
%        returned.
%   Return value:
%       [nothing]

    %% Sanity check
    if(~nargin)
        error('This function needs the NatNet object for it to kill it.')
    end
    
    natnet_object.delete;
    fprintf('[OptiTrack]: Disconnected. See you later!\n')
end

