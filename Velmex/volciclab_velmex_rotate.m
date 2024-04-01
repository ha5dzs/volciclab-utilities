function volciclab_velmex_rotate(rotation_angle)
%VOLCICLAB_OPTITRACK_MOVE_ROTATOR Yuu guessed it, rotates the rotator to
%the specified angle.
% Calibrate the rotator first.
% Input argument:
%   -rotation_angle, in degrees. Can be any number.

    %% Sanity checks
    if(nargin == 0)
        error('This function needs an input argument.')
    end

    if(length(rotation_angle) > 1)
        error('rotation_angle must be a single value.')
    end

    if(~isnumeric(rotation_angle))
        error('rotation_angle should be a number.')
    end

    %% Connection

    volciclab_velmex_config; % Import this stuff to this namespace too

    velmex.comport_object = [];
    velmex.comport_object = serialport(velmex.comport, 9600, "Timeout", 40);

    %% Calculation of steps.
    
     % I don't know yet why this *2 needs to be there 
    steps_rotator = -1 * round(rotation_angle / 360 * velmex.shaft_revolution_per_rotation * velmex.steps_per_revolution) *2;
    
    %% Send instruction

    try
        magic_word = ...
            sprintf("C, IA3M%d, R", steps_rotator);
        writeline(velmex.comport_object, magic_word); % Go to the middle
        read(velmex.comport_object, 1, "char");
    catch
        fprintf('Moving instruction:\t%s\n', magic_word);
        error('The controller didn''t reply after this instruction.\n')
    end
   
    flushinput(velmex.comport_object); % Clear RX buffer

    %% Release serial port

    velmex.comport_object = [];
end

