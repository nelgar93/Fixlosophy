using Fixlosophy.Data;
using Microsoft.AspNetCore.Identity;

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

    public List<StaffMember> GetAllStaff() =>
        db.Staff.OrderBy(s => s.FullName).ToList();

    public void SaveStaff(StaffMember staff)
    {
        if (db.Staff.Any(s => s.Id == staff.Id))
            db.Staff.Update(staff);
        else
            db.Staff.Add(staff);
        db.SaveChanges();
    }
}
