namespace Fixlosophy.Services;

/// <summary>
/// A note staff write about a customer — usually when finishing a job ("rear hub is
/// on its way out, mention it next visit"), sometimes just something worth
/// remembering ("prefers a call, not a text").
///
/// Both foreign keys are nullable and ON DELETE SET NULL, because this is the shop's
/// record rather than the booking's: deleting a booking must not erase what was
/// learned doing it.
/// </summary>
public class CustomerNote
{
    public string Id { get; set; } = Guid.NewGuid().ToString();

    /// Null when the note was written against a booking made by a guest. It gets
    /// filled in later by AuthService.LinkGuestBookings, when they register and prove
    /// they own the email.
    public string? CustomerId { get; set; }

    /// The job this came out of, when it came out of one.
    public string? BookingId { get; set; }

    /// Who wrote it. Null once that staff member's record is gone.
    public string? AuthorStaffId { get; set; }

    public DateTime CreatedAt { get; set; } = ShopClock.Now;

    public string Body { get; set; } = "";

    /// <summary>
    /// False (the default) means staff-only: it stays out of the customer's account
    /// page and out of their GDPR export, so staff can write frankly.
    ///
    /// True turns it into a service report the customer sees — which is what the
    /// Gold Service already promises on the Services page.
    /// </summary>
    public bool VisibleToCustomer { get; set; }

    // Navigation
    public Customer? Customer { get; set; }
    public Booking? Booking { get; set; }
}
