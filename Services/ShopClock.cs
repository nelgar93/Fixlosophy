namespace Fixlosophy.Services;

/// <summary>
/// "Now" as the shop experiences it — Europe/London, so British Summer Time is
/// handled — rather than as the server experiences it.
///
/// Booking logic asks two questions that are only meaningful in the shop's own
/// timezone: "is this slot already in the past?" and "what is today?". Answering them
/// with <c>DateTime.Now</c> ties them to the host's clock, so a VPS running UTC (the
/// default on most Linux images) puts every availability check an hour out for the
/// whole of BST — the 10:00 slot would still look bookable at 10:30.
///
/// Static rather than an injected service: the shop is in one place and that isn't a
/// per-request or per-environment concern. <see cref="ToShopTime"/> is a pure function
/// so the DST behaviour is testable without touching the wall clock.
/// </summary>
public static class ShopClock
{
    public static readonly TimeZoneInfo TimeZone = ResolveTimeZone();

    /// Current date and time in the shop's timezone.
    public static DateTime Now => ToShopTime(DateTimeOffset.UtcNow);

    /// Today's date in the shop's timezone.
    public static DateTime Today => Now.Date;

    /// Converts any instant to the shop's local wall-clock time.
    public static DateTime ToShopTime(DateTimeOffset instant) =>
        TimeZoneInfo.ConvertTime(instant, TimeZone).DateTime;

    // .NET 6+ accepts IANA ids on Windows as well as Linux, but only when ICU is
    // available — a container built in globalization-invariant mode has no timezone
    // database at all. Fall back to the Windows id, then to UTC, so a misconfigured
    // host degrades to "an hour out in summer" rather than failing to start.
    private static TimeZoneInfo ResolveTimeZone()
    {
        foreach (var id in new[] { "Europe/London", "GMT Standard Time" })
        {
            try
            {
                return TimeZoneInfo.FindSystemTimeZoneById(id);
            }
            catch (Exception ex) when (ex is TimeZoneNotFoundException or InvalidTimeZoneException)
            {
                // Try the next id.
            }
        }
        return TimeZoneInfo.Utc;
    }
}
