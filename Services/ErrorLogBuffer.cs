using System.Threading.Channels;

namespace Fixlosophy.Services;

/// <summary>
/// The buffer between "something logged an error" and "a row got written".
/// </summary>
/// <remarks>
/// <para>The whole reason this exists is that the logging call happens on a thread
/// that has a customer waiting on it. Writing to Postgres there would put database
/// latency — and database <em>failure</em> — directly into a request that was
/// otherwise succeeding. So the logger only ever hands the record over, and a single
/// background reader does the writing.</para>
///
/// <para>The channel is <b>bounded and drops the oldest record when full</b>. That is a
/// deliberate choice over the alternatives: waiting would reintroduce exactly the
/// blocking this exists to avoid, and growing without limit turns a burst of errors
/// into a memory problem on top of whatever was already wrong. When something is
/// failing thousands of times a second, the newest records describe the current state
/// and the oldest are already represented by the group's Count.</para>
/// </remarks>
public sealed class ErrorLogBuffer
{
    /// Enough to absorb a burst without being a memory concern — each record is a few
    /// hundred bytes of already-materialised strings.
    public const int Capacity = 1024;

    private readonly Channel<ErrorLogRecord> _channel =
        Channel.CreateBounded<ErrorLogRecord>(new BoundedChannelOptions(Capacity)
        {
            FullMode = BoundedChannelFullMode.DropOldest,
            SingleReader = true,
            SingleWriter = false
        });

    public ChannelReader<ErrorLogRecord> Reader => _channel.Reader;

    /// <summary>
    /// Hands a record to the writer. Never blocks, never throws, and the return value
    /// is safe to ignore — a false means the buffer was full and something was dropped,
    /// which is not a condition the caller can do anything useful about.
    /// </summary>
    public bool TryEnqueue(ErrorLogRecord record) => _channel.Writer.TryWrite(record);

    /// Lets the writer's drain loop finish on shutdown instead of waiting forever.
    public void Complete() => _channel.Writer.TryComplete();
}
