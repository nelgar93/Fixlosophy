using Microsoft.Playwright;

namespace Fixlosophy.Tests.E2E;

/// <summary>
/// Base for the end-to-end tests: a fresh browser context per test, and one way to
/// navigate that does not race the Blazor circuit.
/// </summary>
[Collection(E2ECollection.Name)]
public abstract class E2ETest : IAsyncLifetime
{
    /// <summary>
    /// How long to wait for a page to become interactive.
    /// </summary>
    /// <remarks>
    /// Generous on purpose. The first circuit after the app starts pays for JIT on top
    /// of everything else and can take seconds, where every later one is in the tens of
    /// milliseconds. Because the wait ends the moment the marker appears, a large ceiling
    /// costs nothing on the fast path — it only decides how bad a day the suite is
    /// willing to survive before it calls the page broken.
    /// </remarks>
    private const int CircuitTimeoutMs = 30_000;

    private readonly BrowserFixture _browsers;
    private readonly List<string> _httpFailures = [];
    private IBrowserContext _context = null!;

    protected AppFixture App { get; }

    protected IPage Page { get; private set; } = null!;

    protected E2ETest(AppFixture app, BrowserFixture browsers)
    {
        App = app;
        _browsers = browsers;
    }

    public async Task InitializeAsync()
    {
        _context = await _browsers.Browser.NewContextAsync(new BrowserNewContextOptions
        {
            BaseURL = App.BaseUrl,
            ViewportSize = new ViewportSize { Width = 1280, Height = 900 }
        });

        if (TracingEnabled)
        {
            await _context.Tracing.StartAsync(new TracingStartOptions
            {
                Screenshots = true,
                Snapshots = true,
                Sources = true
            });
        }

        Page = await _context.NewPageAsync();
        Page.Response += (_, response) =>
        {
            if (response.Status >= 400) _httpFailures.Add($"{response.Status} {response.Url}");
        };
    }

    public async Task DisposeAsync()
    {
        // A trace turns "it failed on CI and passes here" into a frame-by-frame replay
        // with the DOM at every step. Off by default because it is not free; set
        // E2E_TRACE=1 locally or on a re-run of a failed CI job.
        if (TracingEnabled)
        {
            Directory.CreateDirectory("traces");
            await _context.Tracing.StopAsync(new TracingStopOptions
            {
                Path = Path.Combine("traces", $"{GetType().Name}-{Guid.NewGuid():N}.zip")
            });
        }

        await _context.DisposeAsync();
    }

    private static bool TracingEnabled =>
        Environment.GetEnvironmentVariable("E2E_TRACE") is "1" or "true";

    /// <summary>
    /// Navigates to an interactive page and waits until its circuit is live.
    /// </summary>
    /// <remarks>
    /// <para>
    /// <b>Use this, not <c>Page.GotoAsync</c>, for anything with a button on it.</b>
    /// Blazor Server prerenders an interactive page as static HTML and attaches the
    /// circuit afterwards. In the gap the markup is complete and every control looks
    /// perfectly clickable — it simply has nothing behind it, so a click in that window
    /// is accepted by the browser and then dropped on the floor.
    /// </para>
    /// <para>
    /// Playwright's auto-waiting does not close this gap. Auto-waiting establishes that
    /// an element is attached, visible, stable and hit-testable, all of which are true
    /// of a prerendered button; it has no way to know whether the application behind it
    /// is listening yet. This is the single biggest source of flake in a Blazor Server
    /// suite, and it is why so many of them are pebbledashed with sleeps.
    /// </para>
    /// <para>
    /// A sleep is the wrong fix twice over: too short on a cold start, where the first
    /// circuit takes seconds, and wasted on every warm navigation after it. Waiting for
    /// a signal the app itself emits is both faster and correct — see
    /// <c>Components/Shared/InteractiveReady.razor</c>, which renders the marker only
    /// once <c>RendererInfo.IsInteractive</c> turns true.
    /// </para>
    /// </remarks>
    protected async Task GotoInteractiveAsync(string path)
    {
        await Page.GotoAsync(path);

        try
        {
            await Page.Locator("#blazor-ready").WaitForAsync(new LocatorWaitForOptions
            {
                // Attached, not Visible: the marker is deliberately hidden, since its job
                // is to be readable by a test without being a stray pixel on the page.
                State = WaitForSelectorState.Attached,
                Timeout = CircuitTimeoutMs
            });
        }
        catch (TimeoutException ex)
        {
            // A bare "timed out waiting for #blazor-ready" sends you looking at the
            // marker, which is almost never the problem. What went wrong is upstream —
            // a 429 from the app's own rate limiter, a 500 on the page, a script that
            // did not load — and the browser already knows. Say so.
            throw new TimeoutException(
                $"The circuit never attached on '{path}' within {CircuitTimeoutMs}ms. " +
                (_httpFailures.Count == 0
                    ? "No failed HTTP responses were seen, so suspect the page itself."
                    : $"Failed responses on this page: {string.Join("; ", _httpFailures)}."),
                ex);
        }
    }

    /// <summary>
    /// Navigates to a statically rendered page — the auth pages, which opt out of
    /// interactive routing so their forms post as real requests.
    /// </summary>
    /// <remarks>
    /// There is no circuit to wait for on these, and waiting for the readiness marker
    /// would time out rather than pass. Kept as its own method so choosing the wrong one
    /// is a visible mistake at the call site instead of a mystery timeout.
    /// </remarks>
    protected async Task GotoStaticAsync(string path) =>
        await Page.GotoAsync(path, new PageGotoOptions { WaitUntil = WaitUntilState.DOMContentLoaded });

    /// <summary>Signs in through the real staff login form and lands on /admin.</summary>
    protected async Task SignInAsAdminAsync()
    {
        await GotoStaticAsync("/admin/login");
        await Page.FillAsync("#adminlogin-email", AppFixture.AdminEmail);
        await Page.FillAsync("#adminlogin-password", AppFixture.AdminPassword);

        // The form is a real POST to /auth/staff-login, which writes the cookie and
        // redirects; the page it lands on is interactive, so wait for its circuit.
        await Page.ClickAsync("button[type=submit]");
        await Page.Locator("#blazor-ready").WaitForAsync(new LocatorWaitForOptions
        {
            State = WaitForSelectorState.Attached,
            Timeout = CircuitTimeoutMs
        });
    }
}
