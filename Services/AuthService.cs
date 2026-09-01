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
    // Public so the confirmation page can drive its countdown button from the same
    // number the server enforces, rather than a hardcoded copy that can drift.
    public const int ResendCooldownSeconds = 60;

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

    /// Shortest number the shop could actually dial. UK landlines without the area
    /// code are 6 digits, so anything under 7 is a typo rather than a number.
    public const int MinPhoneDigits = 7;

    /// E.164's ceiling. Longer than this is a mistyped run of digits, not a number.
    public const int MaxPhoneDigits = 15;

    /// Longest string we accept, digits plus the formatting people put between them.
    public const int MaxPhoneLength = 20;

    /// The rule below, in the form an <c>&lt;input pattern&gt;</c> understands, so the
    /// browser and the server can't drift apart. Kept deliberately loose on shape —
    /// it constrains the alphabet and the length; the digit count is IsValidPhone's job.
    public const string PhoneInputPattern = @"[0-9 ()+.\-]{7,20}";

    /// Characters a phone number may contain: digits, and the punctuation people
    /// actually write between them.
    private static bool IsPhoneChar(char c) =>
        char.IsDigit(c) || c is ' ' or '(' or ')' or '-' or '.' or '+';

    /// Accepts a loosely-formatted but dialable number — customers write spaces,
    /// dashes, brackets and +44 prefixes, and rejecting those would be worse than
    /// accepting them. What it will not accept is free text: this used to count
    /// digits and nothing else, so "call me on 07700 900000" was stored and reached
    /// the admin panel as a dead Call/WhatsApp link.
    public static bool IsValidPhone(string? phone)
    {
        if (string.IsNullOrWhiteSpace(phone)) return false;

        var trimmed = phone.Trim();
        if (trimmed.Length > MaxPhoneLength) return false;
        if (!trimmed.All(IsPhoneChar)) return false;

        // '+' is a country-code prefix, so it only means anything at the front.
        if (trimmed.IndexOf('+') > 0) return false;

        var digits = trimmed.Count(char.IsDigit);
        return digits >= MinPhoneDigits && digits <= MaxPhoneDigits;
    }

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
        if (!VerifyPassword(customer.PasswordHash, password)) return null;

        // Knowing the password proves ownership, so it's safe to adopt any guest
        // bookings under this address here. Covers accounts grandfathered as
        // EmailConfirmed by the column default, which never pass through ConfirmEmail.
        // Idempotent, so running on every sign-in costs one indexed query.
        LinkGuestBookings(customer);
        return customer;
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
        if (!IsValidPhone(phone))
            return (null, "Please enter a phone number we can reach you on.");
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
        // Required here too, so a phone number can't be cleared from the profile
        // page after registration made it mandatory.
        if (!IsValidPhone(phone))
            return (null, "Please enter a phone number we can reach you on.");

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

    // ── Guest booking adoption ───────────────────────────────────────────────

    /// <summary>
    /// Attaches any unclaimed bookings made under this customer's email address to
    /// their account, so a booking placed as a guest shows up once they register.
    /// Returns how many were adopted.
    ///
    /// SECURITY: an email address on its own must never be enough to claim someone's
    /// booking history — it isn't a secret. This is therefore only ever called from
    /// two places, both of which have already proved control of the mailbox or the
    /// password: <see cref="ConfirmEmail"/> (they clicked the emailed link) and the
    /// success path of <see cref="AuthenticateCustomer"/> (they knew the password).
    /// Do not call it from registration.
    ///
    /// Only rows with a null CustomerId are touched, so a booking already belonging
    /// to someone else can never be reassigned.
    /// </summary>
    public int LinkGuestBookings(Customer customer)
    {
        var normEmail = NormalizeEmail(customer.Email);
#pragma warning disable CA1304, CA1311, CA1862 // SQL-translated by EF Core; see AuthenticateStaff.
        var orphans = db.Bookings
            .Where(b => b.CustomerId == null && b.CustomerEmail.ToLower() == normEmail)
            .ToList();
#pragma warning restore CA1304, CA1311, CA1862

        if (orphans.Count == 0) return 0;

        foreach (var booking in orphans)
            booking.CustomerId = customer.Id;

        // Notes written against those bookings follow the customer in. A note added
        // while they were still a guest would otherwise stay orphaned and never
        // appear on their record.
        var bookingIds = orphans.Select(b => b.Id).ToList();
        foreach (var note in db.CustomerNotes.Where(n => n.CustomerId == null && bookingIds.Contains(n.BookingId!)))
            note.CustomerId = customer.Id;

        db.SaveChanges();
        return orphans.Count;
    }

    // ── Account deletion (UK GDPR right to erasure) ──────────────────────────

    /// <summary>
    /// Deletes a customer's account and anonymises their bookings.
    ///
    /// The bookings themselves are kept — they're the record of work carried out on a
    /// bike, which we have a legitimate interest in retaining — but every field that
    /// identifies a person is overwritten first. The FK is ON DELETE SET NULL, so
    /// deleting the customer row alone would detach the bookings while leaving the
    /// name, email and phone sitting on them in plain text; that isn't erasure.
    ///
    /// Saved bikes cascade automatically (ON DELETE CASCADE), as do booking photos
    /// when their booking is removed.
    /// </summary>
    public bool DeleteCustomerAccount(string customerId)
    {
        var customer = db.Customers.Find(customerId);
        if (customer is null) return false;

        foreach (var booking in db.Bookings.Where(b => b.CustomerId == customerId).ToList())
        {
            booking.CustomerName  = "Deleted customer";
            booking.CustomerEmail = "";
            booking.CustomerPhone = "";
            booking.Notes         = "";
            booking.CustomerId    = null;
        }

        db.Customers.Remove(customer);
        db.SaveChanges();
        return true;
    }

    /// <summary>
    /// Everything held about one customer, for the UK GDPR right of access. Deliberately
    /// excludes the password hash and reset-token columns: they're about them but
    /// handing them back is a security risk with no benefit to the person asking.
    /// </summary>
    public CustomerDataExport? ExportCustomerData(string customerId)
    {
        var customer = db.Customers
            .Include(c => c.Bikes)
            .AsNoTracking()
            .FirstOrDefault(c => c.Id == customerId);
        if (customer is null) return null;

        var bookings = db.Bookings
            .Where(b => b.CustomerId == customerId)
            .Include(b => b.Photos)
            .AsNoTracking()
            .OrderByDescending(b => b.SlotDate)
            .ToList();

        // Only notes staff explicitly marked as shareable. Internal ones stay internal
        // — that flag exists so staff can write frankly, and an access request must
        // not quietly undo it.
        var sharedNotes = db.CustomerNotes
            .Where(n => n.CustomerId == customerId && n.VisibleToCustomer && n.BookingId != null)
            .AsNoTracking()
            .OrderBy(n => n.CreatedAt)
            .ToList()
            .GroupBy(n => n.BookingId!)
            .ToDictionary(g => g.Key, g => g.Select(n => n.Body).ToArray());

        return new CustomerDataExport(
            ExportedAt: ShopClock.Now,
            Account: new(customer.FullName, customer.Email, customer.Phone,
                         customer.CreatedAt, customer.EmailConfirmed),
            Bikes: [.. customer.Bikes.Select(b => new ExportedBike(b.MakeModel, b.CreatedAt))],
            Bookings: [.. bookings.Select(b => new ExportedBooking(
                b.Reference, b.CreatedAt, b.ServiceName, b.ServiceCategory, b.ServicePrice,
                b.SlotDate, b.SlotTime, b.BikeDescription, b.Notes, b.Status.ToString(),
                b.Photos.Count,
                sharedNotes.GetValueOrDefault(b.Id, [])))]);
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

        // Clicking the emailed link proves they control the mailbox, so any guest
        // bookings under this address are theirs. Done here rather than at
        // registration so the account is already populated at first sign-in.
        LinkGuestBookings(customer);

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
        if (customer.ResetCooldownUntil is { } cooldown && cooldown > ShopClock.Now) return null;

        var token = GenerateToken();
        customer.ResetTokenHash = HashToken(token);
        customer.ResetTokenExpiresAt = ShopClock.Now.AddMinutes(ResetTokenMinutes);
        customer.ResetCooldownUntil = ShopClock.Now.AddSeconds(ResendCooldownSeconds);
        db.SaveChanges();
        return token;
    }

    public string? RequestStaffPasswordReset(string email)
    {
        var staff = GetStaffByEmail(email);
        if (staff is null) return null;
        if (staff.ResetCooldownUntil is { } cooldown && cooldown > ShopClock.Now) return null;

        var token = GenerateToken();
        staff.ResetTokenHash = HashToken(token);
        staff.ResetTokenExpiresAt = ShopClock.Now.AddMinutes(ResetTokenMinutes);
        staff.ResetCooldownUntil = ShopClock.Now.AddSeconds(ResendCooldownSeconds);
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
            c.ResetTokenHash == hash && c.ResetTokenExpiresAt > ShopClock.Now);
        if (customer is not null)
        {
            customer.PasswordHash = HashPassword(newPassword);
            customer.ResetTokenHash = null;
            customer.ResetTokenExpiresAt = null;
            db.SaveChanges();
            return (true, false, null);
        }

        var staff = db.Staff.FirstOrDefault(s =>
            s.ResetTokenHash == hash && s.ResetTokenExpiresAt > ShopClock.Now);
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
