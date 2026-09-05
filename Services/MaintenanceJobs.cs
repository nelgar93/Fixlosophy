using Fixlosophy.Data;
using Microsoft.EntityFrameworkCore;

namespace Fixlosophy.Services;

/// <summary>
/// The recurring work the shop needs done without anyone clicking anything: the annual
/// price increase, notification retention, and the day-before appointment reminder.
///
/// Deliberately a plain scoped service rather than logic inside the
/// <see cref="MaintenanceService"/> background loop. Each method is a pure "is there
/// work, do it, say how much" call that a test can drive directly against an InMemory
/// context — where a BackgroundService would have to be started, waited on and stopped
/// to test the same thing.
///
/// Every method is safe to call at any time and as often as you like: each decides for
/// itself whether there is anything to do. That is what lets the scheduler stay a bare
/// timer with no memory of what it has already run, and what makes the whole thing
/// self-healing after a restart or a missed window.
/// </summary>
public class MaintenanceJobs(
    AppDbContext db,
    IConfiguration config,
    IEmailSender emailSender,
    InflationService inflation,
    NotificationService notifications,
    ILogger<MaintenanceJobs> logger)
{
    /// Earliest hour (shop time) the day-before reminder may go out. Sending at 03:00
    /// because that's when the container happened to restart would be worse than not
    /// sending at all.
    public const int DefaultReminderHour = 17;

    /// A booking made this close to the reminder going out has just had a confirmation
    /// email carrying the same details; a second one an hour later reads as a glitch.
    private static readonly TimeSpan JustBookedGrace = TimeSpan.FromHours(6);

    /// <summary>
    /// Applies the annual price increase, once per calendar year, on or after 1 April.
    /// Returns the number of prices changed (0 when it's not due).
    /// </summary>
    /// <remarks>
    /// The <c>PriceAdjustments</c> row is what makes this idempotent — it's written in
    /// the same SaveChanges as the new prices, so either both land or neither does.
    /// This used to run only at startup, which meant a server that stayed up from March
    /// to May never applied it at all.
    /// </remarks>
    public async Task<int> ApplyAnnualPriceIncreaseAsync(CancellationToken ct = default)
    {
        var today = ShopClock.Today;
        if (today < new DateTime(today.Year, 4, 1)) return 0;
        if (await db.PriceAdjustments.AnyAsync(a => a.Year == today.Year, ct)) return 0;

        // Fetch live UK CPI (ONS CPIH L55O series). PriceIncrease:InflationRate is the
        // fallback used only when the ONS API is unavailable — it is not the floor.
        // The business floor is a deliberate 5% a year, so any rate below it (live or
        // configured) is raised to 5%; only a rate above 5% is used as-is.
        var liveRate      = await inflation.GetLatestAnnualRateAsync();
        var configuredMin = config.GetValue("PriceIncrease:InflationRate", 0.03m);
        var rate          = Math.Max(liveRate ?? configuredMin, 0.05m);

        var services = await db.ServicePricings.Where(s => !s.IsQuoteOnly).ToListAsync(ct);
        foreach (var service in services)
            service.CurrentPrice = Math.Ceiling(service.CurrentPrice * (1 + rate));

        db.PriceAdjustments.Add(new PriceAdjustment { Year = today.Year, Rate = rate });
        await db.SaveChangesAsync(ct);

        // IsEnabled guard per CA1873: don't box the arguments when Information-level
        // logging is switched off.
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation(
                "Applied the {Year} price increase at {Rate:P2} across {Count} services.",
                today.Year, rate, services.Count);
        return services.Count;
    }

    /// <summary>
    /// Deletes notifications past their retention window. Returns how many went.
    /// </summary>
    public int PurgeExpiredNotifications() => notifications.PurgeOlderThanRetention();

    /// How long an error group is kept after its last occurrence. Grouping already
    /// keeps the row count near the number of distinct problems rather than the number
    /// of failures, so this is really about clearing out things fixed long ago.
    public static readonly TimeSpan ErrorRetention = TimeSpan.FromDays(90);

    /// <summary>
    /// Deletes error groups not seen for <see cref="ErrorRetention"/>. Returns how many
    /// went. A group that is still happening is never removed, however old it is.
    /// </summary>
    public async Task<int> PurgeExpiredErrorsAsync(CancellationToken ct = default)
    {
        var cutoff = ShopClock.Now - ErrorRetention;
        var stale = await db.ErrorLog.Where(e => e.LastSeen < cutoff).ToListAsync(ct);
        if (stale.Count == 0) return 0;

        db.ErrorLog.RemoveRange(stale);
        await db.SaveChangesAsync(ct);
        return stale.Count;
    }

    /// <summary>
    /// Emails everyone booked in tomorrow, once. Returns how many were sent.
    /// </summary>
    /// <remarks>
    /// <para><b>At most once, never twice.</b> <c>ReminderSentAt</c> is stamped after
    /// the send and saved per booking, so a crash halfway through a batch resumes
    /// cleanly, and a second instance overlapping during a deploy can't double-send.</para>
    ///
    /// <para><b>At most once also means it can be zero.</b> <see cref="IEmailSender"/>
    /// never throws — a dead SMTP host is logged and swallowed — so a send that failed
    /// is still stamped and never retried. That is the right trade for this particular
    /// message: its whole content is "tomorrow", so a retry on the next daily pass
    /// would arrive on the day itself, saying the wrong thing. A failure here shows up
    /// in the log as a send error, which is where it belongs.</para>
    /// </remarks>
    /// <summary>
    /// Whether <paramref name="now"/> is late enough in the day to send tomorrow's
    /// reminders. Pure, so the rule is testable without waiting for the clock — the
    /// same reason <see cref="ShopClock.ToShopTime"/> is pure.
    /// </summary>
    public static bool IsWithinReminderWindow(DateTime now, int reminderHour) =>
        now.Hour >= reminderHour;

    /// How long after a slot starts before staff are told the bike hasn't arrived.
    /// Long enough that someone parking up isn't flagged, short enough to still be
    /// worth a phone call.
    public static readonly TimeSpan LateAfter = TimeSpan.FromMinutes(20);

    /// <summary>
    /// Rings the bell for today's bookings whose slot has come and gone with the bike
    /// not booked in. Returns how many were flagged.
    /// </summary>
    /// <remarks>
    /// <para>"Late" is a booking still sitting at Pending or Confirmed more than
    /// <see cref="LateAfter"/> past its start. Moving it to InProgress is what the shop
    /// does when a bike lands on the stand, so that status is the arrival signal — no
    /// separate check-in step to remember.</para>
    ///
    /// <para>Once per booking, stamped on <c>LateNotifiedAt</c>. Without that the bell
    /// would ring again on every tick for the rest of the day, which is how people
    /// learn to ignore a notification.</para>
    ///
    /// <para>Today only. A booking left un-progressed from last week is a records
    /// problem, not someone who might still walk in.</para>
    /// </remarks>
    public async Task<int> FlagLateArrivalsAsync(CancellationToken ct = default)
    {
        var now = ShopClock.Now;
        var todayStart = now.Date;
        var todayEnd = todayStart.AddDays(1);

        var candidates = await db.Bookings
            .Where(b => b.SlotDate >= todayStart && b.SlotDate < todayEnd
                     && b.LateNotifiedAt == null
                     && (b.Status == BookingStatus.Pending || b.Status == BookingStatus.Confirmed))
            .ToListAsync(ct);

        // SlotTime is "HH:mm" text, so the "is it late yet" comparison can't be done
        // in SQL — the candidate set is one day's bookings, so filtering here is cheap.
        var late = candidates
            .Where(b => now - BookingService.SlotStart(b) >= LateAfter)
            .OrderBy(b => b.SlotTime)
            .ToList();

        if (late.Count == 0) return 0;

        foreach (var booking in late)
        {
            ct.ThrowIfCancellationRequested();
            notifications.RaiseLateArrival(booking);
            booking.LateNotifiedAt = now;
            await db.SaveChangesAsync(ct);
        }

        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("Flagged {Count} booking(s) as not yet arrived.", late.Count);
        return late.Count;
    }

    public async Task<int> SendRemindersAsync(CancellationToken ct = default)
    {
        var reminderHour = config.GetValue("Maintenance:ReminderHour", DefaultReminderHour);
        var now = ShopClock.Now;
        if (!IsWithinReminderWindow(now, reminderHour)) return 0;

        var tomorrow = ShopClock.Today.AddDays(1);
        var dayAfter = tomorrow.AddDays(1);
        var bookedBefore = now - JustBookedGrace;

        var due = await db.Bookings
            .Where(b => b.SlotDate >= tomorrow && b.SlotDate < dayAfter
                     && b.ReminderSentAt == null
                     && b.CreatedAt < bookedBefore
                     // Pending and Confirmed both mean "we expect this bike tomorrow".
                     // InProgress means it's already on the stand, and the other two
                     // are over — none of the three wants a reminder.
                     && (b.Status == BookingStatus.Pending || b.Status == BookingStatus.Confirmed)
                     && b.CustomerEmail != "")
            .OrderBy(b => b.SlotTime)
            .ToListAsync(ct);

        if (due.Count == 0) return 0;

        // Built from configuration, not from a request — there is no HttpContext here
        // to borrow a host from. Unset means no link rather than a broken one.
        var baseUrl = config["App:BaseUrl"]?.TrimEnd('/');
        if (string.IsNullOrWhiteSpace(baseUrl))
            logger.LogWarning(
                "App:BaseUrl is not set — sending {Count} reminders without a link to the customer's bookings.",
                due.Count);

        var sent = 0;
        foreach (var booking in due)
        {
            ct.ThrowIfCancellationRequested();

            // Guests have no account page to be sent to; the template gives them the
            // shop's phone number instead.
            var manageLink = booking.CustomerId is null || string.IsNullOrWhiteSpace(baseUrl)
                ? null
                : $"{baseUrl}/account";

            await emailSender.SendAppointmentReminderAsync(booking, manageLink);
            booking.ReminderSentAt = ShopClock.Now;
            await db.SaveChangesAsync(ct);
            sent++;
        }

        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("Sent {Count} appointment reminders for {Date:yyyy-MM-dd}.", sent, tomorrow);
        return sent;
    }
}
