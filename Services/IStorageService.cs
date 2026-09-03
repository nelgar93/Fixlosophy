namespace Fixlosophy.Services;

// Abstraction over StorageService so consumers (BookingService, and tests) don't
// depend on the concrete HttpClient-based implementation — mirrors IEmailSender,
// the existing pattern for swappable/testable services.
public interface IStorageService
{
    string? ValidatePhoto(string contentType, long size);
    Task<(string? path, string? error)> UploadCustomerPhotoAsync(string bookingId, string contentType, byte[] content);
    Task<string?> GetSignedPhotoUrlAsync(string storagePath, TimeSpan expiry);
    Task<bool> DeleteAsync(string storagePath);
    string GetPublicWebsiteImageUrl(string fileName);
}
