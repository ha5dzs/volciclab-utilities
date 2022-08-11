function volciclab_optitrack_stop_recording(natnet_object)
%VOLCICLAB_OPTITRACK_STOP_RECORDING This is a wrapper function for
%natnet_object.stopRecord. Mostly written for code asthetics.
% Input argument is:
%    -natnet_object, which is created by volciclab_optitrack_init()
% Returns:
%    (nohing)

    %% Sanity check
    if(~nargin)
        error('Please pass the natnet object you want to control.')
    end
    
    %% Do the work
    
    natnet_object.stopRecord;

end

