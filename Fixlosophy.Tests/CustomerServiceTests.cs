using Fixlosophy.Data;
using Fixlosophy.Services;
using Microsoft.EntityFrameworkCore;

namespace Fixlosophy.Tests;

public class CustomerServiceTests
{
    private sealed class NoopTokenStore : IVerificationTokenStore
    {
        public Dictionary<string, string> Tokens { get; } = [];
        public void SetToken(string key, string hash, TimeSpan ttl) => Tokens[key] = hash;
        public bool TrySetTokenIfAbsent(string key, string value, TimeSpan ttl) => Tokens.TryAdd(key, value);
        public string? GetTokenHash(string key) => Tokens.GetValueOrDefault(key);
        public void RemoveToken(string key) => Tokens.Remove(key);
    }

    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private const string Password = "correct-horse-battery";

    private static Customer SeedCustomer(AppDbContext db, string email = "jane@example.com", string name = "Jane Doe")
    {
        var c = new Customer
        {
            Email = AuthService.NormalizeEmail(email),
            FullName = name,
            Phone = "07700900000",
            PasswordHash = AuthService.HashPassword(Password),
            EmailConfirmed = true
        };
        db.Customers.Add(c);
        db.SaveChanges();
        return c;
    }

    private static Booking SeedBooking(
        AppDbContext db, string? customerId, string email, decimal price,
        BookingStatus status, int dayOffset = -7, string reference = "FIX-260830-001")
    {
        var b = new Booking
        {
            Reference = reference,
            CustomerId = customerId,
            CustomerName = "Jane Doe",
            CustomerEmail = email,
            ServiceName = "Full Service",
            ServicePrice = price,
            SlotDate = ShopClock.Today.AddDays(dayOffset),
            SlotTime = "10:00",
            Status = status
        };
        db.Bookings.Add(b);
        db.SaveChanges();
        return b;
    }

    // ── List ─────────────────────────────────────────────────────────────────

    [Fact]
    public void GetCustomersPage_SearchesNameEmailAndPhone()
    {
        using var db = NewDb();
        SeedCustomer(db, "alice@example.com", "Alice Smith");
        SeedCustomer(db, "bob@example.com", "Bob Jones");
        var svc = new CustomerService(db);

        Assert.Equal(1, svc.GetCustomersPage("alice", 0).total);
        Assert.Equal(1, svc.GetCustomersPage("ALICE", 0).total);   // case-insensitive
        Assert.Equal(1, svc.GetCustomersPage("bob@example", 0).total);
        Assert.Equal(2, svc.GetCustomersPage("07700", 0).total);   // both share a phone
        Assert.Equal(0, svc.GetCustomersPage("nobody", 0).total);
    }

    [Fact]
    public void GetCustomersPage_CountsBookingsAndLastVisit()
    {
        using var db = NewDb();
        var c = SeedCustomer(db);
        SeedBooking(db, c.Id, c.Email, 35m, BookingStatus.Completed, dayOffset: -30, reference: "A");
        SeedBooking(db, c.Id, c.Email, 70m, BookingStatus.Completed, dayOffset: -5,  reference: "B");

        var row = new CustomerService(db).GetCustomersPage(null, 0).items.Single();

        Assert.Equal(2, row.BookingCount);
        Assert.Equal(ShopClock.Today.AddDays(-5), row.LastVisit);
    }

    [Fact]
    public void GetCustomersPage_PagesWithoutRepeatingRows()
    {
        using var db = NewDb();
        for (var i = 0; i < 30; i++) SeedCustomer(db, $"c{i:D2}@example.com", $"Customer {i:D2}");
        var svc = new CustomerService(db);

        var first = svc.GetCustomersPage(null, 0, 10).items.Select(c => c.Id).ToList();
        var second = svc.GetCustomersPage(null, 1, 10).items.Select(c => c.Id).ToList();

        Assert.Equal(10, first.Count);
        Assert.Empty(first.Intersect(second));
    }

    // ── Detail ───────────────────────────────────────────────────────────────

    // Lifetime spend is the number a shop would quote back at someone, so it must
    // count work actually done — not pending estimates or cancelled jobs.
    [Fact]
    public void GetCustomerDetail_CountsOnlyCompletedBookingsTowardSpend()
    {
        using var db = NewDb();
        var c = SeedCustomer(db);
        SeedBooking(db, c.Id, c.Email, 35m,  BookingStatus.Completed, reference: "A");
        SeedBooking(db, c.Id, c.Email, 70m,  BookingStatus.Completed, reference: "B");
        SeedBooking(db, c.Id, c.Email, 120m, BookingStatus.Pending,   reference: "C");
        SeedBooking(db, c.Id, c.Email, 999m, BookingStatus.Cancelled, reference: "D");

        var detail = new CustomerService(db).GetCustomerDetail(c.Id)!;

        Assert.Equal(4, detail.Bookings.Count);
        Assert.Equal(2, detail.CompletedCount);
        Assert.Equal(105m, detail.LifetimeSpend);   // 35 + 70 only
    }

    // The point of the view: a guest booking made before registering is invisible in
    // the admin today, so a returning customer looks like a stranger.
    [Fact]
    public void GetCustomerDetail_SurfacesUnlinkedGuestBookingsUnderTheSameEmail()
    {
        using var db = NewDb();
        var c = SeedCustomer(db);
        SeedBooking(db, c.Id, c.Email, 35m, BookingStatus.Completed, reference: "linked");
        SeedBooking(db, null, "JANE@Example.com", 70m, BookingStatus.Completed, reference: "guest");

        var detail = new CustomerService(db).GetCustomerDetail(c.Id)!;

        Assert.Equal("linked", detail.Bookings.Single().Reference);
        Assert.Equal("guest", detail.UnlinkedGuestBookings.Single().Reference);
    }

    [Fact]
    public void GetCustomerDetail_DoesNotClaimAnotherPersonsGuestBooking()
    {
        using var db = NewDb();
        var c = SeedCustomer(db);
        SeedBooking(db, null, "someone.else@example.com", 70m, BookingStatus.Completed, reference: "theirs");

        var detail = new CustomerService(db).GetCustomerDetail(c.Id)!;

        Assert.Empty(detail.UnlinkedGuestBookings);
    }

    // The panel shows each job's notes on the job. A note written when completing a
    // booking has to arrive keyed by that booking, not in the general pile.
    [Fact]
    public void GetCustomerDetail_FilesNotesUnderTheBookingTheyWereWrittenOn()
    {
        using var db = NewDb();
        var c = SeedCustomer(db);
        var b = SeedBooking(db, c.Id, c.Email, 70m, BookingStatus.Completed);
        var svc = new CustomerService(db);

        svc.AddNote(c.Id, b.Id, "staff-1", "Rebuilt the rear wheel.", visibleToCustomer: true);
        svc.AddNote(c.Id, null, "staff-1", "Prefers a call, not a text.", visibleToCustomer: false);

        var detail = svc.GetCustomerDetail(c.Id)!;

        Assert.Equal("Rebuilt the rear wheel.", Assert.Single(detail.BookingNotes[b.Id]).Body);
        Assert.Equal("Prefers a call, not a text.", Assert.Single(detail.GeneralNotes).Body);
    }

    // A note on a guest booking has no CustomerId — it is reachable only through the
    // booking, and was stored and then invisible in this panel.
    [Fact]
    public void GetCustomerDetail_FindsNotesOnUnlinkedGuestBookings()
    {
        using var db = NewDb();
        var c = SeedCustomer(db);
        var guest = SeedBooking(db, null, "JANE@Example.com", 70m, BookingStatus.Completed, reference: "guest");
        var svc = new CustomerService(db);

        svc.AddNote(customerId: null, bookingId: guest.Id, authorStaffId: "staff-1",
                    body: "Bottom bracket was seized.", visibleToCustomer: false);

        var detail = svc.GetCustomerDetail(c.Id)!;

        Assert.Equal("Bottom bracket was seized.", Assert.Single(detail.BookingNotes[guest.Id]).Body);
    }

    [Fact]
    public void GetCustomerDetail_ReturnsNull_ForAnUnknownId() =>
        Assert.Null(new CustomerService(NewDb()).GetCustomerDetail("no-such-customer"));

    // ── Notes ────────────────────────────────────────────────────────────────

    [Fact]
    public void AddNote_StoresTheNote()
    {
        using var db = NewDb();
        var c = SeedCustomer(db);
        var svc = new CustomerService(db);

        svc.AddNote(c.Id, null, "staff-1", "  Rear hub has play.  ", visibleToCustomer: false);

        var note = db.CustomerNotes.Single();
        Assert.Equal("Rear hub has play.", note.Body);   // trimmed
        Assert.False(note.VisibleToCustomer);
        Assert.Equal("staff-1", note.AuthorStaffId);
    }

    // An empty note is a no-op, which is what makes it optional on the completion flow.
    [Fact]
    public void AddNote_IgnoresAnEmptyBody()
    {
        using var db = NewDb();
        var c = SeedCustomer(db);
        var svc = new CustomerService(db);

        Assert.Null(svc.AddNote(c.Id, null, "staff-1", "   ", false));
        Assert.Empty(db.CustomerNotes);
    }

    [Fact]
    public void AddNote_ClampsAnOverlongBody()
    {
        using var db = NewDb();
        var c = SeedCustomer(db);

        new CustomerService(db).AddNote(c.Id, null, "staff-1",
            new string('x', CustomerService.MaxNoteLength + 500), false);

        Assert.Equal(CustomerService.MaxNoteLength, db.CustomerNotes.Single().Body.Length);
    }

    // The visibility flag is the whole point: staff write frankly because internal
    // notes never reach the customer.
    [Fact]
    public void GetCustomerVisibleNotesByBooking_ExcludesInternalNotes()
    {
        using var db = NewDb();
        var c = SeedCustomer(db);
        var b = SeedBooking(db, c.Id, c.Email, 70m, BookingStatus.Completed);
        var svc = new CustomerService(db);

        svc.AddNote(c.Id, b.Id, "staff-1", "Replaced the chain and cables.", visibleToCustomer: true);
        svc.AddNote(c.Id, b.Id, "staff-1", "Customer haggles — quote firm next time.", visibleToCustomer: false);

        var shared = svc.GetCustomerVisibleNotesByBooking(c.Id);

        Assert.Equal("Replaced the chain and cables.", Assert.Single(shared[b.Id]).Body);
    }

    [Fact]
    public void DeleteNote_RemovesIt()
    {
        using var db = NewDb();
        var c = SeedCustomer(db);
        var svc = new CustomerService(db);
        var note = svc.AddNote(c.Id, null, "staff-1", "Temporary", false)!;

        Assert.True(svc.DeleteNote(note.Id));
        Assert.Empty(db.CustomerNotes);
        Assert.False(svc.DeleteNote("no-such-note"));
    }

    // A note written while they were still a guest has no CustomerId. It should
    // follow them in when they register and prove they own the email.
    [Fact]
    public void LinkGuestBookings_AdoptsNotesLeftOnThoseBookings()
    {
        using var db = NewDb();
        var customer = SeedCustomer(db);
        var guestBooking = SeedBooking(db, null, customer.Email, 70m, BookingStatus.Completed);

        var notes = new CustomerService(db);
        notes.AddNote(customerId: null, bookingId: guestBooking.Id, authorStaffId: "staff-1",
                      body: "Bottom bracket was seized.", visibleToCustomer: true);

        new AuthService(db, new NoopTokenStore()).LinkGuestBookings(customer);

        var note = db.CustomerNotes.Single();
        Assert.Equal(customer.Id, note.CustomerId);
        Assert.Equal(guestBooking.Id, note.BookingId);
    }

    // ── Export ───────────────────────────────────────────────────────────────

    [Fact]
    public void ExportCustomerData_IncludesSharedNotesAndExcludesInternalOnes()
    {
        using var db = NewDb();
        var c = SeedCustomer(db);
        var b = SeedBooking(db, c.Id, c.Email, 70m, BookingStatus.Completed);

        var notes = new CustomerService(db);
        notes.AddNote(c.Id, b.Id, "staff-1", "New cables fitted.", visibleToCustomer: true);
        notes.AddNote(c.Id, b.Id, "staff-1", "Chased for payment twice.", visibleToCustomer: false);

        var export = new AuthService(db, new NoopTokenStore()).ExportCustomerData(c.Id)!;
        var json = System.Text.Json.JsonSerializer.Serialize(export);

        Assert.Equal("New cables fitted.", Assert.Single(export.Bookings.Single().ServiceNotes));
        Assert.DoesNotContain("Chased for payment", json, StringComparison.Ordinal);
    }
}
