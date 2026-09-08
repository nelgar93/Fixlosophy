using Bunit;
using Fixlosophy.Data;
using Fixlosophy.Services;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fixlosophy.Tests;

/// <summary>
/// Base for the component tests: a throwaway database and the same service graph
/// <c>Program.cs</c> registers, wired into bUnit's container.
/// </summary>
/// <remarks>
/// <para>
/// These tests exist for the wiring inside a <c>.razor</c> file — the order validators
/// run in, which branch a guard takes, what a handler passes on — and nothing else.
/// The rules themselves are already covered against the services that own them
/// (BookingServiceTests, AuthServiceTests, StorageServiceTests …), and duplicating them
/// here would only mean two places to update when a rule changes.
/// </para>
/// <para>
/// Deliberately no assertions on markup, class names or copy. Those break on every
/// visual change without catching a single defect, and they are what turns a component
/// suite into something people delete. If a test here would still pass after the
/// feature broke, or fails after a purely visual edit, it does not belong.
/// </para>
/// <para>
/// The layer is meant to stay small and stay droppable: because the rules live in the
/// services, deleting this file would cost a known, bounded amount of coverage rather
/// than the safety net itself. That is the deal that makes a second test framework
/// worth taking on.
/// </para>
/// </remarks>
public abstract class ComponentTestBase : BunitContext
{
    protected AppDbContext Db { get; }

    /// Records what the component asked to send instead of sending it, so the
    /// best-effort mail paths can be asserted on.
    internal RecordingEmailSender Email { get; }

    protected ComponentTestBase()
    {
        Db = TestFactory.NewDb();
        Email = new RecordingEmailSender();

        // The same registrations as Program.cs, with the two things a test can't have
        // swapped out: Postgres for the InMemory provider (via TestFactory) and SMTP
        // for the recording sender. Registered as instances where a test needs to seed
        // or read them directly.
        Services.AddSingleton(Db);
        Services.AddSingleton<IEmailSender>(Email);
        Services.AddSingleton<IStorageService, TestFactory.FakeStorageService>();
        Services.AddSingleton<AvailabilityService>();
        Services.AddSingleton<BookingService>();
        Services.AddSingleton<AuthService>();
        Services.AddSingleton<ActionRateLimiter>();
        Services.AddSingleton<NotificationHub>();
        Services.AddSingleton<NotificationService>();

        // Components inject ILogger<T>. NullLogger rather than AddLogging() so a failing
        // test's output stays the assertion, not a page of framework logging.
        Services.AddSingleton(typeof(ILogger<>), typeof(NullLogger<>));
    }

    /// <summary>
    /// The cascading <see cref="AuthenticationState"/> a signed-in customer's circuit
    /// carries.
    /// </summary>
    /// <remarks>
    /// Built by <see cref="AuthClaims.BuildCustomerPrincipal"/> — the same call the
    /// sign-in endpoint makes before it writes the cookie — rather than by hand or from
    /// bUnit's authorization doubles. The component reads two specific claims out of
    /// this principal, so taking it from the code that really produces it is what makes
    /// the test meaningful: rename a claim and both sides move together.
    /// </remarks>
    protected static Task<AuthenticationState> SignedInAs(Customer customer) =>
        Task.FromResult(new AuthenticationState(AuthClaims.BuildCustomerPrincipal(customer)));

    protected override void Dispose(bool disposing)
    {
        if (disposing) Db.Dispose();
        base.Dispose(disposing);
    }
}
