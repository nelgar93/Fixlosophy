# Deploying Fixlosophy to a Hostinger VPS

Everything the server needs, in the order you'd do it. Written for a Hostinger KVM VPS
running Ubuntu, London region.

> **None of this has been run against a real VPS yet.** It's written from the app's
> actual requirements, but treat the first run as a dry run and expect to correct a
> path or two.

## What runs where

```
Caddy  :443  ──►  Kestrel  :5000  ──►  Supabase (eu-west-1)
                     │
                     ├── /var/lib/fixlosophy/keys   Data Protection key ring
                     └── /var/backups/fixlosophy    nightly dumps, encrypted
                                    │
                                    └──► Cloudflare R2 / Backblaze B2
```

One instance, deliberately. Blazor Server keeps state per circuit and `NotificationHub`
is an in-process fan-out, so scaling out would need sticky sessions and a SignalR
backplane. Deploys are stop-then-start for the same reason.

## 1. Server setup

```bash
adduser --system --group --home /srv/fixlosophy-home fixlosophy
mkdir -p /srv/fixlosophy /srv/fixlosophy-src /var/lib/fixlosophy/keys /etc/fixlosophy
chown -R fixlosophy:fixlosophy /srv/fixlosophy /var/lib/fixlosophy

# The key ring is the one thing here worth protecting properly — it decrypts every
# auth cookie the app has issued.
chmod 700 /var/lib/fixlosophy/keys

apt install -y dotnet-sdk-10.0 postgresql-client-16 age rclone caddy git
git clone <repo> /srv/fixlosophy-src
```

## 2. Application config

`/etc/fixlosophy/app.env`, root-owned, `chmod 600`. Environment variables rather than
`appsettings.Local.json` — nothing secret then lives in or near the repo checkout.
`__` is the separator for `:`.

```ini
ConnectionStrings__DefaultConnection=Host=aws-0-eu-west-1.pooler.supabase.com;Port=5432;...
AllowedHosts=fixlosophy.com;www.fixlosophy.com
App__BaseUrl=https://fixlosophy.com
DataProtection__KeyPath=/var/lib/fixlosophy/keys
SeedAdmin__Email=...
SeedAdmin__Password=...
Smtp__Host=...
Smtp__User=...
Smtp__Password=...
Smtp__From=hello@fixlosophy.com
Notifications__Email=...
Supabase__Url=https://<project>.supabase.co
Supabase__ServiceRoleKey=...
ForwardedHeaders__KnownProxies=127.0.0.1
```

**Four of these fail startup if missing**, on purpose: `AllowedHosts` (must not be
`*`), `SeedAdmin__Email`, `SeedAdmin__Password`, `DataProtection__KeyPath`. If the
first boot refuses to start, read the exception — it names the key.

## 3. Service

```bash
cp deploy/fixlosophy.service /etc/systemd/system/
systemctl daemon-reload && systemctl enable --now fixlosophy
curl -fsS http://127.0.0.1:5000/health   # expect: Healthy
```

## 4. Caddy

Caddy is suggested over nginx only because it gets and renews the TLS certificate with
no extra moving parts.

```caddyfile
fixlosophy.com, www.fixlosophy.com {
    encode gzip zstd
    reverse_proxy 127.0.0.1:5000
}
```

The app already runs `UseForwardedHeaders`, and Caddy sets `X-Forwarded-For` and
`X-Forwarded-Proto` by default. Getting this wrong is not cosmetic: without the real
client IP every per-IP rate limit collapses into one bucket shared by the whole
internet, and `Request.Scheme` reads `http`, which corrupts the absolute links in
verification and password-reset emails.

## 5. Backups

The database is the only thing here that can't be rebuilt from the repo.

**Generate the key pair on your own machine, and keep the private half there.**

```bash
age-keygen -o fixlosophy-backup.key     # keep this OFF the VPS — password manager
grep 'public key' fixlosophy-backup.key # this half goes on the server
```

That split is the point: backups a compromised VPS can also decrypt aren't much of a
backup, and it means restoring is deliberately something you do from your laptop.

`/etc/fixlosophy/backup.env` (root, `chmod 600`) — see `backup.env.example`. Then:

```bash
install -m 755 deploy/backup.sh /usr/local/bin/fixlosophy-backup
install -m 755 deploy/deploy.sh /usr/local/bin/fixlosophy-deploy
printf '15 3 * * * root /usr/local/bin/fixlosophy-backup >> /var/log/fixlosophy-backup.log 2>&1\n' \
    > /etc/cron.d/fixlosophy-backup
```

### Off-box copy

A backup that only exists on the machine being backed up is one dead disk from being no
backup at all. Cloudflare R2 and Backblaze B2 both have free tiers far larger than this
database will be for years.

```bash
rclone config    # create an "r2" remote
# then in backup.env:  RCLONE_REMOTE=r2:fixlosophy-backups
```

### Knowing when it stops

`HEALTHCHECK_URL` points at a free healthchecks.io check; the script pings it on
success and on failure.

This is a dead-man's switch rather than a "warn me if the file is old" script, and the
difference matters. A checker running on this box can only warn you while this box is
working — but the failure that actually loses your data is the one where cron, the
disk, or the whole VPS stopped, and then the checker is dead too and says nothing.
Pinging outward on success inverts it: **silence is the alarm.**

### Prove it works

Before launch, and every few months after:

```bash
docker run -d --name pgdrill -e POSTGRES_PASSWORD=drill -p 55432:5432 postgres:16
./deploy/restore.sh fixlosophy-20260905-030000Z.dump.age \
    postgres://postgres:drill@localhost:55432/postgres
docker rm -f pgdrill
```

An untested backup is a guess. The failure you're looking for isn't "the file is
missing" — it's "the file is there and doesn't restore", and trying is the only way to
find that out.

## 6. Deploying

```bash
fixlosophy-deploy
```

Backs up, fetches, runs the tests, publishes to a staging directory, swaps it in,
restarts, then polls `/health` — and **rolls back to the previous build** if it doesn't
come up within 90 seconds.

The backup-first step is the project's whole answer to not having EF Core migrations.
The schema is built by idempotent DDL in `EnsureSchema`, which can add but not undo, so
"restore last night's dump" *is* the rollback. Taking a fresh one immediately before
every deploy is what keeps that worth having.

## Reading the logs

```bash
journalctl -u fixlosophy -f            # live
journalctl -u fixlosophy -p err -n 50  # recent errors
```

Application errors also land in the `ErrorLog` table, grouped by fingerprint with a
count — read that in Supabase's table editor. There's no admin screen for it on
purpose. Note the two are complementary: if the database is what's broken, the table
can't tell you, and `journalctl` can.

```sql
select "LastSeen", "Count", "Logger", "MessageTemplate", "ExceptionType"
from "ErrorLog" order by "LastSeen" desc limit 20;
```

## Still to do before this is real

- Buy the domain, point DNS at the VPS.
- **SPF, DKIM and DMARC** on the sending domain, or confirmations go to spam — which
  breaks the booking flow while looking fine from the inside.
- `ufw` to 22/80/443, and SSH keys only.
- Unattended security upgrades.
