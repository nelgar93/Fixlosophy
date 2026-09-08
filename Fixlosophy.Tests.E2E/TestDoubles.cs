using System.Collections.Concurrent;
using Fixlosophy.Services;

namespace Fixlosophy.Tests.E2E;

/// <summary>
/// An <see cref="IEmailSender"/> that records instead of sending.
/// </summary>
/// <remarks>
/// <para>
/// <b>This one is not optional.</b> The fixture boots the real application with the
/// real <c>appsettings.Local.json</c> — it has to, because that is where the content
/// root and the static assets are — and that file holds a working SMTP host. Left
/// alone, Program.cs would pick <c>SmtpEmailSender</c> and every booking these tests
/// make would put real mail in a real inbox. Program reads <c>Smtp:Host</c> before any
/// test-side configuration can reach it, so the swap has to happen at the service
/// registration, which is what <see cref="AppFixture"/> does.
/// </para>
/// <para>
/// The same reasoning covers <see cref="NoOpStorageService"/> below and the Supabase
/// credentials sitting next to those SMTP ones.
/// </para>
/// </remarks>
public sealed class RecordingEmailSender : IEmailSender
{
    public ConcurrentBag<Booking> Confirmations { get; } = [];

    public ConcurrentBag<Booking> ShopNotifications { get; } = [];

    public Task SendBookingConfirmationAsync(Booking booking, string manageLink)
    {
        Confirmations.Add(booking);
        return Task.CompletedTask;
    }

    public Task SendBookingNotificationAsync(Booking booking, bool isCancellation)
    {
        ShopNotifications.Add(booking);
        return Task.CompletedTask;
    }

    public Task SendVerificationEmailAsync(string toEmail, string toName, string verificationLink) => Task.CompletedTask;
    public Task SendPasswordResetEmailAsync(string toEmail, string toName, string resetLink) => Task.CompletedTask;
    public Task SendAppointmentReminderAsync(Booking booking, string? manageLink) => Task.CompletedTask;
    public Task SendBookingRescheduledAsync(Booking booking, DateTime previousDate, string previousSlot, string? reason, string? manageLink) => Task.CompletedTask;
    public Task SendBookingCancelledByShopAsync(Booking booking, string? reason, string? bookAgainLink) => Task.CompletedTask;
    public Task SendBookingStatusChangedAsync(Booking booking, bool isCancellation) => Task.CompletedTask;
    public Task SendAccountClaimAsync(string toEmail, string toName, string claimLink) => Task.CompletedTask;
    public Task SendContactEnquiryAsync(Enquiry enquiry) => Task.CompletedTask;
}

/// <summary>
/// Keeps photo uploads away from the real Supabase bucket, whose service-role key is in
/// the same local settings file as the SMTP credentials. Accepts everything and returns
/// plausible values, so the wizard's upload path still runs end to end.
/// </summary>
public sealed class NoOpStorageService : IStorageService
{
    public string? ValidatePhoto(string contentType, long size) => null;

    public Task<(string? path, string? error)> UploadCustomerPhotoAsync(string bookingId, string contentType, byte[] content) =>
        Task.FromResult<(string?, string?)>(($"e2e/{bookingId}.jpg", null));

    public Task<string?> GetSignedPhotoUrlAsync(string storagePath, TimeSpan expiry) =>
        Task.FromResult<string?>("https://example.invalid/signed");

    public Task<bool> DeleteAsync(string storagePath) => Task.FromResult(true);

    public string GetPublicWebsiteImageUrl(string fileName) => $"https://example.invalid/{fileName}";
}
