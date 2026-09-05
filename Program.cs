using System.Globalization;
using System.Net;
using System.Security.Cryptography;
using System.Threading.RateLimiting;
using Fixlosophy.Components;
using Fixlosophy.Data;
using Fixlosophy.Services;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.AspNetCore.HttpOverrides;
using Microsoft.AspNetCore.Mvc;
using Microsoft.EntityFrameworkCore;

// Lets the DateTime values the app writes (see ShopClock — everything goes through it)
// be stored as Postgres "timestamp without time zone".
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

// The app runs behind nginx/Caddy in production, so the socket's remote address is
// always the proxy. Without this every per-IP rate-limit partition below collapses
// into a single bucket shared by the whole internet — which turns the 5-per-minute
// auth limit into a self-inflicted lockout rather than brute-force protection — and
// Request.Scheme reads "http", which corrupts the absolute links in verification and
// password-reset emails.
//
// KnownProxies defaults to loopback, which covers a proxy on the same host. Set
// ForwardedHeaders:KnownProxies (comma-separated) when the proxy is elsewhere; only
// addresses listed there are trusted to speak for the client, so an attacker can't
// spoof X-Forwarded-For to dodge the rate limiter.
builder.Services.Configure<ForwardedHeadersOptions>(options =>
{
    options.ForwardedHeaders = ForwardedHeaders.XForwardedFor | ForwardedHeaders.XForwardedProto;

    var configured = builder.Configuration["ForwardedHeaders:KnownProxies"];
    if (!string.IsNullOrWhiteSpace(configured))
    {
        options.KnownProxies.Clear();
        options.KnownIPNetworks.Clear();
        foreach (var entry in configured.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            if (IPAddress.TryParse(entry, out var address))
                options.KnownProxies.Add(address);
    }
});

builder.Services.AddHttpClient();
builder.Services.AddScoped<AvailabilityService>();
builder.Services.AddScoped<BookingService>();
builder.Services.AddScoped<AuthService>();
builder.Services.AddScoped<InflationService>();
builder.Services.AddScoped<ActionRateLimiter>();
builder.Services.AddSingleton<SiteImages>();
builder.Services.AddScoped<IStorageService, StorageService>();
builder.Services.AddScoped<BikeService>();
builder.Services.AddScoped<EnquiryService>();
builder.Services.AddScoped<NotificationService>();
builder.Services.AddScoped<CustomerService>();
builder.Services.AddScoped<CustomerImportService>();
// Error capture. The queue is the hand-off point between a request thread and the
// single writer; the provider turns every Error/Critical log record in the app — ours
// and the framework's — into a row without any call site knowing about it.
builder.Services.AddSingleton<ErrorLogBuffer>();
builder.Services.AddSingleton<ILoggerProvider>(sp =>
    new DatabaseLoggerProvider(sp.GetRequiredService<ErrorLogBuffer>()));
builder.Services.AddHostedService<ErrorLogWriter>();

builder.Services.AddScoped<MaintenanceJobs>();
// The timer that drives MaintenanceJobs. Hosted services start after the schema
// bootstrap below has finished, so its first tick always sees a seeded database.
builder.Services.AddHostedService<MaintenanceService>();
// Singleton: the in-process fan-out that lets an open dashboard hear about a new
// notification without polling. See NotificationHub for why a plain event suffices.
builder.Services.AddSingleton<NotificationHub>();

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

// Liveness for the deploy script and the uptime monitor. AddDbContextCheck opens a
// connection and runs a trivial query, which is the point: the failure worth catching
// is Supabase going away — a pooler hiccup, a connection-limit ceiling, a paused
// project — while this process stays perfectly healthy. systemd would still say
// "running", and only a check that touches the database says "not serving".
builder.Services.AddHealthChecks()
    .AddCheck<DatabaseHealthCheck>("database");

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

// The Data Protection keyring encrypts the auth cookie above and every antiforgery
// token. Left at its default it is generated in memory and thrown away on shutdown,
// which means every restart signs out every customer and staff member, and any form
// already open in a browser fails its next POST with a bare 400. On a host that
// restarts to deploy, that is not an edge case — it's every release.
//
// SetApplicationName pins the key derivation's discriminator, which otherwise comes
// from the content root path: move or rename the deployment directory and, without
// this, every previously issued cookie stops decrypting.
//
// Keys are protected by filesystem permissions rather than a certificate — the app
// runs as its own user on a single VPS, so `chmod 700` on the directory is the
// boundary. Encrypting them at rest would need a cert whose private key lives in the
// same place, which moves the problem rather than solving it.
var keyPath = builder.Configuration["DataProtection:KeyPath"];
if (!string.IsNullOrWhiteSpace(keyPath))
{
    // Fail at startup rather than on the first key write, which would otherwise
    // surface mid-request as a 500 on somebody's login.
    Directory.CreateDirectory(keyPath);
    var probe = Path.Combine(keyPath, ".write-probe");
    File.WriteAllText(probe, "");
    File.Delete(probe);

    builder.Services.AddDataProtection()
        .PersistKeysToFileSystem(new DirectoryInfo(keyPath))
        .SetApplicationName("Fixlosophy");
}
else if (!builder.Environment.IsDevelopment())
{
    // Same stance as AllowedHosts and SeedAdmin:Password below: an ephemeral keyring
    // is invisible until it logs everyone out, so refuse to start rather than run in
    // a state that looks fine and isn't.
    throw new InvalidOperationException(
        "DataProtection:KeyPath is not configured. Point it at a directory on durable " +
        "storage (e.g. \"/var/lib/fixlosophy/keys\") via the DataProtection__KeyPath " +
        "environment variable — without it every restart signs out every user.");
}
else
{
    // Development only: the in-memory default is fine, and writing a keyring into a
    // developer's working tree is worse than being signed out by a rebuild.
    builder.Services.AddDataProtection().SetApplicationName("Fixlosophy");
}

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

// AllowedHosts ships as "*" so local development works from any hostname. Left that
// way in production it accepts any Host header, which lets an attacker poison the
// absolute URLs BuildAbsoluteUrl derives from the request — including the ones in
// verification and password-reset emails. Fail fast instead, the same way an unset
// SeedAdmin:Password does.
if (!builder.Environment.IsDevelopment() &&
    builder.Configuration["AllowedHosts"] is null or "" or "*")
{
    throw new InvalidOperationException(
        "AllowedHosts is \"*\". Set it to the site's real hostname(s), semicolon-separated " +
        "(e.g. \"booking.fixlosophy.com;www.booking.fixlosophy.com\"), in appsettings.Local.json " +
        "or the ALLOWEDHOSTS environment variable.");
}

var app = builder.Build();

using (var scope = app.Services.CreateScope())
{
    var db     = scope.ServiceProvider.GetRequiredService<AppDbContext>();
    var config = scope.ServiceProvider.GetRequiredService<IConfiguration>();

    // Schema creation and seeding run under a Postgres advisory lock, so two
    // processes reaching this point together can't race. That happens whenever a
    // deploy starts the new instance before stopping the old one, and it would put
    // two writers through CREATE TABLE, ALTER TABLE, CREATE INDEX and the seeders at
    // once — where the loser doesn't get a clean "already exists", it gets a
    // duplicate-key or a deadlock, mid-startup.
    //
    // Everything inside stays idempotent regardless: the lock orders the two
    // instances, and idempotency is what makes the second one's pass a no-op.
    //
    // Recurring work — the annual price increase, notification retention, appointment
    // reminders — deliberately does NOT live here any more; see MaintenanceService.
    // Startup ran each exactly once, which is wrong for anything that needs to happen
    // on a schedule: a server that stays up from March to May never applied the April
    // increase. Hosted services start after this block completes, so the seeded price
    // list is always in place before MaintenanceService's first pass reads it.
    await WithSchemaLockAsync(db, app.Logger, () =>
    {
        EnsureSchema(db, app.Logger);
        SeedServicePricings(db);
        // Development only. These are five invented customers with invented emails and
        // phone numbers; seeding them outside dev drops fake bookings straight into the
        // live dashboard on first deploy, indistinguishable from real ones.
        if (app.Environment.IsDevelopment())
            SeedDemoData(db);
        SeedDefaultAdmin(db, config, app.Logger, app.Environment.IsDevelopment());
    });
}

// Must run before anything that reads the client IP or the request scheme — which
// means before the rate limiter and before HTTPS redirection.
app.UseForwardedHeaders();

if (!app.Environment.IsDevelopment())
{
    app.UseExceptionHandler("/Error", createScopeForErrors: true);
    app.UseHsts();
}
app.UseStatusCodePagesWithReExecute("/not-found", createScopeForStatusCodePages: true);
app.UseHttpsRedirection();

// Security response headers. Placed before the endpoints so they apply to every
// response, including error pages and static assets.
app.Use(async (context, next) =>
{
    var headers = context.Response.Headers;
    headers["X-Content-Type-Options"] = "nosniff";
    headers["Referrer-Policy"]        = "strict-origin-when-cross-origin";
    // The site has no need for any of these, and denying them by default means a
    // future dependency can't quietly start asking for them.
    headers["Permissions-Policy"]     = "camera=(), microphone=(), geolocation=(), payment=(), usb=(), interest-cohort=()";
    // frame-ancestors in the CSP below supersedes this for modern browsers; kept for
    // older ones that don't implement it.
    headers["X-Frame-Options"]        = "DENY";

    // script-src no longer allows 'unsafe-inline'. Getting there took three things:
    // the image fallbacks moved from inline onerror= attributes to a delegated
    // listener in site.js, the JSON-LD block below carries the nonce minted here, and
    // <ImportMap /> came out of App.razor — an import map is the one inline script a
    // nonce can't cover, so it was the piece actually forcing the exception.
    //
    // 'unsafe-inline' does stay in style-src: Blazor's own reconnect/error UI carries
    // inline style attributes, and there is no nonce hook for framework-emitted
    // markup. That's a far weaker exposure than script — it can restyle a page, not
    // execute on it.
    //
    // A nonce is per-response and unguessable, so injected markup can't carry a valid
    // one. Note browsers ignore 'unsafe-inline' whenever a nonce is present, so the
    // two can't be listed as a fallback pair — this is all-or-nothing.
    //
    // Supabase is allowed for images (site photography, signed customer-upload URLs)
    // and connect-src (storage API); wss:/ws: are the SignalR circuit.
    var nonce = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
    context.Items[Fixlosophy.Components.App.CspNonceKey] = nonce;

    headers["Content-Security-Policy"] = string.Join("; ",
        "default-src 'self'",
        "base-uri 'self'",
        "object-src 'none'",
        "frame-ancestors 'none'",
        "form-action 'self'",
        "img-src 'self' data: https://*.supabase.co",
        "font-src 'self' data:",
        "style-src 'self' 'unsafe-inline'",
        $"script-src 'self' 'nonce-{nonce}'",
        "connect-src 'self' https://*.supabase.co wss: ws:");

    await next();
});

app.UseRateLimiter();

app.UseAuthentication();
app.UseAuthorization();

app.UseAntiforgery();

// 200 "Healthy" when the database answers, 503 when it doesn't. Deliberately outside
// the rate limiter: a probe every ten seconds must not eat a real visitor's budget,
// and a health check that starts 429ing under load reports an outage that isn't one.
// It's in robots.txt's disallow list too — no reason for it in a search index.
app.MapHealthChecks("/health").DisableRateLimiting();

app.MapStaticAssets();
app.MapRazorComponents<App>()
    .AddInteractiveServerRenderMode();

// ── SEO ───────────────────────────────────────────────────────────────────
// Generated rather than static files in wwwroot, so both track whatever domain the
// app is actually served from (App:BaseUrl, or the request host) — the domain isn't
// fixed yet, and a sitemap advertising the wrong one is worse than none.

// Public, indexable routes. Anything touching an account or the dashboard is
// deliberately absent and disallowed below.
string[] publicRoutes = ["/", "/services", "/about", "/gallery", "/book"];

// /contact was folded into /about, which now carries the shop's details and the
// enquiry form. Permanent rather than a deleted route: the address is on printed
// cards and in Google's index, and a literal route beats the Blazor fallback.
app.MapGet("/contact", () => Results.Redirect("/about#contact", permanent: true));

app.MapGet("/robots.txt", (HttpContext http, IConfiguration config) =>
{
    var baseUrl = BuildAbsoluteUrl(http, config, "");
    var body = string.Join('\n',
        "User-agent: *",
        "Allow: /",
        // Staff and customer areas: nothing to index, and the sign-in pages
        // shouldn't surface in search results.
        "Disallow: /admin",
        "Disallow: /account",
        "Disallow: /auth/",
        "Disallow: /reset-password",
        "Disallow: /not-found",
        "Disallow: /Error",
        "Disallow: /health",
        "",
        $"Sitemap: {baseUrl}/sitemap.xml",
        "");
    return Results.Text(body, "text/plain");
});

app.MapGet("/sitemap.xml", (HttpContext http, IConfiguration config) =>
{
    var baseUrl = BuildAbsoluteUrl(http, config, "");
    var today = ShopClock.Today.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);

    var urls = string.Concat(publicRoutes.Select(route =>
    {
        var loc = System.Security.SecurityElement.Escape($"{baseUrl}{route}");
        // The booking page and the homepage are the ones that matter commercially.
        var priority = route switch { "/" => "1.0", "/book" => "0.9", _ => "0.7" };
        return $"  <url><loc>{loc}</loc><lastmod>{today}</lastmod><priority>{priority}</priority></url>\n";
    }));

    var xml = "<?xml version=\"1.0\" encoding=\"UTF-8\"?>\n" +
              "<urlset xmlns=\"http://www.sitemaps.org/schemas/sitemap/0.9\">\n" +
              urls +
              "</urlset>\n";
    return Results.Text(xml, "application/xml");
});

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

// UK GDPR right of access. A plain HTTP GET rather than an in-circuit action because
// a Blazor Server circuit can't hand the browser a file without JS interop. Reads the
// customer id from the auth cookie — never from a parameter — so it can only ever
// return the caller's own data.
app.MapGet("/account/export", (HttpContext http, AuthService auth) =>
{
    // Deliberately not [Authorize]: the cookie scheme's LoginPath is the *staff*
    // sign-in, so the framework's challenge would drop a customer on /admin/login —
    // a dead end with no link across, which is the same trap RedirectToLogin.razor
    // exists to avoid for the Blazor pages.
    var user = http.User;
    if (user.FindFirst(AuthClaims.UserType)?.Value != AuthClaims.CustomerType)
        return Results.Redirect("/account/login?returnUrl=%2Faccount");

    var id = user.FindFirst(System.Security.Claims.ClaimTypes.NameIdentifier)?.Value;
    if (id is null) return Results.Redirect("/account/login?returnUrl=%2Faccount");

    var export = auth.ExportCustomerData(id);
    if (export is null) return Results.NotFound();

    // Content-Disposition so the browser saves it rather than rendering JSON in a
    // tab — the account page presents this as "Download my data".
    var json = System.Text.Json.JsonSerializer.SerializeToUtf8Bytes(export,
        new System.Text.Json.JsonSerializerOptions { WriteIndented = true });

    return Results.File(json, "application/json",
        fileDownloadName: $"fixlosophy-my-data-{ShopClock.Today:yyyy-MM-dd}.json");
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

// Only the token's hash is stored, so the link carries the email too — that's what
// identifies which Customer row to check the hash against.
static string BuildVerifyEmailLink(HttpContext http, IConfiguration config, string email, string token) =>
    BuildAbsoluteUrl(http, config,
        $"/auth/verify-email?email={Uri.EscapeDataString(email)}&token={Uri.EscapeDataString(token)}");

/// <summary>
/// Runs the schema bootstrap holding a Postgres session-level advisory lock, so only
/// one process at a time can be creating tables and seeding.
/// </summary>
/// <remarks>
/// Session-level (<c>pg_advisory_lock</c>) rather than transaction-level, because the
/// bootstrap is a series of separate statements with their own implicit transactions —
/// a transaction-scoped lock would be released after the first one. The connection is
/// held open for the duration so every statement runs in the same session; EF Core
/// ref-counts <c>OpenConnection</c>, so the nested open/close inside
/// <see cref="BookingService.NextReferenceSequence"/> can't close it underneath us.
///
/// If the process dies mid-bootstrap the lock goes with the session, so there is
/// nothing to clean up by hand — the reason this beats a lock row in a table.
///
/// Waiting is the correct behaviour for the loser: the winner is doing the very work
/// it was about to do, and the idempotent statements make its own pass a no-op
/// afterwards. The bound exists only so a genuinely stuck peer fails the deploy with
/// a clear message instead of hanging a container that never reports ready.
/// </remarks>
static async Task WithSchemaLockAsync(AppDbContext db, ILogger logger, Action bootstrap)
{
    // The test suite's InMemory provider has no advisory locks — and no concurrency
    // to protect against either.
    if (!db.Database.IsRelational())
    {
        bootstrap();
        return;
    }

    // Arbitrary but stable 64-bit key. Advisory locks share one namespace per
    // database, so this only needs to not collide with anything else using the same
    // Postgres instance.
    const long schemaLockKey = 0x_46_49_58_4C_4F_53_01;  // "FIXLOS" + slot 1
    var waitedFor = TimeSpan.Zero;
    var pollEvery = TimeSpan.FromSeconds(1);
    var giveUpAfter = TimeSpan.FromSeconds(60);

    await db.Database.OpenConnectionAsync();
    try
    {
        while (true)
        {
            var acquired = await TryAcquireAsync(db, schemaLockKey);
            if (acquired) break;

            if (waitedFor >= giveUpAfter)
                throw new InvalidOperationException(
                    $"Could not acquire the schema bootstrap lock within {giveUpAfter.TotalSeconds:0}s — " +
                    "another instance appears to be stuck partway through startup. Check for an " +
                    "orphaned process still holding a connection to this database.");

            if (waitedFor == TimeSpan.Zero)
                logger.LogInformation(
                    "Another instance is running the schema bootstrap — waiting for it to finish.");

            await Task.Delay(pollEvery);
            waitedFor += pollEvery;
        }

        try
        {
            bootstrap();
        }
        finally
        {
            await db.Database.ExecuteSqlRawAsync(
                "SELECT pg_advisory_unlock({0})", schemaLockKey);
        }
    }
    finally
    {
        await db.Database.CloseConnectionAsync();
    }

    // pg_try_advisory_lock returns immediately with whether it got the lock, which is
    // what lets the wait above stay bounded — pg_advisory_lock would block forever.
    static async Task<bool> TryAcquireAsync(AppDbContext db, long key)
    {
        var connection = db.Database.GetDbConnection();
        using var cmd = connection.CreateCommand();
        cmd.CommandText = "SELECT pg_try_advisory_lock(@key)";
        var parameter = cmd.CreateParameter();
        parameter.ParameterName = "key";
        parameter.Value = key;
        cmd.Parameters.Add(parameter);
        return (bool)(await cmd.ExecuteScalarAsync())!;
    }
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

        -- Verification tokens live here alongside the reset-token columns above.
        -- (They briefly lived in Redis; that dependency is gone — see AuthService.)
        ALTER TABLE ""Customers"" ADD COLUMN IF NOT EXISTS ""VerificationTokenHash""      text      NULL;
        ALTER TABLE ""Customers"" ADD COLUMN IF NOT EXISTS ""VerificationTokenExpiresAt"" timestamp NULL;
        ALTER TABLE ""Customers"" ADD COLUMN IF NOT EXISTS ""VerificationCooldownUntil""  timestamp NULL;
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

        -- Day-before reminders. NULL means ""not sent yet"", which is the correct
        -- grandfathering for rows that predate the feature: the job only ever looks at
        -- tomorrow's bookings, so old rows are never picked up regardless.
        ALTER TABLE ""Bookings"" ADD COLUMN IF NOT EXISTS ""ReminderSentAt""    timestamp   NULL;

        -- When staff were told the bike hadn't turned up. Same purpose as
        -- ReminderSentAt: ring the bell once, not on every tick for the rest of the day.
        ALTER TABLE ""Bookings"" ADD COLUMN IF NOT EXISTS ""LateNotifiedAt""    timestamp   NULL;

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

        -- Messages from the /contact form. Stored as well as emailed so an SMTP
        -- outage costs a notification rather than the enquiry itself.
        CREATE TABLE IF NOT EXISTS ""Enquiries"" (
            ""Id""              varchar(36) NOT NULL,
            ""CreatedAt""       timestamp   NOT NULL DEFAULT now(),
            ""Name""            text        NOT NULL DEFAULT '',
            ""Email""           text        NOT NULL DEFAULT '',
            ""Phone""           text        NOT NULL DEFAULT '',
            ""Service""         text        NOT NULL DEFAULT '',
            ""BikeDescription"" text        NOT NULL DEFAULT '',
            ""Message""         text        NOT NULL DEFAULT '',
            ""PreferredDate""   timestamp   NULL,
            ""HandledAt""       timestamp   NULL,
            CONSTRAINT ""PK_Enquiries"" PRIMARY KEY (""Id"")
        );
        CREATE INDEX IF NOT EXISTS ""IX_Enquiries_CreatedAt"" ON ""Enquiries"" (""CreatedAt"" DESC);

        -- Staff notifications (new booking, cancellation, enquiry — and later, stock).
        -- TargetStaffId NULL means every staff member sees it. No FK to Staff: a
        -- notification is a historical record and should survive the member leaving.
        CREATE TABLE IF NOT EXISTS ""Notifications"" (
            ""Id""            varchar(36) NOT NULL,
            ""Type""          integer     NOT NULL DEFAULT 0,
            ""CreatedAt""     timestamp   NOT NULL DEFAULT now(),
            ""Title""         text        NOT NULL DEFAULT '',
            ""Body""          text        NOT NULL DEFAULT '',
            ""LinkUrl""       text        NOT NULL DEFAULT '',
            ""TargetStaffId"" varchar(36) NULL,
            ""ReadAt""        timestamp   NULL,
            CONSTRAINT ""PK_Notifications"" PRIMARY KEY (""Id"")
        );
        CREATE INDEX IF NOT EXISTS ""IX_Notifications_CreatedAt""  ON ""Notifications"" (""CreatedAt"" DESC);
        CREATE INDEX IF NOT EXISTS ""IX_Notifications_Target""     ON ""Notifications"" (""TargetStaffId"");

        -- Read state, one row per (notification, staff member). The row existing IS
        -- the read state, so nothing ever updates it.
        --
        -- This replaces a ""ReadAt"" column on Notifications, which was wrong: a
        -- broadcast is a single row shared by all staff, so one person marking it
        -- read cleared everybody's badge.
        CREATE TABLE IF NOT EXISTS ""NotificationReads"" (
            ""NotificationId"" varchar(36) NOT NULL,
            ""StaffId""        varchar(36) NOT NULL,
            ""ReadAt""         timestamp   NOT NULL DEFAULT now(),
            CONSTRAINT ""PK_NotificationReads"" PRIMARY KEY (""NotificationId"", ""StaffId"")
        );

        DO $$ BEGIN
            IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_NotificationReads_Notifications') THEN
                ALTER TABLE ""NotificationReads"" ADD CONSTRAINT ""FK_NotificationReads_Notifications""
                    FOREIGN KEY (""NotificationId"") REFERENCES ""Notifications""(""Id"") ON DELETE CASCADE;
            END IF;
        END $$;

        -- One-time migration off the old shared column. Anything previously marked
        -- read becomes read for every staff member who could see it, then the column
        -- goes — which is what stops this block running again on the next startup.
        DO $$ BEGIN
            IF EXISTS (
                SELECT 1 FROM information_schema.columns
                WHERE table_name = 'Notifications' AND column_name = 'ReadAt'
            ) THEN
                INSERT INTO ""NotificationReads"" (""NotificationId"", ""StaffId"", ""ReadAt"")
                SELECT n.""Id"", s.""Id"", n.""ReadAt""
                FROM ""Notifications"" n
                CROSS JOIN ""Staff"" s
                WHERE n.""ReadAt"" IS NOT NULL
                  AND (n.""TargetStaffId"" IS NULL OR n.""TargetStaffId"" = s.""Id"")
                ON CONFLICT DO NOTHING;

                ALTER TABLE ""Notifications"" DROP COLUMN ""ReadAt"";
            END IF;
        END $$;

        -- Notes staff write about a customer, usually on completing a job. Both FKs
        -- are SET NULL: the note is the shop's record and must outlive the booking or
        -- the account it was written against.
        CREATE TABLE IF NOT EXISTS ""CustomerNotes"" (
            ""Id""                varchar(36) NOT NULL,
            ""CustomerId""        varchar(36) NULL,
            ""BookingId""         varchar(36) NULL,
            ""AuthorStaffId""     varchar(36) NULL,
            ""CreatedAt""         timestamp   NOT NULL DEFAULT now(),
            ""Body""              text        NOT NULL DEFAULT '',
            ""VisibleToCustomer"" boolean     NOT NULL DEFAULT false,
            CONSTRAINT ""PK_CustomerNotes"" PRIMARY KEY (""Id"")
        );
        CREATE INDEX IF NOT EXISTS ""IX_CustomerNotes_Customer"" ON ""CustomerNotes"" (""CustomerId"", ""CreatedAt"" DESC);
        CREATE INDEX IF NOT EXISTS ""IX_CustomerNotes_Booking""  ON ""CustomerNotes"" (""BookingId"");

        -- Application errors, grouped by fingerprint rather than one row per
        -- occurrence — see ErrorLogEntry. Written only by ErrorLogWriter. There is no
        -- admin screen for this on purpose: it's read in Supabase's table editor when
        -- something needs diagnosing, and a bike shop's dashboard has no business
        -- carrying a stack-trace viewer.
        CREATE TABLE IF NOT EXISTS ""ErrorLog"" (
            ""Id""               varchar(36) NOT NULL,
            ""Fingerprint""      varchar(32) NOT NULL,
            ""Level""            text        NOT NULL DEFAULT '',
            ""Logger""           text        NOT NULL DEFAULT '',
            ""MessageTemplate""  text        NOT NULL DEFAULT '',
            ""LastMessage""      text        NOT NULL DEFAULT '',
            ""ExceptionType""    text        NULL,
            ""ExceptionMessage"" text        NULL,
            ""StackTrace""       text        NULL,
            ""FirstSeen""        timestamp   NOT NULL DEFAULT now(),
            ""LastSeen""         timestamp   NOT NULL DEFAULT now(),
            ""Count""            integer     NOT NULL DEFAULT 1,
            CONSTRAINT ""PK_ErrorLog"" PRIMARY KEY (""Id"")
        );
        CREATE UNIQUE INDEX IF NOT EXISTS ""IX_ErrorLog_Fingerprint"" ON ""ErrorLog"" (""Fingerprint"");
        CREATE INDEX IF NOT EXISTS ""IX_ErrorLog_LastSeen"" ON ""ErrorLog"" (""LastSeen"" DESC);

        -- Days the shop is shut. Customer-facing: the reason is shown on the booking
        -- calendar, because ""Closed — Christmas"" and ""fully booked"" are different
        -- answers and only one means try tomorrow. Both dates inclusive.
        CREATE TABLE IF NOT EXISTS ""Closures"" (
            ""Id""               varchar(36) NOT NULL,
            ""StartDate""        timestamp   NOT NULL,
            ""EndDate""          timestamp   NOT NULL,
            ""StartTime""        varchar(5)  NULL,
            ""EndTime""          varchar(5)  NULL,
            ""Reason""           text        NOT NULL DEFAULT '',
            ""CreatedAt""        timestamp   NOT NULL DEFAULT now(),
            ""CreatedByStaffId"" varchar(36) NULL,
            CONSTRAINT ""PK_Closures"" PRIMARY KEY (""Id"")
        );
        CREATE INDEX IF NOT EXISTS ""IX_Closures_Range"" ON ""Closures"" (""StartDate"", ""EndDate"");

        -- A mechanic being away. Internal only — a customer sees a closed day, never
        -- whose holiday it is. Availability treats it like a closure in exactly one
        -- respect: a day with nobody in can't take a booking.
        CREATE TABLE IF NOT EXISTS ""StaffAbsences"" (
            ""Id""        varchar(36) NOT NULL,
            ""StaffId""   varchar(36) NOT NULL,
            ""StartDate"" timestamp   NOT NULL,
            ""EndDate""   timestamp   NOT NULL,
            ""Type""      integer     NOT NULL DEFAULT 0,
            ""Note""      text        NOT NULL DEFAULT '',
            ""CreatedAt"" timestamp   NOT NULL DEFAULT now(),
            CONSTRAINT ""PK_StaffAbsences"" PRIMARY KEY (""Id"")
        );
        CREATE INDEX IF NOT EXISTS ""IX_StaffAbsences_Range"" ON ""StaffAbsences"" (""StartDate"", ""EndDate"");

        DO $$ BEGIN
            IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_StaffAbsences_Staff') THEN
                ALTER TABLE ""StaffAbsences"" ADD CONSTRAINT ""FK_StaffAbsences_Staff""
                    FOREIGN KEY (""StaffId"") REFERENCES ""Staff""(""Id"") ON DELETE CASCADE;
            END IF;
        END $$;

        -- Whether someone actually works on bikes. DEFAULT true grandfathers existing
        -- staff: before this column there was no such distinction, and every one of
        -- them was in the workshop. Untick the office ones afterwards.
        ALTER TABLE ""Staff"" ADD COLUMN IF NOT EXISTS ""IsMechanic"" boolean NOT NULL DEFAULT true;

        DO $$ BEGIN
            IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_CustomerNotes_Customers') THEN
                ALTER TABLE ""CustomerNotes"" ADD CONSTRAINT ""FK_CustomerNotes_Customers""
                    FOREIGN KEY (""CustomerId"") REFERENCES ""Customers""(""Id"") ON DELETE SET NULL;
            END IF;
            IF NOT EXISTS (SELECT 1 FROM pg_constraint WHERE conname = 'FK_CustomerNotes_Bookings') THEN
                ALTER TABLE ""CustomerNotes"" ADD CONSTRAINT ""FK_CustomerNotes_Bookings""
                    FOREIGN KEY (""BookingId"") REFERENCES ""Bookings""(""Id"") ON DELETE SET NULL;
            END IF;
        END $$;

        -- Indexes behind the admin dashboard's filters and the availability queries.
        -- Without these every filter tab and every calendar month is a sequential scan.
        CREATE INDEX IF NOT EXISTS ""IX_Bookings_SlotDate""        ON ""Bookings"" (""SlotDate"");
        CREATE INDEX IF NOT EXISTS ""IX_Bookings_Status""          ON ""Bookings"" (""Status"");
        CREATE INDEX IF NOT EXISTS ""IX_Bookings_AssignedStaffId"" ON ""Bookings"" (""AssignedStaffId"");
        CREATE INDEX IF NOT EXISTS ""IX_Bookings_CustomerId""      ON ""Bookings"" (""CustomerId"");

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
        ALTER TABLE ""Enquiries""        ENABLE ROW LEVEL SECURITY;
        ALTER TABLE ""Notifications""     ENABLE ROW LEVEL SECURITY;
        ALTER TABLE ""NotificationReads"" ENABLE ROW LEVEL SECURITY;
        ALTER TABLE ""CustomerNotes""     ENABLE ROW LEVEL SECURITY;

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

    // Capacity, enforced where it can't be raced: one active booking per slot. The
    // count-then-insert check in CreateBooking is read-committed, so two customers
    // confirming the same empty slot at once both read zero and both succeed — their
    // emails differ, so the duplicate index above never fires. This one does.
    //
    // Only correct while MaxPerSlot is 1; a unique index cannot express "at most N".
    // Same separate-try treatment as above, so a slot that already holds two bookings
    // degrades to a startup warning naming the clashes rather than blocking boot.
    try
    {
        db.Database.ExecuteSqlRaw(@"
            CREATE UNIQUE INDEX IF NOT EXISTS ""IX_Bookings_OneBookingPerSlot""
            ON ""Bookings"" (""SlotDate"", ""SlotTime"")
            WHERE ""Status"" <> 4;");
    }
    catch (Exception ex)
    {
        var clashes = db.Bookings
            .Where(b => b.Status != BookingStatus.Cancelled)
            .GroupBy(b => new { b.SlotDate, b.SlotTime })
            .Where(g => g.Count() > 1)
            .Select(g => new { g.Key.SlotDate, g.Key.SlotTime, Count = g.Count() })
            .ToList();

        logger.LogWarning(ex,
            "Could not create IX_Bookings_OneBookingPerSlot — {Count} slot(s) already hold more " +
            "than one active booking, so overbooking is not yet enforced by the database. " +
            "Move or cancel one booking from each of: {Slots}",
            clashes.Count,
            string.Join(", ", clashes.Select(c => $"{c.SlotDate:yyyy-MM-dd} {c.SlotTime} ({c.Count})")));
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
                    -- setval's floor is 1, so a count of 0 threw and took the whole
                    -- CREATE down with it: on a brand-new database the sequence was
                    -- never created and startup then failed on the missing relation.
                    -- is_called=false makes the first nextval return 1 rather than 2.
                    PERFORM setval('""BookingReferenceSeq""',
                                   GREATEST((SELECT COUNT(*) FROM ""Bookings""), 1),
                                   (SELECT COUNT(*) FROM ""Bookings"") > 0);
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

static void SeedDemoData(AppDbContext db)
{
    if (db.Bookings.Any()) return;

    var today    = ShopClock.Today;
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
            CreatedAt       = ShopClock.Now.AddHours(-i * 3),
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
