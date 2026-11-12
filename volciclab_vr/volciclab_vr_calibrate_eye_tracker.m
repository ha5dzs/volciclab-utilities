function  volciclab_vr_calibrate_eye_tracker(udp_port_object)
%VOLCICLAB_VR_CALIBRATE_EYE_TRACKER Per-trial eye-tracker calibration.
%   Because the participant *wears* the eye tracker (soft pads, loosely
%   attached to the head, the headset moves relative to the head at any
%   time), the eye tracker must be calibrated at the start of *every* trial
%   (see `unity_docs/GazeContingentTransform2.cs.md` line 38: "Calibration is
%   basically impossible so it must be done on the fly"; and line 73: "In
%   practice, this is a process of placing a small sphere to a known
%   location, asking the participant to look at that sphere, and when the
%   participant confirms, then we record the raw (uncalibrated) gaze vector.
%   The `GazeVectorOffset` is the inverse of the difference between the
%   sphere's and the gaze vector's azimuth and elevation coordinates").
%
%   This is the Matlab-side implementation of that loop. It is *deliberately
%   a single function call* (no optional arguments, no grid loop, no retry
%   counter): the only thing the function owns is the UDP interface.
%   Everything else — the shape and location of the calibration spot and the
%   calibration scotoma, the number of times to loop — is owned by the
%   experimenter (via the `headset.calibration_*` fields in
%   `volciclab_vr_config.m`, and via the `questdlg` gate below).
%
%   The wire contract (the two `?` replies that drive the math) is per
%   `unity_docs/ExternalEyeTrackerAzimuthElevationAdjuster.cs.md`:
%
%     - the gaze *reply* (`?` on port 40001) is the **calibrated** gaze
%       (raw + offset, i.e. the value *after* the headset has applied any
%       existing offset). The "before the offset" value we want is obtained
%       by *resetting* the offset to (0, 0) in step (3), and then querying —
%       at that moment, calibrated = raw.
%
%     - the *set* (`<az>,<el>` on port 40001) is a relative delta that is
%       added to the running offset on the headset (fire-and-forget, no
%       reply). The *calibration* we send here is an **absolute** correction
%       (it replaces the previous offset's meaning, because we reset to
%       (0, 0) in step (3) immediately before this step in the loop).
%
%   The formula (per the tested-and-working Python reference
%   `unity_docs/eye_tracker_calibration_demo.py`):
%
%       offset_az = spot.azimuth       - raw_gaze_az
%       offset_el = spot.elevation     - raw_gaze_el
%
%   where `spot.azimuth`/`spot.elevation` is the *head-frame* position of the
%   calibration stimulus (from `headset.calibration_spot` in
%   `volciclab_vr_config.m`), and `raw_gaze_az`/`raw_gaze_el` is the
%   *gaze-frame* reading we get after the reset in step (3). The Python
%   demo (working on device, per the docstring at the top of that file)
%   validates this formula end-to-end.
%
%   The *single read* (no "two-reads, use the second" delay) is the
%   recommended pattern — the `?` reply already returns the 10-sample
%   moving average on the Unity side (per
%   `ExternalEyeTrackerAzimuthElevationAdjuster.cs.md`: "a running average
%   of its last `windowFrames` samples (default 10, ~111 ms at 90 Hz)"),
%   so a second read would only add wall-clock delay for no accuracy gain.
%
%   Loop shape (the function owns all of this; the experimenter's two
%   *manual* touches are the `fprintf`+`pause` prompt in step (5) and the
%   `questdlg` gate in step (8)):
%
%     (1)  saved_s  = volciclab_vr_scotoma(udp)
%                     -- capture the *live* experiment scotoma (whatever
%                        `is_rendered` it had) so we can restore it later.
%     (2)  cs_off   = headset.calibration_scotoma; cs_off.is_rendered = 0
%          -------->  volciclab_vr_scotoma(udp, cs_off)
%                     -- *hide* any live visual disturbance (so the
%                        participant's retinal field is clean for the
%                        calibration spot + offset measurement).
%     (3)  -------->  volciclab_vr_eye_tracker_offset(udp, 0, 0)
%                     -- *reset* the offset to (0, 0), so the next gaze
%                        query returns the *raw* (un-corrected) value.
%     (4)  cs_on    = headset.calibration_spot;  cs_on.is_rendered  = 1
%          -------->  volciclab_vr_calibration_spot(udp, cs_on)
%                     -- *show* the calibration spot (the stimulus to
%                        fixate on).
%     LOOP:
%     (5)  fprintf(...); pause()
%                     -- prompt the experimenter: "ask the participant to
%                        look at the calibration spot and hold fixation",
%                        then "Press ENTER in the command window to
%                        continue." (the same pattern as
%                        `generate_participant_id.m`).
%     (6)  [raw_az, raw_el] = volciclab_vr_eye_tracker_offset(udp)
%                     -- *measure* the un-corrected gaze (the 10-sample
%                        moving average on the Unity side).
%     (7)  -------->  volciclab_vr_eye_tracker_offset(udp,
%                          spot.az - raw_az, spot.el - raw_el)
%                     -- *apply* the inverse offset on the headset.
%          cs_chk    = headset.calibration_scotoma
%          cs_chk.is_rendered = 1
%          -------->  volciclab_vr_scotoma(udp, cs_chk)
%                     -- *re-show* the calibration scotoma so the
%                        experimenter has a visual reference while
%                        judging the calibration (the participant can see
%                        the scotoma through the optics gap, per the
%                        "crude and it works very well" check that the
%                        UnityOptitrackAlignment.md "Why the fudge"
%                        section documents).
%     (8)  reply = questdlg('Is the eye tracker properly calibrated for
%                                 the next trial?',
%                           'Eye tracker - Good to go?',
%                           'Yes', 'No', 'Yes')
%                     -- the gate. *No* => back to (2) (a fresh loop
%                        iteration); *Yes* => the exit path below.
%     EXIT:
%     (9)  cs_off2   = headset.calibration_spot;  cs_off2.is_rendered = 0
%          -------->   volciclab_vr_calibration_spot(udp, cs_off2)
%                     -- *hide* the calibration spot (the "show on / hide
%                        off" symmetry with step (4); the calibration
%                        stimulus is no longer needed).
%          -------->   volciclab_vr_scotoma(udp, saved_s)
%                     -- *restore* the experiment scotoma exactly as we
%                        found it in step (1) (whatever `is_rendered` it
%                        had; the function makes no assumption about the
%                        experiment's state).
%         end
%
%   Dependency: volciclab_vr_config.m (for `headset.ip_address`,
%   `headset.calibration_scotoma`, `headset.calibration_spot`, the
%   port fields, and the two comm timeouts); the four helper functions
%   `volciclab_vr_scotoma`, `volciclab_vr_calibration_spot`,
%   `volciclab_vr_eye_tracker_offset` (all already in this directory).
%
%   Input arguments:
%     - a pre-initialised udpport object (the *unity* interface —
%       `network_interface_unity` per `create_network_interfaces.m` —
%       because every port we talk to is a *headset* port, in the
%       `40000`–`40008` core block; we do not touch the *datasink*
%       interface at all in this function).
%
%   Returns:
%     - nothing. The function's only side effect (on the headset) is: the
%       eye-tracker offset is *set* to the calibrated value (readable by
%       the experiment code via `volciclab_vr_eye_tracker_offset(udp)` if
%       it wants the number), and the *visual* state of the headset is
%       *restored* to exactly what it was before the call (the
%       experiment scotoma is back in its step-(1) state, the
%       calibration spot is off, the calibration scotoma is off). The
%       experiment code that called this function can then *immediately*
%       run the next trial, confident that the eye tracker is calibrated
%       and the retinal field is the experiment's, not a calibration
%       remnant.

    % Sanity checks.
    if(nargin ~= 1)
        error('This function must have exactly one input argument: a valid udpport object.')
    end
    if~(isvalid(udp_port_object))
        error('The argument must be a valid udpport object.')
    end

    % Load the shared config (the headset struct).
    volciclab_vr_config;

    if~(isstring(headset.ip_address) || ischar(headset.ip_address))
        error('volciclab_vr_config.m: the IP address must be a string.')
    end

    % The calibration spot's head-frame position is the *target* of the
    % calibration formula. These two fields must be present and numeric
    % (a finite value, in degrees of visual field, head-frame — per
    % `volciclab_vr_config.m`'s own "With respect to the head" comment).
    if~(isfield(headset.calibration_spot, 'azimuth') || ~isfield(headset.calibration_spot, 'elevation'))
        error('volciclab_vr_config.m: missing "azimuth" and/or "elevation" fields on "calibration_spot".')
    end
    target_az = headset.calibration_spot.azimuth;
    target_el = headset.calibration_spot.elevation;
    if(~(isfinite(target_az) && isfinite(target_el)))
        error('The calibration spot target (azimuth, elevation) must be finite numbers (degrees).')
    end

    % Pre-compute the four "toggled" scotoma/spot structures (the same shape
    % the helper expects — the canonical 6-field struct of
    % `volciclab_vr_scotoma` / `volciclab_vr_calibration_spot`, with only
    % `is_rendered` toggled). We copy the *config* struct (not a freshly
    % allocated one) so any customisation the experimenter made
    % (diameter, colour, etc.) is preserved — we only flip the on/off bit.
    calib_scotoma_off = headset.calibration_scotoma; calib_scotoma_off.is_rendered = 0;
    calib_scotoma_on  = headset.calibration_scotoma; calib_scotoma_on .is_rendered = 1;
    calib_spot_off    = headset.calibration_spot;    calib_spot_off    .is_rendered = 0;
    calib_spot_on     = headset.calibration_spot;    calib_spot_on     .is_rendered = 1;

    % ------------------------------------------------------------------
    % (1) Capture the live experiment scotoma.
    % ------------------------------------------------------------------
    saved_s = volciclab_vr_scotoma(udp_port_object);

    % ------------------------------------------------------------------
    % The calibration loop. The "No" path goes back to the top of the
    % loop (a full re-run: hide the scotoma, reset the offset, re-show
    % the spot, re-prompt, re-measure, re-apply). The "Yes" path
    % executes the exit block below and the function returns.
    % ------------------------------------------------------------------
    done = false;
    while(~done)

        % (2) Hide the visual disturbance.
        volciclab_vr_scotoma(udp_port_object, calib_scotoma_off);

        % (3) Reset the offset to (0, 0) so the next query returns *raw*.
        volciclab_vr_eye_tracker_offset(udp_port_object, 0, 0);

        % (4) Show the calibration spot.
        volciclab_vr_calibration_spot(udp_port_object, calib_spot_on);

        % (5) Prompt the experimenter (the "manual touch" #1).
        fprintf('\n')
        fprintf('\tvolciclab_vr_calibrate_eye_tracker(): the calibration spot is now rendered.')
        fprintf('\n')
        fprintf('\tAsk the participant to look at the spot and hold fixation.')
        fprintf('\n')
        fprintf('\tWhen they are steady, press ENTER in the command window to continue.')
        fprintf('\n')
        pause();

        % (6) Measure the un-corrected gaze (the 10-sample moving average
        %     on the Unity side — see the helper's docstring and the
        %     `ExternalEyeTrackerAzimuthElevationAdjuster.cs.md` doc).
        [raw_az, raw_el] = volciclab_vr_eye_tracker_offset(udp_port_object);

        % (7a) Apply the inverse offset (the tested-and-working formula,
        %      per `eye_tracker_calibration_demo.py`).
        offset_az = target_az - raw_az;
        offset_el = target_el - raw_el;
        volciclab_vr_eye_tracker_offset(udp_port_object, offset_az, offset_el);

        % (7b) Re-show the *calibration scotoma* (for the visual check).
        volciclab_vr_scotoma(udp_port_object, calib_scotoma_on);

        % (8) The gate (the "manual touch" #2). questdlg(question, title,
        %     button1, button2, default) — the order is per your spec.
        reply = questdlg( ...
            'Is the eye tracker properly calibrated for the next trial?', ...
            'Eye tracker - Good to go?', ...
            'Yes', 'No', ...
            'Yes');

        if(~strcmpi(reply, 'yes'))
            % "No" — back to the top of the loop (a fresh iteration: the
            % loop body re-runs (2), (3), (4), (5), (6), (7), (8) from
            % scratch; the (2) line below *turns the scotoma off again*
            % because the (7b) line above turned it on — that's the
            % "back to (2)" semantics you specified).
            continue;
        else
            done = true;
        end
    end

    % ------------------------------------------------------------------
    % (9) Exit path: hide the calibration spot, restore the experiment
    %     scotoma. "Leave it as you found it" (per your spec): the
    %     experiment scotoma's `is_rendered` is exactly what it was in
    %     step (1) — we do not force it on, we do not force it off, we
    %     restore the *full* struct verbatim.
    % ------------------------------------------------------------------
    volciclab_vr_calibration_spot(udp_port_object, calib_spot_off);
    volciclab_vr_scotoma(udp_port_object, saved_s);

    % (Optional) A one-line acknowledgement in the command window, in the
    % style of `generate_participant_id.m`. The experiment code can
    % proceed to the next trial now.
    fprintf('\tvolciclab_vr_calibrate_eye_tracker(): eye tracker calibrated. Calibration spot hidden; experiment scotoma restored.')
    fprintf('\n')

end
