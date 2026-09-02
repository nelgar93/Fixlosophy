using Fixlosophy.Data;
using Microsoft.EntityFrameworkCore;

namespace Fixlosophy.Services;

/// <summary>
/// Reads for the admin's Customers tab, and the notes staff write about people.
///
/// Kept separate from AuthService (which is about signing in and account lifecycle)
/// because this is the shop looking outward at its customers rather than a customer
/// acting on their own account.
/// </summary>
public class CustomerService(AppDbContext db)
{
    public const int PageSize = 25;
    public const int MaxNoteLength = 2000;

    // ── Customer list ────────────────────────────────────────────────────────

    /// <summary>
    /// One page of customers with their booking summary, filtered in SQL.
    ///
    /// Follows the shape of BookingService.GetBookingsPage rather than inventing a
    /// second paging convention: search in the query, Skip/Take, and an ordering with
    /// a tie-breaker so a row can't appear on two pages.
    /// </summary>
    public (List<CustomerSummary> items, int total) GetCustomersPage(string? search, int page, int pageSize = PageSize)
    {
        var query = db.Customers.AsQueryable();

        if (!string.IsNullOrWhiteSpace(search))
        {
            var term = search.Trim().ToLowerInvariant();
            // ToLower() is translated to SQL lower(...) by EF Core — the analyzer's
            // suggested StringComparison overload isn't SQL-translatable and would throw.
#pragma warning disable CA1304, CA1311, CA1862
            query = query.Where(c =>
                c.FullName.ToLower().Contains(term) ||
                c.Email.ToLower().Contains(term) ||
                c.Phone.Contains(term));
#pragma warning restore CA1304, CA1311, CA1862
        }

        var total = query.Count();

        var items = query
            .OrderBy(c => c.FullName)
            .ThenBy(c => c.Id)
            .Skip(Math.Max(0, page) * pageSize)
            .Take(pageSize)
            .Select(c => new CustomerSummary(
                c.Id,
                c.FullName,
                c.Email,
                c.Phone,
                c.CreatedAt,
                db.Bookings.Count(b => b.CustomerId == c.Id),
                db.Bookings.Where(b => b.CustomerId == c.Id)
                           .OrderByDescending(b => b.SlotDate)
                           .Select(b => (DateTime?)b.SlotDate)
                           .FirstOrDefault()))
            .ToList();

        return (items, total);
    }

    // ── Customer detail ──────────────────────────────────────────────────────

    /// <summary>
    /// Everything the shop knows about one customer, for the detail view.
    ///
    /// Includes bookings made as a guest under the same email that were never linked
    /// to the account. Those exist today and are invisible in the admin — a customer
    /// who booked before registering looks like a stranger without this.
    /// </summary>
    public CustomerDetail? GetCustomerDetail(string customerId)
    {
        var customer = db.Customers
            .Include(c => c.Bikes.OrderBy(b => b.CreatedAt))
            .AsNoTracking()
            .FirstOrDefault(c => c.Id == customerId);
        if (customer is null) return null;

        var linked = db.Bookings
            .Where(b => b.CustomerId == customerId)
            .AsNoTracking()
            .OrderByDescending(b => b.SlotDate).ThenByDescending(b => b.SlotTime)
            .ToList();

        var normEmail = AuthService.NormalizeEmail(customer.Email);
#pragma warning disable CA1304, CA1311, CA1862 // SQL-translated by EF Core; see above.
        var unlinked = db.Bookings
            .Where(b => b.CustomerId == null && b.CustomerEmail.ToLower() == normEmail)
            .AsNoTracking()
            .OrderByDescending(b => b.SlotDate).ThenByDescending(b => b.SlotTime)
            .ToList();
#pragma warning restore CA1304, CA1311, CA1862

        // Notes are split by what they are about, because the detail panel shows each
        // job's notes on the job itself rather than as one undifferentiated list.
        //
        // Keyed across the unlinked guest bookings as well as the linked ones: a note
        // written against a guest booking has no CustomerId, so GetNotesForCustomer
        // never sees it and it was stored and then invisible here.
        var bookingIds = linked.Select(b => b.Id).Concat(unlinked.Select(b => b.Id)).ToList();
        var bookingNotes = db.CustomerNotes
            .Where(n => n.BookingId != null && bookingIds.Contains(n.BookingId))
            .AsNoTracking()
            .OrderByDescending(n => n.CreatedAt)
            // In memory for the same reason as GetCustomerVisibleNotesByBooking below:
            // a GroupBy projecting to a collection per key has no SQL translation.
            .ToList()
            .GroupBy(n => n.BookingId!)
            .ToDictionary(g => g.Key, g => g.ToList());

        // What's left is about the customer rather than any one job.
        var generalNotes = GetNotesForCustomer(customerId).Where(n => n.BookingId is null).ToList();

        // Lifetime spend counts completed work only: a pending or cancelled booking
        // isn't money, and the price on a booking is an estimate until the job is done.
        var completed = linked.Where(b => b.Status == BookingStatus.Completed).ToList();

        return new CustomerDetail(
            Customer: customer,
            Bookings: linked,
            UnlinkedGuestBookings: unlinked,
            GeneralNotes: generalNotes,
            BookingNotes: bookingNotes,
            CompletedCount: completed.Count,
            LifetimeSpend: completed.Sum(b => b.ServicePrice),
            FirstVisit: linked.Count > 0 ? linked.Min(b => b.SlotDate) : null,
            LastVisit: linked.Count > 0 ? linked.Max(b => b.SlotDate) : null);
    }

    // ── Notes ────────────────────────────────────────────────────────────────

    public List<CustomerNote> GetNotesForCustomer(string customerId) =>
        db.CustomerNotes
          .Where(n => n.CustomerId == customerId)
          .AsNoTracking()
          .OrderByDescending(n => n.CreatedAt)
          .ToList();

    /// Notes attached to one booking — including a guest booking's, which have no
    /// CustomerId to find them by.
    public List<CustomerNote> GetNotesForBooking(string bookingId) =>
        db.CustomerNotes
          .Where(n => n.BookingId == bookingId)
          .AsNoTracking()
          .OrderByDescending(n => n.CreatedAt)
          .ToList();

    /// Notes on a customer's bookings that they're allowed to see, keyed by booking.
    /// Used by the account page, which shows each one on the job it came out of.
    ///
    /// Carries CreatedAt as well as the body: the account page heads the note with the
    /// date it was written, which is not the booking's own date — a job finished a day
    /// late would otherwise read as having been reported on the appointment.
    public Dictionary<string, List<SharedNote>> GetCustomerVisibleNotesByBooking(string customerId) =>
        db.CustomerNotes
          .Where(n => n.CustomerId == customerId && n.VisibleToCustomer && n.BookingId != null)
          .AsNoTracking()
          .OrderBy(n => n.CreatedAt)
          // Grouped in memory, not in the query: a GroupBy projecting to a collection
          // per key has no SQL translation and throws at runtime. There are at most a
          // handful of notes per customer, so the round trip is the same either way.
          .ToList()
          .GroupBy(n => n.BookingId!)
          .ToDictionary(g => g.Key, g => g.Select(n => new SharedNote(n.Body, n.CreatedAt)).ToList());

    /// <summary>
    /// Records a note. Returns null when there's nothing to record — an empty note is
    /// a no-op rather than an error, because the completion flow makes it optional.
    /// </summary>
    public CustomerNote? AddNote(
        string? customerId, string? bookingId, string? authorStaffId,
        string body, bool visibleToCustomer)
    {
        var trimmed = (body ?? "").Trim();
        if (trimmed.Length == 0) return null;
        if (trimmed.Length > MaxNoteLength) trimmed = trimmed[..MaxNoteLength];

        var note = new CustomerNote
        {
            CustomerId = customerId,
            BookingId = bookingId,
            AuthorStaffId = authorStaffId,
            Body = trimmed,
            VisibleToCustomer = visibleToCustomer
        };
        db.CustomerNotes.Add(note);
        db.SaveChanges();
        return note;
    }

    public bool DeleteNote(string noteId)
    {
        var note = db.CustomerNotes.Find(noteId);
        if (note is null) return false;
        db.CustomerNotes.Remove(note);
        db.SaveChanges();
        return true;
    }
}

/// One note the customer is allowed to see, as their account page needs it.
public sealed record SharedNote(string Body, DateTime CreatedAt);

/// A row in the admin's customer list.
public sealed record CustomerSummary(
    string Id,
    string FullName,
    string Email,
    string Phone,
    DateTime MemberSince,
    int BookingCount,
    DateTime? LastVisit);

/// Everything on one customer's detail page.
public sealed record CustomerDetail(
    Customer Customer,
    List<Booking> Bookings,
    List<Booking> UnlinkedGuestBookings,
    /// Notes about the customer rather than any one job.
    List<CustomerNote> GeneralNotes,
    /// BookingId -> the notes written on that job, newest first. Covers the unlinked
    /// guest bookings too.
    Dictionary<string, List<CustomerNote>> BookingNotes,
    int CompletedCount,
    decimal LifetimeSpend,
    DateTime? FirstVisit,
    DateTime? LastVisit);
