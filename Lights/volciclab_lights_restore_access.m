% This is a script that waits and throws a warning when the dmx
% communication failed.

light_fail = 1;
while(light_fail)
    % We stay in this loop until Windows decides to give access to th
    % device.
    try
        light_fail = dmx('devicetest');
    catch
        pause(0.1);
        warning('DMX device communication failure, retrying...')
        pause(1+rand*2); % Wait for random time
    end
end