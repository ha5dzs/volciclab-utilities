% Light calibration with the sekonic stuff.

clear all;
clc;

%% Light and colour values

% Light address register. Check the lights. No address collision check
addresses = [80, 90, 16, 10]; % Wash, spot, spot, wash.

% Some colours:

white_led = [0.31, 0.31];
white_cold = [0.32, 0.32];
d65 = [0.31271, 0.32902];
lab_ambient = [0.38, 0.38];


%% Blackout.


dmx('send', addresses(1):addresses(1)+5, zeros(1, 6));
dmx('send', addresses(2):addresses(2)+5, zeros(1, 6));
dmx('send', addresses(3):addresses(3)+5, zeros(1, 6));
dmx('send', addresses(4):addresses(4)+5, zeros(1, 6));


%% Right wash: manual measurement loop

% Light condititons
which_wash = 1; % In addresses

wash_conditions = [
    255, 255, 255, 255, 0, 0; % Full blast, all white
    255, 255, 0, 0, 0, 0; % Red
    255, 0, 255, 0, 0, 0; % Green
    255, 0, 0, 255, 0, 0; % Blue
];


measurement_is_ongoing = true;


% We need to manually terminate this loop to indicate that we are done.
while(measurement_is_ongoing)

    % Cycle across conditions
    for i = 1:4
        dmx('send', addresses(which_wash):addresses(which_wash)+5, wash_conditions(i, :));
        pause;
    end

end

%% Right wash: enter data manually, then run this section.

% Manually enter the data here.
right_wash_manual = struct;

% Spectrum, relative intensities
right_wash_manual.blue.intensity = 1;
right_wash_manual.green.intensity = 0.3;
right_wash_manual.red.intensity = 0.38;

% Organise this data.
right_wash_manual.red.x = 0.695;
right_wash_manual.red.y = 0.303;
right_wash_manual.red.luminance = 82.5; 

right_wash_manual.green.x = 0.135;
right_wash_manual.green.y = 0.697;
right_wash_manual.green.luminance = 223; 
 
right_wash_manual.blue.x = 0.147;
right_wash_manual.blue.y = 0.045;
right_wash_manual.blue.luminance = 74.9;

%% Left wash: manual measurement loop

% Light condititons
which_wash = 4; % In addresses

wash_conditions = [
    255, 255, 255, 255, 0, 0; % Full blast, all white
    255, 255, 0, 0, 0, 0; % Red
    255, 0, 255, 0, 0, 0; % Green
    255, 0, 0, 255, 0, 0; % Blue
];


measurement_is_ongoing = true;


% We need to manually terminate this loop to indicate that we are done.
while(measurement_is_ongoing)

    % Cycle across conditions
    for i = 1:4
        dmx('send', addresses(which_wash):addresses(which_wash)+5, wash_conditions(i, :));
        pause;
    end

end

%% Left wash: enter data manually, then run this section.

% Manually enter the data here.
left_wash_manual = struct;

% Spectrum, relative intensities
left_wash_manual.blue.intensity = 1;
left_wash_manual.green.intensity = 0.4;
left_wash_manual.red.intensity = 0.48;

% Organise this data.
left_wash_manual.red.x = 0.695;
left_wash_manual.red.y = 0.303;
left_wash_manual.red.luminance = 129; 

left_wash_manual.green.x = 0.136;
left_wash_manual.green.y = 0.697;
left_wash_manual.green.luminance = 329; 
 
left_wash_manual.blue.x = 0.146;
left_wash_manual.blue.y = 0.047;
left_wash_manual.blue.luminance = 79.6;

%% Save

save('light_structures/wash_lights_manual.mat', 'right_wash_manual', 'left_wash_manual')


%% Test.

colour_we_need = white_led;

% Calculate the colour values

[r_wash_red, r_wash_green, r_wash_blue] = volciclab_lights_get_rgb_values(right_wash_manual, colour_we_need(1), colour_we_need(2));
[l_wash_red, l_wash_green, l_wash_blue] = volciclab_lights_get_rgb_values(left_wash_manual, colour_we_need(1), colour_we_need(2));

%% Set all lights on
clc;
pause(0.3);
brightnesses = [41, 107, 177, 28]

dmx('send', addresses(1):addresses(1)+5, [brightnesses(1), r_wash_red, r_wash_green, r_wash_blue, 0, 0]);
dmx('send', addresses(2):addresses(2)+5, [255, 0, 0, 0, brightnesses(2), 0]);
dmx('send', addresses(3):addresses(3)+5, [255, 0, 0, 0, brightnesses(3), 0]);
dmx('send', addresses(4):addresses(4)+5, [brightnesses(4), l_wash_red, l_wash_green, l_wash_blue, 0, 0]);

%% Save

% Ambient: 100 lux, spot light: 5000 lux

save('light_structures/addresses_and_brightnesses.mat', 'addresses', 'brightnesses');