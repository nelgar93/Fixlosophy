using Fixlosophy.Services;

namespace Fixlosophy.Tests;

// The upload used to trust the content type the browser declared, which is
// attacker-controlled: anything at all could be stored, and stored *labelled* as an
// image, because the declared type was also what Supabase served it back as. These
// cover the byte-level check that replaced that trust.
public class StorageServiceTests
{
    // Real signatures, padded out to a plausible length. Only the leading bytes are
    // examined, so the padding is arbitrary.
    private static byte[] WithHeader(params byte[] header) =>
        [.. header, .. new byte[32]];

    private static byte[] Jpeg() => WithHeader(0xFF, 0xD8, 0xFF, 0xE0);
    private static byte[] Png()  => WithHeader(0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A);

    // RIFF containers put a 4-byte chunk length between the two markers.
    private static byte[] Webp() =>
        [.. "RIFF"u8.ToArray(), 0x24, 0x00, 0x00, 0x00, .. "WEBP"u8.ToArray(), .. new byte[16]];

    // ISO base-media: 4-byte box size, "ftyp", then the major brand.
    private static byte[] Iso(string brand) =>
        [0x00, 0x00, 0x00, 0x18, .. "ftyp"u8.ToArray(), .. System.Text.Encoding.ASCII.GetBytes(brand), .. new byte[16]];

    [Fact]
    public void SniffImageType_RecognisesJpeg() =>
        Assert.Equal("image/jpeg", StorageService.SniffImageType(Jpeg()));

    [Fact]
    public void SniffImageType_RecognisesPng() =>
        Assert.Equal("image/png", StorageService.SniffImageType(Png()));

    [Fact]
    public void SniffImageType_RecognisesWebp() =>
        Assert.Equal("image/webp", StorageService.SniffImageType(Webp()));

    [Theory]
    [InlineData("heic")]
    [InlineData("heix")]
    [InlineData("mif1")]  // the generic still-image brand iOS also emits
    [InlineData("msf1")]
    public void SniffImageType_RecognisesHeifBrands(string brand) =>
        Assert.Equal("image/heic", StorageService.SniffImageType(Iso(brand)));

    // An MP4 is also an ISO base-media file with an ftyp box, so the brand check is
    // what stops "has ftyp" being read as "is a photo".
    [Theory]
    [InlineData("isom")]
    [InlineData("mp42")]
    [InlineData("qt  ")]
    public void SniffImageType_RejectsOtherIsoContainers(string brand) =>
        Assert.Null(StorageService.SniffImageType(Iso(brand)));

    // The case the whole check exists for: an HTML file posted with a JPEG label.
    [Fact]
    public void SniffImageType_RejectsHtmlDressedAsAnImage() =>
        Assert.Null(StorageService.SniffImageType("<html><script>alert(1)</script>"u8.ToArray()));

    [Fact]
    public void SniffImageType_RejectsAZipOrOfficeFile() =>
        Assert.Null(StorageService.SniffImageType(WithHeader(0x50, 0x4B, 0x03, 0x04)));

    [Fact]
    public void SniffImageType_RejectsAnExecutable() =>
        Assert.Null(StorageService.SniffImageType(WithHeader(0x4D, 0x5A)));

    // A RIFF file that isn't WEBP — a WAV, for instance — must not pass.
    [Fact]
    public void SniffImageType_RejectsNonWebpRiffContainers()
    {
        byte[] wav = [.. "RIFF"u8.ToArray(), 0x24, 0x00, 0x00, 0x00, .. "WAVE"u8.ToArray(), .. new byte[16]];
        Assert.Null(StorageService.SniffImageType(wav));
    }

    // Truncated input must return null rather than reading off the end.
    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    [InlineData(6)]
    [InlineData(11)]
    public void SniffImageType_HandlesShortInputWithoutThrowing(int length) =>
        Assert.Null(StorageService.SniffImageType(new byte[length]));

    // ── The declared-type pre-check is unchanged ─────────────────────────────
    // It still runs at file-selection time in Book.razor, where the bytes haven't been
    // read yet and only the browser's claim and the size are available.

    [Fact]
    public void ValidatePhoto_RejectsATypeWeDontAccept() =>
        Assert.NotNull(NewService().ValidatePhoto("application/pdf", 1024));

    [Fact]
    public void ValidatePhoto_RejectsAnOversizeFile() =>
        Assert.NotNull(NewService().ValidatePhoto("image/jpeg", StorageService.MaxFileSizeBytes + 1));

    [Fact]
    public void ValidatePhoto_AcceptsAnOrdinaryPhoto() =>
        Assert.Null(NewService().ValidatePhoto("image/jpeg", 2 * 1024 * 1024));

    private static StorageService NewService() =>
        new(new UnusedHttpClientFactory(),
            new Microsoft.Extensions.Configuration.ConfigurationBuilder().Build(),
            Microsoft.Extensions.Logging.Abstractions.NullLogger<StorageService>.Instance);

    // ValidatePhoto makes no network call, so the factory is never used.
    private sealed class UnusedHttpClientFactory : IHttpClientFactory
    {
        public HttpClient CreateClient(string name) => throw new NotSupportedException();
    }
}
