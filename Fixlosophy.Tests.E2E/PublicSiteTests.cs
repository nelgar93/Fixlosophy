using System.Text.RegularExpressions;
using Microsoft.Playwright;

namespace Fixlosophy.Tests.E2E;

/// <summary>
/// The site as a visitor meets it: the pages load, the two render modes behave the way
/// they are meant to, and the phone menu works.
/// </summary>
public class PublicSiteTests(AppFixture app, BrowserFixture browsers) : E2ETest(app, browsers)
{
    [Theory]
    [InlineData("/")]
    [InlineData("/services")]
    [InlineData("/gallery")]
    [InlineData("/about")]
    [InlineData("/book")]
    public async Task PublicPages_LoadAndBecomeInteractive(string path)
    {
        // A page that renders but never attaches a circuit looks completely fine in a
        // screenshot and is completely dead to the touch. Waiting for the marker is what
        // makes this a real check rather than a "did it 200" check.
        await GotoInteractiveAsync(path);

        await Assertions.Expect(Page.Locator("h1").First).ToBeVisibleAsync();
    }

    [Fact]
    public async Task AuthPages_AreStaticallyRendered_WithNoCircuit()
    {
        // These opt out of interactive routing so their forms post as real requests that
        // can write the auth cookie. Asserting the marker is absent pins that decision:
        // if one of them ever silently became interactive, its form would stop posting
        // and sign-in would break in a way no unit test would notice.
        await GotoStaticAsync("/admin/login");

        await Assertions.Expect(Page.Locator("#adminlogin-email")).ToBeVisibleAsync();
        Assert.Equal(0, await Page.Locator("#blazor-ready").CountAsync());
    }

    [Fact]
    public async Task Admin_IsNotReachable_WithoutSigningIn()
    {
        await Page.GotoAsync("/admin");

        await Assertions.Expect(Page.Locator("#adminlogin-email")).ToBeVisibleAsync();
    }

    [Fact]
    public async Task PhoneMenu_OpensAndCloses()
    {
        await GotoInteractiveAsync("/");
        await Page.SetViewportSizeAsync(390, 844);

        var hamburger = Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Toggle navigation" });
        var links = Page.Locator(".nav-links");

        await Assertions.Expect(hamburger).ToBeVisibleAsync();
        await Assertions.Expect(links).Not.ToHaveClassAsync(new Regex("nav-links--open"));

        await hamburger.ClickAsync();
        await Assertions.Expect(links).ToHaveClassAsync(new Regex("nav-links--open"));

        // Following a link closes it again — the handler that does this is easy to lose
        // in a refactor, and the symptom is a menu left covering the page you navigated to.
        // Scoped to the menu: the footer carries its own "Services" link, and an
        // unscoped lookup is a strict-mode violation rather than a coin toss.
        await links.GetByRole(AriaRole.Link, new LocatorGetByRoleOptions { Name = "Services", Exact = true }).ClickAsync();
        await Assertions.Expect(links).Not.ToHaveClassAsync(new Regex("nav-links--open"));
    }

    [Fact]
    public async Task Pages_DoNotScrollSideways_OnAPhone()
    {
        await GotoInteractiveAsync("/book");
        await Page.SetViewportSizeAsync(390, 844);

        var overflow = await Page.EvaluateAsync<int>(
            "() => document.documentElement.scrollWidth - document.documentElement.clientWidth");

        Assert.True(overflow <= 0, $"The page scrolls {overflow}px sideways at 390px wide.");
    }
}
