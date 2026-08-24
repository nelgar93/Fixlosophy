using System.Security.Cryptography;
using System.Text;
using System.Text.RegularExpressions;
using Fixlosophy.Data;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;

namespace Fixlosophy.Services;

public class AuthService(AppDbContext db, IVerificationTokenStore tokenStore)
{
    public const int MinPasswordLength = 8;

    private const int VerificationTokenHours = 24;
    private const int ResetTokenMinutes = 60;

    // How soon a NEW link can be requested — independent of how long an issued
    // link stays valid (VerificationTokenHours / ResetTokenMinutes above), so a
    // genuinely lost email can be retried quickly instead of being blocked for
    // the link's whole validity window.
    private const int ResendCooldownSeconds = 60;

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
        // ToLower() here is translated to SQL lower(...) by EF Core — the analyzer's
        // suggested StringComparison overload isn't SQL-translatable and would throw.
#pragma warning disable CA1304, CA1311, CA1862
        var staff = db.Staff.FirstOrDefault(s => s.Email.ToLower() == normEmail && s.IsActive);
#pragma warning restore CA1304, CA1311, CA1862
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
#pragma warning disable CA1304, CA1311, CA1862 // SQL-translated by EF Core; see AuthenticateStaff.
        var customer = db.Customers.FirstOrDefault(c => c.Email.ToLower() == normEmail);
#pragma warning restore CA1304, CA1311, CA1862
        if (customer is null)
        {
            VerifyPassword(_dummyHash, password); // equalize timing (anti-enumeration)
            return null;
        }
        return VerifyPassword(customer.PasswordHash, password) ? customer : null;
    }

    // Restore a persisted customer session from the auth cookie's subject id.
    // Bikes are eager-loaded (cheap — a handful of rows per customer at most) so
    // callers like Book.razor/Account.razor can read loggedInCustomer.Bikes with
    // no extra query.
    public Customer? GetCustomerById(string id) =>
        db.Customers.Include(c => c.Bikes.OrderBy(b => b.CreatedAt)).FirstOrDefault(c => c.Id == id);

    public Customer? GetCustomerByEmail(string email)
    {
        var normEmail = NormalizeEmail(email);
#pragma warning disable CA1304, CA1311, CA1862 // SQL-translated by EF Core; see AuthenticateStaff.
        return db.Customers.FirstOrDefault(c => c.Email.ToLower() == normEmail);
#pragma warning restore CA1304, CA1311, CA1862
    }

    public StaffMember? GetStaffByEmail(string email)
    {
        var normEmail = NormalizeEmail(email);
#pragma warning disable CA1304, CA1311, CA1862 // SQL-translated by EF Core; see AuthenticateStaff.
        return db.Staff.FirstOrDefault(s => s.Email.ToLower() == normEmail && s.IsActive);
#pragma warning restore CA1304, CA1311, CA1862
    }

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
#pragma warning disable CA1304, CA1311, CA1862 // SQL-translated by EF Core; see AuthenticateStaff.
        if (db.Customers.Any(c => c.Email.ToLower() == normEmail))
#pragma warning restore CA1304, CA1311, CA1862
            return (null, "An account with this email already exists.");

        var customer = new Customer
        {
            Email = normEmail,
            FullName = fullName.Trim(),
            Phone = phone.Trim(),
            PasswordHash = HashPassword(password),
            EmailConfirmed = false
        };
        db.Customers.Add(customer);
        db.SaveChanges();
        return (customer, null);
    }

    // Email is intentionally not editable here — it's the login identity.
    public (Customer? customer, string? error) UpdateCustomerProfile(string customerId, string fullName, string phone)
    {
        if (string.IsNullOrWhiteSpace(fullName))
            return (null, "Please enter your name.");

        var customer = db.Customers.Find(customerId);
        if (customer is null) return (null, "Account not found.");

        customer.FullName = fullName.Trim();
        customer.Phone = (phone ?? "").Trim();
        db.SaveChanges();
        return (customer, null);
    }

    public (bool ok, string? error) ChangeCustomerPassword(string customerId, string currentPassword, string newPassword)
    {
        var customer = db.Customers.Find(customerId);
        if (customer is null) return (false, "Account not found.");
        if (!VerifyPassword(customer.PasswordHash, currentPassword))
            return (false, "Current password is incorrect.");
        if (ValidatePassword(newPassword) is { } pwError)
            return (false, pwError);

        customer.PasswordHash = HashPassword(newPassword);
        db.SaveChanges();
        return (true, null);
    }

    // ── Email verification ───────────────────────────────────────────────────
    // Token expiry is backed by Redis (via tokenStore), keyed by email, using TTL
    // instead of a manually-compared timestamp column — see IVerificationTokenStore.

    // 256 bits of entropy, hex-encoded. Only the hash is persisted — the raw token
    // exists only in the emailed link and the request that redeems it — so a store
    // leak can't be replayed as a working verification link.
    private static string GenerateToken() => RandomNumberGenerator.GetHexString(64);

    private static string HashToken(string token) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(token)));

    private static (string token, string hash) NewCandidate()
    {
        var token = GenerateToken();
        return (token, HashToken(token));
    }

    private static string VerifyKey(string email) => $"verify:{NormalizeEmail(email)}";
    private static string VerifyCooldownKey(string email) => $"verify-cooldown:{NormalizeEmail(email)}";

    public string GenerateEmailVerificationToken(Customer customer)
    {
        var (token, hash) = NewCandidate();
        tokenStore.SetToken(VerifyKey(customer.Email), hash, TimeSpan.FromHours(VerificationTokenHours));
        // Starts the resend cooldown right away too, not just on an explicit resend.
        tokenStore.SetToken(VerifyCooldownKey(customer.Email), "1", TimeSpan.FromSeconds(ResendCooldownSeconds));
        return token;
    }

    // Resend: no-ops while still inside the cooldown window, so repeatedly hitting
    // "resend" can't be used to spam a victim's inbox. The cooldown is a separate
    // key/TTL from the verification token itself, so it's deliberately shorter
    // than the token's 24h validity — a genuinely lost email can be retried in a
    // minute instead of being stuck for a day. TrySetTokenIfAbsent on the cooldown
    // key is atomic, so the "still cooling down" check and claiming the next
    // resend slot happen as one operation — no check-then-write race.
    public string? RegenerateEmailVerificationTokenIfNeeded(Customer customer)
    {
        if (customer.EmailConfirmed) return null;
        if (!tokenStore.TrySetTokenIfAbsent(VerifyCooldownKey(customer.Email), "1", TimeSpan.FromSeconds(ResendCooldownSeconds)))
            return null;

        // Unconditionally overwrites the previous (still-valid) token — only the
        // newest link is ever live, same as before.
        var (token, hash) = NewCandidate();
        tokenStore.SetToken(VerifyKey(customer.Email), hash, TimeSpan.FromHours(VerificationTokenHours));
        return token;
    }

    public bool ConfirmEmail(string email, string token)
    {
        var key = VerifyKey(email);
        var storedHash = tokenStore.GetTokenHash(key);
        if (storedHash is null ||
            !CryptographicOperations.FixedTimeEquals(
                Convert.FromHexString(storedHash), Convert.FromHexString(HashToken(token))))
            return false;

        var customer = GetCustomerByEmail(email);
        if (customer is null) return false;

        customer.EmailConfirmed = true;
        // Save first, then invalidate the token: if the process dies in between, a
        // replayed link just redundantly re-confirms an already-confirmed row and
        // retries the removal — never burns the only working link with no DB change.
        db.SaveChanges();
        tokenStore.RemoveToken(key);
        return true;
    }

    // ── Forgot password ───────────────────────────────────────────────────────

    // Returns the raw token to email, or null if there's no account for this email
    // OR a resend was already requested within the last ResendCooldownSeconds.
    // Callers MUST show the identical "check your email" response for both null
    // cases — never reveal which — the same anti-enumeration property the
    // dummy-hash protects on the login path, applied here to this lookup instead.
    // The cooldown is deliberately shorter than the token's own validity
    // (ResetTokenExpiresAt) so a genuinely lost email can be retried quickly; each
    // new token replaces the previous one, so only one link is ever live.
    public string? RequestCustomerPasswordReset(string email)
    {
        var customer = GetCustomerByEmail(email);
        if (customer is null) return null;
        if (customer.ResetCooldownUntil is { } cooldown && cooldown > DateTime.Now) return null;

        var token = GenerateToken();
        customer.ResetTokenHash = HashToken(token);
        customer.ResetTokenExpiresAt = DateTime.Now.AddMinutes(ResetTokenMinutes);
        customer.ResetCooldownUntil = DateTime.Now.AddSeconds(ResendCooldownSeconds);
        db.SaveChanges();
        return token;
    }

    public string? RequestStaffPasswordReset(string email)
    {
        var staff = GetStaffByEmail(email);
        if (staff is null) return null;
        if (staff.ResetCooldownUntil is { } cooldown && cooldown > DateTime.Now) return null;

        var token = GenerateToken();
        staff.ResetTokenHash = HashToken(token);
        staff.ResetTokenExpiresAt = DateTime.Now.AddMinutes(ResetTokenMinutes);
        staff.ResetCooldownUntil = DateTime.Now.AddSeconds(ResendCooldownSeconds);
        db.SaveChanges();
        return token;
    }

    // Checks Customers then Staff — the token itself (256 bits, effectively
    // collision-free) disambiguates which account it belongs to, so callers never
    // need to know the account type up front.
    public (bool ok, bool isStaff, string? error) ResetPasswordByToken(string token, string newPassword)
    {
        if (ValidatePassword(newPassword) is { } pwError) return (false, false, pwError);
        var hash = HashToken(token);

        var customer = db.Customers.FirstOrDefault(c =>
            c.ResetTokenHash == hash && c.ResetTokenExpiresAt > DateTime.Now);
        if (customer is not null)
        {
            customer.PasswordHash = HashPassword(newPassword);
            customer.ResetTokenHash = null;
            customer.ResetTokenExpiresAt = null;
            db.SaveChanges();
            return (true, false, null);
        }

        var staff = db.Staff.FirstOrDefault(s =>
            s.ResetTokenHash == hash && s.ResetTokenExpiresAt > DateTime.Now);
        if (staff is not null)
        {
            staff.PasswordHash = HashPassword(newPassword);
            staff.ResetTokenHash = null;
            staff.ResetTokenExpiresAt = null;
            db.SaveChanges();
            return (true, true, null);
        }

        return (false, false, "This link is invalid or has expired.");
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
        // NormalizeEmail rather than a bare Trim so the edit path stores the same
        // shape as the add path (Admin.razor's AddStaff already normalizes) and the
        // uniqueness check below can't be defeated by a difference in casing.
        staff.Email = NormalizeEmail(staff.Email);
        if (string.IsNullOrEmpty(staff.Email))
            return "Email is required.";

        var normEmail = staff.Email;
#pragma warning disable CA1304, CA1311, CA1862 // SQL-translated by EF Core; see AuthenticateStaff.
        if (db.Staff.Any(s => s.Email.ToLower() == normEmail && s.Id != staff.Id))
#pragma warning restore CA1304, CA1311, CA1862
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
