# ["Talpán zöld betűk: Én vagyok a fény, a tűz / Hogy láss az éjszakában" (Omega)](https://youtu.be/4szfWQ7Sspg?si=UoTt90WBfxC32ZbN)

The light functions are relatively simple. There is a mex file available from [this](https://github.com/ha5dzs/udmx-matlab-commander), just copy it to your working path or wherever your matlab files are. They all use the [DMX512 standard](https://en.wikipedia.org/wiki/DMX512), so expanding this set-up is ideally a mere matter of getting more lights and more cables.

tldr: Every light has a base address that is set on the display, in the format of `Axxx`. Every additional function is available with respect to this address, and every channel can have the data values from 0 to 255.

There are additional caveats and intricacies, read below.

# Health and safety

Lights can be bright. Do not shine in the eyes, and avoid strobing as much as possible. Not only strobing is annoying, but photosensitive epileptics can develop seizures.

# Instruction sets

We have these RGB floodlights. They use 3x3 10W RGB chips, so there will be some parallax error between the primary colours. Additional diffusors might be needed to achieve colour uniformity when using in close proximity (1-2m).

Channel | Value range | Function
--------|:-------:|---------
Base address `+0` | `0-255` | Total Dimming
Base address `+1` | `0-255` | Red
Base address `+2` | `0-255` | Green
Base address `+3` | `0-255` | Blue
Base address `+4` | `0-255` | Strobe
Base address `+5` | `0-50` | No function
Base address `+5` | `51-100` | Preset colours
Base address `+5` | `101-150` | Silly effect 1
Base address `+5` | `151-200` | Silly effect 2
Base address `+5` | `201-255` | Sound-activated mode
Base address `+6` | `0-255` | Silly effect change speed (slow to fast)

We also have these RGBW spot lights. They look good, but the parallax error in the RGBW LED assembly might make it difficult to project colours uniformly. Also, there is some chromatic aberration in the lens, and since it's just a simple plastic thing, you will get some polarisation artefacts too. Again, the more distance there is between the light and the target, the less this effect is.

Channel | Value range | Function
--------|:-------:|---------
Base address `+0` | `0-255` | Total dimming (and strobing below 100 or so)
Base address `+1` | `0-255` | Red
Base address `+2` | `0-255` | Green
Base address `+3` | `0-255` | Blue
Base address `+4` | `0-255` | White
Base address `+5` | `0-255` | Effect
Base address `+6` | `0-255` | Speed

# The fact that Microsoft Windows is used

When using Windows to drive the uDMX dongle, please be careful not to update it too often (as in less than every 10 milliseconds or so). If this happens, Windows 11's kernel will block the device (Code 48, then Code 10), and will keep it blocked until the number of USB requests to this device drop below a certain threshold. Use and modify `volciclab_lights_restore_access' to check if the device is available again as required. While it's good practice to use as few USB requests as possible, sometimes it is unavoidable. Also, depending on future Windows updates, the hammering threshold (possibly something set in the scheduler algorithm) policy may change, as it did in the January 2024 update. Disabling core isolation and memory integrity protection may help this issue.

# Intel USB 3.0 root hub

If you set spurious temporary device failures in Device Manager, try changing the USB root hub. Either use something in a Thunderbolt port with a USB-C connector, or try to connect the device in a separate PCI-E USB root hub on the workstations is possible. The uDMX device also has two LEDs. One is for power, the other is for data communication. If still in doubt, use a packet sniffer like Wireshark.

# Wiring

DMX512 is related to [RS-485](https://en.wikipedia.org/wiki/RS-485), with the exception that an entire 512-byte frame is transmitted. This standard is using differential signal, and ideally has some pull-up resistors for the two data lines. In reality, from the cheap lights, these are missing. This causes an impedance mismatch along the transmission line (i.e. the 'wiring'), which will cause signal reflection. This makes it prone for some DMX addresses to not have valid data in the frame in certain physical location along the cable. If you get this, and you don't have a signal repeater or at least a 12V pull-up resistor set, then you need to change the address of the light you have trouble with.

# The code to drive this device

From `dmx.mex`, you have the `dmx('send', [addresses], [data_values])` function. While this function can accept one address and data value, if you use multiple values, then the addresses have to be consecutive, and strictly monotonically increasing. So you cannot just cherrypick the addresses you want, you will need to specify the entire range. The data vector length must match the address vector length. You don't have to do any variable conversion and casting, the C-code does that internally.
As this is a mex file, if it crashes, Matlab will crash too, which will destroy all your unsaved data in the memory. There are a number of sanity checks and error messages in place, but this is no guarantee for anything. Save your workspace every trial as required.


# Calibration

As these devices are using [pulse width modulation](https://en.wikipedia.org/wiki/Pulse-width_modulation), the luminance output for a channel will be linear ($\gamma$=1) by principle. This is where the advantages end.

The white LEDs are blue chips with cold white phosphor (x=0.31, y=0.32), so the colour rendering will not be great. Also, the output intensity for each primary colour is nowhere near matched to human vision, so `RGB = [255, 255, 255]` will not give you D65 illumination at all.

In order to calibrate these lights, you need a calibrated chromameter. You need to measure the chromaticity of every light's every primary, and log down their intensities, and package this information into a structure like so:
```Matlab
% These are measured with the Sekonic C-800, on 06/11/2023.
% Intensity values are relative.

%% Right light

right = struct;

right.red.x = 0.6944;
right.red.y = 0.3028;
right.red.intensity = 0.45;

right.green.x = 0.1356;
right.green.y = 0.6893;
right.green.intensity = 0.33;

right.blue.x = 0.1474;
right.blue.y = 0.0458;
right.blue.intensity = 1;

%% Left light

left = struct;

left.red.x = 0.6927;
left.red.y = 0.3054;
left.red.intensity = 0.39;

left.green.x = 0.1358;
left.green.y = 0.6954;
left.green.intensity = 0.35;

left.blue.x = 0.1466;
left.blue.y = 0.0472;
left.blue.intensity = 1;

```

See [sekonic_light_calibration.m](seconic_light_calibration.m) for a particular implementation, but you need to change it to your set-up.


## `volciclab_lights_get_rgb_values()`

This function is a Volciclab-specific implementation from a workshop held on the [Vision Sciences Society Conference in 2019](https://github.com/ha5dzs/InvisibleMacGyver/tree/master/WorkshopMaterial#creating-a-calibrated-d65-or-any-other-chromaticity-coordinate-light-source).

When you generated the specific data structure for each light, this function creates the necessary PWM values between 0 and 255 to generate the requested chromaticity.

**A couple of sentences of caution:**

* With some lights, the global dimming is also done with PWM, which in turn will affect the ratio of the times the three primaries are on.
This means that the there can be a colour difference when you use the global dimming channel when a primary is hitting an upper or lower extreme PWM value.

* A cold LED is of a different colour to a warm LED.
The drift is about 100 pm/K for SiInGaP chips, and for GaN-based chips, it is not linear at all. Considering that many drives absolutely grill these chips, 60-80K temperature difference is not unheard of, so you should run these lights for a good 15-20 minutes on 75% brightness before calibrating. Just like in the good old days with CRT monitors.

* The code assumes that LEDs are monochromatic. They are not, they have an about 15-20 nm half-power bandwidth.
This means that the chromaticity coordinates coming from the chromameter are approximates, and you should treat them as such.

Always verify your chromaticity coordinates, and adjust the relative intensity values in the structure accordingly.

Otherwise:
```
%VOLCICLAB_LIGHTS_GET_RGB_VALUES Using a defined light structure, this
% function creates the necessary RGB values for a specified chromaticity
% coordinates. See
% https://github.com/ha5dzs/InvisibleMacGyver/tree/master/WorkshopMaterial#creating-a-calibrated-d65-or-any-other-chromaticity-coordinate-light-source
% for a detailed explanation on how this works.
% Input arguments are:
%   -light_structure, which is a structure of the following fields:
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
% -requested_chroma_y is the CIE 1931 y coordinate you want
```
