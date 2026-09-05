using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Fixlosophy.Services;

/// <summary>
/// One <em>kind</em> of error, not one occurrence. Repeats collapse onto the same row
/// and bump <see cref="Count"/>.
/// </summary>
/// <remarks>
/// Grouping is the whole point. A single failing dependency writes an error every few
/// seconds; stored one row per occurrence, the table becomes unreadable in an hour and
/// unbounded in a week. Grouped, it reads "412 × NpgsqlException, first seen Tuesday
/// 09:14, last seen a minute ago" — which is the sentence you actually want.
/// </remarks>
public class ErrorLogEntry
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// Stable hash of what makes two errors "the same problem". See
    /// <see cref="ErrorLogRecord.ComputeFingerprint"/>.
    public string Fingerprint { get; set; } = "";

    /// "Error" or "Critical". Warnings are not captured — they'd swamp the signal.
    public string Level { get; set; } = "";

    /// The ILogger category, i.e. the fully-qualified type that logged it.
    public string Logger { get; set; } = "";

    /// The message template, not the rendered message: "Could not send email {Subject}
    /// to {Recipient}" rather than a version with one customer's address baked in.
    /// Keeping the template is what lets repeats group instead of fragmenting.
    public string MessageTemplate { get; set; } = "";

    /// The most recent rendered message, with values filled in. One example of the
    /// group, so there's something concrete to read.
    public string LastMessage { get; set; } = "";

    public string? ExceptionType { get; set; }
    public string? ExceptionMessage { get; set; }

    /// Truncated — see <see cref="ErrorLogRecord.MaxStackTrace"/>. Enough to find the
    /// code, not so much that the table becomes a stack-trace archive.
    public string? StackTrace { get; set; }

    public DateTime FirstSeen { get; set; } = ShopClock.Now;
    public DateTime LastSeen { get; set; } = ShopClock.Now;
    public int Count { get; set; } = 1;
}

/// <summary>
/// An error on its way to the table: captured in the logger, carried through the
/// queue, folded into an <see cref="ErrorLogEntry"/> by the writer.
/// </summary>
/// <remarks>
/// A separate type from the entity on purpose. This one is created on the request's
/// thread while a customer is waiting, so it holds only plain strings already
/// materialised — no DbContext, no lazy formatting, nothing that could throw later on
/// a background thread where there is no caller left to tell.
/// </remarks>
public sealed record ErrorLogRecord(
    string Level,
    string Logger,
    string MessageTemplate,
    string Message,
    string? ExceptionType,
    string? ExceptionMessage,
    string? StackTrace,
    DateTime OccurredAt)
{
    public const int MaxMessage = 4000;
    public const int MaxStackTrace = 8000;

    /// <summary>
    /// What makes two errors the same problem: where it came from, what it was trying
    /// to say, and what went wrong — deliberately <em>not</em> the rendered message or
    /// the timestamp, which differ on every occurrence.
    /// </summary>
    /// <remarks>
    /// Only the first stack frame is included. Deeper frames vary with the call path
    /// (the same failing SMTP call reached from a booking and from a registration is
    /// one problem, not two), while the top frame is where it actually broke.
    /// SHA-256 truncated to 16 bytes: this is a grouping key, not a security boundary,
    /// and 128 bits is far past any realistic collision.
    /// </remarks>
    public string ComputeFingerprint()
    {
        var firstFrame = StackTrace is null
            ? ""
            : StackTrace.Split('\n', 2)[0].Trim();

        var material = string.Join('',
            Level, Logger, MessageTemplate, ExceptionType ?? "", firstFrame);

        return Convert.ToHexStringLower(
            SHA256.HashData(Encoding.UTF8.GetBytes(material)).AsSpan(0, 16));
    }

    public static string Truncate(string? value, int max) =>
        string.IsNullOrEmpty(value) ? "" :
        value.Length <= max ? value : value[..max];

    /// Formats a timestamp for the rare case one needs to appear inside a message.
    public static string Stamp(DateTime at) =>
        at.ToString("yyyy-MM-dd HH:mm:ss", CultureInfo.InvariantCulture);
}
