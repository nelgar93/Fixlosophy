using System.Security.Claims;
using Fixlosophy.Services;

namespace Fixlosophy.Tests;

public class AuthClaimsTests
{
    [Fact]
    public void BuildStaffPrincipal_SetsExpectedClaims()
    {
        var staff = new StaffMember { Id = "staff-1", FullName = "Ada Admin", Role = StaffRole.Admin };
        var principal = AuthClaims.BuildStaffPrincipal(staff);

        Assert.Equal("staff-1", principal.FindFirst(ClaimTypes.NameIdentifier)?.Value);
        Assert.Equal("Ada Admin", principal.FindFirst(ClaimTypes.Name)?.Value);
        Assert.Equal("Admin", principal.FindFirst(ClaimTypes.Role)?.Value);
        Assert.Equal(AuthClaims.StaffType, principal.FindFirst(AuthClaims.UserType)?.Value);
    }

    [Fact]
    public void BuildStaffPrincipal_WorkerRole_IsNotInAdminRole()
    {
        var staff = new StaffMember { Id = "staff-2", FullName = "Wendy Worker", Role = StaffRole.Worker };
        var principal = AuthClaims.BuildStaffPrincipal(staff);

        Assert.False(principal.IsInRole("Admin"));
        Assert.True(principal.IsInRole("Worker"));
    }

    [Fact]
    public void BuildCustomerPrincipal_SetsExpectedClaims()
    {
        var customer = new Customer { Id = "cust-1", FullName = "Cara Customer" };
        var principal = AuthClaims.BuildCustomerPrincipal(customer);

        Assert.Equal("cust-1", principal.FindFirst(ClaimTypes.NameIdentifier)?.Value);
        Assert.Equal("Cara Customer", principal.FindFirst(ClaimTypes.Name)?.Value);
        Assert.Equal(AuthClaims.CustomerRole, principal.FindFirst(ClaimTypes.Role)?.Value);
        Assert.Equal(AuthClaims.CustomerType, principal.FindFirst(AuthClaims.UserType)?.Value);
    }

    [Fact]
    public void BuildStaffPrincipal_IsAuthenticated()
    {
        var staff = new StaffMember { Id = "staff-1", FullName = "Ada Admin", Role = StaffRole.Worker };
        var principal = AuthClaims.BuildStaffPrincipal(staff);
        Assert.True(principal.Identity?.IsAuthenticated);
    }
}
