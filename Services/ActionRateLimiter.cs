using System.Threading.RateLimiting;

namespace Fixlosophy.Services;

public enum LimitedAction
{
    Login,
    Register,
    CreateBooking,
    ContactMessage
}

// Registered as scoped, which in Blazor Server means one instance per circuit
// (browser session). UI events travel over the SignalR circuit and never pass
// through the HTTP rate-limiting middleware, so sensitive actions are
// throttled here instead.
public sealed class ActionRateLimiter : IDisposable
{
    private readonly Dictionary<LimitedAction, RateLimiter> _limiters = new()
    {
        [LimitedAction.Login] = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5,
            Window = TimeSpan.FromMinutes(1),
            QueueLimit = 0
        }),
        [LimitedAction.Register] = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
        {
            PermitLimit = 3,
            Window = TimeSpan.FromMinutes(10),
            QueueLimit = 0
        }),
        [LimitedAction.CreateBooking] = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
        {
            PermitLimit = 3,
            Window = TimeSpan.FromMinutes(10),
            QueueLimit = 0
        }),
        [LimitedAction.ContactMessage] = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
        {
            PermitLimit = 3,
            Window = TimeSpan.FromMinutes(10),
            QueueLimit = 0
        })
    };

    public bool TryAcquire(LimitedAction action)
    {
        using var lease = _limiters[action].AttemptAcquire();
        return lease.IsAcquired;
    }

    public void Dispose()
    {
        foreach (var limiter in _limiters.Values)
            limiter.Dispose();
    }
}
