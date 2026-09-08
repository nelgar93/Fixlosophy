using Microsoft.Playwright;

namespace Fixlosophy.Tests.E2E;

/// <summary>
/// One browser for the whole suite. Launching is the expensive part; contexts are not,
/// so each test gets its own context (see <see cref="E2ETest"/>) and they share this.
/// </summary>
/// <remarks>
/// Which browser depends on the machine, the same arrangement the run-fixlosophy skill
/// uses. Unset, it drives the <b>Edge already installed</b> on the box, so a Windows
/// dev machine needs no download and no <c>playwright install</c>. CI sets
/// <c>E2E_BROWSER_CHANNEL</c> to empty and installs the bundled Chromium instead, which
/// is the one thing available on a clean Linux runner.
/// </remarks>
public sealed class BrowserFixture : IAsyncLifetime
{
    private IPlaywright? _playwright;

    public IBrowser Browser { get; private set; } = null!;

    public async Task InitializeAsync()
    {
        _playwright = await Playwright.CreateAsync();

        var channel = Environment.GetEnvironmentVariable("E2E_BROWSER_CHANNEL") ?? "msedge";
        var executable = Environment.GetEnvironmentVariable("E2E_BROWSER_EXECUTABLE");

        // "none" rather than an empty string as the way to ask for Playwright's own
        // Chromium: an empty environment variable reads back as "" on Linux and as null
        // on Windows, so a blank value would mean different things on the two machines
        // this has to run on.
        var useChannel = !string.IsNullOrEmpty(channel)
                         && !channel.Equals("none", StringComparison.OrdinalIgnoreCase);

        Browser = await _playwright.Chromium.LaunchAsync(new BrowserTypeLaunchOptions
        {
            Headless = true,
            // An explicit executable wins, then a channel, then the bundled download.
            ExecutablePath = string.IsNullOrEmpty(executable) ? null : executable,
            Channel = string.IsNullOrEmpty(executable) && useChannel ? channel : null
        });
    }

    public async Task DisposeAsync()
    {
        if (Browser is not null) await Browser.DisposeAsync();
        _playwright?.Dispose();
    }
}

/// <summary>
/// Binds the app and the browser to one xUnit collection, so the site boots once and
/// the browser launches once for every test class in the suite.
/// </summary>
[CollectionDefinition(Name)]
public sealed class E2ECollection : ICollectionFixture<AppFixture>, ICollectionFixture<BrowserFixture>
{
    public const string Name = "Fixlosophy end-to-end";
}
