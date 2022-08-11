function [framecounter, timestamp, translation, quaternion] = volciclab_optitrack_get_rigid_body_data(natnet_object, rigid_body_id_or_name)
%VOLCICLAB_OPTITRACK_GET_RIGID_BODY_DATA This function fetches the rigid
%body transforsm from a properly working and initialised OptiTrack system.
%   Input arguments are:
%       -natnet_object, which is created with volciclab_optitrack_init()
%       -rigid_body_id_or_name, which can be a numeric or string array.
%   Returns:
%       -framecounter, which is a sequential number since the take started
%       -timestamp, which is a time in microseconds
%       -translation, which is an (X-Y-Z)*n triplet, for n rigid bodies
%        you have in the system
%        [IMPORTANT]: Set the correct units in Motive for streaming!
%       -quaternion, which is an (qx-qy-qz-qw)*n quatruplet, for n rigid
%        bodies you have in the system

    %% Sanity checks.
    
    if(nargin ~= 2)
        error('This function needs exactly two input arguments: the natnet object, and the rigid body ID(s) or name(s) you want to extract.')
    end

    %% Process the rigid body input argument
    
    input_is_string = false;
    input_is_number = false;
    
    if(isstring(rigid_body_id_or_name))
        input_is_string = true;
    end
    
    if(isnumeric(rigid_body_id_or_name))
        input_is_number = true;
    end
    
    if(input_is_string && input_is_number)
        error('Input argument is both a number AND character? What sorcery is this?')
    end
    
    if(~input_is_string && ~ input_is_number)
        error('The rigid_body_id_or_name must be either a string or numeric.')
    end
    
    %% Get metadata, so we can assign the framecounter and dig out the rigid bodies and their IDs
    
    metadata = natnet_object.getFrameMetaData;
    
    % I will get these when actually requesting a frame.
    %framecounter = metadata.Frame; % Framecounter
    %timestamp = metadata.CameraDataReceivedTimestamp; % I think this is in microseconds.
    
    no_of_rigid_bodies = metadata.RigidBodyCount; % For the santiy check
    
    %% Additional sanity check
    if(length(rigid_body_id_or_name) > no_of_rigid_bodies)
        fprintf('Number of rigid bodies in the system: %d; numer of rigid bodies requested: %d;', no_of_rigid_bodies, length(rigid_body_id_or_name))
        error('You are requesting more rigid bodies than what is loaded in the system!')
    end
    
    
    %% If required, convert text to rigid body IDs.
    
    rigid_body_ids = int32.empty(no_of_rigid_bodies, 0);
    
    % Of course, we only need to do this when 
    if(input_is_string)
         model_info = natnet_object.getModelDescription;
         
         for(i=1:length(rigid_body_id_or_name))
             
             % The index in the structure array seem to correspond with the
             % IDs of the rigid bodies. However, I am not sure if this is
             % going to be the case in the future, so I am fetching the
             % correct ID that is required.
             index_in_structure_array = find(strcmp({model_info.RigidBody.Name}, rigid_body_id_or_name(i))==1); 
             
             % Now that we have the rigid body indices, we can assign their
             % IDs.
             rigid_body_ids(i) = model_info.RigidBody(index_in_structure_array).ID;
         end
         
    else
            % if IDs were supplied as the input argument, then we just
            % assign them without problems.
            rigid_body_ids = int32(rigid_body_id_or_name); % We keep int32, make sure they are round numbers.
    end
    
    % This is for debug.
    %fprintf('Rigid body IDs are:\n')
    %rigid_body_ids
    
    %% Extract data using rigid body IDs.
    
    translation = zeros(no_of_rigid_bodies, 3);
    quaternion = zeros(no_of_rigid_bodies, 4);
    
    latest_frame = natnet_object.getFrame;
    
    % The first two return values
    framecounter = latest_frame.iFrame;
    timestamp = latest_frame.CameraDataReceivedTimestamp;
    
    % Now we go through our Ids, and get the data.
    for(i=1:length(rigid_body_ids))
        % These IDs are as per we requested in the input argument.
        translation(i, :) = [latest_frame.RigidBodies(rigid_body_ids(i)).x, ...
                             latest_frame.RigidBodies(rigid_body_ids(i)).y, ...
                             latest_frame.RigidBodies(rigid_body_ids(i)).z];
                         
        quaternion(i, :) = [latest_frame.RigidBodies(rigid_body_ids(i)).qx, ...
                            latest_frame.RigidBodies(rigid_body_ids(i)).qy, ...
                            latest_frame.RigidBodies(rigid_body_ids(i)).qz, ...
                            latest_frame.RigidBodies(rigid_body_ids(i)).qw];
    end
    
end

