using Fixlosophy.Data;
using Fixlosophy.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fixlosophy.Tests;

// Bulk import of customers from a previous system's CSV export. The rules that matter:
// email is the dedupe key (matching the DB's lower(email) unique index), an existing
// record is never overwritten, and nothing is written until the preview has been seen.
public class CustomerImportTests
{
    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static AuthService NewAuth(AppDbContext db) =>
        new(db);

    private static CustomerImportService NewService(AppDbContext db) =>
        new(db, NewAuth(db), NullLogger<CustomerImportService>.Instance);

    private static Customer SeedCustomer(
        AppDbContext db, string email, string name = "Existing Person", string phone = "07700 900111")
    {
        var c = new Customer { Email = email.ToLowerInvariant(), FullName = name, Phone = phone };
        db.Customers.Add(c);
        db.SaveChanges();
        return c;
    }

    // ── The parser ───────────────────────────────────────────────────────────

    [Fact]
    public void Parse_HandlesQuotedFieldsWithCommasAndNewlines()
    {
        var rows = CsvReader.Parse("a,b\n\"one, two\",\"line\nbreak\"");

        Assert.Equal(2, rows.Count);
        Assert.Equal(["one, two", "line\nbreak"], rows[1]);
    }

    [Fact]
    public void Parse_TreatsDoubledQuotesAsALiteralQuote()
    {
        var rows = CsvReader.Parse("name\n\"She said \"\"hi\"\"\"");

        Assert.Equal("She said \"hi\"", rows[1][0]);
    }

    [Fact]
    public void Parse_StripsTheBomExcelWrites()
    {
        var rows = CsvReader.Parse("﻿email,name\njane@example.com,Jane");

        Assert.Equal("email", rows[0][0]);
    }

    [Theory]
    [InlineData("a,b\r\nc,d")]   // CRLF
    [InlineData("a,b\nc,d")]     // LF
    [InlineData("a,b\rc,d")]     // bare CR
    public void Parse_AcceptsEveryLineEnding(string csv)
    {
        var rows = CsvReader.Parse(csv);

        Assert.Equal(2, rows.Count);
        Assert.Equal(["c", "d"], rows[1]);
    }

    [Fact]
    public void Parse_KeepsAFinalRowWithNoTrailingNewline_AndDropsBlankOnes()
    {
        var rows = CsvReader.Parse("a,b\nc,d\n\n\n");

        Assert.Equal(2, rows.Count);
    }

    [Fact]
    public void Parse_TrimsUnquotedFieldsButNotQuotedOnes()
    {
        var rows = CsvReader.Parse("a,b\n  spaced  ,\"  kept  \"");

        Assert.Equal("spaced", rows[1][0]);
        Assert.Equal("  kept  ", rows[1][1]);
    }

    // ── Headers ──────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("email,name,phone")]
    [InlineData("E-Mail,Full Name,Mobile")]
    [InlineData("  EMAIL  ,customer name,telephone")]
    public void Preview_MatchesHeaderAliasesCaseInsensitively(string header)
    {
        using var db = NewDb();

        var preview = NewService(db).Preview($"{header}\njane@example.com,Jane Smith,07700 900000");

        var row = Assert.Single(preview.Rows);
        Assert.Equal(ImportOutcome.Create, row.Outcome);
        Assert.Equal("Jane Smith", row.FullName);
        Assert.Equal("07700 900000", row.Phone);
    }

    [Fact]
    public void Preview_ReportsColumnsItIgnores()
    {
        using var db = NewDb();

        var preview = NewService(db).Preview("email,loyalty points\njane@example.com,42");

        Assert.Equal(["loyalty points"], preview.UnknownHeaders);
    }

    [Fact]
    public void Preview_RefusesAFileWithNoEmailColumn()
    {
        using var db = NewDb();

        var preview = NewService(db).Preview("name,phone\nJane,07700 900000");

        Assert.NotNull(preview.FatalError);
        Assert.Empty(preview.Rows);
    }

    // ── Classification ───────────────────────────────────────────────────────

    [Fact]
    public void Preview_MatchesAnExistingCustomerDespiteDifferentCasing()
    {
        using var db = NewDb();
        SeedCustomer(db, "jane@example.com");

        var preview = NewService(db).Preview("email\n  JANE@Example.COM  ");

        Assert.Equal(ImportOutcome.Unchanged, Assert.Single(preview.Rows).Outcome);
    }

    [Fact]
    public void Preview_FlagsARepeatedEmailInTheSameFile()
    {
        using var db = NewDb();

        var preview = NewService(db).Preview(
            "email\njane@example.com\nJANE@example.com");

        Assert.Equal(ImportOutcome.Create, preview.Rows[0].Outcome);
        Assert.Equal(ImportOutcome.DuplicateInFile, preview.Rows[1].Outcome);
    }

    [Theory]
    [InlineData("", "No email address.")]
    [InlineData("not-an-email", null)]
    [InlineData("@example.com", null)]
    [InlineData("jane@", null)]
    [InlineData("jane doe@example.com", null)]
    public void Preview_RejectsUnusableEmails(string email, string? expectedReason)
    {
        using var db = NewDb();

        // A name alongside it, so the row is a real row with a missing email rather
        // than a blank line — those are skipped, not reported.
        var row = Assert.Single(NewService(db).Preview($"email,name\n{email},Jane").Rows);

        Assert.Equal(ImportOutcome.Invalid, row.Outcome);
        if (expectedReason is not null) Assert.Equal(expectedReason, row.Problem);
    }

    [Fact]
    public void Preview_SkipsBlankLinesRatherThanReportingThem()
    {
        using var db = NewDb();

        var preview = NewService(db).Preview("email,name\njane@example.com,Jane\n,\n\n");

        Assert.Equal(ImportOutcome.Create, Assert.Single(preview.Rows).Outcome);
    }

    [Fact]
    public void Preview_RejectsAPhoneNumberThatIsNotDialable()
    {
        using var db = NewDb();

        var row = Assert.Single(NewService(db)
            .Preview("email,phone\njane@example.com,call me on 07700 900000").Rows);

        Assert.Equal(ImportOutcome.Invalid, row.Outcome);
    }

    // Registration demands a phone because the shop needs to reach someone about a
    // booking. An imported record predates that rule and shouldn't be thrown away.
    [Fact]
    public void Preview_AcceptsARowWithNoPhoneAtAll()
    {
        using var db = NewDb();

        var row = Assert.Single(NewService(db).Preview("email,name\njane@example.com,Jane").Rows);

        Assert.Equal(ImportOutcome.Create, row.Outcome);
    }

    [Fact]
    public void Preview_ReportsTheLineNumberSoAProblemCanBeFound()
    {
        using var db = NewDb();

        var preview = NewService(db).Preview("email\njane@example.com\nbroken\n");

        Assert.Equal(3, preview.Rows.Single(r => r.Outcome == ImportOutcome.Invalid).LineNumber);
    }

    // ── Fill blanks, never overwrite ─────────────────────────────────────────

    [Fact]
    public void Preview_FillsOnlyTheFieldsThatAreBlank()
    {
        using var db = NewDb();
        SeedCustomer(db, "jane@example.com", name: "Jane Smith", phone: "");

        var row = Assert.Single(NewService(db)
            .Preview("email,name,phone\njane@example.com,WRONG NAME,07700 900222").Rows);

        Assert.Equal(ImportOutcome.Update, row.Outcome);
        Assert.Equal(["phone"], row.Fills);
    }

    [Fact]
    public void Commit_NeverOverwritesAPopulatedField()
    {
        using var db = NewDb();
        SeedCustomer(db, "jane@example.com", name: "Jane Smith", phone: "");
        var svc = NewService(db);

        svc.Commit(svc.Preview("email,name,phone\njane@example.com,WRONG NAME,07700 900222"));

        var stored = db.Customers.Single();
        Assert.Equal("Jane Smith", stored.FullName);      // untouched
        Assert.Equal("07700 900222", stored.Phone);       // filled
    }

    [Fact]
    public void Commit_LeavesAnUpToDateCustomerCompletelyAlone()
    {
        using var db = NewDb();
        SeedCustomer(db, "jane@example.com", name: "Jane Smith", phone: "07700 900111");
        var svc = NewService(db);

        var result = svc.Commit(svc.Preview("email,name,phone\njane@example.com,Other,07700 900999"));

        Assert.Equal(0, result.Created);
        Assert.Equal(0, result.Updated);
        Assert.Equal("Jane Smith", db.Customers.Single().FullName);
    }

    // ── Creating ─────────────────────────────────────────────────────────────

    // The EmailConfirmed column is `NOT NULL DEFAULT true`, to grandfather accounts
    // predating email verification. Anything relying on that default arrives confirmed,
    // so the import has to set it explicitly — as RegisterCustomer does.
    [Fact]
    public void Commit_CreatesAnUnconfirmedAccountWithNoPassword()
    {
        using var db = NewDb();
        var svc = NewService(db);

        svc.Commit(svc.Preview("email,name,phone\njane@example.com,Jane Smith,07700 900000"));

        var created = db.Customers.Single();
        Assert.Equal("jane@example.com", created.Email);
        Assert.False(created.EmailConfirmed);
        Assert.Equal("", created.PasswordHash);
    }

    // An imported account must not be signable-into until they claim it. VerifyPassword
    // is called unguarded on the login path, so an empty hash has to fail rather than throw.
    [Fact]
    public void AnImportedAccountCannotBeSignedIntoBeforeItIsClaimed()
    {
        using var db = NewDb();
        var svc = NewService(db);
        svc.Commit(svc.Preview("email,name\njane@example.com,Jane"));

        var signedIn = NewAuth(db).AuthenticateCustomer("jane@example.com", "");

        Assert.Null(signedIn);
        Assert.Null(NewAuth(db).AuthenticateCustomer("jane@example.com", "anything at all"));
    }

    [Fact]
    public void Commit_AdoptsGuestBookingsMadeUnderTheSameAddress()
    {
        using var db = NewDb();
        db.Bookings.Add(new Booking
        {
            Reference = "FIX-260101-001",
            CustomerName = "Jane Smith",
            CustomerEmail = "JANE@example.com",   // casing differs from the CSV
            ServiceName = "Full Service",
            SlotDate = ShopClock.Today.AddDays(-30),
            SlotTime = "10:00",
            CustomerId = null
        });
        db.SaveChanges();
        var svc = NewService(db);

        var result = svc.Commit(svc.Preview("email,name\njane@example.com,Jane Smith"));

        Assert.Equal(1, result.BookingsAdopted);
        Assert.Equal(db.Customers.Single().Id, db.Bookings.Single().CustomerId);
    }

    [Fact]
    public void Commit_WritesNothingForAPreviewFullOfProblems()
    {
        using var db = NewDb();
        var svc = NewService(db);

        var result = svc.Commit(svc.Preview("email\nbroken\n\nalso-broken"));

        Assert.Equal(0, result.Created);
        Assert.Empty(db.Customers);
    }

    [Fact]
    public void Preview_RefusesAFileWithTooManyRows()
    {
        using var db = NewDb();
        var rows = string.Join('\n', Enumerable.Range(0, CustomerImportService.MaxRows + 1)
            .Select(i => $"person{i}@example.com"));

        var preview = NewService(db).Preview($"email\n{rows}");

        Assert.NotNull(preview.FatalError);
        Assert.Empty(preview.Rows);
    }

    // ── The claim link ───────────────────────────────────────────────────────

    [Fact]
    public void BuildClaimLink_PrefersTheConfiguredBaseUrlOverTheCircuitsOwn()
    {
        var link = CustomerImportService.BuildClaimLink(
            "https://fixlosophy.co.uk/", "http://localhost:5127/", "abc123");

        Assert.Equal("https://fixlosophy.co.uk/reset-password?token=abc123", link);
    }

    [Fact]
    public void BuildClaimLink_FallsBackToTheCircuitWhenNoBaseUrlIsConfigured()
    {
        var link = CustomerImportService.BuildClaimLink(null, "http://localhost:5127/", "a b&c");

        Assert.StartsWith("http://localhost:5127/reset-password?token=", link, StringComparison.Ordinal);
        Assert.DoesNotContain(" ", link, StringComparison.Ordinal);
    }
}
