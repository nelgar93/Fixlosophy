using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Fixlosophy.Services;

public class SmtpEmailSender(IConfiguration config, ILogger<SmtpEmailSender> logger) : IEmailSender
{
    public Task SendVerificationEmailAsync(string toEmail, string toName, string verificationLink)
    {
        var (html, text) = EmailTemplates.Verification(toName, verificationLink);
        return SendAsync(toEmail, toName, $"Verify your {SiteContent.ShopName} account", html, text);
    }

    public Task SendPasswordResetEmailAsync(string toEmail, string toName, string resetLink)
    {
        var (html, text) = EmailTemplates.PasswordReset(toName, resetLink);
        return SendAsync(toEmail, toName, $"Reset your {SiteContent.ShopName} password", html, text);
    }

    public Task SendBookingConfirmationAsync(Booking booking, string manageLink)
    {
        var (html, text) = EmailTemplates.BookingConfirmation(booking, manageLink);
        return SendAsync(booking.CustomerEmail, booking.CustomerName,
            $"Booking confirmed — {booking.Reference}", html, text);
    }

    public Task SendAppointmentReminderAsync(Booking booking, string? manageLink)
    {
        var (html, text) = EmailTemplates.AppointmentReminder(booking, manageLink);
        return SendAsync(booking.CustomerEmail, booking.CustomerName,
            $"Reminder: your bike is booked in tomorrow — {booking.Reference}", html, text);
    }

    public Task SendBookingRescheduledAsync(
        Booking booking, DateTime previousDate, string previousSlot, string? reason, string? manageLink)
    {
        var (html, text) = EmailTemplates.BookingRescheduled(booking, previousDate, previousSlot, reason, manageLink);
        return SendAsync(booking.CustomerEmail, booking.CustomerName,
            $"Your booking has moved — {booking.Reference}", html, text);
    }

    public Task SendBookingCancelledByShopAsync(Booking booking, string? reason, string? bookAgainLink)
    {
        var (html, text) = EmailTemplates.BookingCancelledByShop(booking, reason, bookAgainLink);
        return SendAsync(booking.CustomerEmail, booking.CustomerName,
            $"We've had to cancel your booking — {booking.Reference}", html, text);
    }

    public Task SendBookingNotificationAsync(Booking booking, bool isCancellation)
    {
        var inbox = ShopInbox($"booking notification for {booking.Reference}");
        if (inbox is null) return Task.CompletedTask;

        var (html, text) = EmailTemplates.BookingNotification(booking, isCancellation);
        var subject = isCancellation
            ? $"Cancelled: {booking.Reference} — {booking.CustomerName}"
            : $"New booking: {booking.Reference} — {booking.CustomerName}";

        // Goes to the shop, and replying should reach the customer.
        return SendAsync(inbox, SiteContent.ShopName, subject, html, text,
            replyToEmail: booking.CustomerEmail, replyToName: booking.CustomerName);
    }

    public Task SendBookingStatusChangedAsync(Booking booking, bool isCancellation)
    {
        var (html, text) = EmailTemplates.BookingStatusChanged(booking, isCancellation);
        var subject = isCancellation
            ? $"Cancelled — {booking.Reference}"
            : $"Booking confirmed — {booking.Reference}";

        return SendAsync(booking.CustomerEmail, booking.CustomerName, subject, html, text);
    }

    public Task SendAccountClaimAsync(string toEmail, string toName, string claimLink)
    {
        var (html, text) = EmailTemplates.AccountClaim(toName, claimLink);
        return SendAsync(toEmail, toName, $"Your {SiteContent.ShopName} account is ready", html, text);
    }

    public Task SendContactEnquiryAsync(Enquiry enquiry)
    {
        var inbox = ShopInbox($"enquiry from {enquiry.Name}");
        if (inbox is null) return Task.CompletedTask;

        var (html, text) = EmailTemplates.ContactEnquiry(enquiry);
        return SendAsync(inbox, SiteContent.ShopName,
            $"Website enquiry from {enquiry.Name}", html, text,
            replyToEmail: enquiry.Email, replyToName: enquiry.Name);
    }

    // Where staff notifications land. Deliberately not SiteContent.Email: that
    // constant is the address the site publishes to customers, and the inbox that
    // receives the work is free to differ from it — and to change without a
    // rebuild, which a const compiled into the binary cannot.
    private string? ShopInbox(string what)
    {
        var inbox = config["Notifications:Email"];
        if (!string.IsNullOrWhiteSpace(inbox)) return inbox;

        // Same stance as the missing Smtp:Host below: every caller treats mail as
        // best-effort, so log loudly and drop it rather than fail the booking.
        logger.LogError("Notifications:Email is not configured — dropped {What}", what);
        return null;
    }

    /// <summary>
    /// Sends one message, and never throws.
    ///
    /// Every caller in the application treats mail as best-effort — the booking is
    /// already committed, the account already created, the enquiry already stored —
    /// so a dead SMTP host must cost the notification and nothing else. That contract
    /// is enforced here rather than at each call site, because it was only true at
    /// the call sites that remembered: the four /auth/* endpoints in Program.cs
    /// awaited this directly, so a refused connection 500'd a registration whose
    /// account row had already been written (leaving an account its owner could
    /// neither verify nor re-create), and made /auth/forgot-password answer
    /// differently for a real address than an unknown one — an enumeration oracle
    /// undoing the identical-response handling deliberately built above it.
    ///
    /// Failures are logged at Error with the subject and recipient. Nothing here is
    /// retried: a transient outage costs one message, and the operations that matter
    /// (verification, password reset) all have a user-driven resend behind a cooldown.
    /// </summary>
    private async Task SendAsync(
        string toEmail, string toName, string subject, string html, string text,
        string? replyToEmail = null, string? replyToName = null)
    {
        var host     = config["Smtp:Host"] ?? "";
        var user     = config["Smtp:User"] ?? "";
        var password = config["Smtp:Password"] ?? "";
        var from     = config["Smtp:From"] ?? "";
        var fromName = config["Smtp:FromName"] ?? SiteContent.ShopName;

        if (string.IsNullOrEmpty(host))
        {
            // Reachable in production only if Smtp:Host was left unset.
            logger.LogError(
                "Smtp:Host is not configured — dropped email {Subject} to {Recipient}", subject, toEmail);
            return;
        }

        try
        {
            var message = new MimeMessage();
            message.From.Add(new MailboxAddress(fromName, from));
            message.To.Add(new MailboxAddress(toName, toEmail));
            if (!string.IsNullOrWhiteSpace(replyToEmail))
                message.ReplyTo.Add(new MailboxAddress(replyToName ?? "", replyToEmail));
            message.Subject = subject;
            message.Body = new BodyBuilder { HtmlBody = html, TextBody = text }.ToMessageBody();

            // MailKit's SmtpClient is not thread-safe and not reusable across concurrent
            // sends, so a fresh instance is created per send even though this service
            // itself is registered as a singleton.
            using var client = new SmtpClient();
            await client.ConnectAsync(
                host,
                config.GetValue("Smtp:Port", 587),
                config.GetValue("Smtp:UseSsl", true) ? SecureSocketOptions.StartTls : SecureSocketOptions.None);
            if (!string.IsNullOrEmpty(user))
                await client.AuthenticateAsync(user, password);
            await client.SendAsync(message);
            await client.DisconnectAsync(true);
        }
        catch (Exception ex)
        {
            // Deliberately catch-all. A malformed address, a TLS negotiation failure, a
            // template bug and a refused connection all arrive as different types, and
            // the caller's correct response to every one of them is the same: carry on.
            logger.LogError(ex,
                "Could not send email {Subject} to {Recipient}", subject, toEmail);
        }
    }
}
