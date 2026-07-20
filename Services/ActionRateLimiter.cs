using System.Threading.RateLimiting;

namespace Fixlosophy.Services;

public enum LimitedAction
{
    CreateBooking,
    ContactMessage,
    ChangePassword
}

// Registered as scoped, which in Blazor Server means one instance per circuit
// (browser session). UI events travel over the SignalR circuit and never pass
// through the HTTP rate-limiting middleware, so in-circuit actions are throttled
// here instead.
//
// NOTE: this is best-effort. Because the limiter is per-circuit, a client can
// reset it by opening a new connection/tab, so it only slows casual abuse. The
// real backstops are elsewhere: sign-in/registration run as plain HTTP requests
// guarded by the per-IP "auth" rate-limit policy (see Program.cs), and booking
// abuse is bounded by DB constraints (max per slot, max active per email, and the
// unique-slot index). Login/Register are therefore NOT throttled here.
public sealed class ActionRateLimiter : IDisposable
{
    private readonly Dictionary<LimitedAction, RateLimiter> _limiters = new()
    {
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
        }),
        [LimitedAction.ChangePassword] = new FixedWindowRateLimiter(new FixedWindowRateLimiterOptions
        {
            PermitLimit = 5,
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
