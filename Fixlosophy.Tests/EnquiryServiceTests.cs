using Fixlosophy.Data;
using Fixlosophy.Services;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging.Abstractions;

namespace Fixlosophy.Tests;

// The /contact form used to set a bool and nothing else, so every message was lost
// while the customer was told it had arrived. These pin down the two properties that
// matter now: the enquiry is stored, and a mail failure doesn't lose it.
public class EnquiryServiceTests
{
    private sealed class RecordingEmailSender : IEmailSender
    {
        public bool ThrowOnSend { get; init; }
        public List<Enquiry> Sent { get; } = [];

        public Task SendContactEnquiryAsync(Enquiry enquiry)
        {
            if (ThrowOnSend) throw new InvalidOperationException("SMTP is down");
            Sent.Add(enquiry);
            return Task.CompletedTask;
        }

        public Task SendVerificationEmailAsync(string toEmail, string toName, string link) => Task.CompletedTask;
        public Task SendPasswordResetEmailAsync(string toEmail, string toName, string link) => Task.CompletedTask;
        public Task SendBookingConfirmationAsync(Booking booking, string manageLink) => Task.CompletedTask;
        public Task SendBookingNotificationAsync(Booking booking, bool isCancellation) => Task.CompletedTask;
        public Task SendBookingStatusChangedAsync(Booking booking, bool isCancellation) => Task.CompletedTask;
        public Task SendAccountClaimAsync(string toEmail, string toName, string claimLink) => Task.CompletedTask;
    }

    private static AppDbContext NewDb() =>
        new(new DbContextOptionsBuilder<AppDbContext>()
            .UseInMemoryDatabase(Guid.NewGuid().ToString())
            .Options);

    private static EnquiryService NewService(AppDbContext db, IEmailSender sender) =>
        new(db, sender,
            new NotificationService(db, new NotificationHub(), NullLogger<NotificationService>.Instance),
            NullLogger<EnquiryService>.Instance);

    private static Enquiry NewEnquiry() => new()
    {
        Name = "Jane Doe",
        Email = "jane@example.com",
        Service = "Full Service",
        Message = "Rear derailleur is skipping under load."
    };

    [Fact]
    public async Task SubmitAsync_StoresTheEnquiry()
    {
        using var db = NewDb();
        var sender = new RecordingEmailSender();
        var service = NewService(db, sender);

        var ok = await service.SubmitAsync(NewEnquiry());

        Assert.True(ok);
        var stored = db.Enquiries.Single();
        Assert.Equal("jane@example.com", stored.Email);
        Assert.Equal("Rear derailleur is skipping under load.", stored.Message);
        Assert.Null(stored.HandledAt);
    }

    [Fact]
    public async Task SubmitAsync_EmailsTheShop()
    {
        using var db = NewDb();
        var sender = new RecordingEmailSender();
        var service = NewService(db, sender);

        await service.SubmitAsync(NewEnquiry());

        Assert.Single(sender.Sent);
        Assert.Equal("jane@example.com", sender.Sent[0].Email);
    }

    // The whole point of persisting as well as emailing: a mail outage degrades the
    // notification, it doesn't lose the customer's message.
    [Fact]
    public async Task SubmitAsync_StillReportsSuccess_WhenTheEmailFails()
    {
        using var db = NewDb();
        var sender = new RecordingEmailSender { ThrowOnSend = true };
        var service = NewService(db, sender);

        var ok = await service.SubmitAsync(NewEnquiry());

        Assert.True(ok);
        Assert.Single(db.Enquiries);
    }

    [Fact]
    public async Task SubmitAsync_TrimsAndClampsOverlongInput()
    {
        using var db = NewDb();
        var service = NewService(db, new RecordingEmailSender());

        var enquiry = NewEnquiry();
        enquiry.Name = "   Jane Doe   ";
        enquiry.Message = new string('x', EnquiryService.MaxMessageLength + 500);

        await service.SubmitAsync(enquiry);

        var stored = db.Enquiries.Single();
        Assert.Equal("Jane Doe", stored.Name);
        Assert.Equal(EnquiryService.MaxMessageLength, stored.Message.Length);
    }

    [Fact]
    public async Task GetEnquiries_ExcludesHandledByDefault()
    {
        using var db = NewDb();
        var service = NewService(db, new RecordingEmailSender());
        await service.SubmitAsync(NewEnquiry());
        var id = db.Enquiries.Single().Id;

        service.MarkHandled(id, true);

        Assert.Empty(service.GetEnquiries());
        Assert.Single(service.GetEnquiries(includeHandled: true));
    }
}
