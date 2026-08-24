using Fixlosophy.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Fixlosophy.Services;

public class AuthService(AppDbContext db)
{
    private static readonly PasswordHasher<string> _hasher = new();

    public static string HashPassword(string password) =>
        _hasher.HashPassword("", password);

    public static bool VerifyPassword(string hash, string password) =>
        _hasher.VerifyHashedPassword("", hash, password) != PasswordVerificationResult.Failed;

    public StaffMember? AuthenticateStaff(string email, string password)
    {
        var staff = db.Staff.FirstOrDefault(s => s.Email == email.Trim() && s.IsActive);
        return staff != null && VerifyPassword(staff.PasswordHash, password) ? staff : null;
    }

    public Customer? AuthenticateCustomer(string email, string password)
    {
        var customer = db.Customers.FirstOrDefault(c => c.Email == email.Trim());
        return customer != null && VerifyPassword(customer.PasswordHash, password) ? customer : null;
    }

    // Restore a persisted customer session from the auth cookie's subject id.
    public Customer? GetCustomerById(string id) =>
        db.Customers.FirstOrDefault(c => c.Id == id);

    public (Customer? customer, string? error) RegisterCustomer(
        string email, string fullName, string phone, string password)
    {
        var normEmail = email.Trim();
        if (db.Customers.Any(c => c.Email == normEmail))
            return (null, "An account with this email already exists.");

        var customer = new Customer
        {
            Email = normEmail,
            FullName = fullName.Trim(),
            Phone = phone.Trim(),
            PasswordHash = HashPassword(password)
        };
        db.Customers.Add(customer);
        db.SaveChanges();
        return (customer, null);
    }

    // Restore a persisted session: re-fetch from the DB so we honour any
    // deactivation/role change since the cookie was issued.
    // Untracked: this feeds read-only UI, and in Blazor Server the DbContext is
    // scoped to the whole circuit, so a tracked entity here would let unrelated
    // SaveChanges() calls pick up edits the admin never confirmed.
    public StaffMember? GetStaffById(string id) =>
        db.Staff.AsNoTracking().FirstOrDefault(s => s.Id == id && s.IsActive);

    // Untracked for the same reason: the admin dashboard binds permission
    // checkboxes straight to these instances, and those edits must not reach the
    // database until SaveStaff is called.
    public List<StaffMember> GetAllStaff() =>
        db.Staff.AsNoTracking().OrderBy(s => s.FullName).ToList();

    // Returns null on success, or a message to show the admin. The unique index
    // IX_Staff_Email turns a repeated address into a DbUpdateException, which
    // would otherwise escape a Blazor event handler, tear down the circuit and
    // leave the failed entry poisoning every later save in that session.
    public string? SaveStaff(StaffMember staff)
    {
        staff.Email = staff.Email.Trim();
        if (string.IsNullOrEmpty(staff.Email))
            return "Email is required.";

        var normEmail = staff.Email.ToLower();
        if (db.Staff.Any(s => s.Email.ToLower() == normEmail && s.Id != staff.Id))
            return "A staff member with this email already exists.";

        // Find checks the change tracker before the database, so an admin editing
        // their own row reuses the instance GetStaffById may already have loaded
        // instead of tripping "another instance with the same key is already
        // being tracked". SetValues copies scalars only, leaving navigations alone.
        var existing = db.Staff.Find(staff.Id);
        if (existing is null)
            db.Staff.Add(staff);
        else
            db.Entry(existing).CurrentValues.SetValues(staff);

        try
        {
            db.SaveChanges();
        }
        catch (DbUpdateException)
        {
            // Lost a race against IX_Staff_Email; detach so the circuit-scoped
            // context stays usable.
            db.Entry(existing ?? staff).State = EntityState.Detached;
            return "A staff member with this email already exists.";
        }
        return null;
    }
}
