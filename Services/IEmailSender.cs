namespace Fixlosophy.Services;

/// <summary>
/// Outbound mail. Every method here is <b>best-effort and must not throw</b> —
/// implementations log a failure and return normally.
///
/// This is a contract, not a courtesy. Each of these is called after the thing it
/// describes has already been committed: the booking is taken, the account created,
/// the enquiry stored. A caller that has to defend against an exception either
/// undoes work it shouldn't, or — as the /auth/* endpoints in Program.cs once did —
/// fails a request whose side effects already happened, stranding the user in a
/// state they can't retry out of.
/// </summary>
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

    /// The day-before nudge, sent by MaintenanceJobs rather than by anything a user
    /// did. <paramref name="manageLink"/> is null for a guest booking, or when
    /// App:BaseUrl isn't set and no absolute URL can be built.
    Task SendAppointmentReminderAsync(Booking booking, string? manageLink);

    /// Staff moved a booking — usually because a closure landed on it. The booking
    /// carries its new time; the previous one is passed separately so the email can
    /// say which of the two now applies.
    Task SendBookingRescheduledAsync(
        Booking booking, DateTime previousDate, string previousSlot, string? reason, string? manageLink);

    /// Staff cancelled a booking the customer didn't ask to cancel. Carries a link
    /// back into the booking wizard, because a cancellation notice with no way
    /// forward is how a displaced booking becomes a lost customer.
    Task SendBookingCancelledByShopAsync(Booking booking, string? reason, string? bookAgainLink);

    /// Tells the CUSTOMER that staff confirmed or cancelled their booking. Best-effort
    /// like the confirmation above: the status change is already committed, so a mail
    /// failure must not undo it.
    Task SendBookingStatusChangedAsync(Booking booking, bool isCancellation);

    /// Invites a customer brought over by the bulk import to set a password and claim
    /// their account. Best-effort at the call site: the account already exists, so a
    /// mail failure costs the invitation, not the import.
    Task SendAccountClaimAsync(string toEmail, string toName, string claimLink);

    /// Delivers a /contact form submission to the shop, with the sender's address set
    /// as Reply-To so hitting reply reaches the customer.
    Task SendContactEnquiryAsync(Enquiry enquiry);
}
