using Fixlosophy.Services;

namespace Fixlosophy.Components.Admin;

/// <summary>
/// Who is looking at the dashboard, and what they're allowed to do. Cascaded from
/// <c>Admin.razor</c> to every tab.
/// </summary>
/// <remarks>
/// <para>One cascading value rather than four parameters per tab. The permissions are
/// always wanted together and always derived from the same staff record, so passing
/// them separately meant every tab's signature grew whenever a permission did — and
/// left room for a tab to be handed a <c>CanManage</c> that didn't match its
/// <c>Staff</c>.</para>
///
/// <para><b>This is for rendering decisions only.</b> Hiding a button is not a
/// permission check: every action that changes something re-checks server-side in the
/// service or at the top of the handler, because a Blazor circuit takes instructions
/// from a browser. The guards throughout the tabs are deliberate duplication, not
/// leftovers.</para>
/// </remarks>
/// <param name="Staff">The signed-in staff member, re-read from the database on load
/// rather than taken from the cookie's claims, so a permission change or a
/// deactivation takes effect on the next page load.</param>
public sealed record AdminContext(StaffMember Staff)
{
    /// Admins have everything; the three flags below are only consulted for Workers.
    public bool IsAdmin => Staff.Role == StaffRole.Admin;

    /// May change bookings — advance, cancel, reopen, complete, handle enquiries.
    public bool CanManage => IsAdmin || Staff.CanManageBookings;

    /// May see a customer's contact details, and therefore the Customers and
    /// Enquiries tabs — an enquiry is a name, an email and a phone number typed in by
    /// a member of the public, so reaching them through a list mustn't be a way round
    /// the same gate that governs seeing them on a booking.
    public bool CanSeeCustomerDetails => IsAdmin || Staff.CanViewCustomerDetails;

    /// May see everyone's bookings rather than only their own.
    public bool CanViewAllBookings => IsAdmin || Staff.CanViewAllBookings;

    /// <summary>
    /// The staff id to scope booking queries to, or null for "everything".
    /// </summary>
    /// <remarks>
    /// Restriction is applied in the query rather than after it, so a worker without
    /// <see cref="CanViewAllBookings"/> never has other people's bookings in memory to
    /// begin with.
    /// </remarks>
    public string? BookingScopeStaffId => CanViewAllBookings ? null : Staff.Id;
}
