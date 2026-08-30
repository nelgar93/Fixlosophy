---
name: run-fixlosophy
description: Build, run, screenshot, and drive the Fixlosophy Blazor Server bike-shop site. Use when asked to run or start the app, take a screenshot, check responsive/mobile layout, measure touch targets or overflow, drive the booking wizard, or verify a UI change in the real browser rather than in tests.
---

# Run and drive Fixlosophy

Blazor Server (.NET 10) + PostgreSQL (Supabase). Paths below are relative to the
repo root. Driven headlessly by a Playwright REPL at
`.claude/skills/run-fixlosophy/driver/` — it speaks to the **Edge already installed
on the machine**, so there is no Node dependency and no browser download.

## Prerequisites

The .NET 10 SDK, and a `appsettings.Local.json` at the repo root holding the Supabase
connection string (gitignored; see README). Nothing else — no `npm`, no
`playwright install`. Playwright's NuGet package ships its own driver binary, and the
driver launches Edge via `Channel = "msedge"`.

## Build

```bash
dotnet build Fixlosophy.sln -c Release --nologo
dotnet build .claude/skills/run-fixlosophy/driver -c Release --nologo
```

The driver is a separate project, deliberately not in `Fixlosophy.sln`. It lives under
`.claude/`, which the .NET SDK excludes from its default `**/*.cs` glob — so its
`Program.cs` does **not** collide with the web project's own top-level statements.
Verify that still holds after moving things around:

```bash
dotnet msbuild Fixlosophy.csproj -getItem:Compile -p:Configuration=Release | grep -c claude   # expect 0
```

## Run the app (agent path)

Start the **Release** build on port 5127, in Development:

```bash
ASPNETCORE_ENVIRONMENT=Development dotnet exec bin/Release/net10.0/Fixlosophy.dll \
  --urls http://localhost:5127 > /tmp/app.log 2>&1 &
```

Both details matter — see Gotchas. Wait for it:

```bash
until grep -qE "Now listening|Unhandled" /tmp/app.log; do sleep 1; done
```

## Drive it

The driver reads commands on stdin, one per line, and prints `OK: ...` / `ERR: ...`
per command. Pipe a script into it:

```bash
cd .claude/skills/run-fixlosophy/driver
{
  echo 'goto http://localhost:5127/'
  echo 'size 390x844'
  echo 'shotview C:\path\to\home-390.png'
  echo 'overflow'
  echo 'quit'
} | dotnet bin/Release/net10.0/pwdriver.dll
```

| Command | Effect |
|---|---|
| `goto <url>` | navigate, wait for network idle, settle layout |
| `size <w>x<h>` | set viewport, settle layout |
| `shot <path>` / `shotview <path>` | full-page / viewport-only screenshot |
| `eval <js>` | evaluate an expression or arrow function, print JSON |
| `click <selector>` | click first match |
| `text` / `count <selector>` | textContent / match count |
| `overflow` | horizontal overflow + the elements causing it |
| `touch <selector>` | matching elements smaller than 44×44 |
| `wait <ms>` | sleep |
| `quit` | close browser and exit |

`ERR:` sets a non-zero exit code, so a smoke script can be gated on it.

### Recipes that have actually been used here

Responsive sweep — every route at every breakpoint, reporting only failures:

```bash
{
for p in / /services /about /gallery /contact /book /privacy /terms \
         /account/login /account/register /admin/login /not-found; do
  for w in 320 360 390 414 480 640 768 1024 1440; do
    echo "size ${w}x900"; echo "goto http://localhost:5127$p"
    echo "eval () => { const o=document.documentElement.scrollWidth-window.innerWidth; return o>1 ? 'OVERFLOW $p @${w} by '+o : 'ok'; }"
  done
done
echo quit
} | dotnet bin/Release/net10.0/pwdriver.dll 2>/dev/null | grep -i overflow
```

Drive the booking wizard to the calendar (the main mobile flow):

```bash
{
  echo 'size 390x844'
  echo 'goto http://localhost:5127/book'
  echo 'wait 2500'
  echo 'click .service-pick-card'
  echo 'wait 600'
  echo 'click .booking-nav .btn-primary'
  echo 'wait 1500'
  echo 'count .cal-day'
  echo 'quit'
} | dotnet bin/Release/net10.0/pwdriver.dll
```

Prove the iOS zoom fix — any focusable field under 16px makes Safari zoom the
viewport on focus and never zoom back:

```bash
{
  echo 'size 390x900'; echo 'goto http://localhost:5127/contact'
  echo 'eval () => [...new Set([...document.querySelectorAll("input,select,textarea")].map(e=>parseFloat(getComputedStyle(e).fontSize)))]'
  echo 'quit'
} | dotnet bin/Release/net10.0/pwdriver.dll
```

Expect `[16]` below 768px and `[16, 15.2]` above it.

## Test on a physical device (phone / tablet)

Headless Edge at an emulated 390px is not an iPhone. Three behaviours can only be
checked on real hardware: iOS Safari's zoom-on-focus, whether `@media (hover: hover)`
actually suppresses latched hover styles on touch, and `env(safe-area-inset-*)` on a
notched device.

**Launch it so other devices can reach it.** The `http`/`https` profiles bind
`localhost`, which nothing else on the network can see:

```bash
dotnet run --launch-profile phone
```

Binds `http://0.0.0.0:5127`. HTTP-only is deliberate — adding an HTTPS binding gives
`UseHttpsRedirection` a port to redirect to, and the phone doesn't trust the dev
certificate, so the redirect fails and takes the SignalR circuit with it. With no
HTTPS port to find, that middleware logs a warning and passes through.

Find the machine's address and confirm the binding before involving the phone:

```powershell
Get-NetIPAddress -AddressFamily IPv4 |
  Where-Object { $_.IPAddress -notlike '127.*' -and $_.IPAddress -notlike '169.254.*' } |
  Select-Object IPAddress, InterfaceAlias
```

Then browse to `http://<that-address>:5127` on the phone.

### Prerequisites — the phone will not connect without these

Note that `curl http://<lan-ip>:5127/` **from the host itself** returns 200 regardless:
that's loopback-to-self and never crosses the firewall, so it proves the binding but
not reachability. Only the phone proves reachability.

1. **A firewall rule.** Windows blocks inbound by default, more so when the network is
   classed Public. `-RemoteAddress LocalSubnet` keeps it to your own network
   (needs an elevated shell):
   ```powershell
   New-NetFirewallRule -DisplayName "Fixlosophy dev (LAN test)" `
     -Direction Inbound -Action Allow -Protocol TCP -LocalPort 5127 `
     -Profile Private,Public -RemoteAddress LocalSubnet
   ```
   Remove with `Remove-NetFirewallRule -DisplayName "Fixlosophy dev (LAN test)"`.

2. **VPN local network sharing.** A VPN with a killswitch (Mullvad, for one) drops LAN
   traffic before the firewall sees it. Enable its local-network-sharing setting, or
   disconnect for the session. Check with `Get-NetIPAddress` — a tunnel interface
   alongside the LAN one means a VPN is up.

3. **Network category.** `Get-NetConnectionProfile`; if your own Wi-Fi says `Public`,
   `Set-NetConnectionProfile -InterfaceAlias "<alias>" -NetworkCategory Private` makes
   Windows less restrictive and is the correct category for a network you own.

**Fallback when none of that is possible** (different network, cellular, locked-down
VPN): a Cloudflare quick tunnel gives a public HTTPS URL with no account and no
firewall change — `cloudflared tunnel --url http://localhost:5127`. Weigh it first:
this app talks to the real Supabase database, so the tunnel puts live customer data on
the internet for as long as it runs.

### `/devinfo`

Safari's Web Inspector needs a Mac, so there is no console to read from Windows.
`/devinfo` (Development only, static SSR, filled in by `wwwroot/site.js`) reports what
the device actually says:

- **`visualViewport.scale`, live** — the headline test. Tap the field on that page; if
  the number stays `1.00`, iOS is not zooming. It jumps to ~1.3 if a field is under 16px.
- Computed `font-size` of a real `.form-control` (the mechanism behind the above)
- `matchMedia('(hover: hover) and (pointer: fine)')` — must read **false** on a phone
- `env(safe-area-inset-*)` — non-zero on a notched device
- Viewport, DPR, `100svh` vs `innerHeight`, reduced-motion, measured touch targets,
  whether the Blazor script loaded, user agent

There's a **Copy** button. On plain HTTP `navigator.clipboard` is unavailable (it needs
a secure context), so it falls back to selecting the text in a textarea for a manual copy.

Sanity-check it renders before handing the URL over:

```bash
curl -s http://localhost:5127/devinfo | grep -c 'id="devinfo-probe"'   # expect 1
```

## Run the app (human path)

`dotnet run` uses the `http` launch profile (Development, port 5126) and opens a
browser. Fine for hand-testing; see the Debug-lock gotcha before mixing it with the
agent path.

## Test

```bash
dotnet test Fixlosophy.Tests/Fixlosophy.Tests.csproj -c Release --no-build --nologo
```

EF Core InMemory — no database or secrets needed.

## Gotchas

- **`--no-launch-profile` means Production, and Production refuses to start.** Without
  `ASPNETCORE_ENVIRONMENT=Development` the app hits its own guard and throws
  `AllowedHosts is "*"`. That guard is deliberate (host-header injection into emailed
  links). Always set the env var, or use a launch profile.
- **A stale instance on 5126 locks the Debug build.** If someone left `dotnet run`
  going, `bin/Debug/net10.0/Fixlosophy.exe` is locked and any later `dotnet run` dies
  with `MSB3027 ... being used by another process`. Running the **Release** DLL via
  `dotnet exec` on **5127** sidesteps both the lock and the port. Check the holder
  with `Get-NetTCPConnection -LocalPort 5126 -State Listen`.
- **Screenshot paths must be Windows-style.** Playwright treats the string literally,
  so a bash path like `/c/Users/...` silently creates `C:\c\Users\...`. Pass
  `C:\Users\...`.
- **`shotview` captures at the current scroll position.** After clicking through the
  booking wizard the page may be scrolled to the footer. Scroll first:
  `eval () => { document.querySelector(".calendar-wrapper").scrollIntoView({block:"start"}); return "ok"; }`
- **Blazor's circuit needs ~2s before `@onclick` works.** Clicks sent immediately
  after `goto` on an interactive page are dropped. `wait 2500` after loading `/book`.
- **After restarting the app, the first circuit is much slower.** JIT plus the first
  Supabase round trips push it past 2.5s, and clicks time out with
  `TimeoutException: Timeout 10000ms exceeded`. Use `wait 6000` after the first
  navigation following a restart; 2500 is fine for every interaction after that.
  A timeout on the *first* click of a fresh run is nearly always this, not a bad
  selector — re-run before going hunting.
- **`goto` on the same URL you're already on may not fully reload.** Interleave a
  different route, or change viewport between measurements.
- **A residual ~948px overflow reading on `/about` and `/contact`.** The driver settles
  layout across animation frames before measuring, but a long sweep still occasionally
  reports content at exactly 948px on the two pages carrying the portrait Supabase
  photo. It is not reproducible in isolation (18 attempts), and
  `PerformanceObserver('layout-shift')` reports **CLS 0** on those pages — so it is a
  measurement artifact between `goto` resolving and first paint, not a page defect.
  Re-check in isolation before believing a hit.
- **`/admin` cannot be reached without staff credentials.** The seeded dev admin
  password is random and logged only on first run. To verify admin CSS without logging
  in, inject probe markup into any page that loads `booking.css` and read computed
  styles — that is how the mobile/desktop switch was checked:
  ```
  eval () => { const d=document.createElement("div"); d.innerHTML='<div class="admin-mobile-cards" id="p1">x</div><div class="admin-table-wrapper admin-table-wrapper--desktop-only" id="p2">y</div>'; document.body.appendChild(d); return "injected"; }
  eval () => ({ cards: getComputedStyle(document.getElementById("p1")).display, table: getComputedStyle(document.getElementById("p2")).display })
  ```
  Exactly one of the two must be non-`none` at any width.
- **`booking.css` loads after `app.css`.** Media queries add no specificity, so at
  equal specificity a later rule in `booking.css` beats an earlier one in `app.css` —
  and a rule placed *above* its target in the same file loses too. Two real bugs came
  from this (the 16px input floor, and the admin cards rendering alongside the table
  at 768px+). Put an override next to what it overrides, and verify with
  `getComputedStyle`, not by reading the source.

## Troubleshooting

| Symptom | Fix |
|---|---|
| `AllowedHosts is "*"` on startup | Set `ASPNETCORE_ENVIRONMENT=Development`, or set a real `AllowedHosts` for a production run. |
| `MSB3027: Could not copy ... Fixlosophy.exe ... locked by Fixlosophy (PID)` | A previous instance is running. Use the Release DLL on another port, or stop that process. |
| `Failed to bind to address ... already in use` | Something is on 5126. Use `--urls http://localhost:5127`. |
| Driver writes screenshots somewhere odd | Bash-style path — use `C:\...`. |
| `ERR: TimeoutError` on `click` | Circuit not connected yet; add `wait 2500` after `goto`. |
| `ConnectionString property has not been initialized` | `appsettings.Local.json` missing at repo root. |
