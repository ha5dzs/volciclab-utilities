function volciclab_velmex_kill()
%VOLCICLAB_VELMEX_KILL Stops any movement of the Velmex thing
%   You should ideally never need this, but this stops all movement
%   You should never rely on this, and should be able to turn it off manually.
% 

    volciclab_velmex_config; % Load structure

    velmex.comport_object = serialport(velmex.comport, 9600, "Timeout", 1);
    flushinput(velmex.comport_object); % Clear RX buffer, just in case
    writeline(velmex.comport_object, "K");
    velmex.comport_object = []; % This apparently closes the serial port.
end
    


