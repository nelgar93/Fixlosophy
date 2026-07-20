namespace Fixlosophy.Services;

public class Customer
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Email { get; set; } = "";
    public string FullName { get; set; } = "";
    public string Phone { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    // Email verification — new accounts start unverified; pre-existing rows are
    // grandfathered as verified via the DB column default (see EnsureSchema). The
    // verification token itself lives in Redis (see IVerificationTokenStore), not here.
    public bool EmailConfirmed { get; set; }

    // Forgot-password. ResetTokenExpiresAt governs link validity (60 min); the
    // separate ResetCooldownUntil governs how soon a NEW link can be requested
    // (60s) — decoupled so a genuinely lost email can be retried quickly instead
    // of being blocked for the link's whole validity window.
    public string? ResetTokenHash { get; set; }
    public DateTime? ResetTokenExpiresAt { get; set; }
    public DateTime? ResetCooldownUntil { get; set; }

    // Navigation
    public List<Booking> Bookings { get; set; } = [];
    public List<Bike> Bikes { get; set; } = [];
}

public class Bike
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string CustomerId { get; set; } = "";
    public string MakeModel { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    // Navigation
    public Customer? Customer { get; set; }
}

public enum StaffRole { Admin = 0, Worker = 1 }

public class StaffMember
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string FullName { get; set; } = "";
    public string Email { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public StaffRole Role { get; set; } = StaffRole.Worker;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    // Permissions — only evaluated for Workers; Admins always have full access
    public bool CanViewAllBookings { get; set; }
    public bool CanManageBookings { get; set; } = true;
    public bool CanViewCustomerDetails { get; set; }

    // Forgot-password — see Customer.ResetCooldownUntil for why this is separate
    // from ResetTokenExpiresAt.
    public string? ResetTokenHash { get; set; }
    public DateTime? ResetTokenExpiresAt { get; set; }
    public DateTime? ResetCooldownUntil { get; set; }

    // Navigation
    public List<Booking> Bookings { get; set; } = [];
}
