using Fixlosophy.Data;
using Fixlosophy.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fixlosophy.Tests;

public class NotificationServiceTests
{
    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static NotificationService NewService(AppDbContext db, NotificationHub hub) =>
        new(db, hub, NullLogger<NotificationService>.Instance);

    private static Booking NewBooking(string? assignedStaffId = null) => new()
    {
        Reference = "FIX-260830-001",
        CustomerName = "Jane Doe",
        ServiceName = "Full Service",
        SlotDate = ShopClock.Today.AddDays(2),
        SlotTime = "10:00",
        AssignedStaffId = assignedStaffId
    };

    [Fact]
    public void RaiseNewBooking_StoresAndPublishes()
    {
        using var db = NewDb();
        var hub = new NotificationHub();
        var seen = new List<Notification>();
        hub.Raised += seen.Add;

        NewService(db, hub).RaiseNewBooking(NewBooking());

        var stored = db.Notifications.Single();
        Assert.Equal(NotificationType.NewBooking, stored.Type);
        Assert.Contains("Jane Doe", stored.Title, StringComparison.Ordinal);
        Assert.Empty(db.NotificationReads);   // nobody has read it yet
        Assert.Single(seen);
    }

    // The bug this design replaces: read state used to be one ReadAt column on a row
    // that every staff member shares, so one person marking read cleared the other's
    // badge. It is now a row per (notification, staff).
    [Fact]
    public void MarkAllRead_LeavesTheOtherStaffMembersUnreadCountAlone()
    {
        using var db = NewDb();
        var svc = NewService(db, new NotificationHub());
        svc.RaiseNewBooking(NewBooking());          // a broadcast both can see
        svc.RaiseNewEnquiry(new Enquiry { Name = "Someone", Service = "Full Service" });

        Assert.Equal(2, svc.UnreadCount("staff-1"));
        Assert.Equal(2, svc.UnreadCount("staff-2"));

        svc.MarkAllRead("staff-1");

        Assert.Equal(0, svc.UnreadCount("staff-1"));
        Assert.Equal(2, svc.UnreadCount("staff-2"));   // untouched
    }

    [Fact]
    public void GetRecent_ReportsIsReadPerStaffMember()
    {
        using var db = NewDb();
        var svc = NewService(db, new NotificationHub());
        svc.RaiseNewBooking(NewBooking());
        svc.MarkAllRead("staff-1");

        Assert.True(svc.GetRecent("staff-1").Single().IsRead);
        Assert.False(svc.GetRecent("staff-2").Single().IsRead);
    }

    // The bell lists unread by default, which is what makes "Mark all read" clear it.
    [Fact]
    public void GetRecent_UnreadOnly_EmptiesAfterMarkingRead()
    {
        using var db = NewDb();
        var svc = NewService(db, new NotificationHub());
        svc.RaiseNewBooking(NewBooking());

        Assert.Single(svc.GetRecent("staff-1", unreadOnly: true));

        svc.MarkAllRead("staff-1");

        Assert.Empty(svc.GetRecent("staff-1", unreadOnly: true));
        Assert.Single(svc.GetRecent("staff-1"));                 // still there under "show read"
        Assert.Single(svc.GetRecent("staff-2", unreadOnly: true)); // and still unread for them
    }

    [Fact]
    public void MarkAllRead_IsIdempotent()
    {
        using var db = NewDb();
        var svc = NewService(db, new NotificationHub());
        svc.RaiseNewBooking(NewBooking());

        Assert.Equal(1, svc.MarkAllRead("staff-1"));
        Assert.Equal(0, svc.MarkAllRead("staff-1"));   // nothing left to mark
        Assert.Single(db.NotificationReads);           // and no duplicate row
    }

    [Fact]
    public void Purge_RemovesOnlyNotificationsPastRetention()
    {
        using var db = NewDb();
        var svc = NewService(db, new NotificationHub());

        svc.Raise(new Notification { Title = "old",   CreatedAt = ShopClock.Now - NotificationService.Retention.Add(TimeSpan.FromDays(1)) });
        svc.Raise(new Notification { Title = "fresh", CreatedAt = ShopClock.Now });

        Assert.Equal(1, svc.PurgeOlderThanRetention());
        Assert.Equal("fresh", db.Notifications.Single().Title);
    }

    // A broadcast has no target, so every staff member sees it.
    [Fact]
    public void GetRecent_ReturnsBroadcastsToEveryone()
    {
        using var db = NewDb();
        var svc = NewService(db, new NotificationHub());
        svc.RaiseNewBooking(NewBooking());

        Assert.Single(svc.GetRecent("staff-1"));
        Assert.Single(svc.GetRecent("staff-2"));
    }

    // A cancellation is targeted at whoever the booking was assigned to — it's their
    // day that changed — so it must not reach anyone else.
    [Fact]
    public void GetRecent_ScopesTargetedNotificationsToTheirRecipient()
    {
        using var db = NewDb();
        var svc = NewService(db, new NotificationHub());
        svc.RaiseBookingCancelled(NewBooking(assignedStaffId: "staff-1"));

        Assert.Single(svc.GetRecent("staff-1"));
        Assert.Empty(svc.GetRecent("staff-2"));
        Assert.Equal(1, svc.UnreadCount("staff-1"));
        Assert.Equal(0, svc.UnreadCount("staff-2"));
    }

    // An unassigned booking's cancellation still has to reach somebody.
    [Fact]
    public void RaiseBookingCancelled_BroadcastsWhenNobodyIsAssigned()
    {
        using var db = NewDb();
        var svc = NewService(db, new NotificationHub());
        svc.RaiseBookingCancelled(NewBooking(assignedStaffId: null));

        Assert.Null(db.Notifications.Single().TargetStaffId);
        Assert.Single(svc.GetRecent("anyone"));
    }

    [Fact]
    public void MarkAllRead_ClearsOnlyWhatThatStaffMemberCanSee()
    {
        using var db = NewDb();
        var svc = NewService(db, new NotificationHub());
        svc.RaiseNewBooking(NewBooking());                                  // broadcast
        svc.RaiseBookingCancelled(NewBooking(assignedStaffId: "staff-2"));  // for staff-2

        var cleared = svc.MarkAllRead("staff-1");

        Assert.Equal(1, cleared);                    // staff-1 can only see the broadcast
        Assert.Equal(0, svc.UnreadCount("staff-1"));

        // Both of staff-2's are still unread: the broadcast AND the one aimed at them.
        // This assertion used to expect 1, which was the bug — read state lived in a
        // single ReadAt column on the shared broadcast row, so staff-1 marking it read
        // silently cleared it for staff-2 as well.
        Assert.Equal(2, svc.UnreadCount("staff-2"));
    }

    [Fact]
    public void GetRecent_IsNewestFirst()
    {
        using var db = NewDb();
        var svc = NewService(db, new NotificationHub());

        svc.Raise(new Notification { Title = "older", CreatedAt = ShopClock.Now.AddMinutes(-10) });
        svc.Raise(new Notification { Title = "newer", CreatedAt = ShopClock.Now });

        Assert.Equal(["newer", "older"], svc.GetRecent("staff-1").Select(n => n.Title));
    }

    [Fact]
    public void GetRecent_CapsAtRecentCount()
    {
        using var db = NewDb();
        var svc = NewService(db, new NotificationHub());
        for (var i = 0; i < NotificationService.RecentCount + 5; i++)
            svc.Raise(new Notification { Title = $"n{i}" });

        Assert.Equal(NotificationService.RecentCount, svc.GetRecent("staff-1").Count);
    }

    // The hub is a bare event over a singleton, so one subscriber throwing must not
    // stop the others hearing about it — a torn-down circuit is the normal case.
    [Fact]
    public void Hub_KeepsNotifyingAfterASubscriberThrows()
    {
        var hub = new NotificationHub();
        var reached = false;
        hub.Raised += _ => throw new InvalidOperationException("circuit is gone");
        hub.Raised += _ => reached = true;

        hub.Publish(new Notification { Title = "x" });

        Assert.True(reached);
    }

    [Fact]
    public void Hub_UnsubscribeStopsDelivery()
    {
        var hub = new NotificationHub();
        var count = 0;
        void Handler(Notification _) => count++;

        hub.Raised += Handler;
        hub.Publish(new Notification());
        hub.Raised -= Handler;
        hub.Publish(new Notification());

        Assert.Equal(1, count);
    }
}
