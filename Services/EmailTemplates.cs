using System.Globalization;
using System.Net;

namespace Fixlosophy.Services;

/// <summary>
/// Builds the HTML and plain-text bodies for every outbound email, so the two senders
/// (SMTP and the dev console logger) can't drift apart and every message carries the
/// same shop details from <see cref="SiteContent"/>.
///
/// Deliberately plain, table-free HTML with inline styles: mail clients strip
/// stylesheets, and a simple single-column body renders correctly on a phone, which is
/// where most of these will be opened.
/// </summary>
public static class EmailTemplates
{
    private const string Green = "#2D9A38";
    private const string Ink   = "#1a1a1a";
    private const string Muted = "#666666";

    /// Every value interpolated into an email body is HTML-escaped — booking notes and
    /// enquiry messages are free text typed by the public, and they land in the shop's
    /// inbox.
    private static string E(string? value) => WebUtility.HtmlEncode(value ?? "");

    private static string Shell(string heading, string bodyHtml) => $"""
        <div style="margin:0;padding:24px 16px;background:#f4f4f5;font-family:Helvetica,Arial,sans-serif;">
          <div style="max-width:560px;margin:0 auto;background:#ffffff;border-radius:12px;padding:28px 24px;">
            <p style="margin:0 0 4px;font-size:13px;font-weight:700;letter-spacing:1.5px;text-transform:uppercase;color:{Green};">{E(SiteContent.ShopName)}</p>
            <h1 style="margin:0 0 18px;font-size:21px;line-height:1.3;color:{Ink};">{E(heading)}</h1>
            {bodyHtml}
            <hr style="border:none;border-top:1px solid #e5e5e5;margin:26px 0 16px;" />
            <p style="margin:0;font-size:13px;line-height:1.6;color:{Muted};">
              {E(SiteContent.AddressOneLine)}<br />
              <a href="tel:{SiteContent.PhoneE164}" style="color:{Green};text-decoration:none;">{E(SiteContent.PhoneDisplay)}</a>
              &nbsp;·&nbsp;
              <a href="mailto:{SiteContent.Email}" style="color:{Green};text-decoration:none;">{E(SiteContent.Email)}</a>
            </p>
          </div>
        </div>
        """;

    private static string Button(string href, string label) => $"""
        <p style="margin:22px 0;">
          <a href="{E(href)}" style="display:inline-block;background:{Green};color:#ffffff;text-decoration:none;font-weight:600;font-size:15px;padding:12px 24px;border-radius:50px;">{E(label)}</a>
        </p>
        """;

    private static string Para(string text) =>
        $"""<p style="margin:0 0 14px;font-size:15px;line-height:1.6;color:{Ink};">{E(text)}</p>""";

    /// A label/value pair. Values are escaped; labels are ours, never user input.
    private static string Row(string label, string value) => $"""
        <tr>
          <td style="padding:7px 14px 7px 0;font-size:13px;color:{Muted};white-space:nowrap;vertical-align:top;">{label}</td>
          <td style="padding:7px 0;font-size:15px;color:{Ink};font-weight:600;">{E(value)}</td>
        </tr>
        """;

    private static string Table(params string[] rows) =>
        $"""<table style="width:100%;border-collapse:collapse;margin:0 0 8px;">{string.Concat(rows)}</table>""";

    // ── Verification ─────────────────────────────────────────────────────────
    public static (string html, string text) Verification(string toName, string link) =>
    (
        Shell("Verify your email address",
            Para($"Hi {toName},") +
            Para("Confirm your email address to activate your account.") +
            Button(link, "Verify my email") +
            Para("This link expires in 24 hours. If you didn't create an account, you can ignore this email.")),

        $"Hi {toName},\n\nVerify your email address: {link}\n\n" +
        "This link expires in 24 hours. If you didn't create an account, you can ignore this email."
    );

    // ── Password reset ───────────────────────────────────────────────────────
    public static (string html, string text) PasswordReset(string toName, string link) =>
    (
        Shell("Reset your password",
            Para($"Hi {toName},") +
            Para("Choose a new password using the link below.") +
            Button(link, "Reset my password") +
            Para("This link expires in 1 hour. If you didn't request this, you can ignore this email — your password won't change.")),

        $"Hi {toName},\n\nReset your password: {link}\n\n" +
        "This link expires in 1 hour. If you didn't request this, you can ignore this email."
    );

    // ── Booking confirmation (to the customer) ───────────────────────────────
    public static (string html, string text) BookingConfirmation(Booking b, string manageLink)
    {
        var when  = $"{b.SlotDate.ToString("dddd d MMMM yyyy", CultureInfo.GetCultureInfo("en-GB"))} at {b.SlotTime}";
        var price = b.ServicePrice > 0
            ? $"From £{b.ServicePrice.ToString("0.##", CultureInfo.InvariantCulture)}"
            : "Quote on arrival";

        var rows = new List<string>
        {
            Row("Reference", b.Reference),
            Row("Service",   b.ServiceName),
            Row("When",      when),
            Row("Estimate",  price),
            Row("Where",     SiteContent.AddressOneLine)
        };
        if (!string.IsNullOrWhiteSpace(b.BikeDescription))
            rows.Add(Row("Bike", b.BikeDescription));

        // Account holders get a link to their bookings list; guests have nowhere to
        // manage it themselves yet, so they're pointed at the shop instead.
        var (linkLabel, linkNote) = b.CustomerId is null
            ? ("Get in touch", $"Need to change or cancel? Call us on {SiteContent.PhoneDisplay}.")
            : ("View my bookings", $"You can cancel from your account, or call us on {SiteContent.PhoneDisplay}.");

        var html = Shell("Your booking is confirmed",
            Para($"Hi {b.CustomerName},") +
            Para("Thanks for booking with us. Here are the details:") +
            Table([.. rows]) +
            Para("Please bring your bike in about five minutes before your slot. The final price is confirmed after we've assessed the bike — you won't be charged until you approve the quote.") +
            Button(manageLink, linkLabel) +
            Para(linkNote));

        var text =
            $"Hi {b.CustomerName},\n\nYour booking is confirmed.\n\n" +
            $"Reference: {b.Reference}\n" +
            $"Service:   {b.ServiceName}\n" +
            $"When:      {when}\n" +
            $"Estimate:  {price}\n" +
            $"Where:     {SiteContent.AddressOneLine}\n\n" +
            "Please arrive about five minutes early. The final price is confirmed after assessment.\n\n" +
            $"{linkLabel}: {manageLink}\n" +
            $"Questions: {SiteContent.PhoneDisplay}\n";

        return (html, text);
    }

    // ── Account claim (to an imported customer) ──────────────────────────────
    /// <summary>
    /// Sent once to a customer whose record was brought over from the shop's previous
    /// system. Deliberately not the password-reset template: they never had a password
    /// here, and "reset your password" to someone who has never signed in reads as a
    /// phishing attempt.
    /// </summary>
    public static (string html, string text) AccountClaim(string toName, string claimLink)
    {
        var greeting = string.IsNullOrWhiteSpace(toName) ? "Hello," : $"Hi {toName},";

        var html = Shell($"Your {SiteContent.ShopName} account is ready",
            Para(greeting) +
            Para($"We've moved {SiteContent.ShopName}'s booking system over to a new site, and brought your details with us. " +
                 "Set a password and you'll be able to book online and see your past visits.") +
            Button(claimLink, "Set my password") +
            Para("If you'd rather not have an account, you can ignore this — you can still book by phone " +
                 $"on {SiteContent.PhoneDisplay} or by dropping in.") +
            Para("This link is valid for a week."));

        var text =
            $"{greeting}\n\n" +
            $"We've moved {SiteContent.ShopName}'s booking system over to a new site, and brought your\n" +
            "details with us. Set a password and you'll be able to book online and see your\n" +
            "past visits.\n\n" +
            $"Set my password: {claimLink}\n\n" +
            "This link is valid for a week. If you'd rather not have an account, you can\n" +
            $"ignore this — you can still book on {SiteContent.PhoneDisplay} or by dropping in.\n";

        return (html, text);
    }

    // ── Status changed by the shop (to the customer) ─────────────────────────
    /// <summary>
    /// What the customer hears when staff move a booking on. Only two statuses get an
    /// email: Confirmed ("we've accepted it") and Cancelled ("we can't do it"). Starting
    /// work and finishing it stay inside the shop — a customer who is already in the
    /// diary doesn't need a message when a mechanic picks the bike up.
    ///
    /// Until this existed the dashboard's Confirm button told the customer nothing at
    /// all, so a cancellation reached them by them turning up at the shop.
    /// </summary>
    public static (string html, string text) BookingStatusChanged(Booking b, bool isCancellation)
    {
        var when = $"{b.SlotDate.ToString("dddd d MMMM yyyy", CultureInfo.GetCultureInfo("en-GB"))} at {b.SlotTime}";

        var rows = new List<string>
        {
            Row("Reference", b.Reference),
            Row("Service",   b.ServiceName),
            Row("When",      when)
        };
        if (!isCancellation)
            rows.Add(Row("Where", SiteContent.AddressOneLine));

        if (isCancellation)
        {
            var html = Shell("Your booking has been cancelled",
                Para($"Hi {b.CustomerName},") +
                Para("We're sorry — we've had to cancel this booking, and the slot is now free again.") +
                Table([.. rows]) +
                Para($"Give us a ring on {SiteContent.PhoneDisplay} and we'll find you another time.")); 

            var text =
                $"Hi {b.CustomerName},\n\nWe're sorry — we've had to cancel this booking.\n\n" +
                $"Reference: {b.Reference}\n" +
                $"Service:   {b.ServiceName}\n" +
                $"When:      {when}\n\n" +
                $"Call us on {SiteContent.PhoneDisplay} and we'll find you another time.\n";

            return (html, text);
        }

        var confirmedHtml = Shell("Your booking is confirmed",
            Para($"Hi {b.CustomerName},") +
            Para("We've confirmed your appointment — we'll see you then.") +
            Table([.. rows]) +
            Para("Please bring your bike in about five minutes before your slot. The final price is confirmed after we've assessed the bike.") +
            Para($"Need to change it? Call us on {SiteContent.PhoneDisplay}."));

        var confirmedText =
            $"Hi {b.CustomerName},\n\nWe've confirmed your appointment.\n\n" +
            $"Reference: {b.Reference}\n" +
            $"Service:   {b.ServiceName}\n" +
            $"When:      {when}\n" +
            $"Where:     {SiteContent.AddressOneLine}\n\n" +
            "Please arrive about five minutes early.\n" +
            $"Need to change it? Call us on {SiteContent.PhoneDisplay}.\n";

        return (confirmedHtml, confirmedText);
    }

    // ── Booking notification (to the shop) ───────────────────────────────────
    public static (string html, string text) BookingNotification(Booking b, bool isCancellation)
    {
        var heading = isCancellation ? "Booking cancelled" : "New booking";
        var when    = $"{b.SlotDate.ToString("dddd d MMMM yyyy", CultureInfo.GetCultureInfo("en-GB"))} at {b.SlotTime}";

        var rows = new List<string>
        {
            Row("Reference", b.Reference),
            Row("Customer",  b.CustomerName),
            Row("Phone",     b.CustomerPhone),
            Row("Email",     b.CustomerEmail),
            Row("Service",   b.ServiceName),
            Row("When",      when)
        };
        if (!string.IsNullOrWhiteSpace(b.BikeDescription)) rows.Add(Row("Bike", b.BikeDescription));
        if (!string.IsNullOrWhiteSpace(b.Notes))           rows.Add(Row("Notes", b.Notes));

        var html = Shell(heading, Table([.. rows]));
        var text = $"{heading}\n\nRef: {b.Reference}\nCustomer: {b.CustomerName}\n" +
                   $"Phone: {b.CustomerPhone}\nEmail: {b.CustomerEmail}\n" +
                   $"Service: {b.ServiceName}\nWhen: {when}\n" +
                   (string.IsNullOrWhiteSpace(b.BikeDescription) ? "" : $"Bike: {b.BikeDescription}\n") +
                   (string.IsNullOrWhiteSpace(b.Notes) ? "" : $"Notes: {b.Notes}\n");

        return (html, text);
    }

    // ── Contact enquiry (to the shop) ────────────────────────────────────────
    public static (string html, string text) ContactEnquiry(Enquiry e)
    {
        var rows = new List<string>
        {
            Row("From",  e.Name),
            Row("Email", e.Email)
        };
        if (!string.IsNullOrWhiteSpace(e.Phone))           rows.Add(Row("Phone", e.Phone));
        // Service and the preferred date came off the form when it stopped being a
        // second booking flow. Still rendered when present, so the enquiries already
        // stored keep reading the way they were sent.
        if (!string.IsNullOrWhiteSpace(e.Service))         rows.Add(Row("Service", e.Service));
        if (!string.IsNullOrWhiteSpace(e.BikeDescription)) rows.Add(Row("Bike", e.BikeDescription));
        if (e.PreferredDate is { } pref)
            rows.Add(Row("Preferred date", pref.ToString("dddd d MMMM yyyy", CultureInfo.GetCultureInfo("en-GB"))));

        var html = Shell("New website enquiry",
            Table([.. rows]) +
            $"""<div style="margin:16px 0 0;padding:14px 16px;background:#f4f4f5;border-radius:8px;font-size:15px;line-height:1.6;color:{Ink};white-space:pre-wrap;">{E(e.Message)}</div>""");

        var text = $"New website enquiry\n\nFrom: {e.Name}\nEmail: {e.Email}\n" +
                   (string.IsNullOrWhiteSpace(e.Phone) ? "" : $"Phone: {e.Phone}\n") +
                   (string.IsNullOrWhiteSpace(e.Service) ? "" : $"Service: {e.Service}\n") +
                   (string.IsNullOrWhiteSpace(e.BikeDescription) ? "" : $"Bike: {e.BikeDescription}\n") +
                   (e.PreferredDate is { } p ? $"Preferred date: {p:dddd d MMMM yyyy}\n" : "") +
                   $"\n{e.Message}\n";

        return (html, text);
    }
}
