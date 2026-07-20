# Fixlosophy

A bike repair shop's booking site: public marketing pages, an online booking wizard,
customer accounts, and a staff/admin dashboard. Built with **.NET 10** and **Blazor
Server** (interactive server render mode), backed by **PostgreSQL** (Supabase).

## Stack

- ASP.NET Core / Blazor Server, C#
- EF Core 9 + Npgsql (PostgreSQL)
- Cookie authentication — one shared scheme for staff and customers, distinguished by
  a `user_type` claim
- No client-side JS framework; plain CSS (`wwwroot/app.css`, `wwwroot/booking.css`)

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

## Project layout

- `Program.cs` — app startup, schema bootstrap/seeding, auth cookie config, the
  `/auth/*` sign-in/register/logout endpoints (plain HTTP POSTs, since Blazor Server
  circuits can't write cookies mid-circuit).
- `Services/` — business logic (`BookingService`, `AuthService`, `InflationService`,
  `ActionRateLimiter`) and the EF Core models.
- `Data/AppDbContext.cs` — the EF Core context.
- `Components/Pages/` — routed pages (`Home`, `Book`, `Admin`, `AccountLogin`, …).
- `Components/Layout/` — `NavMenu`, `MainLayout`, shared `Logo` component.
- `wwwroot/` — `app.css` (public site), `booking.css` (booking wizard + admin
  dashboard).

### Roles & permissions

- **Customers** register/sign in to prefill and track their own bookings.
- **Staff** are `Admin` or `Worker`. Admins have full access; Workers have three
  independent permission flags (`CanViewAllBookings`, `CanManageBookings`,
  `CanViewCustomerDetails`) set per-staff-member in the admin dashboard.

## Tests

```
dotnet test Fixlosophy.Tests/Fixlosophy.Tests.csproj
```

Unit tests for `AuthService`, `AuthClaims`, and `BookingService` using EF Core's
InMemory provider — no database connection needed. One exception:
`BookingService.CreateBooking`'s success path assigns a booking reference from a
Postgres sequence via raw SQL, which InMemory can't back, so only its validation/
rejection paths are covered by unit tests; the success path needs a real Postgres
connection to exercise end-to-end.

## CI

GitHub Actions (`.github/workflows/build.yml`) restores, builds, and runs the test
suite on every push/PR to `main`/`dev`. No secrets required — the test suite doesn't
touch a real database.
