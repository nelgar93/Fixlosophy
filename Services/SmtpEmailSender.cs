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

    public Task SendBookingNotificationAsync(Booking booking, bool isCancellation)
    {
        var (html, text) = EmailTemplates.BookingNotification(booking, isCancellation);
        var subject = isCancellation
            ? $"Cancelled: {booking.Reference} — {booking.CustomerName}"
            : $"New booking: {booking.Reference} — {booking.CustomerName}";

        // Goes to the shop, and replying should reach the customer.
        return SendAsync(SiteContent.Email, SiteContent.ShopName, subject, html, text,
            replyToEmail: booking.CustomerEmail, replyToName: booking.CustomerName);
    }

    public Task SendContactEnquiryAsync(Enquiry enquiry)
    {
        var (html, text) = EmailTemplates.ContactEnquiry(enquiry);
        return SendAsync(SiteContent.Email, SiteContent.ShopName,
            $"Website enquiry from {enquiry.Name}", html, text,
            replyToEmail: enquiry.Email, replyToName: enquiry.Name);
    }

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
            // Reachable in production only if Smtp:Host was left unset. Log loudly
            // rather than throwing: every caller treats mail as best-effort, and a
            // missing config value must not take a confirmed booking down with it.
            logger.LogError(
                "Smtp:Host is not configured — dropped email {Subject} to {Recipient}", subject, toEmail);
            return;
        }

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
}
