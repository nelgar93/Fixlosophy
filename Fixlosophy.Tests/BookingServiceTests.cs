using Fixlosophy.Data;
using Fixlosophy.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fixlosophy.Tests;

public class BookingServiceTests
{
    // No-op IStorageService — these tests exercise booking logic, not Supabase
    // Storage, and a real StorageService needs HttpClient/config neither of which
    // exist in this in-memory-DB test setup.
    private sealed class FakeStorageService : IStorageService
    {
        public string? ValidatePhoto(string contentType, long size) => null;
        public Task<(string? path, string? error)> UploadCustomerPhotoAsync(string bookingId, string contentType, byte[] content) =>
            Task.FromResult<(string?, string?)>(("fake/path.jpg", null));
        public Task<string?> GetSignedPhotoUrlAsync(string storagePath, TimeSpan expiry) =>
            Task.FromResult<string?>("https://example.com/signed");
        public Task<bool> DeleteAsync(string storagePath) => Task.FromResult(true);
        public string GetPublicWebsiteImageUrl(string fileName) => $"https://example.com/{fileName}";
    }

    private static BookingService NewService(AppDbContext db) =>
        new(db, new FakeStorageService(), NullLogger<BookingService>.Instance);

    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    // The service filters out past dates and (for today only) slots whose time has
    // already passed. Sundays are open but run a shorter 11–17 slot list, so
    // anchoring on a future non-Sunday keeps these tests off both the wall clock
    // and the Sunday branch.
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
    public void GetAvailableSlots_ReturnsShorterSundayList_OnSunday()
    {
        using var db = NewDb();
        var sunday = NextSunday();
        var slots = NewService(db).GetAvailableSlots(sunday);

        Assert.Equal(BookingService.SlotsFor(sunday), slots);
        // The 9am and 6pm weekday slots fall outside Sunday's 11–17 trading hours.
        Assert.DoesNotContain("09:00", slots);
        Assert.DoesNotContain("18:00", slots);
    }

    // Saturday closes at 18:00, an hour earlier than the 19:00 weekday close, so its
    // last bookable slot is 17:00. This used to share the weekday list and offered an
    // 18:00 appointment for the moment the shop locks up.
    [Fact]
    public void SlotsFor_StopsAnHourBeforeClosing_OnSaturday()
    {
        var saturday = DateTime.Today.AddDays(1);
        while (saturday.DayOfWeek != DayOfWeek.Saturday) saturday = saturday.AddDays(1);

        var slots = BookingService.SlotsFor(saturday);

        Assert.Equal("17:00", slots[^1]);
        Assert.DoesNotContain("18:00", slots);
        Assert.Contains("09:00", slots);
    }

    // 13:00 is lunch on every trading day.
    [Fact]
    public void SlotsFor_SkipsTheLunchHour_OnEveryOpenDay()
    {
        foreach (var day in Enum.GetValues<DayOfWeek>())
        {
            var slots = BookingService.SlotsFor(DateTime.Today.AddDays(((int)day - (int)DateTime.Today.DayOfWeek + 7) % 7));
            Assert.DoesNotContain("13:00", slots);
        }
    }

    [Fact]
    public void GetAvailableSlots_ReturnsEmpty_ForPastDate()
    {
        using var db = NewDb();
        Assert.Empty(NewService(db).GetAvailableSlots(DateTime.Today.AddDays(-1)));
    }

    [Fact]
    public void GetAvailableSlots_ReturnsEverySlot_WhenNothingIsBooked()
    {
        using var db = NewDb();
        var date = FutureWorkday();
        var slots = NewService(db).GetAvailableSlots(date);
        Assert.Equal(BookingService.SlotsFor(date), slots);
    }

    [Fact]
    public void GetAvailableSlots_ExcludesSlotAtCapacity()
    {
        using var db = NewDb();
        var date = FutureWorkday();
        for (var i = 0; i < BookingService.MaxPerSlot; i++)
            Seed(db, date, "09:00", email: $"filler{i}@example.com");

        var slots = NewService(db).GetAvailableSlots(date);

        Assert.DoesNotContain("09:00", slots);
        Assert.Contains("10:00", slots);
    }

    [Fact]
    public void GetAvailableSlots_KeepsSlotOpen_BelowCapacity()
    {
        using var db = NewDb();
        var date = FutureWorkday();
        Seed(db, date, "09:00");

        Assert.Contains("09:00", NewService(db).GetAvailableSlots(date));
    }

    [Fact]
    public void GetAvailableSlots_IgnoresCancelledBookings()
    {
        using var db = NewDb();
        var date = FutureWorkday();
        for (var i = 0; i < BookingService.MaxPerSlot; i++)
            Seed(db, date, "09:00", email: $"filler{i}@example.com", status: BookingStatus.Cancelled);

        Assert.Contains("09:00", NewService(db).GetAvailableSlots(date));
    }

    [Fact]
    public void CreateBooking_AssignsReferenceAndPendingStatus()
    {
        using var db = NewDb();
        var date = FutureWorkday();

        var (created, error) = NewService(db)
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

        var (created, error) = NewService(db)
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

        var (created, error) = NewService(db)
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

        var (created, error) = NewService(db)
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

        var (created, error) = NewService(db).CreateBooking(
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

        var (created, error) = NewService(db).CreateBooking(
            NewBooking(FutureWorkday(BookingService.MaxActiveBookingsPerEmail + 2), "11:00", "jane@example.com"));

        Assert.Null(error);
        Assert.NotNull(created);
    }

    [Fact]
    public void GetDateAvailabilityForMonth_MarksSundaysAvailable()
    {
        using var db = NewDb();
        var nextMonth = DateTime.Today.AddMonths(1);

        var availability = NewService(db)
            .GetDateAvailabilityForMonth(nextMonth.Year, nextMonth.Month);

        Assert.All(availability.Where(kv => kv.Key.DayOfWeek == DayOfWeek.Sunday),
                   kv => Assert.True(kv.Value));
    }

    [Fact]
    public void GetDateAvailabilityForMonth_MarksSundayUnavailable_WhenItsShorterSlotListIsFull()
    {
        using var db = NewDb();
        var target = NextSunday().AddDays(7);

        var filler = 0;
        foreach (var slot in BookingService.SlotsFor(target))
            for (var i = 0; i < BookingService.MaxPerSlot; i++)
                Seed(db, target, slot, email: $"filler{filler++}@example.com");

        var availability = NewService(db)
            .GetDateAvailabilityForMonth(target.Year, target.Month);

        Assert.False(availability[target]);
    }

    [Fact]
    public void GetDateAvailabilityForMonth_MarksDayUnavailable_WhenEverySlotIsFull()
    {
        using var db = NewDb();
        var nextMonth = DateTime.Today.AddMonths(1);
        var target = new DateTime(nextMonth.Year, nextMonth.Month, 1);
        while (target.DayOfWeek == DayOfWeek.Sunday) target = target.AddDays(1);

        var filler = 0;
        foreach (var slot in BookingService.SlotsFor(target))
            for (var i = 0; i < BookingService.MaxPerSlot; i++)
                Seed(db, target, slot, email: $"filler{filler++}@example.com");

        var availability = NewService(db)
            .GetDateAvailabilityForMonth(target.Year, target.Month);

        Assert.False(availability[target]);
    }

    [Fact]
    public void UpdateStatus_ChangesStoredStatus()
    {
        using var db = NewDb();
        var booking = Seed(db, FutureWorkday(), "09:00", status: BookingStatus.Pending);

        NewService(db).UpdateStatus(booking.Id, BookingStatus.Confirmed);

        Assert.Equal(BookingStatus.Confirmed, db.Bookings.Single().Status);
    }

    [Fact]
    public void UpdateStatus_IsNoOp_ForUnknownId()
    {
        using var db = NewDb();
        Seed(db, FutureWorkday(), "09:00", status: BookingStatus.Pending);

        NewService(db).UpdateStatus("does-not-exist", BookingStatus.Cancelled);

        Assert.Equal(BookingStatus.Pending, db.Bookings.Single().Status);
    }

    [Fact]
    public async Task DeleteBooking_RemovesBooking_AndIsNoOpForUnknownId()
    {
        using var db = NewDb();
        var booking = Seed(db, FutureWorkday(), "09:00");
        var service = NewService(db);

        Assert.False(await service.DeleteBookingAsync("does-not-exist"));
        Assert.Single(db.Bookings);

        Assert.True(await service.DeleteBookingAsync(booking.Id));
        Assert.Empty(db.Bookings);
    }

    [Fact]
    public void AssignStaff_SetsAndClearsAssignment()
    {
        using var db = NewDb();
        var booking = Seed(db, FutureWorkday(), "09:00");
        var service = NewService(db);

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

        var stats = NewService(db).GetStats();

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

        var stats = NewService(db).GetStatsForStaff("staff-1");

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

        var bookings = NewService(db).GetBookingsForStaff("staff-1");

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

        var bookings = NewService(db).GetBookingsByDate(date);

        Assert.Equal(["09:00", "15:00"], bookings.Select(b => b.SlotTime));
    }

    // ── Ported from the dev-branch suite ─────────────────────────────────────
    // dev's GetAvailableSlots_ReturnsEmpty_OnSunday is deliberately NOT carried
    // over: it asserted the shop is shut on Sundays, which this branch superseded
    // with the shorter 11–17 Sunday slot list (see SiteContent.HoursFor).

    [Fact]
    public void AssignStaff_ReturnsFalse_WhenBookingDoesNotExist()
    {
        using var db = NewDb();
        Assert.False(NewService(db).AssignStaff("nonexistent-id", "staff-id"));
    }

    [Fact]
    public void UpdateServicePrice_ReturnsFalse_WhenPricingDoesNotExist()
    {
        using var db = NewDb();
        Assert.False(NewService(db).UpdateServicePrice("nonexistent-id", 42));
    }

    [Fact]
    public void UpdateServicePrice_ReturnsTrue_AndAppliesNewPrice()
    {
        using var db = NewDb();
        var pricing = new ServicePricing { Name = "Basic Service", CurrentPrice = 35 };
        db.ServicePricings.Add(pricing);
        db.SaveChanges();

        Assert.True(NewService(db).UpdateServicePrice(pricing.Id, 40));
        Assert.Equal(40, db.ServicePricings.Find(pricing.Id)!.CurrentPrice);
    }

    [Fact]
    public void GetStats_ReturnsZeros_WhenNoBookingsExist()
    {
        using var db = NewDb();
        Assert.Equal((0, 0, 0, 0), NewService(db).GetStats());
    }

    [Fact]
    public void GetBookingsForCustomer_OnlyReturnsMatchingCustomerId_ExcludesGuestBookings()
    {
        using var db = NewDb();
        var date = FutureWorkday(3);
        var mine = Seed(db, date, "09:00", "a@example.com");
        var someoneElses = Seed(db, date, "10:00", "b@example.com");
        Seed(db, date, "11:00", "c@example.com"); // guest booking — CustomerId left null
        mine.CustomerId = "customer-1";
        someoneElses.CustomerId = "customer-2";
        db.SaveChanges();

        var results = NewService(db).GetBookingsForCustomer("customer-1");

        Assert.Single(results);
        Assert.Equal("a@example.com", results[0].CustomerEmail);
    }
}
