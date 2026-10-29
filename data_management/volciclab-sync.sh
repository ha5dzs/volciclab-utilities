#!/usr/bin/env bash
#
# volciclab-sync.sh -- one-way nightly sync of the volciclab lab data share.
#
# WHAT IT DOES
#   Keeps up-to-date copies of the server's main data share (see sync_setup.md
#   for the design and reasoning). Two profiles:
#
#     nas    rolling mirror on the lab NAS.
#            Deletions made on the source propagate to the NAS clone
#            (that is intended: it is a rolling mirror), but protected:
#            an empty source tree is refused, and per-run deletion volume is
#            capped (see --max-delete below).
#
#     nyuad  append-only dump on the off-site NYUAD share.
#            Deletions made on the source NEVER propagate here.
#            This profile must never be given --delete.
#
#   Direction is ALWAYS   source -> destination.
#   This script never reads data from a destination and never writes
#   to the source. Do not add a reverse sync here.
#
# USAGE
#   volciclab-sync.sh nas      run the NAS mirror sync
#   volciclab-sync.sh nyuad    run the NYUAD dump sync
#
# OUTPUTS
#   log/                 one log file per run:  YYYY-MM-DD_<profile>.log
#   state/               per-profile last-run state (status, timing, counts)
#   SYNC_STATUS.txt      human-readable status, in the web document root,
#                        browsable at  http://<server>/SYNC_STATUS.txt
#
# EXITS
#   0      sync completed cleanly
#   2      bad usage
#   other  the sync failed or was skipped (reason is in the status file/log)
#
set -u
export PATH=/usr/local/sbin:/usr/local/bin:/usr/sbin:/usr/bin:/sbin:/bin

# ------------------------------------------------------------ configuration
# If a share ever gets mounted at a different path, only this block has to
# be changed.

SRC="/mnt/zfs_pool/volciclab-storage/"        # the shared lab data (READ-ONLY for this script)
SRC_MP="/mnt/zfs_pool"                        # mountpoint the source must be up on
STATUS_FILE="/mnt/zfs_pool/volciclab-storage/website/SYNC_STATUS.txt"
BASE="$(cd "$(dirname "${BASH_SOURCE[0]}")" && pwd)"

case "${1:-}" in
  nas)
    DEST="/mnt/volciclab-nas-16tb/volciclab-storage-rsync-clone/"
    DEST_MP="/mnt/volciclab-nas-16tb"
    # Flag notes for the curious (or for future you):
    #   -r   recurse
    #   -t   keep timestamps on transferred files. With --checksum the skip
    #        decision does not rely on time, so this is a mirroring nicety,
    #        not a correctness dependency (see --checksum below).
    #   -l   preserve symlinks as links, never follow them: dangling ones are
    #        copied without error, healthy ones keep their meaning instead of
    #        being copied as their (potentially huge) content
    #   --delete           (nas profile only) makes the destination a real
    #                      mirror: deletions propagate. Intended.
    #   --max-delete=NNN   safety valve: abort the run instead of deleting
    #                      more than NNN files in one go (protects the clone
    #                      from a wiped / miss-mounted / typo'd source)
    #   --partial          keep partial transfers; an interrupted run resumes
    #   --stats            file/byte summary, parsed into the status file
    #   --checksum         (nas profile only) judge "unchanged" by content
    #                      hash instead of size+mtime. WHY: the NAS
    #                      (UGREEN/UGOS) does not reliably keep the mtime of
    #                      files written to it over SMB, so the plain quick
    #                      check is not trustworthy and kept re-copying the
    #                      whole tree every run (~8 h; ~300 GB / ~790k files).
    #                      A content hash depends on none of that: identical
    #                      content -> skip, whatever time the NAS reports.
    #                      Cost: every run reads + hashes the whole tree on
    #                      both sides (no cheap steady state) -- the price of
    #                      a no-blind-spot detector. NOT used on nyuad: that
    #                      share keeps mtime correctly and must stay a cheap
    #                      diff sync.
    # Deliberately absent: -o -g -p -A -X -H -- the share is 777-ish and
    # the destinations are CIFS mounts, so ownership/permissions/ACLs/xattrs
    # are not preserved here by design (and skipping them keeps per-file
    # CIFS traffic down, which matters with ~1M files).
    SYNC_OPTS=(-rtl --delete --max-delete=2000000 --partial --stats --checksum)
    GUARDED=1
    ;;
  nyuad)
    DEST="/mnt/nyuad-file-share/volciclab-storage/"
    DEST_MP="/mnt/nyuad-file-share"
    SYNC_OPTS=(-rtl --partial --stats)
    GUARDED=0
    ;;
  *)
    echo "usage: volciclab-sync.sh {nas|nyuad}" >&2
    exit 2
    ;;
esac
PROFILE="$1"

LOG_DIR="$BASE/log"
STATE_DIR="$BASE/state"
mkdir -p "$LOG_DIR" "$STATE_DIR" || { echo "FATAL: cannot create $LOG_DIR and $STATE_DIR" >&2; exit 1; }

# One log file per run (kept in log/). A plain '| tee' pipeline is used
# instead of a process substitution ('>(tee)') on purpose: with the pipeline,
# the caller cannot get control back until tee has flushed everything, so a
# log/status check right after exit can never see a half-finished file.
RUN_LOG="$LOG_DIR/$(date +%F)_${PROFILE}.log"

{
# ---------------------------------------------------------------- helpers --

ts() { date '+%F %T'; }

fmt_dur() {
  local s=$1
  if [ "$s" -ge 3600 ]; then
    printf '%dh %dm %ds' "$((s/3600))" "$(((s%3600)/60))" "$((s%60))"
  else
    printf '%dm %ds' "$(((s%3600)/60))" "$((s%60))"
  fi
}

human_bytes() {
  local n="${1//,/}"
  case "$n" in
    ''|*[!0-9]*) printf '%s' "${1:-?}" ;;
    *)           numfmt --to=iec --suffix=B "$n" 2>/dev/null || printf '%s' "$1" ;;
  esac
}

# write_state STATUS NOTE STARTED FINISHED DURATION FILES BYTES
#   Writes this profile's state file and re-renders the shared
#   browser-visible status file (atomic: write to temp, chmod, rename over).
write_state() {
  local sf="$STATE_DIR/$PROFILE"
  {
    echo "status=$1"
    echo "note=$2"
    echo "started=$3"
    echo "finished=$4"
    echo "duration=$5"
    echo "files=$6"
    echo "bytes=$7"
  } > "$sf"

  local tmp="${STATUS_FILE}.tmp" p pf k v
  mkdir -p "$(dirname "$STATUS_FILE")" 2>/dev/null || true
  {
    echo "# Volciclab storage sync -- status"
    echo "# Generated by volciclab-sync.sh. Do not edit by hand."
    echo
    for p in nas nyuad; do
      if [ "$p" = "nas" ]; then
        echo "## nas    rolling mirror (deletions DO propagate to the NAS clone)"
      else
        echo "## nyuad  append-only off-site dump (deletions NEVER propagate here)"
      fi
      pf="$STATE_DIR/$p"
      if [ -f "$pf" ]; then
        while IFS='=' read -r k v; do
          [ -n "$k" ] && printf '   %-10s%s\n' "$k:" "$v"
        done < "$pf"
      else
        echo "   status:     not yet run"
        if [ "$p" = "nyuad" ]; then
          echo "   note:       enable (initial-nyuad-copy.sh + cron) once the initial copy is done"
        fi
      fi
      echo
    done
  } > "$tmp"
  chmod 644 "$tmp"
  mv -f "$tmp" "$STATUS_FILE"
}

# finish STATUS NOTE [FILES] [BYTES]
finish() {
  local now
  now="$(date +%s)"
  write_state "$1" "$2" "$START_TS" "$(ts)" "$(fmt_dur "$((now - START_EPOCH))")" "${3:-?}" "${4:-?}"
}

# ------------------------------------------------------------- the run ----

START_EPOCH="$(date +%s)"
START_TS="$(date '+%F %T')"
echo "== volciclab-sync: profile=$PROFILE   start=$START_TS"
echo "   source:  $SRC"
echo "   target:  $DEST"
echo

# Per-profile lock: a second run of the SAME profile can never overlap the
# first. nas and nyuad use separate locks and never block each other.
exec 9>"$STATE_DIR/.lock-$PROFILE"
if ! flock -n 9; then
  echo "another '$PROFILE' sync is already running; exiting without touching anything."
  exit 0
fi

# ------------------------------------------------------------- pre-flight --
# These are network/CIFS mounts: they can drop. Refusing to run blind is the
# point -- "sync to an empty/dropped mount" is how backups die.

if ! mountpoint -q "$SRC_MP"; then
  echo "FATAL: source mount is down:  $SRC_MP"
  echo "       fix the mount (sudo mount -a), then re-run; nothing was touched."
  finish "SKIPPED" "source mount missing: $SRC_MP"
  exit 1
fi

if ! mountpoint -q "$DEST_MP"; then
  echo "FATAL: destination mount is down:  $DEST_MP"
  echo "       the share is not reachable; not syncing to a dropped mount."
  finish "SKIPPED" "destination mount missing: $DEST_MP"
  exit 1
fi

if [ ! -d "${SRC%/}" ]; then
  echo "FATAL: source directory is missing:  ${SRC%/}"
  finish "SKIPPED" "source directory missing: ${SRC%/}"
  exit 1
fi

# Mirror-profile guard: never run --delete against a source that looks empty.
# That is the classic rsync horror: a bad mount, a typo, or a wiped share --
# and --delete would quietly empty the backup.
if [ "$GUARDED" = 1 ]; then
  first="$(find "${SRC%/}" -mindepth 1 -maxdepth 1 -print -quit 2>/dev/null)" || first=""
  if [ -z "$first" ]; then
    echo "FATAL: source tree is EMPTY -- refusing to run with --delete."
    echo "       The NAS clone was left untouched. Investigate the source first."
    finish "SKIPPED" "source tree empty -- refused to run with --delete"
    exit 1
  fi
fi

if ! mkdir -p "${DEST%/}"; then
  echo "FATAL: destination directory not accessible:  $DEST"
  finish "SKIPPED" "destination directory not accessible"
  exit 1
fi

# ---------------------------------------------------------------- the sync --

echo "\$ rsync ${SYNC_OPTS[*]} $SRC $DEST"
echo
out="$(rsync "${SYNC_OPTS[@]}" "$SRC" "$DEST" 2>&1)" && rc=0 || rc=$?
printf '%s\n' "$out"
echo
echo "rsync exit code: $rc"

files="$(printf '%s\n' "$out" | sed -n 's/^Number of regular files transferred:[[:space:]]*//p' | head -n1)"
bytesraw="$(printf '%s\n' "$out" | sed -n 's/^Total transferred file size:[[:space:]]*//p' | head -n1)"
bytesraw="${bytesraw% bytes}"
[ -n "$files" ] || files="?"
bytes="$(human_bytes "$bytesraw")"

if [ "$rc" -eq 0 ]; then
  echo "RESULT: OK  (duration: $(fmt_dur "$(( $(date +%s) - START_EPOCH ))"))"
  finish "OK" "-" "$files" "$bytes"
  exit 0
fi

echo "RESULT: FAILED  (rsync exit $rc) -- see log above; re-running the script continues/repairs."
case "$rc" in
  23) echo "    exit 23: some files could not be transferred (often a transient network problem)." ;;
  24) echo "    exit 24: files vanished before transfer could complete; re-run, and if it repeats, investigate." ;;
  12) echo "    exit 12: I/O or other transfer error; check the log and the mounts." ;;
  25) echo "    exit 25: the --max-delete safety valve tripped; deletions were capped."
     echo "             The destination tree may be incomplete -- investigate before re-running." ;;
  *)  echo "    (see rsync(1) for exit code $rc)" ;;
esac
finish "FAILED" "rsync exit $rc" "$files" "$bytes"
exit "$rc"

} 2>&1 | tee "$RUN_LOG"
rc=${PIPESTATUS[0]}
exit "$rc"
