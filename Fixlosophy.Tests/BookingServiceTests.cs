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

    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static BookingService NewService(AppDbContext db) =>
        new(db, new FakeStorageService(), NullLogger<BookingService>.Instance);

    // A date at least `daysAhead` from today that isn't a Sunday (the shop is closed then) —
    // computed relative to "today" so these tests don't go stale/flaky over time.
    private static DateTime FutureWeekday(int daysAhead)
    {
        var date = DateTime.Today.AddDays(daysAhead);
        while (date.DayOfWeek == DayOfWeek.Sunday) date = date.AddDays(1);
        return date;
    }

    private static Booking NewBooking(
        DateTime date, string time, string email = "customer@example.com", BookingStatus status = BookingStatus.Pending) => new()
    {
        CustomerName = "Test Customer",
        CustomerEmail = email,
        ServiceCategory = "Components",
        ServiceName = "Gear Service",
        ServicePrice = 10,
        SlotDate = date,
        SlotTime = time,
        Status = status
    };

    [Fact]
    public void GetAvailableSlots_ReturnsEmpty_OnSunday()
    {
        using var db = NewDb();
        var svc = NewService(db);

        var sunday = DateTime.Today.AddDays(1);
        while (sunday.DayOfWeek != DayOfWeek.Sunday) sunday = sunday.AddDays(1);

        Assert.Empty(svc.GetAvailableSlots(sunday));
    }

    [Fact]
    public void GetAvailableSlots_ExcludesFullyBookedSlot()
    {
        using var db = NewDb();
        var date = FutureWeekday(3);
        db.Bookings.Add(NewBooking(date, "09:00", "a@example.com"));
        db.Bookings.Add(NewBooking(date, "09:00", "b@example.com"));
        db.SaveChanges();

        var svc = NewService(db);
        var slots = svc.GetAvailableSlots(date);

        Assert.DoesNotContain("09:00", slots);
        Assert.Contains("10:00", slots);
    }

    [Fact]
    public void GetAvailableSlots_IgnoresCancelledBookings()
    {
        using var db = NewDb();
        var date = FutureWeekday(3);
        db.Bookings.Add(NewBooking(date, "09:00", "a@example.com", BookingStatus.Cancelled));
        db.Bookings.Add(NewBooking(date, "09:00", "b@example.com", BookingStatus.Cancelled));
        db.SaveChanges();

        var svc = NewService(db);
        Assert.Contains("09:00", svc.GetAvailableSlots(date));
    }

    // CreateBooking's success path calls a Postgres sequence (BookingReferenceSeq) via raw
    // ADO.NET, which the InMemory provider can't back — so only the three rejection paths
    // (which all return before that call) are unit-tested here. The success path needs a
    // real Postgres connection; see the manual verification notes in the audit history.

    [Fact]
    public void CreateBooking_RejectsWhenSlotIsFull()
    {
        using var db = NewDb();
        var date = FutureWeekday(3);
        db.Bookings.Add(NewBooking(date, "09:00", "a@example.com"));
        db.Bookings.Add(NewBooking(date, "09:00", "b@example.com"));
        db.SaveChanges();

        var svc = NewService(db);
        var (created, error) = svc.CreateBooking(NewBooking(date, "09:00", "c@example.com"));

        Assert.Null(created);
        Assert.Contains("filled up", error);
    }

    [Fact]
    public void CreateBooking_RejectsDuplicateForSameCustomerAndSlot_CaseInsensitiveEmail()
    {
        using var db = NewDb();
        var date = FutureWeekday(3);
        db.Bookings.Add(NewBooking(date, "09:00", "same@example.com"));
        db.SaveChanges();

        var svc = NewService(db);
        var (created, error) = svc.CreateBooking(NewBooking(date, "09:00", "SAME@Example.com"));

        Assert.Null(created);
        Assert.Contains("already have a booking", error);
    }

    [Fact]
    public void CreateBooking_RejectsWhenCustomerHasTooManyUpcomingBookings()
    {
        using var db = NewDb();
        const string email = "busy@example.com";
        for (var i = 0; i < BookingService.MaxActiveBookingsPerEmail; i++)
            db.Bookings.Add(NewBooking(FutureWeekday(3 + i), $"{9 + i}:00", email));
        db.SaveChanges();

        var svc = NewService(db);
        var (created, error) = svc.CreateBooking(NewBooking(FutureWeekday(20), "09:00", email));

        Assert.Null(created);
        Assert.Contains($"{BookingService.MaxActiveBookingsPerEmail} upcoming bookings", error);
    }

    [Fact]
    public void UpdateStatus_ReturnsTrue_WhenBookingExists()
    {
        using var db = NewDb();
        var booking = NewBooking(FutureWeekday(3), "09:00");
        db.Bookings.Add(booking);
        db.SaveChanges();

        var svc = NewService(db);
        Assert.True(svc.UpdateStatus(booking.Id, BookingStatus.Confirmed));
        Assert.Equal(BookingStatus.Confirmed, db.Bookings.Find(booking.Id)!.Status);
    }

    [Fact]
    public void UpdateStatus_ReturnsFalse_WhenBookingDoesNotExist()
    {
        using var db = NewDb();
        var svc = NewService(db);
        Assert.False(svc.UpdateStatus("nonexistent-id", BookingStatus.Confirmed));
    }

    [Fact]
    public async Task DeleteBooking_ReturnsTrueThenFalse_ForSameId()
    {
        using var db = NewDb();
        var booking = NewBooking(FutureWeekday(3), "09:00");
        db.Bookings.Add(booking);
        db.SaveChanges();

        var svc = NewService(db);
        Assert.True(await svc.DeleteBookingAsync(booking.Id));
        Assert.False(await svc.DeleteBookingAsync(booking.Id));
    }

    [Fact]
    public void AssignStaff_ReturnsFalse_WhenBookingDoesNotExist()
    {
        using var db = NewDb();
        var svc = NewService(db);
        Assert.False(svc.AssignStaff("nonexistent-id", "staff-id"));
    }

    [Fact]
    public void AssignStaff_ClearsAssignment_WhenStaffIdIsEmpty()
    {
        using var db = NewDb();
        var booking = NewBooking(FutureWeekday(3), "09:00");
        booking.AssignedStaffId = "some-staff-id";
        db.Bookings.Add(booking);
        db.SaveChanges();

        var svc = NewService(db);
        Assert.True(svc.AssignStaff(booking.Id, ""));
        Assert.Null(db.Bookings.Find(booking.Id)!.AssignedStaffId);
    }

    [Fact]
    public void UpdateServicePrice_ReturnsFalse_WhenPricingDoesNotExist()
    {
        using var db = NewDb();
        var svc = NewService(db);
        Assert.False(svc.UpdateServicePrice("nonexistent-id", 42));
    }

    [Fact]
    public void UpdateServicePrice_ReturnsTrue_AndAppliesNewPrice()
    {
        using var db = NewDb();
        var pricing = new ServicePricing { Name = "Basic Service", CurrentPrice = 35 };
        db.ServicePricings.Add(pricing);
        db.SaveChanges();

        var svc = NewService(db);
        Assert.True(svc.UpdateServicePrice(pricing.Id, 40));
        Assert.Equal(40, db.ServicePricings.Find(pricing.Id)!.CurrentPrice);
    }

    [Fact]
    public void GetStats_CountsMatchRawData()
    {
        using var db = NewDb();
        var today = DateTime.Today;
        db.Bookings.Add(NewBooking(today, "09:00", "a@example.com", BookingStatus.Pending));
        db.Bookings.Add(NewBooking(today, "10:00", "b@example.com", BookingStatus.Confirmed));
        db.Bookings.Add(NewBooking(FutureWeekday(5), "09:00", "c@example.com", BookingStatus.Pending));
        db.SaveChanges();

        var svc = NewService(db);
        var stats = svc.GetStats();

        Assert.Equal(3, stats.total);
        Assert.Equal(2, stats.today);
        Assert.Equal(2, stats.pending);
        Assert.Equal(1, stats.confirmed);
    }

    [Fact]
    public void GetStats_ReturnsZeros_WhenNoBookingsExist()
    {
        using var db = NewDb();
        var svc = NewService(db);
        Assert.Equal((0, 0, 0, 0), svc.GetStats());
    }

    [Fact]
    public void GetStatsForStaff_OnlyCountsAssignedBookings()
    {
        using var db = NewDb();
        var date = FutureWeekday(3);
        var assigned = NewBooking(date, "09:00", "a@example.com", BookingStatus.Pending);
        assigned.AssignedStaffId = "staff-1";
        var otherStaff = NewBooking(date, "10:00", "b@example.com", BookingStatus.Confirmed);
        otherStaff.AssignedStaffId = "staff-2";
        db.Bookings.AddRange(assigned, otherStaff);
        db.SaveChanges();

        var svc = NewService(db);
        var stats = svc.GetStatsForStaff("staff-1");

        Assert.Equal(1, stats.total);
        Assert.Equal(1, stats.pending);
        Assert.Equal(0, stats.confirmed);
    }

    [Fact]
    public void GetBookingsForCustomer_OnlyReturnsMatchingCustomerId_ExcludesGuestBookings()
    {
        using var db = NewDb();
        var date = FutureWeekday(3);
        var mine = NewBooking(date, "09:00", "a@example.com");
        mine.CustomerId = "customer-1";
        var someoneElses = NewBooking(date, "10:00", "b@example.com");
        someoneElses.CustomerId = "customer-2";
        var guest = NewBooking(date, "11:00", "c@example.com"); // CustomerId left null
        db.Bookings.AddRange(mine, someoneElses, guest);
        db.SaveChanges();

        var svc = NewService(db);
        var results = svc.GetBookingsForCustomer("customer-1");

        Assert.Single(results);
        Assert.Equal("a@example.com", results[0].CustomerEmail);
    }
}
