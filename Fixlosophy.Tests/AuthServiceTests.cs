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

    // In-memory token store keeps these tests fast and fully offline — no real Redis.
    private static AuthService NewSvc(AppDbContext db) => new(db, new InMemoryVerificationTokenStore());

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

    [Theory]
    [InlineData("jane@example.com", true)]
    [InlineData("jane@sub.example.co.uk", true)]
    [InlineData("not-an-email", false)]
    [InlineData("missing-domain@", false)]
    [InlineData("@missing-local.com", false)]
    [InlineData("", false)]
    [InlineData(null, false)]
    public void IsValidEmail_ValidatesFormat(string? email, bool expected) =>
        Assert.Equal(expected, AuthService.IsValidEmail(email));

    [Theory]
    [InlineData("", false)]
    [InlineData(null, false)]
    [InlineData("short7", false)]
    [InlineData("exactly8", true)]
    [InlineData("a-much-longer-password", true)]
    public void ValidatePassword_EnforcesMinimumLength(string? password, bool expectValid)
    {
        var error = AuthService.ValidatePassword(password);
        Assert.Equal(expectValid, error is null);
    }

    [Fact]
    public void NormalizeEmail_TrimsAndLowercases() =>
        Assert.Equal("jane@example.com", AuthService.NormalizeEmail("  Jane@Example.COM  "));

    [Fact]
    public void HashPassword_VerifyPassword_RoundTripsCorrectly()
    {
        var hash = AuthService.HashPassword("correct-horse-battery-staple");
        Assert.True(AuthService.VerifyPassword(hash, "correct-horse-battery-staple"));
        Assert.False(AuthService.VerifyPassword(hash, "wrong-password"));
    }

    [Fact]
    public void AuthenticateCustomer_SucceedsWithCorrectCredentials_CaseInsensitiveEmail()
    {
        using var db = NewDb();
        var svc = NewSvc(db);
        var (customer, error) = svc.RegisterCustomer("Jane@Example.com", "Jane Doe", "", "hunter2pass");
        Assert.Null(error);
        Assert.NotNull(customer);

        var authed = svc.AuthenticateCustomer("JANE@EXAMPLE.COM", "hunter2pass");
        Assert.NotNull(authed);
        Assert.Equal(customer!.Id, authed!.Id);
    }

    [Fact]
    public void AuthenticateCustomer_FailsWithWrongPassword()
    {
        using var db = NewDb();
        var svc = NewSvc(db);
        svc.RegisterCustomer("jane@example.com", "Jane Doe", "", "hunter2pass");

        Assert.Null(svc.AuthenticateCustomer("jane@example.com", "wrong-password"));
    }

    [Fact]
    public void AuthenticateCustomer_FailsForUnknownEmail_WithoutThrowing() // exercises the anti-enumeration dummy-hash path
    {
        using var db = NewDb();
        var svc = NewSvc(db);
        Assert.Null(svc.AuthenticateCustomer("nobody@example.com", "whatever123"));
    }

    [Fact]
    public void RegisterCustomer_RejectsWeakPassword()
    {
        using var db = NewDb();
        var svc = NewSvc(db);
        var (customer, error) = svc.RegisterCustomer("jane@example.com", "Jane Doe", "", "short");
        Assert.Null(customer);
        Assert.NotNull(error);
    }

    [Fact]
    public void RegisterCustomer_RejectsInvalidEmail()
    {
        using var db = NewDb();
        var svc = NewSvc(db);
        var (customer, error) = svc.RegisterCustomer("not-an-email", "Jane Doe", "", "hunter2pass");
        Assert.Null(customer);
        Assert.NotNull(error);
    }

    [Fact]
    public void RegisterCustomer_RejectsDuplicateEmail_CaseInsensitive()
    {
        using var db = NewDb();
        var svc = NewSvc(db);
        svc.RegisterCustomer("jane@example.com", "Jane Doe", "", "hunter2pass");

        var (customer, error) = svc.RegisterCustomer("JANE@EXAMPLE.COM", "Impersonator", "", "hunter2pass");
        Assert.Null(customer);
        Assert.Equal("An account with this email already exists.", error);
    }

    [Fact]
    public void AuthenticateStaff_RejectsDeactivatedStaff()
    {
        using var db = NewDb();
        db.Staff.Add(new StaffMember
        {
            Email = "staff@example.com",
            FullName = "Staff Member",
            PasswordHash = AuthService.HashPassword("staffpass123"),
            IsActive = false
        });
        db.SaveChanges();

        var svc = NewSvc(db);
        Assert.Null(svc.AuthenticateStaff("staff@example.com", "staffpass123"));
    }

    [Fact]
    public void AuthenticateStaff_SucceedsForActiveStaff()
    {
        using var db = NewDb();
        db.Staff.Add(new StaffMember
        {
            Email = "staff@example.com",
            FullName = "Staff Member",
            PasswordHash = AuthService.HashPassword("staffpass123"),
            IsActive = true
        });
        db.SaveChanges();

        var svc = NewSvc(db);
        Assert.NotNull(svc.AuthenticateStaff("staff@example.com", "staffpass123"));
    }

    // ── Email verification ───────────────────────────────────────────────────

    [Fact]
    public void RegisterCustomer_NewAccountStartsUnverified()
    {
        using var db = NewDb();
        var svc = NewSvc(db);
        var (customer, _) = svc.RegisterCustomer("jane@example.com", "Jane Doe", "", "hunter2pass");
        Assert.False(customer!.EmailConfirmed);
    }

    [Fact]
    public void ConfirmEmail_SucceedsWithValidToken_AndIsSingleUse()
    {
        using var db = NewDb();
        var svc = NewSvc(db);
        var (customer, _) = svc.RegisterCustomer("jane@example.com", "Jane Doe", "", "hunter2pass");
        var token = svc.GenerateEmailVerificationToken(customer!);

        Assert.True(svc.ConfirmEmail(customer!.Email, token));

        var reloaded = svc.GetCustomerByEmail("jane@example.com");
        Assert.True(reloaded!.EmailConfirmed);

        // Token is removed from the store on success, so replaying the same link fails.
        Assert.False(svc.ConfirmEmail(customer.Email, token));
    }

    [Fact]
    public void ConfirmEmail_FailsWithWrongToken()
    {
        using var db = NewDb();
        var svc = NewSvc(db);
        var (customer, _) = svc.RegisterCustomer("jane@example.com", "Jane Doe", "", "hunter2pass");
        svc.GenerateEmailVerificationToken(customer!);

        Assert.False(svc.ConfirmEmail(customer!.Email, "not-the-real-token"));
    }

    [Fact]
    public void ConfirmEmail_FailsWithExpiredToken()
    {
        using var db = NewDb();
        var store = new InMemoryVerificationTokenStore();
        var svc = new AuthService(db, store);
        var (customer, _) = svc.RegisterCustomer("jane@example.com", "Jane Doe", "", "hunter2pass");
        var token = svc.GenerateEmailVerificationToken(customer!);

        // Overwrite with a negative TTL to simulate the 24h window having already
        // passed, without waiting for it — the store treats it as already expired.
        store.SetToken($"verify:{customer!.Email}", "irrelevant-hash", TimeSpan.FromSeconds(-1));

        Assert.False(svc.ConfirmEmail(customer.Email, token));
    }

    [Fact]
    public void RegenerateEmailVerificationTokenIfNeeded_NoOpsWhileCooldownActive()
    {
        using var db = NewDb();
        var svc = NewSvc(db);
        var (customer, _) = svc.RegisterCustomer("jane@example.com", "Jane Doe", "", "hunter2pass");
        svc.GenerateEmailVerificationToken(customer!);

        Assert.Null(svc.RegenerateEmailVerificationTokenIfNeeded(customer!));
    }

    [Fact]
    public void RegenerateEmailVerificationTokenIfNeeded_SucceedsAfterCooldownEvenThoughTokenStillValid()
    {
        using var db = NewDb();
        var store = new InMemoryVerificationTokenStore();
        var svc = new AuthService(db, store);
        var (customer, _) = svc.RegisterCustomer("jane@example.com", "Jane Doe", "", "hunter2pass");
        var firstToken = svc.GenerateEmailVerificationToken(customer!);

        // Cooldown already elapsed, but the 24h verification token is still live.
        store.SetToken($"verify-cooldown:{customer!.Email}", "1", TimeSpan.FromSeconds(-1));

        var secondToken = svc.RegenerateEmailVerificationTokenIfNeeded(customer);
        Assert.NotNull(secondToken);
        Assert.NotEqual(firstToken, secondToken);

        // The new token replaced the old one — only the newest link is live.
        Assert.False(svc.ConfirmEmail(customer.Email, firstToken));
        Assert.True(svc.ConfirmEmail(customer.Email, secondToken!));
    }

    [Fact]
    public void RegenerateEmailVerificationTokenIfNeeded_NoOpsOnceAlreadyVerified()
    {
        using var db = NewDb();
        var svc = NewSvc(db);
        var (customer, _) = svc.RegisterCustomer("jane@example.com", "Jane Doe", "", "hunter2pass");
        var token = svc.GenerateEmailVerificationToken(customer!);
        svc.ConfirmEmail(customer!.Email, token);

        Assert.Null(svc.RegenerateEmailVerificationTokenIfNeeded(customer));
    }

    // ── Forgot password ───────────────────────────────────────────────────────

    [Fact]
    public void RequestCustomerPasswordReset_ReturnsNullForUnknownEmail() // anti-enumeration
    {
        using var db = NewDb();
        var svc = NewSvc(db);
        Assert.Null(svc.RequestCustomerPasswordReset("nobody@example.com"));
    }

    [Fact]
    public void RequestCustomerPasswordReset_ReturnsNullWhileCooldownActive() // resend-abuse debounce
    {
        using var db = NewDb();
        var svc = NewSvc(db);
        svc.RegisterCustomer("jane@example.com", "Jane Doe", "", "hunter2pass");

        Assert.NotNull(svc.RequestCustomerPasswordReset("jane@example.com"));
        Assert.Null(svc.RequestCustomerPasswordReset("jane@example.com"));
    }

    [Fact]
    public void RequestCustomerPasswordReset_SucceedsAfterCooldownEvenThoughTokenStillValid()
    {
        using var db = NewDb();
        var svc = NewSvc(db);
        var (customer, _) = svc.RegisterCustomer("jane@example.com", "Jane Doe", "", "hunter2pass");
        var firstToken = svc.RequestCustomerPasswordReset("jane@example.com");
        Assert.NotNull(firstToken);

        // Cooldown already elapsed, but the 60-minute reset token is still valid.
        customer!.ResetCooldownUntil = DateTime.Now.AddSeconds(-1);
        db.SaveChanges();

        var secondToken = svc.RequestCustomerPasswordReset("jane@example.com");
        Assert.NotNull(secondToken);
        Assert.NotEqual(firstToken, secondToken);

        // The new token replaced the old one — only the newest link is live.
        var (firstOk, _, _) = svc.ResetPasswordByToken(firstToken!, "newpassword1");
        Assert.False(firstOk);
        var (secondOk, _, _) = svc.ResetPasswordByToken(secondToken!, "newpassword1");
        Assert.True(secondOk);
    }

    [Fact]
    public void RequestStaffPasswordReset_ReturnsNullForUnknownEmail()
    {
        using var db = NewDb();
        var svc = NewSvc(db);
        Assert.Null(svc.RequestStaffPasswordReset("nobody@example.com"));
    }

    [Fact]
    public void ResetPasswordByToken_SucceedsForCustomer_AndAllowsLoginWithNewPassword()
    {
        using var db = NewDb();
        var svc = NewSvc(db);
        svc.RegisterCustomer("jane@example.com", "Jane Doe", "", "hunter2pass");
        var token = svc.RequestCustomerPasswordReset("jane@example.com")!;

        var (ok, isStaff, error) = svc.ResetPasswordByToken(token, "newpassword1");
        Assert.True(ok);
        Assert.False(isStaff);
        Assert.Null(error);
        Assert.NotNull(svc.AuthenticateCustomer("jane@example.com", "newpassword1"));
    }

    [Fact]
    public void ResetPasswordByToken_SucceedsForStaff()
    {
        using var db = NewDb();
        db.Staff.Add(new StaffMember
        {
            Email = "staff@example.com",
            FullName = "Staff Member",
            PasswordHash = AuthService.HashPassword("staffpass123"),
            IsActive = true
        });
        db.SaveChanges();

        var svc = NewSvc(db);
        var token = svc.RequestStaffPasswordReset("staff@example.com")!;

        var (ok, isStaff, error) = svc.ResetPasswordByToken(token, "newpassword1");
        Assert.True(ok);
        Assert.True(isStaff);
        Assert.Null(error);
        Assert.NotNull(svc.AuthenticateStaff("staff@example.com", "newpassword1"));
    }

    [Fact]
    public void ResetPasswordByToken_FailsForGarbageToken()
    {
        using var db = NewDb();
        var svc = NewSvc(db);
        var (ok, _, error) = svc.ResetPasswordByToken("not-a-real-token", "newpassword1");
        Assert.False(ok);
        Assert.NotNull(error);
    }

    [Fact]
    public void ResetPasswordByToken_FailsForExpiredToken()
    {
        using var db = NewDb();
        var svc = NewSvc(db);
        var (customer, _) = svc.RegisterCustomer("jane@example.com", "Jane Doe", "", "hunter2pass");
        var token = svc.RequestCustomerPasswordReset("jane@example.com")!;
        customer!.ResetTokenExpiresAt = DateTime.Now.AddSeconds(-1);
        db.SaveChanges();

        var (ok, _, _) = svc.ResetPasswordByToken(token, "newpassword1");
        Assert.False(ok);
    }

    [Fact]
    public void ResetPasswordByToken_RejectsWeakNewPassword()
    {
        using var db = NewDb();
        var svc = NewSvc(db);
        svc.RegisterCustomer("jane@example.com", "Jane Doe", "", "hunter2pass");
        var token = svc.RequestCustomerPasswordReset("jane@example.com")!;

        var (ok, _, error) = svc.ResetPasswordByToken(token, "short");
        Assert.False(ok);
        Assert.NotNull(error);
    }

    // ── Profile / password change ────────────────────────────────────────────

    [Fact]
    public void UpdateCustomerProfile_UpdatesNameAndPhone()
    {
        using var db = NewDb();
        var svc = NewSvc(db);
        var (customer, _) = svc.RegisterCustomer("jane@example.com", "Jane Doe", "", "hunter2pass");

        var (updated, error) = svc.UpdateCustomerProfile(customer!.Id, "Jane Smith", "+1 555 0100");
        Assert.Null(error);
        Assert.Equal("Jane Smith", updated!.FullName);
        Assert.Equal("+1 555 0100", updated.Phone);
    }

    [Fact]
    public void UpdateCustomerProfile_RejectsEmptyName()
    {
        using var db = NewDb();
        var svc = NewSvc(db);
        var (customer, _) = svc.RegisterCustomer("jane@example.com", "Jane Doe", "", "hunter2pass");

        var (updated, error) = svc.UpdateCustomerProfile(customer!.Id, "   ", "+1 555 0100");
        Assert.Null(updated);
        Assert.NotNull(error);
    }

    [Fact]
    public void ChangeCustomerPassword_SucceedsAndAllowsLoginWithNewPassword()
    {
        using var db = NewDb();
        var svc = NewSvc(db);
        var (customer, _) = svc.RegisterCustomer("jane@example.com", "Jane Doe", "", "hunter2pass");

        var (ok, error) = svc.ChangeCustomerPassword(customer!.Id, "hunter2pass", "newpassword1");
        Assert.True(ok);
        Assert.Null(error);
        Assert.NotNull(svc.AuthenticateCustomer("jane@example.com", "newpassword1"));
        Assert.Null(svc.AuthenticateCustomer("jane@example.com", "hunter2pass"));
    }

    [Fact]
    public void ChangeCustomerPassword_RejectsWrongCurrentPassword()
    {
        using var db = NewDb();
        var svc = NewSvc(db);
        var (customer, _) = svc.RegisterCustomer("jane@example.com", "Jane Doe", "", "hunter2pass");

        var (ok, error) = svc.ChangeCustomerPassword(customer!.Id, "wrong-password", "newpassword1");
        Assert.False(ok);
        Assert.NotNull(error);
        Assert.NotNull(svc.AuthenticateCustomer("jane@example.com", "hunter2pass"));
    }

    [Fact]
    public void ChangeCustomerPassword_RejectsWeakNewPassword()
    {
        using var db = NewDb();
        var svc = NewSvc(db);
        var (customer, _) = svc.RegisterCustomer("jane@example.com", "Jane Doe", "", "hunter2pass");

        var (ok, error) = svc.ChangeCustomerPassword(customer!.Id, "hunter2pass", "short");
        Assert.False(ok);
        Assert.NotNull(error);
    }

    // ── Ported from the pre-merge suite on this branch ───────────────────────
    // Passwords here are >= AuthService.MinPasswordLength; the originals predated
    // RegisterCustomer's password validation and would now be rejected.

    [Fact]
    public void HashPassword_IsSalted_SoRepeatHashesDiffer()
    {
        Assert.NotEqual(AuthService.HashPassword("same"), AuthService.HashPassword("same"));
    }

    [Fact]
    public void RegisterCustomer_CreatesCustomer_AndTrimsInput()
    {
        using var db = NewDb();

        var (customer, error) = NewSvc(db)
            .RegisterCustomer("  jane@example.com  ", "  Jane Doe  ", "  +44 7700 900000  ", "hunter2pass");

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

        var (customer, _) = NewSvc(db)
            .RegisterCustomer("jane@example.com", "Jane Doe", "", "s3cretpass");

        Assert.NotEqual("s3cretpass", customer!.PasswordHash);
        Assert.True(AuthService.VerifyPassword(customer.PasswordHash, "s3cretpass"));
    }

    [Fact]
    public void AuthenticateStaff_RejectsWrongPassword()
    {
        using var db = NewDb();
        SeedStaff(db, "wendy@fixlosophy.com", "staffpass123");

        Assert.Null(NewSvc(db).AuthenticateStaff("wendy@fixlosophy.com", "nope"));
    }

    [Fact]
    public void GetStaffById_ReturnsNull_ForDeactivatedMember()
    {
        using var db = NewDb();
        var staff = SeedStaff(db, "wendy@fixlosophy.com", "staffpass123", isActive: false);

        // The admin dashboard re-fetches by id on every load so a deactivation takes
        // effect immediately rather than being frozen into the auth cookie.
        Assert.Null(NewSvc(db).GetStaffById(staff.Id));
    }

    [Fact]
    public void GetStaffById_ReturnsActiveMember()
    {
        using var db = NewDb();
        var staff = SeedStaff(db, "wendy@fixlosophy.com", "staffpass123");

        Assert.Equal(staff.Id, NewSvc(db).GetStaffById(staff.Id)?.Id);
    }

    [Fact]
    public void GetCustomerById_ReturnsNull_ForUnknownId()
    {
        using var db = NewDb();
        Assert.Null(NewSvc(db).GetCustomerById("does-not-exist"));
    }

    [Fact]
    public void GetAllStaff_IsOrderedByFullName_AndIncludesDeactivatedMembers()
    {
        using var db = NewDb();
        SeedStaff(db, "zoe@fixlosophy.com", "staffpass123", fullName: "Zoe Zephyr");
        SeedStaff(db, "ada@fixlosophy.com", "staffpass123", fullName: "Ada Admin", role: StaffRole.Admin);
        SeedStaff(db, "bob@fixlosophy.com", "staffpass123", fullName: "Bob Builder", isActive: false);

        var staff = NewSvc(db).GetAllStaff();

        Assert.Equal(["Ada Admin", "Bob Builder", "Zoe Zephyr"], staff.Select(s => s.FullName));
    }
}
