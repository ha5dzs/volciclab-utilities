% Volciclab velmex config script.

velmex = struct; % Conveniently, in a structure

% Serial port. If need be, will make a server for it.
velmex.comport = "COM3";
velmex.comport_object = []; % This closes the serial port, if it was open.

% Motors. Both of them (PK266, PK245) have 1.8° step angle, so 200 steps
% per revolution
velmex.steps_per_revolution = 200;
velmex.millimetres_per_revolution = 1; % I mm per turn on the threaded rod.
velmex.shaft_revolution_per_rotation = 90; % 90:1 slow-down.


% Some strings, just in case when the controllers have been factory reset.
% There is a lot more to this, so RTFM.
velmex.config_strings_fyi_rtfm = {
    "E"; % Echo on, put it to 'On-line' mode
    "F"; % Echo off, put it to 'On-line' mode
    "C"; % Clear program from controller
    "N"; % Reset position counters
    "K"; % Kill all movement
    "V"; % Verify controller status, i.e. are we moving
    "rst"; % Reset controller
    "S1M3000"; % Motor 1 speed set to 3000 steps per second
    "S2M3000"; % Motor 2 speed set to 3000 steps per second
    "S3M3000"; % Motor 3 speed set to 3000 steps per second
    "IA1M200"; % Move motor 1, abolute, clockwise, 200 steps
    "I3M-100"; % Move motor 4, relative, anti-clockwise, 100 steps
    "getM1M"; % Get Motor 1's type (4: PK266, 3A)
    "setM1M4"; % Set Motor 1 to Type 4
    "getM2M"; % Get Motor 2's type (4: PK266, 3A)
    "getM3M"; % Get Motor 3's type (1: PK245, 1.2A)
};

