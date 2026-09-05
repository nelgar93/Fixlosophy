using System.ComponentModel.DataAnnotations.Schema;

namespace Fixlosophy.Services;

/// <summary>
/// What a notification is about. New kinds are added here and raised from wherever
/// the event happens — the table, the bell UI and the live push are all
/// type-agnostic, so nothing else needs to change.
///
/// Explicit values because these are persisted: reordering the enum must not
/// reinterpret existing rows. Inventory is reserved rather than implemented, so the
/// numbering doesn't shuffle when it lands.
/// </summary>
public enum NotificationType
{
    NewBooking        = 0,
    BookingCancelled  = 1,
    NewEnquiry        = 2,

    /// A slot has come and gone with the bike not booked in. Raised by
    /// MaintenanceJobs, not by anything a person did — see FlagLateArrivalsAsync.
    LateArrival       = 3,

    LowStock          = 10,
    OutOfStock        = 11,
}

public class Notification
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public NotificationType Type { get; set; }
    public DateTime CreatedAt { get; set; } = ShopClock.Now;

    public string Title { get; set; } = "";
    public string Body { get; set; } = "";

    /// Where the bell's dropdown sends you — a relative path within the app.
    public string LinkUrl { get; set; } = "";

    /// Null means every staff member sees it. Set it to scope a notification to one
    /// person (e.g. "a booking assigned to you was cancelled").
    public string? TargetStaffId { get; set; }

    /// <summary>
    /// Whether the staff member this was loaded for has read it. Not a column —
    /// read state lives in <see cref="NotificationRead"/>, one row per person, and
    /// this is filled in by the query.
    ///
    /// It used to be a persisted <c>ReadAt</c> column, which was wrong: a broadcast
    /// notification is a single row shared by every staff member, so one person
    /// marking it read cleared everyone's badge.
    /// </summary>
    [NotMapped]
    public bool IsRead { get; set; }

    // Navigation
    public List<NotificationRead> Reads { get; set; } = [];
}

/// <summary>
/// One staff member having read one notification. The existence of the row is the
/// read state; there is nothing to update, only to insert.
///
/// No foreign key to Staff on purpose — the record of who read what should outlive
/// someone leaving, and the row is meaningless to anyone else anyway.
/// </summary>
public class NotificationRead
{
    public string NotificationId { get; set; } = "";
    public string StaffId { get; set; } = "";
    public DateTime ReadAt { get; set; } = ShopClock.Now;

    // Navigation
    public Notification? Notification { get; set; }
}
