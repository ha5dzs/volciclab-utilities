function [no_of_rigid_bodies, rigid_body_ids, rigid_body_names] = volciclab_optitrack_get_rigid_body_info(natnet_object)
%VOLCICLAB_OPTITRACK_GET_RIGID_BODY_INFO This function fetches the number
%of rigid bodies loaded in the system, their IDs and their names too.
%   Input argument is:
%       -natnet_object, which is the object volciclab_optitrack_init()
%        returns when successful.
%   Return values are:
%       -no_of_rigid_bodies, which tells you how many rigid bodies are
%        loaded into the system
%       -rigid_body_ids, which is the unique ID of the rigid body for each
%        rigid body
%       -rigid_body_names is a string array, and these will be names you
%        specified in Motive.

    %% Sanity check.
    
    if(~nargin)
        error('This function needs the NatNet object to work with.')
    end
    
    % That's all I can do.
    
    %% Get frame metadata for the rigid body count.
    
    metadata = natnet_object.getFrameMetaData;
    
    no_of_rigid_bodies = metadata.RigidBodyCount;
    
    %% Get rigid body data

    model_info = natnet_object.getModelDescription;
    fprintf('There are %d rigid bodies loaded in the system:\n', no_of_rigid_bodies)
    rigid_body_ids = double.empty(no_of_rigid_bodies, 0);
    rigid_body_names = string.empty(no_of_rigid_bodies, 0);
    for(i=1:no_of_rigid_bodies)
        rigid_body_ids(i) = model_info.RigidBody(i).ID;
        rigid_body_names(i) = model_info.RigidBody(i).Name;
        
        fprintf('\t -ID: %d,\t name: %s\n', rigid_body_ids(i), rigid_body_names(i))
    end
    
    
end

