using Fixlosophy.Data;
using Fixlosophy.Services;
using Microsoft.EntityFrameworkCore;

namespace Fixlosophy.Tests;

public class AuthServiceTests
{
    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static StaffMember SeedStaff(
        AppDbContext db, string email, string password,
        bool isActive = true, StaffRole role = StaffRole.Worker, string fullName = "Wendy Worker")
    {
        var staff = new StaffMember
        {
            FullName = fullName,
            Email = email,
            PasswordHash = AuthService.HashPassword(password),
            Role = role,
            IsActive = isActive
        };
        db.Staff.Add(staff);
        db.SaveChanges();
        return staff;
    }

    [Fact]
    public void HashPassword_RoundTripsThroughVerify()
    {
        var hash = AuthService.HashPassword("correct horse battery staple");
        Assert.True(AuthService.VerifyPassword(hash, "correct horse battery staple"));
    }

    [Fact]
    public void VerifyPassword_RejectsWrongPassword()
    {
        var hash = AuthService.HashPassword("correct horse battery staple");
        Assert.False(AuthService.VerifyPassword(hash, "Correct Horse Battery Staple"));
        Assert.False(AuthService.VerifyPassword(hash, ""));
    }

    [Fact]
    public void HashPassword_IsSalted_SoRepeatHashesDiffer()
    {
        Assert.NotEqual(AuthService.HashPassword("same"), AuthService.HashPassword("same"));
    }

    [Fact]
    public void RegisterCustomer_CreatesCustomer_AndTrimsInput()
    {
        using var db = NewDb();

        var (customer, error) = new AuthService(db)
            .RegisterCustomer("  jane@example.com  ", "  Jane Doe  ", "  +44 7700 900000  ", "pw");

        Assert.Null(error);
        Assert.NotNull(customer);
        Assert.Equal("jane@example.com", customer.Email);
        Assert.Equal("Jane Doe", customer.FullName);
        Assert.Equal("+44 7700 900000", customer.Phone);
        Assert.Single(db.Customers);
    }

    [Fact]
    public void RegisterCustomer_StoresHashedPassword_NotPlaintext()
    {
        using var db = NewDb();

        var (customer, _) = new AuthService(db)
            .RegisterCustomer("jane@example.com", "Jane Doe", "", "s3cret");

        Assert.NotEqual("s3cret", customer!.PasswordHash);
        Assert.True(AuthService.VerifyPassword(customer.PasswordHash, "s3cret"));
    }

    [Fact]
    public void RegisterCustomer_RejectsDuplicateEmail()
    {
        using var db = NewDb();
        var service = new AuthService(db);
        service.RegisterCustomer("jane@example.com", "Jane Doe", "", "pw");

        var (customer, error) = service.RegisterCustomer("jane@example.com", "Jane Again", "", "pw2");

        Assert.Null(customer);
        Assert.Contains("already exists", error);
        Assert.Single(db.Customers);
    }

    [Fact]
    public void AuthenticateCustomer_SucceedsWithCorrectPassword()
    {
        using var db = NewDb();
        var service = new AuthService(db);
        service.RegisterCustomer("jane@example.com", "Jane Doe", "", "pw");

        Assert.NotNull(service.AuthenticateCustomer("jane@example.com", "pw"));
    }

    [Fact]
    public void AuthenticateCustomer_RejectsWrongPasswordAndUnknownEmail()
    {
        using var db = NewDb();
        var service = new AuthService(db);
        service.RegisterCustomer("jane@example.com", "Jane Doe", "", "pw");

        Assert.Null(service.AuthenticateCustomer("jane@example.com", "wrong"));
        Assert.Null(service.AuthenticateCustomer("nobody@example.com", "pw"));
    }

    [Fact]
    public void AuthenticateStaff_SucceedsForActiveAccount()
    {
        using var db = NewDb();
        SeedStaff(db, "wendy@fixlosophy.com", "pw");

        Assert.NotNull(new AuthService(db).AuthenticateStaff("wendy@fixlosophy.com", "pw"));
    }

    [Fact]
    public void AuthenticateStaff_RejectsDeactivatedAccount()
    {
        using var db = NewDb();
        SeedStaff(db, "wendy@fixlosophy.com", "pw", isActive: false);

        Assert.Null(new AuthService(db).AuthenticateStaff("wendy@fixlosophy.com", "pw"));
    }

    [Fact]
    public void AuthenticateStaff_RejectsWrongPassword()
    {
        using var db = NewDb();
        SeedStaff(db, "wendy@fixlosophy.com", "pw");

        Assert.Null(new AuthService(db).AuthenticateStaff("wendy@fixlosophy.com", "nope"));
    }

    [Fact]
    public void GetStaffById_ReturnsNull_ForDeactivatedMember()
    {
        using var db = NewDb();
        var staff = SeedStaff(db, "wendy@fixlosophy.com", "pw", isActive: false);

        // The admin dashboard re-fetches by id on every load so a deactivation takes
        // effect immediately rather than being frozen into the auth cookie.
        Assert.Null(new AuthService(db).GetStaffById(staff.Id));
    }

    [Fact]
    public void GetStaffById_ReturnsActiveMember()
    {
        using var db = NewDb();
        var staff = SeedStaff(db, "wendy@fixlosophy.com", "pw");

        Assert.Equal(staff.Id, new AuthService(db).GetStaffById(staff.Id)?.Id);
    }

    [Fact]
    public void GetCustomerById_ReturnsNull_ForUnknownId()
    {
        using var db = NewDb();
        Assert.Null(new AuthService(db).GetCustomerById("does-not-exist"));
    }

    [Fact]
    public void GetAllStaff_IsOrderedByFullName_AndIncludesDeactivatedMembers()
    {
        using var db = NewDb();
        SeedStaff(db, "zoe@fixlosophy.com", "pw", fullName: "Zoe Zephyr");
        SeedStaff(db, "ada@fixlosophy.com", "pw", fullName: "Ada Admin", role: StaffRole.Admin);
        SeedStaff(db, "bob@fixlosophy.com", "pw", fullName: "Bob Builder", isActive: false);

        var staff = new AuthService(db).GetAllStaff();

        Assert.Equal(["Ada Admin", "Bob Builder", "Zoe Zephyr"], staff.Select(s => s.FullName));
    }
}
