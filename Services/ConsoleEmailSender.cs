namespace Fixlosophy.Services;

// Development-only fallback used when no Smtp:Host is configured, so local dev
// works end-to-end without a real email account — logs the link instead of
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
}
