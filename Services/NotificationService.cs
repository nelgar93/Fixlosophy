using System.Globalization;
using Fixlosophy.Data;

namespace Fixlosophy.Services;

/// <summary>
/// Records notifications for staff and pushes them to any dashboard that's open.
///
/// Persisted first, published second: the row is the source of truth (it survives a
/// restart and carries the unread state), and the live push is a convenience on top.
/// A subscriber blowing up therefore can't lose the notification.
///
/// Every Raise* method is best-effort by contract — see the try/catch in each caller.
/// Nothing here may take down the booking or enquiry it is reporting on.
/// </summary>
public class NotificationService(AppDbContext db, NotificationHub hub, ILogger<NotificationService> logger)
{
    /// How many to show in the bell dropdown.
    public const int RecentCount = 20;

    private static readonly CultureInfo Uk = CultureInfo.GetCultureInfo("en-GB");

    public void RaiseNewBooking(Booking booking) => Raise(new Notification
    {
        Type = NotificationType.NewBooking,
        Title = $"New booking — {booking.CustomerName}",
        Body = $"{booking.ServiceName}, {booking.SlotDate.ToString("ddd d MMM", Uk)} at {booking.SlotTime}",
        LinkUrl = "/admin"
    });

    public void RaiseBookingCancelled(Booking booking) => Raise(new Notification
    {
        Type = NotificationType.BookingCancelled,
        Title = $"Cancelled — {booking.CustomerName}",
        Body = $"{booking.ServiceName}, {booking.SlotDate.ToString("ddd d MMM", Uk)} at {booking.SlotTime}. The slot is free again.",
        LinkUrl = "/admin",
        // If it was assigned to someone, they're the one whose day just changed.
        TargetStaffId = booking.AssignedStaffId
    });

    public void RaiseNewEnquiry(Enquiry enquiry) => Raise(new Notification
    {
        Type = NotificationType.NewEnquiry,
        Title = $"Website enquiry — {enquiry.Name}",
        Body = enquiry.Service is { Length: > 0 } s ? s : "General enquiry",
        LinkUrl = "/admin"
    });

    public void Raise(Notification notification)
    {
        try
        {
            db.Notifications.Add(notification);
            db.SaveChanges();
        }
        catch (Exception ex)
        {
            // Don't publish what we failed to store — a bell entry that vanishes on
            // refresh is worse than no bell entry.
            logger.LogError(ex, "Could not store {Type} notification", notification.Type);
            return;
        }

        hub.Publish(notification);
    }

    /// How long a notification is kept before the startup purge removes it.
    public static readonly TimeSpan Retention = TimeSpan.FromDays(30);

    /// Broadcasts (no target) plus anything aimed at this person specifically.
    private IQueryable<Notification> VisibleTo(string staffId) =>
        db.Notifications.Where(n => n.TargetStaffId == null || n.TargetStaffId == staffId);

    /// <summary>
    /// What this staff member should see in the bell, newest first, with
    /// <see cref="Notification.IsRead"/> filled in for them specifically.
    ///
    /// The read test is "does a NotificationReads row exist for me" rather than a
    /// column on the notification — a broadcast is one row shared by everyone, so a
    /// column would mean one person reading it cleared the other's badge.
    /// </summary>
    public List<Notification> GetRecent(string staffId, bool unreadOnly = false)
    {
        var query = VisibleTo(staffId);

        if (unreadOnly)
            query = query.Where(n => !n.Reads.Any(r => r.StaffId == staffId));

        // Projected in one round trip: no N+1 over Reads, and IsRead is computed by
        // the database rather than by loading every read row.
        return query
            .OrderByDescending(n => n.CreatedAt)
            .Take(RecentCount)
            .Select(n => new Notification
            {
                Id            = n.Id,
                Type          = n.Type,
                CreatedAt     = n.CreatedAt,
                Title         = n.Title,
                Body          = n.Body,
                LinkUrl       = n.LinkUrl,
                TargetStaffId = n.TargetStaffId,
                IsRead        = n.Reads.Any(r => r.StaffId == staffId)
            })
            .ToList();
    }

    public int UnreadCount(string staffId) =>
        VisibleTo(staffId).Count(n => !n.Reads.Any(r => r.StaffId == staffId));

    /// <summary>
    /// Marks everything currently visible to this staff member as read, by inserting
    /// a read row per notification. Returns how many were newly marked.
    ///
    /// Only this person's rows are written, so the other staff member's unread count
    /// is untouched — which is the whole point of the join table.
    /// </summary>
    public int MarkAllRead(string staffId)
    {
        var unreadIds = VisibleTo(staffId)
            .Where(n => !n.Reads.Any(r => r.StaffId == staffId))
            .Select(n => n.Id)
            .ToList();
        if (unreadIds.Count == 0) return 0;

        var now = ShopClock.Now;
        foreach (var id in unreadIds)
            db.NotificationReads.Add(new NotificationRead
            {
                NotificationId = id,
                StaffId = staffId,
                ReadAt = now
            });

        db.SaveChanges();
        return unreadIds.Count;
    }

    /// <summary>
    /// Deletes notifications past <see cref="Retention"/>. Their read rows go with
    /// them via ON DELETE CASCADE.
    ///
    /// Called once at startup rather than on a timer: this is housekeeping to stop
    /// the table growing without bound, not something that needs to be prompt.
    /// </summary>
    public int PurgeOlderThanRetention()
    {
        var cutoff = ShopClock.Now - Retention;
        var stale = db.Notifications.Where(n => n.CreatedAt < cutoff).ToList();
        if (stale.Count == 0) return 0;

        db.Notifications.RemoveRange(stale);
        db.SaveChanges();
        return stale.Count;
    }
}
