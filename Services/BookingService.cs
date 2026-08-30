using System.Globalization;
using Fixlosophy.Data;
using Microsoft.EntityFrameworkCore;

namespace Fixlosophy.Services;

public class BookingService(AppDbContext db, IStorageService storage, ILogger<BookingService> logger)
{
    public const int MaxPerSlot = 2;
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
        if (date.Date < ShopClock.Today)
            return [];

        var start = date.Date;
        var end = start.AddDays(1);

        var booked = db.Bookings
            .Where(b => b.SlotDate >= start && b.SlotDate < end && b.Status != BookingStatus.Cancelled)
            .GroupBy(b => b.SlotTime)
            .Select(g => new { Slot = g.Key, Count = g.Count() })
            .ToDictionary(x => x.Slot, x => x.Count);

        return SlotsFor(date).Where(slot =>
        {
            if (date.Date == ShopClock.Today && TimeOnly.TryParse(slot, out var t) && t <= TimeOnly.FromDateTime(ShopClock.Now))
                return false;
            return !booked.TryGetValue(slot, out var count) || count < MaxPerSlot;
        }).ToList();
    }

    public bool IsDateAvailable(DateTime date)
    {
        if (date.Date < ShopClock.Today) return false;
        return GetAvailableSlots(date).Count > 0;
    }

    public (Booking? booking, string? error) CreateBooking(Booking booking)
    {
        booking.CustomerEmail = booking.CustomerEmail.Trim();
        booking.CustomerPhone = booking.CustomerPhone.Trim();
        var normEmail = booking.CustomerEmail.ToLowerInvariant();
        var today = ShopClock.Today;

        var slotTaken = db.Bookings.Count(b =>
            b.SlotDate == booking.SlotDate && b.SlotTime == booking.SlotTime &&
            b.Status != BookingStatus.Cancelled);
        if (slotTaken >= MaxPerSlot)
            return (null, "Sorry, this time slot has just filled up. Please pick another time.");

        // ToLower() below is translated to SQL lower(...) by EF Core — the analyzer's
        // suggested StringComparison overload isn't SQL-translatable and would throw.
#pragma warning disable CA1304, CA1311, CA1862
        var hasDuplicate = db.Bookings.Any(b =>
            b.CustomerEmail.ToLower() == normEmail &&
            b.SlotDate == booking.SlotDate && b.SlotTime == booking.SlotTime &&
            b.Status != BookingStatus.Cancelled);
        if (hasDuplicate)
            return (null, "You already have a booking at this time.");

        var upcoming = db.Bookings.Count(b =>
            b.CustomerEmail.ToLower() == normEmail &&
            b.SlotDate >= today &&
            b.Status != BookingStatus.Cancelled);
#pragma warning restore CA1304, CA1311, CA1862
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
        catch (DbUpdateException)
        {
            // Lost a race against IX_Bookings_NoDuplicateSlot; detach so the
            // circuit-scoped context stays usable.
            db.Entry(booking).State = EntityState.Detached;
            return (null, "You already have a booking at this time.");
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
    public (List<Booking> items, int total) GetBookingsPage(
        string? staffId, string filter, string? search, int page, int pageSize)
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

        // Skip/Take needs a total order; SlotDate alone isn't unique, so Id breaks ties
        // and stops a row appearing on two pages.
        var items = query
            .OrderByDescending(b => b.SlotDate)
            .ThenBy(b => b.SlotTime)
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

    public bool UpdateStatus(string id, BookingStatus status)
    {
        var booking = db.Bookings.Find(id);
        if (booking is null) return false;
        booking.Status = status;
        db.SaveChanges();
        return true;
    }

    /// How close to the slot a customer can still cancel themselves. Inside this
    /// window the mechanic may already have set aside the time, so it becomes a phone
    /// call rather than a silent no-show.
    public static readonly TimeSpan SelfCancelCutoff = TimeSpan.FromHours(2);

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

        if (booking.Status == BookingStatus.Cancelled)
            return (null, "That booking is already cancelled.");
        if (booking.Status == BookingStatus.Completed)
            return (null, "That booking is already completed.");

        // SlotTime is "HH:mm"; combine it with the date so the cutoff compares against
        // the actual appointment, not midnight on the day.
        var slotStart = booking.SlotDate.Date;
        if (TimeOnly.TryParse(booking.SlotTime, CultureInfo.InvariantCulture, out var t))
            slotStart = slotStart.Add(t.ToTimeSpan());

        if (slotStart - ShopClock.Now < SelfCancelCutoff)
            return (null, $"This booking is less than {SelfCancelCutoff.TotalHours:0} hours away — " +
                          $"please call us on {SiteContent.PhoneDisplay} so we can free the slot properly.");

        booking.Status = BookingStatus.Cancelled;
        db.SaveChanges();
        return (booking, null);
    }

    // Single DB query: returns true/false availability for every day in the given month.
    // A day is available when it has at least one open slot (not at MaxPerSlot capacity).
    public Dictionary<DateTime, bool> GetDateAvailabilityForMonth(int year, int month)
    {
        var start = new DateTime(year, month, 1);
        var end = start.AddMonths(1);
        var today = ShopClock.Today;
        var now = ShopClock.Now;

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

        var result = new Dictionary<DateTime, bool>();
        for (var d = start; d < end; d = d.AddDays(1))
        {
            if (d.Date < today) { result[d] = false; continue; }

            var full = fullSlotsByDate.TryGetValue(d, out var set) ? set : [];
            result[d] = SlotsFor(d).Any(slot =>
            {
                if (d == today && TimeOnly.TryParse(slot, out var t) && t <= TimeOnly.FromDateTime(now))
                    return false;
                return !full.Contains(slot);
            });
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
