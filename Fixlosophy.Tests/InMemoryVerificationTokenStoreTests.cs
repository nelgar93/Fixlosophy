using Fixlosophy.Services;

namespace Fixlosophy.Tests;

public class InMemoryVerificationTokenStoreTests
{
    [Fact]
    public void Get_ReturnsNullForUnknownKey() =>
        Assert.Null(new InMemoryVerificationTokenStore().GetTokenHash("missing"));

    [Fact]
    public void Set_ThenGet_RoundTrips()
    {
        var store = new InMemoryVerificationTokenStore();
        store.SetToken("k", "hash1", TimeSpan.FromMinutes(5));
        Assert.Equal("hash1", store.GetTokenHash("k"));
    }

    [Fact]
    public void Get_ReturnsNullOnceExpired()
    {
        var store = new InMemoryVerificationTokenStore();
        store.SetToken("k", "hash1", TimeSpan.FromSeconds(-1));
        Assert.Null(store.GetTokenHash("k"));
    }

    [Fact]
    public void TrySetIfAbsent_SucceedsWhenNoLiveEntry()
    {
        var store = new InMemoryVerificationTokenStore();
        Assert.True(store.TrySetTokenIfAbsent("k", "hash1", TimeSpan.FromMinutes(5)));
        Assert.Equal("hash1", store.GetTokenHash("k"));
    }

    [Fact]
    public void TrySetIfAbsent_FailsWhileLiveEntryExists_AndLeavesItUnchanged()
    {
        var store = new InMemoryVerificationTokenStore();
        store.SetToken("k", "hash1", TimeSpan.FromMinutes(5));

        Assert.False(store.TrySetTokenIfAbsent("k", "hash2", TimeSpan.FromMinutes(5)));
        Assert.Equal("hash1", store.GetTokenHash("k"));
    }

    [Fact]
    public void TrySetIfAbsent_SucceedsAfterPriorEntryExpired()
    {
        var store = new InMemoryVerificationTokenStore();
        store.SetToken("k", "hash1", TimeSpan.FromSeconds(-1)); // already expired

        Assert.True(store.TrySetTokenIfAbsent("k", "hash2", TimeSpan.FromMinutes(5)));
        Assert.Equal("hash2", store.GetTokenHash("k"));
    }

    [Fact]
    public void Remove_DeletesEntry()
    {
        var store = new InMemoryVerificationTokenStore();
        store.SetToken("k", "hash1", TimeSpan.FromMinutes(5));
        store.RemoveToken("k");
        Assert.Null(store.GetTokenHash("k"));
    }
}
