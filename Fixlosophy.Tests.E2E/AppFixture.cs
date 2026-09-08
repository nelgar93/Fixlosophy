using System.Threading.RateLimiting;
using Fixlosophy.Data;
using Fixlosophy.Services;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Hosting.Server;
using Microsoft.AspNetCore.Hosting.Server.Features;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.AspNetCore.RateLimiting;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;

namespace Fixlosophy.Tests.E2E;

/// <summary>
/// Boots the real application on a real port, backed by an in-memory database, and
/// hands out its base URL.
/// </summary>
/// <remarks>
/// <para>
/// <b>Why a real port.</b> <see cref="WebApplicationFactory{TEntryPoint}"/> normally
/// runs on TestServer, which has no socket — an <see cref="System.Net.Http.HttpClient"/>
/// can talk to it, a browser cannot. Blazor Server also needs a real WebSocket for its
/// circuit. So this builds the TestServer host the factory insists on, then builds a
/// second host on Kestrel bound to port 0 and reads back whichever port the OS gave it.
/// The order matters: the address is only knowable after the Kestrel host has started.
/// </para>
/// <para>
/// <b>Why an in-memory database.</b> Postgres in CI would mean a service container, a
/// connection string and a secret, and Fixlosophy.Tests deliberately needs none of
/// those. What these tests are for is the UI, the circuit and the wiring between them;
/// what Npgsql does with a timestamp is already covered where it belongs. Trading
/// database fidelity for a suite that runs anywhere with no setup is the right way
/// round here — and it is the reason the E2E job needs nothing CI does not already have.
/// </para>
/// <para>
/// The store is shared by every test in the collection, so tests must not assume a
/// pristine database: use an address of your own rather than counting rows.
/// </para>
/// </remarks>
public sealed class AppFixture : WebApplicationFactory<Program>
{
    /// Seeded by the app's own first-run path, given a known password here so the admin
    /// journey can sign in as a real member of staff rather than faking a cookie.
    public const string AdminEmail = "e2e-admin@fixlosophy.com";

    public const string AdminPassword = "E2e-Admin-Password-1";

    private IHost? _kestrel;
    private string? _baseUrl;
    private int _hostsConfigured;

    /// <summary>What the app tried to email, instead of emailing it.</summary>
    /// <remarks>
    /// Shared by both hosts on purpose — unlike the database, there is nothing to keep
    /// apart here, and a test wants to see what the host the browser is talking to did.
    /// </remarks>
    public RecordingEmailSender Emails { get; } = new();

    /// <summary>
    /// The service provider of the host the browser actually talks to.
    /// </summary>
    /// <remarks>
    /// Not <see cref="WebApplicationFactory{TEntryPoint}.Services"/>: that belongs to the
    /// TestServer host, which runs alongside with a database of its own. Anything that
    /// wants to seed or inspect what the browser will see has to come through here.
    /// </remarks>
    public IServiceProvider ServerServices
    {
        get
        {
            _ = Services;
            return _kestrel?.Services ?? throw new InvalidOperationException("The Kestrel host has not been built.");
        }
    }

    /// <summary>The running app's origin, with a trailing slash.</summary>
    public string BaseUrl
    {
        get
        {
            // Touching Services is what forces the host to be built, and CreateHost
            // below is where the address gets captured.
            _ = Services;
            return _baseUrl ?? throw new InvalidOperationException("The Kestrel host did not report an address.");
        }
    }

    protected override void ConfigureWebHost(IWebHostBuilder builder)
    {
        // Production refuses to start with AllowedHosts "*" — a deliberate guard
        // against host-header injection into emailed links. Development is also what
        // seeds the demo bookings, so the dashboard has something in it.
        builder.UseEnvironment("Development");

        // Without these the first-run seeder mints a random admin password and logs it,
        // which no test can then use.
        builder.UseSetting("SeedAdmin:Email", AdminEmail);
        builder.UseSetting("SeedAdmin:Password", AdminPassword);

        builder.ConfigureServices(services =>
        {
            // Swap Npgsql for the in-memory provider.
            //
            // Removing DbContextOptions<AppDbContext> alone is not enough: since EF Core
            // 9, AddDbContext also leaves an IDbContextOptionsConfiguration<T> behind
            // holding the UseNpgsql call, and those configurations are additive. Leave
            // it in place and both providers end up configured on the same context,
            // which EF refuses with "Only a single database provider can be registered".
            // Matching on the name catches that type without naming an EF internal that
            // may be renamed again.
            foreach (var descriptor in services
                         .Where(d => d.ServiceType.FullName?.Contains("DbContextOptions", StringComparison.Ordinal) == true)
                         .ToList())
            {
                services.Remove(descriptor);
            }

            // One database per host, named once and captured. Both halves matter.
            //
            // Per host, because CreateHost below builds the app twice — on TestServer
            // because the factory requires it, and on Kestrel because a browser needs a
            // socket — and starting a deferred host is what runs Program.cs, so the
            // startup seeders run twice. Give both hosts the same store and their
            // "if anything is already there, stop" guards do not save them: the two
            // startups overlap, both look at an empty table, and both fill it. That
            // surfaces a long way from here, as a duplicate-key ArgumentException out of
            // a page that reasonably assumed service names were unique.
            //
            // Named out here rather than inside the options lambda, because that lambda
            // runs on every DbContext resolution — build the name in it and each scope
            // gets its own empty database, so startup seeds one store and the next
            // request reads a different one. The page still renders, just with nothing
            // on it, which is a far more confusing failure than a crash.
            var databaseName = $"fixlosophy-e2e-{Interlocked.Increment(ref _hostsConfigured)}";
            services.AddDbContext<AppDbContext>(o => o.UseInMemoryDatabase(databaseName));

            // Cut the two lines that reach the outside world.
            //
            // The app boots with the real appsettings.Local.json — unavoidable, since
            // that sits at the content root these tests need for static assets — and it
            // holds a working SMTP host and a Supabase service-role key. Program.cs
            // reads Smtp:Host to choose its sender before any configuration a test can
            // supply is in play, so it will have registered SmtpEmailSender by the time
            // this runs. Replacing the registration is what actually stops a booking
            // journey from putting real mail in a real inbox.
            services.RemoveAll<IEmailSender>();
            services.AddSingleton<IEmailSender>(Emails);

            services.RemoveAll<IStorageService>();
            services.AddSingleton<IStorageService, NoOpStorageService>();

            // Lift the per-IP request ceiling for the suite.
            //
            // The app allows 100 requests per 10 seconds from one address. Every test
            // here arrives from 127.0.0.1, and a Blazor page load is a dozen requests of
            // markup, stylesheets, collocated scripts and the circuit's own negotiate —
            // so a handful of tests in quick succession reads as one abusive client and
            // starts collecting 429s. The one that matters is /_blazor/initializers:
            // lose it and the circuit never starts, which surfaces as the readiness
            // marker never appearing, on whichever test happened to be running when the
            // window filled. Nothing to do with that test, and it moves between runs.
            //
            // Turning the limiter off here does not leave it untested — what it defends
            // against is a real client abusing a real deployment, which is a unit-level
            // concern, not something a browser suite can meaningfully assert. The
            // tighter "auth" policy is deliberately left alone: at five sign-ins a
            // minute it is well clear of what these tests do, and leaving it in force
            // means the sign-in journey still runs the way a customer's would.
            services.Configure<RateLimiterOptions>(options =>
                options.GlobalLimiter = PartitionedRateLimiter.Create<HttpContext, string>(
                    _ => RateLimitPartition.GetNoLimiter("e2e")));
        });
    }

    protected override IHost CreateHost(IHostBuilder builder)
    {
        // Build the TestServer host first, before the builder is switched over to
        // Kestrel. The factory's internals check that the host it gets back really is
        // the TestServer one, so this is the object that must be returned at the end.
        var testHost = builder.Build();

        // Port 0 asks the OS for a free port, so parallel runs and a developer's own
        // `dotnet run` on 5126/5127 never collide.
        builder.ConfigureWebHost(web => web.UseKestrel().UseUrls("http://127.0.0.1:0"));

        // Start Kestrel before the test host: with the minimal-hosting deferred builder
        // the server is not initialised enough to report its address until it has run.
        _kestrel = builder.Build();
        _kestrel.Start();

        var addresses = _kestrel.Services.GetRequiredService<IServer>()
            .Features.Get<IServerAddressesFeature>()
            ?? throw new InvalidOperationException("Kestrel exposed no addresses feature.");

        _baseUrl = addresses.Addresses.Last().TrimEnd('/') + "/";

        testHost.Start();
        return testHost;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _kestrel?.Dispose();
            _kestrel = null;
        }

        base.Dispose(disposing);
    }
}
