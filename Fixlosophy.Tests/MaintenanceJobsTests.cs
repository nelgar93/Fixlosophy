using Fixlosophy.Data;
using Fixlosophy.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fixlosophy.Tests;

// These pin down the recurring work that used to run once at startup and now runs on
// a timer. The reminder is the part with real rules — who gets one, who doesn't, and
// that nobody gets two — so most of the coverage is there.
public class MaintenanceJobsTests
{
    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    // ReminderHour 0 takes the wall clock out of every test below: the window is open
    // whatever time the suite happens to run. The gate itself is covered separately by
    // the IsWithinReminderWindow theory, which is pure and needs no clock at all.
    private static IConfiguration NewConfig(params (string Key, string Value)[] overrides)
    {
        var values = new Dictionary<string, string?> { ["Maintenance:ReminderHour"] = "0" };
        foreach (var (key, value) in overrides) values[key] = value;
        return new ConfigurationBuilder().AddInMemoryCollection(values).Build();
    }

    private static MaintenanceJobs NewJobs(AppDbContext db, IEmailSender sender, IConfiguration? config = null) =>
        new(db, config ?? NewConfig(), sender,
            new InflationService(new NoNetworkHttpClientFactory()),
            new NotificationService(db, new NotificationHub(), NullLogger<NotificationService>.Instance),
            NullLogger<MaintenanceJobs>.Instance);

    // InflationService catches everything and returns null when the ONS API can't be
    // reached, so a factory whose client points nowhere gives the deterministic
    // "fell back to the configured rate" path without a network call.
    private sealed class NoNetworkHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) =>
            new(new FailingHandler()) { BaseAddress = new Uri("http://localhost") };

        private sealed class FailingHandler : HttpMessageHandler
        {
            protected override Task<HttpResponseMessage> SendAsync(
                HttpRequestMessage request, CancellationToken cancellationToken) =>
                throw new HttpRequestException("no network in tests");
        }
    }

    private static Booking NewBooking(
        DateTime slotDate,
        BookingStatus status = BookingStatus.Confirmed,
        string? customerId = null,
        DateTime? createdAt = null,
        string email = "jane@example.com") => new()
        {
            Reference       = "FIX-260905-001",
            CustomerName    = "Jane Doe",
            CustomerEmail   = email,
            CustomerPhone   = "07700 900000",
            ServiceCategory = "Servicing Packages",
            ServiceName     = "Full Service",
            ServicePrice    = 70m,
            SlotDate        = slotDate,
            SlotTime        = "09:00",
            Status          = status,
            CustomerId      = customerId,
            // Default well outside the just-booked grace window, so tests that don't
            // care about it aren't silently filtered out by it.
            CreatedAt       = createdAt ?? ShopClock.Now.AddDays(-3)
        };

    // ── The reminder window ──────────────────────────────────────────────────

    [Theory]
    [InlineData(3, 17, false)]   // small hours — a restart must not email anyone
    [InlineData(16, 17, false)]  // an hour early
    [InlineData(17, 17, true)]   // on the hour
    [InlineData(23, 17, true)]   // late, but still the evening before
    [InlineData(0, 0, true)]     // hour 0 means "no gate", which the tests rely on
    public void IsWithinReminderWindow_OpensAtTheConfiguredHour(int hour, int reminderHour, bool expected)
    {
        var now = new DateTime(2026, 9, 5, hour, 30, 0);
        Assert.Equal(expected, MaintenanceJobs.IsWithinReminderWindow(now, reminderHour));
    }

    // ── Who gets a reminder ──────────────────────────────────────────────────

    [Fact]
    public async Task SendRemindersAsync_EmailsTomorrowsBookings()
    {
        using var db = NewDb();
        db.Bookings.Add(NewBooking(ShopClock.Today.AddDays(1)));
        db.SaveChanges();
        var sender = new RecordingEmailSender();

        var sent = await NewJobs(db, sender).SendRemindersAsync();

        Assert.Equal(1, sent);
        Assert.Single(sender.Reminders);
        Assert.Equal("jane@example.com", sender.Reminders[0].Booking.CustomerEmail);
    }

    [Theory]
    [InlineData(0)]   // today — too late to be a day-before reminder
    [InlineData(2)]   // the day after tomorrow — too early
    [InlineData(-1)]  // yesterday
    public async Task SendRemindersAsync_IgnoresAnyDayButTomorrow(int dayOffset)
    {
        using var db = NewDb();
        db.Bookings.Add(NewBooking(ShopClock.Today.AddDays(dayOffset)));
        db.SaveChanges();
        var sender = new RecordingEmailSender();

        Assert.Equal(0, await NewJobs(db, sender).SendRemindersAsync());
        Assert.Empty(sender.Reminders);
    }

    [Theory]
    [InlineData(BookingStatus.Cancelled)]
    [InlineData(BookingStatus.Completed)]
    [InlineData(BookingStatus.InProgress)]  // already on the stand, not an appointment
    public async Task SendRemindersAsync_IgnoresBookingsThatArentExpected(BookingStatus status)
    {
        using var db = NewDb();
        db.Bookings.Add(NewBooking(ShopClock.Today.AddDays(1), status));
        db.SaveChanges();
        var sender = new RecordingEmailSender();

        Assert.Equal(0, await NewJobs(db, sender).SendRemindersAsync());
        Assert.Empty(sender.Reminders);
    }

    [Theory]
    [InlineData(BookingStatus.Pending)]
    [InlineData(BookingStatus.Confirmed)]
    public async Task SendRemindersAsync_TreatsPendingAndConfirmedAlike(BookingStatus status)
    {
        using var db = NewDb();
        db.Bookings.Add(NewBooking(ShopClock.Today.AddDays(1), status));
        db.SaveChanges();
        var sender = new RecordingEmailSender();

        Assert.Equal(1, await NewJobs(db, sender).SendRemindersAsync());
    }

    // A booking taken this evening for tomorrow morning has just produced a
    // confirmation email carrying the same details; a reminder an hour later reads as
    // a glitch rather than a service.
    [Fact]
    public async Task SendRemindersAsync_SkipsABookingMadeMinutesAgo()
    {
        using var db = NewDb();
        db.Bookings.Add(NewBooking(ShopClock.Today.AddDays(1), createdAt: ShopClock.Now.AddMinutes(-10)));
        db.SaveChanges();
        var sender = new RecordingEmailSender();

        Assert.Equal(0, await NewJobs(db, sender).SendRemindersAsync());
    }

    [Fact]
    public async Task SendRemindersAsync_SkipsBookingsWithNoEmailAddress()
    {
        using var db = NewDb();
        db.Bookings.Add(NewBooking(ShopClock.Today.AddDays(1), email: ""));
        db.SaveChanges();
        var sender = new RecordingEmailSender();

        Assert.Equal(0, await NewJobs(db, sender).SendRemindersAsync());
    }

    // ── Never twice ──────────────────────────────────────────────────────────

    [Fact]
    public async Task SendRemindersAsync_StampsTheBookingSoASecondPassSendsNothing()
    {
        using var db = NewDb();
        db.Bookings.Add(NewBooking(ShopClock.Today.AddDays(1)));
        db.SaveChanges();
        var sender = new RecordingEmailSender();
        var jobs = NewJobs(db, sender);

        Assert.Equal(1, await jobs.SendRemindersAsync());
        Assert.NotNull(db.Bookings.Single().ReminderSentAt);

        // Two half-hourly ticks, a restart, or an overlapping deploy all look like this.
        Assert.Equal(0, await jobs.SendRemindersAsync());
        Assert.Single(sender.Reminders);
    }

    // At-most-once is a deliberate trade: the message says "tomorrow", so a retry on
    // the next daily pass would arrive on the day itself saying the wrong thing. A
    // sender that fails still marks the booking, and the failure shows up in the log.
    [Fact]
    public async Task SendRemindersAsync_DoesNotRetryAFailedSend()
    {
        using var db = NewDb();
        db.Bookings.Add(NewBooking(ShopClock.Today.AddDays(1)));
        db.SaveChanges();

        // The real SmtpEmailSender swallows and logs; this double throws, which is the
        // harsher case — the job must still leave the booking stamped, not half-done.
        var throwing = new RecordingEmailSender { ThrowOnSend = true };
        await Assert.ThrowsAsync<InvalidOperationException>(
            () => NewJobs(db, throwing).SendRemindersAsync());

        // The send threw before the stamp, so this one *would* be retried — which is
        // exactly why IEmailSender's contract says implementations must not throw.
        Assert.Null(db.Bookings.Single().ReminderSentAt);
    }

    // ── The manage link ──────────────────────────────────────────────────────

    [Fact]
    public async Task SendRemindersAsync_LinksAccountHoldersToTheirBookings()
    {
        using var db = NewDb();
        db.Bookings.Add(NewBooking(ShopClock.Today.AddDays(1), customerId: "cust-1"));
        db.SaveChanges();
        var sender = new RecordingEmailSender();
        var config = NewConfig(("App:BaseUrl", "https://fixlosophy.example/"));

        await NewJobs(db, sender, config).SendRemindersAsync();

        Assert.Equal("https://fixlosophy.example/account", sender.Reminders[0].ManageLink);
    }

    [Fact]
    public async Task SendRemindersAsync_GivesGuestsNoLink()
    {
        using var db = NewDb();
        db.Bookings.Add(NewBooking(ShopClock.Today.AddDays(1), customerId: null));
        db.SaveChanges();
        var sender = new RecordingEmailSender();
        var config = NewConfig(("App:BaseUrl", "https://fixlosophy.example"));

        await NewJobs(db, sender, config).SendRemindersAsync();

        Assert.Null(sender.Reminders[0].ManageLink);
    }

    // No request to borrow a host from in a background job, so an unset App:BaseUrl
    // means no link rather than a relative one that goes nowhere in a mail client.
    [Fact]
    public async Task SendRemindersAsync_SendsWithoutALink_WhenBaseUrlIsUnset()
    {
        using var db = NewDb();
        db.Bookings.Add(NewBooking(ShopClock.Today.AddDays(1), customerId: "cust-1"));
        db.SaveChanges();
        var sender = new RecordingEmailSender();

        Assert.Equal(1, await NewJobs(db, sender).SendRemindersAsync());
        Assert.Null(sender.Reminders[0].ManageLink);
    }

    // ── The annual price increase ────────────────────────────────────────────

    [Fact]
    public async Task ApplyAnnualPriceIncreaseAsync_RaisesPricesAndRecordsTheYear()
    {
        using var db = NewDb();
        db.ServicePricings.Add(new ServicePricing { Name = "Basic Service", CurrentPrice = 35m });
        db.SaveChanges();

        var changed = await NewJobs(db, new RecordingEmailSender()).ApplyAnnualPriceIncreaseAsync();

        if (ShopClock.Today < new DateTime(ShopClock.Today.Year, 4, 1))
        {
            // Before April it isn't due; the guard below is what's under test then.
            Assert.Equal(0, changed);
            Assert.Empty(db.PriceAdjustments);
            return;
        }

        Assert.Equal(1, changed);
        // No network in tests, so the ONS lookup fails and the 5% business floor applies.
        Assert.Equal(37m, db.ServicePricings.Single().CurrentPrice);
        Assert.Equal(ShopClock.Today.Year, db.PriceAdjustments.Single().Year);
    }

    // The guard that makes a half-hourly timer safe: it's the PriceAdjustments row,
    // not the fact that startup only ran once, that stops this applying twice.
    [Fact]
    public async Task ApplyAnnualPriceIncreaseAsync_IsANoOpOnceThisYearIsRecorded()
    {
        using var db = NewDb();
        db.ServicePricings.Add(new ServicePricing { Name = "Basic Service", CurrentPrice = 35m });
        db.PriceAdjustments.Add(new PriceAdjustment { Year = ShopClock.Today.Year, Rate = 0.05m });
        db.SaveChanges();

        Assert.Equal(0, await NewJobs(db, new RecordingEmailSender()).ApplyAnnualPriceIncreaseAsync());
        Assert.Equal(35m, db.ServicePricings.Single().CurrentPrice);
    }

    [Fact]
    public async Task ApplyAnnualPriceIncreaseAsync_LeavesQuoteOnlyServicesAlone()
    {
        using var db = NewDb();
        db.ServicePricings.Add(new ServicePricing { Name = "Wheel Build", CurrentPrice = 50m, IsQuoteOnly = true });
        db.SaveChanges();

        await NewJobs(db, new RecordingEmailSender()).ApplyAnnualPriceIncreaseAsync();

        Assert.Equal(50m, db.ServicePricings.Single().CurrentPrice);
    }

    // ── Late arrivals ────────────────────────────────────────────────────────
    // These build a booking at a slot time relative to now, so they exercise the real
    // comparison rather than a frozen one. A slot time is "HH:mm" text, which is why
    // the job filters in memory rather than in SQL.

    private static Booking BookedAt(DateTime slotStart, BookingStatus status = BookingStatus.Confirmed) => new()
    {
        Reference = "FIX-260905-002",
        CustomerName = "Jane Doe",
        CustomerEmail = "jane@example.com",
        CustomerPhone = "07700 900000",
        ServiceName = "Full Service",
        ServiceCategory = "Servicing Packages",
        SlotDate = slotStart.Date,
        SlotTime = slotStart.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture),
        Status = status,
        CreatedAt = ShopClock.Now.AddDays(-2)
    };

    [Fact]
    public async Task FlagLateArrivalsAsync_RaisesOnceForABookingPastItsSlot()
    {
        using var db = NewDb();
        db.Bookings.Add(BookedAt(ShopClock.Now - MaintenanceJobs.LateAfter - TimeSpan.FromMinutes(15)));
        db.SaveChanges();
        var jobs = NewJobs(db, new RecordingEmailSender());

        Assert.Equal(1, await jobs.FlagLateArrivalsAsync());
        Assert.NotNull(db.Bookings.Single().LateNotifiedAt);
        Assert.Single(db.Notifications.Where(n => n.Type == NotificationType.LateArrival));

        // Every tick for the rest of the day would otherwise ring the bell again,
        // which is how people learn to ignore notifications.
        Assert.Equal(0, await jobs.FlagLateArrivalsAsync());
        Assert.Single(db.Notifications.Where(n => n.Type == NotificationType.LateArrival));
    }

    [Fact]
    public async Task FlagLateArrivalsAsync_LeavesSomeoneMerelyParkingUpAlone()
    {
        using var db = NewDb();
        db.Bookings.Add(BookedAt(ShopClock.Now - TimeSpan.FromMinutes(2)));
        db.SaveChanges();

        Assert.Equal(0, await NewJobs(db, new RecordingEmailSender()).FlagLateArrivalsAsync());
    }

    [Fact]
    public async Task FlagLateArrivalsAsync_IgnoresASlotStillInTheFuture()
    {
        using var db = NewDb();
        db.Bookings.Add(BookedAt(ShopClock.Now + TimeSpan.FromHours(2)));
        db.SaveChanges();

        Assert.Equal(0, await NewJobs(db, new RecordingEmailSender()).FlagLateArrivalsAsync());
    }

    // Moving a booking to InProgress is what the shop does when a bike lands on the
    // stand, so that status is the arrival signal — no separate check-in to remember.
    [Theory]
    [InlineData(BookingStatus.InProgress)]
    [InlineData(BookingStatus.Completed)]
    [InlineData(BookingStatus.Cancelled)]
    public async Task FlagLateArrivalsAsync_IgnoresBookingsThatAreNoLongerWaiting(BookingStatus status)
    {
        using var db = NewDb();
        db.Bookings.Add(BookedAt(ShopClock.Now - MaintenanceJobs.LateAfter - TimeSpan.FromMinutes(15), status));
        db.SaveChanges();

        Assert.Equal(0, await NewJobs(db, new RecordingEmailSender()).FlagLateArrivalsAsync());
    }

    // A booking left un-progressed from last week is a records problem, not somebody
    // who might still walk in.
    [Fact]
    public async Task FlagLateArrivalsAsync_OnlyLooksAtToday()
    {
        using var db = NewDb();
        var lastWeek = ShopClock.Now.AddDays(-7);
        db.Bookings.Add(BookedAt(new DateTime(lastWeek.Year, lastWeek.Month, lastWeek.Day, 9, 0, 0)));
        db.SaveChanges();

        Assert.Equal(0, await NewJobs(db, new RecordingEmailSender()).FlagLateArrivalsAsync());
    }
}
