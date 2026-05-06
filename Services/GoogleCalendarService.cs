using Google.Apis.Auth.OAuth2;
using Google.Apis.Calendar.v3;
using Google.Apis.Calendar.v3.Data;
using Google.Apis.Services;

namespace Fixlosophy.Services;

public class GoogleCalendarService
{
    private readonly CalendarService? _cal;
    private readonly string? _calendarId;

    public bool IsEnabled => _cal != null && !string.IsNullOrWhiteSpace(_calendarId);

    public GoogleCalendarService(IConfiguration config)
    {
        var calendarId = config["GoogleCalendar:CalendarId"];
        var keyPath    = config["GoogleCalendar:ServiceAccountKeyPath"];

        if (string.IsNullOrWhiteSpace(calendarId) || string.IsNullOrWhiteSpace(keyPath))
            return;

        if (!File.Exists(keyPath))
            return;

        try
        {
            var credential = GoogleCredential
                .FromFile(keyPath)
                .CreateScoped(CalendarService.Scope.Calendar);

            _cal = new CalendarService(new BaseClientService.Initializer
            {
                HttpClientInitializer = credential,
                ApplicationName       = "Fixlosophy"
            });
            _calendarId = calendarId;
        }
        catch { /* invalid credentials — calendar sync silently disabled */ }
    }

    public async Task<string?> CreateEventAsync(Booking b)
    {
        if (!IsEnabled) return null;
        try
        {
            var result = await _cal!.Events.Insert(BuildEvent(b), _calendarId!).ExecuteAsync();
            return result.Id;
        }
        catch { return null; }
    }

    public async Task UpdateEventAsync(string eventId, Booking b)
    {
        if (!IsEnabled) return;
        try { await _cal!.Events.Update(BuildEvent(b), _calendarId!, eventId).ExecuteAsync(); }
        catch { }
    }

    private static Event BuildEvent(Booking b)
    {
        var start = SlotStart(b);
        return new Event
        {
            Summary     = $"{b.ServiceName} — {b.CustomerName}",
            Description = BuildDescription(b),
            Start       = new EventDateTime { DateTime = start, TimeZone = "Europe/London" },
            End         = new EventDateTime { DateTime = start.AddHours(2), TimeZone = "Europe/London" },
            ColorId     = StatusToColor(b.Status)
        };
    }

    private static DateTime SlotStart(Booking b)
    {
        TimeOnly.TryParse(b.SlotTime, out var t);
        return b.SlotDate.Date.Add(t.ToTimeSpan());
    }

    private static string BuildDescription(Booking b)
    {
        var sb = new System.Text.StringBuilder();
        sb.AppendLine($"Ref: {b.Reference}");
        sb.AppendLine($"Status: {b.Status}");
        sb.AppendLine($"Email: {b.CustomerEmail}");
        if (!string.IsNullOrWhiteSpace(b.CustomerPhone))   sb.AppendLine($"Phone: {b.CustomerPhone}");
        if (!string.IsNullOrWhiteSpace(b.BikeDescription)) sb.AppendLine($"Bike: {b.BikeDescription}");
        if (!string.IsNullOrWhiteSpace(b.Notes))           sb.AppendLine($"Notes: {b.Notes}");
        sb.Append($"Price: £{b.ServicePrice}");
        return sb.ToString();
    }

    // Google Calendar event colour IDs
    private static string StatusToColor(BookingStatus s) => s switch
    {
        BookingStatus.Confirmed  => "2",   // Sage (green)
        BookingStatus.InProgress => "9",   // Blueberry
        BookingStatus.Completed  => "8",   // Graphite
        BookingStatus.Cancelled  => "11",  // Tomato
        _                        => "5"    // Banana (yellow) — Pending
    };
}
