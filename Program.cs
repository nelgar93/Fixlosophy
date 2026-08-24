using System.Security.Cryptography;
using System.Threading.RateLimiting;
using Fixlosophy.Components;
using Fixlosophy.Data;
using Fixlosophy.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;
using StackExchange.Redis;

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
builder.Services.AddScoped<IStorageService, StorageService>();
builder.Services.AddScoped<BikeService>();

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

// The configuration sources are set up at the top of this file, so
// appsettings.Local.json's Smtp:Host (if any) is already visible here. Singleton is
// safe even for the SMTP implementation: it holds no per-request state and
// constructs a fresh MailKit SmtpClient inside every send.
// Falls back to a console-logging sender in Development when no SMTP host is
// configured, so local dev works end-to-end without a real email account.
if (builder.Environment.IsDevelopment() && string.IsNullOrEmpty(builder.Configuration["Smtp:Host"]))
    builder.Services.AddSingleton<IEmailSender, ConsoleEmailSender>();
else
    builder.Services.AddSingleton<IEmailSender, SmtpEmailSender>();

// Verification-token expiry is backed by Redis (TTL-based) rather than a Postgres
// column. Falls back to an in-memory store in Development when no Redis connection
// string is configured; fails fast everywhere else, since silently falling back
// there would quietly defeat the point of the migration (tokens wouldn't survive an
// app restart) with no visible symptom until someone can't verify after a deploy.
var redisConnectionString = builder.Configuration.GetConnectionString("Redis");
if (string.IsNullOrEmpty(redisConnectionString))
{
    if (!builder.Environment.IsDevelopment())
        throw new InvalidOperationException(
            "ConnectionStrings:Redis is not configured. Set it in appsettings.Local.json or environment.");
    builder.Services.AddSingleton<IVerificationTokenStore, InMemoryVerificationTokenStore>();
}
else
{
    builder.Services.AddSingleton<IConnectionMultiplexer>(
        _ => ConnectionMultiplexer.Connect(BuildRedisOptions(redisConnectionString)));
    builder.Services.AddSingleton<IVerificationTokenStore, RedisVerificationTokenStore>();
}

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db        = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var config    = scope.ServiceProvider.GetRequiredService<IConfiguration>();
    var inflation = scope.ServiceProvider.GetRequiredService<InflationService>();
    EnsureSchema(db, app.Logger);
    SeedServicePricings(db);
    SeedDemoData(db);
    SeedDefaultAdmin(db, config, app.Logger, app.Environment.IsDevelopment());
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
    if (!customer.EmailConfirmed)
        return Results.Redirect(
            $"/account/login?error=unverified&email={Uri.EscapeDataString(customer.Email)}" +
            $"&returnUrl={Uri.EscapeDataString(SafeReturn(returnUrl, "/"))}");

    await http.SignInAsync(
        CookieAuthenticationDefaults.AuthenticationScheme,
        AuthClaims.BuildCustomerPrincipal(customer),
        new AuthenticationProperties { IsPersistent = !string.IsNullOrEmpty(remember) });

    return Results.Redirect(SafeReturn(returnUrl, "/"));
}).RequireRateLimiting("auth");

app.MapPost("/auth/customer-register", async (
    HttpContext http, AuthService auth, IEmailSender emailSender, IConfiguration config,
    [FromForm] string fullName, [FromForm] string email,
    [FromForm] string? phone, [FromForm] string password,
    [FromForm] string? returnUrl) =>
{
    var (customer, error) = auth.RegisterCustomer(email, fullName, phone ?? "", password);
    if (error is not null || customer is null)
        return Results.Redirect($"/account/register?error={Uri.EscapeDataString(error ?? "Could not create account.")}");

    var token = auth.GenerateEmailVerificationToken(customer);
    var link  = BuildVerifyEmailLink(http, config, customer.Email, token);
    await emailSender.SendVerificationEmailAsync(customer.Email, customer.FullName, link);

    // No SignInAsync: verification is enforced, so no session is issued until the
    // customer clicks the link and logs in — otherwise an unverified session could
    // persist up to 30 days via "remember me", defeating the point of enforcing it.
    return Results.Redirect($"/account/register-confirmation?returnUrl={Uri.EscapeDataString(SafeReturn(returnUrl, "/"))}");
}).RequireRateLimiting("auth");

app.MapGet("/auth/verify-email", (AuthService auth, string? email, string? token) =>
{
    var ok = !string.IsNullOrEmpty(email) && !string.IsNullOrEmpty(token) && auth.ConfirmEmail(email, token);
    return Results.Redirect(ok ? "/account/login?verified=true" : "/account/login?error=verify-failed");
}).RequireRateLimiting("auth");

app.MapPost("/auth/resend-verification", async (
    HttpContext http, AuthService auth, IEmailSender emailSender, IConfiguration config,
    [FromForm] string email) =>
{
    var customer = auth.GetCustomerByEmail(email);
    if (customer is not null)
    {
        var token = auth.RegenerateEmailVerificationTokenIfNeeded(customer);
        if (token is not null)
        {
            var link = BuildVerifyEmailLink(http, config, customer.Email, token);
            await emailSender.SendVerificationEmailAsync(customer.Email, customer.FullName, link);
        }
    }
    // Identical redirect regardless of match — anti-enumeration.
    return Results.Redirect("/account/login?resent=true");
}).RequireRateLimiting("auth");

app.MapPost("/auth/forgot-password", async (
    HttpContext http, AuthService auth, IEmailSender emailSender, IConfiguration config,
    [FromForm] string email) =>
{
    var token = auth.RequestCustomerPasswordReset(email);
    if (token is not null)
    {
        var customer = auth.GetCustomerByEmail(email)!;
        var link = BuildAbsoluteUrl(http, config, $"/reset-password?token={Uri.EscapeDataString(token)}");
        await emailSender.SendPasswordResetEmailAsync(customer.Email, customer.FullName, link);
    }
    // Identical redirect whether or not the email matched an account — anti-enumeration.
    return Results.Redirect("/account/forgot-password?sent=true");
}).RequireRateLimiting("auth");

app.MapPost("/auth/staff-forgot-password", async (
    HttpContext http, AuthService auth, IEmailSender emailSender, IConfiguration config,
    [FromForm] string email) =>
{
    var token = auth.RequestStaffPasswordReset(email);
    if (token is not null)
    {
        var staff = auth.GetStaffByEmail(email)!;
        var link = BuildAbsoluteUrl(http, config, $"/reset-password?token={Uri.EscapeDataString(token)}");
        await emailSender.SendPasswordResetEmailAsync(staff.Email, staff.FullName, link);
    }
    return Results.Redirect("/admin/forgot-password?sent=true");
}).RequireRateLimiting("auth");

app.MapPost("/auth/reset-password", (
    AuthService auth, [FromForm] string token, [FromForm] string password) =>
{
    var (ok, isStaff, error) = auth.ResetPasswordByToken(token, password);
    if (!ok)
        return Results.Redirect(
            $"/reset-password?token={Uri.EscapeDataString(token)}&error={Uri.EscapeDataString(error ?? "Reset failed.")}");

    return Results.Redirect(isStaff ? "/admin/login?reset=true" : "/account/login?reset=true");
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
        && url.StartsWith('/') && !url.StartsWith("//", StringComparison.Ordinal)
        && !url.StartsWith("/\\", StringComparison.Ordinal)
        ? url : fallback;

// Builds an absolute link for emailed verification/reset URLs. Prefers the
// configured App:BaseUrl (needed behind a reverse proxy that doesn't forward the
// original scheme), falling back to the current request's scheme/host otherwise.
static string BuildAbsoluteUrl(HttpContext http, IConfiguration config, string path)
{
    var baseUrl = config["App:BaseUrl"];
    return string.IsNullOrEmpty(baseUrl)
        ? $"{http.Request.Scheme}://{http.Request.Host}{path}"
        : $"{baseUrl.TrimEnd('/')}{path}";
}

// The verification token is keyed by email in the token store (Redis can't
// efficiently reverse-lookup "which key has this value"), so the link carries both.
static string BuildVerifyEmailLink(HttpContext http, IConfiguration config, string email, string token) =>
    BuildAbsoluteUrl(http, config,
        $"/auth/verify-email?email={Uri.EscapeDataString(email)}&token={Uri.EscapeDataString(token)}");

// StackExchange.Redis doesn't natively parse redis://rediss:// URI-scheme connection
// strings (only its own host:port,key=value token format), so this parses the URI by
// hand. AbortOnConnectFail=false + KeepAlive so a transient blip or Upstash's idle-
// connection kill self-heal instead of throwing/staying dead.
static ConfigurationOptions BuildRedisOptions(string connectionString)
{
    var uri = new Uri(connectionString);
    var options = new ConfigurationOptions
    {
        EndPoints          = { { uri.Host, uri.Port == -1 ? 6379 : uri.Port } },
        Ssl                = uri.Scheme.Equals("rediss", StringComparison.OrdinalIgnoreCase),
        AbortOnConnectFail = false,
        ConnectRetry       = 3,
        ConnectTimeout     = 10_000,
        KeepAlive          = 30,
    };
    if (!string.IsNullOrEmpty(uri.UserInfo))
    {
        var parts = uri.UserInfo.Split(':', 2);
        if (parts.Length == 2)
        {
            options.User     = Uri.UnescapeDataString(parts[0]);
            options.Password = Uri.UnescapeDataString(parts[1]);
        }
    }
    return options;
}

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

        -- Email verification (customers only — staff are admin-provisioned, not
        -- self-registered) + forgot-password (both). DEFAULT true on EmailConfirmed
        -- grandfathers every pre-existing customer row; new rows always specify
        -- false explicitly via the Customer model, so the column default never
        -- applies to them.
        ALTER TABLE ""Customers"" ADD COLUMN IF NOT EXISTS ""EmailConfirmed""              boolean   NOT NULL DEFAULT true;
        ALTER TABLE ""Customers"" ADD COLUMN IF NOT EXISTS ""ResetTokenHash""               text      NULL;
        ALTER TABLE ""Customers"" ADD COLUMN IF NOT EXISTS ""ResetTokenExpiresAt""          timestamp NULL;
        ALTER TABLE ""Customers"" ADD COLUMN IF NOT EXISTS ""ResetCooldownUntil""           timestamp NULL;

        -- Verification tokens moved to Redis (TTL-based expiry, see
        -- IVerificationTokenStore) — these columns are no longer used.
        ALTER TABLE ""Customers"" DROP COLUMN IF EXISTS ""VerificationTokenHash"";
        ALTER TABLE ""Customers"" DROP COLUMN IF EXISTS ""VerificationTokenExpiresAt"";
        ALTER TABLE ""Staff""     ADD COLUMN IF NOT EXISTS ""ResetTokenHash""               text      NULL;
        ALTER TABLE ""Staff""     ADD COLUMN IF NOT EXISTS ""ResetTokenExpiresAt""          timestamp NULL;
        ALTER TABLE ""Staff""     ADD COLUMN IF NOT EXISTS ""ResetCooldownUntil""           timestamp NULL;
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

        -- Depends on Bookings, so it must be created after the block above.
        CREATE TABLE IF NOT EXISTS ""BookingPhotos"" (
            ""Id""          varchar(36) NOT NULL,
            ""BookingId""   varchar(36) NOT NULL,
            ""StoragePath"" text        NOT NULL DEFAULT '',
            ""CreatedAt""   timestamp   NOT NULL DEFAULT now(),
            CONSTRAINT ""PK_BookingPhotos"" PRIMARY KEY (""Id"")
        );

        DO $$ BEGIN
            IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_BookingPhotos_Bookings') THEN
                ALTER TABLE ""BookingPhotos"" ADD CONSTRAINT ""FK_BookingPhotos_Bookings""
                    FOREIGN KEY (""BookingId"") REFERENCES ""Bookings""(""Id"") ON DELETE CASCADE;
            END IF;
        END $$;

        CREATE TABLE IF NOT EXISTS ""Bikes"" (
            ""Id""         varchar(36) NOT NULL,
            ""CustomerId"" varchar(36) NOT NULL,
            ""MakeModel""  text        NOT NULL DEFAULT '',
            ""CreatedAt""  timestamp   NOT NULL DEFAULT now(),
            CONSTRAINT ""PK_Bikes"" PRIMARY KEY (""Id"")
        );

        DO $$ BEGIN
            IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_Bikes_Customers') THEN
                ALTER TABLE ""Bikes"" ADD CONSTRAINT ""FK_Bikes_Customers""
                    FOREIGN KEY (""CustomerId"") REFERENCES ""Customers""(""Id"") ON DELETE CASCADE;
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
        ALTER TABLE ""BookingPhotos""    ENABLE ROW LEVEL SECURITY;
        ALTER TABLE ""Bikes""            ENABLE ROW LEVEL SECURITY;

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

    // Case-insensitive unique email indexes so differing casing can't create
    // duplicate accounts. Done separately (and after dropping any case-sensitive
    // predecessor) so pre-existing case-variant duplicates degrade to a warning
    // rather than blocking startup.
    try
    {
        db.Database.ExecuteSqlRaw(@"
            DROP INDEX IF EXISTS ""IX_Customers_Email"";
            CREATE UNIQUE INDEX IF NOT EXISTS ""IX_Customers_Email"" ON ""Customers"" (lower(""Email""));
            DROP INDEX IF EXISTS ""IX_Staff_Email"";
            CREATE UNIQUE INDEX IF NOT EXISTS ""IX_Staff_Email"" ON ""Staff"" (lower(""Email""));");
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex,
            "Could not create case-insensitive email indexes — existing data may contain case-variant duplicate emails.");
    }

    // Case-insensitive per-customer unique bike names — the real backstop against
    // two concurrent "add bike" requests racing past BikeService.AddBike's own
    // duplicate check (same relationship IX_Bookings_NoDuplicateSlot has to
    // CreateBooking's check).
    try
    {
        db.Database.ExecuteSqlRaw(@"
            CREATE UNIQUE INDEX IF NOT EXISTS ""IX_Bikes_CustomerId_MakeModel""
            ON ""Bikes"" (""CustomerId"", lower(""MakeModel""));");
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex,
            "Could not create IX_Bikes_CustomerId_MakeModel — existing data may contain duplicate bike names.");
    }

    // Atomic counter backing booking references (see BookingService.NextReferenceSequence).
    // Replaces a Count()+1 read-then-format, which raced under concurrent bookings.
    // Seeded from the current row count on first creation only, so numbering continues
    // roughly where it left off instead of restarting at 1 on an existing database.
    try
    {
        db.Database.ExecuteSqlRaw(@"
            DO $$ BEGIN
                IF NOT EXISTS (SELECT 1 FROM pg_sequences WHERE schemaname = 'public' AND sequencename = 'BookingReferenceSeq') THEN
                    CREATE SEQUENCE ""BookingReferenceSeq"";
                    PERFORM setval('""BookingReferenceSeq""', (SELECT COUNT(*) FROM ""Bookings""));
                END IF;
            END $$;");
    }
    catch (Exception ex)
    {
        logger.LogWarning(ex, "Could not create BookingReferenceSeq.");
    }
}

static void SeedServicePricings(AppDbContext db)
{
    if (db.ServicePricings.Any()) return;

    ServicePricing[] services =
    [
        new() { Name = "Basic Service",                   Category = "Servicing Packages", CurrentPrice = 35,  Duration = "1 hour",    SortOrder = 10 },
        new() { Name = "Full Service",                    Category = "Servicing Packages", CurrentPrice = 70,  Duration = "2–3 hours", SortOrder = 20 },
        new() { Name = "Gold Service",                    Category = "Servicing Packages", CurrentPrice = 120, Duration = "3–4 hours", SortOrder = 30 },
        new() { Name = "Brompton Full Service",           Category = "Specialist",         CurrentPrice = 70,  Duration = "2–3 hours", SortOrder = 40 },
        new() { Name = "Fixed-Single Speed Full Service", Category = "Specialist",         CurrentPrice = 50,  Duration = "2 hours",   SortOrder = 50 },
        new() { Name = "Kids Bike Basic Service",         Category = "Specialist",         CurrentPrice = 25,  Duration = "1 hour",    SortOrder = 60 },
        new() { Name = "Bottom Bracket Service",          Category = "Components",         CurrentPrice = 25,  Duration = "1 hour",    SortOrder = 70 },
        new() { Name = "Wheel Build",                     Category = "Components",         CurrentPrice = 50,  Duration = "2–3 hours", SortOrder = 80 },
        new() { Name = "Wheel Trueing",                   Category = "Components",         CurrentPrice = 15,  Duration = "30 min",    SortOrder = 90 },
        new() { Name = "Headset Service",                 Category = "Components",         CurrentPrice = 25,  Duration = "45 min",    SortOrder = 100 },
        new() { Name = "Hub Service",                     Category = "Components",         CurrentPrice = 15,  Duration = "45 min",    SortOrder = 110 },
        new() { Name = "Gear Service",                    Category = "Components",         CurrentPrice = 10,  Duration = "30 min",    SortOrder = 120 },
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
            // Dated by creation day (today) and numbered from the same sequence as
            // real bookings, to match CreateBooking's semantics rather than dating
            // the reference by the appointment's slot date.
            Reference       = $"FIX-{today:yyMMdd}-{BookingService.NextReferenceSequence(db):D3}",
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

static void SeedDefaultAdmin(AppDbContext db, IConfiguration config, ILogger logger, bool isDevelopment)
{
    if (db.Staff.Any()) return;

    var email    = config["SeedAdmin:Email"]?.Trim();
    var password = config["SeedAdmin:Password"];
    if (string.IsNullOrEmpty(email)) email = "admin@fixlosophy.com";
    email = AuthService.NormalizeEmail(email);

    if (string.IsNullOrEmpty(password))
    {
        // Never mint-and-log a real admin credential in production — fail fast so the
        // operator sets one deliberately (env var / secret store / appsettings.Local.json).
        if (!isDevelopment)
            throw new InvalidOperationException(
                "SeedAdmin:Password is not configured. Set SeedAdmin:Email/Password before first " +
                "run (environment variable or appsettings.Local.json) — refusing to seed a logged password.");

        // Development-only convenience so local first-run works out of the box.
        password = RandomNumberGenerator.GetString(
            "ABCDEFGHJKLMNPQRSTUVWXYZabcdefghijkmnpqrstuvwxyz23456789", 20);
        logger.LogWarning(
            "No SeedAdmin:Password configured. Seeded initial admin {Email} with a generated DEVELOPMENT " +
            "password: {Password} — set your own in appsettings.Local.json.", email, password);
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
