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
    //
    // This checks only what's knowable before the bytes arrive: the type the browser
    // claims, and the size. The bytes themselves are checked by SniffImageType at
    // upload time — see UploadCustomerPhotoAsync.
    public string? ValidatePhoto(string contentType, long size)
    {
        if (!AllowedImageTypes.ContainsKey(contentType))
            return "Only JPEG, PNG, WEBP or HEIC photos are supported.";
        if (size > MaxFileSizeBytes)
            return "Each photo must be 8 MB or smaller.";
        return null;
    }

    /// <summary>
    /// The content type the file's own leading bytes say it is, or null if they don't
    /// match any format we accept.
    /// </summary>
    /// <remarks>
    /// <para>The browser-declared content type is attacker-controlled — it's whatever
    /// the multipart part says. Taking it on trust meant a file declared
    /// <c>image/jpeg</c> could hold anything, and would later be handed to staff
    /// through a signed URL <em>labelled</em> as a JPEG, because the declared type is
    /// also what gets stored as the object's Content-Type.</para>
    ///
    /// <para>So the sniffed type wins over the declared one everywhere: it decides
    /// whether the upload is allowed at all, the extension on the stored object, and
    /// the Content-Type sent to Supabase. A genuine photo whose browser mislabelled it
    /// — iOS is inconsistent about HEIC — therefore uploads correctly rather than being
    /// rejected, which the old check couldn't manage either.</para>
    ///
    /// <para>Magic numbers only, deliberately. This is not a decoder and is not trying
    /// to prove the file is a <em>valid</em> image; it's establishing that the bytes
    /// agree with the label, which is the property that was missing.</para>
    /// </remarks>
    public static string? SniffImageType(ReadOnlySpan<byte> content)
    {
        // JPEG: SOI marker, then the start of any segment.
        if (content.Length >= 3 && content[0] == 0xFF && content[1] == 0xD8 && content[2] == 0xFF)
            return "image/jpeg";

        // PNG: the 8-byte signature, including the CRLF/EOF bytes that catch
        // transfers which mangled line endings.
        if (StartsWith(content, [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A]))
            return "image/png";

        // WEBP: a RIFF container whose form type is WEBP. Bytes 4-7 are the chunk
        // length and vary, so the two markers are checked at their own offsets.
        if (content.Length >= 12 &&
            StartsWith(content, "RIFF"u8) &&
            content[8..12].SequenceEqual("WEBP"u8))
            return "image/webp";

        // HEIC: an ISO base-media file whose major brand is one of the HEIF ones.
        // Bytes 0-3 are the box size, so "ftyp" sits at offset 4 and the brand at 8.
        if (content.Length >= 12 && content[4..8].SequenceEqual("ftyp"u8))
        {
            var brand = content[8..12];
            foreach (var known in HeifBrands)
                if (brand.SequenceEqual(known))
                    return "image/heic";
        }

        return null;
    }

    // Major brands that mean "HEIF image" in practice. mif1/msf1 are the generic
    // still-image brands iOS also emits; heic/heix/hevc/hevx are the codec-specific
    // ones. Anything else with an ftyp box is some other ISO-BMFF file — an MP4, say —
    // and must not be accepted as a photo.
    private static readonly byte[][] HeifBrands =
    [
        "heic"u8.ToArray(), "heix"u8.ToArray(), "hevc"u8.ToArray(), "hevx"u8.ToArray(),
        "mif1"u8.ToArray(), "msf1"u8.ToArray(), "heim"u8.ToArray(), "heis"u8.ToArray(),
    ];

    private static bool StartsWith(ReadOnlySpan<byte> content, ReadOnlySpan<byte> prefix) =>
        content.Length >= prefix.Length && content[..prefix.Length].SequenceEqual(prefix);

    // Uploads into the private Fixlosophy_Customers_Uploads folder, under the
    // booking's own id. The filename is never trusted for the storage path — a
    // fresh guid plus an extension derived from the allowlisted content-type is
    // used instead, so it can't be used to escape the intended folder.
    public async Task<(string? path, string? error)> UploadCustomerPhotoAsync(
        string bookingId, string contentType, byte[] content)
    {
        if (content.LongLength > MaxFileSizeBytes)
            return (null, "Each photo must be 8 MB or smaller.");

        // The file's own bytes decide, not the type the browser claimed. Everything
        // downstream — the extension, and the Content-Type Supabase will serve it
        // back with — is derived from this rather than from the declared value.
        var actualType = SniffImageType(content);
        if (actualType is null)
            return (null, "That file doesn't look like a JPEG, PNG, WEBP or HEIC photo.");

        // Not an error — browsers get this wrong honestly, iOS especially, and the
        // sniffed type has already been used in preference. Logged because a sudden
        // run of mismatches is the first sign of either a browser quirk worth knowing
        // about or somebody probing the upload.
        if (actualType != contentType && logger.IsEnabled(LogLevel.Information))
            logger.LogInformation(
                "Upload for booking {BookingId} declared {Declared}; its bytes are {Actual}, which is what was used.",
                bookingId, contentType, actualType);

        var ext  = AllowedImageTypes[actualType];
        var path = $"{CustomerUploadsFolder}/{bookingId}/{Guid.NewGuid()}.{ext}";

        try
        {
            var client = httpFactory.CreateClient();
            using var request = new HttpRequestMessage(HttpMethod.Post, $"{BaseUrl}/storage/v1/object/{Bucket}/{path}")
            {
                Content = new ByteArrayContent(content)
            };
            request.Content.Headers.ContentType = new MediaTypeHeaderValue(actualType);
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
