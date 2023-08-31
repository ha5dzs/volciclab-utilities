function volciclab_velmex_move(horizontal_x, vertical_y, rotation_angle)
%VOLCICLAB_VELMEX_MOVE This function moves the linear state,
% RELATIVE TO THE CENTER POSITION!
% IMPORTANT: RUN THIS FIRST: volciclab_velmex_init
% Input arguments are:
%   -horizontal_x, axis 1's movement, in millimetres.
%   -vertical_y, axis 2's movement, in millimetres.
%   -rotation_angle, in degrees.
% The function will hold execution until the instructed movement is
% finished.

    %% Sanity checks.
    
    % Do we have the correct number of arguments?
    if(nargin ~= 3)
        error('This function needs exactly three arguments.')
    end
    
    % Have been given numbers?
    if(~isnumeric(horizontal_x) || ~isnumeric(vertical_y) || ~isnumeric(rotation_angle) )
        error('Input arguments must be numeric.')
    end
    
    % Are these numbers scalars?
    if(~isscalar(horizontal_x) || ~isscalar(vertical_y) || ~isscalar(rotation_angle) )
        error('Every input argument must be a single number.')
    end
    
    %% Build up the connection
    
    volciclab_velmex_config; % Import this stuff to this namespace too

    velmex.comport_object = [];
    velmex.comport_object = serialport(velmex.comport, 9600, "Timeout", 40);
    
    %% Calculate the required steps
    
    steps_x = round(horizontal_x * velmex.millimetres_per_revolution * velmex.steps_per_revolution);
    
    % Positive is upwards
    steps_y = -1 * round(vertical_y * velmex.millimetres_per_revolution * velmex.steps_per_revolution);
    
    % I don't know yet why this *2 needs to be there 
    steps_rotator = -1 * round(rotation_angle / 360 * velmex.shaft_revolution_per_rotation * velmex.steps_per_revolution) *2;
    
    %% Move the Velmex contraption
    
    try
        magic_word = ...
            sprintf("C, IA3M%d, IA1M%d, IA2M%d, R", steps_rotator, steps_x, steps_y);
        writeline(velmex.comport_object, magic_word); % Go to the middle
        read(velmex.comport_object, 1, "char");
    catch
        fprintf('Moving instruction:\t%s\n', magic_word);
        error('The controller didn''t reply after this instruction.\n')
    end
   
    %% Release serial port

    velmex.comport_object = [];

end