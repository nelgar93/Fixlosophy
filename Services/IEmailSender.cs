namespace Fixlosophy.Services;

public interface IEmailSender
{
    Task SendVerificationEmailAsync(string toEmail, string toName, string verificationLink);
    Task SendPasswordResetEmailAsync(string toEmail, string toName, string resetLink);
}
