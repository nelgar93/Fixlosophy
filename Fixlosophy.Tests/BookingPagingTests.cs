using Fixlosophy.Data;
using Fixlosophy.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fixlosophy.Tests;

// The admin list used to load every booking with every photo and filter in memory on
// each keystroke. These pin the replacement: filter and search happen in the query,
// paging is stable, and the total reflects the filter rather than the page.
public class BookingPagingTests
{
    private sealed class FakeStorageService : IStorageService
    {
        public string? ValidatePhoto(string c, long s) => null;
        public Task<(string? path, string? error)> UploadCustomerPhotoAsync(string b, string c, byte[] d) =>
            Task.FromResult<(string?, string?)>(("p", null));
        public Task<string?> GetSignedPhotoUrlAsync(string p, TimeSpan e) => Task.FromResult<string?>("u");
        public Task<bool> DeleteAsync(string p) => Task.FromResult(true);
        public string GetPublicWebsiteImageUrl(string f) => f;
    }

    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static BookingService NewService(AppDbContext db) =>
        TestFactory.NewBookingService(db);

    private static void Seed(
        AppDbContext db, int count, BookingStatus status = BookingStatus.Confirmed,
        string namePrefix = "Customer", string? staffId = null, int dayOffset = 3)
    {
        for (var i = 0; i < count; i++)
        {
            db.Bookings.Add(new Booking
            {
                Reference = $"FIX-260830-{i:D3}",
                CustomerName = $"{namePrefix} {i}",
                CustomerEmail = $"c{i}@example.com",
                ServiceName = "Full Service",
                SlotDate = ShopClock.Today.AddDays(dayOffset),
                SlotTime = "10:00",
                Status = status,
                AssignedStaffId = staffId
            });
        }
        db.SaveChanges();
    }

    [Fact]
    public void GetBookingsPage_ReturnsOnePageAndTheFullTotal()
    {
        using var db = NewDb();
        Seed(db, 25);

        var (items, total) = NewService(db).GetBookingsPage(null, "All", null, page: 0, pageSize: 10);

        Assert.Equal(10, items.Count);
        Assert.Equal(25, total);   // the total is of matches, not of the page
    }

    [Fact]
    public void GetBookingsPage_ReturnsTheRequestedPage()
    {
        using var db = NewDb();
        Seed(db, 25);
        var svc = NewService(db);

        var first = svc.GetBookingsPage(null, "All", null, 0, 10).items.Select(b => b.Id).ToList();
        var second = svc.GetBookingsPage(null, "All", null, 1, 10).items.Select(b => b.Id).ToList();
        var last = svc.GetBookingsPage(null, "All", null, 2, 10).items;

        Assert.Empty(first.Intersect(second));   // no row on two pages
        Assert.Equal(5, last.Count);             // final partial page
    }

    [Fact]
    public void GetBookingsPage_FiltersByStatus()
    {
        using var db = NewDb();
        Seed(db, 3, BookingStatus.Pending);
        Seed(db, 7, BookingStatus.Completed, namePrefix: "Done");

        var (items, total) = NewService(db).GetBookingsPage(null, "Pending", null, 0, 50);

        Assert.Equal(3, total);
        Assert.All(items, b => Assert.Equal(BookingStatus.Pending, b.Status));
    }

    [Fact]
    public void GetBookingsPage_SearchesNameEmailServiceAndReference()
    {
        using var db = NewDb();
        Seed(db, 5, namePrefix: "Alice");
        Seed(db, 5, namePrefix: "Bob");

        var svc = NewService(db);

        Assert.Equal(5, svc.GetBookingsPage(null, "All", "alice", 0, 50).total);
        Assert.Equal(5, svc.GetBookingsPage(null, "All", "ALICE", 0, 50).total);   // case-insensitive
        Assert.Equal(10, svc.GetBookingsPage(null, "All", "full service", 0, 50).total);
        Assert.Equal(0, svc.GetBookingsPage(null, "All", "nobody", 0, 50).total);
    }

    [Fact]
    public void GetBookingsPage_RestrictsToAssignedStaff_WhenAStaffIdIsGiven()
    {
        using var db = NewDb();
        Seed(db, 4, staffId: "staff-1");
        Seed(db, 6, staffId: "staff-2", namePrefix: "Other");

        var svc = NewService(db);

        Assert.Equal(4, svc.GetBookingsPage("staff-1", "All", null, 0, 50).total);
        Assert.Equal(10, svc.GetBookingsPage(null, "All", null, 0, 50).total);
    }

    [Fact]
    public void GetBookingsPage_CombinesFilterAndSearch()
    {
        using var db = NewDb();
        Seed(db, 3, BookingStatus.Pending, namePrefix: "Alice");
        Seed(db, 3, BookingStatus.Confirmed, namePrefix: "Alice");
        Seed(db, 3, BookingStatus.Pending, namePrefix: "Bob");

        var (_, total) = NewService(db).GetBookingsPage(null, "Pending", "alice", 0, 50);

        Assert.Equal(3, total);
    }

    [Fact]
    public void GetBookingsPage_HandlesAPageBeyondTheEnd()
    {
        using var db = NewDb();
        Seed(db, 5);

        var (items, total) = NewService(db).GetBookingsPage(null, "All", null, page: 99, pageSize: 10);

        Assert.Empty(items);
        Assert.Equal(5, total);
    }

    [Fact]
    public void GetPhotosForBooking_ReturnsOnlyThatBookingsPhotos()
    {
        using var db = NewDb();
        Seed(db, 2);
        var ids = db.Bookings.Select(b => b.Id).ToList();
        db.BookingPhotos.Add(new BookingPhoto { BookingId = ids[0], StoragePath = "a.jpg" });
        db.BookingPhotos.Add(new BookingPhoto { BookingId = ids[1], StoragePath = "b.jpg" });
        db.SaveChanges();

        var photos = NewService(db).GetPhotosForBooking(ids[0]);

        Assert.Equal("a.jpg", Assert.Single(photos).StoragePath);
    }

    // ── Sort ─────────────────────────────────────────────────────────────────
    // The list used to be fixed at "furthest appointment first", which put history on
    // page one. Default is now "soonest first": what's coming up on top, history below
    // it, most recent first.

    private static void SeedOn(AppDbContext db, int dayOffset, string time, string name)
    {
        db.Bookings.Add(new Booking
        {
            Reference = $"FIX-260830-{dayOffset:D3}",
            CustomerName = name,
            CustomerEmail = $"{name}@example.com",
            ServiceName = "Full Service",
            SlotDate = ShopClock.Today.AddDays(dayOffset),
            SlotTime = time,
            Status = BookingStatus.Confirmed
        });
        db.SaveChanges();
    }

    private static AppDbContext SeedAcrossToday()
    {
        var db = NewDb();
        SeedOn(db, -10, "10:00", "past-older");
        SeedOn(db,  -2, "10:00", "past-recent");
        SeedOn(db,   0, "09:00", "today-early");
        SeedOn(db,   0, "16:00", "today-late");
        SeedOn(db,   5, "10:00", "soon");
        SeedOn(db,  30, "10:00", "far");
        return db;
    }

    private static List<string> Names(AppDbContext db, string sort) =>
        NewService(db).GetBookingsPage(null, "All", null, 0, 50, sort)
                      .items.Select(b => b.CustomerName).ToList();

    [Fact]
    public void GetBookingsPage_DefaultsToUpcomingFirstThenPastMostRecent()
    {
        using var db = SeedAcrossToday();

        Assert.Equal(
            ["today-early", "today-late", "soon", "far", "past-recent", "past-older"],
            Names(db, BookingService.SortUpcoming));
    }

    [Fact]
    public void GetBookingsPage_DefaultSortIsUpcomingWhenNoneIsGiven()
    {
        using var db = SeedAcrossToday();

        var withoutArgument = NewService(db).GetBookingsPage(null, "All", null, 0, 50)
                                            .items.Select(b => b.CustomerName);

        Assert.Equal(Names(db, BookingService.SortUpcoming), withoutArgument);
    }

    [Fact]
    public void GetBookingsPage_SortsDateAscendingAcrossTheWholeList()
    {
        using var db = SeedAcrossToday();

        Assert.Equal(
            ["past-older", "past-recent", "today-early", "today-late", "soon", "far"],
            Names(db, BookingService.SortDateAsc));
    }

    [Fact]
    public void GetBookingsPage_SortsDateDescendingAcrossTheWholeList()
    {
        using var db = SeedAcrossToday();

        Assert.Equal(
            ["far", "soon", "today-early", "today-late", "past-recent", "past-older"],
            Names(db, BookingService.SortDateDesc));
    }

    // Skip/Take needs a total order or a row can show up on two pages.
    [Theory]
    [InlineData(BookingService.SortUpcoming)]
    [InlineData(BookingService.SortDateAsc)]
    [InlineData(BookingService.SortDateDesc)]
    public void GetBookingsPage_PagesWithoutRepeatingOrDroppingRows(string sort)
    {
        using var db = NewDb();
        Seed(db, 25, dayOffset: 0);
        var svc = NewService(db);

        var seen = new List<string>();
        for (var page = 0; page < 3; page++)
            seen.AddRange(svc.GetBookingsPage(null, "All", null, page, 10, sort).items.Select(b => b.Id));

        Assert.Equal(25, seen.Count);
        Assert.Equal(25, seen.Distinct().Count());
    }

    [Fact]
    public void GetBookingsPage_SortAppliesAfterTheSearch()
    {
        using var db = SeedAcrossToday();

        var (items, total) = NewService(db).GetBookingsPage(
            null, "All", "past", 0, 50, BookingService.SortDateAsc);

        Assert.Equal(2, total);
        Assert.Equal(["past-older", "past-recent"], items.Select(b => b.CustomerName));
    }
}
