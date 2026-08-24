using Fixlosophy.Services;
using Microsoft.EntityFrameworkCore;

namespace Fixlosophy.Data;

public class AppDbContext(DbContextOptions<AppDbContext> options) : DbContext(options)
{
    public DbSet<Booking> Bookings => Set<Booking>();
    public DbSet<Customer> Customers => Set<Customer>();
    public DbSet<StaffMember> Staff => Set<StaffMember>();
    public DbSet<ServicePricing> ServicePricings => Set<ServicePricing>();
    public DbSet<PriceAdjustment> PriceAdjustments => Set<PriceAdjustment>();
    public DbSet<BookingPhoto> BookingPhotos => Set<BookingPhoto>();
    public DbSet<Bike> Bikes => Set<Bike>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.Entity<Booking>(b =>
        {
            b.HasKey(e => e.Id);
            b.Property(e => e.Id).HasMaxLength(36).ValueGeneratedNever();
            b.Property(e => e.ServicePrice).HasPrecision(18, 2);
            b.Property(e => e.CustomerId).HasMaxLength(36);
            b.Property(e => e.AssignedStaffId).HasMaxLength(36);

            b.HasOne(e => e.Customer)
             .WithMany(c => c.Bookings)
             .HasForeignKey(e => e.CustomerId)
             .OnDelete(DeleteBehavior.SetNull);

            b.HasOne(e => e.AssignedStaff)
             .WithMany(s => s.Bookings)
             .HasForeignKey(e => e.AssignedStaffId)
             .OnDelete(DeleteBehavior.SetNull);
        });

        modelBuilder.Entity<BookingPhoto>(p =>
        {
            p.HasKey(e => e.Id);
            p.Property(e => e.Id).HasMaxLength(36).ValueGeneratedNever();
            p.Property(e => e.BookingId).HasMaxLength(36);

            p.HasOne(e => e.Booking)
             .WithMany(b => b.Photos)
             .HasForeignKey(e => e.BookingId)
             .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<Customer>(c =>
        {
            c.HasKey(e => e.Id);
            c.Property(e => e.Id).HasMaxLength(36).ValueGeneratedNever();
            c.HasIndex(e => e.Email).IsUnique();
        });

        modelBuilder.Entity<Bike>(bk =>
        {
            bk.HasKey(e => e.Id);
            bk.Property(e => e.Id).HasMaxLength(36).ValueGeneratedNever();
            bk.Property(e => e.CustomerId).HasMaxLength(36);
            bk.HasIndex(e => new { e.CustomerId, e.MakeModel }).IsUnique();

            bk.HasOne(e => e.Customer)
              .WithMany(c => c.Bikes)
              .HasForeignKey(e => e.CustomerId)
              .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<StaffMember>(s =>
        {
            s.HasKey(e => e.Id);
            s.Property(e => e.Id).HasMaxLength(36).ValueGeneratedNever();
            s.HasIndex(e => e.Email).IsUnique();
        });

        modelBuilder.Entity<ServicePricing>(sp =>
        {
            sp.HasKey(e => e.Id);
            sp.Property(e => e.Id).HasMaxLength(36).ValueGeneratedNever();
            sp.Property(e => e.CurrentPrice).HasPrecision(18, 2);
        });

        modelBuilder.Entity<PriceAdjustment>(pa =>
        {
            pa.HasKey(e => e.Id);
            pa.Property(e => e.Id).ValueGeneratedOnAdd();
            pa.Property(e => e.Rate).HasPrecision(8, 4);
        });
    }
}
