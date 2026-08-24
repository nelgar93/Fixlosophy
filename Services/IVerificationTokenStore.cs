namespace Fixlosophy.Services;

public interface IVerificationTokenStore
{
    // Unconditional set + TTL.
    void SetToken(string key, string tokenHash, TimeSpan ttl);

    // Atomic "only if no live entry" — the resend-abuse debounce. Returns false
    // (and does not overwrite) if a still-live entry already exists for this key.
    bool TrySetTokenIfAbsent(string key, string tokenHash, TimeSpan ttl);

    // The stored hash, or null if absent/expired.
    string? GetTokenHash(string key);

    // Consume/invalidate on success.
    void RemoveToken(string key);
}
