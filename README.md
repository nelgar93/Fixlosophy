# Fixlosophy

A bike repair shop's booking site: public marketing pages, an online booking wizard,
customer accounts, and a staff/admin dashboard. Built with **.NET 10** and **Blazor
Server** (interactive server render mode), backed by **PostgreSQL** (Supabase).

## Stack

- ASP.NET Core / Blazor Server, C#
- EF Core 9 + Npgsql (PostgreSQL)
- Cookie authentication — one shared scheme for staff and customers, distinguished by
  a `user_type` claim
- MailKit for outbound email over SMTP
- No client-side JS framework; plain CSS (`wwwroot/app.css`, `wwwroot/booking.css`)
  and one plain script (`wwwroot/site.js`)

## Running locally

1. **Database connection.** The app needs a Postgres connection string. Create
   `appsettings.Local.json` (gitignored, never commit real credentials) at the repo
   root:
   ```json
   {
     "ConnectionStrings": {
       "DefaultConnection": "Host=...;Port=5432;Database=postgres;Username=...;Password=...;SSL Mode=Require;"
     }
   }
   ```
   If you're on Supabase: the direct database host is IPv6-only, so from most local
   networks you'll want the **session pooler** connection string (Project Settings →
   Database → Connection pooling), not the direct host.

   Add `Notifications:Email` in the same file — the inbox that receives new/cancelled
   booking alerts and contact enquiries:
   ```json
   "Notifications": { "Email": "someone@example.com" }
   ```
   This is deliberately **not** `SiteContent.Email`, which is the address the site
   publishes to customers on every public page and in the JSON-LD business listing.
   The two are free to differ, and the inbox can move without a rebuild. Left unset,
   those two notifications are dropped with an error in the log; customer-facing mail
   (verification, password reset, booking confirmation) is unaffected.

2. **First run seeds itself.** On startup the app creates its schema with idempotent
   `CREATE TABLE IF NOT EXISTS` statements (see `EnsureSchema` in `Program.cs` — there's
   no formal EF Core migrations setup), seeds the service price list and some demo
   bookings, and creates an initial admin account.
   - In **Development**, if you don't set `SeedAdmin:Password`, one is generated and
     logged to the console on first run — copy it from there.
   - Outside Development, `SeedAdmin:Email`/`SeedAdmin:Password` **must** be set (via
     `appsettings.Local.json` or environment variables) or startup fails — the app
     refuses to mint-and-log a real admin credential in production.

3. **Run it:**
   ```
   dotnet run
   ```
   Default dev URL: `http://localhost:5126` (see `Properties/launchSettings.json`).
   The `phone` profile binds `0.0.0.0:5127` for testing against a real handset.

## Configuration

Sources are layered in `Program.cs`, later winning: `appsettings.json` →
`appsettings.{Environment}.json` → `appsettings.Local.json` (gitignored) →
environment variables → command line. In production prefer **environment variables**
over a file, using `__` for the `:` separator (`DataProtection__KeyPath`).

| Key | Required | What it does |
|---|---|---|
| `ConnectionStrings:DefaultConnection` | always | Postgres. Supabase users want the session pooler. |
| `SeedAdmin:Email` / `SeedAdmin:Password` | **prod (fails startup)** | The first admin account, created only when the `Staff` table is empty. |
| `AllowedHosts` | **prod (fails startup)** | Real hostname(s), semicolon-separated. `*` is refused outside Development — it lets a forged `Host` header poison the absolute URLs in verification and reset emails. |
| `DataProtection:KeyPath` | **prod (fails startup)** | Directory on durable storage for the key ring. See below. |
| `App:BaseUrl` | strongly recommended | Absolute base for emailed links. Falls back to the request's scheme/host — which a background job doesn't have, so reminders go out without a link when it's unset. |
| `Smtp:Host` / `Port` / `User` / `Password` / `From` / `FromName` / `UseSsl` | for real email | Unset in Development falls back to `ConsoleEmailSender`, which logs the message. |
| `Notifications:Email` | for staff alerts | Inbox for booking alerts and enquiries. Not the published address. |
| `Supabase:Url` / `ServiceRoleKey` / `Bucket` / `WebsiteImagesBucket` | for photos | Validated lazily on first use, not at startup. |
| `SiteImages:BaseUrl` | — | Public bucket URL for site photography. |
| `PriceIncrease:InflationRate` | — | Fallback rate when the ONS API is unreachable. Not a floor — see below. |
| `Maintenance:ReminderHour` | — | Earliest hour (shop time) reminders may go out. Default `17`. |
| `ForwardedHeaders:KnownProxies` | if the proxy isn't local | Comma-separated. Defaults to loopback, which covers a proxy on the same host. |

### Data Protection keys

The key ring encrypts the auth cookie and every antiforgery token. Left at its default
it's held in memory and regenerated on restart, which signs out every customer and
staff member and makes any form open across the restart fail with a bare 400 — on
every deploy.

`DataProtection:KeyPath` must therefore point at a directory that survives restarts,
and the app refuses to start in production without it. The directory is created and
write-probed at startup so a permissions problem fails the deploy rather than
somebody's login. Keys are protected by filesystem permissions (`chmod 700`), not a
certificate.

## Project layout

- `Program.cs` — app startup, schema bootstrap/seeding, auth cookie config, security
  headers, and the `/auth/*` sign-in/register/logout endpoints (plain HTTP POSTs, since
  Blazor Server circuits can't write cookies mid-circuit).
- `Services/` — business logic and the EF Core models.
- `Data/AppDbContext.cs` — the EF Core context.
- `Components/Pages/` — routed pages (`Home`, `Book`, `Admin`, `AccountLogin`, …).
- `Components/Admin/` — the dashboard's tabs, one component each. See below.
- `Components/Layout/` — `NavMenu`, `MainLayout`, shared `Logo` component.
- `Components/Shared/` — reusable panels (`CustomerDetailPanel`, `CustomerImportPanel`,
  `Icon`, `SeoHead`).
- `wwwroot/` — `app.css` (public site), `booking.css` (booking wizard + admin
  dashboard), `site.js` (behaviours for statically-rendered pages and the image
  fallbacks).
- `demo/` — a browser-only replica published to GitHub Pages. localStorage, no network,
  invented data. It is a shareable mock-up, **not** a deployable copy of the app. It
  links `wwwroot/app.css` and `wwwroot/booking.css` rather than copying them, so the
  styling cannot drift; the render functions are hand-written and can, which is what
  `demo/smoke.js` is for.

### Roles & permissions

- **Customers** register/sign in to prefill and track their own bookings. Guests can
  book without an account.
- **Staff** are `Admin` or `Worker`. Admins have full access; Workers have three
  independent permission flags (`CanViewAllBookings`, `CanManageBookings`,
  `CanViewCustomerDetails`) set per-staff-member in the admin dashboard.
- `IsMechanic` sits alongside those but is not a permission — it says whether someone
  works on bikes, which is what availability counts. See below.

## How a few things work

### Startup, and the schema lock

`EnsureSchema` plus the seeders run under a Postgres **session-level advisory lock**
(`WithSchemaLockAsync`), so two processes starting at once — a rolling deploy that
starts the new instance before stopping the old one — can't race on `CREATE TABLE`,
`ALTER TABLE` and the seeders. The loser waits (up to 60s, then fails loudly) and
then runs its own idempotent pass, which is a no-op.

Note the app is intended to run as a **single instance**. Blazor Server holds state per
circuit, so scaling out would also need sticky sessions, and `NotificationHub` is an
in-process fan-out that would need a SignalR backplane.

### The admin dashboard

`Components/Pages/Admin.razor` is only the shell — the header, the notification bell
and the tab bar. Each tab is its own component in `Components/Admin/`:

| Component | Gate |
|---|---|
| `CalendarTab` | any staff |
| `BookingsTab` | any staff (scoped to their own bookings without `CanViewAllBookings`) |
| `CustomersTab`, `EnquiriesTab` | `CanSeeCustomerDetails` |
| `AvailabilityTab`, `PricingTab`, `StaffTab` | Admin |

Three things to know before changing it:

- **`AdminContext` is cascaded**, carrying the staff member and the derived
  permissions. It's for *rendering decisions only* — every handler that changes
  something re-checks, because a Blazor circuit takes instructions from a browser.
  The duplicated guards are deliberate.
- **Switching tabs destroys and recreates the component**, so each tab loads its own
  data in `OnInitialized` and can never show something stale. This replaced a set of
  hand-written cross-tab reloads (the bookings tab used to have to remember to refresh
  the calendar).
- **`BookingActions` and `BookingDangerZone` are shared** by the Bookings and Calendar
  tabs, so both offer the same controls. Which confirm panel is open is held by the
  *parent* and passed down, so opening one closes any other on the page.

This was one 2,013-line file with all six tabs sharing a single `@code` block of about
forty fields. They never shared state — they shared a file.

### Availability: closures and staff absence

A day is bookable if it's a trading day, no all-day `Closure` covers it, and at least
one mechanic is in. `AvailabilityService` owns that rule; `BookingService` consults it
in `GetAvailableSlots`, `DescribeMonth` and `CreateBooking`.

- A **closure** is customer-facing — the reason shows on the booking calendar, because
  "Closed — Christmas" and "fully booked" are different answers and only one means try
  tomorrow. Optionally part-day.
- A **staff absence** is internal. Customers see a closed day, never whose holiday it is.
- `StaffMember.IsMechanic` is what counts towards a day being open, deliberately
  separate from `Role` and `CanManageBookings` — those govern the dashboard, this
  governs the workshop.
- **With nobody flagged as a mechanic the rule switches off** rather than closing every
  day. Failing open is the safe direction: the alternative turns unticking the last
  mechanic into a site that silently stops taking bookings. The Availability and Staff
  tabs both warn when it happens.

Adding a closure that lands on existing bookings surfaces them, with a choice per
booking: move it (keeps the reference, notes and photos) or cancel it and email an
invitation to rebook.

Bookings are also capped at `BookingService.BookingHorizon` (60 days) — the calendar
used to page forward indefinitely, so "fill the whole calendar" had no end to it.

### Recurring work

`MaintenanceService` is a `BackgroundService` that ticks every 10 minutes and calls
each job on `MaintenanceJobs`. It keeps no state about what it has already done — every
job decides for itself whether it is due, so a tick is always safe and a missed window
heals on the next one.

- **Annual price increase** — on or after 1 April, once per calendar year, guarded by a
  `PriceAdjustments` row written in the same transaction as the new prices. The rate is
  live UK CPIH from the ONS API, with `PriceIncrease:InflationRate` as the fallback when
  that's unreachable; either way a **5% business floor** applies, so the configured value
  is a fallback, not a minimum.
- **Notification retention** — deletes notifications older than 30 days.
- **Error log retention** — deletes error groups not seen for 90 days. A group still
  happening is never removed, however old it is.
- **Late arrivals** — rings the bell once for a booking still Pending or Confirmed
  more than 20 minutes past its slot. Moving a booking to InProgress is what the shop
  does when a bike hits the stand, so that's the arrival signal; there's no check-in
  step to remember. The dashboard also shows a derived "Not arrived" badge, which
  doesn't wait on a tick.
- **Appointment reminders** — emails everyone booked in tomorrow, once, no earlier than
  `Maintenance:ReminderHour`. `Bookings.ReminderSentAt` is what makes it once. Bookings
  made within the last 6 hours are skipped, because their confirmation email carried the
  same details.

These used to run at startup, which meant exactly once per process lifetime — a server
that stayed up from March to May never applied the April increase.

### Email

`IEmailSender` is **best-effort and must not throw**. Every send happens after the thing
it describes is already committed, so a dead SMTP host costs the notification and
nothing else. `SmtpEmailSender.SendAsync` enforces that with a catch-all that logs at
Error.

This is a contract, not a courtesy: before it existed, the four `/auth/*` endpoints
awaited sends directly, so a refused SMTP connection 500'd a registration whose account
row had already been written, and made `/auth/forgot-password` answer differently for a
real address than an unknown one.

### Security headers and the CSP

`script-src` carries **no `'unsafe-inline'`**. Practical consequences when editing:

- **An `onclick=""` / `onerror=""` attribute in markup is silently dead.** Put the
  behaviour in `wwwroot/site.js`, delegated from `document`. The image fallbacks
  (`data-hide-on-error`, `data-reveal-next-on-error`) are the worked example.
- **An inline `<script>` needs the nonce** — `nonce="@CspNonce"` in `App.razor`, minted
  per response by the middleware in `Program.cs`. The JSON-LD block does this.
- **`<ImportMap />` is deliberately absent from `App.razor`.** An import map is the one
  inline script a nonce can't cover, and it was the only thing forcing the exception.
  Nothing needs it: the sole JS module is loaded by fingerprinted URL from `@Assets`.
  If you add a collocated `.razor.js` that needs a bare-specifier import, either import
  the `@Assets` URL instead, or put `<ImportMap />` back and accept `'unsafe-inline'`.

`style-src` does still allow `'unsafe-inline'`: Blazor's own reconnect and error UI
carry inline style attributes and there's no nonce hook for framework-emitted markup.

### Photo uploads

Customer photos go to a **private** Supabase bucket; viewing one mints a short-lived
signed URL server-side. The stored path is a fresh guid, never the uploaded filename.

The declared content type is not trusted — `StorageService.SniffImageType` reads the
file's magic bytes, and the sniffed type decides whether the upload is allowed, the
stored extension, and the `Content-Type` Supabase serves it back with.

### Time

Everything date-related goes through `ShopClock`, which is Europe/London (so BST is
handled) rather than the host's clock. A VPS running UTC would otherwise put every
availability check an hour out for the whole of summer.

## Tests

Three tiers, in the order you should reach for them.

```
dotnet test Fixlosophy.Tests/Fixlosophy.Tests.csproj          # 411 tests, ~2s
dotnet test Fixlosophy.Tests.E2E/Fixlosophy.Tests.E2E.csproj  # 13 tests, ~17s
```

**Service tests** (386) over `AuthService`, `AuthClaims`, `BookingService`,
`CustomerService`, `CustomerImportService`, `EnquiryService`, `NotificationService`,
`BikeService`, `MaintenanceJobs`, `StorageService` and `ShopClock`, using EF Core's
InMemory provider — no database connection needed. **The rules live here.** A rule
asserted at this tier should not be asserted again higher up.

**Component tests** (25, `BookWizardTests.cs`) render `Book.razor` in-process with
[bUnit](https://bunit.dev), covering only what a service test cannot see: the order the
wizard's validators run in, which branch each guard takes, and what the confirm handler
passes on — including that a signed-in customer's booking is filed against their account
address rather than the form's. No assertions on markup or copy: those break on every
visual change and catch nothing. Kept small and deliberately droppable — because the
rules live one tier down, deleting this file would cost a bounded amount of coverage
rather than the safety net.

**End-to-end tests** (13, `Fixlosophy.Tests.E2E`) drive the real site in a real browser
via Playwright. Reserved for what genuinely crosses a boundary: the SignalR circuit, the
auth cookie, a page-to-page hand-off — the booking a guest just made showing up on a
signed-in mechanic's dashboard. Also needs no database or secrets; see below.

`RecordingEmailSender.cs` is the shared `IEmailSender` double; add new interface methods
there rather than to a per-class fake. (`Fixlosophy.Tests.E2E` has its own, because it
substitutes the sender inside the running app rather than constructing one.)

### End-to-end: the parts worth knowing

`AppFixture` boots the real `Program.cs` on Kestrel on a random port, backed by the
InMemory provider, and **replaces `IEmailSender` and `IStorageService` inside the running
app**. That substitution is not optional: the fixture loads the real
`appsettings.Local.json` — it has to, that is where the content root is — and that file
holds a working SMTP host and a Supabase service-role key. Without the swap, every
booking these tests make would put real mail in a real inbox.

`E2ETest.GotoInteractiveAsync` is the only way to navigate to an interactive page.
Blazor Server prerenders the markup and attaches the circuit afterwards, so for a moment
every button is present, visible and enabled with nothing behind it — a click in that gap
is silently dropped. **Playwright's auto-waiting does not help**: it waits for the
element to be actionable, not for the application to be listening. So
`Components/Shared/InteractiveReady.razor` renders a hidden `#blazor-ready` marker only
once `RendererInfo.IsInteractive` turns true, and the helper waits for that. No
JavaScript, no framework internals, and no sleeps — which would be both too short on a
cold start and wasted on every navigation after it. Statically rendered pages (the auth
pages) never render the marker; use `GotoStaticAsync` for those.

The fixture also lifts the app's per-IP request limiter for the suite. Every test arrives
from `127.0.0.1` and a Blazor page load is a dozen requests, so a few tests in quick
succession read as one abusive client and start collecting `429`s — including on
`/_blazor/initializers`, which stops the circuit dead on whichever test happened to be
running. The tighter `auth` policy is deliberately left in force.

Set `E2E_TRACE=1` to record a Playwright trace per test (CI does this and uploads them on
failure). The browser is the machine's installed Edge by default; set
`E2E_BROWSER_CHANNEL=none` to use Playwright's own Chromium, which is what CI does.

Known flake, pre-dating this suite:
`MaintenanceJobsTests.FlagLateArrivalsAsync_RaisesOnceForABookingPastItsSlot` fails when
run within ~35 minutes of midnight. It places a booking at `now - 35min`, which lands on
yesterday's date, while `FlagLateArrivalsAsync` only scans today's — arguably a real gap
in the job rather than only in the test.

Two places take an InMemory-specific path so they stay testable, both guarded by
`Database.IsRelational()`: `BookingService.NextReferenceSequence` (falls back to a count
where there's no Postgres sequence) and `WithSchemaLockAsync` (no advisory locks, and no
concurrency to protect against).

## Verifying in a real browser

`.claude/skills/run-fixlosophy/` drives the app headlessly through the machine's
installed Edge via Playwright — no Node, no browser download. Useful for responsive
sweeps, touch-target checks, driving the booking wizard, and confirming CSP changes
didn't quietly break interactivity. See the skill for recipes and gotchas.

## CI

GitHub Actions (`.github/workflows/build.yml`) restores, builds, and runs the test
suite on every push/PR to `main`/`dev`. No secrets required — the test suite doesn't
touch a real database. A separate `e2e` job installs headless Chromium and runs
`Fixlosophy.Tests.E2E`, kept apart so the fast suite stays fast; it uploads Playwright
traces as an artifact when it fails. A third job in the same workflow builds the demo and runs
`demo/smoke.js` against it in headless Chromium, which also cross-checks the demo's
dashboard tabs against `Components/Pages/Admin.razor` — the demo fell a whole tab
behind once, silently, and that is the check that catches it.

`.github/workflows/demo-pages.yml` publishes `demo/` to GitHub Pages, on a push to
`main` touching `demo/` or `wwwroot/` (the demo inlines the real stylesheets at build
time, so a CSS change alters what Pages serves). It never touches the real app or its
database.

## Deployment

Not yet automated. The intended target is a **Hostinger KVM VPS in London** (Supabase is
`eu-west-1`, and prices are in £), behind nginx or Caddy — `UseForwardedHeaders` is
already configured for that.

Before a first deploy:

- Set the four fail-fast keys: `AllowedHosts`, `SeedAdmin:Email`, `SeedAdmin:Password`,
  `DataProtection:KeyPath`.
- Set `App:BaseUrl`, `Smtp:*` and `Notifications:Email`.
- SPF, DKIM and DMARC on the sending domain, or confirmations go to spam.
- A tested database backup — Supabase's free tier has none.
- Deploy stop-then-start rather than rolling, matching the single-instance design.
