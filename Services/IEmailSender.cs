namespace Fixlosophy.Services;

public interface IEmailSender
{
    Task SendVerificationEmailAsync(string toEmail, string toName, string verificationLink);
    Task SendPasswordResetEmailAsync(string toEmail, string toName, string resetLink);

    /// Confirmation to the customer once a booking is created. Best-effort at every
    /// call site — a mail failure must never undo a booking the customer has been told
    /// is confirmed.
    Task SendBookingConfirmationAsync(Booking booking, string manageLink);

    /// Tells the shop a booking has arrived or been cancelled by the customer.
    Task SendBookingNotificationAsync(Booking booking, bool isCancellation);

    /// Tells the CUSTOMER that staff confirmed or cancelled their booking. Best-effort
    /// like the confirmation above: the status change is already committed, so a mail
    /// failure must not undo it.
    Task SendBookingStatusChangedAsync(Booking booking, bool isCancellation);

    /// Invites a customer brought over by the bulk import to set a password and claim
    /// their account. Best-effort at the call site: the account already exists, so a
    /// mail failure costs the invitation, not the import.
    Task SendAccountClaimAsync(string toEmail, string toName, string claimLink);

    /// Delivers a /contact form submission to the shop, with the sender's address set
    /// as Reply-To so hitting reply reaches the customer.
    Task SendContactEnquiryAsync(Enquiry enquiry);
}
