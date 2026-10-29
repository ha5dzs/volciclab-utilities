# Nightly sync: keeping duplicate copies of the volciclab storage

Companion document to [`server_setup.md`](server_setup.md). That document is the from-scratch disaster-reconstruction guide; this one covers the nightly sync that keeps duplicate copies of the lab data, and how to verify, enable, and (in case of trouble) understand it. Since this is a public document, NYUAD internal network intricacies are anonymised.

## Why this exists

The lab's main data lives on the `zfs_pool` mirror and is shared via Samba to all workstations. The mirror protects against a single disk failing, but not against:

* the whole server failing (SSD controller, power, OS bricked, ...),
* an accidental `rm -rf` on the server (by intentional sabotage, or by a script),
* something going terribly wrong only on the server.

So the data is copied to two more places, nightly, at unsociable hours:

| Profile | Destination                                | Deletes there?  | Purpose                                            |
| ------- | ------------------------------------------ | --------------- | -------------------------------------------------- |
| `nas`   | `//192.168.42.16/volciclab-nas-16tb` | **yes** (guarded) | Rolling mirror. Fast local recovery; the lab's everyday safety net. |
| `nyuad` | `//zoltan_is_awesome.abudhabi.nyu.edu/volciclab` | **never** | Append-only off-site dump. This is the "the whole building burns down" layer, and because deletions never reach it, it is also the accidental-deletion backstop. |

Direction is **always one-way**: server → copy. A copy never writes back to the server, and the two copies never overwrite each other. **This is the single most important invariant of the whole thing.**

## Why this shape (design decisions)

* **rsync over the existing CIFS mounts, run on the server.**
Both destinations are already mounted at boot (`/etc/fstab`), so nothing has to be installed or started *on* the NAS or the campus box, and the transport already used by Samba is the one that has been observed to keep the NAS alive (heavy CIFS traffic is where the UGOS box gets nervous; it ony has 8 GB of RAM). No rsync daemon on the NAS to be set up, no SSH keys to manage there.
* **Plain folders, not snapshots or a restore-able-elsewhere backup format.**
The colleagues on Windows and Mac must be able to browse the share without Linux knowledge. A nightly rsync mirror is just, well files. No database to be corrupted or hacked, no proprietary formats, just _the stuff_. If the server is ever down, the data is directly browsable on the NAS.
* The **`nas` is a rolling mirror (`--delete`), `nyuad` is append-only (no `--delete`), by explicit design.**
If you delete something on the server: it is gone (moved to its own 'Recycle bin') from the NAS mirror (by design — it is a *mirror*), but it is still on the NYUAD dump (by design — it is a *dump*), and it definitely is on the portable "doomsday cold storage" drive that gets made per project / every six months - if the file is that old, that is.

## What gets where

```
server:  /mnt/zfs_pool/volciclab-storage/          (source, the shared Samba data — READ-ONLY for the sync)
          |
          |  nas profile:   rsync -rtl --delete --checksum --max-delete=2000000 --partial --stats
          v
         /mnt/volciclab-nas-16tb/volciclab-storage-rsync-clone/
          (NAS, 2x16 TB RAID1. Seeded once by hand; nightly mirror since.)

server:  /mnt/zfs_pool/volciclab-storage/
          |
          |  nyuad profile: rsync -rtl --partial --stats        (no --delete, ever)
          v
         /mnt/nyuad-file-share/volciclab-storage/
          (NYUAD off-site share. Seeded with initial-nyuad-copy.sh; nightly dump once enabled.)
```

The sync script is designed to be copied to `~/volciclab-sync/*`.

There are status and log files created, and stored all on the server itself:

```
~/volciclab-sync/volciclab-sync.sh        the sync script, included in this directory
~/volciclab-sync/log/                     one log per run (YYYY-MM-DD_<profile>.log)
~/volciclab-sync/state/                   last-run state per profile (used by the status file + the seeder)

/mnt/zfs_pool/volciclab-storage/website/SYNC_STATUS.txt
                                         the browser-visible status (http://192.168.42.6/SYNC_STATUS.txt)
```

## Installing it (from a fresh server rebuild)

1. Put the  script into the sync home directory:

   ```bash
   sudo mkdir -p ~/volciclab-sync
   # (copy volciclab-sync.sh into it, e.g. with mc)
   sudo chmod +x ~/volciclab-sync/volciclab-sync.sh
   ls -al ~/volciclab-sync
   ```

2. Verify the prerequisites are in place (all from `server_setup.md`):
   `rsync` is installed, the NAS 16 TB share is mounted at `/mnt/volciclab-nas-16tb`, the NYUAD share is mounted at `/mnt/nyuad-file-share`, and the Samba share path `/mnt/zfs_pool/volciclab-storage/` exists and is not empty.

   ```bash
   command -v rsync
   mountpoint /mnt/volciclab-nas-16tb
   mountpoint /mnt/nyuad-file-share
   ls /mnt/zfs_pool/volciclab-storage/ | head
   ```

3. Do a **manual** run of the NAS profile and watch it:

   ```bash
   bash ~/volciclab-sync/volciclab-sync.sh nas
   echo "exit: $?"
   cat ~/volciclab-sync/log/$(date +%F)_nas.log
   ```

   Expected: exit `0`, a `RESULT: OK` line, and something like

   ```
   Number of regular files transferred: <a small number>
   ```

   (small, because the NAS clone is already caught up). Then open
   `http://192.168.42.6/SYNC_STATUS.txt` and check it says `status: OK` for `nas`.

4. A manual run of the NYUAD profile too — it *copies*, it must never delete:
(this is going to take a while, the initial copy took 17 hours - NYUAD IT are 'working' on improving it.)

   ```bash
   bash ~/volciclab-sync/volciclab-sync.sh nyuad
   ```

## Scheduling it (cron)

Cron is the scheduling choice, because it's simple and the script already guards against double-runs (per-profile lock) and against running against a dropped mount (it checks first and skips otherwise).

```bash
crontab -e
```

Add these two lines (the NYUAD one stays commented out until the initial copy is finished — see the next section):

```cron
# nightly lab-data sync: 01:00 rolling mirror to the NAS, 03:00 dump to NYUAD
0 1 * * * /home/volciclab/volciclab-sync/volciclab-sync.sh nas >/dev/null 2>&1
#0 3 * * * /home/volciclab/volciclab-sync/volciclab-sync.sh nyuad >/dev/null 2>&1
```

Verify with:

```bash
crontab -l
```

During some idle time, ssh into the server and run `/home/volciclab/volciclab-sync/volciclab-sync.sh nyuad`. Check status, and if all OK, remove the comment `#` from the second line in `crontab`.

## Reading the status

`SYNC_STATUS.txt` (browser) is generated fresh on every run and looks like:

```
# Volciclab storage sync -- status
# Generated by volciclab-sync.sh. Do not edit by hand.

## nas    rolling mirror (deletions DO propagate to the NAS clone)
   status:   OK
   note:     -
   started:  2026-10-29 01:00:01
   finished: 2026-10-29 02:11:41
   duration: 1h 11m 40s
   files:    1,892
   bytes:    3.1GB

## nyuad  append-only off-site dump (deletions NEVER propagate here)
   status:   OK
   note:     -
   started:  2026-10-29 03:00:01
   finished: 2026-10-29 03:29:33
   duration: 29m 32s
   files:    1,892
   bytes:    3.1GB

```

What could the `status` be:

* `OK`
Finished cleanly.
* `SKIPPED`
A pre-flight check stopped it from running (a mount was down, the source looked empty — it refused rather than risk the mirror).
* `FAILED`
This is trouble: rsync hit an error; the log named in the run above has the detail.

Per-run logs are in `~/volciclab-sync/log/`, one file per invocation, and are the place to look first when something is not `OK`.

## Safety invariants — read before editing anything

* **One-way only.**
No reverse sync, ever. The scripts never read from a destination and never write to the source.
* **`--delete` must stay out of the `nyuad` profile.**
That line must never gain `--delete`. The whole point of that share is that deletions cannot reach it. And to emphasise its 'dump' nature.
* **The `nas` destination must stay a dedicated clone directory**
It is not accessible from other computers without explicitly mounting it, and should not be a directory other people use daily — `--delete` would apply to everything in it.
* **The guards are the guards.**
In the `nas` branch: refuse to run when the source tree is empty, and cap per-run deletions (`--max-delete`) are two independent protections against a wiped or miss-mounted source. If you  raise `--max-delete`, note that it currently sits at 200k, which is roughly today's whole tree — plenty of headroom for a day of churn, no headroom for a mistake.
* **The per-profile lock keeps runs from overlapping.**
Don't replace `flock` with a "just hope so" without also keeping `--partial` and the  mount checks.

## Tuning knobs

* `--max-delete=NNN`
This is only in the `nas` branch — deletion cap per run.
* Cron times (01:00 / 03:00)
There is a very little probability of anyone working in the lab at that time, and NYUAD network will also be similarly less busy, so it is least likely to be throttled. Not that the NAS cares, as long as it has enough free RAM.
* Log retention
The log directory grows one file per run per profile, which will eventually fill the system drive. This is not implemented, but for future reference, crontab may have a `find ~/volciclab-sync/log -name '*.log' -mtime +90 -delete` line.

## Additional quirks

Add quirks as they occur. Since we are pretty much in the directory tree and probably nobody will ever read it, this is a good space for story time with the occasional rant.

### The NAS, when mounted on Linux, does not respect file times.

This was a difficult one to trace.

In the olden days, when green-on-black CRTs were all the rage, and people quietly prayed for data integrity every time they saved stuff on a 5.25" floppy, the file version logging was simple: you had the file creation date, the file
modification date, and the file access date. These were local to your computer's clock, which you had to set manually. When you copy a file, you effectively create a _facsimile_ of it, so this information is preserved. But then, as the accessibility of computer networks started trickling down to mere mortals like us, it became a different matter. When you 'send' a file over the network, you don't just make a copy of it, but as far as the other party is concerned, it is a new file that is created, because it is a transmission that was received over the wire - with 'now' being the creation date. So now you have two identical copies, with two different file creation dates, both of them being valid, which effectively nullifies its credibility. This is the very reason the file dates should not be used for file creation history.

This issue, which is purely a convention-based issue, is still not resolved today, and setting up rsync here is a very good example of it manifesting: when copying a file from a Linux computer to the NAS, the creation date is set to 'now'. When copying a file from a Windows computer to the NAS, the file creation date is preserved. Why? (well actually I can't be sure, it can totally be a bug that is not discovered yet, and it's annoying but a plausible explanation is...) Because Windows handles a network share as a physical drive (i.e. 'floppy'), whereas Unix-based systems handle it as a network service that happens to be mounted onto the directory tree. And a unix-based system (the operating system on the NAS: UGOS, which is based on Debian 12) will respect this convention, whereas a Windows-based server (on the NYUAD file share) will not. This is because Windows follows the old-school DOS convention of having local time, whereas Unix systems will follow UTC time, and displays local time with an offset as it is set, which can be different user and system-wide. And it has to be done this way, because now you can connect two computers on the opposite sides of the planet, with totally different local times. But UTC will be the same. Microsecond precision. Windows computers, even today, set the system time to the local time zone and daylight saving time.

But interestingly, the file time and date modifications **DO** persist when manually set. When copied, they do not. I don't know why.

Normally nobody would care (or, if aware, can be blissfully ignorant of it) about this, but when you have to automate the live redundant backup of hundreds of thousands of files taking up several hundred gigabytes of space, this causes problems.

Why? Because rsync, by default, checks if a file is the _same_ file by inspecting its size _and_ creation date. If any of these two are different at the source (i.e. on 'Drive X', which we have the unhealthy habit of writing to them using Windows), the destination (i.e. the NAS) is deleted with extreme prejudice, and then copied over again. This forces the NAS to move the deleted files to the recycle bin, effectively working double-time: it has to move a file on the drive, and also write the same file as it received over the network to the same place. If there is something a mechanical hard drive hates to do, is this: take a tiny piece of data from one location of the disk, and write it to a slightly different location of the same disk. It is slow: you have to waste a few milliseconds to move the head on the platter, and then you have to wait another few milliseconds for the appropriate sector to arrive under the head. Ridiculous. And these waiting times add up, resulting taking 8 hours for the sync: it literally copied over the entire directory tree twice, and it also wastes a lot of space, because the previous copy of the directory tree is moved to the recycle bin as well. So a work-around for this is to have rsync use a different method of validating the file - using a _checksum_: The checksum is a number created from the entire data of the file, using some sort of a standard algorithm. It can be used to detect errors in the file, but it cannot be used to locate and correct these errors. These algorithms can be as simple as parity (counting the number of '1' bits), cyclic redundancy code (CRC), or 'hash' algorithms, where the output is always a constant length, irrespective of how much data is being sent to it. The current implementation of  rsync (3.4.1) is using the 'md5' algorithm. In the documentation, there is a warning that while it is possible to rely on these, it is not recommended due to the computational intensity and network load: it reads the file from the source and the destination, computes `md5sum` twice, compares them, and then it decides whether to delete/copy or move on. As an added safeguard, a deleted file will not be deleted, it will move to the recycle bin on the NAS.

Well, in this case, where we have slow storage (in the NAS), but a fast network, and a fairly brutal amount of computational resources available, this 8-hour sync ordeal was reduced to 1...1.5 hours, and only the differences were copied. So, in this case switching over from 'rsync -rtl <source> <destination>' to 'rsync -rtl --checksum <source> <destination>' was a worthwhile trade-off.

Note that this work-around was only applied for the `nas` and not for the `nyuad` profile.
