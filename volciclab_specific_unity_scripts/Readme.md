# Volciclab-specific Unity scripts for VR use in the lab

These are a collection of scripts that are instrumental in implementing the [concept of the game engine running inside the VR headset being nothing else but a small aperture into an interactive volumetric display](https://doi.org/10.1167/jov.25.9.2930). They are not really intended for beginners, and some of them are depending on external hardware, such as the motion tracker.

Note that this is not necessarily about interfacing the game engine with the motion tracker, [those scripts are in a different repository, and are stored elsewhere](https://github.com/ha5dzs/optitrack-motive-data-streamer/tree/main/examples/Unity). That being said, there is a distinct overlap, probably because it was written by the same person. :)

Also note that this is in active development, so the documentation is not quite up to date. Luckily there are lots of comments in left in the code, so it is relatively easy to understand what is going on.

## `GazeContingentTransform.cs`

This is for the eye tracker of the Meta Quest Pro. I believe that Meta intended the eye tracker to be used for modifying the eyes of external meshes, such as an avatar based on the user. I found the implementation problematic: the tracker is noisy and it does not measure vergence angles despite getting the eye coordinates from both eyes. So I ended up butchering their code. Allegedly they grant permission, provided that appropriate credit is given, which is hereby done.

I took the gaze coordinates from both eyes, and provided that the eye tracker's confidence level is high enough, I converted them to headspace, and then [slerped](https://dl.acm.org/doi/abs/10.1145/325334.325242) them to find the gaze angle from the cyclopean perspective.

When this script is attached to a GameObject, it will adjust the rotation according to the gaze vector.

## `gazeVectorTransmitter.cs`

This script gets the eye tracker's position, and calculates the gaze vector's azimuth and elevation with respect to the head. It also transmits it over the network as plain text in UDP packets to a specified IP address and port. The streaming rate can be set with the `decimation` global variable.

## `passthrough_external_control.cs`

It listens on a specified UDP port. If the `0` (as in ASCII `0x30`) is being sent to a specified port (7511 by default), the passthrough is disabled. For literally anything else, the passtrhough is enabled. This is handy for testing and visually verifying coordinate system alignment.

## `SilverAnnulus.cs`

This code is mostly AI-generated. It creates an annulus by generating a mesh with customisable inner and outer diameter. Perfect to be used together with `GazeContingentTransform.cs` to induce a number of visual deficits.

## `universe_relocator.cs`

This is to be used together with our motion tracker. **The headset must have a working rigid body added, with the transformation point being the cyclopean perspective!** When _any_ UDP packet is being sent to port 7510 by default, this will request the latest position and rotation from the motion tracker for the specified rigid body (ideally on the headset), and adjusts the camera rig inside the game engine such that the current headset coordinates and rotation matches with what is reported in the motion tracker.

When done properly, this allows the headset to 'teleport' into the same coordinate system as the motion tracker, so the appearance of externally tracked objects would match with what is presented physically. The script also does some rudimentary iteration, so if the position and rotation difference is too great, it will do the alignment again.
