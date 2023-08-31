function return_string = volciclab_velmex_send_command(command_as_string, return_expected, varargin)
%VOLCICLAB_VELMEX_SEND_COMMAND This function just sends to the controller
% whatever you specify.
% CAUTION: The controller WILL EXECUTE ANYTHING YOU SPECIFY!
% It cannot think, you need to do the thinking.
% Input arguments:
%   - command_as_string is the command for the controller.
%     Sould be something like this: "C, IA3M0, R"
%       (note the quote marks: " because it only accepts a string)
%   - return_expected is a boolean, true if you expect the controller to
%     respond something, false if not.
%   - return_length is a number in bytes. If you expect a reply with the
%     length of 10 bytes, but the controller responds less than that, this
%     function will just hang until timeout (40 seconds).

    %% Basis sanity checks
    
    if(nargin < 2)
        error('This function needs at least two arguments.')
    end

    if(~isstring(command_as_string))
        error('This function needs a string input. Have you accidentally used '' instead of "?')
    end

    if(~islogical(return_expected))
        error('return_expected can only be true or false.')
    end

    if(length(varargin) == 0)
        return_length = 1;
    else
        if(length(varargin) > 1)
            error('Please only specify one optional argument.')
        end
        
        if(~isnumeric(varargin{1}))
            error('The optional argument should be an integer')
        end

        if(length(varargin{1}) ~= 1)
            error('The optional argument should be ONE integer.')
        end

        return_length = varargin{1};

    end

    % Initialise serial port

    volciclab_velmex_config; % Import this stuff to this namespace too

    velmex.comport_object = [];
    velmex.comport_object = serialport(velmex.comport, 9600, "Timeout", 40);

    flushinput(velmex.comport_object); % Clear RX buffer


    %% Send command
    
    writeline(velmex.comport_object, command_as_string);

    %% If reply needed, check here

    % TODO: Return stuff could be multiple characters.
    if(return_expected)
        return_string = read(velmex.comport_object, return_length, "char");
    end

    %% Hang up.
    velmex.comport_object = [];
end

