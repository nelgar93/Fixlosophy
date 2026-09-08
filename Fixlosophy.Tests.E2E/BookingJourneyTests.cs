using Fixlosophy.Services;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Playwright;

namespace Fixlosophy.Tests.E2E;

/// <summary>
/// The journeys that only a browser can prove: a real circuit, a real cookie, a real
/// round trip per click.
/// </summary>
/// <remarks>
/// Kept deliberately few. Each of these is seconds where a bUnit test is milliseconds,
/// so the rule for adding one is that it has to cross a boundary the component tests
/// cannot: the SignalR circuit, the auth cookie, a page-to-page hand-off. Anything that
/// is really about the wizard's own logic belongs in
/// <c>Fixlosophy.Tests/BookWizardTests.cs</c>, where it runs a thousand times faster.
/// </remarks>
public class BookingJourneyTests(AppFixture app, BrowserFixture browsers) : E2ETest(app, browsers)
{
    private ILocator Forward => Page.Locator(".booking-nav .btn-primary");

    /// <summary>
    /// Books a repair the way a customer would, and returns the reference shown at the
    /// end.
    /// </summary>
    /// <remarks>
    /// Not one Playwright wait or sleep in here, which is the point of the readiness
    /// marker: once the circuit is live, auto-waiting handles the rest on its own,
    /// because every subsequent delay is a normal "the element is not ready yet" that
    /// Playwright was built to ride out.
    /// </remarks>
    private async Task<string> BookARepairAsync(string name, string email, string phone)
    {
        await GotoInteractiveAsync("/book");

        await Page.Locator(".service-pick-card").First.ClickAsync();
        await Forward.ClickAsync();

        // Next month rather than this one: how much of the current month is still
        // bookable depends on today's date, and on the 30th the answer can be nothing.
        await Page.Locator("button[aria-label='Next month']").ClickAsync();
        await Page.Locator(".cal-day--available").First.ClickAsync();
        await Page.Locator(".timeslot").First.ClickAsync();
        await Forward.ClickAsync();

        await Page.FillAsync("#booking-name", name);
        await Page.FillAsync("#booking-email", email);
        await Page.FillAsync("#booking-phone", phone);
        await Forward.ClickAsync();

        await Forward.ClickAsync();
        return (await Page.Locator(".confirmed-ref strong").InnerTextAsync()).Trim();
    }

    [Fact]
    public async Task Guest_CanBookARepair_FromEndToEnd()
    {
        var email = $"guest-{Guid.NewGuid():N}@example.com";

        var reference = await BookARepairAsync("Guest Rider", email, "07700 900111");

        Assert.StartsWith("FIX-", reference, StringComparison.Ordinal);
        Assert.Contains(App.Emails.Confirmations, b => b.CustomerEmail == email);
        Assert.Contains(App.Emails.ShopNotifications, b => b.CustomerEmail == email);
    }

    [Fact]
    public async Task Book_RejectsBadDetails_OverTheCircuit()
    {
        // The same rule the component tests cover, asserted once here for a different
        // reason: to prove the validation actually makes it back to the browser. A
        // server-side error message is only useful if the round trip renders it.
        await GotoInteractiveAsync("/book");

        await Page.Locator(".service-pick-card").First.ClickAsync();
        await Forward.ClickAsync();
        await Page.Locator("button[aria-label='Next month']").ClickAsync();
        await Page.Locator(".cal-day--available").First.ClickAsync();
        await Page.Locator(".timeslot").First.ClickAsync();
        await Forward.ClickAsync();

        await Page.FillAsync("#booking-name", "Guest Rider");
        await Page.FillAsync("#booking-email", "@");
        await Page.FillAsync("#booking-phone", "07700 900111");
        await Forward.ClickAsync();

        await Assertions.Expect(Page.Locator(".booking-error")).ToContainTextAsync("valid email");
        await Assertions.Expect(Page.Locator(".review-grid")).Not.ToBeVisibleAsync();
    }

    [Fact]
    public async Task StaffCanSeeTheBooking_ACustomerJustMade()
    {
        // The one that earns its seconds. It spans the customer's circuit, a real
        // sign-in POST that writes a cookie, and the dashboard's own circuit reading
        // what the first one wrote — three things no component test shares a process
        // with, let alone a session.
        var email = $"guest-{Guid.NewGuid():N}@example.com";
        var reference = await BookARepairAsync("Handover Rider", email, "07700 900222");

        await SignInAsAdminAsync();

        // The dashboard opens on Calendar; the reference lives on Bookings. Clicking a
        // tab is itself a circuit round trip, so this doubles as proof the dashboard is
        // interactive and not just prerendered.
        await Page.GetByRole(AriaRole.Button, new PageGetByRoleOptions { Name = "Bookings", Exact = true }).ClickAsync();

        // Scoped to the visible copy on purpose. The dashboard renders the same booking
        // twice — a card list for narrow screens and a table for wide ones, with CSS
        // deciding which is shown — so a plain GetByText(reference).First resolves to
        // whichever comes first in the DOM, which at this viewport is the hidden one.
        // The assertion then fails on a booking that is right there on the screen.
        var badge = Page.Locator(".ref-badge:visible")
                        .Filter(new LocatorFilterOptions { HasTextString = reference });

        await Assertions.Expect(badge).ToBeVisibleAsync();
    }

    [Fact]
    public void OutboundServices_AreSubstituted_SoNoTestCanSendRealMail()
    {
        // A guard on the guard. If someone reorders AppFixture's registrations, or the
        // app starts resolving its sender differently, these tests would quietly go back
        // to talking to the real SMTP host and the real Supabase bucket using the
        // credentials in appsettings.Local.json. Cheap to assert, expensive to discover
        // by finding booking confirmations in a customer's inbox.
        using var scope = App.ServerServices.CreateScope();

        Assert.IsType<RecordingEmailSender>(scope.ServiceProvider.GetRequiredService<IEmailSender>());
        Assert.IsType<NoOpStorageService>(scope.ServiceProvider.GetRequiredService<IStorageService>());
    }
}
