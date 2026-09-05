using Fixlosophy.Data;
using Microsoft.EntityFrameworkCore;

namespace Fixlosophy.Services;

/// <summary>
/// Drains <see cref="ErrorLogBuffer"/> and folds records into <c>ErrorLog</c> rows.
/// The only thing that writes to that table.
/// </summary>
/// <remarks>
/// <para>Single reader by design. Because nothing else writes these rows, the
/// read-then-update below needs no locking and no <c>ON CONFLICT</c> — which also
/// keeps it working under the InMemory provider the tests use.</para>
///
/// <para>Records are drained in batches and grouped by fingerprint before touching the
/// database, so a burst of four hundred identical errors is one row update, not four
/// hundred.</para>
/// </remarks>
public sealed class ErrorLogWriter(IServiceScopeFactory scopeFactory, ErrorLogBuffer queue)
    : BackgroundService
{
    /// How long to let records accumulate before writing, so a burst becomes one round
    /// trip. Long enough to batch usefully, short enough that an error is visible in
    /// the table while you're still looking for it.
    private static readonly TimeSpan BatchWindow = TimeSpan.FromSeconds(2);

    private const int MaxBatch = 200;

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        try
        {
            while (await queue.Reader.WaitToReadAsync(stoppingToken))
            {
                // Let a burst gather rather than writing the first record alone.
                await Task.Delay(BatchWindow, stoppingToken);

                var batch = new List<ErrorLogRecord>();
                while (batch.Count < MaxBatch && queue.Reader.TryRead(out var record))
                    batch.Add(record);

                if (batch.Count > 0)
                    await WriteBatchAsync(batch, stoppingToken);
            }
        }
        catch (OperationCanceledException)
        {
            // Shutting down.
        }
    }

    public override async Task StopAsync(CancellationToken cancellationToken)
    {
        queue.Complete();

        // Best effort at not losing the last few on the way down. The suppression flag
        // is what keeps a failure here from generating more of what we're draining.
        var remaining = new List<ErrorLogRecord>();
        while (remaining.Count < MaxBatch && queue.Reader.TryRead(out var record))
            remaining.Add(record);
        if (remaining.Count > 0)
            await WriteBatchAsync(remaining, CancellationToken.None);

        await base.StopAsync(cancellationToken);
    }

    /// <summary>
    /// Folds one batch of records into rows. Public so a test can drive the write path
    /// directly rather than starting and stopping a hosted service to reach it.
    /// </summary>
    public async Task WriteBatchAsync(List<ErrorLogRecord> batch, CancellationToken ct)
    {
        // Everything from here until the finally is invisible to DatabaseLogger. This
        // is what stops a database outage feeding itself: the INSERT fails, EF Core
        // logs that failure, and without this flag that log line becomes another
        // record to write, which fails, which logs...
        ErrorLogSuppression.Active = true;
        try
        {
            using var scope = scopeFactory.CreateScope();
            var db = scope.ServiceProvider.GetRequiredService<AppDbContext>();

            foreach (var group in batch.GroupBy(r => r.ComputeFingerprint()))
            {
                var newest = group.MaxBy(r => r.OccurredAt)!;
                var oldest = group.MinBy(r => r.OccurredAt)!;

                var existing = await db.ErrorLog
                    .FirstOrDefaultAsync(e => e.Fingerprint == group.Key, ct);

                if (existing is null)
                {
                    db.ErrorLog.Add(new ErrorLogEntry
                    {
                        Fingerprint      = group.Key,
                        Level            = newest.Level,
                        Logger           = newest.Logger,
                        MessageTemplate  = newest.MessageTemplate,
                        LastMessage      = newest.Message,
                        ExceptionType    = newest.ExceptionType,
                        ExceptionMessage = newest.ExceptionMessage,
                        StackTrace       = newest.StackTrace,
                        FirstSeen        = oldest.OccurredAt,
                        LastSeen         = newest.OccurredAt,
                        Count            = group.Count()
                    });
                }
                else
                {
                    existing.Count           += group.Count();
                    existing.LastSeen         = newest.OccurredAt;
                    existing.LastMessage      = newest.Message;
                    existing.ExceptionMessage = newest.ExceptionMessage;
                    existing.StackTrace       = newest.StackTrace;
                }
            }

            await db.SaveChangesAsync(ct);
        }
        catch (Exception ex)
        {
            // Deliberately Console, not ILogger. ILogger is what feeds this queue, so
            // reporting a write failure through it is the recursion the suppression
            // flag exists to prevent — and if the database is down, the table is not
            // where this can be recorded anyway. Console reaches journalctl on the VPS.
            Console.Error.WriteLine(
                $"[ErrorLogWriter] Could not persist {batch.Count} error record(s): {ex.Message}");
        }
        finally
        {
            ErrorLogSuppression.Active = false;
        }
    }
}
