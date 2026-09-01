using Fixlosophy.Data;

namespace Fixlosophy.Services;

public class EnquiryService(
    AppDbContext db,
    IEmailSender emailSender,
    NotificationService notifications,
    ILogger<EnquiryService> logger)
{
    public const int MaxMessageLength = 4000;
    public const int MaxFieldLength = 200;

    /// <summary>
    /// Stores an enquiry, then tries to email it to the shop.
    ///
    /// The order matters: the row is committed first, so a mail failure costs the
    /// notification rather than the message. Returns false only when the enquiry
    /// couldn't be stored — a failed send is logged and swallowed, because telling the
    /// customer "that didn't work" when we do in fact have their message would send
    /// them somewhere else for no reason.
    ///
    /// Re-validates email and phone rather than trusting the form. This is a public
    /// POST target and the client-side attributes are bypassable; up to now the only
    /// server-side handling was a length clamp, so anything at all could be stored.
    /// </summary>
    public async Task<bool> SubmitAsync(Enquiry enquiry)
    {
        if (!AuthService.IsValidEmail(enquiry.Email))
        {
            logger.LogWarning("Rejected enquiry with an unusable email address.");
            return false;
        }

        // Phone is optional on the contact form, but a supplied one has to be dialable.
        if (!string.IsNullOrWhiteSpace(enquiry.Phone) && !AuthService.IsValidPhone(enquiry.Phone))
        {
            logger.LogWarning("Rejected enquiry from {Email} with an unusable phone number.", enquiry.Email);
            return false;
        }

        enquiry.Name            = Clamp(enquiry.Name, MaxFieldLength);
        enquiry.Email           = Clamp(enquiry.Email, MaxFieldLength);
        enquiry.Phone           = Clamp(enquiry.Phone, MaxFieldLength);
        enquiry.Service         = Clamp(enquiry.Service, MaxFieldLength);
        enquiry.BikeDescription = Clamp(enquiry.BikeDescription, MaxFieldLength);
        enquiry.Message         = Clamp(enquiry.Message, MaxMessageLength);

        try
        {
            db.Enquiries.Add(enquiry);
            await db.SaveChangesAsync();
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Could not store enquiry from {Email}", enquiry.Email);
            return false;
        }

        // In-app notification and email are independent paths to the same person, so
        // each is tried separately — a broken mailbox still lights up the bell.
        try
        {
            notifications.RaiseNewEnquiry(enquiry);
        }
        catch (Exception ex)
        {
            logger.LogError(ex, "Stored enquiry {Id} but could not raise a notification", enquiry.Id);
        }

        try
        {
            await emailSender.SendContactEnquiryAsync(enquiry);
        }
        catch (Exception ex)
        {
            // Stored but not delivered — it's still in the admin list, so this is a
            // degraded notification rather than lost data.
            logger.LogError(ex, "Stored enquiry {Id} but could not email it to the shop", enquiry.Id);
        }
        return true;
    }

    public List<Enquiry> GetEnquiries(bool includeHandled = false) =>
        db.Enquiries
          .Where(e => includeHandled || e.HandledAt == null)
          .OrderByDescending(e => e.CreatedAt)
          .ToList();

    public bool MarkHandled(string id, bool handled)
    {
        var enquiry = db.Enquiries.Find(id);
        if (enquiry is null) return false;
        enquiry.HandledAt = handled ? ShopClock.Now : null;
        db.SaveChanges();
        return true;
    }

    // Trims to the column's practical limit rather than rejecting: the form already
    // enforces these client-side, so anything longer is a direct POST, and silently
    // truncating an over-long message is better than dropping the enquiry.
    private static string Clamp(string? value, int max)
    {
        var trimmed = (value ?? "").Trim();
        return trimmed.Length <= max ? trimmed : trimmed[..max];
    }
}
