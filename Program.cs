using System.Security.Cryptography;
using System.Threading.RateLimiting;
using Fixlosophy.Components;
using Fixlosophy.Data;
using Fixlosophy.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

// Allow DateTime.Now / DateTime.Today to be stored as Postgres "timestamp without time zone"
AppContext.SetSwitch("Npgsql.EnableLegacyTimestampBehavior", true);

var builder = WebApplication.CreateBuilder(args);

// CreateBuilder has already loaded appsettings.json and
// appsettings.{Environment}.json, followed by environment variables and
// command-line arguments. Later sources win, so re-adding those JSON files here
// would let the checked-in appsettings.json shadow the environment. Add only the
// gitignored local-overrides file, then re-add the environment and command-line
// sources so they keep the last word.
builder.Configuration.AddJsonFile("appsettings.Local.json", optional: true, reloadOnChange: true);
builder.Configuration.AddEnvironmentVariables();
builder.Configuration.AddCommandLine(args);

builder.Services.AddDbContext<AppDbContext>(options =>
    options.UseNpgsql(builder.Configuration.GetConnectionString("DefaultConnection")));

builder.Services.AddHttpClient();
builder.Services.AddScoped<BookingService>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<InflationService>();
builder.Services.AddScoped<ActionRateLimiter>();
builder.Services.AddSingleton<SiteImages>();

// HTTP-level rate limit per client IP: covers page loads and SignalR circuit
// negotiation. In-circuit actions are throttled separately by ActionRateLimiter.
builder.Services.AddRateLimiter(options =>
{
    options.RejectionStatusCode = StatusCodes.Status429TooManyRequests;
    options.OnRejected = async (context, cancellationToken) =>
    {
        context.HttpContext.Response.Headers.RetryAfter = "10";
        await context.HttpContext.Response.WriteAsync(
            "Too many requests. Please try again shortly.", cancellationToken);
    };
    options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 100,
                Window = TimeSpan.FromSeconds(10),
                QueueLimit = 0
            }));

    // Tighter per-IP limit for the sign-in / registration endpoints, which run
    // as plain HTTP requests outside the SignalR circuit (so ActionRateLimiter,
    // which is per-circuit, can't guard them).
    options.AddPolicy("auth", httpContext =>
        RateLimitPartition.GetFixedWindowLimiter(
            httpContext.Connection.RemoteIpAddress?.ToString() ?? "unknown",
            _ => new FixedWindowRateLimiterOptions
            {
                PermitLimit = 5,
                Window = TimeSpan.FromMinutes(1),
                QueueLimit = 0
            }));
});

builder.Services.AddRazorComponents()
    .AddInteractiveServerComponents();

// Cookie authentication for staff and customers (one shared scheme; a
// "user_type" claim distinguishes them). Persistence is decided per login via
// the "Remember me" checkbox, so the 30-day window only applies when the user
// opts in — otherwise it's a session cookie that clears on browser close.
builder.Services.AddAuthentication(CookieAuthenticationDefaults.AuthenticationScheme)
    .AddCookie(options =>
    {
        options.Cookie.Name        = "Fixlosophy.Auth";
        options.Cookie.HttpOnly    = true;
        options.Cookie.SameSite    = SameSiteMode.Lax;
        // Secure in production; SameAsRequest in dev so it works over plain http://localhost.
        options.Cookie.SecurePolicy = builder.Environment.IsDevelopment()
            ? CookieSecurePolicy.SameAsRequest
            : CookieSecurePolicy.Always;
        options.ExpireTimeSpan    = TimeSpan.FromDays(30);
        options.SlidingExpiration = true;
        options.LoginPath         = "/admin/login";
        options.AccessDeniedPath  = "/admin/login";
    });
builder.Services.AddAuthorization();
builder.Services.AddCascadingAuthenticationState();

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db        = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var config    = scope.ServiceProvider.GetRequiredService<IConfiguration>();
    var inflation = scope.ServiceProvider.GetRequiredService<InflationService>();
    EnsureSchema(db, app.Logger);
    SeedServicePricings(db);
    SeedDemoData(db);
    SeedDefaultAdmin(db, config, app.Logger);
    await ApplyAnnualPriceIncreaseAsync(db, config, inflation);
}

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

app.UseAntiforgery();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// ── Auth endpoints ────────────────────────────────────────────────────────
// Cookies can only be written on a real HTTP request, never mid-circuit, so the
// login forms (plain <form method="post"> on static pages) post here. The
// "remember" field comes from the Remember-me checkbox; presence == checked.
// Antiforgery is enforced automatically because these bind [FromForm] params.

app.MapPost("/auth/staff-login", async (
    HttpContext http, AuthService auth,
    [FromForm] string email, [FromForm] string password,
    [FromForm] string? remember, [FromForm] string? returnUrl) =>
{
    var staff = auth.AuthenticateStaff(email, password);
    if (staff is null)
        return Results.Redirect("/admin/login?error=1");

    await http.SignInAsync(
        CookieAuthenticationDefaults.AuthenticationScheme,
        AuthClaims.BuildStaffPrincipal(staff),
        new AuthenticationProperties { IsPersistent = !string.IsNullOrEmpty(remember) });

    return Results.Redirect(SafeReturn(returnUrl, "/admin"));
}).RequireRateLimiting("auth");

app.MapPost("/auth/customer-login", async (
    HttpContext http, AuthService auth,
    [FromForm] string email, [FromForm] string password,
    [FromForm] string? remember, [FromForm] string? returnUrl) =>
{
    var customer = auth.AuthenticateCustomer(email, password);
    if (customer is null)
        return Results.Redirect($"/account/login?error=1&returnUrl={Uri.EscapeDataString(SafeReturn(returnUrl, "/"))}");

    await http.SignInAsync(
        CookieAuthenticationDefaults.AuthenticationScheme,
        AuthClaims.BuildCustomerPrincipal(customer),
        new AuthenticationProperties { IsPersistent = !string.IsNullOrEmpty(remember) });

    return Results.Redirect(SafeReturn(returnUrl, "/"));
}).RequireRateLimiting("auth");

app.MapPost("/auth/customer-register", async (
    HttpContext http, AuthService auth,
    [FromForm] string fullName, [FromForm] string email,
    [FromForm] string? phone, [FromForm] string password,
    [FromForm] string? remember, [FromForm] string? returnUrl) =>
{
    var (customer, error) = auth.RegisterCustomer(email, fullName, phone ?? "", password);
    if (error is not null || customer is null)
        return Results.Redirect($"/account/register?error={Uri.EscapeDataString(error ?? "Could not create account.")}");

    await http.SignInAsync(
        CookieAuthenticationDefaults.AuthenticationScheme,
        AuthClaims.BuildCustomerPrincipal(customer),
        new AuthenticationProperties { IsPersistent = !string.IsNullOrEmpty(remember) });

    return Results.Redirect(SafeReturn(returnUrl, "/"));
}).RequireRateLimiting("auth");

// GET so the Sign Out button inside interactive components is a simple
// forceLoad navigation. Logout CSRF is low-risk (worst case: a forced logout).
app.MapGet("/auth/logout", async (HttpContext http, string? returnUrl) =>
{
    await http.SignOutAsync(CookieAuthenticationDefaults.AuthenticationScheme);
    return Results.Redirect(SafeReturn(returnUrl, "/"));
});

app.Run();

// Only allow same-site relative redirects to avoid open-redirect via returnUrl.
static string SafeReturn(string? url, string fallback) =>
    !string.IsNullOrEmpty(url) && Uri.IsWellFormedUriString(url, UriKind.Relative)
        && url.StartsWith('/') && !url.StartsWith("//") && !url.StartsWith("/\\")
        ? url : fallback;

static void EnsureSchema(AppDbContext db, ILogger logger)
{
    db.Database.ExecuteSqlRaw(@"
        CREATE TABLE IF NOT EXISTS ""Customers"" (
            ""Id""           varchar(36) NOT NULL,
            ""Email""        text        NOT NULL DEFAULT '',
            ""FullName""     text        NOT NULL DEFAULT '',
            ""Phone""        text        NOT NULL DEFAULT '',
            ""PasswordHash"" text        NOT NULL DEFAULT '',
            ""CreatedAt""    timestamp   NOT NULL DEFAULT now(),
            CONSTRAINT ""PK_Customers"" PRIMARY KEY (""Id"")
        );
        CREATE UNIQUE INDEX IF NOT EXISTS ""IX_Customers_Email"" ON ""Customers"" (""Email"");

        CREATE TABLE IF NOT EXISTS ""Staff"" (
            ""Id""                     varchar(36) NOT NULL,
            ""FullName""               text        NOT NULL DEFAULT '',
            ""Email""                  text        NOT NULL DEFAULT '',
            ""PasswordHash""           text        NOT NULL DEFAULT '',
            ""Role""                   integer     NOT NULL DEFAULT 1,
            ""IsActive""               boolean     NOT NULL DEFAULT true,
            ""CreatedAt""              timestamp   NOT NULL DEFAULT now(),
            ""CanViewAllBookings""     boolean     NOT NULL DEFAULT false,
            ""CanManageBookings""      boolean     NOT NULL DEFAULT true,
            ""CanViewCustomerDetails"" boolean     NOT NULL DEFAULT false,
            CONSTRAINT ""PK_Staff"" PRIMARY KEY (""Id"")
        );
        CREATE UNIQUE INDEX IF NOT EXISTS ""IX_Staff_Email"" ON ""Staff"" (""Email"");

        CREATE TABLE IF NOT EXISTS ""ServicePricings"" (
            ""Id""           varchar(36)   NOT NULL,
            ""Name""         text          NOT NULL DEFAULT '',
            ""Category""     text          NOT NULL DEFAULT '',
            ""CurrentPrice"" numeric(18,2) NOT NULL DEFAULT 0,
            ""Duration""     text          NOT NULL DEFAULT '',
            ""Icon""         text          NOT NULL DEFAULT '',
            ""SortOrder""    integer       NOT NULL DEFAULT 0,
            ""IsQuoteOnly""  boolean       NOT NULL DEFAULT false,
            CONSTRAINT ""PK_ServicePricings"" PRIMARY KEY (""Id"")
        );

        CREATE TABLE IF NOT EXISTS ""PriceAdjustments"" (
            ""Id""        serial       NOT NULL,
            ""Year""      integer      NOT NULL,
            ""Rate""      numeric(8,4) NOT NULL,
            ""AppliedAt"" timestamp    NOT NULL DEFAULT now(),
            CONSTRAINT ""PK_PriceAdjustments"" PRIMARY KEY (""Id"")
        );

        CREATE TABLE IF NOT EXISTS ""Bookings"" (
            ""Id""              varchar(36)   NOT NULL,
            ""Reference""       text          NOT NULL DEFAULT '',
            ""CreatedAt""       timestamp     NOT NULL DEFAULT now(),
            ""CustomerName""    text          NOT NULL DEFAULT '',
            ""CustomerEmail""   text          NOT NULL DEFAULT '',
            ""CustomerPhone""   text          NOT NULL DEFAULT '',
            ""ServiceCategory"" text          NOT NULL DEFAULT '',
            ""ServiceName""     text          NOT NULL DEFAULT '',
            ""ServicePrice""    numeric(18,2) NOT NULL DEFAULT 0,
            ""SlotDate""        timestamp     NOT NULL DEFAULT now(),
            ""SlotTime""        text          NOT NULL DEFAULT '',
            ""BikeDescription"" text          NOT NULL DEFAULT '',
            ""Notes""           text          NOT NULL DEFAULT '',
            ""Status""          integer       NOT NULL DEFAULT 0,
            CONSTRAINT ""PK_Bookings"" PRIMARY KEY (""Id"")
        );

        ALTER TABLE ""Bookings"" ADD COLUMN IF NOT EXISTS ""CustomerId""       varchar(36) NULL;
        ALTER TABLE ""Bookings"" ADD COLUMN IF NOT EXISTS ""AssignedStaffId""  varchar(36) NULL;

        -- Relational FK constraints, added after every referenced table exists (idempotent via pg_constraint check)
        DO $$ BEGIN
            IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_Bookings_Customers') THEN
                ALTER TABLE ""Bookings"" ADD CONSTRAINT ""FK_Bookings_Customers""
                    FOREIGN KEY (""CustomerId"") REFERENCES ""Customers""(""Id"") ON DELETE SET NULL;
            END IF;
        END $$;

        DO $$ BEGIN
            IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_Bookings_Staff') THEN
                ALTER TABLE ""Bookings"" ADD CONSTRAINT ""FK_Bookings_Staff""
                    FOREIGN KEY (""AssignedStaffId"") REFERENCES ""Staff""(""Id"") ON DELETE SET NULL;
            END IF;
        END $$;

        -- Enable RLS on all public tables to prevent exposure via PostgREST.
        -- The app connects as postgres (superuser) which bypasses RLS, so this
        -- only affects the anon / authenticated roles used by the REST API.
        ALTER TABLE ""Bookings""         ENABLE ROW LEVEL SECURITY;
        ALTER TABLE ""Customers""        ENABLE ROW LEVEL SECURITY;
        ALTER TABLE ""Staff""            ENABLE ROW LEVEL SECURITY;
        ALTER TABLE ""ServicePricings""  ENABLE ROW LEVEL SECURITY;
        ALTER TABLE ""PriceAdjustments"" ENABLE ROW LEVEL SECURITY;

        -- ServicePricings is a public price catalogue; allow anonymous reads.
        -- All other tables have no policies, so PostgREST access is fully blocked.
        DO $$ BEGIN
            IF NOT EXISTS (
                SELECT 1 FROM pg_policies
                WHERE schemaname = 'public'
                  AND tablename  = 'ServicePricings'
                  AND policyname = 'anon_read_service_pricings'
            ) THEN
                CREATE POLICY anon_read_service_pricings ON ""ServicePricings""
                    FOR SELECT TO anon, authenticated USING (true);
            END IF;
        END $$;
    ");

    // Race-safety net for duplicate bookings: one active (non-cancelled)
    // booking per email + slot. Created separately so pre-existing duplicate
    // rows degrade to a startup warning instead of a crash.
    try
    {
        db.Database.ExecuteSqlRaw(@"
            CREATE UNIQUE INDEX IF NOT EXISTS ""IX_Bookings_NoDuplicateSlot""
            ON ""Bookings"" (lower(""CustomerEmail""), ""SlotDate"", ""SlotTime"")
            WHERE ""Status"" <> 4;");
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex,
            "Could not create IX_Bookings_NoDuplicateSlot — existing data may contain duplicate bookings.");
    }
}

static void SeedServicePricings(AppDbContext db)
{
    if (db.ServicePricings.Any()) return;

    ServicePricing[] services =
    [
        new() { Name = "Basic Service",                   Category = "Servicing Packages", CurrentPrice = 35,  Duration = "1 hour",    Icon = "&#128736;", SortOrder = 10 },
        new() { Name = "Full Service",                    Category = "Servicing Packages", CurrentPrice = 70,  Duration = "2–3 hours", Icon = "&#128736;", SortOrder = 20 },
        new() { Name = "Gold Service",                    Category = "Servicing Packages", CurrentPrice = 120, Duration = "3–4 hours", Icon = "&#128736;", SortOrder = 30 },
        new() { Name = "Brompton Full Service",           Category = "Specialist",         CurrentPrice = 70,  Duration = "2–3 hours", Icon = "&#9889;",   SortOrder = 40 },
        new() { Name = "Fixed-Single Speed Full Service", Category = "Specialist",         CurrentPrice = 50,  Duration = "2 hours",   Icon = "&#9889;",   SortOrder = 50 },
        new() { Name = "Kids Bike Basic Service",         Category = "Specialist",         CurrentPrice = 25,  Duration = "1 hour",    Icon = "&#9889;",   SortOrder = 60 },
        new() { Name = "Bottom Bracket Service",          Category = "Components",         CurrentPrice = 25,  Duration = "1 hour",    Icon = "&#128295;", SortOrder = 70 },
        new() { Name = "Wheel Build",                     Category = "Components",         CurrentPrice = 50,  Duration = "2–3 hours", Icon = "&#128295;", SortOrder = 80 },
        new() { Name = "Wheel Trueing",                   Category = "Components",         CurrentPrice = 15,  Duration = "30 min",    Icon = "&#128295;", SortOrder = 90 },
        new() { Name = "Headset Service",                 Category = "Components",         CurrentPrice = 25,  Duration = "45 min",    Icon = "&#128295;", SortOrder = 100 },
        new() { Name = "Hub Service",                     Category = "Components",         CurrentPrice = 15,  Duration = "45 min",    Icon = "&#128295;", SortOrder = 110 },
        new() { Name = "Gear Service",                    Category = "Components",         CurrentPrice = 10,  Duration = "30 min",    Icon = "&#128295;", SortOrder = 120 },
    ];

    foreach (var s in services)
        db.ServicePricings.Add(s);
    db.SaveChanges();
}

static async Task ApplyAnnualPriceIncreaseAsync(
    AppDbContext db, IConfiguration config, InflationService inflation)
{
    var today  = DateTime.Today;
    var april1 = new DateTime(today.Year, 4, 1);

    if (today < april1) return;
    if (db.PriceAdjustments.Any(a => a.Year == today.Year)) return;

    // Fetch live UK CPI (ONS CPIH L55O series). PriceIncrease:InflationRate is the
    // fallback used only when the ONS API is unavailable — it is not the floor.
    // The business floor is a deliberate 5% a year, so any rate below it (live or
    // configured) is raised to 5%; only a rate above 5% is used as-is.
    var liveRate       = await inflation.GetLatestAnnualRateAsync();
    var configuredMin  = config.GetValue<decimal>("PriceIncrease:InflationRate", 0.03m);
    var rate           = Math.Max(liveRate ?? configuredMin, 0.05m);

    foreach (var service in db.ServicePricings.Where(s => !s.IsQuoteOnly).ToList())
        service.CurrentPrice = Math.Ceiling(service.CurrentPrice * (1 + rate));

    db.PriceAdjustments.Add(new PriceAdjustment { Year = today.Year, Rate = rate });
    db.SaveChanges();
}

static void SeedDemoData(AppDbContext db)
{
    if (db.Bookings.Any()) return;

    var today    = DateTime.Today;
    var names    = new[] { "Lena Fischer", "Marco Rossi", "Sarah O'Brien", "Tom Walsh", "Priya Nair" };
    var services = new[] { ("Servicing Packages", "Basic Service", 35m), ("Components", "Wheel Trueing", 15m), ("Servicing Packages", "Full Service", 70m), ("Specialist", "Brompton Full Service", 70m), ("Components", "Gear Service", 10m) };
    var emails   = new[] { "lena@example.com", "marco@example.com", "sarah@example.com", "tom@example.com", "priya@example.com" };
    var statuses = new[] { BookingStatus.Confirmed, BookingStatus.Pending, BookingStatus.Confirmed, BookingStatus.Pending, BookingStatus.Completed };
    var offsets  = new[] { 0, 0, 1, 2, -1 };
    var times    = new[] { "09:00", "11:00", "14:00", "10:00", "15:00" };
    var bikes    = new[] { "Trek FX3 Disc 2022", "Giant Escape 3", "Specialized Turbo Como", "Orbea MX 50", "Cannondale Quick 5" };

    for (int i = 0; i < names.Length; i++)
    {
        var date = today.AddDays(offsets[i]);
        while (date.DayOfWeek == DayOfWeek.Sunday) date = date.AddDays(1);
        db.Bookings.Add(new Booking
        {
            Reference       = $"FIX-{date:yyMMdd}-{i:D3}",
            CreatedAt       = DateTime.Now.AddHours(-i * 3),
            CustomerName    = names[i],
            CustomerEmail   = emails[i],
            CustomerPhone   = $"+1 555 00{i}00{i}",
            ServiceCategory = services[i].Item1,
            ServiceName     = services[i].Item2,
            ServicePrice    = services[i].Item3,
            SlotDate        = date,
            SlotTime        = times[i],
            BikeDescription = bikes[i],
            Notes           = "",
            Status          = statuses[i]
        });
    }
    db.SaveChanges();
}

static void SeedDefaultAdmin(AppDbContext db, IConfiguration config, ILogger logger)
{
    if (db.Staff.Any()) return;

    var email    = config["SeedAdmin:Email"]?.Trim();
    var password = config["SeedAdmin:Password"];
    if (string.IsNullOrEmpty(email)) email = "admin@fixlosophy.com";

    if (string.IsNullOrEmpty(password))
    {
        password = RandomNumberGenerator.GetString(
            "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789", 20);
        logger.LogWarning(
            "No SeedAdmin:Password configured. Seeded initial admin {Email} with generated password: {Password} " +
            "— store it securely. To choose your own, set SeedAdmin:Email/Password in appsettings.Local.json " +
            "before first run.", email, password);
    }

    db.Staff.Add(new StaffMember
    {
        FullName     = "Admin",
        Email        = email,
        PasswordHash = AuthService.HashPassword(password),
        Role         = StaffRole.Admin,
        IsActive     = true
    });
    db.SaveChanges();
}
