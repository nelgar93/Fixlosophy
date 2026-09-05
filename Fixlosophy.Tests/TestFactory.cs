using Fixlosophy.Data;
using Fixlosophy.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fixlosophy.Tests;

/// <summary>
/// The shared way to build a throwaway database and the services under test.
/// </summary>
/// <remarks>
/// Three suites had grown their own copy of an in-memory context, a no-op
/// <see cref="IStorageService"/> and a <see cref="BookingService"/> constructor call.
/// Every dependency added to BookingService then broke all three for reasons that had
/// nothing to do with paging, cancellation or booking rules. One place instead.
/// </remarks>
internal static class TestFactory
{
    public static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    public static AvailabilityService NewAvailability(AppDbContext db) => new(db);

    public static BookingService NewBookingService(AppDbContext db) =>
        new(db, new FakeStorageService(), NewAvailability(db), NullLogger<BookingService>.Instance);

    /// <summary>
    /// Adds an active mechanic, which is what switches the "somebody has to be in"
    /// rule on.
    /// </summary>
    /// <remarks>
    /// Tests that don't call this get the rule switched off — see
    /// <see cref="AvailabilityService.MechanicRuleApplies"/> — so the suites written
    /// before absences existed keep meaning what they meant. Anything testing absence
    /// behaviour has to add a mechanic first, which is also true of the real shop.
    /// </remarks>
    public static StaffMember AddMechanic(AppDbContext db, string name = "Francesco")
    {
        var staff = new StaffMember
        {
            FullName = name,
            Email = $"{name.ToLowerInvariant()}@example.com",
            Role = StaffRole.Worker,
            IsActive = true,
            IsMechanic = true
        };
        db.Staff.Add(staff);
        db.SaveChanges();
        return staff;
    }

    /// A future date that is never a Sunday — Sundays run a shorter slot list, which
    /// would otherwise make slot-count assertions depend on what day the suite runs.
    public static DateTime FutureWorkday(int daysAhead = 7)
    {
        var date = ShopClock.Today.AddDays(daysAhead);
        while (date.DayOfWeek == DayOfWeek.Sunday) date = date.AddDays(1);
        return date;
    }

    /// Exercises booking logic, not Supabase Storage: a real StorageService needs an
    /// HttpClient and config, neither of which an in-memory test has.
    internal sealed class FakeStorageService : IStorageService
    {
        public string? ValidatePhoto(string contentType, long size) => null;
        public Task<(string? path, string? error)> UploadCustomerPhotoAsync(string bookingId, string contentType, byte[] content) =>
            Task.FromResult<(string?, string?)>(("fake/path.jpg", null));
        public Task<string?> GetSignedPhotoUrlAsync(string storagePath, TimeSpan expiry) =>
            Task.FromResult<string?>("https://example.com/signed");
        public Task<bool> DeleteAsync(string storagePath) => Task.FromResult(true);
        public string GetPublicWebsiteImageUrl(string fileName) => $"https://example.com/{fileName}";
    }
}
