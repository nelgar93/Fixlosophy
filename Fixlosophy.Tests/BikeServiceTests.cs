using Fixlosophy.Data;
using Fixlosophy.Services;
using Microsoft.EntityFrameworkCore;

namespace Fixlosophy.Tests;

public class BikeServiceTests
{
    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static Customer NewCustomer(AppDbContext db, string email = "jane@example.com")
    {
        var customer = new Customer { Email = email, FullName = "Jane Doe", PasswordHash = "irrelevant" };
        db.Customers.Add(customer);
        db.SaveChanges();
        return customer;
    }

    [Fact]
    public void AddBike_TrimsAndSucceeds()
    {
        using var db = NewDb();
        var customer = NewCustomer(db);
        var svc = new BikeService(db);

        var (bike, error) = svc.AddBike(customer.Id, "  Trek Marlin 7, 2022  ");
        Assert.Null(error);
        Assert.Equal("Trek Marlin 7, 2022", bike!.MakeModel);
    }

    [Fact]
    public void AddBike_RejectsEmpty()
    {
        using var db = NewDb();
        var customer = NewCustomer(db);
        var svc = new BikeService(db);

        var (bike, error) = svc.AddBike(customer.Id, "   ");
        Assert.Null(bike);
        Assert.NotNull(error);
    }

    [Fact]
    public void AddBike_RejectsOverMaxLength()
    {
        using var db = NewDb();
        var customer = NewCustomer(db);
        var svc = new BikeService(db);

        var (bike, error) = svc.AddBike(customer.Id, new string('x', BikeService.MaxBikeNameLength + 1));
        Assert.Null(bike);
        Assert.NotNull(error);
    }

    [Fact]
    public void AddBike_RejectsCaseInsensitiveDuplicateForSameCustomer()
    {
        using var db = NewDb();
        var customer = NewCustomer(db);
        var svc = new BikeService(db);
        svc.AddBike(customer.Id, "Trek Marlin 7");

        var (bike, error) = svc.AddBike(customer.Id, "trek marlin 7");
        Assert.Null(bike);
        Assert.NotNull(error);
    }

    [Fact]
    public void AddBike_AllowsSameNameAcrossDifferentCustomers()
    {
        using var db = NewDb();
        var customerA = NewCustomer(db, "a@example.com");
        var customerB = NewCustomer(db, "b@example.com");
        var svc = new BikeService(db);

        svc.AddBike(customerA.Id, "Trek Marlin 7");
        var (bike, error) = svc.AddBike(customerB.Id, "Trek Marlin 7");
        Assert.Null(error);
        Assert.NotNull(bike);
    }

    [Fact]
    public void AddBike_EnforcesMaxBikesPerCustomer()
    {
        using var db = NewDb();
        var customer = NewCustomer(db);
        var svc = new BikeService(db);
        for (var i = 0; i < BikeService.MaxBikesPerCustomer; i++)
            svc.AddBike(customer.Id, $"Bike {i}");

        var (bike, error) = svc.AddBike(customer.Id, "One too many");
        Assert.Null(bike);
        Assert.NotNull(error);
    }

    [Fact]
    public void GetBikesForCustomer_OnlyReturnsOwnBikes()
    {
        using var db = NewDb();
        var customerA = NewCustomer(db, "a@example.com");
        var customerB = NewCustomer(db, "b@example.com");
        var svc = new BikeService(db);
        svc.AddBike(customerA.Id, "Trek Marlin 7");
        svc.AddBike(customerB.Id, "Brompton P6");

        var bikesA = svc.GetBikesForCustomer(customerA.Id);
        Assert.Single(bikesA);
        Assert.Equal("Trek Marlin 7", bikesA[0].MakeModel);
    }

    [Fact]
    public void RemoveBike_SucceedsForOwningCustomer()
    {
        using var db = NewDb();
        var customer = NewCustomer(db);
        var svc = new BikeService(db);
        var (bike, _) = svc.AddBike(customer.Id, "Trek Marlin 7");

        Assert.True(svc.RemoveBike(customer.Id, bike!.Id));
        Assert.Empty(svc.GetBikesForCustomer(customer.Id));
    }

    [Fact]
    public void RemoveBike_FailsForNonOwningCustomer_AndLeavesBikeIntact()
    {
        using var db = NewDb();
        var customerA = NewCustomer(db, "a@example.com");
        var customerB = NewCustomer(db, "b@example.com");
        var svc = new BikeService(db);
        var (bike, _) = svc.AddBike(customerA.Id, "Trek Marlin 7");

        Assert.False(svc.RemoveBike(customerB.Id, bike!.Id));
        Assert.Single(svc.GetBikesForCustomer(customerA.Id));
    }

    [Fact]
    public void RemoveBike_ReturnsFalse_WhenBikeDoesNotExist()
    {
        using var db = NewDb();
        var customer = NewCustomer(db);
        var svc = new BikeService(db);
        Assert.False(svc.RemoveBike(customer.Id, "nonexistent-id"));
    }
}
