function volciclab_optitrack_start_recording(natnet_object, take_name)
%VOLCICLAB_OPTITRACK_START_RECORDING This function starts a recording.
% You need to have the OptiTrack system initialised and set up for this to
% work.
% Input arguments are:
%   -natnet_object, which is created by volciclab_optitrack_init()
%   -take_name, which will be recording's file name

    %% Sanity check.
    if(nargin ~= 2)
        error('This function needs exactly two input arguments.')
    end
    
    if(~ischar(take_name))
        error('take_name must be a string.')
    end
    
    %% Get to work
    
    natnet_object.sendMessageAndWait(sprintf('setRecordTakeName,%s', take_name));
    natnet_object.startRecord;
    
end

