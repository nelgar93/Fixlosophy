using Fixlosophy.Data;
using Microsoft.EntityFrameworkCore;

namespace Fixlosophy.Services;

/// <summary>
/// Whether the shop can take a booking on a given day, and why not when it can't.
/// </summary>
/// <remarks>
/// <para><b>The rule, in one sentence:</b> a day is bookable if it's a trading day, no
/// all-day closure covers it, and at least one mechanic is working.</para>
///
/// <para>That last clause is what makes a mechanic's holiday block bookings without
/// needing per-mechanic capacity. With one mechanic on the books, Francesco being away
/// means nobody is working, so the day closes itself. With two, one being away leaves
/// the shop open at the same one-bike-at-a-time capacity it always had — which is
/// correct, because <see cref="BookingService.MaxPerSlot"/> is about how many bikes fit
/// on the stand, not how many people are in.</para>
///
/// <para>Keeping <c>MaxPerSlot</c> at 1 is what lets this stay simple. Making capacity
/// scale with mechanics would mean a seats table — one row per (date, slot, seat) — so
/// a unique index could still do the enforcing, since an index cannot express "at most
/// N". That's a real change, and it's only worth making if the shop ever runs two bikes
/// through the same hour.</para>
///
/// <para>Split out of <see cref="BookingService"/> rather than added to it: this is a
/// question several callers ask (the booking calendar, the admin calendar, the
/// closure editor) and it has nothing to do with creating or moving a booking.</para>
/// </remarks>
public class AvailabilityService(AppDbContext db)
{
    // ── Reading the calendar ─────────────────────────────────────────────────

    /// <summary>
    /// The state of a single day, ignoring how many slots are left. Use
    /// <see cref="BookingService.GetAvailableSlots"/> for the full picture.
    /// </summary>
    public DayState StateOf(DateTime date)
    {
        if (date.Date < ShopClock.Today) return DayState.Past;
        if (BookingService.SlotsFor(date).Length == 0) return DayState.NotTrading;
        if (AllDayClosureOn(date) is not null) return DayState.Closed;
        if (MechanicRuleApplies && WorkingMechanicCount(date) == 0) return DayState.NoMechanic;
        return DayState.Open;
    }

    /// <summary>
    /// Whether anyone is flagged as a mechanic at all. When nobody is, the
    /// "somebody has to be in" rule is switched off rather than closing every day.
    /// </summary>
    /// <remarks>
    /// The alternative reading — nobody flagged means nobody working means shut — is
    /// defensible and wrong in practice. It turns unticking the last mechanic, or
    /// simply never having ticked one, into a site that silently stops taking bookings
    /// with nothing on screen to explain it. Treating "none configured" as "this
    /// feature isn't set up yet" degrades to the behaviour the shop had before
    /// absences existed, which is the safe direction to fail in.
    ///
    /// The admin availability screen warns when this is false, so it can't stay
    /// unnoticed.
    /// </remarks>
    public bool MechanicRuleApplies => db.Staff.Any(s => s.IsActive && s.IsMechanic);

    /// The all-day closure covering this date, if any. Part-day closures are not
    /// returned here — they narrow the slot list rather than shutting the day.
    public Closure? AllDayClosureOn(DateTime date) =>
        ClosuresOverlapping(date, date).FirstOrDefault(c => c.IsAllDay);

    /// <summary>
    /// Slot times ("HH:mm") blocked on this date by a part-day closure.
    /// </summary>
    public HashSet<string> BlockedSlotsOn(DateTime date)
    {
        var closures = ClosuresOverlapping(date, date).Where(c => !c.IsAllDay).ToList();
        if (closures.Count == 0) return [];

        return BookingService.SlotsFor(date)
            .Where(slot => closures.Exists(c => c.CoversSlot(slot)))
            .ToHashSet(StringComparer.Ordinal);
    }

    /// <summary>
    /// How many mechanics are in on a given date. Only active staff flagged
    /// <see cref="StaffMember.IsMechanic"/> count.
    /// </summary>
    /// <remarks>
    /// Returns a raw count, so zero here means "nobody in" <em>or</em> "nobody
    /// configured". Callers deciding whether a day is bookable must pair it with
    /// <see cref="MechanicRuleApplies"/> to tell those apart.
    /// </remarks>
    public int WorkingMechanicCount(DateTime date)
    {
        var mechanicIds = db.Staff
            .Where(s => s.IsActive && s.IsMechanic)
            .Select(s => s.Id)
            .ToList();

        if (mechanicIds.Count == 0) return 0;

        var away = AbsencesOverlapping(date, date)
            .Select(a => a.StaffId)
            .ToHashSet(StringComparer.Ordinal);

        return mechanicIds.Count(id => !away.Contains(id));
    }

    // ── Batched, for a whole month ───────────────────────────────────────────

    /// <summary>
    /// Every day in a month, resolved in three queries rather than three per day.
    /// </summary>
    /// <remarks>
    /// The per-day methods above are fine for one date; the calendar asks about
    /// thirty-one at once, and doing that one at a time was how the original
    /// availability check became the slowest thing on the booking page.
    /// </remarks>
    public Dictionary<DateTime, (DayState State, string? Reason, HashSet<string> BlockedSlots)>
        StatesForMonth(int year, int month)
    {
        var start = new DateTime(year, month, 1);
        var end   = start.AddMonths(1);
        var today = ShopClock.Today;

        var closures = ClosuresOverlapping(start, end.AddDays(-1));
        var absences = AbsencesOverlapping(start, end.AddDays(-1));
        var mechanicIds = db.Staff
            .Where(s => s.IsActive && s.IsMechanic)
            .Select(s => s.Id)
            .ToList();

        var result = new Dictionary<DateTime, (DayState, string?, HashSet<string>)>();

        for (var d = start; d < end; d = d.AddDays(1))
        {
            if (d.Date < today)
            {
                result[d] = (DayState.Past, null, []);
                continue;
            }

            var slots = BookingService.SlotsFor(d);
            if (slots.Length == 0)
            {
                result[d] = (DayState.NotTrading, null, []);
                continue;
            }

            var day = d;
            var allDay = closures.Find(c => c.IsAllDay && c.Covers(day));
            if (allDay is not null)
            {
                result[d] = (DayState.Closed, allDay.Reason, []);
                continue;
            }

            // mechanicIds empty means the rule isn't configured — see
            // MechanicRuleApplies for why that's "open", not "shut".
            var away = absences.Where(a => a.Covers(day)).Select(a => a.StaffId)
                               .ToHashSet(StringComparer.Ordinal);
            if (mechanicIds.Count > 0 && mechanicIds.TrueForAll(away.Contains))
            {
                result[d] = (DayState.NoMechanic, null, []);
                continue;
            }

            var blocked = slots
                .Where(slot => closures.Exists(c => !c.IsAllDay && c.Covers(day) && c.CoversSlot(slot)))
                .ToHashSet(StringComparer.Ordinal);

            result[d] = (DayState.Open, null, blocked);
        }

        return result;
    }

    // ── Closures ─────────────────────────────────────────────────────────────

    public List<Closure> GetClosures(bool includePast = false)
    {
        var query = db.Closures.AsQueryable();
        if (!includePast) query = query.Where(c => c.EndDate >= ShopClock.Today);
        return query.OrderBy(c => c.StartDate).ToList();
    }

    public List<Closure> ClosuresOverlapping(DateTime from, DateTime to)
    {
        var start = from.Date;
        var end   = to.Date;
        return db.Closures
            .Where(c => c.StartDate <= end && c.EndDate >= start)
            .OrderBy(c => c.StartDate)
            .ToList();
    }

    /// <summary>
    /// Validates and stores a closure. Returns the saved row, or an error to show.
    /// </summary>
    public (Closure? closure, string? error) AddClosure(Closure closure)
    {
        if (closure.EndDate.Date < closure.StartDate.Date)
            return (null, "The last day can't be before the first day.");

        if (string.IsNullOrWhiteSpace(closure.Reason))
            return (null, "Give the closure a reason — customers see it on the calendar.");

        // Either both times or neither. One alone is ambiguous: does "from 13:00" mean
        // until closing, or from opening until 13:00?
        var hasStart = !string.IsNullOrWhiteSpace(closure.StartTime);
        var hasEnd   = !string.IsNullOrWhiteSpace(closure.EndTime);
        if (hasStart != hasEnd)
            return (null, "For a part-day closure, set both a start and an end time. Leave both blank to close all day.");

        if (hasStart && string.CompareOrdinal(closure.EndTime, closure.StartTime) <= 0)
            return (null, "The closure's end time must be after its start time.");

        closure.StartDate = closure.StartDate.Date;
        closure.EndDate   = closure.EndDate.Date;
        closure.Reason    = closure.Reason.Trim();
        if (!hasStart) { closure.StartTime = null; closure.EndTime = null; }

        db.Closures.Add(closure);
        db.SaveChanges();
        return (closure, null);
    }

    public bool RemoveClosure(string id)
    {
        var closure = db.Closures.Find(id);
        if (closure is null) return false;
        db.Closures.Remove(closure);
        db.SaveChanges();
        return true;
    }

    // ── Staff absence ────────────────────────────────────────────────────────

    public List<StaffAbsence> GetAbsences(bool includePast = false)
    {
        var query = db.StaffAbsences.Include(a => a.Staff).AsQueryable();
        if (!includePast) query = query.Where(a => a.EndDate >= ShopClock.Today);
        return query.OrderBy(a => a.StartDate).ToList();
    }

    public List<StaffAbsence> AbsencesOverlapping(DateTime from, DateTime to)
    {
        var start = from.Date;
        var end   = to.Date;
        return db.StaffAbsences
            .Where(a => a.StartDate <= end && a.EndDate >= start)
            .OrderBy(a => a.StartDate)
            .ToList();
    }

    public (StaffAbsence? absence, string? error) AddAbsence(StaffAbsence absence)
    {
        if (absence.EndDate.Date < absence.StartDate.Date)
            return (null, "The last day can't be before the first day.");

        var staff = db.Staff.Find(absence.StaffId);
        if (staff is null) return (null, "That staff member no longer exists.");

        absence.StartDate = absence.StartDate.Date;
        absence.EndDate   = absence.EndDate.Date;
        absence.Note      = absence.Note.Trim();

        db.StaffAbsences.Add(absence);
        db.SaveChanges();
        return (absence, null);
    }

    public bool RemoveAbsence(string id)
    {
        var absence = db.StaffAbsences.Find(id);
        if (absence is null) return false;
        db.StaffAbsences.Remove(absence);
        db.SaveChanges();
        return true;
    }

    // ── Orphan detection ─────────────────────────────────────────────────────

    /// <summary>
    /// Bookings that already exist on days a proposed closure or absence would make
    /// unbookable.
    /// </summary>
    /// <remarks>
    /// <para>This is the check worth having. Closing a week without it strands however
    /// many customers had booked into it — they'd turn up to a locked door, having had
    /// a confirmation email and heard nothing since.</para>
    ///
    /// <para>Call it <em>before</em> saving to warn, and again after to act. Only
    /// bookings still expecting a bike count: cancelled and completed ones are already
    /// resolved, and one already in progress means the bike is on the stand, which a
    /// future closure doesn't affect.</para>
    /// </remarks>
    /// <param name="ignoreDaysStillOpen">
    /// When true (the default for an absence), days that would still have another
    /// mechanic working are excluded — one person's holiday only orphans bookings on
    /// days it actually empties the workshop.
    /// </param>
    public List<Booking> FindAffectedBookings(
        DateTime from, DateTime to, string? startTime = null, string? endTime = null,
        bool ignoreDaysStillOpen = false)
    {
        var start = from.Date;
        var end   = to.Date.AddDays(1);

        var candidates = db.Bookings
            .Where(b => b.SlotDate >= start && b.SlotDate < end
                     && (b.Status == BookingStatus.Pending || b.Status == BookingStatus.Confirmed))
            .OrderBy(b => b.SlotDate).ThenBy(b => b.SlotTime)
            .ToList();

        if (candidates.Count == 0) return candidates;

        // A part-day window only affects bookings inside it.
        if (!string.IsNullOrWhiteSpace(startTime) && !string.IsNullOrWhiteSpace(endTime))
        {
            var window = new Closure { StartTime = startTime, EndTime = endTime };
            candidates = candidates.Where(b => window.CoversSlot(b.SlotTime)).ToList();
        }

        if (!ignoreDaysStillOpen) return candidates;

        // For an absence: keep only bookings on days that would be left with nobody.
        return candidates.Where(b => WouldHaveNoMechanic(b.SlotDate, from, to)).ToList();
    }

    /// Whether adding an absence over [from, to] would leave <paramref name="date"/>
    /// with no mechanic — counting the absence being proposed, which isn't saved yet.
    private bool WouldHaveNoMechanic(DateTime date, DateTime from, DateTime to)
    {
        if (date.Date < from.Date || date.Date > to.Date) return false;
        if (!MechanicRuleApplies) return false;
        // One fewer than are working today; zero means the proposal empties the day.
        return WorkingMechanicCount(date) <= 1;
    }
}
