using System.Globalization;
using Fixlosophy.Data;
using Microsoft.EntityFrameworkCore;
using Npgsql;

namespace Fixlosophy.Services;

public class BookingService(
    AppDbContext db, IStorageService storage, AvailabilityService availability,
    ILogger<BookingService> logger)
{
    /// <summary>
    /// How far ahead a booking may be made.
    /// </summary>
    /// <remarks>
    /// The calendar used to page forward indefinitely, which meant "fill the whole
    /// calendar" had no end to it — a script could take every slot from here to the
    /// heat death of the universe. A horizon bounds the damage any abuse can do, and
    /// it also stops a customer booking a service for a date the shop hasn't decided
    /// its holidays for yet.
    /// </remarks>
    public static readonly TimeSpan BookingHorizon = TimeSpan.FromDays(60);

    /// The last date a booking may be made for.
    public static DateTime LatestBookableDate => ShopClock.Today.Add(BookingHorizon);

    // One booking per slot: one bike, one customer, one mechanic. The shop currently
    // runs one mechanic a day, so an hour-long slot is exactly one job — and hourly
    // slots leave room for the walk-in fixes that never get booked.
    //
    // This is deliberately the capacity the database can enforce on its own: a unique
    // index over (SlotDate, SlotTime) makes overbooking impossible, which no
    // count-then-insert check can promise. Raising this above 1 gives that up, because
    // a unique index cannot express "at most N" — that needs a seats table, one row per
    // (date, time, seat), so a unique constraint keeps doing the enforcing.
    public const int MaxPerSlot = 1;
    public const int MaxActiveBookingsPerEmail = 3;

    // Slots are derived from the trading hours in SiteContent rather than typed out,
    // so the calendar can never offer an appointment for a time the shop is shut.
    // Two rules, applied to every day: appointments start on the hour, and the last
    // one starts a full hour before closing (nobody books the minute they lock up).
    // SiteContent.LunchStart is skipped.
    //
    // This previously lived in two hand-written arrays, which had drifted: Saturday
    // shared the weekday list and so offered an 18:00 slot even though the shop
    // closes at 18:00 on Saturdays.
    private static readonly Dictionary<DayOfWeek, string[]> _slotsByDay =
        Enum.GetValues<DayOfWeek>().ToDictionary(day => day, BuildSlotsFor);

    private static string[] BuildSlotsFor(DayOfWeek day)
    {
        if (SiteContent.HoursFor(day) is not { } hours) return [];

        var slots = new List<string>();
        for (var t = hours.Open; t.AddHours(1) <= hours.Close; t = t.AddHours(1))
        {
            if (t == SiteContent.LunchStart) continue;
            // Invariant: these strings are persisted in Bookings.SlotTime and compared
            // against stored values, so they must not follow the server's locale.
            slots.Add(t.ToString("HH:mm", CultureInfo.InvariantCulture));
        }
        return [.. slots];
    }

    /// Bookable appointment times for a given date. Empty on a day the shop is closed.
    public static string[] SlotsFor(DateTime date) => _slotsByDay[date.DayOfWeek];

    // Atomic booking-reference counter (Postgres sequence "BookingReferenceSeq",
    // created in Program.cs's EnsureSchema). Static + takes db explicitly so the
    // startup demo-data seeder can draw from the same counter as real bookings —
    // a plain Count()+1 read-then-format has a race window between two concurrent
    // CreateBooking calls that lets them land on the same reference.
    public static long NextReferenceSequence(AppDbContext db)
    {
        // The InMemory provider used by the test suite has no sequences and throws
        // on any relational-specific call. Fall back to a count there so the
        // successful-booking path stays testable; the race this guards against
        // needs concurrent writers, which the single-threaded tests never have.
        if (!db.Database.IsRelational())
            return db.Bookings.Count() + 1;

        db.Database.OpenConnection();
        try
        {
            using var cmd = db.Database.GetDbConnection().CreateCommand();
            cmd.CommandText = "SELECT nextval('\"BookingReferenceSeq\"')";
            return (long)cmd.ExecuteScalar()!;
        }
        finally
        {
            db.Database.CloseConnection();
        }
    }

    public List<ServiceOption> GetServices() =>
        db.ServicePricings
          .OrderBy(s => s.SortOrder)
          .Select(s => new ServiceOption
          {
              Category  = s.Category,
              Name      = s.Name,
              PriceFrom = s.IsQuoteOnly ? 0 : s.CurrentPrice,
              Duration  = s.Duration,
              Icon      = s.Icon
          })
          .ToList();

    public List<ServicePricing> GetServicePricings() =>
        db.ServicePricings.OrderBy(s => s.SortOrder).ToList();

    // Returns false (a no-op) when id doesn't match any row, so callers can tell
    // "updated" apart from "target no longer exists" instead of both looking identical.
    public bool UpdateServicePrice(string id, decimal price)
    {
        var sp = db.ServicePricings.Find(id);
        if (sp == null) return false;
        sp.CurrentPrice = price;
        db.SaveChanges();
        return true;
    }

    public List<PriceAdjustment> GetPriceAdjustments() =>
        db.PriceAdjustments.OrderByDescending(a => a.AppliedAt).ToList();

    public List<string> GetAvailableSlots(DateTime date)
    {
        if (date.Date < ShopClock.Today || date.Date > LatestBookableDate)
            return [];

        // Shut for the day — a closure, or nobody in the workshop. Either way there is
        // nothing to offer, whatever the booking table says.
        if (availability.StateOf(date) != DayState.Open)
            return [];

        var start = date.Date;
        var end = start.AddDays(1);

        var booked = db.Bookings
            .Where(b => b.SlotDate >= start && b.SlotDate < end && b.Status != BookingStatus.Cancelled)
            .GroupBy(b => b.SlotTime)
            .Select(g => new { Slot = g.Key, Count = g.Count() })
            .ToDictionary(x => x.Slot, x => x.Count);

        // A part-day closure takes some slots without shutting the day.
        var blocked = availability.BlockedSlotsOn(date);

        return SlotsFor(date).Where(slot =>
        {
            if (blocked.Contains(slot)) return false;
            if (date.Date == ShopClock.Today && TimeOnly.TryParse(slot, out var t) && t <= TimeOnly.FromDateTime(ShopClock.Now))
                return false;
            return !booked.TryGetValue(slot, out var count) || count < MaxPerSlot;
        }).ToList();
    }

    public bool IsDateAvailable(DateTime date)
    {
        if (date.Date < ShopClock.Today || date.Date > LatestBookableDate) return false;
        return GetAvailableSlots(date).Count > 0;
    }

    /// <summary>
    /// The full picture for one day: why it is or isn't bookable, and whether anything
    /// is left. What the calendar renders from.
    /// </summary>
    public DayAvailability DescribeDay(DateTime date)
    {
        if (date.Date > LatestBookableDate)
            return new DayAvailability(DayState.NotTrading, null, false);

        var state = availability.StateOf(date);
        var reason = state == DayState.Closed ? availability.AllDayClosureOn(date)?.Reason : null;
        return new DayAvailability(state, reason, state == DayState.Open && GetAvailableSlots(date).Count > 0);
    }

    /// <summary>
    /// Upcoming, uncancelled bookings already held by this email — the figure
    /// <see cref="MaxActiveBookingsPerEmail"/> is measured against.
    ///
    /// Public because the wizard needs it too: the cap used to be discovered only at
    /// the final Confirm click, after the customer had picked a service and a slot and
    /// typed every field. Asking the same question up front is the difference between
    /// a limit and a dead end.
    /// </summary>
    public int CountUpcomingForEmail(string email)
    {
        var normEmail = email.Trim().ToLowerInvariant();
        if (normEmail.Length == 0) return 0;

        var today = ShopClock.Today;
#pragma warning disable CA1304, CA1311, CA1862
        return db.Bookings.Count(b =>
            b.CustomerEmail.ToLower() == normEmail &&
            b.SlotDate >= today &&
            b.Status != BookingStatus.Cancelled);
#pragma warning restore CA1304, CA1311, CA1862
    }

    public (Booking? booking, string? error) CreateBooking(Booking booking)
    {
        booking.CustomerEmail = booking.CustomerEmail.Trim();
        booking.CustomerPhone = booking.CustomerPhone.Trim();
        var normEmail = booking.CustomerEmail.ToLowerInvariant();

        // Checked server-side as well as hidden from the calendar: the slot list is a
        // UI convenience, and this is a public POST target. A closure added while
        // somebody had the wizard open lands here too.
        if (booking.SlotDate.Date > LatestBookableDate)
            return (null, $"We're only taking bookings up to {LatestBookableDate:d MMMM}. Please pick an earlier date.");

        switch (availability.StateOf(booking.SlotDate))
        {
            case DayState.Past:
                return (null, "That date has already passed. Please pick another.");
            case DayState.NotTrading:
                return (null, "We're not open that day. Please pick another.");
            case DayState.Closed:
                var reason = availability.AllDayClosureOn(booking.SlotDate)?.Reason;
                return (null, string.IsNullOrWhiteSpace(reason)
                    ? "We're closed that day. Please pick another."
                    : $"We're closed that day ({reason}). Please pick another.");
            case DayState.NoMechanic:
                return (null, "We've no mechanic in that day. Please pick another.");
        }

        if (availability.BlockedSlotsOn(booking.SlotDate).Contains(booking.SlotTime))
            return (null, "We're closed at that time. Please pick another slot.");

        // Their own duplicate is checked before capacity: at one booking per slot the
        // two conditions coincide, and "you already have a booking at this time" is the
        // more useful of the two answers when the clash is with themselves.
        //
        // ToLower() below is translated to SQL lower(...) by EF Core — the analyzer's
        // suggested StringComparison overload isn't SQL-translatable and would throw.
#pragma warning disable CA1304, CA1311, CA1862
        var hasDuplicate = db.Bookings.Any(b =>
            b.CustomerEmail.ToLower() == normEmail &&
            b.SlotDate == booking.SlotDate && b.SlotTime == booking.SlotTime &&
            b.Status != BookingStatus.Cancelled);
        if (hasDuplicate)
            return (null, "You already have a booking at this time.");

        var slotTaken = db.Bookings.Count(b =>
            b.SlotDate == booking.SlotDate && b.SlotTime == booking.SlotTime &&
            b.Status != BookingStatus.Cancelled);
        if (slotTaken >= MaxPerSlot)
            return (null, "Sorry, this time slot has just been taken. Please pick another time.");

#pragma warning restore CA1304, CA1311, CA1862
        var upcoming = CountUpcomingForEmail(normEmail);
        if (upcoming >= MaxActiveBookingsPerEmail)
            return (null, $"You already have {MaxActiveBookingsPerEmail} upcoming bookings — please contact us if you need to change one.");

        booking.Reference = $"FIX-{ShopClock.Now:yyMMdd}-{NextReferenceSequence(db):D3}";
        booking.CreatedAt = ShopClock.Now;
        booking.Status = BookingStatus.Pending;
        db.Bookings.Add(booking);
        try
        {
            db.SaveChanges();
        }
        catch (DbUpdateException ex)
        {
            // Lost a race against one of the slot indexes; detach so the circuit-scoped
            // context stays usable. Which index caught it decides what to say: their own
            // second booking at that time, or somebody else reaching the slot first.
            db.Entry(booking).State = EntityState.Detached;
            var constraint = (ex.InnerException as PostgresException)?.ConstraintName;
            return (null, constraint == "IX_Bookings_OneBookingPerSlot"
                ? "Sorry, this time slot has just been taken. Please pick another time."
                : "You already have a booking at this time.");
        }
        return (booking, null);
    }

    public async Task<bool> DeleteBookingAsync(string id)
    {
        var booking = db.Bookings.Include(b => b.Photos).FirstOrDefault(b => b.Id == id);
        if (booking is null) return false;

        // Best-effort: a storage hiccup shouldn't block deleting the booking itself.
        foreach (var photo in booking.Photos)
        {
            if (!await storage.DeleteAsync(photo.StoragePath))
                logger.LogWarning("Could not delete Supabase Storage object {Path} for booking {BookingId}",
                    photo.StoragePath, id);
        }

        db.Bookings.Remove(booking);
        db.SaveChanges();
        return true;
    }

    // Called once a booking row exists (see Book.razor's ConfirmBooking) so photo
    // storage paths can be scoped under the real booking id.
    public void AddPhotos(string bookingId, IEnumerable<string> storagePaths)
    {
        foreach (var path in storagePaths)
            db.BookingPhotos.Add(new BookingPhoto { BookingId = bookingId, StoragePath = path });
        db.SaveChanges();
    }

    /// <summary>
    /// One page of bookings for the admin list, filtered and searched in the database.
    ///
    /// This used to load every booking with every photo and then filter in memory on
    /// each keystroke. Photos are deliberately not included: only an expanded row
    /// needs them, and Admin.razor already fetches those lazily when a row opens.
    /// </summary>
    /// <param name="staffId">Non-null to restrict to bookings assigned to that person.</param>
    /// <returns>The page, plus the total number of matches for the pager.</returns>
    /// How the admin bookings list is ordered. <see cref="SortUpcoming"/> is the
    /// default: the shop works forwards, so what is coming up belongs at the top and
    /// history belongs below it, most recent first.
    public const string SortUpcoming = "Upcoming";
    public const string SortDateAsc  = "DateAsc";
    public const string SortDateDesc = "DateDesc";

    public static readonly string[] SortOptions = [SortUpcoming, SortDateAsc, SortDateDesc];

    /// Label for each sort, for the dashboard's picker.
    public static string SortLabel(string sort) => sort switch
    {
        SortDateAsc  => "Date: oldest first",
        SortDateDesc => "Date: newest first",
        _            => "Soonest first"
    };

    public (List<Booking> items, int total) GetBookingsPage(
        string? staffId, string filter, string? search, int page, int pageSize,
        string sort = SortUpcoming)
    {
        var query = db.Bookings.AsQueryable();

        if (staffId is not null)
            query = query.Where(b => b.AssignedStaffId == staffId);

        var today = ShopClock.Today;
        var tomorrow = today.AddDays(1);
        query = filter switch
        {
            "Today"       => query.Where(b => b.SlotDate >= today && b.SlotDate < tomorrow),
            "Pending"     => query.Where(b => b.Status == BookingStatus.Pending),
            "Confirmed"   => query.Where(b => b.Status == BookingStatus.Confirmed),
            "In Progress" => query.Where(b => b.Status == BookingStatus.InProgress),
            "Completed"   => query.Where(b => b.Status == BookingStatus.Completed),
            "Cancelled"   => query.Where(b => b.Status == BookingStatus.Cancelled),
            _             => query
        };

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLowerInvariant();
            // ToLower() is translated to SQL lower(...) by EF Core — the analyzer's
            // suggested StringComparison overload isn't SQL-translatable and would throw.
#pragma warning disable CA1304, CA1311, CA1862
            query = query.Where(b =>
                b.CustomerName.ToLower().Contains(term) ||
                b.CustomerEmail.ToLower().Contains(term) ||
                b.ServiceName.ToLower().Contains(term) ||
                b.Reference.ToLower().Contains(term));
#pragma warning restore CA1304, CA1311, CA1862
        }

        var total = query.Count();

        // Skip/Take needs a total order; SlotDate alone isn't unique, so SlotTime and
        // then Id break ties and stop a row appearing on two pages. SlotTime is a
        // zero-padded "HH:mm" string, so ordering it lexicographically is chronological.
        //
        // "Upcoming" needs three keys because it sorts the two halves of the list in
        // opposite directions. The first splits them (Postgres orders false before
        // true, putting today and later on top); the second orders the upcoming half
        // ascending and is a constant for the past half; the third does the reverse.
        // Each key is inert for the group it doesn't apply to, so neither disturbs the
        // other. All three translate to SQL.
        var ordered = sort switch
        {
            SortDateAsc  => query.OrderBy(b => b.SlotDate).ThenBy(b => b.SlotTime),
            SortDateDesc => query.OrderByDescending(b => b.SlotDate).ThenBy(b => b.SlotTime),
            _            => query
                .OrderBy(b => b.SlotDate < today)
                .ThenBy(b => b.SlotDate >= today ? b.SlotDate : DateTime.MinValue)
                .ThenByDescending(b => b.SlotDate < today ? b.SlotDate : DateTime.MinValue)
                .ThenBy(b => b.SlotTime)
        };

        var items = ordered
            .ThenBy(b => b.Id)
            .Skip(Math.Max(0, page) * pageSize)
            .Take(pageSize)
            .ToList();

        return (items, total);
    }

    /// Photos for one booking, fetched when its row is expanded rather than for the
    /// whole list up front.
    public List<BookingPhoto> GetPhotosForBooking(string bookingId) =>
        db.BookingPhotos.Where(p => p.BookingId == bookingId).ToList();

    // GetAllBookings() used to live here — every booking with every photo, then
    // filtered in memory. Superseded by GetBookingsPage above and deliberately
    // removed rather than left available, so it can't be reached for again.

    public List<Booking> GetBookingsByDate(DateTime date)
    {
        var start = date.Date;
        var end = start.AddDays(1);
        return db.Bookings
            .Include(b => b.Photos)
            .Where(b => b.SlotDate >= start && b.SlotDate < end)
            .OrderBy(b => b.SlotTime)
            .ToList();
    }

    /// <summary>
    /// Which statuses a booking can move to from where it is now.
    ///
    /// This used to be a bare setter, with the only thing constraining a transition
    /// being which buttons the dashboard happened to render — so any caller could put a
    /// booking into any state, in any order. The shape below is the one the shop
    /// actually works in: forward through the job, out to Cancelled at any point before
    /// it is finished, and back to Confirmed from either terminal state when someone
    /// mis-taps (which is the honest reason people reached for Delete).
    /// </summary>
    private static readonly Dictionary<BookingStatus, BookingStatus[]> AllowedTransitions = new()
    {
        [BookingStatus.Pending]    = [BookingStatus.Confirmed, BookingStatus.Cancelled],
        [BookingStatus.Confirmed]  = [BookingStatus.InProgress, BookingStatus.Cancelled],
        [BookingStatus.InProgress] = [BookingStatus.Completed,  BookingStatus.Cancelled],
        [BookingStatus.Completed]  = [BookingStatus.Confirmed],   // Reopen
        [BookingStatus.Cancelled]  = [BookingStatus.Confirmed]    // Reopen
    };

    /// Whether <paramref name="to"/> is reachable from <paramref name="from"/>.
    /// Public so the dashboard can decide what to offer from the same source of truth
    /// it will be judged against.
    public static bool CanTransition(BookingStatus from, BookingStatus to) =>
        AllowedTransitions.TryGetValue(from, out var allowed) && allowed.Contains(to);

    /// <summary>
    /// Whether a booking still lies ahead of the customer, for the account page's split
    /// between "Upcoming" and history.
    ///
    /// Status comes first, and the date only decides the states that are still open.
    /// Splitting on the date alone — which is what this replaced — left a job completed
    /// on its own appointment day sitting under "Upcoming Bookings", where the
    /// mechanic's report on it is not rendered at all. The report stayed invisible
    /// until the date rolled over, which is precisely when the customer is most likely
    /// to go looking for it.
    /// </summary>
    public static bool IsUpcoming(Booking booking) =>
        booking.Status is not (BookingStatus.Completed or BookingStatus.Cancelled)
        && booking.SlotDate.Date >= ShopClock.Today;

    /// The next step forward in the job, or null when there isn't one. Drives the
    /// dashboard's single primary action.
    public static BookingStatus? NextStage(BookingStatus from) => from switch
    {
        BookingStatus.Pending    => BookingStatus.Confirmed,
        BookingStatus.Confirmed  => BookingStatus.InProgress,
        BookingStatus.InProgress => BookingStatus.Completed,
        _                        => null
    };

    /// <returns>false if the booking is gone, or the transition isn't allowed.</returns>
    public bool UpdateStatus(string id, BookingStatus status)
    {
        var booking = db.Bookings.Find(id);
        if (booking is null) return false;
        if (!CanTransition(booking.Status, status)) return false;
        booking.Status = status;
        db.SaveChanges();
        return true;
    }

    /// How close to the slot a customer can still cancel themselves. Inside this
    /// window the mechanic may already have set aside the time, so it becomes a phone
    /// call rather than a silent no-show.
    public static readonly TimeSpan SelfCancelCutoff = TimeSpan.FromHours(2);

    /// <summary>
    /// Whether a booking is at a stage the customer may still call off themselves.
    ///
    /// Deliberately narrower than <see cref="CanTransition"/>, which governs what
    /// *staff* may do: staff can cancel a job mid-repair, because they're the ones
    /// holding the bike. A customer can't — once a mechanic has it apart, calling the
    /// job off is a conversation about parts already fitted, not a button.
    /// </summary>
    public static bool CanCustomerCancel(BookingStatus status) =>
        status is BookingStatus.Pending or BookingStatus.Confirmed;

    /// <summary>
    /// SlotTime is "HH:mm" and SlotDate is midnight, so neither alone says when the
    /// appointment actually starts.
    /// </summary>
    public static DateTime SlotStart(Booking booking) =>
        TimeOnly.TryParse(booking.SlotTime, CultureInfo.InvariantCulture, out var t)
            ? booking.SlotDate.Date.Add(t.ToTimeSpan())
            : booking.SlotDate.Date;

    /// <summary>
    /// Why this customer can't cancel this booking themselves, or null if they can.
    ///
    /// One rule, read by both sides: the account page calls it to decide whether to
    /// offer a Cancel button or a phone number, and CancelOwnBooking calls it to decide
    /// what to allow. That's what stops the button and the rule drifting apart — which
    /// is exactly how a Cancel button came to sit on jobs already in progress.
    /// </summary>
    public static string? SelfCancelBlockedReason(Booking booking)
    {
        if (booking.Status == BookingStatus.Cancelled)
            return "That booking is already cancelled.";
        if (booking.Status == BookingStatus.Completed)
            return "That booking is already completed.";
        if (!CanCustomerCancel(booking.Status))
            return "We've already started work on this one — please call us on " +
                   $"{SiteContent.PhoneDisplay} and we'll sort it out with you.";
        if (SlotStart(booking) - ShopClock.Now < SelfCancelCutoff)
            return $"This booking is less than {SelfCancelCutoff.TotalHours:0} hours away — " +
                   $"please call us on {SiteContent.PhoneDisplay} so we can free the slot properly.";
        return null;
    }

    /// <summary>
    /// Cancels a booking on the customer's own behalf.
    ///
    /// customerId is part of the lookup rather than checked afterwards, so one
    /// customer can never cancel another's booking by guessing an id — the same
    /// pattern BikeService.RemoveBike uses. Returns the cancelled booking so the
    /// caller can notify the shop.
    /// </summary>
    public (Booking? booking, string? error) CancelOwnBooking(string customerId, string bookingId)
    {
        var booking = db.Bookings.FirstOrDefault(b => b.Id == bookingId && b.CustomerId == customerId);
        if (booking is null)
            return (null, "We couldn't find that booking.");

        var blocked = SelfCancelBlockedReason(booking);
        if (blocked is not null)
            return (null, blocked);

        booking.Status = BookingStatus.Cancelled;
        db.SaveChanges();
        return (booking, null);
    }

    // Batched: availability for every day in the given month in a handful of queries
    // rather than a handful per day. A day is available when it's open (trading, not
    // closed, a mechanic in) and has at least one slot left.
    public Dictionary<DateTime, bool> GetDateAvailabilityForMonth(int year, int month) =>
        DescribeMonth(year, month).ToDictionary(kv => kv.Key, kv => kv.Value.IsBookable);

    /// <summary>
    /// Every day in a month with the reason behind its state, so the calendar can say
    /// "Closed — Christmas" rather than greying a day out silently.
    /// </summary>
    public Dictionary<DateTime, DayAvailability> DescribeMonth(int year, int month)
    {
        var start = new DateTime(year, month, 1);
        var end = start.AddMonths(1);
        var today = ShopClock.Today;
        var now = ShopClock.Now;
        var horizon = LatestBookableDate;

        // One round-trip: booked count per (date, slot) for the whole month
        var bookedPerSlot = db.Bookings
            .Where(b => b.SlotDate >= start && b.SlotDate < end && b.Status != BookingStatus.Cancelled)
            .GroupBy(b => new { b.SlotDate, b.SlotTime })
            .Select(g => new { g.Key.SlotDate, g.Key.SlotTime, Count = g.Count() })
            .ToList();

        var fullSlotsByDate = bookedPerSlot
            .GroupBy(x => x.SlotDate)
            .ToDictionary(
                g => g.Key,
                g => g.Where(x => x.Count >= MaxPerSlot).Select(x => x.SlotTime).ToHashSet());

        // Three more queries for the whole month, not three per day.
        var states = availability.StatesForMonth(year, month);

        var result = new Dictionary<DateTime, DayAvailability>();
        for (var d = start; d < end; d = d.AddDays(1))
        {
            var (state, reason, blocked) = states[d];

            // Past the horizon reads as "not trading" rather than "closed": the shop
            // isn't shut, it just isn't taking bookings that far out yet.
            if (state == DayState.Open && d.Date > horizon)
            {
                result[d] = new DayAvailability(DayState.NotTrading, null, false);
                continue;
            }

            if (state != DayState.Open)
            {
                result[d] = new DayAvailability(state, reason, false);
                continue;
            }

            var full = fullSlotsByDate.TryGetValue(d, out var set) ? set : [];
            var day = d;
            var hasFree = SlotsFor(d).Any(slot =>
            {
                if (blocked.Contains(slot)) return false;
                if (day == today && TimeOnly.TryParse(slot, out var t) && t <= TimeOnly.FromDateTime(now))
                    return false;
                return !full.Contains(slot);
            });

            result[d] = new DayAvailability(DayState.Open, null, hasFree);
        }
        return result;
    }

    public List<Booking> GetBookingsForStaff(string staffId) =>
        db.Bookings
          .Where(b => b.AssignedStaffId == staffId)
          .OrderByDescending(b => b.SlotDate)
          .ThenBy(b => b.SlotTime)
          .ToList();

    // Only surfaces bookings made while the customer was logged in (ConfirmBooking
    // only stamps CustomerId then) — guest bookings under the same email won't
    // retroactively appear here.
    public List<Booking> GetBookingsForCustomer(string customerId) =>
        db.Bookings
          .Where(b => b.CustomerId == customerId)
          .OrderByDescending(b => b.SlotDate)
          .ThenByDescending(b => b.SlotTime)
          .ToList();

    /// <summary>
    /// Moves a booking to a different date and slot, keeping its reference and history.
    /// </summary>
    /// <remarks>
    /// <para>The alternative — cancel and rebook — loses the reference the customer
    /// has in their confirmation email, and drops the notes and photos attached to the
    /// job. When a closure displaces someone, moving them is what you actually want.</para>
    ///
    /// <para>Goes through the same availability checks a new booking does, and the same
    /// unique index catches a race for the target slot. Clears
    /// <c>ReminderSentAt</c>: the reminder that already went out named the old date, so
    /// the customer is owed a fresh one.</para>
    /// </remarks>
    public (Booking? booking, string? error) RescheduleBooking(string bookingId, DateTime newDate, string newSlot)
    {
        var booking = db.Bookings.Find(bookingId);
        if (booking is null) return (null, "That booking no longer exists.");

        if (booking.Status is BookingStatus.Cancelled or BookingStatus.Completed)
            return (null, "That booking is already closed — it can't be moved.");

        if (booking.SlotDate.Date == newDate.Date && booking.SlotTime == newSlot)
            return (booking, null);   // nothing to do

        if (newDate.Date < ShopClock.Today)
            return (null, "That date has already passed.");
        if (newDate.Date > LatestBookableDate)
            return (null, $"We're only booking up to {LatestBookableDate:d MMMM}.");
        if (availability.StateOf(newDate) != DayState.Open)
            return (null, "We're not open that day.");
        if (!SlotsFor(newDate).Contains(newSlot))
            return (null, "That isn't one of the day's slots.");
        if (availability.BlockedSlotsOn(newDate).Contains(newSlot))
            return (null, "We're closed at that time.");

        var taken = db.Bookings.Any(b =>
            b.Id != bookingId && b.SlotDate == newDate.Date && b.SlotTime == newSlot &&
            b.Status != BookingStatus.Cancelled);
        if (taken) return (null, "Something else is already booked into that slot.");

        booking.SlotDate       = newDate.Date;
        booking.SlotTime       = newSlot;
        booking.ReminderSentAt = null;

        try
        {
            db.SaveChanges();
        }
        catch (DbUpdateException)
        {
            // Lost a race for the slot against a customer booking it at the same moment.
            db.Entry(booking).State = EntityState.Detached;
            return (null, "Something else was just booked into that slot. Please pick another.");
        }

        return (booking, null);
    }

    public bool AssignStaff(string bookingId, string? staffId)
    {
        var booking = db.Bookings.Find(bookingId);
        if (booking is null) return false;
        booking.AssignedStaffId = string.IsNullOrEmpty(staffId) ? null : staffId;
        db.SaveChanges();
        return true;
    }

    public (int total, int today, int pending, int confirmed) GetStats() =>
        GetStatsCore(db.Bookings);

    public (int total, int today, int pending, int confirmed) GetStatsForStaff(string staffId) =>
        GetStatsCore(db.Bookings.Where(b => b.AssignedStaffId == staffId));

    // One grouped query with conditional counts instead of 4 separate Count()
    // round-trips per call site.
    private static (int total, int today, int pending, int confirmed) GetStatsCore(IQueryable<Booking> query)
    {
        var todayStart = ShopClock.Today;
        var tomorrowStart = todayStart.AddDays(1);

        var row = query
            .GroupBy(_ => 1)
            .Select(g => new
            {
                Total     = g.Count(),
                Today     = g.Count(b => b.SlotDate >= todayStart && b.SlotDate < tomorrowStart),
                Pending   = g.Count(b => b.Status == BookingStatus.Pending),
                Confirmed = g.Count(b => b.Status == BookingStatus.Confirmed)
            })
            .FirstOrDefault();

        return row is null ? (0, 0, 0, 0) : (row.Total, row.Today, row.Pending, row.Confirmed);
    }
}
