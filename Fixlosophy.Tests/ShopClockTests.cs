using Fixlosophy.Services;

namespace Fixlosophy.Tests;

// The shop is in London, the server may not be. These pin the conversion so a UTC
// host can't silently shift every slot by an hour through British Summer Time.
public class ShopClockTests
{
    [Fact]
    public void ToShopTime_AddsAnHour_DuringBritishSummerTime()
    {
        // 1 July 2026, 10:00 UTC — BST is UTC+1, so the shop clock reads 11:00.
        var instant = new DateTimeOffset(2026, 7, 1, 10, 0, 0, TimeSpan.Zero);

        var shopTime = ShopClock.ToShopTime(instant);

        Assert.Equal(new DateTime(2026, 7, 1, 11, 0, 0), shopTime);
    }

    [Fact]
    public void ToShopTime_MatchesUtc_InWinter()
    {
        // 1 January 2026, 10:00 UTC — GMT, so no offset.
        var instant = new DateTimeOffset(2026, 1, 1, 10, 0, 0, TimeSpan.Zero);

        Assert.Equal(new DateTime(2026, 1, 1, 10, 0, 0), ShopClock.ToShopTime(instant));
    }

    // The exact moment BST begins: 01:00 UTC on the last Sunday of March 2026 (29th).
    [Fact]
    public void ToShopTime_ShiftsAtTheSpringTransition()
    {
        var justBefore = new DateTimeOffset(2026, 3, 29, 0, 59, 0, TimeSpan.Zero);
        var justAfter  = new DateTimeOffset(2026, 3, 29, 1, 1, 0, TimeSpan.Zero);

        Assert.Equal(new DateTime(2026, 3, 29, 0, 59, 0), ShopClock.ToShopTime(justBefore));
        Assert.Equal(new DateTime(2026, 3, 29, 2, 1, 0), ShopClock.ToShopTime(justAfter));
    }

    // A booking slot near midnight must not land on the wrong calendar day: during BST
    // 23:30 UTC is already 00:30 the next morning in the shop.
    [Fact]
    public void ToShopTime_RollsOverTheDate_LateEveningInSummer()
    {
        var instant = new DateTimeOffset(2026, 7, 1, 23, 30, 0, TimeSpan.Zero);

        var shopTime = ShopClock.ToShopTime(instant);

        Assert.Equal(2, shopTime.Day);
        Assert.Equal(new DateTime(2026, 7, 2, 0, 30, 0), shopTime);
    }

    [Fact]
    public void Today_HasNoTimeComponent()
    {
        Assert.Equal(TimeSpan.Zero, ShopClock.Today.TimeOfDay);
    }
}
