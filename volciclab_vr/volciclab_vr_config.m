% Big config script for the VR headset.

% There is a Unity game engine-based implementation running in Unity.
% The Unity project is not here, but some of its documentation is included.
% We are communicating with the headset over UDP.

headset = struct;


%% HEADSET IP ADDRESS: CHANGE THIS TO YOUR HEADSET!!!!!

headset.ip_address = "192.168.42.125";

%% Comms

% Reasonable response time of the headset (this is for blocking queries), you'll get a warning thrown after this
headset.reasonable_response_time = 0.2;

% If the headset doesn't respond by this time, you'll get an error thrown after this.
headset.comm_timeout = 1;


%% Eye tracker calibration default settings - YOU PROBABLY SHOULD CUSTOMISE THIS!

% The numbers are in degrees of visual field

headset.calibration_scotoma = struct;
headset.calibration_scotoma.is_rendered = 1; % This is a boolean
headset.calibration_scotoma.azimuth = 0; % With respect to the retina
headset.calibration_scotoma.elevation = 0; % With respect to the retina
headset.calibration_scotoma.inner_diameter = 0; % Solid disk
headset.calibration_scotoma.outer_diameter = 5;
headset.calibration_scotoma.colour = '#AAAAAAFF'; % RGBA, HTML code

headset.calibration_spot = struct;
headset.calibration_spot.is_rendered = 1; % This is a boolean
headset.calibration_spot.azimuth = 0; % With respect to the head, in the middle
headset.calibration_spot.elevation = -15; % Slightly downwards
headset.calibration_spot.inner_diameter = 6; % annulus
headset.calibration_spot.outer_diameter = 12; 
headset.calibration_spot.colour = '#006400FF'; % RGBA


%% Control port definitions


% Port definitions for the core functions. 
headset.vr_port_eye_tracker_settings = 40000;
headset.vr_port_eye_tracker_gaze_offset = 40001;
headset.vr_port_passthrough_control = 40002;
headset.vr_port_hand_rendering_control = 40003;
headset.vr_port_scotoma_distance_adjuster = 40004;
headset.vr_port_ipd_reporter = 40005;
headset.vr_port_stimulus_adjustments_scotoma = 40006;
headset.vr_port_stimulus_adjustments_calibration = 40007;
headset.vr_port_telemetry_decimation_control = 40008;
headset.vr_port_optitrack_unity_alignment = 5000;


%% Hardware stuff

% These are the 'lab-default' eye tracker settings. This is set in Unity.
headset.eye_tracking_confidence_threshold = 0.95; % 0...1. too low: noisy, too high: cutting out
headset.eyes_are_closed_threshold = 0.3; % 0...1 1 is eyes open, 0 is eyes closed

% Eye tracker offsets (Azimuth, elevation), for calibration
headset.eye_tracker_offsets = [0, 0];
