using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json.Serialization;

namespace Fixlosophy.Services;

// Talks to the Supabase Storage REST API directly over HttpClient (same style as
// InflationService's external API call) rather than the supabase-csharp SDK, whose
// compatibility with this project's net10.0 target is unverified.
public class StorageService(IHttpClientFactory httpFactory, IConfiguration config, ILogger<StorageService> logger) : IStorageService
{
    public const long MaxFileSizeBytes = 8 * 1024 * 1024;

    private static readonly Dictionary<string, string> AllowedImageTypes = new()
    {
        ["image/jpeg"] = "jpg",
        ["image/png"]  = "png",
        ["image/webp"] = "webp",
        ["image/heic"] = "heic",
    };

    private const string CustomerUploadsFolder = "Fixlosophy_Customers_Uploads";

    // Storage isn't touched on every page load (unlike the DB checks in
    // Program.cs), so config is validated lazily here on first real use rather than
    // failing app startup — a dev working on unrelated pages without storage
    // configured shouldn't be blocked from running the app at all.
    private string BaseUrl        => (config["Supabase:Url"] ?? throw MissingConfig("Supabase:Url")).TrimEnd('/');
    private string ServiceRoleKey => config["Supabase:ServiceRoleKey"] ?? throw MissingConfig("Supabase:ServiceRoleKey");
    private string Bucket         => config["Supabase:Bucket"] ?? "Fixlosophy_N1_Bucket";

    // A *separate* bucket, not a folder: Supabase's /object/public/ route is gated
    // by the bucket's own public flag and ignores RLS entirely, so a single private
    // bucket can't selectively expose one folder that way. This bucket holds only
    // public site imagery; Fixlosophy_N1_Bucket (private) holds customer uploads.
    private string WebsiteImagesBucket => config["Supabase:WebsiteImagesBucket"] ?? "Fixlosophy_Website_Images";

    private static InvalidOperationException MissingConfig(string key) =>
        new($"{key} is not configured. Set it in appsettings.Local.json before using Supabase Storage.");

    // Shared by Book.razor (fast per-file feedback at selection time) and
    // UploadCustomerPhotoAsync (defense in depth) so the rules only live in one place.
    public string? ValidatePhoto(string contentType, long size)
    {
        if (!AllowedImageTypes.ContainsKey(contentType))
            return "Only JPEG, PNG, WEBP or HEIC photos are supported.";
        if (size > MaxFileSizeBytes)
            return "Each photo must be 8 MB or smaller.";
        return null;
    }

    // Uploads into the private Fixlosophy_Customers_Uploads folder, under the
    // booking's own id. The filename is never trusted for the storage path — a
    // fresh guid plus an extension derived from the allowlisted content-type is
    // used instead, so it can't be used to escape the intended folder.
    public async Task<(string? path, string? error)> UploadCustomerPhotoAsync(
        string bookingId, string contentType, byte[] content)
    {
        var validationError = ValidatePhoto(contentType, content.LongLength);
        if (validationError is not null)
            return (null, validationError);

        var ext  = AllowedImageTypes[contentType];
        var path = $"{CustomerUploadsFolder}/{bookingId}/{Guid.NewGuid()}.{ext}";

        try
        {
            var client = httpFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/storage/v1/object/{Bucket}/{path}")
            {
                Content = new ByteArrayContent(content)
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(contentType);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ServiceRoleKey);
            request.Headers.Add("apikey", ServiceRoleKey);

            var response = await client.SendAsync(request);
            if (!response.IsSuccessStatusCode)
            {
                logger.LogWarning("Supabase Storage upload failed with {Status}: {Body}",
                    response.StatusCode, await response.Content.ReadAsStringAsync());
                return (null, "Could not upload photo — please try again.");
            }
            return (path, null);
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Supabase Storage upload failed for booking {BookingId}", bookingId);
            return (null, "Could not upload photo — please try again.");
        }
    }

    // Customer uploads live in a private folder (no anon read policy — see
    // Program.cs EnsureSchema), so viewing one requires a short-lived signed URL
    // minted server-side with the service-role key.
    public async Task<string?> GetSignedPhotoUrlAsync(string storagePath, TimeSpan expiry)
    {
        try
        {
            var client = httpFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/storage/v1/object/sign/{Bucket}/{storagePath}")
            {
                Content = JsonContent.Create(new { expiresIn = (int)expiry.TotalSeconds })
            };
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ServiceRoleKey);
            request.Headers.Add("apikey", ServiceRoleKey);

            var response = await client.SendAsync(request);
            if (!response.IsSuccessStatusCode) return null;

            var body = await response.Content.ReadFromJsonAsync<SignedUrlResponse>();
            return body?.SignedURL is null ? null : $"{BaseUrl}/storage/v1{body.SignedURL}";
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Could not sign Supabase Storage URL for {Path}", storagePath);
            return null;
        }
    }

    // Best-effort — the caller (BookingService.DeleteBooking) logs and moves on if
    // this fails rather than blocking the booking delete on a storage hiccup.
    public async Task<bool> DeleteAsync(string storagePath)
    {
        try
        {
            var client = httpFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Delete, $"{BaseUrl}/storage/v1/object/{Bucket}/{storagePath}");
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", ServiceRoleKey);
            request.Headers.Add("apikey", ServiceRoleKey);

            var response = await client.SendAsync(request);
            return response.IsSuccessStatusCode;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException)
        {
            logger.LogWarning(ex, "Could not delete Supabase Storage object {Path}", storagePath);
            return false;
        }
    }

    // Pure string build, no HTTP call — WebsiteImagesBucket is a public bucket, so
    // the URL just works directly. For the logo/favicon specifically, see
    // SupabaseImageUrls instead: those render on every page load and shouldn't
    // depend on Supabase:Url being configured.
    public string GetPublicWebsiteImageUrl(string fileName) =>
        $"{BaseUrl}/storage/v1/object/public/{WebsiteImagesBucket}/{fileName}";

    private sealed record SignedUrlResponse(
        [property: JsonPropertyName("signedURL")] string? SignedURL);
}
