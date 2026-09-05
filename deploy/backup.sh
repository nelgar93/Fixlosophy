#!/usr/bin/env bash
#
# Nightly database backup: dump → encrypt → keep locally → copy off-box.
#
# Run from cron on the VPS. Everything it needs comes from /etc/fixlosophy/backup.env
# (see backup.env.example).
#
# Two design points worth knowing before changing anything here:
#
#   1. The dump is encrypted with a public key whose PRIVATE half is deliberately not
#      on this machine. Backups that a VPS compromise also hands over are not much of
#      a backup. Restoring is therefore something you do from your own laptop, on
#      purpose — see restore.sh.
#
#   2. Alerting is a dead-man's switch, not a "check if the file is old" script. A
#      checker that lives on the same box can only warn you while the box is working;
#      the failure that actually loses your data is the one where cron, the disk, or
#      the whole VPS stopped — and then the checker is dead too. Pinging out on
#      success inverts that: silence is the alarm.

set -Eeuo pipefail

CONFIG="${BACKUP_CONFIG:-/etc/fixlosophy/backup.env}"
# shellcheck source=/dev/null
[[ -r "$CONFIG" ]] && source "$CONFIG"

: "${PG_URL:?PG_URL is not set — see deploy/backup.env.example}"
: "${AGE_RECIPIENT:?AGE_RECIPIENT is not set — see deploy/backup.env.example}"
BACKUP_DIR="${BACKUP_DIR:-/var/backups/fixlosophy}"
KEEP_DAILY="${KEEP_DAILY:-7}"
KEEP_WEEKLY="${KEEP_WEEKLY:-4}"
RCLONE_REMOTE="${RCLONE_REMOTE:-}"
HEALTHCHECK_URL="${HEALTHCHECK_URL:-}"

STAMP="$(date -u +%Y%m%d-%H%M%SZ)"
DOW="$(date -u +%u)"          # 7 = Sunday, kept as the weekly
umask 077

log()  { printf '%s  %s\n' "$(date -u +%H:%M:%S)" "$*"; }
ping_healthcheck() {
    [[ -n "$HEALTHCHECK_URL" ]] || return 0
    curl -fsS -m 10 --retry 3 "${HEALTHCHECK_URL}$1" -o /dev/null || true
}

# Any failure past this point tells the dead-man's switch explicitly, so you hear
# about it within minutes instead of at the next missed daily ping.
trap 'ping_healthcheck /fail' ERR

ping_healthcheck /start

mkdir -p "$BACKUP_DIR/daily" "$BACKUP_DIR/weekly"

OUT="$BACKUP_DIR/daily/fixlosophy-$STAMP.dump.age"

log "Dumping database…"
# --format=custom is compressed and restores selectively, which plain SQL can't.
# --no-owner / --no-privileges because the roles on a restore target won't match
# Supabase's, and a restore that fails on GRANT statements is not a restore.
#
# NOTE for Supabase: this must be the SESSION pooler (port 5432) or the direct host,
# never the transaction pooler (6543) — pg_dump needs a session it can hold, and the
# transaction pooler will not give it one.
pg_dump "$PG_URL" \
    --format=custom \
    --no-owner \
    --no-privileges \
    --exclude-schema='pg_*' \
    --exclude-schema=information_schema \
  | age --recipient "$AGE_RECIPIENT" --output "$OUT"

# A dump that failed halfway can still leave a small, plausible-looking file. There is
# no honest lower bound for a general database, but this one always has the seeded
# price list in it, so anything under a few KB means something went wrong.
SIZE="$(stat -c%s "$OUT")"
if (( SIZE < 4096 )); then
    log "FAILED: dump is only ${SIZE} bytes — refusing to treat that as a backup."
    rm -f "$OUT"
    exit 1
fi
log "Wrote $OUT (${SIZE} bytes)"

# Sunday's copy is also kept as the weekly, so a problem you don't notice for a
# fortnight is still recoverable after the dailies have rotated away.
if [[ "$DOW" == "7" ]]; then
    cp -- "$OUT" "$BACKUP_DIR/weekly/"
    log "Kept a weekly copy."
fi

if [[ -n "$RCLONE_REMOTE" ]]; then
    log "Copying off-box to $RCLONE_REMOTE…"
    # The whole point: a backup that only exists on the machine being backed up is
    # one `rm -rf` or one dead disk from being no backup at all.
    rclone copy "$OUT" "$RCLONE_REMOTE/daily/" --no-traverse
    [[ "$DOW" == "7" ]] && rclone copy "$OUT" "$RCLONE_REMOTE/weekly/" --no-traverse
    log "Off-box copy done."
else
    log "WARNING: RCLONE_REMOTE is not set — this backup exists only on this machine."
fi

log "Rotating (keeping $KEEP_DAILY daily, $KEEP_WEEKLY weekly)…"
prune() {
    local dir="$1" keep="$2"
    # Newest first, skip the ones we're keeping, delete the rest.
    find "$dir" -maxdepth 1 -name 'fixlosophy-*.dump.age' -printf '%T@ %p\n' \
      | sort -rn | tail -n "+$((keep + 1))" | cut -d' ' -f2- \
      | while read -r old; do rm -f -- "$old"; log "  removed $(basename "$old")"; done
}
prune "$BACKUP_DIR/daily"  "$KEEP_DAILY"
prune "$BACKUP_DIR/weekly" "$KEEP_WEEKLY"

if [[ -n "$RCLONE_REMOTE" ]]; then
    # Remote rotation is by age rather than count — simpler, and the remote is the
    # copy you want to err on the side of keeping.
    rclone delete "$RCLONE_REMOTE/daily/"  --min-age "$((KEEP_DAILY * 24))h"  || true
    rclone delete "$RCLONE_REMOTE/weekly/" --min-age "$((KEEP_WEEKLY * 7 * 24))h" || true
fi

ping_healthcheck ""
log "Backup complete."
