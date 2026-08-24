using Fixlosophy.Data;
using Fixlosophy.Services;
using Microsoft.EntityFrameworkCore;

namespace Fixlosophy.Tests;

public class BookingServiceTests
{
    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    // The service filters out Sundays, past dates, and (for today only) slots whose
    // time has already passed. Anchoring on a future weekday keeps these tests from
    // depending on the wall clock at the moment they run.
    private static DateTime FutureWorkday(int daysAhead = 7)
    {
        var date = DateTime.Today.AddDays(daysAhead);
        while (date.DayOfWeek == DayOfWeek.Sunday) date = date.AddDays(1);
        return date;
    }

    private static DateTime NextSunday()
    {
        var date = DateTime.Today.AddDays(1);
        while (date.DayOfWeek != DayOfWeek.Sunday) date = date.AddDays(1);
        return date;
    }

    // Seeds rows straight through the DbContext so arrange steps aren't themselves
    // subject to the booking rules under test.
    private static Booking Seed(
        AppDbContext db, DateTime slotDate, string slotTime,
        string email = "seed@example.com",
        BookingStatus status = BookingStatus.Confirmed,
        string? assignedStaffId = null)
    {
        var booking = new Booking
        {
            CustomerName = "Seed Customer",
            CustomerEmail = email,
            SlotDate = slotDate,
            SlotTime = slotTime,
            Status = status,
            AssignedStaffId = assignedStaffId
        };
        db.Bookings.Add(booking);
        db.SaveChanges();
        return booking;
    }

    private static Booking NewBooking(DateTime slotDate, string slotTime, string email) => new()
    {
        CustomerName = "Jane Doe",
        CustomerEmail = email,
        ServiceCategory = "Servicing Packages",
        ServiceName = "Basic Service",
        ServicePrice = 35m,
        SlotDate = slotDate,
        SlotTime = slotTime
    };

    [Fact]
    public void GetAvailableSlots_ReturnsEmpty_OnSunday()
    {
        using var db = NewDb();
        Assert.Empty(new BookingService(db).GetAvailableSlots(NextSunday()));
    }

    [Fact]
    public void GetAvailableSlots_ReturnsEmpty_ForPastDate()
    {
        using var db = NewDb();
        Assert.Empty(new BookingService(db).GetAvailableSlots(DateTime.Today.AddDays(-1)));
    }

    [Fact]
    public void GetAvailableSlots_ReturnsEverySlot_WhenNothingIsBooked()
    {
        using var db = NewDb();
        var slots = new BookingService(db).GetAvailableSlots(FutureWorkday());
        Assert.Equal(BookingService.TimeSlots, slots);
    }

    [Fact]
    public void GetAvailableSlots_ExcludesSlotAtCapacity()
    {
        using var db = NewDb();
        var date = FutureWorkday();
        for (var i = 0; i < BookingService.MaxPerSlot; i++)
            Seed(db, date, "09:00", email: $"filler{i}@example.com");

        var slots = new BookingService(db).GetAvailableSlots(date);

        Assert.DoesNotContain("09:00", slots);
        Assert.Contains("10:00", slots);
    }

    [Fact]
    public void GetAvailableSlots_KeepsSlotOpen_BelowCapacity()
    {
        using var db = NewDb();
        var date = FutureWorkday();
        Seed(db, date, "09:00");

        Assert.Contains("09:00", new BookingService(db).GetAvailableSlots(date));
    }

    [Fact]
    public void GetAvailableSlots_IgnoresCancelledBookings()
    {
        using var db = NewDb();
        var date = FutureWorkday();
        for (var i = 0; i < BookingService.MaxPerSlot; i++)
            Seed(db, date, "09:00", email: $"filler{i}@example.com", status: BookingStatus.Cancelled);

        Assert.Contains("09:00", new BookingService(db).GetAvailableSlots(date));
    }

    [Fact]
    public void CreateBooking_AssignsReferenceAndPendingStatus()
    {
        using var db = NewDb();
        var date = FutureWorkday();

        var (created, error) = new BookingService(db)
            .CreateBooking(NewBooking(date, "09:00", "jane@example.com"));

        Assert.Null(error);
        Assert.NotNull(created);
        Assert.Equal(BookingStatus.Pending, created.Status);
        Assert.StartsWith("FIX-", created.Reference);
        Assert.Single(db.Bookings);
    }

    [Fact]
    public void CreateBooking_TrimsCustomerEmail()
    {
        using var db = NewDb();

        var (created, error) = new BookingService(db)
            .CreateBooking(NewBooking(FutureWorkday(), "09:00", "  jane@example.com  "));

        Assert.Null(error);
        Assert.Equal("jane@example.com", created!.CustomerEmail);
    }

    [Fact]
    public void CreateBooking_RejectsWhenSlotIsFull()
    {
        using var db = NewDb();
        var date = FutureWorkday();
        for (var i = 0; i < BookingService.MaxPerSlot; i++)
            Seed(db, date, "09:00", email: $"filler{i}@example.com");

        var (created, error) = new BookingService(db)
            .CreateBooking(NewBooking(date, "09:00", "jane@example.com"));

        Assert.Null(created);
        Assert.Contains("just filled up", error);
    }

    [Fact]
    public void CreateBooking_RejectsDuplicateForSameSlot_IgnoringEmailCase()
    {
        using var db = NewDb();
        var date = FutureWorkday();
        Seed(db, date, "09:00", email: "Jane@Example.com");

        var (created, error) = new BookingService(db)
            .CreateBooking(NewBooking(date, "09:00", "jane@example.com"));

        Assert.Null(created);
        Assert.Contains("already have a booking", error);
    }

    [Fact]
    public void CreateBooking_RejectsWhenCustomerHasTooManyUpcomingBookings()
    {
        using var db = NewDb();
        // Distinct days so neither the slot-capacity nor the duplicate-slot rule
        // fires before the upcoming-bookings cap does.
        for (var i = 0; i < BookingService.MaxActiveBookingsPerEmail; i++)
            Seed(db, FutureWorkday(i + 1), "09:00", email: "jane@example.com");

        var (created, error) = new BookingService(db).CreateBooking(
            NewBooking(FutureWorkday(BookingService.MaxActiveBookingsPerEmail + 2), "11:00", "jane@example.com"));

        Assert.Null(created);
        Assert.Contains("upcoming bookings", error);
    }

    [Fact]
    public void CreateBooking_IgnoresCancelledBookings_WhenCountingUpcoming()
    {
        using var db = NewDb();
        for (var i = 0; i < BookingService.MaxActiveBookingsPerEmail; i++)
            Seed(db, FutureWorkday(i + 1), "09:00", email: "jane@example.com",
                 status: BookingStatus.Cancelled);

        var (created, error) = new BookingService(db).CreateBooking(
            NewBooking(FutureWorkday(BookingService.MaxActiveBookingsPerEmail + 2), "11:00", "jane@example.com"));

        Assert.Null(error);
        Assert.NotNull(created);
    }

    [Fact]
    public void GetDateAvailabilityForMonth_MarksSundaysUnavailable()
    {
        using var db = NewDb();
        var nextMonth = DateTime.Today.AddMonths(1);

        var availability = new BookingService(db)
            .GetDateAvailabilityForMonth(nextMonth.Year, nextMonth.Month);

        Assert.All(availability.Where(kv => kv.Key.DayOfWeek == DayOfWeek.Sunday),
                   kv => Assert.False(kv.Value));
        Assert.Contains(availability, kv => kv.Value);
    }

    [Fact]
    public void GetDateAvailabilityForMonth_MarksDayUnavailable_WhenEverySlotIsFull()
    {
        using var db = NewDb();
        var nextMonth = DateTime.Today.AddMonths(1);
        var target = new DateTime(nextMonth.Year, nextMonth.Month, 1);
        while (target.DayOfWeek == DayOfWeek.Sunday) target = target.AddDays(1);

        var filler = 0;
        foreach (var slot in BookingService.TimeSlots)
            for (var i = 0; i < BookingService.MaxPerSlot; i++)
                Seed(db, target, slot, email: $"filler{filler++}@example.com");

        var availability = new BookingService(db)
            .GetDateAvailabilityForMonth(target.Year, target.Month);

        Assert.False(availability[target]);
    }

    [Fact]
    public void UpdateStatus_ChangesStoredStatus()
    {
        using var db = NewDb();
        var booking = Seed(db, FutureWorkday(), "09:00", status: BookingStatus.Pending);

        new BookingService(db).UpdateStatus(booking.Id, BookingStatus.Confirmed);

        Assert.Equal(BookingStatus.Confirmed, db.Bookings.Single().Status);
    }

    [Fact]
    public void UpdateStatus_IsNoOp_ForUnknownId()
    {
        using var db = NewDb();
        Seed(db, FutureWorkday(), "09:00", status: BookingStatus.Pending);

        new BookingService(db).UpdateStatus("does-not-exist", BookingStatus.Cancelled);

        Assert.Equal(BookingStatus.Pending, db.Bookings.Single().Status);
    }

    [Fact]
    public void DeleteBooking_RemovesBooking_AndIsNoOpForUnknownId()
    {
        using var db = NewDb();
        var booking = Seed(db, FutureWorkday(), "09:00");
        var service = new BookingService(db);

        service.DeleteBooking("does-not-exist");
        Assert.Single(db.Bookings);

        service.DeleteBooking(booking.Id);
        Assert.Empty(db.Bookings);
    }

    [Fact]
    public void AssignStaff_SetsAndClearsAssignment()
    {
        using var db = NewDb();
        var booking = Seed(db, FutureWorkday(), "09:00");
        var service = new BookingService(db);

        service.AssignStaff(booking.Id, "staff-1");
        Assert.Equal("staff-1", db.Bookings.Single().AssignedStaffId);

        // An empty selection from the dropdown means "unassigned", not an empty string.
        service.AssignStaff(booking.Id, "");
        Assert.Null(db.Bookings.Single().AssignedStaffId);
    }

    [Fact]
    public void GetStats_CountsTotalTodayAndStatuses()
    {
        using var db = NewDb();
        var future = FutureWorkday();
        var today = DateTime.Today;
        Seed(db, future, "09:00", email: "a@example.com", status: BookingStatus.Pending);
        Seed(db, future, "10:00", email: "b@example.com", status: BookingStatus.Confirmed);
        Seed(db, future, "11:00", email: "c@example.com", status: BookingStatus.Completed);
        Seed(db, today, "12:00", email: "d@example.com", status: BookingStatus.Pending);

        var stats = new BookingService(db).GetStats();

        Assert.Equal(4, stats.total);
        Assert.Equal(1, stats.today);
        Assert.Equal(2, stats.pending);
        Assert.Equal(1, stats.confirmed);
    }

    [Fact]
    public void GetStatsForStaff_OnlyCountsAssignedBookings()
    {
        using var db = NewDb();
        var future = FutureWorkday();
        Seed(db, future, "09:00", email: "a@example.com", status: BookingStatus.Pending, assignedStaffId: "staff-1");
        Seed(db, future, "10:00", email: "b@example.com", status: BookingStatus.Confirmed, assignedStaffId: "staff-1");
        Seed(db, future, "11:00", email: "c@example.com", status: BookingStatus.Pending, assignedStaffId: "staff-2");
        Seed(db, future, "12:00", email: "d@example.com", status: BookingStatus.Pending);

        var stats = new BookingService(db).GetStatsForStaff("staff-1");

        Assert.Equal(2, stats.total);
        Assert.Equal(1, stats.pending);
        Assert.Equal(1, stats.confirmed);
    }

    [Fact]
    public void GetBookingsForStaff_ReturnsOnlyThatStaffMembersBookings()
    {
        using var db = NewDb();
        var future = FutureWorkday();
        Seed(db, future, "09:00", email: "a@example.com", assignedStaffId: "staff-1");
        Seed(db, future, "10:00", email: "b@example.com", assignedStaffId: "staff-2");
        Seed(db, future, "11:00", email: "c@example.com");

        var bookings = new BookingService(db).GetBookingsForStaff("staff-1");

        Assert.Equal("a@example.com", Assert.Single(bookings).CustomerEmail);
    }

    [Fact]
    public void GetBookingsByDate_ReturnsOnlyThatDay_OrderedBySlotTime()
    {
        using var db = NewDb();
        var date = FutureWorkday();
        Seed(db, date, "15:00", email: "late@example.com");
        Seed(db, date, "09:00", email: "early@example.com");
        Seed(db, date.AddDays(1), "10:00", email: "other@example.com");

        var bookings = new BookingService(db).GetBookingsByDate(date);

        Assert.Equal(["09:00", "15:00"], bookings.Select(b => b.SlotTime));
    }
}
