namespace Fixlosophy.Services;

public enum BookingStatus { Pending, Confirmed, InProgress, Completed, Cancelled }

public class Booking
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Reference { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public string CustomerName { get; set; } = "";
    public string CustomerEmail { get; set; } = "";
    public string CustomerPhone { get; set; } = "";
    public string ServiceCategory { get; set; } = "";
    public string ServiceName { get; set; } = "";
    public decimal ServicePrice { get; set; }
    public DateTime SlotDate { get; set; }
    public string SlotTime { get; set; } = "";
    public string BikeDescription { get; set; } = "";
    public string Notes { get; set; } = "";
    public BookingStatus Status { get; set; } = BookingStatus.Pending;
    public string? CustomerId { get; set; }
    public string? AssignedStaffId { get; set; }

    // Navigation
    public Customer? Customer { get; set; }
    public StaffMember? AssignedStaff { get; set; }
    public List<BookingPhoto> Photos { get; set; } = [];
}

public class BookingPhoto
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string BookingId { get; set; } = "";
    // Path within the Supabase Storage bucket (Fixlosophy_Customers_Uploads/...),
    // not a URL — the folder is private, so viewing a photo requires minting a
    // signed URL server-side (see StorageService.GetSignedPhotoUrlAsync).
    public string StoragePath { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    // Navigation
    public Booking? Booking { get; set; }
}

public class ServiceOption
{
    public string Category { get; set; } = "";
    public string Name { get; set; } = "";
    public decimal PriceFrom { get; set; }
    public string Duration { get; set; } = "";
    public string Icon { get; set; } = "";
}
