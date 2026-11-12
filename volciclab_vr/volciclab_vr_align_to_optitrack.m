function [alignment_result] = volciclab_vr_align_to_optitrack(udp_port_object, varargin)
%VOLCICLAB_VR_ALIGN_TO_OPTITRACK Trigger a Unity <-> OptiTrack alignment, or query the
%   result of the last one.
%   This is the Matlab-side twin of `UnityOptitrackAlignment.cs` on the headset
%   (lab-specific port 5000, OUTSIDE the 40000-40008 core block). See the sibling
%   docs in `unity_docs/` (in particular `UnityOptitrackAlignment.cs.md`) for the
%   headset side of the wire contract; this helper is the other half.
%
%   The alignment is the "marriage" between the Unity world and the OptiTrack/Motive
%   world: it fetches one pose of the headset's OptiTrack rigid body from the lab
%   streaming server and moves/rotates the camera rig so that the cyclopean eye
%   lands exactly on that pose. It is an occasional lab action (cradled-headset
%   boot, or a participant reset), NOT a per-frame data path.
%
%   There are three call shapes (all use the same udp port object):
%
%     A. volciclab_vr_align_to_optitrack(udp)
%        Plain trigger. Fire-and-forget: the headset does NOT send a reply on a
%        plain trigger — it runs the alignment and updates the internal state, and
%        that is it. The experimenter must VISUALLY VERIFY the alignment before
%        any participant arrives (see the "How it is used?" section of
%        `unity_docs/UnityOptitrackAlignment.cs.md`), so the experimenter is
%        never left wondering whether the alignment actually took place.
%        This is the same pattern as `volciclab_vr_passthrough`/`volciclab_vr_hands`.
%
%     B. volciclab_vr_align_to_optitrack(udp, '?')
%        Query the result of the LAST alignment. The headset replies with a
%        single 6-field packet:
%
%            OK;<STATE>;<fx,fy,fz>;errMm;errDeg;attempts
%
%        The reply is parsed into the following struct:
%
%            alignment_result.alignment_status
%                logical. The derived "did it succeed?" — `~strcmpi(<STATE>, 'FAILED')`.
%                This is the *derived* view; the authoritative value is
%                `alignment_result.alignment_result` (the `<STATE>` string itself).
%
%            alignment_result.alignment_result
%                char. The literal `<STATE>` token on the wire (e.g. `'FAILED'`,
%                `'SUCCESS'`); case preserved from the headset.
%
%            alignment_result.fudge_values
%                1x3 double. The accumulated fudge translation on the headset,
%                in metres (Unity world space, Y-up). Deliberately left in m —
%                the game engine's own units; convert for display if you want mm.
%
%            alignment_result.error_magnitude
%                double (mm). The residual translation error of the LAST alignment
%                run, as reported by the headset. The field is called `errMm` on
%                the wire (i.e. already in millimetres) — no further scaling is
%                applied here.
%
%            alignment_result.error_angle
%                double (degrees). The residual yaw error of the LAST alignment
%                run, as reported by the headset.
%
%            alignment_result.number_of_calibration_loops_done
%                double (integer-valued). The number of alignment attempts that
%                were actually run (the Unity side retries up to `maxAttempts`
%                before giving up). Despite being integer, it is returned as a
%                double.
%
%     C. volciclab_vr_align_to_optitrack(udp, fudge_dx, fudge_dy, fudge_dz)
%        Apply a fudge-delta (in metres, Y-up, added to the running fudge), then
%        trigger an alignment with the updated fudge. Fire-and-forget: the
%        headset does NOT send a reply on a fudge+trigger either — the
%        experimenter verifies via (B) or visually.
%
%        This is exactly the workflow described in `unity_docs/UnityOptitrackAlignment.cs.md`,
%        "Why the fudge is a relative delta, and why it exists at all": the
%        experimenter looks through the optics gap, sees the virtual twin a bit
%        off from the real object, sends ONE relative delta (e.g. `0, 0.01, 0`
%        to nudge everything 1 cm up in Unity space), re-aligns, and the twin
%        snaps by exactly that amount.
%
%   Dependency: volciclab_vr_config.m, which must be in the path and set up.
%   This helper uses `headset.vr_port_optitrack_unity_alignment` (port 5000 by
%   default), `headset.reasonable_response_time` (for the warn-on-slow path of
%   the `?` query) and `headset.comm_timeout` (for the hard-stop on the same).
%
%   Returns:
%       - Shape A (1 arg) and Shape C (4 args): no return value. The caller
%         should follow up with Shape B to inspect the result.
%       - Shape B (2 args, `?`): the struct described above.
%
%   Units:
%       The fudge-delta and the accumulated fudge are in **metres** (the game
%       engine's own units, Y-up). The returned `error_magnitude` is in **mm**
%       — this is the *only* one of the alignment fields that is NOT in
%       metres, matching the `unity_docs/` convention that the Unity side
%       reports residuals in mm and degrees (the field is literally named
%       `errMm`). `error_angle` is in **degrees**.

    % Correct number of arguments? (0 or 2 optional args for the query, or 3 optional args for the fudge+trigger)
    if(nargout ~= 0 && nargout ~= 1)
        error('This function must have zero or one output argument.')
    end

    % Do we get a udpport?
    if~(isvalid(udp_port_object))
        error('The first argument for this function must be a valid udpport object.')
    end

    % Now load the headset structure, and see if we get the correct fields.
    volciclab_vr_config;

    % Do we have an IP address?
    if~(isstring(headset.ip_address) || ischar(headset.ip_address))
        error('volciclab_vr_config.m: the IP address must be a string.')
    end

    % Do we have a port?
    if~(isfield(headset, 'vr_port_optitrack_unity_alignment'))
        error('volciclab_vr_config.m: missing field "vr_port_optitrack_unity_alignment".')
    end
    alignment_port = headset.vr_port_optitrack_unity_alignment;

    % Configure the receive timeout (only used on the `?` query path).
    udp_port_object.Timeout = headset.comm_timeout;

    % ------------------------------------------------------------------
    % DISPATCH ON THE CALL SHAPE
    % ------------------------------------------------------------------

    if(nargin == 1)
        % Shape A: plain trigger. Fire-and-forget. The Unity side does the
        % alignment on its own; it does not send a reply.
        % We send a non-empty token that is NOT the query token `?` and NOT a
        % three-float fudge-delta — the Unity side treats "anything that
        % isn't ? or three floats" as a plain trigger.
        write(udp_port_object, "align", "string", headset.ip_address, alignment_port);
        % Clear the output buffer (housekeeping, per the AGENTS.md R2026 quirk).
        flush(udp_port_object, "output");

    elseif(nargin == 2)
        % Shape B: query the result of the last alignment.
        if(~(strcmpi(varargin{1}, '?')))
            error('The second argument (when present) must be the query token "?".')
        end

        % Clear the input buffer before querying, to avoid stale data.
        flush(udp_port_object, 'input');
        write(udp_port_object, "?", "string", headset.ip_address, alignment_port);
        % Clear the output buffer.
        flush(udp_port_object, "output");
        % Give the headset a head start.
        pause(headset.reasonable_response_time / 2);
        % Still waiting: warn once when the reasonable time is exceeded, error on timeout.
        tic;
        warning_thrown = false;
        while~(udp_port_object.NumBytesAvailable)
            if( (toc > headset.reasonable_response_time) && (warning_thrown == false) )
                % If we got here, the headset didn't respond.
                warning('The headset failed to respond within a reasonable time.');
                warning_thrown = true;
            end

            if(toc > headset.comm_timeout)
                error('No response from the headset when queried. This is a problem, cannot continue.')
            end
        end

        % Parse the receive buffer. We receive the payload as an array of
        % characters, with no string terminator.
        received_payload = read(udp_port_object, udp_port_object.NumBytesAvailable, "char")

        % The wire grammar (per `unity_docs/UnityOptitrackAlignment.cs.md` line 30
        % and 101) is 6 `;`-separated fields:
        %   OK;<STATE>;<fx,fy,fz>;errMm;errDeg;attempts
        % The `<fx,fy,fz>` field is itself comma-separated, so a simple
        % `strsplit(received_payload, ';')` is NOT sufficient — we have to
        % split on `;` first (to get the 6 top-level fields) and then split
        % field 3 on `,` to get the vector.
        payload_fields = strsplit(received_payload, ';');

        % Step 1: does it have 6 top-level fields?
        if numel(payload_fields) ~= 6
            sprintf('\tvolciclab_vr_align_to_optitrack(): Headset sent: %s\n', received_payload)
            error('Alignment query payload has %d fields, not 6 (got "%s")', numel(payload_fields), received_payload);
        end

        % Step 2: field 1 must be 'OK' (this is the "the reply arrived and was well-formed"
        %         token, NOT the alignment status).
        if(~strncmpi(payload_fields{1}, 'OK', 2))
            fprintf('\tvolciclab_vr_align_to_optitrack(): headset responded with: %s\n', received_payload);
            error('The first field of the alignment query reply is not "OK" (got "%s").', payload_fields{1});
        end

        % Step 3: derive alignment_status, and keep the literal STATE string in the struct.
        alignment_result  = struct;
        alignment_result.alignment_status  = ~(strcmpi(payload_fields{2}, 'FAILED'));
        alignment_result.alignment_result  = payload_fields{2};

        % Step 4: the fudge vector — field 3 is '<fx,fy,fz>'.
        fudge_components = strsplit(payload_fields{3}, ',');
        if numel(fudge_components) ~= 3
            fprintf('\tvolciclab_vr_align_to_optitrack(): headset sent: %s\n', received_payload);
            error('The fudge vector has %d components, not 3 (got "%s").', numel(fudge_components), payload_fields{3});
        end
        fudge_values = zeros(1, 3);
        for k = 1:3
            v = str2double(fudge_components{k});
            if isnan(v)
                fprintf('\tvolciclab_vr_align_to_optitrack(): headset sent: %s\n', received_payload);
                error('Fudge component %d is not a number (got "%s").', k, fudge_components{k});
            end
            fudge_values(k) = v;
        end
        alignment_result.fudge_values = fudge_values;

        % Step 5: error_magnitude — errMm (already in millimetres on the wire).
        v = str2double(payload_fields{4});
        if isnan(v)
            fprintf('\tvolciclab_vr_align_to_optitrack(): headset sent: %s\n', received_payload);
            error('error_magnitude (field 4) is not a number (got "%s").', payload_fields{4});
        end
        alignment_result.error_magnitude = v;

        % Step 6: error_angle (degrees, on the wire).
        v = str2double(payload_fields{5});
        if isnan(v)
            fprintf('\tvolciclab_vr_align_to_optitrack(): headset sent: %s\n', received_payload);
            error('error_angle (field 5) is not a number (got "%s").', payload_fields{5});
        end
        alignment_result.error_angle = v;

        % Step 7: number of alignment attempts (integer-valued, returned as a double).
        v = str2double(payload_fields{6});
        if isnan(v)
            fprintf('\tvolciclab_vr_align_to_optitrack(): headset sent: %s\n', received_payload);
            error('number_of_calibration_loops_done (field 6) is not a number (got "%s").', payload_fields{6});
        end
        alignment_result.number_of_calibration_loops_done = v;

        % Flush buffers.
        flush(udp_port_object, "output");
        flush(udp_port_object, "input");

    elseif(nargin == 4)
        % Shape C: fudge-delta + re-trigger. Fire-and-forget — the Unity side
        % does NOT send a reply on a fudge+trigger either, so we flush and
        % return. The experimenter verifies via Shape B or visually.

        % Local sanity check: all three values must be finite numbers (metres).
        % We deliberately don't range-check against an absolute box: a
        % legitimate fudge value might be large if the marker-holder slipped
        % considerably relative to the headset.
        fudge_dx = varargin{1};
        fudge_dy = varargin{2};
        fudge_dz = varargin{3};
        if(~isfinite(fudge_dx) || ~isfinite(fudge_dy) || ~isfinite(fudge_dz))
            error('All fudge-delta components must be finite numbers (metres).')
        end

        % Assemble the payload: '<fx>,<fy>,<fz>' — fixed field order, matching
        % the Unity-side convention (see `unity_docs/UnityOptitrackAlignment.cs.md`),
        % formatted at four decimals.
        payload_to_send = sprintf('%0.4f,%0.4f,%0.4f', fudge_dx, fudge_dy, fudge_dz);

        % Send it.
        write(udp_port_object, payload_to_send, "string", headset.ip_address, alignment_port);
        % Clear the output buffer.
        flush(udp_port_object, "output");

    else
        error('This function must have 1, 2, or 4 input arguments (see the function docstring for the call shapes).')
    end

end
