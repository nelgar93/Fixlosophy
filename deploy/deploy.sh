#!/usr/bin/env bash
#
# Deploy: back up, build, swap, prove it came back.
#
# Run on the VPS. Takes no arguments; deploys whatever is on the tracked branch.
#
# The backup-first step is not belt and braces — it is the project's whole answer to
# not having EF Core migrations. The schema is built by idempotent DDL in EnsureSchema,
# which can add things but cannot undo them, so "restore last night's dump" is the
# rollback. Taking a fresh one immediately before every deploy is what keeps that
# rollback worth having.
#
# Stop-then-start rather than a rolling swap, deliberately. The app is single-instance
# by design: Blazor Server holds per-circuit state, and NotificationHub is an
# in-process fan-out. Two instances briefly overlapping is not an optimisation here,
# it is a bug — and the schema advisory lock exists precisely because that overlap
# used to be possible.

set -Eeuo pipefail

APP_DIR="${APP_DIR:-/srv/fixlosophy}"
SRC_DIR="${SRC_DIR:-/srv/fixlosophy-src}"
SERVICE="${SERVICE:-fixlosophy}"
HEALTH_URL="${HEALTH_URL:-http://127.0.0.1:5000/health}"
HEALTH_TIMEOUT="${HEALTH_TIMEOUT:-90}"
BRANCH="${BRANCH:-main}"

log()  { printf '\n=== %s\n' "$*"; }
fail() { printf '\nDEPLOY FAILED: %s\n' "$*" >&2; exit 1; }

log "Backing up first"
/usr/local/bin/fixlosophy-backup || fail "backup failed — not deploying on top of an unprotected database"

log "Fetching $BRANCH"
git -C "$SRC_DIR" fetch --prune origin
git -C "$SRC_DIR" checkout "$BRANCH"
git -C "$SRC_DIR" reset --hard "origin/$BRANCH"
REV="$(git -C "$SRC_DIR" rev-parse --short HEAD)"
log "At $REV"

log "Testing"
# If the suite is red, the problem is better found here than after the restart.
dotnet test "$SRC_DIR/Fixlosophy.Tests/Fixlosophy.Tests.csproj" -c Release --nologo \
    || fail "tests are failing at $REV"

log "Publishing"
STAGING="$(mktemp -d /srv/.fixlosophy-publish-XXXXXX)"
trap 'rm -rf -- "$STAGING"' EXIT
dotnet publish "$SRC_DIR/Fixlosophy.csproj" -c Release -o "$STAGING" --nologo \
    || fail "publish failed"

# Publish into a staging directory and swap, so a failed build never leaves the live
# directory half-written. The service is down only for the swap and the restart.
log "Stopping $SERVICE"
systemctl stop "$SERVICE"

log "Swapping in the new build"
PREVIOUS="${APP_DIR}.previous"
rm -rf -- "$PREVIOUS"
[[ -d "$APP_DIR" ]] && mv -- "$APP_DIR" "$PREVIOUS"
mv -- "$STAGING" "$APP_DIR"
trap - EXIT
chown -R fixlosophy:fixlosophy "$APP_DIR"

log "Starting $SERVICE"
systemctl start "$SERVICE"

log "Waiting for /health"
# This is what the health endpoint is for. systemctl start returns as soon as the
# process exists, which is well before it can serve — and an instance that booted but
# cannot reach Supabase looks identical to a healthy one from the outside.
deadline=$(( SECONDS + HEALTH_TIMEOUT ))
until curl -fsS -m 5 "$HEALTH_URL" >/dev/null 2>&1; do
    if (( SECONDS >= deadline )); then
        echo
        echo "Did not become healthy within ${HEALTH_TIMEOUT}s. Last 40 log lines:"
        journalctl -u "$SERVICE" -n 40 --no-pager
        echo
        echo "Rolling back to the previous build."
        systemctl stop "$SERVICE"
        rm -rf -- "$APP_DIR"
        mv -- "$PREVIOUS" "$APP_DIR"
        systemctl start "$SERVICE"
        fail "rolled back to the previous build — $REV did not come up"
    fi
    sleep 2
done

log "Healthy. Deployed $REV."
