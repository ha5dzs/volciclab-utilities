function [red_value, green_value, blue_value] = volciclab_lights_get_rgb_values(light_structure, requested_chroma_x, requested_chroma_y)
%VOLCICLAB_LIGHTS_GET_RGB_VALUES Using a defined light structure, this
% function creates the necessary RGB values for a specified chromaticity
% coordinates. See
% https://github.com/ha5dzs/InvisibleMacGyver/tree/master/WorkshopMaterial#creating-a-calibrated-d65-or-any-other-chromaticity-coordinate-light-source
% for a detailed explanantion on how this works.
% Input arguments are:
%   -light_strucure, which is a structure of the following fields:
%        ~.red.chroma.x
%        ~.red.chroma.y
%        ~.red.chroma.intensity
%        ~.green.chroma.x
%        ~.green.chroma.y
%        ~.green.chroma.intensity
%        ~.blue.chroma.x
%        ~.blue.chroma.y
%        ~.blue.chroma.intensity
%   ...the x, y coordinates are CIE 1931 chromaticity coordinates
%   ...and the intensity values are relative (0...1) numbers.
% -requested_chroma_x is the CIE 1931 x coordinate you want
% -requested_chroma_y is the CIE 1931 y cordinate you want

    %% Sanity checks

    % Are we being fed a structure?
    if(~isstruct(light_structure))
        error('The input light strucutre is not specified correctly. See the help of this function.')
    end

    % Are the intensity values valid?
    if( (light_structure.red.intensity > 1 || light_structure.green.intensity > 1 || light_structure.green.intensity > 1) || ...
        (light_structure.red.intensity < 0 || light_structure.green.intensity < 0 || light_structure.green.intensity < 0) )
        
        error('The light intensity values must be normalised between 0 and 1')
    
    end

    % Are the chroma coordinates valid?
    if(requested_chroma_x > 0.78 || requested_chroma_x < 0)
        error('The x coordinate must be within the valid range.')
    end

    if(requested_chroma_y > 0.84 || requested_chroma_y < 0)
        error('The y coordinate must be within the valid range.')
    end


    %% Check if requested colour is displayable
    
    % We can check this by verifying that the requested colour is inside the colour gamut, i.e. by verifying that on the X-Y plane, our point is within the
    % triangle. Basically, the interior function. See this: http://mathworld.wolfram.com/TriangleInterior.html
    
    value_a = ( (requested_chroma_x * light_structure.blue.y - requested_chroma_y * light_structure.blue.x) - (light_structure.red.x * light_structure.blue.y - light_structure.red.y * light_structure.blue.x) ) / (light_structure.green.x * light_structure.blue.y - light_structure.green.y * light_structure.blue.x);
    value_b = ( (requested_chroma_x * light_structure.green.y - requested_chroma_y * light_structure.green.x) - (light_structure.red.x * light_structure.green.y - light_structure.red.y * light_structure.green.x) ) / (light_structure.green.x * light_structure.blue.y - light_structure.green.y * light_structure.blue.x);


    colour_is_not_displayable = true;

    % colour is displayable, if:
    if( (value_a > 0) && (value_b > 0) )
        %... both values are greater than 0. But this is not all:
        if( (value_a + value_b) > 1 )
            % The sum of the two values has to be greater than one too.
            colour_is_not_displayable = false; % Only adjust this semaphore if the colour is displayable.
        end
    end
    
    % For Matlab users, this looks weird, but I did this so it's similar to what's in the C-code
    if(colour_is_not_displayable)
        fprintf("Requested colour is: x=%.2f, y=%.2f\n", requested_chroma_x, requested_chroma_y)
        error("This colour can't be displayed, as it's outside the colour gamut!")
    end

    %% Proportionately how much do you need to drive your primaries?
    % Now that we know we are within the colour gamut, we can calculate the distance from the requested colour's coordinate to the primaries.
    % These are:
    
    red_distance = sqrt( (light_structure.red.x - requested_chroma_x)^2 + (light_structure.red.y - requested_chroma_y)^2 );
    green_distance = sqrt( (light_structure.green.x - requested_chroma_x)^2 + (light_structure.green.y - requested_chroma_y)^2 );
    blue_distance = sqrt( (light_structure.blue.x - requested_chroma_x)^2 + (light_structure.blue.y - requested_chroma_y)^2 );
    
    % These distances are INVERSELY PROPORTIONAL to the intensity of the primary colours.
    % So the closer the requested colour to the primary is, the more you need of it.
    % But this is not enough, because these are just the distances and not the proportions of intensity.
    % We need to find the point where the line stating from a pimary colour's coordinates that goes across the requested colour's coordinates hit the opposite side
    % of the triangle: We need to make two lines, and calculate the intersection. See this: http://zonalandeducation.com/mmts/intersections/intersectionOfTwoLines1/intersectionOfTwoLines1.html
    
    % These are the lines between the requested colour and the primaries
    red_line_slope = (requested_chroma_y - light_structure.red.y) / (requested_chroma_x - light_structure.red.x);
    red_line_intercept = requested_chroma_y - red_line_slope * requested_chroma_x;
    
    green_line_slope = (requested_chroma_y - light_structure.green.y) / (requested_chroma_x - light_structure.green.x);
    green_line_intercept = requested_chroma_y - green_line_slope * requested_chroma_x;
    infinite_green_slope = false;
    
    % The green slope can be infinite! This happens when the colour in question is directly below the green primary. This just means that we can calculate the
    % intersection point much easier.
    if( (requested_chroma_x - light_structure.green.x) == 0)
        infinite_green_slope = true;
    end
    
    blue_line_slope = (requested_chroma_y - light_structure.blue.y) / (requested_chroma_x - light_structure.blue.x);
    blue_line_intercept = requested_chroma_y - blue_line_slope * requested_chroma_x;
    
    % And now we calculate the line equations for the colour gamut too
    blue_green_line_slope = (light_structure.blue.y - light_structure.green.y) / (light_structure.blue.x - light_structure.green.x);
    blue_green_line_intercept = light_structure.blue.y - blue_green_line_slope * light_structure.blue.x;
    
    red_blue_line_slope = (light_structure.red.y - light_structure.blue.y) / (light_structure.red.x - light_structure.blue.x);
    red_blue_line_intercept = light_structure.red.y - red_blue_line_slope * light_structure.red.x;
    
    green_red_line_slope = (light_structure.green.y - light_structure.red.y) / (light_structure.green.x - light_structure.red.x);
    green_red_line_intercept = light_structure.green.y - green_red_line_slope * light_structure.green.x;

    % Now we can calculate the intersection points.

    % Red line - blue-green line intersection
    red_intersection_x = (blue_green_line_intercept - red_line_intercept) / (red_line_slope - blue_green_line_slope);
    red_intersection_y = red_line_slope * red_intersection_x + red_line_intercept;
    
    % Green line - red-blue line intersection. This is the one that CAN get simple
    if(infinite_green_slope)
        % If we have infinite slope, we are going down. So the X coordinate is alread solved!
        green_intersection_x = light_structure.green.x;
    else
        green_intersection_x = (red_blue_line_intercept - green_line_intercept) / (green_line_slope - red_blue_line_slope);
    end
    green_intersection_y = green_line_slope * green_intersection_x + green_line_intercept;
    

    % Blue line - green-red line intersection
    blue_intersection_x = (green_red_line_intercept - blue_line_intercept) / (blue_line_slope - green_red_line_slope);
    blue_intersection_y = blue_line_slope * blue_intersection_x + blue_line_intercept;


    % And now we can calculate the lengths.

    red_line_length = sqrt( (light_structure.red.x - red_intersection_x)^2 + (light_structure.red.y - red_intersection_y)^2 );
    green_line_length = sqrt( (light_structure.green.x - green_intersection_x)^2 + (light_structure.green.y - green_intersection_y)^2 );
    blue_line_length = sqrt( (light_structure.blue.x - blue_intersection_x)^2 + (light_structure.blue.y - blue_intersection_y)^2 );
    
    
    % We can now take the intensities into account.
    % An intensity of 0.2 means that it would have to be five times as luminous to produce as much light output as intensity of 1.
    
    red_drive = (1 - (red_distance / red_line_length)) / light_structure.red.intensity;
    green_drive = (1 - (green_distance / green_line_length)) / light_structure.green.intensity;
    blue_drive = (1 - (blue_distance / blue_line_length)) / light_structure.blue.intensity;
    
    
    % and we are STILL not done, because we ideally should maximise the drive on the highest colour channel.
    
    max_drive_value = max([red_drive, green_drive, blue_drive]);
    
    % Scale the values to something a microcontroller can understand: from 0-1 to 0-255.
    
    red_value = round(red_drive / max_drive_value * 255);
    green_value = round(green_drive / max_drive_value * 255);
    blue_value = round(blue_drive / max_drive_value * 255);
    

end

