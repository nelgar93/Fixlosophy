using StackExchange.Redis;

namespace Fixlosophy.Services;

// Fully synchronous, matching AuthService's existing entirely-synchronous style.
// IDatabase's sync methods complete via the multiplexer's own I/O-completion
// signaling rather than blocking on a captured SynchronizationContext, so there's
// no deadlock risk here — the same reasoning that already lets this codebase call
// EF Core's synchronous FirstOrDefault/SaveChanges safely.
public class RedisVerificationTokenStore(IConnectionMultiplexer redis) : IVerificationTokenStore
{
    private IDatabase Db => redis.GetDatabase();

    public void SetToken(string key, string tokenHash, TimeSpan ttl) =>
        Db.StringSet(key, tokenHash, ttl);

    public bool TrySetTokenIfAbsent(string key, string tokenHash, TimeSpan ttl) =>
        Db.StringSet(key, tokenHash, ttl, When.NotExists);

    public string? GetTokenHash(string key) => Db.StringGet(key);

    public void RemoveToken(string key) => Db.KeyDelete(key);
}
