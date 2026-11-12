function volciclab_expt_beep(varargin)
%VOLCICLAB_EXPT_BEEP Simple beep function without psychtoolbox
% For quick execution, you can just use without arguments.
% This will create a 400 Hz beep for 0.2 seconds, at the volume of 0.8
% If you 
% Input arguments are optional:
%   -frequency, in Hz.
%   -duration, in seconds
%   -volume, which is a number between 0 and 1.

    %% Process varargin
    if(length(varargin) == 0)
        % default stuff
        frequency = 600;
        duration = 0.25;
        volume = 0.5;
    else
    
        if(length(varargin) ~= 3)
            error('If you give this function input arguments, then give exactly 3.')
        end

        if(~isnumeric(varargin{1}) || ~isnumeric(varargin{2}) || ~isnumeric(varargin{3}))
            error('Input arguments must be numeric.')
        end
        
         % Toss the arguments to the function
        frequency = varargin{1};
        duration = varargin{2};
        volume = varargin{3};
   
    end
  
    %% Sanity checks
    
    if(frequency > 4000)
        warning('More than 4 kHz? Okay, but it won''t be pretty, because the sampling rate is set to 8192 Hz.')
    end
    
    if(duration > 5)
        error('More than 5 seconds of beep? If you are sure, just comment this line out.')
    end
    
    if(volume < 0)
        warning('Volume is less than zero. Setting it to zero.')
        volume = 0;
    end
    
    if(volume > 1)
        warning('Volume is more than 1. Setting it to 1.')
        volume = 1;
    end
    
    %% Main course.
    

    sampling_rate = 8192;
    time  = linspace(0, duration, sampling_rate*duration);
    audio_sample = sin(2*pi*frequency * time);
    
    % We make the volume soft.
    fade_time = 0.12;
    fade_samples = round(fade_time * sampling_rate); % this many samples
    volume_vector = ones(1, length(audio_sample)) * volume;
    
    volume_vector(1:fade_samples) = linspace(0, volume, fade_samples); % Fade in
    volume_vector(end-(fade_samples-1):end) = linspace(volume, 0, fade_samples); % Fade out
    
    faded_audio_sample = audio_sample .* volume_vector;
    
    %plot(faded_audio_sample) % see the waveform
    
    % Send it to the sound card.
    sound(audio_sample, sampling_rate);

    % Delay a bit, wait for the audio to stop.
    pause(duration*0.6);


end

