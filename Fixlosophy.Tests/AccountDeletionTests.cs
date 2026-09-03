using Fixlosophy.Data;
using Fixlosophy.Services;
using Microsoft.EntityFrameworkCore;

namespace Fixlosophy.Tests;

// UK GDPR right to erasure. The important property is that deletion actually erases:
// the FK is ON DELETE SET NULL, so removing the customer row alone would detach the
// bookings while leaving the name, email and phone sitting on them in plain text.
public class AccountDeletionTests
{
    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static AuthService NewService(AppDbContext db) => new(db);

    private static Customer SeedCustomer(AppDbContext db)
    {
        var customer = new Customer
        {
            Email = "jane@example.com",
            FullName = "Jane Doe",
            Phone = "07700900000",
            PasswordHash = AuthService.HashPassword("correct-horse"),
            EmailConfirmed = true
        };
        db.Customers.Add(customer);
        db.Bikes.Add(new Bike { CustomerId = customer.Id, MakeModel = "Trek FX3" });
        db.Bookings.Add(new Booking
        {
            Reference = "FIX-260830-001",
            CustomerId = customer.Id,
            CustomerName = "Jane Doe",
            CustomerEmail = "jane@example.com",
            CustomerPhone = "07700900000",
            Notes = "Creaking bottom bracket",
            ServiceName = "Full Service",
            SlotDate = DateTime.Today.AddDays(3),
            SlotTime = "10:00"
        });
        db.SaveChanges();
        return customer;
    }

    [Fact]
    public void DeleteCustomerAccount_RemovesTheCustomerRow()
    {
        using var db = NewDb();
        var customer = SeedCustomer(db);

        Assert.True(NewService(db).DeleteCustomerAccount(customer.Id));
        Assert.Empty(db.Customers);
    }

    [Fact]
    public void DeleteCustomerAccount_ScrubsIdentifyingFieldsFromBookings()
    {
        using var db = NewDb();
        var customer = SeedCustomer(db);

        NewService(db).DeleteCustomerAccount(customer.Id);

        // The booking survives as a record of work carried out...
        var booking = db.Bookings.Single();
        Assert.Equal("FIX-260830-001", booking.Reference);
        Assert.Equal("Full Service", booking.ServiceName);
        // ...but nothing on it points at a person any more.
        Assert.Null(booking.CustomerId);
        Assert.Equal("", booking.CustomerEmail);
        Assert.Equal("", booking.CustomerPhone);
        Assert.Equal("", booking.Notes);
        Assert.DoesNotContain("Jane", booking.CustomerName, StringComparison.Ordinal);
    }

    [Fact]
    public void DeleteCustomerAccount_ReturnsFalse_ForAnUnknownId()
    {
        using var db = NewDb();
        Assert.False(NewService(db).DeleteCustomerAccount("no-such-customer"));
    }

    [Fact]
    public void ExportCustomerData_ReturnsAccountBikesAndBookings()
    {
        using var db = NewDb();
        var customer = SeedCustomer(db);

        var export = NewService(db).ExportCustomerData(customer.Id);

        Assert.NotNull(export);
        Assert.Equal("jane@example.com", export.Account.Email);
        Assert.Equal("Trek FX3", Assert.Single(export.Bikes).MakeModel);
        Assert.Equal("FIX-260830-001", Assert.Single(export.Bookings).Reference);
    }

    // The export answers "what do you hold about me", not "give me the credential
    // material" — the shape deliberately has nowhere to put a password hash.
    [Fact]
    public void ExportCustomerData_DoesNotIncludeSecrets()
    {
        using var db = NewDb();
        var customer = SeedCustomer(db);

        var export = NewService(db).ExportCustomerData(customer.Id)!;
        var json = System.Text.Json.JsonSerializer.Serialize(export);

        Assert.DoesNotContain("PasswordHash", json, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("ResetToken", json, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void ExportCustomerData_ReturnsNull_ForAnUnknownId()
    {
        using var db = NewDb();
        Assert.Null(NewService(db).ExportCustomerData("no-such-customer"));
    }
}
