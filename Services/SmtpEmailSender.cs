using MailKit.Net.Smtp;
using MailKit.Security;
using MimeKit;

namespace Fixlosophy.Services;

public class SmtpEmailSender(IConfiguration config) : IEmailSender
{
    public Task SendVerificationEmailAsync(string toEmail, string toName, string verificationLink) =>
        SendAsync(toEmail, toName, "Verify your Fixlosophy account",
            $"<p>Hi {toName},</p><p>Click below to verify your email address:</p><p><a href=\"{verificationLink}\">Verify my email</a></p><p>This link expires in 24 hours.</p>",
            $"Hi {toName},\n\nVerify your email address: {verificationLink}\n\nThis link expires in 24 hours.");

    public Task SendPasswordResetEmailAsync(string toEmail, string toName, string resetLink) =>
        SendAsync(toEmail, toName, "Reset your Fixlosophy password",
            $"<p>Hi {toName},</p><p>Click below to reset your password:</p><p><a href=\"{resetLink}\">Reset my password</a></p><p>This link expires in 1 hour. If you didn't request this, you can ignore this email.</p>",
            $"Hi {toName},\n\nReset your password: {resetLink}\n\nThis link expires in 1 hour. If you didn't request this, you can ignore this email.");

    private async Task SendAsync(string toEmail, string toName, string subject, string html, string text)
    {
        var host     = config["Smtp:Host"] ?? "";
        var user     = config["Smtp:User"] ?? "";
        var password = config["Smtp:Password"] ?? "";
        var from     = config["Smtp:From"] ?? "";
        var fromName = config["Smtp:FromName"] ?? "Fixlosophy";

        var message = new MimeMessage();
        message.From.Add(new MailboxAddress(fromName, from));
        message.To.Add(new MailboxAddress(toName, toEmail));
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
