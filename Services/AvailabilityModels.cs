using System.Globalization;

namespace Fixlosophy.Services;

/// <summary>
/// Why a mechanic isn't in. Explicit values because these are persisted — reordering
/// the enum must not reinterpret existing rows.
/// </summary>
public enum AbsenceType
{
    Holiday  = 0,
    Sick     = 1,
    Training = 2,
    Other    = 9,
}

/// <summary>
/// A period the shop is shut. Customer-facing: the booking calendar shows the reason
/// rather than just greying the day out, because "Closed — Christmas" and "fully
/// booked" are different disappointments and only one of them means try tomorrow.
/// </summary>
/// <remarks>
/// A range rather than a single date because the reason it's usually needed is a
/// range — the week between Christmas and New Year, a fortnight in August. Storing
/// those as one row per day would make "why are we shut?" a question with fourteen
/// identical answers, and editing it a fourteen-row job.
/// </remarks>
public class Closure
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// First day shut, inclusive. Date only — the time half is always midnight.
    public DateTime StartDate { get; set; }

    /// Last day shut, <b>inclusive</b>. A single-day closure has StartDate == EndDate,
    /// which is what people mean by "closed on the 25th".
    public DateTime EndDate { get; set; }

    /// <summary>
    /// Optional window within each day, as "HH:mm". Both null means all day.
    /// </summary>
    /// <remarks>
    /// Set both for a half-day — "closing at 13:00 on Saturday" is
    /// StartTime "13:00", EndTime "19:00". Slots starting inside the window go; the
    /// rest of the day stays bookable. A part-day closure never makes the day itself
    /// unavailable, only some of its slots.
    /// </remarks>
    public string? StartTime { get; set; }
    public string? EndTime { get; set; }

    /// Shown to customers. Keep it short and true: "Bank holiday", "Staff training".
    public string Reason { get; set; } = "";

    public DateTime CreatedAt { get; set; } = ShopClock.Now;
    public string? CreatedByStaffId { get; set; }

    /// Whether this closure shuts the whole day rather than a window inside it.
    public bool IsAllDay => string.IsNullOrWhiteSpace(StartTime) || string.IsNullOrWhiteSpace(EndTime);

    /// Whether <paramref name="date"/> falls inside the range. Inclusive both ends.
    public bool Covers(DateTime date) =>
        date.Date >= StartDate.Date && date.Date <= EndDate.Date;

    /// <summary>
    /// Whether a slot starting at <paramref name="slot"/> ("HH:mm") is inside the
    /// closed window. Always true for an all-day closure.
    /// </summary>
    /// <remarks>
    /// Start-inclusive, end-exclusive: a closure of 13:00–19:00 takes the 13:00 slot
    /// but not a slot that would start at 19:00, matching how the trading-hours
    /// generator treats closing time.
    /// </remarks>
    public bool CoversSlot(string slot)
    {
        if (IsAllDay) return true;
        if (!TimeOnly.TryParse(slot, CultureInfo.InvariantCulture, out var at)) return false;
        if (!TimeOnly.TryParse(StartTime, CultureInfo.InvariantCulture, out var from)) return false;
        if (!TimeOnly.TryParse(EndTime, CultureInfo.InvariantCulture, out var until)) return false;
        return at >= from && at < until;
    }

    /// How the range reads in the admin list and on the public notice.
    public string DescribeDates() =>
        StartDate.Date == EndDate.Date
            ? StartDate.ToString("ddd d MMM yyyy", CultureInfo.GetCultureInfo("en-GB"))
            : $"{StartDate.ToString("ddd d MMM", CultureInfo.GetCultureInfo("en-GB"))} – " +
              $"{EndDate.ToString("ddd d MMM yyyy", CultureInfo.GetCultureInfo("en-GB"))}";
}

/// <summary>
/// A mechanic being away. Internal only — customers never see who is off, just that
/// the day isn't bookable.
/// </summary>
/// <remarks>
/// <para>This is deliberately a <em>separate</em> concept from <see cref="Closure"/>,
/// even though with one mechanic on the books they amount to the same thing today.
/// They answer different questions — "is the shop shut?" versus "who is in?" — and
/// only the first is any of a customer's business.</para>
///
/// <para>Availability treats them alike in exactly one respect: a day with no mechanic
/// working can't take a booking. See <see cref="AvailabilityService"/>.</para>
/// </remarks>
public class StaffAbsence
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string StaffId { get; set; } = "";

    /// First day away, inclusive.
    public DateTime StartDate { get; set; }

    /// Last day away, inclusive.
    public DateTime EndDate { get; set; }

    public AbsenceType Type { get; set; } = AbsenceType.Holiday;

    /// Free text for the rota. Never shown to a customer.
    public string Note { get; set; } = "";

    public DateTime CreatedAt { get; set; } = ShopClock.Now;

    // Navigation
    public StaffMember? Staff { get; set; }

    public bool Covers(DateTime date) =>
        date.Date >= StartDate.Date && date.Date <= EndDate.Date;

    public string DescribeDates() =>
        StartDate.Date == EndDate.Date
            ? StartDate.ToString("ddd d MMM yyyy", CultureInfo.GetCultureInfo("en-GB"))
            : $"{StartDate.ToString("ddd d MMM", CultureInfo.GetCultureInfo("en-GB"))} – " +
              $"{EndDate.ToString("ddd d MMM yyyy", CultureInfo.GetCultureInfo("en-GB"))}";
}

/// <summary>
/// Why a given day can or can't take bookings. One answer, so the booking calendar,
/// the admin calendar and the availability checks can't disagree about it.
/// </summary>
public enum DayState
{
    /// Bookable — trading hours, at least one mechanic, no closure.
    Open = 0,

    /// Outside trading hours entirely (no slots defined for this weekday).
    NotTrading = 1,

    /// An all-day <see cref="Closure"/> covers it.
    Closed = 2,

    /// Nobody who turns a spanner is working. With one mechanic, this is what their
    /// holiday looks like to the calendar.
    NoMechanic = 3,

    /// In the past.
    Past = 4,
}

/// <summary>
/// A day's availability, with enough detail for the calendar to explain itself.
/// </summary>
/// <param name="State">Why the day is or isn't bookable.</param>
/// <param name="Reason">
/// The closure's customer-facing reason, when <see cref="DayState.Closed"/>. Null
/// otherwise — <see cref="DayState.NoMechanic"/> deliberately carries no reason,
/// because whose holiday it is isn't a customer's business.
/// </param>
/// <param name="HasFreeSlot">
/// Whether any slot is actually open. A day can be <see cref="DayState.Open"/> and
/// still have nothing left, which is the ordinary "fully booked" case.
/// </param>
public readonly record struct DayAvailability(DayState State, string? Reason, bool HasFreeSlot)
{
    /// Whether a customer can book something on this day.
    public bool IsBookable => State == DayState.Open && HasFreeSlot;

    /// What the calendar puts on a day the customer can't book. Null when it's simply
    /// full, which the calendar already styles on its own.
    public string? CustomerLabel => State switch
    {
        DayState.Closed     => string.IsNullOrWhiteSpace(Reason) ? "Closed" : $"Closed — {Reason}",
        DayState.NoMechanic => "Closed",
        _                   => null,
    };
}
