# The Volciclab OptiTrack Implementation

**Quick Matlab example, because life is too short!:**

```matlab
% Set up your rigid bodies in Motive before doing anything in Matlab!
clear all;
clc;

% Initialise the system:

experiment_name = 'test_experiment';
path_to_your_session = 'Z:/wherever_your_stuff_is'; % On your network drive
recording_name = 'trial001'; % You probably update this in the experimental loop


% Your own (NOT the server's) IP address, and the session (experiment) name
motion_tracker = volciclab_optitrack_init('192.168.42.154', experiment_name);


% Check loaded rigid bodies. Motive is already set to Live mode, so we can access this.
[no_of_rigid_bodies, rigid_body_ids, rigid_body_names] = volciclab_optitrack_get_rigid_body_info(motion_tracker);

% Stream data: get latest frame
[framecounter, timestamp, translation, quaternion] = volciclab_optitrack_get_rigid_body_data(motion_tracker, rigid_body_names);

% Display some data
fprintf('Coordinates for rigid body %s:\n', rigid_body_names(2))
translation(2, :)

% Set a recording name, and record for a couple of seconds.

fprintf('Starting recording now.\n')
% You can specify your own custom names (trial numbers) here.
volciclab_optitrack_start_recording(motion_tracker, recording_name);

pause(rand()*3); % Just randomly wait

volciclab_optitrack_stop_recording(motion_tracker)

fprintf('Recording stopped.\n')

% Gracefully retire the connection
volciclab_optitrack_kill(motion_tracker);

% We can also convert .tak files to CSV, and access the data from Matlab. [READ THE DOCS AT THE END OF THE PAGE ABOUT HOW THIS WORKS!]
my_data_with_quaternion_rotations = volciclab_optitrack_get_take( ...
  sprintf("%s/%s%s.tak", path_to_your_session, experiment_name, recording_name), ...
  sprintf("%s/%s%s.csv", path_to_your_session, experiment_name, recording_name));

% Note the extra argument here. See what this does below.
my_data_with_euler_rotations = volciclab_optitrack_get_take( ...
  sprintf("%s/%s%s.tak", path_to_your_session, experiment_name, recording_name), ...
  sprintf("%s/%s%s.csv", path_to_your_session, experiment_name, recording_name), ...
  1);

```

All lab computers have a network drive, `Z:\` mounted, which is where Motive records the take files.

## NatNet SDK

For everything OptiTrack, we are using the Matlab wrapper for the [NatNet SDK](https://v23.wiki.optitrack.com/index.php?title=NatNet_SDK_3.1). It has three files:

```
[Volciclab OptiTrack tools]
    ├ NatNetLib.dll
    ├ NatNetML.dll
    └ natnet.m
```

Occasionally in Matlab, the path to the `NatNetML.dll` _assembly_ might need to be specified. The DLL files must be in the same directory. Matlab can produce a strange error message when initialising communications. If this happens, delete the `assemblypath.txt` file, and run the code again.

**[IMPORTANT]:** the _natnet class_ in Matlab has been modified for the Volciclab implementation: the `sendMessageAndWait()` is now public, so users can send [Motive server requests and commands](https://v23.wiki.optitrack.com/index.php?title=NatNet:_Remote_Requests/Commands) directly. This was required to bypass some Matlab limitations of assigning values to temporary variables.

***
## Matlab function reference

Here are the detailed explanations of the functions used.
***

### `volciclab_optitrack_init(volciclab_optitrack_init(your_ip_address, session_name)`

This function initialises communication to the Motive server. Prior calling this function, all the markers and rigid bodies have to be set up in Motive.
`your_ip_address'` should be a character array, such as `'192.168.42.154'`. Since we are streaming data using multicast, your own computer's IP address on the Volciclab network (`192.168.42.x`) needs to be specified. If you are using a lab computer, the IP address will be printed on a label.
`session_name` is the directory Motive will create, where the take files will go. Note that it may be in a strange location, for example inside an other session directory, with today's date on it.

This function returns a `natnet_object`, which you can either handle directly with the pulic methods, or pass on as the first argument of most `volciclab_optitrack` functions.
***

### `volciclab_optitrack_get_rigid_body_info(natnet_object)`

This function returns specific information about rigid bodies loaded in the system. The input argument is the natnet object you created using `volciclab_optitrack_init()`
Return values are (in order):

- `framecounter`, which is the number of frames captured since data acquisition began
- `timestamp` in microseconds, which is the time elapsed since data acquisition began
- `rigid_body_ids`, which is a number of array of numbers, for each rigid body
- `rigid_body_names`, which is a string array of the names of the rigid bodies you assgned in Motive.

***

### `volciclab_optitrack_get_rigid_body_data(natnet_object, rigid_body_names)` or

### `volciclab_optitrack_get_rigid_body_data(natnet_object, rigid_body_ids)`

This function gets the latest rigid body data from the system. `natnet_object` is the object you created using `volciclab_optitrack_init()`. In this function, you can get a single or multiple rigid body data, and you can refer to them with either IDs, or by their names as array. **Please do not mix numbers and strings in a single in the input argument.**.

For example, let's say that you have two rigid bodies in the system: 'Bonkers Conkers' and 'Jimi Matala'. The IDs are '1', and '2', respectively.

You can get a single rigid body data using:
`volciclab_optitrack_get_rigid_body_data(natnet_object, 134`, (note that this is numeric value) or
`volciclab_optitrack_get_rigid_body_data(natnet_object, [1])`, (note that this is numeric array) or
`volciclab_optitrack_get_rigid_body_data(natnet_object, "Jimi Matala")` (note that this is a string) or
`volciclab_optitrack_get_rigid_body_data(natnet_object, 'Jimi Matala")` (note that this is a character array)

The function automatically detects which input argument you used.

You can also get a set of multiple rigid bodies, in any order. Just put them into an array..
`volciclab_optitrack_get_rigid_body_data(natnet_object, [43, 28])`, or
`volciclab_optitrack_get_rigid_body_data(natnet_object, {"Jimi Matala", "Bonkers Conkers"})`

**[IMPORTANT]:** The order of the return data is exactly the same as you specified in the input arguments.

Return values are:
`framecounter`, `timestamp`, these are single numbers, one specified the number of frames, the other specifies the time elapsed in microseconds since data acquisition began
`translation` is an n-by-3 array, for n number of rigid bodies. They are X-Y-Z triplets. As per the streaming settings in Motive, Z is upward, and units are in metres.
`quaternion` is an n-by-4 array, for n number of rigid bodies. This is the rotation data for each rigid body, in quaternion (`W-X-Y-Z` or `w-i-j-k`) format.
***

### `volciclab_optitrack_start_recording(natnet_object, take_name)`

This function names the take, and starts the recording. `natnet_object` is the object you created using `volciclab_optitrack_init()`, and `take_name` will be the name of the .tak file Motive records data into. the recording starts immediately* (*allowing a few milliseconds for network and processing delays) after calling this function.
***

### `volciclab_optitrack_stop_recording(natnet_object)`

This function is the same as calling `natnet_object.stopRecord;`, if you ever bothered reading the SDK documentation. `natnet_object` is the object you created using `volciclab_optitrack_init()`. This function literally only exists so all functions will have the `volciclab_optitrack...` prefix.
Recording stops immediately* (*allowing a few milliseconds for network and processing delays) after calling this function.
***

### `volciclab_optitrack_kill(natnet_object)`
This function gracefully terminates connection to the Motive server. This, however, does not stop Motive itself.`natnet_object` is the object you created using `volciclab_optitrack_init()`.
***

### `volciclab_optitrack_get_take(tak_file_path, csv_file_path, <optional: rotation_format>)`

This function calls the _.tak to .csv converter_ to create an easily readable csv file from the proprietary and binary .tak files. Then it reads the .csv file, and returns the rigid body data to the Matlab environment the function is called from. The advantage is that you can call this from within Matlab, and you don't have to have a licence for Motive on your computer. This comes at a price of a performance overhead, so it will work slower than the [Batch processor](https://docs.optitrack.com/v/v2.3/motive/motive-batch-processor) or Motive itself.

Besides the paths to the files, you can specify the `rotation_format` as well, which is as follows:

- 0: Quaternion, `w-x-y-z` (`w-i-j-k`)
- 1: Euler, `X-Y-Z`
- 2: Euler, `X-Z-Y`
- 3: Euler, `Y-X-Z`
- 4: Euler, `Y-Z-X`
- 5: Euler, `Z-X-Y`
- 6: Euler, `Z-Y-X`


If you don't specify this optional input argument, the rotation will be quaternion.

This function returns `rigid_body_structure`, with the following fields:

- framecounter, which is a sequence starting from 0.
- time, in seconds, which tells you the time difference between subsequent frames
- rigid_body(n), which is a structure array, for n rigid bodies you had in the recording. Its fields are:
  - rigid_body(n).translation is a set of triplets of X-Y-Z coordinates for each frame. **The units are in millimetres!**
  - rigid_body(n).rotation is the rotation, in the format and order as requested above **The units are in degrees!**
  - rigid_body(n).tracking_error is the tracking error of the rigid body in each frame, in millimetres.
**[IMPORTANT]:** The rigid body indexing is exactly the same as you set it in Motive. Since the header of the .csv files generated is following a non-standard, Matlab has a hard time reading it. If in doubt, check the .csv file itself.

## .tak to .csv converter

This code is written in C#, and is a 'fork' of the [Motive Batch Processor](https://v23.wiki.optitrack.com/index.php?title=Motive_Batch_Processor). This batch processor is using the NMotive API, which is a totally different from the [Motive API](https://v23.wiki.optitrack.com/index.php?title=Motive_API), which is again completely different from the [NatNet SDK](https://optitrack.com/software/natnet-sdk/) the rest of the Matlab code is based on. If you are having a hint of confusion here, this is normal. You hopefully never have to interact with these directly.

This executable is loading the take file directly and converts it to .csv. Due to the nature the Motive Batch Processor is implemented, the converter requires about 20 DLL files for it to work (all included, so no need to copy stuff anywhere), and it takes a couple of seconds for it to do the conversion. So this code should ideally be run after a trial, or after an experimental session. Note that the code is _blocking_ so execution will hold if called from an other function such as `optitrack_volciclab_get_take()`.

More info is avaiable on the particulars of this code [here](https://github.com/ha5dzs/optitrack-motive-file-converter).