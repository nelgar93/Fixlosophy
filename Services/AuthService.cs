using System.Text.RegularExpressions;
using Fixlosophy.Data;
using Microsoft.AspNetCore.Identity;

namespace Fixlosophy.Services;

public class AuthService(AppDbContext db)
{
    public const int MinPasswordLength = 8;

    private static readonly PasswordHasher<string> _hasher = new();

    // A fixed hash we verify against when an account isn't found, so a login for a
    // non-existent email costs the same as one for a real email. Without this, the
    // skipped PBKDF2 verify is a timing oracle for enumerating registered emails.
    private static readonly string _dummyHash = HashPassword("timing-equalizer-not-a-real-password");

    public static string HashPassword(string password) =>
        _hasher.HashPassword("", password);

    public static bool VerifyPassword(string hash, string password) =>
        _hasher.VerifyHashedPassword("", hash, password) != PasswordVerificationResult.Failed;

    // ── Shared input validation (registration + staff creation) ─────────────
    public static bool IsValidEmail(string? email) =>
        !string.IsNullOrWhiteSpace(email) &&
        Regex.IsMatch(email.Trim(), @"^[^@\s]+@[^@\s]+\.[^@\s]+$");

    public static string? ValidatePassword(string? password) =>
        string.IsNullOrEmpty(password) || password.Length < MinPasswordLength
            ? $"Password must be at least {MinPasswordLength} characters."
            : null;

    // Emails are stored and compared lower-cased so casing can't create duplicate
    // accounts or block a returning user (matches the booking dedupe's lower()).
    public static string NormalizeEmail(string email) => email.Trim().ToLowerInvariant();

    public StaffMember? AuthenticateStaff(string email, string password)
    {
        var normEmail = NormalizeEmail(email);
        var staff = db.Staff.FirstOrDefault(s => s.Email.ToLower() == normEmail && s.IsActive);
        if (staff is null)
        {
            VerifyPassword(_dummyHash, password); // equalize timing (anti-enumeration)
            return null;
        }
        return VerifyPassword(staff.PasswordHash, password) ? staff : null;
    }

    public Customer? AuthenticateCustomer(string email, string password)
    {
        var normEmail = NormalizeEmail(email);
        var customer = db.Customers.FirstOrDefault(c => c.Email.ToLower() == normEmail);
        if (customer is null)
        {
            VerifyPassword(_dummyHash, password); // equalize timing (anti-enumeration)
            return null;
        }
        return VerifyPassword(customer.PasswordHash, password) ? customer : null;
    }

    // Restore a persisted customer session from the auth cookie's subject id.
    public Customer? GetCustomerById(string id) =>
        db.Customers.FirstOrDefault(c => c.Id == id);

    public (Customer? customer, string? error) RegisterCustomer(
        string email, string fullName, string phone, string password)
    {
        // Server-side validation: the form's `required`/`type=email` are client-only
        // and a direct POST bypasses them, so re-check everything here.
        if (string.IsNullOrWhiteSpace(fullName))
            return (null, "Please enter your name.");
        if (!IsValidEmail(email))
            return (null, "Please enter a valid email address.");
        if (ValidatePassword(password) is { } pwError)
            return (null, pwError);

        var normEmail = NormalizeEmail(email);
        if (db.Customers.Any(c => c.Email.ToLower() == normEmail))
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
    public StaffMember? GetStaffById(string id) =>
        db.Staff.FirstOrDefault(s => s.Id == id && s.IsActive);

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
