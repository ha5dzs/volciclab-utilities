function [framecounter, timestamp, translation, quaternion] = volciclab_optitrack_get_rigid_body_data(natnet_object, rigid_body_id_or_name)
    %VOLCICLAB_OPTITRACK_GET_RIGID_BODY_DATA This function fetches the rigid
    %body transforsm from a properly working and initialised OptiTrack system.
    %   Input arguments are:
    %       -natnet_object, which is created with volciclab_optitrack_init()
    %       -rigid_body_id_or_name, which can be a numeric, string, or cell
    %         array. So:
    %               -5 (single ID)
    %               -"Control rod" or 'Control rod' (string or char)
    %               -[36, 18, 22] (multiple IDs)
    %               -{'Robot', 'Participant'} or {"Robot, "Participant"}
    %               All of these will work.
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
        input_is_cell = false;

        if(isstring(rigid_body_id_or_name))
            input_is_string = true;
        end

        if(ischar(rigid_body_id_or_name))
            % If we got here, we need to convert the character array to a
            % string.
            rigid_body_id_or_name = convertCharsToStrings(rigid_body_id_or_name);
            input_is_string = true;
        end

        if(isnumeric(rigid_body_id_or_name))
            input_is_number = true;
        end

        if(iscell(rigid_body_id_or_name))
            input_is_cell = true; % For a future implementation.
        end

        if(input_is_string && input_is_number)
            error('Input argument is both a number AND character? What sorcery is this? File an issue and tell me all about it!:)')
        end

        if(~input_is_string && ~input_is_number && ~input_is_cell)
            error('The rigid_body_id_or_name must be either a string or numeric.')
        end

        %% Get metadata, so we can assign the framecounter and dig out the rigid bodies and their IDs

        metadata = natnet_object.getFrameMetaData;


        no_of_rigid_bodies = metadata.RigidBodyCount; % For the santiy check


        %% Assign array indices to the rigid body IDs or names, as required.

        rigid_body_indices = int32.empty(no_of_rigid_bodies, 0);
        model_info = natnet_object.getModelDescription; % Get all the rigid body info.

        % Single string, one rigid body.
        if(input_is_string)

            % Find the index of the rigid body name.
            rigid_body_indices = find(strcmp({model_info.RigidBody.Name}, rigid_body_id_or_name)==1);


        end

        % Numeric: Can be single value or array.
        if(input_is_number)
            % We go throug the IDs, and find the indices one by one.
            for(i=1:length(rigid_body_id_or_name))


                 rigid_body_indices(i) = find(cat(1, model_info.RigidBody.ID) == rigid_body_id_or_name(i));

            end

        end

        % Cell: Can have single or multiple rigid bodies, all by text.
        if(input_is_cell)
            % We go throug the specified names, and find the indices one by one.
            for(i=1:length(rigid_body_id_or_name))

                current_rigid_body_name = convertCharsToStrings(rigid_body_id_or_name{i});

                % Find the index of the rigid body name.
                rigid_body_indices(i) = find(strcmp({model_info.RigidBody.Name}, current_rigid_body_name)==1);

            end


        end

        % This is for debug.
        %fprintf('Requested rigid body indices are:\n')
        %rigid_body_indices

        %% Extract data using rigid body IDs.

        translation = zeros(length(rigid_body_id_or_name), 3);
        quaternion = zeros(length(rigid_body_id_or_name), 4);

        latest_frame = natnet_object.getFrame;

        % The first two return values
        framecounter = latest_frame.iFrame;
        % We need to divide by 10 so the output will be in microseconds.
        timestamp = latest_frame.CameraDataReceivedTimestamp / 10;

        % Now we go through our Ids, and get the data.
        for(i=1:length(rigid_body_indices))
            % These IDs are as per we requested in the input argument.
            translation(i, :) = [latest_frame.RigidBodies(rigid_body_indices(i)).x, ...
                                 latest_frame.RigidBodies(rigid_body_indices(i)).y, ...
                                 latest_frame.RigidBodies(rigid_body_indices(i)).z];

            quaternion(i, :) = [latest_frame.RigidBodies(rigid_body_indices(i)).qx, ...
                                latest_frame.RigidBodies(rigid_body_indices(i)).qy, ...
                                latest_frame.RigidBodies(rigid_body_indices(i)).qz, ...
                                latest_frame.RigidBodies(rigid_body_indices(i)).qw];
        end

    end

