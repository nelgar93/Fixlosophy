namespace Fixlosophy.Services;

/// <summary>
/// A message sent through the /contact form.
///
/// Persisted as well as emailed, deliberately. The form previously did neither — it
/// only flipped a flag and told the customer "we've received your booking request" —
/// so every enquiry was lost. Storing the row means a mail outage costs a notification,
/// not the message itself.
/// </summary>
public class Enquiry
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public DateTime CreatedAt { get; set; } = ShopClock.Now;

    public string Name { get; set; } = "";
    public string Email { get; set; } = "";
    public string Phone { get; set; } = "";
    public string Service { get; set; } = "";
    public string BikeDescription { get; set; } = "";
    public string Message { get; set; } = "";
    public DateTime? PreferredDate { get; set; }

    /// Set once someone in the shop has dealt with it, so the admin list can separate
    /// outstanding enquiries from handled ones.
    public DateTime? HandledAt { get; set; }
}
