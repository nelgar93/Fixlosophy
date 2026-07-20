using System.Collections.Concurrent;

namespace Fixlosophy.Services;

// Development/no-config fallback (mirrors ConsoleEmailSender's role) and the store
// used directly by unit tests, so dotnet test stays fast and fully offline. Must be
// registered as a singleton — a fresh dictionary per request would break the
// register-then-verify round trip and the resend debounce immediately.
public class InMemoryVerificationTokenStore : IVerificationTokenStore
{
    private readonly ConcurrentDictionary<string, (string Hash, DateTime ExpiresAt)> _entries = new();

    public void SetToken(string key, string tokenHash, TimeSpan ttl) =>
        _entries[key] = (tokenHash, DateTime.Now.Add(ttl));

    public bool TrySetTokenIfAbsent(string key, string tokenHash, TimeSpan ttl)
    {
        var expiresAt = DateTime.Now.Add(ttl);
        while (true)
        {
            if (_entries.TryGetValue(key, out var existing))
            {
                if (existing.ExpiresAt > DateTime.Now) return false; // still live — debounce
                if (_entries.TryUpdate(key, (tokenHash, expiresAt), existing)) return true;
            }
            else
            {
                if (_entries.TryAdd(key, (tokenHash, expiresAt))) return true;
            }
            // Lost the race to a concurrent writer — retry.
        }
    }

    public string? GetTokenHash(string key) =>
        _entries.TryGetValue(key, out var entry) && entry.ExpiresAt > DateTime.Now ? entry.Hash : null;

    public void RemoveToken(string key) => _entries.TryRemove(key, out _);
}
