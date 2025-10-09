# The Volciclab [dar](http://dar.linux.free.fr/) archiving regime

May we never need to use this archive. Hopefully, this would be one of those 'create and forget about' things.

Dar is a disc archiver utility. In Volciclab, we use this for the yearly cold-storage archives on non-magnetic storage media. These are not incremental, just stand-alone snapshots. This software adds better media error management capabilities than just straight imaging. It allows higher compression ratio than just zipping, and more robust slicing than practically any other software out there.

It is cross-platform, open-source and the versions are more-or-less reverse-compatible with each other.

## Particulars

* We have archival-grade (M-Disc) 50 GB dual-layer Blu-Ray discs
* As of September 2025:
  * we have about 1 TB of data taking up more than 2 TB of space
  * there are many small files such as trials in CSV, Unity projects, for example
  * with compression, the whole thing fits on only 8 discs!
* Windows binaries and source code for dar 2.8.0 included on media
  * There is documentation included too. You are reading some of it. :)

## Creating the slices

Let's assume you use windows. Let's assume that you want to back up Drive `X`, and you want to save the slices to Drive `F`. Then, you will burn these slice files to the disc with your favourite disc authoring software and you will use UDF.

According to Windows, a 50 GB Blu-Ray disc has `48 440 016 896` bytes available. The dar binaries and source code, along with this documentation, and the catalogue for the archive will take up some space.

Just to be safe, let's round the size of the dar file down to `48 000 000 000` bytes.

There is a bit of confusion here. Computer data memory uses 1024, but computer data storage uses 1000 for expressing three orders of magnitude. I am not sure why is this, probably for marketing.

* In computing:
  * for memory allocation, a megabyte is 1024 kilobytes, which are `1024*1024*1024` bytes.
    * This is sometimes annotated is MB, or MiB (pronounced as 'mebibyte') when the software is a bit pedantic
  * for computer storage sizes, a megabyte is 1000 kilobytes, and `1000*1000*1000` bytes.
    * This is usually annotated as MB

Now, dar, being a computer software 'megabytes' being the same as 'mebibytes': 1024*1024*1024 bytes, unless you specifically ask it to use SI units.

Then the command to create 48.4GB slices of everything in `X:\`, compress it with `xz` algorithm and maximum compression ratio (9), to beep for attention when something went wrong, and to create `volciclab_apocalypse.X.dar` where X is the slince number, will be:


For Windows, because the windows executable is running in a bundled environment of Cygwin:
```powershell
./dar --alter=SI-units --slice 48000M --multi-thread 8 --compression=xz:9 --beep --fs-root /cygdrive/x --create /cygdrive/f/volciclab_apocalypse --verbose
```

For unix systems, the paths should be absolute:
```powershell
./dar --alter=SI-units --slice 48000M --multi-thread 8 --compression=xz:9 --beep --fs-root /media/x --create /media/f/volciclab_apocalypse --verbose
```

This is going to take a while. Probably a few days. Not a joke. Make sure that there will be no interruption in power, the system won't go to sleep or hibernation, and no system reboots are scheduled while this is running. When using the maximum compression (9) for xz, dar will need about 2 GB of RAM. If dar is compiled with `libthreadar` and using `--multi-thread X`, where `X` is the number of compression threads, the memory requirements increase considerably. Not specifying this argument will make dar try to work with two threads or a single thread.

## Testing the archive

Do not skip this step. DO NOT SKIP THIS STEP. DO **NOT** SKIP THIS STEP! Do this step BEFORE burning to media, and AFTER burning to media.

This is also going to take a while. Test the archive before and after burning to disc.

```
dar  --multi-thread 8 --test /cygdrive/f/volciclab_apocalypse --verbose
```

The `--multi-thread 8` option may not work. On Unix systems, the path may be different, e.g. `/media/f`.

## Creating an isolated catalogue

Normally, `dar` stores the catalogue in the last slice. If that file gets corrupted, then the entire archive is lost. So, it may be worthwhile isolating the catalogue and save it as a separate file. In this case, it creates `volciclab_apocalypse_dar_catalogue.1.dar` in the directory of the `volciclab_apocalypse` archive. This file can be read directly by `dar`, should the last slice of the archive be lost/damaged.

```powershell
./dar --isolate /cygdrive/f/volciclab_apocalypse_dar_catalogue --ref /cygdrive/f/volciclab_apocalypse --compression=xz:9
```

### Creating a human-readable catalogue

If you have all the dar files in a single directory (to use the previous example, in the root of `F:\volciclab_apocalypse.X.dar` where X is the slice number), as per the manual, the standard output can be redirected into a `volciclab_apocalypse_human_readable_catalogue.txt`:

It is useful to include a human-readable catalogue that lists what files are in each slice, especially `dar` needs the complete path to extract a particular file from the archive.  This way, it is also possible to estimate what files have been lost.

```powershell
./dar --list /cygdrive/f/volciclab_apocalypse --list-format=slicing > F:\volciclab_apocalypse_human_readable_catalogue.txt
```

The `--list-format=slicing` option formats the output by slices. This way, if only a certain file needs to be extracted, it is possible to load the contents of that one slice. The resulting txt file can be used for searching/parsing, or to be included with the archive.

## Extracting an archive

Hopefully this will not ever be needed. There are more convenient, online, regularly maintained backups in Volciclab.

Ideally, all the `dar` files need to be copied together. The last dar file in the set is the most important one, as it contains the catalogue.

You can also use `dar_manager` or any other software that supports dar. This could be a plug-in in Midnight Commander, Kdar, gdar, DarGUI or WebDar.

### Extracting a single file

If you found what file you want by its path using the human-readable catalogue, let's say it's `Zoltan\i1_calibrate_problem.txt`, and you want to save it to say Drive `G`, and the archives are in Drive `F`, then:

```powershell
dar --fs-root /cygdrive/g --extract /cygdrive/f/volciclab_apocalypse --go-into Zoltan/i1_calibrate_problem.txt
```

(...or, in your unix system)
```shell
dar --fs-root /media/g --extract /media/f/volciclab_apocalypse --go-into Zoltan/i1_calibrate_problem.txt
```

Note that the path doesn't accept wildcards, so you can't use something like `Zoltan/*.txt` or similar. In case you need to extract multiple files, you can put several `--go-into <path_relative_to_archive>` switches in a single statement. It also works with directories

### Extracting with external catalogue

If the archive's internal catalogue is damaged, it is possible to use the external file. Using the `volciclab_apocalypse_dar_catalogue.1.dar` as the catalogue reference from the example above, to get the directory `website`, the command becomes:

```powershell
./dar --fs-root /cygdrive/g --extract /cygdrive/f/volciclab_apocalypse --go-into website --ref /media/f/volciclab_apocalypse_dar_catalogue.1.dar
```

### Extracting from an archive with missing/damaged far files

It is possible to extract whatever is available in a given slice, but not a single slice on its own. For this, use the `--sequential-read` option:

```powershell
dar --fs-root /cygdrive/g --extract /cygdrive/f/volciclab_apocalypse --sequential-read --ref volciclab_apocalypse_dar_catalogue.1.dar
```

If this fails too, you can exclude the directory that is in a particular slice, or try targeting what you want directly.

...and if necessary, recover a file that is known to be in the remaining slices:

```powershell
dar --fs-root /cygdrive/g --extract /cygdrive/f/volciclab_apocalypse --sequential-read --ref volciclab_apocalypse_dar_catalogue.1.dar --go-into Zoltan/i1_calibrate_problem.txt
```

Good luck. May you never need this.