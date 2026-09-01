using Microsoft.EntityFrameworkCore;
using Fixlosophy.Data;

namespace Fixlosophy.Services;

/// <summary>
/// Bulk customer import from a CSV export of a previous system.
///
/// Split into two phases on purpose. <see cref="Preview"/> reads and classifies without
/// touching the database, so an admin sees exactly what a messy export will do before
/// it does it; <see cref="Commit"/> then writes only what the preview described. An
/// import that writes as it parses leaves half a migration behind when row 400 turns
/// out to be malformed.
/// </summary>
public class CustomerImportService(
    AppDbContext db,
    AuthService auth,
    ILogger<CustomerImportService> logger)
{
    /// A migration file, not a database. Past this it is a scripted job, not a form.
    public const int MaxRows = 5000;
    public const long MaxFileBytes = 2 * 1024 * 1024;

    /// Claim links go out in one batch and are read whenever the customer gets round to
    /// it, so the usual 60-minute reset window is far too short.
    public const int ClaimLinkValidMinutes = 60 * 24 * 7;

    // Header aliases, because every system names these differently. Matched
    // case-insensitively after trimming.
    private static readonly string[] EmailHeaders = ["email", "email address", "e-mail"];
    private static readonly string[] NameHeaders  = ["name", "full name", "fullname", "customer name", "customer"];
    private static readonly string[] PhoneHeaders = ["phone", "phone number", "mobile", "telephone", "tel"];

    public static string TemplateCsv =>
        "email,name,phone\n" +
        "jane@example.com,Jane Smith,07700 900000\n";

    /// <summary>Classifies every row without writing anything.</summary>
    public ImportPreview Preview(string csv)
    {
        var rows = CsvReader.Parse(csv);
        if (rows.Count == 0)
            return new ImportPreview([], [], "That file is empty.");

        var header = rows[0];
        var emailCol = IndexOf(header, EmailHeaders);
        if (emailCol < 0)
            return new ImportPreview([], [],
                "No 'email' column found. The first row must be a header — download the template to see the expected columns.");

        var nameCol  = IndexOf(header, NameHeaders);
        var phoneCol = IndexOf(header, PhoneHeaders);

        var known = new[] { emailCol, nameCol, phoneCol };
        var unknown = header
            .Select((h, i) => (h, i))
            .Where(x => !known.Contains(x.i) && !string.IsNullOrWhiteSpace(x.h))
            .Select(x => x.h.Trim())
            .ToList();

        var body = rows.Skip(1).ToList();
        if (body.Count > MaxRows)
            return new ImportPreview([], unknown,
                $"That file has {body.Count:N0} rows. The importer takes up to {MaxRows:N0} at a time.");

        // One query for the whole file rather than one per row. The unique index is on
        // lower(Email) and NormalizeEmail lower-cases, so this dictionary matches it.
        var wanted = body
            .Select(r => AuthService.NormalizeEmail(Field(r, emailCol)))
            .Where(e => e.Length > 0)
            .Distinct()
            .ToList();

#pragma warning disable CA1304, CA1311, CA1862 // SQL-translated by EF Core; see AuthenticateStaff.
        var existing = db.Customers
            .Where(c => wanted.Contains(c.Email.ToLower()))
            .AsNoTracking()
            .ToDictionary(c => c.Email.ToLowerInvariant());
#pragma warning restore CA1304, CA1311, CA1862

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var result = new List<ImportRow>(body.Count);

        for (var i = 0; i < body.Count; i++)
        {
            var line = i + 2;               // +1 for the header, +1 for 1-based counting
            var raw = body[i];

            if (raw.All(string.IsNullOrWhiteSpace)) continue;   // blank line, not a row

            var email = Field(raw, emailCol);
            var name  = Field(raw, nameCol);
            var phone = Field(raw, phoneCol);

            if (!AuthService.IsValidEmail(email))
            {
                result.Add(new ImportRow(line, email, name, phone, ImportOutcome.Invalid,
                    string.IsNullOrWhiteSpace(email) ? "No email address." : "That email address isn't usable."));
                continue;
            }

            // Phone is optional here — unlike registration, where the shop needs a way
            // to reach someone about a booking they just made. But a value that IS
            // present has to be dialable, or it reaches the dashboard as a dead link.
            if (!string.IsNullOrWhiteSpace(phone) && !AuthService.IsValidPhone(phone))
            {
                result.Add(new ImportRow(line, email, name, phone, ImportOutcome.Invalid,
                    "That phone number isn't usable."));
                continue;
            }

            var norm = AuthService.NormalizeEmail(email);

            if (!seen.Add(norm))
            {
                result.Add(new ImportRow(line, norm, name, phone, ImportOutcome.DuplicateInFile,
                    "Already appears earlier in this file."));
                continue;
            }

            if (!existing.TryGetValue(norm, out var current))
            {
                result.Add(new ImportRow(line, norm, name.Trim(), phone.Trim(), ImportOutcome.Create));
                continue;
            }

            // Fill blanks only: the live record is more current than an old export, so
            // it wins wherever it has an answer.
            var fills = new List<string>();
            if (string.IsNullOrWhiteSpace(current.FullName) && !string.IsNullOrWhiteSpace(name)) fills.Add("name");
            if (string.IsNullOrWhiteSpace(current.Phone) && !string.IsNullOrWhiteSpace(phone)) fills.Add("phone");

            result.Add(new ImportRow(line, norm, name.Trim(), phone.Trim(),
                fills.Count > 0 ? ImportOutcome.Update : ImportOutcome.Unchanged,
                Fills: fills));
        }

        return new ImportPreview(result, unknown);
    }

    /// <summary>
    /// Writes the rows the preview classified as Create or Update, adopts guest
    /// bookings for the created ones, and returns what happened.
    /// </summary>
    public ImportResult Commit(ImportPreview preview)
    {
        var created = new List<Customer>();
        var updated = 0;

        foreach (var row in preview.Actionable)
        {
            if (row.Outcome == ImportOutcome.Create)
            {
                created.Add(new Customer
                {
                    Email = row.Email,
                    FullName = row.FullName,
                    Phone = row.Phone,
                    // No password: they set one through the claim link. An empty hash
                    // fails VerifyPassword, so the account cannot be signed into until
                    // they do.
                    PasswordHash = "",
                    // Set explicitly. The column is `NOT NULL DEFAULT true` so that
                    // accounts predating email verification stayed usable — anything
                    // relying on that default would arrive already confirmed.
                    EmailConfirmed = false,
                    CreatedAt = ShopClock.Now
                });
                continue;
            }

#pragma warning disable CA1304, CA1311, CA1862 // SQL-translated by EF Core; see AuthenticateStaff.
            var existing = db.Customers.FirstOrDefault(c => c.Email.ToLower() == row.Email);
#pragma warning restore CA1304, CA1311, CA1862
            if (existing is null) continue;    // deleted between preview and commit

            if (row.Fills.Contains("name")) existing.FullName = row.FullName;
            if (row.Fills.Contains("phone")) existing.Phone = row.Phone;
            updated++;
        }

        db.Customers.AddRange(created);

        try
        {
            db.SaveChanges();
        }
        catch (DbUpdateException ex)
        {
            // Lost a race against IX_Customers_Email — somebody registered with one of
            // these addresses between the preview and this click. Detach so the
            // circuit-scoped context stays usable, as CreateBooking does.
            logger.LogWarning(ex, "Customer import failed to save; the file may overlap a new registration.");
            foreach (var entry in db.ChangeTracker.Entries().ToList())
                entry.State = EntityState.Detached;

            return new ImportResult(0, 0, 0, 0, [],
                "Something in this file clashed with an account created since the preview. " +
                "Nothing was imported — re-upload the file to see an up-to-date preview.");
        }

        // Only now that they have ids. Safe here in a way it is not at registration:
        // this is staff importing the shop's own records, not a stranger typing an
        // address they don't own into a signup form.
        var adopted = 0;
        foreach (var customer in created)
            adopted += auth.LinkGuestBookings(customer);

        return new ImportResult(created.Count, updated, adopted, 0, []);
    }

    /// <summary>
    /// Mints a claim link for one newly-imported customer. Returns null when the token
    /// couldn't be issued (no such account, or one was issued moments ago).
    /// </summary>
    public string? CreateClaimToken(string email) =>
        auth.RequestCustomerPasswordReset(email, ClaimLinkValidMinutes);

    /// <summary>
    /// The absolute claim URL. Mirrors Program.cs's BuildAbsoluteUrl, which can't be
    /// reused here: it takes an HttpContext, and a Blazor circuit has none. Prefers a
    /// configured App:BaseUrl for the same reason it does — behind a reverse proxy the
    /// request's own scheme and host are the proxy's, not the site's.
    /// </summary>
    public static string BuildClaimLink(string? configuredBaseUrl, string circuitBaseUri, string token)
    {
        var root = (string.IsNullOrWhiteSpace(configuredBaseUrl) ? circuitBaseUri : configuredBaseUrl)
            .TrimEnd('/');
        return $"{root}/reset-password?token={Uri.EscapeDataString(token)}";
    }

    private static int IndexOf(string[] header, string[] names)
    {
        for (var i = 0; i < header.Length; i++)
            if (names.Contains(header[i].Trim(), StringComparer.OrdinalIgnoreCase))
                return i;
        return -1;
    }

    private static string Field(string[] row, int index) =>
        index >= 0 && index < row.Length ? row[index] : "";
}
