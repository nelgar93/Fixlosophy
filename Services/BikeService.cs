using Fixlosophy.Data;
using Microsoft.EntityFrameworkCore;

namespace Fixlosophy.Services;

public class BikeService(AppDbContext db)
{
    public const int MaxBikesPerCustomer = 10;
    public const int MaxBikeNameLength = 100;

    public List<Bike> GetBikesForCustomer(string customerId) =>
        db.Bikes.Where(b => b.CustomerId == customerId).OrderBy(b => b.CreatedAt).ToList();

    public (Bike? bike, string? error) AddBike(string customerId, string makeModel)
    {
        var trimmed = (makeModel ?? "").Trim();
        if (trimmed.Length == 0)
            return (null, "Please enter a make & model.");
        if (trimmed.Length > MaxBikeNameLength)
            return (null, $"Keep it under {MaxBikeNameLength} characters.");

        if (db.Bikes.Count(b => b.CustomerId == customerId) >= MaxBikesPerCustomer)
            return (null, $"You can save up to {MaxBikesPerCustomer} bikes — remove one first.");

        var normalized = trimmed.ToLowerInvariant();
        // ToLower() below is translated to SQL lower(...) by EF Core — the analyzer's
        // suggested StringComparison overload isn't SQL-translatable and would throw.
#pragma warning disable CA1304, CA1311, CA1862
        var isDuplicate = db.Bikes.Any(b => b.CustomerId == customerId && b.MakeModel.ToLower() == normalized);
#pragma warning restore CA1304, CA1311, CA1862
        if (isDuplicate)
            return (null, "You've already saved a bike with that name.");

        var bike = new Bike { CustomerId = customerId, MakeModel = trimmed };
        db.Bikes.Add(bike);
        try
        {
            db.SaveChanges();
        }
        catch (DbUpdateException)
        {
            // Lost a race against IX_Bikes_CustomerId_MakeModel; detach so the
            // circuit-scoped context stays usable.
            db.Entry(bike).State = EntityState.Detached;
            return (null, "You've already saved a bike with that name.");
        }
        return (bike, null);
    }

    // customerId is part of the WHERE clause, not just bikeId, so one customer
    // can never remove another's bike by guessing/tampering with an id.
    public bool RemoveBike(string customerId, string bikeId)
    {
        var bike = db.Bikes.FirstOrDefault(b => b.Id == bikeId && b.CustomerId == customerId);
        if (bike is null) return false;
        db.Bikes.Remove(bike);
        db.SaveChanges();
        return true;
    }
}
