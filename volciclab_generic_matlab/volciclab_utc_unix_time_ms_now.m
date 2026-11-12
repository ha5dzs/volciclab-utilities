function tms = volciclab_utc_unix_time_ms_now()
%VOLCICLAB_UTC_UNIX_TIME_MS_NOW Current UTC (Zulu) Unix time in milliseconds, as a string.
%
%   tms = volciclab_utc_unix_time_ms_now()
%
%   Returns the current Unix time (milliseconds since
%   1970-01-01 00:00:00 UTC) as a 13-character decimal string, e.g.
%   '1762766400123'.
%
%   Time zone safety:
%     The value is computed as the *elapsed time* (a duration in seconds)
%     between two datetime values, each of which represents an *absolute
%     instant* in time. datetime subtraction is defined on those absolute
%     instants, so the result is independent of the host computer's local
%     time zone and DST rules.
%
%     DO NOT use datenum(datetime('now')) here: 'datetime('now')' carries
%     TimeZone 'local', so datenum() returns a serial number in the local
%     *wall clock*, making the "unix" time off by the local UTC offset
%     (e.g. +14400 s on a UTC+4 host).
%
%   MATLAB version:
%     Uses only datetime/duration, both available on all supported MATLAB
%     versions. Deliberately avoids unixtime()/epochtime(), which are not
%     present in this installation.
%
%   Output:
%     tms - 1x13 char array of decimal digits
%
%   Examples:
%       s = volciclab_utc_unix_time_ms_now();   % char, e.g. '1762766400123'
%       t = str2double(s);                      % double, e.g. 1762766400123

    
    tNow  = posixtime(datetime('now', 'TimeZone', 'UTC'));

    tms = num2str(tNow*1000, '%.0f');
end
