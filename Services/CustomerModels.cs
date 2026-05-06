namespace Fixlosophy.Services;

public class Customer
{
    public string Id { get; set; } = Guid.NewGuid().ToString();
    public string Email { get; set; } = "";
    public string FullName { get; set; } = "";
    public string Phone { get; set; } = "";
    public string PasswordHash { get; set; } = "";
    public DateTime CreatedAt { get; set; } = DateTime.Now;

    // Navigation
    public List<Booking> Bookings { get; set; } = [];
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

    // Navigation
    public List<Booking> Bookings { get; set; } = [];
}
