function [object_struct] = volciclab_vr_object_control(udp_port_object, object_control_param)
%VOLCICLAB_VR_OBJECT_CONTROL Query or set the pose of an externally controlled
%   GameObject in the Unity scene (the `ExternalGameObjectControl` script on the headset).
%   See `unity_docs/ExternalGameObjectControl.cs.md` for the headset side of the wire
%   contract; this helper is the Matlab-side twin half.
%
%   This is *deliberately generic*: the script on the headset is a per-instance,
%   per-port object, the port you pick in the Unity Inspector is whatever you want
%   (5001, 5002, 5003, ...), and the same helper drives any of them. The "identity"
%   of the object to query or set is the *port* (a number); the *pose* is the
%   translation + rotation (a 1x3 double pair, both in the Unity scene's space).
%
%   The canonical object-structure shape (the wire contract, in both directions) is:
%
%       object_struct.vr_port_object_control    - the per-instance UDP port (a number).
%       object_struct.translation               - 1x3 double, in METRES (Unity space;
%                                                 Y is up, Z is the depth axis).
%       object_struct.rotation                  - 1x3 double, Euler angles in DEGREES
%                                                 (Unity's eulerAngles convention;
%                                                 the game engine converts these to a
%                                                 quaternion internally, and the script
%                                                 does *no* other conversion).
%
%   There are two call shapes (disambiguated by the type of the 2nd argument):
%
%     A. Query:  object_struct = volciclab_vr_object_control(udp, port)
%        The 2nd argument is the *numerical value of the port* to query.
%        Sends "?" to that port, waits for the reply, parses the 6-number
%        reply into a full object-structure (port + translation + rotation)
%        and returns it. Because the port is an argument (not a field of the
%        *input*), the *output* structure carries it back, so the result is
%        round-trippable: it can be fed straight back into Shape B.
%
%     B. Set:    volciclab_vr_object_control(udp, object_struct)
%        The 2nd argument is a *full object-structure* (port + translation +
%        rotation). Fire-and-forget: the six numbers are sent to the port
%        carried in the structure, and the headset does NOT reply. This is the
%        same pattern as `volciclab_vr_passthrough` / `volciclab_vr_hands` /
%        the set path of `volciclab_vr_scotoma.m`.
%
%   The two shapes are unambiguous because the 2nd argument's *type* disambiguates
%   them: a port is a number; an object-structure is a struct.
%
%   Units (per the doc and the Unity-side convention):
%       - translation: metres, Y-up, Z-depth (Unity world space by default; the
%         `useLocalSpace` flag on the script side, if enabled, flips this to
%         local space — a C#-side configuration, not a Matlab-side concern).
%       - rotation: Euler degrees (the script's eulerAngles convention).
%       - the helper performs **no conversion** — the same rule as the
%         `ExternalGameObjectControl` script itself ("no conversion happens in
%         the script"; if you are placing objects in an external frame, do the
%         math *there*).
%
%   Dependency: volciclab_vr_config.m, which must be in the path and set up.
%   This helper uses `headset.ip_address`, `headset.reasonable_response_time`
%   (for the warn-on-slow path of the query), and `headset.comm_timeout` (for the
%   hard-stop on the same).
%
%   Returns:
%       - Shape A (query, 2nd arg is a number): the full object-structure
%         described above (port + translation + rotation).
%       - Shape B (set, 2nd arg is the structure): no meaningful return value;
%         the experimenter verifies via Shape A if it matters, or visually via
%         MQDH (the same policy as the other set-path helpers).

    %% Canonical object-structure definition (the wire contract).
    %  Any object-structure we *send* must have exactly these three fields in this
    %  order, and any object-structure we *receive* will have exactly these.
    object_struct = struct;
    object_struct.vr_port_object_control = 0;     % per-instance UDP port (placeholder)
    object_struct.translation            = [0 0 0];  % 1x3, metres, Y-up/Z-depth
    object_struct.rotation               = [0 0 0];  % 1x3, Euler degrees

    %% Sanity checks.

    % Exactly two input arguments (one for query, one for set).
    if(nargin ~= 2)
        error('This function must have exactly two input arguments. See the docstring.')
    end

    % Do we get a udpport?
    if~(isvalid(udp_port_object))
        error('The first argument for this function must be a valid udpport object.')
    end

    % Which shape are we in? The TYPE of the 2nd argument disambiguates the two shapes:
    %   'num'   -> query (the port is a number)
    %   'struct' -> set  (the 2nd arg is the object-structure)
    % Anything else is an error.
    if(isnumeric(object_control_param))
        query_shape = true;
    elseif(isstruct(object_control_param))
        query_shape = false;
    else
        error('The second argument must be either a number (the port, for query) or an object-structure (for set).')
    end

    % Now load the headset structure, and see if we get the correct fields.
    volciclab_vr_config;

    % Do we have an IP address?
    if~(isstring(headset.ip_address) || ischar(headset.ip_address))
        error('volciclab_vr_config.m: the IP address must be a string.')
    end

    % Configure the timeout (only the query path actually uses it).
    udp_port_object.Timeout = headset.comm_timeout;

    %% Do the actual work.

    if(query_shape)
        % ===================================================================
        % SHAPE A: QUERY
        % ===================================================================
        % Local sanity checks: the port must be a positive finite integer.
        port = object_control_param;
        if(~(isfinite(port) && port > 0 && port == floor(port)))
            error('The port (for query) must be a positive finite integer.')
        end

        % Clear the input buffer before querying, to avoid stale data.
        flush(udp_port_object, 'input');
        write(udp_port_object, "?", "string", headset.ip_address, port);
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
        received_payload = read(udp_port_object, udp_port_object.NumBytesAvailable, "char");

        % Per `unity_docs/ExternalGameObjectControl.cs.md`:
        %   the reply is <x>,<y>,<z>,<rx>,<ry>,<rz> at four decimals — i.e. exactly
        %   six comma-separated decimal fields, all finite (the script-side
        %   "parsing is strict" rule is the *send* side; it is the same rule for
        %   the *reply* side, because the script formats them with the same
        %   en-US four-decimal convention).
        %   translation is in Unity-metres (Y up, Z depth), rotation is in
        %   Euler degrees.
        payload_fields = strsplit(received_payload, ',');

        % Step 1: does it have 6 top-level fields?
        if numel(payload_fields) ~= 6
            fprintf('\tvolciclab_vr_object_control(): headset sent: %s\n', received_payload);
            error('Object-control query payload has %d fields, not 6 (got "%s").', numel(payload_fields), received_payload);
        end

        % Step 2: each field must parse as a finite number.
        parsed_values = zeros(1, 6);
        for k = 1:6
            v = str2double(payload_fields{k});
            if isnan(v)
                fprintf('\tvolciclab_vr_object_control(): headset sent: %s\n', received_payload);
                error('Field %d is not a number (got "%s").', k, payload_fields{k});
            end
            parsed_values(k) = v;
        end

        % Step 3: build the canonical object-structure. The port is echoed
        % back in the structure (the "round-trippable" guarantee: the query
        % result can be fed straight back into Shape B as the set-argument).
        object_struct.vr_port_object_control = port;
        object_struct.translation = parsed_values(1:3);  % <x>,<y>,<z>
        object_struct.rotation   = parsed_values(4:6);   % <rx>,<ry>,<rz>

        % Flush buffers.
        flush(udp_port_object, "output");
        flush(udp_port_object, "input");

    else
        % ===================================================================
        % SHAPE B: SET (fire-and-forget — no reply expected, per the doc)
        % ===================================================================
        % Structure shape check — mirror the scotoma helper's pattern.
        if( ~isequal( fields(object_struct), fields(object_control_param) ) )
            error('The input structure did not match the canonical shape for this helper. Try fetching a valid structure from the headset (query shape) and use that as a template.')
        end

        % Local sanity checks: all values must be finite and in the right shape.
        new_port = object_control_param.vr_port_object_control;
        if(~(isfinite(new_port) && new_port > 0 && new_port == floor(new_port)))
            error('The port field of the object-structure must be a positive finite integer.')
        end

        new_translation = object_control_param.translation;
        if( (size(new_translation,1) ~= 1) || (size(new_translation,2) ~= 3) || ~(all(isfinite(new_translation))) )
            error('The translation field of the object-structure must be a 1x3 vector of finite numbers (metres).')
        end

        new_rotation = object_control_param.rotation;
        if( (size(new_rotation,1) ~= 1) || (size(new_rotation,2) ~= 3) || ~(all(isfinite(new_rotation))) )
            error('The rotation field of the object-structure must be a 1x3 vector of finite numbers (Euler degrees).')
        end

        % Assemble the payload: <x>,<y>,<z>,<rx>,<ry>,<rz> — fixed field order,
        % matching the Unity-side convention, formatted at four decimals (the
        % same rule as the reply side, and as the other helpers).
        payload_to_send = sprintf('%0.4f,%0.4f,%0.4f,%0.4f,%0.4f,%0.4f', ...
            new_translation(1), new_translation(2), new_translation(3), ...
            new_rotation(1), new_rotation(2), new_rotation(3));

        % Send it to the port carried in the structure.
        write(udp_port_object, payload_to_send, "string", headset.ip_address, new_port);
        % Clear the output buffer (housekeeping, per the AGENTS.md R2026 quirk).
        flush(udp_port_object, "output");

        % The structure is the "canonical" shape: we do not return anything
        % meaningful on the set path; the caller can follow up with a query
        % (Shape A) or verify visually.
        object_struct = object_control_param;  % echo back, for symmetry with the scotoma helper

    end

end
