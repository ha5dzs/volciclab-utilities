% Light calibrator.
% Uses the udmx dongle and the i1 spectrometer.

clear
clc

% We need to hard-code this

right_wash = struct;
right_spot = struct;
left_spot = struct;
left_wash = struct;

%%  Hardware settings

% Light config data. Full blast on everything.
diffuse = [64, 255, 255, 255, 0, 0]; % Dim, R, G, B, Strobe, Effect
spot = [64, 255, 255, 255, 255, 0]; % Dim(strobe), R, G, B, W, Effect

% Light address register. Check the lights. No address collision check
addresses = [100, 106, 16, 10];

dmx('send', addresses(1):addresses(1)+5, diffuse);
dmx('send', addresses(2):addresses(2)+5, spot);
dmx('send', addresses(3):addresses(3)+5, spot);
dmx('send', addresses(4):addresses(4)+5, diffuse);



%% Blackout.
pause(2);


dmx('send', addresses(1):addresses(1)+5, zeros(1, 6));
dmx('send', addresses(2):addresses(2)+5, zeros(1, 6));
dmx('send', addresses(3):addresses(3)+5, zeros(1, 6));
dmx('send', addresses(4):addresses(4)+5, zeros(1, 6));





%% Right wash

which_wash = 1; % In addresses

wash_conditions = [
    255, 255, 255, 255, 0, 0; % Full blast, all white
    255, 255, 0, 0, 0, 0; % Red
    255, 0, 255, 0, 0, 0; % Green
    255, 0, 0, 255, 0, 0; % Blue
];


wash_lxy = zeros(3, 3);

dmx('send', addresses(which_wash):addresses(which_wash)+5, wash_conditions(1, :));
fprintf('Measuring...');
I1('TriggerMeasurement');
wash_spectrum = I1('GetSpectrum');
fprintf('Done!\n');
wavelengths = [380:10:730];
[wash_peaks, wash_locations] = findpeaks(wash_spectrum, wavelengths, 'NPeaks', 3);
relative_peaks = wash_peaks ./ max(wash_peaks);


% We go R-G-B
for j = 2:4
    % Set the light to a single colour, populate measurement array
    dmx('send', addresses(which_wash):addresses(which_wash)+5, wash_conditions(j, :));
    fprintf('Measuring...');
    I1('TriggerMeasurement');
    wash_lxy(j-1, :) = I1('GetTristimulus');
    fprintf('Done!\n');
    pause(0.5)
end

pause(0.5);
% When done, blackout
dmx('send', addresses(which_wash):addresses(which_wash)+5, zeros(1, 6));



% Organise this data.
right_wash.red.x = wash_lxy(1, 2);
right_wash.red.y = wash_lxy(1, 3);
right_wash.red.luminance = wash_lxy(1, 1);
%right_wash.red.intensity = wash_relative_intensities(1);
right_wash.red.intensity = relative_peaks(3); % From spectrum, backwards

right_wash.green.x = wash_lxy(2, 2);
right_wash.green.y = wash_lxy(2, 3);
right_wash.green.luminance = wash_lxy(2, 1);
%right_wash.green.intensity = wash_relative_intensities(2);
right_wash.green.intensity = relative_peaks(2); % From spectrum, backwards

right_wash.blue.x = wash_lxy(3, 2);
right_wash.blue.y = wash_lxy(3, 3);
right_wash.blue.luminance = wash_lxy(3, 1);
%right_wash.blue.intensity = wash_relative_intensities(3);
right_wash.blue.intensity = relative_peaks(1); % From spectrum, backwards
fprintf('Right wash data saved.\n');


%% Left wash

which_wash = 4; % In addresses

wash_conditions = [
    255, 255, 255, 255, 0, 0; % Full blast, all white
    255, 255, 0, 0, 0, 0; % Red
    255, 0, 255, 0, 0, 0; % Green
    255, 0, 0, 255, 0, 0; % Blue
];


wash_lxy = zeros(3, 3);

dmx('send', addresses(which_wash):addresses(which_wash)+5, wash_conditions(1, :));
fprintf('Measuring...');
I1('TriggerMeasurement');
wash_spectrum = I1('GetSpectrum');
fprintf('Done!\n');
wavelengths = [380:10:730];
[wash_peaks, wash_locations] = findpeaks(wash_spectrum, wavelengths, 'NPeaks', 3)
relative_peaks = wash_peaks ./ max(wash_peaks);


% We go R-G-B
for j = 2:4
    % Set the light to a single colour, populate measurement array
    dmx('send', addresses(which_wash):addresses(which_wash)+5, wash_conditions(j, :));
    fprintf('Measuring...');
    I1('TriggerMeasurement');
    wash_lxy(j-1, :) = I1('GetTristimulus');
    fprintf('Done!\n');
    pause(0.5)
end

pause(0.5);
% When done, blackout
dmx('send', addresses(which_wash):addresses(which_wash)+5, zeros(1, 6));



% Organise this data.
left_wash.red.x = wash_lxy(1, 2);
left_wash.red.y = wash_lxy(1, 3);
left_wash.red.luminance = wash_lxy(1, 1);
%left_wash.red.intensity = wash_relative_intensities(1);
left_wash.red.intensity = relative_peaks(3); % From spectrum, backwards

left_wash.green.x = wash_lxy(2, 2);
left_wash.green.y = wash_lxy(2, 3);
left_wash.green.luminance = wash_lxy(2, 1);
%left_wash.green.intensity = wash_relative_intensities(2);
left_wash.green.intensity = relative_peaks(2); % From spectrum, backwards

left_wash.blue.x = wash_lxy(3, 2);
left_wash.blue.y = wash_lxy(3, 3);
left_wash.blue.luminance = wash_lxy(3, 1);
%left_wash.blue.intensity = wash_relative_intensities(3);
left_wash.blue.intensity = relative_peaks(1); % From spectrum, backwards
fprintf('Left wash data saved.\n');



%% Right spot

which_spot = 2; % In 'addresses'

% We saturate the sensor, so we do 50% of it.
spot_conditions = [
    255, 64, 64, 64, 0, 0; % RGB full blast, but 25% each colour channel
    255, 128, 0, 0, 0, 0; % Red
    255, 0, 128, 0, 0, 0; % Green
    255, 0, 0, 128, 0, 0; % Blue
    255, 0, 0, 0, 128, 0; % White
];

spot_lxy = zeros(4, 3);

% Get spectra of RGB
dmx('send', addresses(which_spot):addresses(which_spot)+5, spot_conditions(1, :));
fprintf('Measuring...');
I1('TriggerMeasurement');
spot_spectrum = I1('GetSpectrum');
fprintf('Done!\n');
wavelengths = [380:10:730];
[spot_peaks, spot_locations] = findpeaks(spot_spectrum, wavelengths, 'NPeaks', 3);
relative_peaks = spot_peaks ./ max(spot_peaks);


% We go R-G-B-W
for k = 2:5
    % Set the light to a single colour, populate measurement array
    dmx('send', addresses(which_spot):addresses(which_spot)+5, spot_conditions(k, :));
    fprintf('Measuring...');
    I1('TriggerMeasurement');
    spot_lxy(k-1, :) = I1('GetTristimulus');
    fprintf('Done!\n');
    pause(0.5)
end
pause(0.5);
% When done, blackout
dmx('send', addresses(which_spot):addresses(which_spot)+5, zeros(1, 6));

% Organise this data.
right_spot.red.x = spot_lxy(1, 2);
right_spot.red.y = spot_lxy(1, 3);
right_spot.red.luminance = spot_lxy(1, 1);
%right_spot.red.intensity = spot_relative_intensities(1);
right_spot.red.intensity = relative_peaks(3); % We go backwards

right_spot.green.x = spot_lxy(2, 2);
right_spot.green.y = spot_lxy(2, 3);
right_spot.green.luminance = spot_lxy(2, 1);
%right_spot.green.intensity = spot_relative_intensities(2);
right_spot.green.intensity = relative_peaks(2); % We go backwards

right_spot.blue.x = spot_lxy(3, 2);
right_spot.blue.y = spot_lxy(3, 3);
right_spot.blue.luminance = spot_lxy(3, 1);
%right_spot.blue.intensity = spot_relative_intensities(3);
right_spot.blue.intensity = relative_peaks(1); % We go backwards

right_spot.white.x = spot_lxy(4, 2);
right_spot.white.y = spot_lxy(4, 3);
right_spot.white.luminance = spot_lxy(4, 1);
right_spot.white.intensity = NaN;


fprintf('Right spot data saved.\n');


%% Left spot

which_spot = 3; % In 'addresses'

% We saturate the sensor, so we do 50% of it.
spot_conditions = [
    255, 64, 64, 64, 0, 0; % RGB full blast, but 25% each colour channel
    255, 128, 0, 0, 0, 0; % Red
    255, 0, 128, 0, 0, 0; % Green
    255, 0, 0, 128, 0, 0; % Blue
    255, 0, 0, 0, 128, 0; % White
];

spot_lxy = zeros(4, 3);

% Get spectra of RGB
dmx('send', addresses(which_spot):addresses(which_spot)+5, spot_conditions(1, :));
fprintf('Measuring...');
I1('TriggerMeasurement');
spot_spectrum = I1('GetSpectrum');
fprintf('Done!\n');
wavelengths = [380:10:730];
[spot_peaks, spot_locations] = findpeaks(spot_spectrum, wavelengths, 'NPeaks', 3);
relative_peaks = spot_peaks ./ max(spot_peaks);


% We go R-G-B-W
for k = 2:5
    % Set the light to a single colour, populate measurement array
    dmx('send', addresses(which_spot):addresses(which_spot)+5, spot_conditions(k, :));
    fprintf('Measuring...');
    I1('TriggerMeasurement');
    spot_lxy(k-1, :) = I1('GetTristimulus');
    fprintf('Done!\n');
    pause(0.5)
end
pause(0.5);
% When done, blackout
dmx('send', addresses(which_spot):addresses(which_spot)+5, zeros(1, 6));

% Organise this data.
left_spot.red.x = spot_lxy(1, 2);
left_spot.red.y = spot_lxy(1, 3);
left_spot.red.luminance = spot_lxy(1, 1);
%left_spot.red.intensity = spot_relative_intensities(1);
left_spot.red.intensity = relative_peaks(3); % We go backwards

left_spot.green.x = spot_lxy(2, 2);
left_spot.green.y = spot_lxy(2, 3);
left_spot.green.luminance = spot_lxy(2, 1);
%left_spot.green.intensity = spot_relative_intensities(2);
left_spot.green.intensity = relative_peaks(2); % We go backwards

left_spot.blue.x = spot_lxy(3, 2);
left_spot.blue.y = spot_lxy(3, 3);
left_spot.blue.luminance = spot_lxy(3, 1);
%left_spot.blue.intensity = spot_relative_intensities(3);
left_spot.blue.intensity = relative_peaks(1); % We go backwards

left_spot.white.x = spot_lxy(4, 2);
left_spot.white.y = spot_lxy(4, 3);
left_spot.white.luminance = spot_lxy(4, 1);
left_spot.white.intensity = NaN;


fprintf('Right spot data saved.\n');




%% Enable white lights, and set the wash lights to the same chromaticity.

% This is the measured chromaticity of the white LEDs in the spot light
white_x = 0.31;
white_y = 0.32;

d65_x = 0.31271;
d65_y = 0.32902;

tungsten_x = 0.445;
tungsten_y = 0.40745;

display_x = white_x;
display_y = white_y;

right_wash_dimmer = 50;
left_wash_dimmer = 50;

% Set white LED white
dmx('send', addresses(2):addresses(2)+5, [255, 0, 0, 0, 128, 0]);
dmx('send', addresses(3):addresses(3)+5, [255, 0, 0, 0, 128, 0]);

% Set RGB white

%[r_spot_red, r_spot_green, r_spot_blue] = volciclab_lights_get_rgb_values(right_spot, display_x, display_y);
%[l_spot_red, l_spot_green, l_spot_blue] = volciclab_lights_get_rgb_values(left_spot, display_x, display_y);

%dmx('send', addresses(2):addresses(2)+5, [255, r_spot_red, r_spot_green, r_spot_blue, 0, 0]);
%dmx('send', addresses(3):addresses(3)+5, [255, l_spot_red, l_spot_green, l_spot_blue, 0, 0]);



[r_wash_red, r_wash_green, r_wash_blue] = volciclab_lights_get_rgb_values(right_wash, display_x, display_y);
[l_wash_red, l_wash_green, l_wash_blue] = volciclab_lights_get_rgb_values(left_wash, display_x, display_y);

dmx('send', addresses(1):addresses(1)+5, [right_wash_dimmer, r_wash_red, r_wash_green, r_wash_blue, 0, 0]);
dmx('send', addresses(4):addresses(4)+5, [left_wash_dimmer, l_wash_red, l_wash_green, l_wash_blue, 0, 0]);


%% Save the light structures


save('light_structures/lights.mat', 'right_wash', 'right_spot', 'left_wash', 'left_spot')