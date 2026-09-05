namespace Fixlosophy.Services;

/// <summary>
/// An <see cref="ILoggerProvider"/> that copies every Error and Critical log record
/// into the <c>ErrorLog</c> table.
/// </summary>
/// <remarks>
/// <para>A logging provider rather than a service anything calls. That's the point:
/// this codebase leans hard — and correctly — on "log loudly and carry on" for dropped
/// emails, failed uploads, failed notifications. Every one of those
/// <c>logger.LogError</c> calls already exists, and this captures all of them without
/// touching a line of them. It also captures what the framework logs on its own:
/// unhandled exceptions, circuit failures, EF Core errors.</para>
///
/// <para>The alternative — a <c>db.ErrorLog.Add(...)</c> in each catch block — needs
/// every existing site edited, every future one remembered, and catches nothing the
/// framework raises. This needs neither.</para>
/// </remarks>
public sealed class DatabaseLoggerProvider(ErrorLogBuffer queue) : ILoggerProvider
{
    public ILogger CreateLogger(string categoryName) => new DatabaseLogger(categoryName, queue);

    public void Dispose() { }
}

/// <summary>
/// The flag that stops error capture feeding itself.
/// </summary>
/// <remarks>
/// <para>Set by <see cref="ErrorLogWriter"/> while it is talking to the database, and
/// honoured by <see cref="DatabaseLogger"/>, which captures nothing while it is set.</para>
///
/// <para>This is the loop-breaker, and it is what makes the whole design safe under the
/// exact conditions it exists for. Without it a database outage is self-amplifying: the
/// writer's INSERT fails, EF Core logs an error about that, the error is captured and
/// queued, the writer picks it up, its INSERT fails again — an outage turning into a
/// spin.</para>
///
/// <para>Async-local rather than thread-static because the writer's work is async and
/// hops threads at every await. It lives in its own type rather than on the logger so
/// both halves can see it without either one being public.</para>
/// </remarks>
public static class ErrorLogSuppression
{
    private static readonly AsyncLocal<bool> _suppressed = new();

    public static bool Active
    {
        get => _suppressed.Value;
        set => _suppressed.Value = value;
    }
}

/// <summary>
/// The per-category logger. Its only job is to turn a log call into an
/// <see cref="ErrorLogRecord"/> and drop it in the buffer.
/// </summary>
internal sealed class DatabaseLogger(string category, ErrorLogBuffer queue) : ILogger
{
    // Only real failures. Warnings are routine here — a missing optional config value,
    // a Supabase retry — and capturing them would bury the errors that matter.
    public bool IsEnabled(LogLevel logLevel) =>
        logLevel >= LogLevel.Error && !ErrorLogSuppression.Active && !IsOwnCategory;

    // Belt and braces alongside the async-local: this component's own diagnostics can
    // never become rows, whatever thread they happen on.
    private bool IsOwnCategory =>
        category.StartsWith("Fixlosophy.Services.ErrorLog", StringComparison.Ordinal) ||
        category.StartsWith("Fixlosophy.Services.DatabaseLogger", StringComparison.Ordinal);

    public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

    public void Log<TState>(
        LogLevel logLevel, EventId eventId, TState state, Exception? exception,
        Func<TState, Exception?, string> formatter)
    {
        if (!IsEnabled(logLevel)) return;

        try
        {
            // The message template — "Could not send email {Subject} to {Recipient}" —
            // rather than the rendered text. It's the stable half, so repeats group
            // instead of every distinct recipient making its own row.
            var template = state is IReadOnlyList<KeyValuePair<string, object?>> values
                ? values.FirstOrDefault(v => v.Key == "{OriginalFormat}").Value?.ToString()
                : null;

            var record = new ErrorLogRecord(
                Level:            logLevel.ToString(),
                Logger:           category,
                MessageTemplate:  ErrorLogRecord.Truncate(template ?? formatter(state, exception), ErrorLogRecord.MaxMessage),
                Message:          ErrorLogRecord.Truncate(formatter(state, exception), ErrorLogRecord.MaxMessage),
                ExceptionType:    exception?.GetType().FullName,
                ExceptionMessage: exception is null ? null : ErrorLogRecord.Truncate(exception.Message, ErrorLogRecord.MaxMessage),
                StackTrace:       exception is null ? null : ErrorLogRecord.Truncate(exception.ToString(), ErrorLogRecord.MaxStackTrace),
                OccurredAt:       ShopClock.Now);

            queue.TryEnqueue(record);
        }
        catch
        {
            // A logger that throws takes down whatever was logging, which here would
            // mean an error-reporting bug escalating into an outage. There is also
            // nowhere to report it to — reporting is the thing that just failed.
        }
    }
}
