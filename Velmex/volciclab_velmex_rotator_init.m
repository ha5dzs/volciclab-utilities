% Velmex rotator is installed on axis #3.
% The tricky part is to start this without anything else.

volciclab_velmex_config; % Get the config structure


%% Initialise comms.

velmex.comport_object = [];
% Yes, we actually need 40 seconds for timeout!
velmex.comport_object = serialport(velmex.comport, 9600, "Timeout", 40);
%configureTerminator(velmex.comport_object, "CR"); % Optionally.


%% Reset controller, put into online mode
writeline(velmex.comport_object, "rst"); % Reset
pause(0.5);
writeline(velmex.comport_object, "F"); % On-line, no echo of commands
pause(0.1);
writeline(velmex.comport_object, "N"); % Reset position counters

flushinput(velmex.comport_object); % Clear RX buffer

%% Configure the third motor

try
    writeline(velmex.comport_object, "S3M6000, R");
    read(velmex.comport_object, 1, "char");
catch
    fprintf('This failed:\twriteline(velmex.comport_object, "S3M6000, R");\n')
    error('The controller didn''t reply after this instruction.\n')
end

flushinput(velmex.comport_object); % Clear RX buffer

%% Move the motor to position 0.

try
    writeline(velmex.comport_object, "C, IA3M0, R");
    read(velmex.comport_object, 1, "char");
catch
    fprintf('This failed:\t C, IA3M0, R');
    error('The controller didn''t reply after this instruction.\n')
end


%% Prompt user.

answer = questdlg('The zero switch in the rotator has some hysteresis. Pelase check if the marker is at 0 before continuing. Press ''Yes'' if alignment is OK. Press ''No'', if the alignment needs adjusting.');

if(strcmp(answer, 'No') || strcmp(answer, 'Cancel'))
    fprintf('The system is now left initialised, and you can adjust the rotations with thw following code:\n');
    fprintf(2, 'volciclab_velmex_send_command("C, I3MXXX, R", false);,')
    fprintf(' ...where "XXX" is the number if steps. Play with it until it is aligned.\n')
    fprintf('Once done, then send: ');
    fprintf(2, 'volciclab_velmex_send_command("N", false);\n')
    fprintf('Good luck!\n')
    clear velmex; % Free the serial port
    error('Adjust your alignment as described above, and run this script again.');
end

%% Set this to be the origin.
writeline(velmex.comport_object, "N");

velmex.comport_object = []; % This apparently closes the serial port.