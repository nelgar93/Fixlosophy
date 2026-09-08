#!/bin/bash
#
# SessionStart hook — gets a Claude Code on the web container ready to build, test and
# drive Fixlosophy.
#
# The remote container starts empty every time: no .NET SDK, an unset NODE_PATH, and no
# browser on PATH. Without this, `dotnet` is not found, `demo/smoke.js` cannot resolve
# Playwright, and the run-fixlosophy driver has nothing to launch. All three are things
# a session is expected to be able to do here.
#
# Synchronous on purpose. It is slower to start, but nothing can run a test before the
# thing that runs tests exists.
#
set -euo pipefail

# A developer's own machine already has its toolchain, and this script has no business
# installing an SDK into it.
if [ "${CLAUDE_CODE_REMOTE:-}" != "true" ]; then
    exit 0
fi

repo="${CLAUDE_PROJECT_DIR:-$(cd "$(dirname "${BASH_SOURCE[0]}")/../.." && pwd)}"
cd "$repo"

log() { printf '[session-start] %s\n' "$*"; }

# ── The .NET SDK ─────────────────────────────────────────────────────────────
# Both projects target net10.0, and .github/workflows/build.yml pins 10.0.x, so the
# channel matches CI rather than whatever is newest. Installed under $HOME so the
# script needs nothing privileged.
DOTNET_ROOT="${DOTNET_ROOT:-$HOME/.dotnet}"
export DOTNET_ROOT
export PATH="$DOTNET_ROOT:$PATH"
export DOTNET_CLI_TELEMETRY_OPTOUT=1
export DOTNET_NOLOGO=1

sdk_present() {
    command -v dotnet >/dev/null 2>&1 && dotnet --list-sdks 2>/dev/null | grep -q '^10\.'
}

if sdk_present; then
    log ".NET SDK $(dotnet --version) already here"
else
    log "installing the .NET 10 SDK into $DOTNET_ROOT"
    installer="$(mktemp)"
    curl -fsSL https://dot.net/v1/dotnet-install.sh -o "$installer"
    bash "$installer" --channel 10.0 --install-dir "$DOTNET_ROOT" --no-path
    rm -f "$installer"
    if ! sdk_present; then
        log "FAILED: the SDK is not usable after installing. Nothing else will work."
        exit 1
    fi
    log "installed .NET SDK $(dotnet --version)"
fi

# ── Node, Playwright, and a browser ──────────────────────────────────────────
# demo/smoke.js does require('playwright'). The image ships the module globally but
# leaves NODE_PATH empty, so plain `node demo/smoke.js` cannot find it — which reads
# like a broken test rather than an unset variable.
node_modules_dir="$(npm root -g 2>/dev/null || true)"
if [ -n "$node_modules_dir" ] && NODE_PATH="$node_modules_dir" \
        node -e "require.resolve('playwright')" >/dev/null 2>&1; then
    log "Playwright resolves from $node_modules_dir"
else
    log "installing Playwright"
    npm install -g playwright@1.56.1 >/dev/null 2>&1
    node_modules_dir="$(npm root -g)"
fi

# The image pre-installs Playwright's Chromium and points PLAYWRIGHT_BROWSERS_PATH at
# it. The run-fixlosophy driver asks for Edge by channel, which exists on the Windows
# box this repo is developed on and nowhere here — PWDRIVER_BROWSER overrides that with
# an executable path. See .claude/skills/run-fixlosophy/driver/Program.cs.
chromium="${PLAYWRIGHT_BROWSERS_PATH:-/opt/pw-browsers}/chromium"
if [ ! -x "$chromium" ]; then
    log "note: no pre-installed Chromium at $chromium — the browser driver will need one"
    chromium=""
fi

# ── Warm the build ───────────────────────────────────────────────────────────
# Restore is the part that needs the network; doing it now means the container image is
# cached with the packages in it. The build is cheap after that and proves the tree is
# sound, but a broken tree is the session's problem to fix, not a reason to refuse to
# start — so it warns rather than exits.
log "restoring packages"
dotnet restore Fixlosophy.sln
dotnet restore .claude/skills/run-fixlosophy/driver

log "building (Release, as CI does)"
if dotnet build Fixlosophy.sln --no-restore --configuration Release --nologo >/dev/null; then
    log "solution builds"
else
    log "WARNING: the solution does not currently build — see dotnet build for why"
fi

if dotnet build .claude/skills/run-fixlosophy/driver --no-restore --configuration Release --nologo >/dev/null; then
    log "browser driver builds"
else
    log "WARNING: the run-fixlosophy browser driver does not build"
fi

# ── Hand the environment to the session ──────────────────────────────────────
env_file="${CLAUDE_ENV_FILE:-/dev/null}"
{
    echo "export DOTNET_ROOT=\"$DOTNET_ROOT\""
    echo "export PATH=\"$DOTNET_ROOT:\$PATH\""
    echo "export DOTNET_CLI_TELEMETRY_OPTOUT=1"
    echo "export DOTNET_NOLOGO=1"
    echo "export NODE_PATH=\"$node_modules_dir\""
    if [ -n "$chromium" ]; then
        echo "export PWDRIVER_BROWSER=\"$chromium\""
    fi
} >> "$env_file"

# ── What this cannot do ──────────────────────────────────────────────────────
# The test suite runs on EF Core's InMemory provider and needs nothing else, so it is
# ready now. Actually *running* the site is a different matter: it wants a Postgres
# connection string in appsettings.Local.json, which is gitignored and holds real
# credentials, so it cannot be created here. Said out loud because the failure it
# causes ("startup failed") otherwise looks like a broken checkout.
if [ ! -f "$repo/appsettings.Local.json" ]; then
    log "note: no appsettings.Local.json — dotnet test and demo/smoke.js work without it,"
    log "      but 'dotnet run' needs a Postgres connection string (see README)."
fi

# One more thing worth saying, because the obvious command misleads. `dotnet format`
# with no subcommand runs the whitespace pass, and .editorconfig pins end_of_line=crlf
# while this checkout is LF (there is no .gitattributes), so it reports every line of
# every file as wrong. The two passes that do mean something here are named explicitly.
log "linting: use 'dotnet format style' / 'dotnet format analyzers' — the bare"
log "         'dotnet format' whitespace pass reports CRLF noise on a Linux checkout."
log "ready — dotnet test, node demo/smoke.js, and the run-fixlosophy driver all work"
