using Fixlosophy.Data;
using Fixlosophy.Services;
using Microsoft.EntityFrameworkCore;

namespace Fixlosophy.Tests;

// Booking as a guest then registering with the same address used to leave the account
// page empty — CustomerId is only stamped when signed in. Adoption fixes that, but the
// interesting tests here are the negative ones: an email address is not a secret, so
// it must never on its own be enough to claim someone's booking history.
public class GuestBookingLinkingTests
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

    private static Customer SeedCustomer(AppDbContext db, string email, bool confirmed = true)
    {
        var c = new Customer
        {
            Email = AuthService.NormalizeEmail(email),
            FullName = "Jane Doe",
            Phone = "07700900000",
            PasswordHash = AuthService.HashPassword(Password),
            EmailConfirmed = confirmed
        };
        db.Customers.Add(c);
        db.SaveChanges();
        return c;
    }

    private static Booking SeedGuestBooking(AppDbContext db, string email, string reference = "FIX-260830-001")
    {
        var b = new Booking
        {
            Reference = reference,
            CustomerName = "Jane Doe",
            CustomerEmail = email,
            CustomerPhone = "07700900000",
            ServiceName = "Full Service",
            SlotDate = DateTime.Today.AddDays(3),
            SlotTime = "10:00",
            CustomerId = null   // guest
        };
        db.Bookings.Add(b);
        db.SaveChanges();
        return b;
    }

    [Fact]
    public void LinkGuestBookings_AdoptsBookingsMadeUnderTheSameEmail()
    {
        using var db = NewDb();
        var customer = SeedCustomer(db, "jane@example.com");
        SeedGuestBooking(db, "jane@example.com");

        var linked = new AuthService(db, new NoopTokenStore()).LinkGuestBookings(customer);

        Assert.Equal(1, linked);
        Assert.Equal(customer.Id, db.Bookings.Single().CustomerId);
    }

    [Fact]
    public void LinkGuestBookings_IgnoresCasingDifferences()
    {
        using var db = NewDb();
        var customer = SeedCustomer(db, "jane@example.com");
        SeedGuestBooking(db, "JANE@Example.COM");

        Assert.Equal(1, new AuthService(db, new NoopTokenStore()).LinkGuestBookings(customer));
    }

    // The important one: a booking already belonging to someone else must be
    // untouchable, whatever the email on it says.
    [Fact]
    public void LinkGuestBookings_NeverReassignsABookingThatAlreadyHasAnOwner()
    {
        using var db = NewDb();
        var owner = SeedCustomer(db, "jane@example.com");
        var other = SeedCustomer(db, "someone.else@example.com");

        var booking = SeedGuestBooking(db, "jane@example.com");
        booking.CustomerId = owner.Id;
        db.SaveChanges();

        var linked = new AuthService(db, new NoopTokenStore()).LinkGuestBookings(other);

        Assert.Equal(0, linked);
        Assert.Equal(owner.Id, db.Bookings.Single().CustomerId);
    }

    [Fact]
    public void LinkGuestBookings_LeavesOtherPeoplesGuestBookingsAlone()
    {
        using var db = NewDb();
        var customer = SeedCustomer(db, "jane@example.com");
        SeedGuestBooking(db, "someone.else@example.com", "FIX-260830-002");

        Assert.Equal(0, new AuthService(db, new NoopTokenStore()).LinkGuestBookings(customer));
        Assert.Null(db.Bookings.Single().CustomerId);
    }

    // Signing in proves knowledge of the password, so adoption is safe there.
    [Fact]
    public void AuthenticateCustomer_AdoptsGuestBookings_OnSuccessfulSignIn()
    {
        using var db = NewDb();
        SeedCustomer(db, "jane@example.com");
        SeedGuestBooking(db, "jane@example.com");

        var auth = new AuthService(db, new NoopTokenStore());
        var signedIn = auth.AuthenticateCustomer("jane@example.com", Password);

        Assert.NotNull(signedIn);
        Assert.Equal(signedIn.Id, db.Bookings.Single().CustomerId);
    }

    // ...and a failed sign-in must adopt nothing. Someone typing a victim's address
    // with the wrong password must not cause their bookings to be claimed.
    [Fact]
    public void AuthenticateCustomer_AdoptsNothing_OnWrongPassword()
    {
        using var db = NewDb();
        SeedCustomer(db, "jane@example.com");
        SeedGuestBooking(db, "jane@example.com");

        var auth = new AuthService(db, new NoopTokenStore());
        var signedIn = auth.AuthenticateCustomer("jane@example.com", "not-the-password");

        Assert.Null(signedIn);
        Assert.Null(db.Bookings.Single().CustomerId);
    }

    // Registering alone proves nothing — the link must wait for the emailed token.
    [Fact]
    public void RegisterCustomer_DoesNotAdoptGuestBookings()
    {
        using var db = NewDb();
        SeedGuestBooking(db, "jane@example.com");

        var auth = new AuthService(db, new NoopTokenStore());
        var (customer, error) = auth.RegisterCustomer(
            "jane@example.com", "Jane Doe", "07700900000", Password);

        Assert.Null(error);
        Assert.NotNull(customer);
        Assert.Null(db.Bookings.Single().CustomerId);
    }

    [Fact]
    public void ConfirmEmail_AdoptsGuestBookings_WhenTheLinkIsRedeemed()
    {
        using var db = NewDb();
        var store = new NoopTokenStore();
        var auth = new AuthService(db, store);

        var (customer, _) = auth.RegisterCustomer("jane@example.com", "Jane Doe", "07700900000", Password);
        SeedGuestBooking(db, "jane@example.com");

        var token = auth.GenerateEmailVerificationToken(customer!);
        Assert.True(auth.ConfirmEmail("jane@example.com", token));

        Assert.Equal(customer!.Id, db.Bookings.Single().CustomerId);
    }

    [Fact]
    public void ConfirmEmail_AdoptsNothing_WhenTheTokenIsWrong()
    {
        using var db = NewDb();
        var store = new NoopTokenStore();
        var auth = new AuthService(db, store);

        var (customer, _) = auth.RegisterCustomer("jane@example.com", "Jane Doe", "07700900000", Password);
        SeedGuestBooking(db, "jane@example.com");
        auth.GenerateEmailVerificationToken(customer!);

        Assert.False(auth.ConfirmEmail("jane@example.com", new string('a', 64)));
        Assert.Null(db.Bookings.Single().CustomerId);
    }
}
