using Fixlosophy.Data;
using Fixlosophy.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fixlosophy.Tests;

// The error log has to hold up in exactly the conditions where everything else is
// already failing, so these lean on the two properties that matter most: repeats
// collapse into one row, and nothing here can make an outage worse.
public class ErrorLogTests
{
    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static ErrorLogRecord NewRecord(
        string template = "Could not send email {Subject} to {Recipient}",
        string message = "Could not send email 'Booking confirmed' to jane@example.com",
        string level = "Error",
        string logger = "Fixlosophy.Services.SmtpEmailSender",
        string? exceptionType = "System.Net.Sockets.SocketException",
        string? stackTrace = "   at MailKit.Net.Smtp.SmtpClient.ConnectAsync()\n   at Fixlosophy.Services.SmtpEmailSender.SendAsync()",
        DateTime? at = null) =>
        new(level, logger, template, message, exceptionType,
            exceptionType is null ? null : "Connection refused", stackTrace,
            at ?? ShopClock.Now);

    // ── Fingerprinting: what counts as "the same problem" ────────────────────

    // The reason the template is fingerprinted and the rendered message isn't. One
    // dead SMTP host is one problem, however many different customers it affected.
    [Fact]
    public void Fingerprint_IsTheSame_ForDifferentRenderedValues()
    {
        var a = NewRecord(message: "Could not send email 'X' to jane@example.com");
        var b = NewRecord(message: "Could not send email 'Y' to bob@example.com");

        Assert.Equal(a.ComputeFingerprint(), b.ComputeFingerprint());
    }

    [Fact]
    public void Fingerprint_IsTheSame_AtDifferentTimes()
    {
        var a = NewRecord(at: new DateTime(2026, 9, 1, 9, 0, 0));
        var b = NewRecord(at: new DateTime(2026, 9, 5, 17, 30, 0));

        Assert.Equal(a.ComputeFingerprint(), b.ComputeFingerprint());
    }

    [Fact]
    public void Fingerprint_DiffersByMessageTemplate() =>
        Assert.NotEqual(
            NewRecord(template: "Could not send email {Subject}").ComputeFingerprint(),
            NewRecord(template: "Could not upload photo {BookingId}").ComputeFingerprint());

    [Fact]
    public void Fingerprint_DiffersByExceptionType() =>
        Assert.NotEqual(
            NewRecord(exceptionType: "System.Net.Sockets.SocketException").ComputeFingerprint(),
            NewRecord(exceptionType: "System.TimeoutException").ComputeFingerprint());

    [Fact]
    public void Fingerprint_DiffersByLogger() =>
        Assert.NotEqual(
            NewRecord(logger: "Fixlosophy.Services.SmtpEmailSender").ComputeFingerprint(),
            NewRecord(logger: "Fixlosophy.Services.StorageService").ComputeFingerprint());

    // Only the top frame counts, so the same failing call reached from a booking and
    // from a registration groups together rather than splitting in two.
    [Fact]
    public void Fingerprint_IgnoresEverythingBelowTheTopStackFrame()
    {
        var fromBooking = NewRecord(stackTrace: "   at Smtp.ConnectAsync()\n   at Book.SubmitAsync()");
        var fromSignup  = NewRecord(stackTrace: "   at Smtp.ConnectAsync()\n   at Auth.RegisterAsync()");

        Assert.Equal(fromBooking.ComputeFingerprint(), fromSignup.ComputeFingerprint());
    }

    [Fact]
    public void Fingerprint_HandlesAnErrorWithNoException() =>
        Assert.False(string.IsNullOrEmpty(
            NewRecord(exceptionType: null, stackTrace: null).ComputeFingerprint()));

    // ── The buffer ───────────────────────────────────────────────────────────

    // Dropping the oldest is the deliberate choice: blocking would put the outage into
    // the request thread, and unbounded growth would add a memory problem to it.
    [Fact]
    public void Buffer_DropsOldestAndNeverRefuses_WhenOverwhelmed()
    {
        var buffer = new ErrorLogBuffer();

        for (var i = 0; i < ErrorLogBuffer.Capacity * 2; i++)
            Assert.True(buffer.TryEnqueue(NewRecord(message: $"failure {i}")));

        var drained = 0;
        while (buffer.Reader.TryRead(out _)) drained++;
        Assert.Equal(ErrorLogBuffer.Capacity, drained);
    }

    // ── The logger ───────────────────────────────────────────────────────────

    [Theory]
    [InlineData(LogLevel.Trace, false)]
    [InlineData(LogLevel.Debug, false)]
    [InlineData(LogLevel.Information, false)]
    [InlineData(LogLevel.Warning, false)]   // routine here; capturing them buries the signal
    [InlineData(LogLevel.Error, true)]
    [InlineData(LogLevel.Critical, true)]
    public void Logger_CapturesOnlyErrorAndAbove(LogLevel level, bool expected)
    {
        var buffer = new ErrorLogBuffer();
        var logger = new DatabaseLoggerProvider(buffer).CreateLogger("Fixlosophy.Services.BookingService");

        logger.Log(level, new EventId(1), "state", null, (s, _) => s);

        Assert.Equal(expected, buffer.Reader.TryRead(out _));
    }

    // The recursion guard. Without it, a database outage is self-amplifying: the write
    // fails, EF logs it, that log becomes another write, which fails...
    [Fact]
    public void Logger_CapturesNothingWhileTheWriterIsWorking()
    {
        var buffer = new ErrorLogBuffer();
        var logger = new DatabaseLoggerProvider(buffer).CreateLogger("Microsoft.EntityFrameworkCore.Database.Command");

        ErrorLogSuppression.Active = true;
        try
        {
            logger.LogError("Failed executing DbCommand");
        }
        finally
        {
            ErrorLogSuppression.Active = false;
        }

        Assert.False(buffer.Reader.TryRead(out _));
    }

    [Fact]
    public void Logger_IgnoresItsOwnComponentsDiagnostics()
    {
        var buffer = new ErrorLogBuffer();
        var logger = new DatabaseLoggerProvider(buffer).CreateLogger("Fixlosophy.Services.ErrorLogWriter");

        logger.LogError("Could not persist error records");

        Assert.False(buffer.Reader.TryRead(out _));
    }

    // The whole reason the template is pulled out of the state rather than the
    // formatted string being used for grouping.
    [Fact]
    public void Logger_KeepsTheTemplateAndTheRenderedMessageApart()
    {
        var buffer = new ErrorLogBuffer();
        var logger = new DatabaseLoggerProvider(buffer).CreateLogger("Fixlosophy.Services.SmtpEmailSender");

        logger.LogError("Could not send email {Subject} to {Recipient}", "Booking confirmed", "jane@example.com");

        Assert.True(buffer.Reader.TryRead(out var record));
        Assert.Equal("Could not send email {Subject} to {Recipient}", record!.MessageTemplate);
        Assert.Contains("jane@example.com", record.Message, StringComparison.Ordinal);
    }

    // ── The writer ───────────────────────────────────────────────────────────

    private static ErrorLogWriter NewWriter(AppDbContext db) =>
        new(new SingleContextScopeFactory(db), new ErrorLogBuffer());

    [Fact]
    public async Task Writer_CollapsesABurstIntoOneRow()
    {
        using var db = NewDb();
        var batch = Enumerable.Range(0, 50)
            .Select(i => NewRecord(message: $"Could not send email to customer{i}@example.com"))
            .ToList();

        await NewWriter(db).WriteBatchAsync(batch, CancellationToken.None);

        var row = db.ErrorLog.Single();
        Assert.Equal(50, row.Count);
        Assert.Equal("Fixlosophy.Services.SmtpEmailSender", row.Logger);
    }

    [Fact]
    public async Task Writer_KeepsDistinctProblemsApart()
    {
        using var db = NewDb();
        var batch = new List<ErrorLogRecord>
        {
            NewRecord(template: "Could not send email {Subject}"),
            NewRecord(template: "Could not upload photo {BookingId}", logger: "Fixlosophy.Services.StorageService"),
        };

        await NewWriter(db).WriteBatchAsync(batch, CancellationToken.None);

        Assert.Equal(2, db.ErrorLog.Count());
    }

    [Fact]
    public async Task Writer_AccumulatesOntoAnExistingGroupAcrossBatches()
    {
        using var db = NewDb();
        var writer = NewWriter(db);
        var first  = new DateTime(2026, 9, 1, 9, 0, 0);
        var second = new DateTime(2026, 9, 5, 17, 0, 0);

        await writer.WriteBatchAsync([NewRecord(at: first)], CancellationToken.None);
        await writer.WriteBatchAsync([NewRecord(at: second)], CancellationToken.None);

        var row = db.ErrorLog.Single();
        Assert.Equal(2, row.Count);
        Assert.Equal(first, row.FirstSeen);   // never moves
        Assert.Equal(second, row.LastSeen);   // always the most recent
    }

    // A write failure must not throw into the drain loop — it would end the loop and
    // silently stop all error capture for the life of the process.
    [Fact]
    public async Task Writer_SwallowsItsOwnFailures()
    {
        var db = NewDb();
        db.Dispose();   // any use now throws ObjectDisposedException

        var exception = await Record.ExceptionAsync(
            () => NewWriter(db).WriteBatchAsync([NewRecord()], CancellationToken.None));

        Assert.Null(exception);
        // And the suppression flag is released, or all later capture would stay dead.
        Assert.False(ErrorLogSuppression.Active);
    }

    // ── Retention ────────────────────────────────────────────────────────────

    [Fact]
    public async Task PurgeExpiredErrorsAsync_RemovesOnlyGroupsThatStopped()
    {
        using var db = NewDb();
        db.ErrorLog.Add(new ErrorLogEntry
        {
            Fingerprint = "old", LastSeen = ShopClock.Now - MaintenanceJobs.ErrorRetention.Add(TimeSpan.FromDays(1))
        });
        db.ErrorLog.Add(new ErrorLogEntry { Fingerprint = "current", LastSeen = ShopClock.Now });
        db.SaveChanges();

        var purged = await NewJobs(db).PurgeExpiredErrorsAsync();

        Assert.Equal(1, purged);
        Assert.Equal("current", db.ErrorLog.Single().Fingerprint);
    }

    private static MaintenanceJobs NewJobs(AppDbContext db) =>
        new(db,
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            new RecordingEmailSender(),
            new InflationService(new UnusedHttpClientFactory()),
            new NotificationService(db, new NotificationHub(), NullLogger<NotificationService>.Instance),
            NullLogger<MaintenanceJobs>.Instance);

    private sealed class UnusedHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new NotSupportedException();
    }

    // ErrorLogWriter takes a scope factory because it's a singleton reaching for a
    // scoped DbContext. In a test there's one context and no scoping to do.
    private sealed class SingleContextScopeFactory(AppDbContext db) : IServiceScopeFactory, IServiceScope, IServiceProvider
    {
        public IServiceScope CreateScope() => this;
        public IServiceProvider ServiceProvider => this;
        public object? GetService(Type serviceType) => serviceType == typeof(AppDbContext) ? db : null;
        public void Dispose() { }
    }
}
