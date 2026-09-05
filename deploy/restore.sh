#!/usr/bin/env bash
#
# Restore a backup — and, more importantly, the thing you run to find out whether the
# backups are real.
#
#   ./restore.sh <encrypted-dump> <target-postgres-url> [--force]
#
# Run this ON YOUR OWN MACHINE, not the VPS: the age private key lives with you, which
# is what stops a compromised server also handing over every backup it ever made.
#
# An untested backup is a guess. Do this once before launch against a scratch database,
# then once every few months. The failure mode you are looking for is not "the file is
# missing" — it is "the file is there and does not restore", which you can only find
# out by trying.

set -Eeuo pipefail

DUMP="${1:-}"
TARGET="${2:-}"
FORCE="${3:-}"

if [[ -z "$DUMP" || -z "$TARGET" ]]; then
    cat >&2 <<'USAGE'
Usage: ./restore.sh <encrypted-dump> <target-postgres-url> [--force]

  <encrypted-dump>       a *.dump.age file pulled down from the VPS or the remote
  <target-postgres-url>  postgres://... — a SCRATCH database, see below
  --force                required if the target is not obviously a scratch database

Drill (do this before launch):
  docker run -d --name pgdrill -e POSTGRES_PASSWORD=drill -p 55432:5432 postgres:16
  ./restore.sh fixlosophy-20260905-030000Z.dump.age \
      postgres://postgres:drill@localhost:55432/postgres
  # then point the app at it and check the bookings are there
  docker rm -f pgdrill
USAGE
    exit 64
fi

[[ -r "$DUMP" ]] || { echo "Cannot read $DUMP" >&2; exit 66; }

# Restoring over the live database is almost never what you mean, and when it is, you
# want to have said so out loud. This is a guess, not a guarantee — hence --force.
if [[ "$TARGET" == *"supabase"* || "$TARGET" == *"pooler"* ]] && [[ "$FORCE" != "--force" ]]; then
    cat >&2 <<'WARN'
REFUSING: the target looks like the live Supabase database.

Restoring into it will overwrite live data. If that is genuinely what you want —
you are recovering from a real loss — re-run with --force as the third argument.
For a drill, point at a scratch database instead (see the usage text).
WARN
    exit 1
fi

echo "Decrypting $DUMP…"
PLAIN="$(mktemp -t fixlosophy-restore-XXXXXX.dump)"
trap 'rm -f -- "$PLAIN"' EXIT

# Prompts for the identity file if AGE_IDENTITY isn't set.
age --decrypt ${AGE_IDENTITY:+--identity "$AGE_IDENTITY"} --output "$PLAIN" -- "$DUMP"
echo "Decrypted $(stat -c%s "$PLAIN") bytes."

echo "Restoring into $TARGET…"
# --clean --if-exists so a repeated drill into the same scratch database works.
# --no-owner / --no-privileges to match how the dump was taken.
# NOT --exit-on-error: a fresh target legitimately reports "role does not exist" style
# notices, and stopping on the first one hides whether the data itself restored.
pg_restore \
    --dbname "$TARGET" \
    --clean --if-exists \
    --no-owner --no-privileges \
    --verbose \
    "$PLAIN" 2>&1 | tail -20

echo
echo "Restored. Now check it actually holds what you expect:"
echo
psql "$TARGET" -c 'SELECT
    (SELECT count(*) FROM "Bookings")       AS bookings,
    (SELECT count(*) FROM "Customers")      AS customers,
    (SELECT count(*) FROM "ServicePricings") AS services,
    (SELECT max("CreatedAt") FROM "Bookings") AS newest_booking;'
echo
echo "If newest_booking is roughly when the backup was taken, the backup is real."
