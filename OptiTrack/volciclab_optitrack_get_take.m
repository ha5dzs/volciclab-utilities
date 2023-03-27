function [rigid_body_structure] = volciclab_optitrack_get_take(tak_file_path, csv_file_path, varargin)
%VOLCICLAB_OPTITRACK_GET_TAKE This function converts .TAK files to .CSV, and extracts
%OptiTrack rigid body data from them.
% [WARNING]: This function needs an external executable to do the
% conversion. You will need to edit this file, and set the correct path to
% your environment.
%
% You can download the source code and the executable here:
%
% You do not need to have a licensed copy of Motive on your computer for
% this function to work.
%
% Input arguments are:
%   [NOTE]: Ideally, every file path should be absolute.
%   -tak_file_location, which is the absolute path to the take file that
%    has been recorded earlier
%   -csv_file_location, which is the absolute path for the CSV file to be
%    created. File creation must be done (i.e. input argument must not be
%    /dev/null), because the function will read and process it.
%   -output_format, which is a number, and specified for each rotation order:
%       0: Quaternion, w-x-y-z (w-i-j-k)
%       1: Euler, X-Y-Z
%       2: Euler, X-Z-Y
%       3: Euler, Y-X-Z
%       4: Euler, Y-Z-X
%       5: Euler, Z-X-Y
%       6: Euler, Z-Y-X
%
% Return value is:
%   -rigid_body_structure is a structure with the following fields:
%       -framecounter, which is a sequence starting from 0.
%       -time, in seconds, which tells you the time difference between
%        subsequent frames
%       -rigid_body(n), which is a structure array, for each rigid body you
%        had in the recording. Its fields are:
%           -rigid_body(n).translation is a set of triplets of X-Y-Z
%            coordinates for each frame. Units are in millimetres.
%           -rigid_body(n).rotation is a set of quad tuple of quaternion (q0, q1, q2, q3)
%            rotations
%           -rigid_body(n).tracking_error is the tracking error of the rigid body in each frame.
%       [IMPORTANT]: The rigid body numbering is exactly the same as you
%       would get with the NatNet SDK.


%For testing only.
%optitrack_get_take('G:/Saját meghajtó/optitrack_sandbox/Matlab wrapper/input_files/rigid_body1.tak', 'G:/Saját meghajtó/optitrack_sandbox/Matlab wrapper/output_files/rigid_body1.csv')
    %% Sanity checks.
    if(nargin > 3)
        error('This function needs no more than three input arguments.')
    end

    if(nargin < 2)
        error('This function needs at least two input arguments.')
    end

    if(nargin == 3)
        if(~isnumeric(varargin{1}))
            error('The rotation output format must be specified as a number. See the function''s help for details.')
        end
    end

    %% Process the optional input argument

    % By default, we use quaternions.
    rotation_format = 0;

    if(nargin == 3)
        % If we have a third input argument, then:
        rotation_format = varargin{1};
    end


    %% FOR HEAVEN'S SAKE, PLEASE EDIT ME!!!!!
    error('Please edit this file, add the absolute path for the executable, and then comment this line out.')
    %converter_executable_location = 'G:/Saját meghajtó/optitrack_sandbox/Matlab wrapper/converter/OptiTrack NMotive converter.exe';
    %converter_executable_location = 'D:\Matlab_Generic_Functions\OptiTrack\converter\OptiTrack NMotive converter.exe'; % In the lab, locally
    %converter_executable_location = 'G:\Saját meghajtó\tmp\optitrack-motive-file-converter\bin\Debug\net5.0\converter.exe';
    %converter_executable_location = 'Z:\converter\converter.exe'; % In the lab, on the network drive



    %% Do the file conversion.
    fprintf('Converting your .tak file to .csv...')
    % The input argumens are tossed as strings. The commas are necessary
    % because we may have spaces and non-latin characters in the path.
    string_to_execute = sprintf('"%s" "%s" "%s" "%d"', converter_executable_location, tak_file_path, csv_file_path, rotation_format);

    % Break the script if the conversion process had failed.
    if(system(string_to_execute))
        error('There was a problem with the external executable. Check the console for details.')
    end

    %% Read the file, check contents.
    converted_data = readtable(sprintf("%s", csv_file_path), 'ReadVariableNames', 0);

    % We know that we will only ever get rigid bodies. We also know that
    % the return CSV file format will be, so we can just determine the
    % number of rigid bodies with the columns numbers themselves.

    [no_of_samples, width] = size(converted_data);

    if(rotation_format == 0)
        % If we have quaternions:
        no_of_rigid_bodies = (width-2) / 8;
    else
        % If we have euler angles, we use one fewer columns per rigid body.
        no_of_rigid_bodies = (width-2) / 7;
    end

    % If something happens, and there is a problem with the .csv file, and
    % we no longer can trust the row numbers, then throw an error.
    if(mod(no_of_rigid_bodies, 1))
        error('The number of columns in the .csv file do not add up to a round number of rigid bodies. Have you loaded the correct file?')
    end

    % Similarly, if we have a
    if(no_of_rigid_bodies == 0)
        error('No rigid bodies were found in the .csv file!')
    end

    fprintf(' found %d rigid bodies and %d samples.\n', no_of_rigid_bodies, no_of_samples)

    %% Assembe return structure
    rigid_body_structure = struct;

    rigid_body_structure.framecounter = table2array(converted_data(:, 1)); % This is just a sequence of numbers.
    rigid_body_structure.time = table2array(converted_data(:, 2)); % Time, in seconds.

    % Based on the rotation format, this changes.
    if(rotation_format == 0)
        % We have quaternions, 4 columns per rotation
        for(i = 1:no_of_rigid_bodies)
            rotation_columns = (i-1)*8 + 3;
            %fprintf('Rotation columns are: %d - %d\n', rotation_columns, rotation_columns + 3);
            rigid_body_structure.rigid_body(i).rotation = table2array(converted_data(:, rotation_columns:(rotation_columns + 3)));
    
            translation_columns = (i-1)*8 + 7;
            %fprintf('Translation columns are: %d - %d\n', translation_columns, translation_columns + 2);
            rigid_body_structure.rigid_body(i).translation = table2array(converted_data(:, translation_columns:(translation_columns + 2)));
    
            tracking_error_columns = (i-1)*8 + 10;
            %fprintf('Tracking error columns are: %d\n', tracking_error_columns);
            rigid_body_structure.rigid_body(i).tracking_error = table2array(converted_data(:, tracking_error_columns));
        end
    else
        % We have Euler angles, we have 3 columns for the rotations. Change
        % all the numbers accordingly.
        for(i = 1:no_of_rigid_bodies)
            rotation_columns = (i-1)*7 + 3;
            %fprintf('Rotation columns are: %d - %d\n', rotation_columns, rotation_columns + 3);
            rigid_body_structure.rigid_body(i).rotation = table2array(converted_data(:, rotation_columns:(rotation_columns + 2)));
    
            translation_columns = (i-1)*7 + 6;
            %fprintf('Translation columns are: %d - %d\n', translation_columns, translation_columns + 2);
            rigid_body_structure.rigid_body(i).translation = table2array(converted_data(:, translation_columns:(translation_columns + 2)));
    
            tracking_error_columns = (i-1)*7 + 9;
            %fprintf('Tracking error columns are: %d\n', tracking_error_columns);
            rigid_body_structure.rigid_body(i).tracking_error = table2array(converted_data(:, tracking_error_columns));
        end
    end
end

