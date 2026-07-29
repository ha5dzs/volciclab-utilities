# In case of disaster: setting up the `volciclab-server` from scratch

Linux is being used here, some sort of a Debian-flavour. I chose xubuntu for its small memory footprint.

## Prerequisites

In order to do these, you need to have at hand:

* Understand concepts of Unix-like systems, such as:
  * What the kernel does
  * How hardware is accessed
  * Users, groups, permissions, owners
* A working system with a sudo-capable account
 * IP address appropriately set up
* Connection to the lab network and/or the Internet
* Time an understanding, especially considering that:
A lot of open-source software is being used, config files and terminology changes, and sometimes progress is with negative magnitude. This is written in July 2026 as I set it up, so at least we have a known-good configuration.

## Open a terminal and install the basics:

Let's say we have a fresh install. This won't hurt.

```bash
sudo apt-get update
```

```bash
sudo apt-get upgrade
```

Now, install some software. This is going to take a while.

```bash
sudo apt-get install mc f3 gparted smartmontools openssh-server apache2 zfs-dkms zfsutils-linux samba apt-file cifs-utils
```

During this installation, you were probably prompted about the licence differences between GNU GPL and whatever zfsutils use, and you saw that a new kernel image has been generated. Now it's a good time to reboot.

```bash
sudo reboot
```

Once rebooted, you have `apache2` working on port 80, and `openssh-server` working on port 22, and you can access the machine remotely. Despite this, if you want to use the built-in monitor instead, temporarily while things are being set up, you can disable the screen saver and display standby.

```bash
xset s off
```

```bash
xset -dpms
```

Let's fill the `apt-file` cache, which will help you in finding some obscure binary later-on. You will thank me later for this.

```bash
apt-file update
```

You probably got a notification that there are some redundant packages on your system. So:

```bash
sudo apt autoremove
```

For permission masks and ownership, it is good to note down your user's `uid` and `gid`. You can do this by:

```bash
id
```

Unless there was some treachery, both the `uid` and `gid` are usually `1000`.

If you installed some new drives, especially if you bought them online, always, **always** **ALWAYS** verify storage and performance. You can do this by:

* starting `gparted` and creating a partition table, and a partition.
  * Let's say, create an exfat partition on `/dev/nvme0n1`, which then becomes `/dev/nvme0n1p1`.
* Temporarily, in your home directory: `mkdir ssdtest`, so its path will be `~/ssdtest`
* Mount the freshly created file system the mount point your just created.
  * `sudo mount -t exfat -o uid=$(id -u),gid=$(id -g) /dev/nvme0n1p1 ~/ssdtest`
* Write it full with test files, and verify them.
  * `f3write ~/ssdtest && f3read ~/ssdtest`
  * (this can take hours, so periodically check the terminal, look for errors that may indicate that the drive is fake/faulty)

## Setting up the zfs pool and volume and the shares

Anything storage-related is in `/mnt`. Such as:

| NAME | Description |
| -------------- | ------------------- |
| `/mnt/zfs_pool/zfs_volume` | The mirrored SSDs live here. |
| `/mnt/nyuad_file_share` | Our shared drive on the NYUAD network. Only used for non-destructive nightly sync. |
| `/mnt/nas_16_tb_volume` | The 2x16 TB hard drive on the NAS, for stuff that we don't need often. |
| `/mnt/nas_4_tb_volume` | The 2x4 TB SATA SSD on the NAS, which is the clone of zfs_volume. Nothing else uses or accesses this. |
| `/mnt/samba` | Generic SMB/cifs share, if you need to mount something over the network (say some OptiTrack-related stuff) you can use this mount point. |

For copying and restoring stuff, you'll need a file manager. Midnight Commander (`mc`) is one of the best terminal-based orthodox file managers. Once you finished copying, set the permissions and ownership `chmod -R xxx <the directory's name>` and `chmod -R <your user name>:<your user name> <the directory's name>`.

### ZFS

So, on this server, you have 3 SSDs: the 2x4 TB ones for the actual storage, and a 500 GB one for the system. You can verify this by:

```bash
$ lsblk
NAME        MAJ:MIN RM   SIZE RO TYPE MOUNTPOINTS
nvme0n1     259:0    0   3.6T  0 disk
└─nvme0n1p1 259:3    0   3.6T  0 part
nvme2n1     259:1    0   3.6T  0 disk
└─nvme2n1p1 259:2    0   3.6T  0 part
nvme1n1     259:4    0 465.8G  0 disk
├─nvme1n1p1 259:5    0 464.7G  0 part /
└─nvme1n1p2 259:6    0     1G  0 part /boot/efi
$
```

So, in this case, `/dev/nvme1n1` is the SSD the system is on, and the root partition is `/dev/nvme1n1p1`. The 4 TB SSDs are `/dev/nvme0n1` and `/dev/nvme2n1`. Note that these may be arbitrary, depending on the kernel. In modern Linux kernels, the individual partitions have UUIDs assigned, which can be accessed by `sudo blkid`. We will use this for mounting as/when. **Note that the partitions on the 4 TB SSDs (`/dev/nvme0n1p1` and `/dev/nvme2n1p1` respectively) are going to be destroyed in the process.** So only do this once you made sure appropriate backups are/were made.

Before we begin, let's verify that that we are indeed on a vanilla system.

```bash
$ zpool status
no pools available
$
```

If you don't get this, and you know what you are doing, then `sudo zpool destroy -f <the pool's name>`.

Let's say we want to set up `/dev/nvme0n1` and `/dev/nvme2n1` in a mirror configuration. But, they contain a file syetem, so we will wipe them
**IMPORTANT:** This is a **weapon of mass destruction!** Once false move and you can PERMANENTLY destroy data. Including your own system disk. No questions or confirmation will be asked, and it CANNOT BE UNDONE, because you are literally overwriting everything with randomly generated data.

So, let's fill the drives with random data. This is also good as a stress test: if the drive overheats, slows down, and cuts out, then it may not be suitable for the file server. Depending on size, this is going to take a while. In the meantime, you can take a look at some drive self-diagnostics with:  `sudo smartctl -a /dev/nvme0n1` and `sudo smartctl -a /dev/nvme0n1`. Note anything abnormal while you abuse these drives. Sectors should not be reallocated, data transfer speed should be consistent, and it should not heat up too much.

```bash
$ sudo dd if=/dev/urandom of=/dev/nvme0n1 bs=8M status=progress

$
```

```bash
$ sudo dd if=/dev/urandom of=/dev/nvme2n1 bs=8M status=progress

$
```

Note anything abnormal while you abuse these drives. Sectors should not be reallocated, there should be no CRC or other media errors. Data transfer speed should be consistent, and the drive not heat up too much - even though it screams abuse and cries for help. **If anything goes wrong at this point, or you just _suspect_ that things are _not perfect_, STOP, and DO NOT use these drives.** This is a file server, people store their stuff on it, and they expect to find them _intact_. You cannot afford risking data integrity with some wonky honka-bonka drives. It doesn't matter if it's new or used, this testing **must** be done by you, and you **must** verify operation _personally_ before you deploy it.

Here is an example for `sudo smartctl -a /dev/nvme0n1`:

```bash
$ sudo smartctl -a /dev/nvme0n1
[sudo: authenticate] Password:
smartctl 7.5 2025-04-30 r5714 [x86_64-linux-7.0.0-28-generic] (local build)
Copyright (C) 2002-25, Bruce Allen, Christian Franke, www.smartmontools.org

=== START OF INFORMATION SECTION ===
Model Number:                       Samsung SSD 990 PRO 4TB
Serial Number:                      S7DPNJ0YA02133Y
Firmware Version:                   4B2QJXD7
PCI Vendor/Subsystem ID:            0x144d
IEEE OUI Identifier:                0x002538
Total NVM Capacity:                 4,000,787,030,016 [4.00 TB]
Unallocated NVM Capacity:           0
Controller ID:                      1
NVMe Version:                       2.0
Number of Namespaces:               1
Namespace 1 Size/Capacity:          4,000,787,030,016 [4.00 TB]
Namespace 1 Utilization:            4,000,786,767,872 [4.00 TB]
Namespace 1 Formatted LBA Size:     512
Namespace 1 IEEE EUI-64:            002538 4a5140e97f
Local Time is:                      Wed Jul 29 19:47:43 2026 +04
Firmware Updates (0x16):            3 Slots, no Reset required
Optional Admin Commands (0x0017):   Security Format Frmw_DL Self_Test
Optional NVM Commands (0x0055):     Comp DS_Mngmt Sav/Sel_Feat Timestmp
Log Page Attributes (0x2f):         S/H_per_NS Cmd_Eff_Lg Ext_Get_Lg Telmtry_Lg Log0_FISE_MI
Maximum Data Transfer Size:         512 Pages
Warning  Comp. Temp. Threshold:     82 Celsius
Critical Comp. Temp. Threshold:     85 Celsius

Supported Power States
St Op     Max   Active     Idle   RL RT WL WT  Ent_Lat  Ex_Lat
 0 +     9.39W       -        -    0  0  0  0        0       0
 1 +     9.39W       -        -    1  1  1  1        0       0
 2 +     9.39W       -        -    2  2  2  2        0       0
 3 -   0.0400W       -        -    3  3  3  3     4200    2700
 4 -   0.0050W       -        -    4  4  4  4      500   21800

Supported LBA Sizes (NSID 0x1)
Id Fmt  Data  Metadt  Rel_Perf
 0 +     512       0         0

=== START OF SMART DATA SECTION ===
SMART overall-health self-assessment test result: PASSED

SMART/Health Information (NVMe Log 0x02, NSID 0x1)
Critical Warning:                   0x00
Temperature:                        59 Celsius
Available Spare:                    100%
Available Spare Threshold:          10%
Percentage Used:                    0%
Data Units Read:                    7,814,634 [4.00 TB]
Data Units Written:                 8,592,645 [4.39 TB]
Host Read Commands:                 30,539,770
Host Write Commands:                33,637,092
Controller Busy Time:               69
Power Cycles:                       2
Power On Hours:                     2
Unsafe Shutdowns:                   0
Media and Data Integrity Errors:    0
Error Information Log Entries:      0
Warning  Comp. Temperature Time:    0
Critical Comp. Temperature Time:    0
Temperature Sensor 1:               59 Celsius
Temperature Sensor 2:               73 Celsius

Error Information (NVMe Log 0x01, 16 of 64 entries)
No Errors Logged

Self-test Log (NVMe Log 0x06, NSID 0xffffffff)
Self-test status: No self-test in progress
No Self-tests Logged
$
```

...and, when both `dd`-s are done, if we do `lsblk`:

```bash
$ lsblk
NAME        MAJ:MIN RM   SIZE RO TYPE MOUNTPOINTS
nvme0n1     259:0    0   3.6T  0 disk
nvme2n1     259:1    0   3.6T  0 disk
nvme1n1     259:4    0 465.8G  0 disk
├─nvme1n1p1 259:5    0 464.7G  0 part /
└─nvme1n1p2 259:6    0     1G  0 part /boot/efi
$
```

Okay, let's create the pool. Note that the `-f` is there, just in case you felt sorry for the drive and only partially wiped them, leaving some corrupt garbage stuff around.

```bash
sudo zpool create -f zfs_pool mirror /dev/nvme0n1 /dev/nvme2n1
```

Verify:

```bash
$ zpool status
  pool: zfs_pool
 state: ONLINE
config:

	NAME         STATE     READ WRITE CKSUM
	zfs_pool     ONLINE       0     0     0
	  mirror-0   ONLINE       0     0     0
	    nvme0n1  ONLINE       0     0     0
	    nvme2n1  ONLINE       0     0     0

errors: No known data errors
$
```

Lovely. Let's see if we actually have the volume:

```bash
$ zfs list
NAME       USED  AVAIL  REFER  MOUNTPOINT
zfs_pool   144K  3.51T    24K  /zfs_pool
$
```

Change the mount point to `/mnt/zfs_pool`:

```bash
sudo zfs set mountpoint=/mnt/zfs_pool zfs_pool
```

...and verify:

```bash
$ sudo zfs get mountpoint zfs_pool
NAME      PROPERTY    VALUE          SOURCE
zfs_pool  mountpoint  /mnt/zfs_pool  local
$
```

Excellent. Now we can create the ZFS volume.

