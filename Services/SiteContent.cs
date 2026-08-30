namespace Fixlosophy.Services;

/// <summary>
/// One day's trading hours. <c>null</c> anywhere an <see cref="OpeningHours"/> is
/// expected means closed that day.
/// </summary>
/// <param name="Open">First minute the shop is open.</param>
/// <param name="Close">Closing time — no appointment starts at or after this.</param>
public sealed record OpeningHours(TimeOnly Open, TimeOnly Close);

/// <summary>
/// Every fact about the business that appears on the site: address, contact details,
/// trading hours, team, and the Google review figures.
///
/// These used to be typed directly into the markup, which meant the phone number lived
/// in three files and the address in four — and they had drifted apart. Keeping them
/// here means a change happens once, and the booking slots, the footer, the Contact
/// page and the structured data can never disagree about when the shop is open.
/// </summary>
public static class SiteContent
{
    public const string ShopName  = "Fixlosophy";
    public const string LegalName = "Fixlosophy Ltd";

    // ── Address ──────────────────────────────────────────────────────────────
    public const string AddressVenue    = "Blue House Yard";
    public const string AddressStreet   = "5 River Park Rd";
    public const string AddressCity     = "London";
    public const string AddressPostcode = "N22 7TB";
    public const string AddressCountry  = "United Kingdom";

    /// Short form for tight spaces (footer, booking confirmation card).
    public const string AddressShort = $"{AddressVenue}, {AddressStreet}";

    /// Full single-line form, for email bodies and link titles.
    public const string AddressOneLine =
        $"{AddressVenue}, {AddressStreet}, {AddressCity} {AddressPostcode}";

    // Google Maps pin, used for the LocalBusiness structured data.
    public const double Latitude  = 51.5971815;
    public const double Longitude = -0.1116462;

    // ── Contact ──────────────────────────────────────────────────────────────
    /// Dialable form for tel:/WhatsApp links. Must stay E.164 — no spaces.
    public const string PhoneE164 = "+447874004100";

    /// How the number is written on screen. Same number as <see cref="PhoneE164"/>.
    public const string PhoneDisplay = "07874 004100";

    public const string Email = "fixlosophy@gmail.com";

    public const string InstagramHandle = "@fixlosophy";
    public const string InstagramUrl    = "https://instagram.com/fixlosophy";

    // ── Reviews ──────────────────────────────────────────────────────────────
    // Shown as an aggregate with a link to the real profile rather than reproduced
    // as testimonial cards: review text belongs to the people who wrote it, and a
    // hardcoded copy goes stale silently. Update these two figures when they move.
    public const string GoogleProfileUrl   = "https://maps.app.goo.gl/3r6QQQmvFnrN8c216";
    public const decimal GoogleRating      = 4.9m;
    public const int    GoogleReviewCount  = 229;

    // ── Team ─────────────────────────────────────────────────────────────────
    public static readonly string[] Team = ["Francesco", "Janek"];

    // ── Trading hours ────────────────────────────────────────────────────────
    // Open seven days. These drive BookingService.SlotsFor, so editing them changes
    // which appointments customers can actually book — not just the displayed text.
    private static readonly OpeningHours Weekday = new(new(9, 0), new(19, 0));
    private static readonly OpeningHours Saturday = new(new(9, 0), new(18, 0));
    private static readonly OpeningHours Sunday = new(new(11, 0), new(17, 0));

    /// The hour kept free for lunch — no appointment starts here.
    public static readonly TimeOnly LunchStart = new(13, 0);

    /// Trading hours for a given day, or null if the shop is closed that day.
    public static OpeningHours? HoursFor(DayOfWeek day) => day switch
    {
        DayOfWeek.Saturday => Saturday,
        DayOfWeek.Sunday   => Sunday,
        _                  => Weekday
    };

    /// Human-readable summary, grouped the way it's written on the window.
    public static readonly string[] HoursDisplay =
    [
        "Monday – Friday: 9am – 7pm",
        "Saturday: 9am – 6pm",
        "Sunday: 11am – 5pm"
    ];

    /// schema.org openingHours strings for the LocalBusiness JSON-LD.
    public static readonly string[] HoursSchemaOrg =
    [
        "Mo-Fr 09:00-19:00",
        "Sa 09:00-18:00",
        "Su 11:00-17:00"
    ];
}
