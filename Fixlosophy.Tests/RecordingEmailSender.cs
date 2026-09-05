using Fixlosophy.Services;

namespace Fixlosophy.Tests;

/// <summary>
/// An <see cref="IEmailSender"/> that records what it was asked to send instead of
/// sending it.
///
/// Shared rather than nested in one test class: it started as a private double inside
/// EnquiryServiceTests, and every method added to the interface since has broken that
/// file for reasons that had nothing to do with enquiries. One implementation here
/// means the next method added to <see cref="IEmailSender"/> is one edit, not a hunt.
/// </summary>
internal sealed class RecordingEmailSender : IEmailSender
{
    /// Makes every send throw, to prove callers treat mail as best-effort.
    ///
    /// Note this double deliberately *can* throw even though the real
    /// <see cref="IEmailSender"/> contract says implementations must not: the point of
    /// these tests is to pin down that a caller survives a sender that misbehaves.
    public bool ThrowOnSend { get; init; }

    public List<Enquiry> Sent { get; } = [];
    public List<(Booking Booking, string? ManageLink)> Reminders { get; } = [];
    public List<(string Email, string Link)> VerificationLinks { get; } = [];
    public List<(string Email, string Link)> PasswordResetLinks { get; } = [];
    public List<(string Email, string Link)> ClaimLinks { get; } = [];
    public List<(Booking Booking, string ManageLink)> Confirmations { get; } = [];
    public List<(Booking Booking, bool IsCancellation)> ShopNotifications { get; } = [];
    public List<(Booking Booking, bool IsCancellation)> StatusChanges { get; } = [];
    public List<(Booking Booking, DateTime PreviousDate, string PreviousSlot, string? Reason)> Rescheduled { get; } = [];
    public List<(Booking Booking, string? Reason, string? BookAgainLink)> ShopCancellations { get; } = [];

    public Task SendContactEnquiryAsync(Enquiry enquiry)
    {
        Guard();
        Sent.Add(enquiry);
        return Task.CompletedTask;
    }

    public Task SendAppointmentReminderAsync(Booking booking, string? manageLink)
    {
        Guard();
        Reminders.Add((booking, manageLink));
        return Task.CompletedTask;
    }

    public Task SendVerificationEmailAsync(string toEmail, string toName, string link)
    {
        Guard();
        VerificationLinks.Add((toEmail, link));
        return Task.CompletedTask;
    }

    public Task SendPasswordResetEmailAsync(string toEmail, string toName, string link)
    {
        Guard();
        PasswordResetLinks.Add((toEmail, link));
        return Task.CompletedTask;
    }

    public Task SendAccountClaimAsync(string toEmail, string toName, string claimLink)
    {
        Guard();
        ClaimLinks.Add((toEmail, claimLink));
        return Task.CompletedTask;
    }

    public Task SendBookingConfirmationAsync(Booking booking, string manageLink)
    {
        Guard();
        Confirmations.Add((booking, manageLink));
        return Task.CompletedTask;
    }

    public Task SendBookingNotificationAsync(Booking booking, bool isCancellation)
    {
        Guard();
        ShopNotifications.Add((booking, isCancellation));
        return Task.CompletedTask;
    }

    public Task SendBookingStatusChangedAsync(Booking booking, bool isCancellation)
    {
        Guard();
        StatusChanges.Add((booking, isCancellation));
        return Task.CompletedTask;
    }

    public Task SendBookingRescheduledAsync(
        Booking booking, DateTime previousDate, string previousSlot, string? reason, string? manageLink)
    {
        Guard();
        Rescheduled.Add((booking, previousDate, previousSlot, reason));
        return Task.CompletedTask;
    }

    public Task SendBookingCancelledByShopAsync(Booking booking, string? reason, string? bookAgainLink)
    {
        Guard();
        ShopCancellations.Add((booking, reason, bookAgainLink));
        return Task.CompletedTask;
    }

    private void Guard()
    {
        if (ThrowOnSend) throw new InvalidOperationException("SMTP is down");
    }
}
