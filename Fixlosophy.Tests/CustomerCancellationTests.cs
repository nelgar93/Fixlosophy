using Fixlosophy.Data;
using Fixlosophy.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fixlosophy.Tests;

// Customers can now cancel their own upcoming bookings instead of phoning the shop.
// The properties that matter: it frees the slot, it can't reach anyone else's
// booking, and it stops being self-service close to the appointment.
public class CustomerCancellationTests
{
    private sealed class FakeStorageService : IStorageService
    {
        public string? ValidatePhoto(string contentType, long size) => null;
        public Task<(string? path, string? error)> UploadCustomerPhotoAsync(string b, string c, byte[] d) =>
            Task.FromResult<(string?, string?)>(("fake/path.jpg", null));
        public Task<string?> GetSignedPhotoUrlAsync(string p, TimeSpan e) => Task.FromResult<string?>("https://example.com/s");
        public Task<bool> DeleteAsync(string p) => Task.FromResult(true);
        public string GetPublicWebsiteImageUrl(string f) => $"https://example.com/{f}";
    }

    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static BookingService NewService(AppDbContext db) =>
        TestFactory.NewBookingService(db);

    // Far enough ahead to clear the cutoff, and never a Sunday (shorter slot list).
    private static DateTime FutureWeekday(int daysAhead = 7)
    {
        var d = ShopClock.Today.AddDays(daysAhead);
        while (d.DayOfWeek is DayOfWeek.Sunday) d = d.AddDays(1);
        return d;
    }

    private static Booking Seed(
        AppDbContext db, string? customerId, DateTime date, string slot = "14:00",
        BookingStatus status = BookingStatus.Confirmed)
    {
        var b = new Booking
        {
            Reference = "FIX-260830-001",
            CustomerId = customerId,
            CustomerName = "Jane Doe",
            CustomerEmail = "jane@example.com",
            ServiceName = "Full Service",
            SlotDate = date,
            SlotTime = slot,
            Status = status
        };
        db.Bookings.Add(b);
        db.SaveChanges();
        return b;
    }

    [Fact]
    public void CancelOwnBooking_SetsStatusToCancelled()
    {
        using var db = NewDb();
        var b = Seed(db, "cust-1", FutureWeekday());

        var (booking, error) = NewService(db).CancelOwnBooking("cust-1", b.Id);

        Assert.Null(error);
        Assert.NotNull(booking);
        Assert.Equal(BookingStatus.Cancelled, db.Bookings.Single().Status);
    }

    // Cancelling must actually free the slot — every availability query filters on
    // Status != Cancelled, so this is what makes the seat re-bookable.
    [Fact]
    public void CancelOwnBooking_FreesTheSlot()
    {
        using var db = NewDb();
        var date = FutureWeekday();
        var svc = NewService(db);

        // Fill the slot to capacity so it disappears from availability.
        var mine = Seed(db, "cust-1", date, "14:00");
        for (var i = 1; i < BookingService.MaxPerSlot; i++)
            Seed(db, null, date, "14:00");

        Assert.DoesNotContain("14:00", svc.GetAvailableSlots(date));

        svc.CancelOwnBooking("cust-1", mine.Id);

        Assert.Contains("14:00", svc.GetAvailableSlots(date));
    }

    [Fact]
    public void CancelOwnBooking_RefusesSomeoneElsesBooking()
    {
        using var db = NewDb();
        var b = Seed(db, "cust-1", FutureWeekday());

        var (booking, error) = NewService(db).CancelOwnBooking("cust-2", b.Id);

        Assert.Null(booking);
        Assert.NotNull(error);
        // Still confirmed — the other customer's attempt changed nothing.
        Assert.Equal(BookingStatus.Confirmed, db.Bookings.Single().Status);
    }

    [Fact]
    public void CancelOwnBooking_RefusesAGuestBookingWithNoOwner()
    {
        using var db = NewDb();
        var b = Seed(db, null, FutureWeekday());

        var (_, error) = NewService(db).CancelOwnBooking("cust-1", b.Id);

        Assert.NotNull(error);
        Assert.Equal(BookingStatus.Confirmed, db.Bookings.Single().Status);
    }

    [Fact]
    public void CancelOwnBooking_RefusesInsideTheCutoff()
    {
        using var db = NewDb();
        // An hour from now — inside the 2-hour self-service cutoff.
        var soon = ShopClock.Now.AddHours(1);
        var b = Seed(db, "cust-1", soon.Date, soon.ToString("HH:mm", System.Globalization.CultureInfo.InvariantCulture));

        var (booking, error) = NewService(db).CancelOwnBooking("cust-1", b.Id);

        Assert.Null(booking);
        Assert.Contains("call us", error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(BookingStatus.Confirmed, db.Bookings.Single().Status);
    }

    [Fact]
    public void CancelOwnBooking_RefusesAnAlreadyCancelledBooking()
    {
        using var db = NewDb();
        var b = Seed(db, "cust-1", FutureWeekday(), status: BookingStatus.Cancelled);

        var (_, error) = NewService(db).CancelOwnBooking("cust-1", b.Id);

        Assert.Contains("already cancelled", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CancelOwnBooking_RefusesACompletedBooking()
    {
        using var db = NewDb();
        var b = Seed(db, "cust-1", FutureWeekday(), status: BookingStatus.Completed);

        var (_, error) = NewService(db).CancelOwnBooking("cust-1", b.Id);

        Assert.Contains("already completed", error, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void CancelOwnBooking_ReturnsAnError_ForAnUnknownBooking()
    {
        using var db = NewDb();
        var (booking, error) = NewService(db).CancelOwnBooking("cust-1", "no-such-booking");

        Assert.Null(booking);
        Assert.NotNull(error);
    }

    // The gap this suite used to have. InProgress is upcoming, so it appeared in the
    // customer's list with a Cancel button beside it, and nothing refused the call —
    // a customer could call off a job with the bike already apart on the stand.
    [Fact]
    public void CancelOwnBooking_RefusesAJobAlreadyInProgress()
    {
        using var db = NewDb();
        var b = Seed(db, "cust-1", FutureWeekday(), status: BookingStatus.InProgress);

        var (booking, error) = NewService(db).CancelOwnBooking("cust-1", b.Id);

        Assert.Null(booking);
        Assert.Contains("already started work", error, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(BookingStatus.InProgress, db.Bookings.Single().Status);
    }

    // Staff keep the ability the customer loses: the dashboard cancels mid-repair,
    // because the people holding the bike are the ones who can explain why.
    [Fact]
    public void StaffCanStillCancelAJobInProgress()
    {
        Assert.False(BookingService.CanCustomerCancel(BookingStatus.InProgress));
        Assert.True(BookingService.CanTransition(BookingStatus.InProgress, BookingStatus.Cancelled));
    }

    // The page renders from the same predicate the service enforces, so a button can
    // never offer something the call behind it would refuse.
    [Theory]
    [InlineData(BookingStatus.Pending,   true)]
    [InlineData(BookingStatus.Confirmed, true)]
    [InlineData(BookingStatus.InProgress, false)]
    [InlineData(BookingStatus.Completed, false)]
    [InlineData(BookingStatus.Cancelled, false)]
    public void SelfCancelBlockedReason_AgreesWithCanCustomerCancel(BookingStatus status, bool allowed)
    {
        using var db = NewDb();
        var b = Seed(db, "cust-1", FutureWeekday(), status: status);

        Assert.Equal(allowed, BookingService.SelfCancelBlockedReason(b) is null);
    }
}
