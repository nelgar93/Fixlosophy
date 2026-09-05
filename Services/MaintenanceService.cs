namespace Fixlosophy.Services;

/// <summary>
/// The clock behind <see cref="MaintenanceJobs"/>. Wakes every half hour, opens a
/// scope, and offers each job the chance to run.
/// </summary>
/// <remarks>
/// <para>It keeps no state about what it has already done — no "last run" field, no
/// catch-up arithmetic after a restart. Every job is individually idempotent and
/// decides for itself whether it is due, so a tick is always safe and a missed window
/// heals on the next one. That is worth more than precision here: the alternative,
/// a scheduler that remembers, is a scheduler that can be wrong about what it
/// remembers.</para>
///
/// <para>A half-hour tick is well inside every window that matters — an hour-granular
/// reminder time, a daily purge, a yearly price rise — while costing three cheap
/// queries an hour when there is nothing to do.</para>
///
/// <para>Hosted services start after the schema bootstrap in Program.cs has completed,
/// so the first tick can rely on the tables existing and the price list being seeded.</para>
///
/// <para>Multi-instance note: nothing here coordinates between processes. The jobs
/// tolerate that — the reminder stamp is saved per booking, and the price increase is
/// guarded by its <c>PriceAdjustments</c> row — but the shop is deliberately deployed
/// as a single instance (see the README), and that is what keeps it simple.</para>
/// </remarks>
public class MaintenanceService(IServiceProvider services, ILogger<MaintenanceService> logger)
    : BackgroundService
{
    /// <remarks>
    /// Ten minutes is set by the most time-sensitive job, not the least: a late-arrival
    /// notification half an hour after the fact is no use to whoever is standing next
    /// to an empty stand. The other jobs are all self-gated and cost one cheap indexed
    /// query each when there's nothing due, so running them more often is close to free.
    /// </remarks>
    private static readonly TimeSpan Tick = TimeSpan.FromMinutes(10);

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // IsEnabled guard per CA1873: don't box the argument when Information-level
        // logging is switched off. Same pattern used throughout the app.
        if (logger.IsEnabled(LogLevel.Information))
            logger.LogInformation("Maintenance loop started; ticking every {Minutes} minutes.", Tick.TotalMinutes);

        using var timer = new PeriodicTimer(Tick);
        do
        {
            await RunOnceAsync(stoppingToken);
        }
        while (await SafeWaitAsync(timer, stoppingToken));

        logger.LogInformation("Maintenance loop stopped.");
    }

    /// <summary>
    /// One pass over every job. Public so a test — or a future admin "run now" button —
    /// can drive the same sequence the timer does.
    /// </summary>
    public async Task RunOnceAsync(CancellationToken ct)
    {
        using var scope = services.CreateScope();
        var jobs = scope.ServiceProvider.GetRequiredService<MaintenanceJobs>();

        // Each job is isolated: one failing must not stop the others, because they are
        // unrelated pieces of work that happen to share a timer. A transient database
        // blip during the purge should not also cost that evening's reminders.
        await RunJobAsync("annual price increase", () => jobs.ApplyAnnualPriceIncreaseAsync(ct));
        await RunJobAsync("notification purge",    () => Task.FromResult(jobs.PurgeExpiredNotifications()));
        await RunJobAsync("error log purge",       () => jobs.PurgeExpiredErrorsAsync(ct));
        await RunJobAsync("late arrivals",         () => jobs.FlagLateArrivalsAsync(ct));
        await RunJobAsync("appointment reminders", () => jobs.SendRemindersAsync(ct));
    }

    private async Task RunJobAsync(string name, Func<Task<int>> job)
    {
        try
        {
            await job();
        }
        catch (OperationCanceledException)
        {
            // Shutdown, not a fault.
            throw;
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Maintenance job \"{Job}\" failed; it will be retried on the next tick.", name);
        }
    }

    /// Returns false when the host is shutting down, so the loop ends quietly instead
    /// of surfacing the cancellation as a fault in the shutdown logs.
    private static async Task<bool> SafeWaitAsync(PeriodicTimer timer, CancellationToken stoppingToken)
    {
        try
        {
            return await timer.WaitForNextTickAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            return false;
        }
    }
}
