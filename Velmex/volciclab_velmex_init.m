% This little scipt just makes sure that:
%   -we know where the end points are
%   -We start at the middle

fprintf('volciclab_velmex_init: Starting...');


volciclab_velmex_config; % Load structure

velmex.comport_object = [];
% Yes, we actually need 40 seconds for timeout!
velmex.comport_object = serialport(velmex.comport, 9600, "Timeout", 40);
%configureTerminator(velmex.comport_object, "CR"); % Optionally.

%% Configure the controller
writeline(velmex.comport_object, "rst"); % Reset
pause(0.5);
writeline(velmex.comport_object, "F"); % On-line, no echo of commands
pause(0.1);
writeline(velmex.comport_object, "N"); % Reset position counters

flushinput(velmex.comport_object); % Clear RX buffer
try
    writeline(velmex.comport_object, "S1M6000, R");
    read(velmex.comport_object, 1, "char");
catch
    fprintf('This failed:\twriteline(velmex.comport_object, "S1M6000, R");\n')
    error('The controller didn''t reply after this instruction.\n')
end
flushinput(velmex.comport_object); % Clear RX buffer
try
    writeline(velmex.comport_object, "S2M6000, R");
    read(velmex.comport_object, 1, "char");
catch
    fprintf('This failed:\twriteline(velmex.comport_object, "S2M6000, R");\n')
    error('The controller didn''t reply after this instruction.\n')
end
flushinput(velmex.comport_object); % Clear RX buffer
try
    writeline(velmex.comport_object, "S3M6000, R");
    read(velmex.comport_object, 1, "char");
catch
    fprintf('This failed:\twriteline(velmex.comport_object, "S3M6000, R");\n')
    error('The controller didn''t reply after this instruction.\n')
end

fprintf("controller reset, speeds set...\n");

%% Home axes, both ends.
% Go to position 0.
fprintf('going to postion -0, ')
flushinput(velmex.comport_object); % Clear RX buffer
try
    writeline(velmex.comport_object, "C, I2M-0, I1M-0, R"); % Home axes distant end
    read(velmex.comport_object, 1, "char");
catch
    fprintf('This failed:\twriteline(velmex.comport_object, "C, I2M-0, I1M-0, R");\n')
    error('The controller didn''t reply after this instruction.\n')
end

% Reset the coordinate system, so this will be the 0 position
writeline(velmex.comport_object, "N");

pause(0.1);

fprintf('done. Now going to the opposite end....')

flushinput(velmex.comport_object); % Clear RX buffer
try
    writeline(velmex.comport_object, "C, I1M0, I2M0, R"); % Home axes distant end
    read(velmex.comport_object, 1, "char");
catch
    fprintf('This failed:\twriteline(velmex.comport_object, "C, I1M0, I2M0, R");\n')
    error('The controller didn''t reply after this instruction.\n')
end

fprintf('done.\n')

%% Save the maximum number of steps for each axis

% Axis 1, 2, 3 are X, Y, Z, respectively.
% Check the wiring on the device if unsure.

% Initial values are NaN.
velmex.x_length = NaN;
velmex.y_length = NaN;
velmex.z_length = NaN;

% Vertical axis
while(isnan(velmex.x_length))
    writeline(velmex.comport_object, "X");
    velmex.x_length = str2double(read(velmex.comport_object, 9, "string"));
end

% Horizontal axis
while(isnan(velmex.y_length))
    writeline(velmex.comport_object, "Y");
    velmex.y_length = str2double(read(velmex.comport_object, 9, "string"));
end

% Rotator
while(isnan(velmex.z_length))
    writeline(velmex.comport_object, "Z");
    velmex.z_length = str2double(read(velmex.comport_object, 9, "string"));
end

% These are treated as an amplitude or absolute maximum from the middle.
velmex.x_max = round(velmex.x_length/2);
velmex.y_max = round(velmex.y_length/2);

%% Move axes to the middle, and move the rotator to zero

fprintf('Going to the middle...')
% Move the device to the middle
flushinput(velmex.comport_object); % Clear RX buffer
try
    velmex.centering_instruction = ...
        sprintf("C, I1M-%d, I2M-%d, R", velmex.x_max, velmex.y_max);
    writeline(velmex.comport_object, velmex.centering_instruction); % Go to the middle
    read(velmex.comport_object, 1, "char");
catch
    fprintf('This failed:\t%s\n', velmex.centering_instruction);
    error('The controller didn''t reply after this instruction.\n')
end

% Move the rotator to the zero point

fprintf('setting the rotator to zero...')
% Absolute distance.
flushinput(velmex.comport_object); % Clear RX buffer
try
    writeline(velmex.comport_object, "C, IA3M0, R");
    read(velmex.comport_object, 1, "char");
catch
    fprintf('This failed:\t C, IA3M0, R');
    error('The controller didn''t reply after this instruction.\n')
end

% ...and finally, we reset the coordinate system,
% so we can work relative from here.


writeline(velmex.comport_object, "N");

fprintf('Done!\n\nVelmex thing initialisation finished. Check the ''velmex'' structure for step limits, if needed.\n')
clear ans;
velmex.comport_object = []; % This apparently closes the serial port.