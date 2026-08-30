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

    /// Delivers a /contact form submission to the shop, with the sender's address set
    /// as Reply-To so hitting reply reaches the customer.
    Task SendContactEnquiryAsync(Enquiry enquiry);
}
