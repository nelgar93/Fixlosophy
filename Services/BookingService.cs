using Fixlosophy.Data;
using Microsoft.EntityFrameworkCore;

namespace Fixlosophy.Services;

public class BookingService(AppDbContext db)
{
    public const int MaxPerSlot = 2;
    public const int MaxActiveBookingsPerEmail = 3;

    public static readonly string[] TimeSlots =
        ["09:00", "10:00", "11:00", "12:00", "14:00", "15:00", "16:00", "17:00", "18:00"];

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

    public void UpdateServicePrice(string id, decimal price)
    {
        var sp = db.ServicePricings.Find(id);
        if (sp == null) return;
        sp.CurrentPrice = price;
        db.SaveChanges();
    }

    public List<PriceAdjustment> GetPriceAdjustments() =>
        db.PriceAdjustments.OrderByDescending(a => a.AppliedAt).ToList();

    public List<string> GetAvailableSlots(DateTime date)
    {
        if (date.DayOfWeek == DayOfWeek.Sunday || date.Date < DateTime.Today)
            return [];

        var start = date.Date;
        var end = start.AddDays(1);

        var booked = db.Bookings
            .Where(b => b.SlotDate >= start && b.SlotDate < end && b.Status != BookingStatus.Cancelled)
            .GroupBy(b => b.SlotTime)
            .Select(g => new { Slot = g.Key, Count = g.Count() })
            .ToDictionary(x => x.Slot, x => x.Count);

        return TimeSlots.Where(slot =>
        {
            if (date.Date == DateTime.Today && TimeOnly.TryParse(slot, out var t) && t <= TimeOnly.FromDateTime(DateTime.Now))
                return false;
            return !booked.TryGetValue(slot, out var count) || count < MaxPerSlot;
        }).ToList();
    }

    public bool IsDateAvailable(DateTime date)
    {
        if (date.DayOfWeek == DayOfWeek.Sunday || date.Date < DateTime.Today) return false;
        return GetAvailableSlots(date).Count > 0;
    }

    public (Booking? booking, string? error) CreateBooking(Booking booking)
    {
        booking.CustomerEmail = booking.CustomerEmail.Trim();
        var normEmail = booking.CustomerEmail.ToLower();
        var today = DateTime.Today;

        var slotTaken = db.Bookings.Count(b =>
            b.SlotDate == booking.SlotDate && b.SlotTime == booking.SlotTime &&
            b.Status != BookingStatus.Cancelled);
        if (slotTaken >= MaxPerSlot)
            return (null, "Sorry, this time slot has just filled up. Please pick another time.");

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
        if (upcoming >= MaxActiveBookingsPerEmail)
            return (null, $"You already have {MaxActiveBookingsPerEmail} upcoming bookings — please contact us if you need to change one.");

        var seq = db.Bookings.Count() + 1;
        booking.Reference = $"FIX-{DateTime.Now:yyMMdd}-{seq:D3}";
        booking.CreatedAt = DateTime.Now;
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

    public void DeleteBooking(string id)
    {
        var booking = db.Bookings.Find(id);
        if (booking is null) return;
        db.Bookings.Remove(booking);
        db.SaveChanges();
    }

    public List<Booking> GetAllBookings() =>
        db.Bookings
          .OrderByDescending(b => b.SlotDate)
          .ThenBy(b => b.SlotTime)
          .ToList();

    public List<Booking> GetBookingsByDate(DateTime date)
    {
        var start = date.Date;
        var end = start.AddDays(1);
        return db.Bookings
            .Where(b => b.SlotDate >= start && b.SlotDate < end)
            .OrderBy(b => b.SlotTime)
            .ToList();
    }

    public void UpdateStatus(string id, BookingStatus status)
    {
        var booking = db.Bookings.Find(id);
        if (booking is null) return;
        booking.Status = status;
        db.SaveChanges();
    }

    // Single DB query: returns true/false availability for every day in the given month.
    // A day is available when it has at least one open slot (not at MaxPerSlot capacity).
    public Dictionary<DateTime, bool> GetDateAvailabilityForMonth(int year, int month)
    {
        var start = new DateTime(year, month, 1);
        var end = start.AddMonths(1);
        var today = DateTime.Today;
        var now = DateTime.Now;

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
            if (d.DayOfWeek == DayOfWeek.Sunday || d.Date < today) { result[d] = false; continue; }

            var full = fullSlotsByDate.TryGetValue(d, out var set) ? set : [];
            result[d] = TimeSlots.Any(slot =>
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

    public void AssignStaff(string bookingId, string? staffId)
    {
        var booking = db.Bookings.Find(bookingId);
        if (booking is null) return;
        booking.AssignedStaffId = string.IsNullOrEmpty(staffId) ? null : staffId;
        db.SaveChanges();
    }

    public (int total, int today, int pending, int confirmed) GetStats()
    {
        var todayStart = DateTime.Today;
        var tomorrowStart = todayStart.AddDays(1);
        return (
            db.Bookings.Count(),
            db.Bookings.Count(b => b.SlotDate >= todayStart && b.SlotDate < tomorrowStart),
            db.Bookings.Count(b => b.Status == BookingStatus.Pending),
            db.Bookings.Count(b => b.Status == BookingStatus.Confirmed)
        );
    }

    public (int total, int today, int pending, int confirmed) GetStatsForStaff(string staffId)
    {
        var todayStart = DateTime.Today;
        var tomorrowStart = todayStart.AddDays(1);
        var q = db.Bookings.Where(b => b.AssignedStaffId == staffId);
        return (
            q.Count(),
            q.Count(b => b.SlotDate >= todayStart && b.SlotDate < tomorrowStart),
            q.Count(b => b.Status == BookingStatus.Pending),
            q.Count(b => b.Status == BookingStatus.Confirmed)
        );
    }
}
