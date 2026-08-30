namespace Fixlosophy.Services;

/// <summary>
/// In-process fan-out so an open admin dashboard learns about a new notification
/// without polling.
///
/// A plain event over a singleton is the right size here: the shop runs one instance
/// on one VPS, and every subscriber is a Blazor circuit in the same process, already
/// holding a SignalR connection to its browser. There is no new transport to add —
/// the circuit re-renders and Blazor pushes the diff down the wire it already has.
///
/// If this ever runs multi-instance, Redis is already a dependency (see
/// IVerificationTokenStore) and can carry the same signal as a backplane; nothing
/// outside this class would need to change.
///
/// Handlers run on the raising thread, so subscribers must not block — Admin.razor
/// marshals onto its own dispatcher with InvokeAsync and returns immediately.
/// </summary>
public sealed class NotificationHub
{
    public event Action<Notification>? Raised;

    public void Publish(Notification notification)
    {
        // Snapshot the delegate: a subscriber unsubscribing on another thread between
        // the null check and the invoke would otherwise NRE.
        var handlers = Raised;
        if (handlers is null) return;

        foreach (var handler in handlers.GetInvocationList().Cast<Action<Notification>>())
        {
            try
            {
                handler(notification);
            }
            catch
            {
                // One torn-down circuit must not stop the others being told, and this
                // is a UI convenience — never worth surfacing to whoever raised it.
            }
        }
    }
}
