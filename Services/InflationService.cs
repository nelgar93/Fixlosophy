using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Fixlosophy.Services;

public class InflationService(IHttpClientFactory httpFactory)
{
    // ONS CPIH All-Items index (L55O). We calculate the YoY % change from
    // consecutive annual index values — this gives us the true calendar-year rate.
    private const string OnsUrl =
        "https://api.ons.gov.uk/v1/timeseries/L55O/dataset/cpih01/data";

    public async Task<decimal?> GetLatestAnnualRateAsync()
    {
        try
        {
            var client = httpFactory.CreateClient();
            client.Timeout = TimeSpan.FromSeconds(10);

            var resp = await client.GetFromJsonAsync<OnsResponse>(OnsUrl);

            var years = (resp?.Years ?? [])
                .Where(y => int.TryParse(y.Date, out _) && decimal.TryParse(y.Value, out _))
                .OrderByDescending(y => int.Parse(y.Date!))
                .Take(2)
                .ToList();

            if (years.Count == 2)
            {
                var current  = decimal.Parse(years[0].Value!);
                var previous = decimal.Parse(years[1].Value!);
                if (previous > 0)
                    return Math.Round((current - previous) / previous, 4);
            }
        }
        catch { /* ONS API unavailable — caller falls back to configured minimum */ }

        return null;
    }

    private sealed record OnsResponse(
        [property: JsonPropertyName("years")] OnsDataPoint[]? Years);

    private sealed record OnsDataPoint(
        [property: JsonPropertyName("date")]  string? Date,
        [property: JsonPropertyName("value")] string? Value);
}
