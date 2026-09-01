using System.Globalization;

namespace Fixlosophy.Services;

// Development-only fallback used when no Smtp:Host is configured, so local dev
// works end-to-end without a real email account — logs the message instead of
// sending it, matching SeedDefaultAdmin's "log a generated dev value" convenience.
public class ConsoleEmailSender(ILogger<ConsoleEmailSender> logger) : IEmailSender
{
    public Task SendVerificationEmailAsync(string toEmail, string toName, string verificationLink)
    {
        logger.LogWarning("[DEV EMAIL] Verification link for {Email}: {Link}", toEmail, verificationLink);
        return Task.CompletedTask;
    }

    public Task SendPasswordResetEmailAsync(string toEmail, string toName, string resetLink)
    {
        logger.LogWarning("[DEV EMAIL] Password reset link for {Email}: {Link}", toEmail, resetLink);
        return Task.CompletedTask;
    }

    public Task SendBookingConfirmationAsync(Booking booking, string manageLink)
    {
        logger.LogWarning(
            "[DEV EMAIL] Booking confirmation to {Email}: {Reference} — {Service} on {Date} at {Time}. Manage: {Link}",
            booking.CustomerEmail, booking.Reference, booking.ServiceName,
            booking.SlotDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), booking.SlotTime, manageLink);
        return Task.CompletedTask;
    }

    public Task SendBookingNotificationAsync(Booking booking, bool isCancellation)
    {
        logger.LogWarning(
            "[DEV EMAIL] Shop notification ({Kind}): {Reference} — {Customer}, {Service} on {Date} at {Time}",
            isCancellation ? "cancelled" : "new booking", booking.Reference, booking.CustomerName,
            booking.ServiceName, booking.SlotDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), booking.SlotTime);
        return Task.CompletedTask;
    }

    public Task SendBookingStatusChangedAsync(Booking booking, bool isCancellation)
    {
        logger.LogWarning(
            "[DEV EMAIL] Booking {Kind} to {Email}: {Reference} — {Service} on {Date} at {Time}",
            isCancellation ? "cancelled" : "confirmed", booking.CustomerEmail, booking.Reference,
            booking.ServiceName, booking.SlotDate.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture), booking.SlotTime);
        return Task.CompletedTask;
    }

    public Task SendAccountClaimAsync(string toEmail, string toName, string claimLink)
    {
        logger.LogWarning("[DEV EMAIL] Account claim link for {Email}: {Link}", toEmail, claimLink);
        return Task.CompletedTask;
    }

    public Task SendContactEnquiryAsync(Enquiry enquiry)
    {
        logger.LogWarning(
            "[DEV EMAIL] Contact enquiry from {Name} <{Email}> about {Service}: {Message}",
            enquiry.Name, enquiry.Email, enquiry.Service, enquiry.Message);
        return Task.CompletedTask;
    }
}
