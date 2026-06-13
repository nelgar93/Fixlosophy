using System.Security.Claims;
using Microsoft.AspNetCore.Authentication.Cookies;

namespace Fixlosophy.Services;

// Builds the ClaimsPrincipal stored in the auth cookie. Both staff and
// customers share one cookie scheme; the UserType claim distinguishes them and
// the role claim drives [Authorize(Roles = ...)] on protected pages. Only the
// id and a little display data live in the cookie — pages re-fetch the full
// record by id so permission/role changes take effect on next load.
public static class AuthClaims
{
    public const string UserType = "user_type";
    public const string StaffType = "staff";
    public const string CustomerType = "customer";

    public const string CustomerRole = "Customer";

    public static ClaimsPrincipal BuildStaffPrincipal(StaffMember staff)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, staff.Id),
            new(ClaimTypes.Name, staff.FullName),
            new(ClaimTypes.Role, staff.Role.ToString()),
            new(UserType, StaffType)
        };
        return Build(claims);
    }

    public static ClaimsPrincipal BuildCustomerPrincipal(Customer customer)
    {
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, customer.Id),
            new(ClaimTypes.Name, customer.FullName),
            new(ClaimTypes.Role, CustomerRole),
            new(UserType, CustomerType)
        };
        return Build(claims);
    }

    private static ClaimsPrincipal Build(IEnumerable<Claim> claims) =>
        new(new ClaimsIdentity(claims, CookieAuthenticationDefaults.AuthenticationScheme));
}
